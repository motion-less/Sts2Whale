using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using SpireStrategist.Core;
using MegaCrit.Sts2.Core.Models.Potions;

namespace SpireStrategist.Mod;

internal sealed partial class PartialAutopilot
{
    private int shopFoulUses;
    private bool TickFoulMerchant(NMerchantRoom room)
    {
        // Only called for a real MerchantRoom. FakeMerchant can start combat and
        // must keep its separate risk assessment. Use the native action queue.
        var potion=service.Player.Potions.OfType<FoulPotion>().FirstOrDefault();
        if(potion==null)return false;
        if(potion.IsQueued){Wait("接管中 · 等待污浊药水兑换完成");return true;}
        if(room.Inventory.IsOpen)
        {
            Wait("接管中 · 关闭商品界面，准备向普通商人投掷污浊药水");
            if(Claim($"foul-close:{room.GetInstanceId()}:{shopFoulUses}",GameActions.Field(room.Inventory,"_isInputBlocked") is not true))
                GameActions.Invoke(room.Inventory,"Close");
            return true;
        }
        Wait("接管中 · 向普通商人投掷污浊药水，随后重新评估商店");
        if(Claim($"foul-use:{room.GetInstanceId()}:{shopFoulUses}",potion.PassesCustomUsabilityCheck&&room.MerchantButton.IsEnabled))
        {
            service.RecordAuto("shop_foul_potion",new{state=SnapshotReader.Read(service.State,service.Player),sequence=shopFoulUses,source="native potion action"});
            potion.EnqueueManualUse(null);
            shopFoulUses++;
            leavingShop=null;
        }
        return true;
    }

    private bool TickPriorityShop(NMerchantRoom room,NMerchantSlot[] slots,RunSnapshot state)
    {
        var relics=slots.Where(s=>s.Entry is MerchantRelicEntry {Model:not null})
            .Select(s=>{var e=(MerchantRelicEntry)s.Entry;return new ShopRelic(s.GetInstanceId().ToString(),e.Model!.Id.Entry,e.Model.Title.GetFormattedText(),e.Cost);}).ToArray();
        var cards=slots.Where(s=>s.Entry is MerchantCardEntry {CreationResult:not null})
            .Select(s=>new ShopCard(s.GetInstanceId().ToString(),SnapshotReader.ReadCard(((MerchantCardEntry)s.Entry).CreationResult!.Card),s.Entry.Cost)).ToArray();
        var plan=PriorityShopPolicy.Evaluate(state,relics,cards,shopBoughtCards,
            slots.FirstOrDefault(s=>s.Entry is MerchantCardRemovalEntry)?.Entry.Cost);
        if(plan==null)return false;
        var key=plan.Card?.Key??plan.Relic.Key;
        var slot=slots.Single(s=>s.GetInstanceId().ToString()==key);
        var choice=new ShopChoice(key,plan.Card?.Card.Name??plan.Relic.Name,plan.Card!=null?"card":"relic",
            slot.Entry.Cost,100,[plan.Reason,$"本次计划总价 {plan.TotalPrice}，现款 {state.Gold}"]);
        service.ShowShop(state,new(choice,[choice],0,[plan.Reason,"优先清单执行后重新评估删牌、买牌；随机遗物仍保留预算。"]));
        Wait($"接管中 · 优先购买 {choice.Name}（{choice.Price} 金币）");
        if(Claim($"priority-shop:{room.GetInstanceId()}:{key}:{state.Gold}:{choice.Price}",slot.Entry.EnoughGold,8000))
        {
            service.RecordAuto("shop_priority",new{state,plan,choice});
            Track(()=>BuyShopSlot(slot,plan.Card!=null));
        }
        return true;
    }
    private ulong? leavingShop;
    private readonly string shopRandomSalt=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    // 第三幕清仓：牌和删牌那一段走完（ShopPolicy 没有推荐了）之后，如果还压着 400 以上的金币，
    // 就随机把遗物买到「金币 ≤ 200 / 遗物售罄 / 买不起」为止。只按金币判断，不做遗物强度排序。
    private const int Act3SpreeGold=400;
    private const int Act3SpreeFloor=200;
    private bool act3SpreeArmed,act3SpreeFinished;
    private bool TickAct3RelicSpree(NMerchantRoom room,NMerchantSlot[] slots,RunSnapshot state)
    {
        if(act3SpreeFinished)return false;
        if(!act3SpreeArmed)
        {
            // 只在第三幕、且买牌/删牌已经无事可做之后才启动一次。
            if(state.Act!=3||state.Gold<=Act3SpreeGold)return false;
            act3SpreeArmed=true;
            service.RecordAuto("shop_act3_spree_start",new{state,trigger=Act3SpreeGold,floor=Act3SpreeFloor});
        }
        if(state.Gold<=Act3SpreeFloor)
        {
            act3SpreeFinished=true;
            service.RecordAuto("shop_act3_spree_end",new{state,reason="gold_floor"});
            return false;
        }
        var relics=slots.Where(s=>s.Entry is MerchantRelicEntry {Model:not null}&&s.Entry.EnoughGold&&s.Entry.Cost<=state.Gold)
            .OrderBy(s=>((MerchantRelicEntry)s.Entry).Model!.Id.ToString(),StringComparer.Ordinal).ToArray();
        if(relics.Length==0)
        {
            act3SpreeFinished=true;
            service.RecordAuto("shop_act3_spree_end",new{state,reason="no_affordable_relic"});
            return false;
        }
        var fingerprint=string.Join("|",relics.Select(s=>((MerchantRelicEntry)s.Entry).Model!.Id+":"+s.Entry.Cost));
        var index=ClimbController.RandomIndex(shopRandomSalt+":act3-spree:"+state.Floor+":"+state.Gold+":"+fingerprint,relics.Length);
        // 跳过这个金币数下已经提交过、但没有成交的槽位，避免买失败后一直卡在同一件遗物上。
        NMerchantSlot? slot=null;string? key=null;
        for(var k=0;k<relics.Length;k++)
        {
            var candidate=relics[(index+k)%relics.Length];
            var candidateKey="act3-relic-spree:"+room.GetInstanceId()+":"+candidate.GetInstanceId()+":"+state.Gold;
            if(gate.WasSubmitted(candidateKey))continue;
            slot=candidate;key=candidateKey;break;
        }
        if(slot==null||key==null)
        {
            act3SpreeFinished=true;
            service.RecordAuto("shop_act3_spree_end",new{state,reason="purchase_stalled"});
            return false;
        }
        var choice=new ShopChoice(slot.GetInstanceId().ToString(),((MerchantRelicEntry)slot.Entry).Model!.Title.GetFormattedText(),
            "relic",slot.Entry.Cost,0,[$"第三幕清仓：金币 {state.Gold} 仍高于 {Act3SpreeGold}，随机买遗物（剩 {relics.Length} 件买得起）",
                $"金币降到 {Act3SpreeFloor} 以下、遗物售罄或买不起就停；不做强度排序"]);
        service.ShowShop(state,new(choice,[choice],Act3SpreeFloor,
            ["第三幕清仓只按金币判断，不代表遗物强度排名；优先清单、买牌和删牌已经先执行过。"]));
        Wait($"接管中 · 第三幕清仓随机买遗物（{state.Gold} 金，剩 {relics.Length} 件）");
        if(Claim(key,slot.Entry.EnoughGold,8000))
        {
            service.RecordAuto("shop_act3_spree",new{state,floor=Act3SpreeFloor,index,trigger=Act3SpreeGold,
                candidates=relics.Select(s=>new{id=((MerchantRelicEntry)s.Entry).Model!.Id.ToString(),cost=s.Entry.Cost})});
            Track(()=>BuyShopSlot(slot,false));
        }
        return true;
    }
    private void TickClimbShop(NMerchantRoom room,NMerchantInventory inventory,NMerchantSlot[] slots,RunSnapshot state)
    {
        var nodes=RouteAdapter.Nodes(service.State);
        var reserve=service.State.CurrentMapPoint is {} point?
            ShopBudget.Reserve(state,nodes,RouteAdapter.Key(point),ClimbController.RelicReserve):ClimbController.RelicReserve;
        var relics=slots.Where(s=>s.Entry is MerchantRelicEntry {Model:not null}&&s.Entry.Cost<=state.Gold-reserve&&s.Entry.EnoughGold)
            .Where(s=>RelicValue.Shop(((MerchantRelicEntry)s.Entry).Model!.Id.Entry,state) is not {} model||model.Value-new StrategicValue(state).Gold(s.Entry.Cost)>0)
            .OrderBy(s=>((MerchantRelicEntry)s.Entry).Model!.Id.ToString(),StringComparer.Ordinal).ToArray();
        // 第三幕清仓已经按 200 的底线收手时，不要再让预留金那一步把金币继续花到 200 以下。
        if(relics.Length>0&&!act3SpreeFinished)
        {
            var fingerprint=string.Join("|",relics.Select(s=>((MerchantRelicEntry)s.Entry).Model!.Id+":"+s.Entry.Cost));
            var index=ClimbController.RandomIndex(shopRandomSalt+":"+state.Floor+":"+state.Gold+":"+fingerprint,relics.Length);
            var modeled=relics.Select((s,i)=>(Index:i,Value:RelicValue.Shop(((MerchantRelicEntry)s.Entry).Model!.Id.Entry,state)?.Value-new StrategicValue(state).Gold(s.Entry.Cost)))
                .Where(x=>x.Value>0).OrderByDescending(x=>x.Value).ThenBy(x=>x.Index).FirstOrDefault();
            var ranked=modeled.Value.HasValue;
            if(ranked)index=modeled.Index;
            var slot=relics[index];
            var reason=ranked?"按已建模遗物的构筑收益与价格选择":"此处为随机预算购买，不代表遗物强度排名。";
            var choice=new ShopChoice(slot.GetInstanceId().ToString(),((MerchantRelicEntry)slot.Entry).Model!.Title.GetFormattedText(),"relic",slot.Entry.Cost,modeled.Value??0,[reason,$"预算剩余步骤：随机遗物，保留 {reserve} 金币"]);
            service.ShowShop(state,new(choice,[choice],reserve,[reason]));
            Wait(ranked?"接管中 · 购买适配当前构筑的遗物":$"接管中 · 随机购买遗物（保留 {reserve} 金；可停止接管）");
            if(Claim("climb-relic:"+room.GetInstanceId()+":"+state.Gold+":"+fingerprint,delay:8000))
            {
                service.RecordAuto(ranked?"climb_modeled_relic":"climb_random_relic",new{state,reserve,index,shopRandomSalt,looping=ClimbController.Enabled,score=modeled.Value,
                    candidates=relics.Select(s=>new{id=((MerchantRelicEntry)s.Entry).Model!.Id.ToString(),cost=s.Entry.Cost})});
                Track(()=>BuyShopSlot(slot,false));
            }
            return;
        }
        var potionPlan=slots.Where(s=>s.Entry is MerchantPotionEntry {Model:not null})
            .Select(s=>(Slot:s,Choice:ShopAdvice.Potion(state,s,reserve))).Where(x=>x.Choice.Eligible&&x.Choice.Score>0)
            .OrderByDescending(x=>x.Choice.Score).ThenBy(x=>x.Choice.Price).FirstOrDefault();
        var potion=potionPlan.Slot;
        if(potion!=null)
        {
            var choice=potionPlan.Choice;
            service.ShowShop(state,new(choice,[choice],reserve,[]));
            Wait("接管中 · 最后补充空药水栏（可停止接管）");
            if(Claim("climb-potion:"+room.GetInstanceId()+":"+potion.GetInstanceId()+":"+state.Gold,delay:8000))
            {service.RecordAuto("climb_buy_potion",new{state,reserve,price=potion.Entry.Cost});Track(()=>BuyShopSlot(potion,false));}
            return;
        }
        Wait("接管中 · 商店处理完成，离开");
        if(Claim("climb-shop-close:"+room.GetInstanceId()))
        {
            leavingShop=room.GetInstanceId();
            GameActions.Invoke(inventory,"Close");
        }
    }
}
