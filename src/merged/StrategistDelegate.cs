using System;
using System.Collections.Generic;
using System.Globalization;
using SpireStrategist.Mod;

namespace Sts2AgentBridge;

/// <summary>
/// 把「选牌」这类局外决策直接交给全局战略助手（SpireStrategist）的**判断**，由桥接执行。
///
/// 为什么不用它自带的「接管」：实测它的 autopilot 会把游戏主线程卡死（画面冻结）。
/// 这里只读它算好的 <c>AdvisorService.Current.Decision</c>（建议模式也会算、会高亮），
/// 再把 <c>evaluation.RecommendedKey</c> 翻译成我们 /respond 的候选 key，由我们代答。
///
/// 它算不出来 / 不给建议时返回 null，交回给鲸鱼（agent）。
/// </summary>
public static class StrategistDelegate
{
    /// <summary>默认接手「选牌」；STS2_STRATEGIST_DELEGATE=0 可关。</summary>
    public static readonly bool Enabled =
        System.Environment.GetEnvironmentVariable("STS2_STRATEGIST_DELEGATE") != "0";

    /// <summary>
    /// 当前是否有一份「卡牌奖励三选一」的战略建议。
    /// 返回 true 时给出要选的候选 key 和一句理由。
    /// </summary>
    public static bool TryPickCard(Dictionary<string, object> pending, out string pickKey, out string reason)
    {
        pickKey = null;
        reason = null;

        try
        {
            if (!Enabled) return false;
            if (pending == null) return false;

            var kind = pending.TryGetValue("kind", out var k) ? k as string : null;
            var stage = pending.TryGetValue("stage", out var s) ? s as string : null;
            if (kind != "reward" || stage != "card-choice") return false;

            var decision = AdvisorService.Current?.Decision;
            var evaluation = decision?.Evaluation;
            var key = evaluation?.RecommendedKey;
            if (string.IsNullOrEmpty(key)) return false;

            // 它有时推荐「备选项」（跳过 / 献祭）而不是某张牌。
            if (key.StartsWith("alternative:", StringComparison.Ordinal))
            {
                var alt = Array.Find(decision.Options ?? Array.Empty<SpireStrategist.Core.DecisionOption>(),
                    o => o.Key == key);
                if (alt == null || alt.Kind != "skip") return false;   // 献祭等复杂项交回鲸鱼

                pickKey = "";                                          // 空 picks = 跳过这张卡
                reason = $"战略助手建议跳过这张牌：{Describe(alt)}";
                return true;
            }

            if (!key.StartsWith("card:", StringComparison.Ordinal)) return false;

            if (!int.TryParse(key.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) return false;

            // 我们的候选顺序 = 游戏三选一的顺序，战略助手的 "card:i" 也是这个下标
            if (!pending.TryGetValue("candidates", out var cands) || cands is not System.Collections.IEnumerable seq) return false;

            var list = new List<Dictionary<string, object>>();
            foreach (var item in seq)
                if (item is Dictionary<string, object> dict) list.Add(dict);

            if (index < 0 || index >= list.Count) return false;
            var candidate = list[index];
            if (!candidate.TryGetValue("key", out var ck) || ck is not string candidateKey) return false;

            pickKey = candidateKey;
            reason = Explain(evaluation, key, candidate);
            return true;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>营火：读它的 rest 建议（heal / smith / effect），映射到我们的候选。</summary>
    public static bool TryPickRest(Dictionary<string, object> pending, out string pickKey, out string reason)
    {
        pickKey = null;
        reason = null;

        try
        {
            if (!Enabled || pending == null) return false;
            var kind = pending.TryGetValue("kind", out var k) ? k as string : null;
            if (kind != "rest") return false;

            var decision = AdvisorService.Current?.Decision;
            if (decision == null || decision.Kind != "rest") return false;
            var key = decision.Evaluation?.RecommendedKey;
            if (string.IsNullOrEmpty(key)) return false;

            var option = Array.Find(decision.Options ?? Array.Empty<SpireStrategist.Core.DecisionOption>(), o => o.Key == key);
            var wantKind = option?.Kind;                       // heal / smith / effect
            if (string.IsNullOrEmpty(wantKind)) return false;

            var list = CandidateList(pending);
            for (var i = 0; i < list.Count; i++)
            {
                var title = list[i].TryGetValue("title", out var t) ? t as string : null;
                if (!MatchesRestKind(wantKind, title)) continue;

                pickKey = list[i].TryGetValue("key", out var ck) ? ck as string : null;
                if (pickKey == null) continue;
                reason = $"战略助手选了「{title}」：{Describe(option)}";
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>事件：读它的 EventPlan（按钮顺序和游戏选项顺序一致），按推荐按钮的下标取候选。</summary>
    public static bool TryPickEvent(Dictionary<string, object> pending, out string pickKey, out string reason)
    {
        pickKey = null;
        reason = null;

        try
        {
            if (!Enabled || pending == null) return false;
            var kind = pending.TryGetValue("kind", out var k) ? k as string : null;
            if (kind != "event") return false;

            var service = AdvisorService.Current;
            if (service == null) return false;

            var screen = MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext.Instance?.GetCurrentScreen();
            if (screen is not MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom room) return false;

            var plan = EventAdapter.Read(service, room);
            if (plan == null) return false;
            var key = plan.Evaluation?.RecommendedKey;
            if (string.IsNullOrEmpty(key)) return false;

            var index = Array.FindIndex(plan.Buttons, b => b != null && b.GetInstanceId().ToString() == key);
            if (index < 0) return false;

            var list = CandidateList(pending);
            if (index >= list.Count) return false;

            pickKey = list[index].TryGetValue("key", out var ck) ? ck as string : null;
            if (pickKey == null) return false;

            var option = Array.Find(plan.Evaluation?.Options ?? Array.Empty<SpireStrategist.Core.OptionScore>(), o => o.Key == key);
            var title = list[index].TryGetValue("title", out var t) ? t as string : null;
            reason = $"战略助手选了「{title}」：{Describe(option)}";
            return true;
        }
        catch { return false; }
    }

    /// <summary>商店：读它最近一次的 ShopEvaluation，按「名字 + 价格」匹配到我们的商品候选。</summary>
    public static bool TryPickShop(Dictionary<string, object> pending, out string pickKey, out string reason)
    {
        pickKey = null;
        reason = null;

        try
        {
            if (!Enabled || pending == null) return false;
            var kind = pending.TryGetValue("kind", out var k) ? k as string : null;
            var stage = pending.TryGetValue("stage", out var st) ? st as string : null;
            if (kind != "shop" || stage != "items") return false;

            var plan = AdvisorService.Current?.LastShopPlan;
            var choice = plan?.Recommendation;

            // 它自己的商店建议只覆盖「牌/删牌」；遗物白名单和药水这里补上（用它的 Policy，我们出手）。
            if (choice == null) return TryPickShopByPolicy(pending, out pickKey, out reason);

            var list = CandidateList(pending);
            foreach (var candidate in list)
            {
                var title = candidate.TryGetValue("title", out var t) ? t as string : null;
                if (string.IsNullOrEmpty(title)) continue;

                var sameName = title == choice.Name || title.StartsWith(choice.Name, StringComparison.Ordinal)
                               || choice.Name.StartsWith(title, StringComparison.Ordinal);
                if (!sameName) continue;

                if (candidate.TryGetValue("price", out var pr) && pr is int price && choice.Price > 0
                    && Math.Abs(price - choice.Price) > 1) continue;

                pickKey = candidate.TryGetValue("key", out var ck) ? ck as string : null;
                if (pickKey == null) continue;

                var why = choice.Reasons is { Length: > 0 } ? choice.Reasons[0] : "价格与收益匹配";
                reason = $"战略助手要买「{choice.Name}」（{choice.Price} 金）：{why}";
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>遗物（它的 PriorityShopPolicy 白名单）→ 先买；没白名单遗物就按简单规则买药水。</summary>
    private static bool TryPickShopByPolicy(Dictionary<string, object> pending, out string pickKey, out string reason)
    {
        pickKey = null;
        reason = null;

        try
        {
            var service = AdvisorService.Current;
            if (service == null) return false;

            var inventory = MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom.Instance?.Inventory;
            if (inventory == null || !inventory.IsOpen) return false;

            var slots = inventory.GetAllSlots()
                .Where(x => x.IsVisibleInTree() && x.Entry is { IsStocked: true })
                .ToArray();

            var snapshot = SnapshotReader.Read(service.State, service.Player);

            var cards = slots
                .Where(x => x.Entry is MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry { CreationResult: not null })
                .Select(x => new SpireStrategist.Core.ShopCard(
                    x.GetInstanceId().ToString(),
                    SnapshotReader.ReadCard(((MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry)x.Entry).CreationResult!.Card),
                    x.Entry!.Cost))
                .ToArray();

            var relics = slots
                .Where(x => x.Entry is MegaCrit.Sts2.Core.Entities.Merchant.MerchantRelicEntry { Model: not null })
                .Select(x =>
                {
                    var r = (MegaCrit.Sts2.Core.Entities.Merchant.MerchantRelicEntry)x.Entry!;
                    return new SpireStrategist.Core.ShopRelic(
                        x.GetInstanceId().ToString(), r.Model!.Id.Entry,
                        r.Model.Title.GetFormattedText(), r.Cost);
                })
                .ToArray();

            // ① 白名单遗物（会员卡/幽灵种子/帐篷/冰淇淋/化学物 X）
            var priority = SpireStrategist.Core.PriorityShopPolicy.Evaluate(snapshot, relics, cards, 0);
            if (priority?.Relic != null)
            {
                if (MatchCandidate(pending, priority.Relic.Name, priority.Relic.Price, out pickKey))
                {
                    reason = $"战略助手买遗物「{priority.Relic.Name}」（{priority.Relic.Price} 金）：{priority.Reason}";
                    return true;
                }
            }

            // ② 药水：有空位、钱够、且留够预算
            var freeSlot = service.Player.PotionSlots.Any(p => p == null);
            if (!freeSlot) return false;

            var potions = slots
                .Where(x => x.Entry is MegaCrit.Sts2.Core.Entities.Merchant.MerchantPotionEntry { Model: not null })
                .Select(x => (Entry: (MegaCrit.Sts2.Core.Entities.Merchant.MerchantPotionEntry)x.Entry!, Cost: x.Entry!.Cost))
                .OrderBy(p => p.Cost)
                .ToArray();

            foreach (var potion in potions)
            {
                if (potion.Cost <= 0) continue;
                if (snapshot.Gold - potion.Cost < 100) continue;      // 留 100 金预算

                var name = potion.Entry.Model!.Title.GetFormattedText();
                if (!MatchCandidate(pending, name, potion.Cost, out pickKey)) continue;

                reason = $"战略助手买药水「{name}」（{potion.Cost} 金）：药水槽有空位，买完仍留 100+ 金币";
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按名字 + 价格把它的选择匹配到我们的商店候选。</summary>
    private static bool MatchCandidate(Dictionary<string, object> pending, string name, int price, out string pickKey)
    {
        pickKey = null;
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var candidate in CandidateList(pending))
        {
            var title = candidate.TryGetValue("title", out var t) ? t as string : null;
            if (string.IsNullOrEmpty(title)) continue;
            var sameName = title == name || title.StartsWith(name, StringComparison.Ordinal) || name.StartsWith(title, StringComparison.Ordinal);
            if (!sameName) continue;
            if (candidate.TryGetValue("price", out var pr) && pr is int p && price > 0 && Math.Abs(p - price) > 1) continue;
            var key = candidate.TryGetValue("key", out var ck) ? ck as string : null;
            if (key == null) continue;
            pickKey = key;
            return true;
        }
        return false;
    }

    private static List<Dictionary<string, object>> CandidateList(Dictionary<string, object> pending)
    {
        var list = new List<Dictionary<string, object>>();
        if (pending.TryGetValue("candidates", out var cands) && cands is System.Collections.IEnumerable seq)
            foreach (var item in seq)
                if (item is Dictionary<string, object> d) list.Add(d);
        return list;
    }

    private static bool MatchesRestKind(string kind, string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        return kind switch
        {
            "heal" => title.Contains("休息") || title.Contains("回复") || title.Contains("治疗"),
            "smith" => title.Contains("锻造") || title.Contains("升级"),
            "effect" => title.Contains("添火") || title.Contains("挖") || title.Contains("祈"),
            _ => false,
        };
    }

    private static string Describe(SpireStrategist.Core.DecisionOption option)
    {
        if (option == null) return "按当前牌组/血量评分";
        var parts = new List<string>();
        if (option.Kind == "heal" && option.HealAmount > 0) parts.Add($"回 {option.HealAmount:0} 血");
        if (option.Kind == "smith") parts.Add("升级牌");
        return parts.Count > 0 ? string.Join("，", parts) : "按当前牌组评分";
    }

    private static string Describe(SpireStrategist.Core.OptionScore option)
    {
        if (option == null) return "按当前局面评分";
        var parts = new List<string>();
        if (option.Parts != null)
            foreach (var part in option.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.Reason)) continue;
                parts.Add(part.Reason);
                if (parts.Count >= 2) break;
            }
        return parts.Count > 0 ? string.Join("，", parts) : "按当前局面评分";
    }

    /// <summary>把它的评分理由压成一句话（取前两条 reason）。</summary>
    private static string Explain(SpireStrategist.Core.Evaluation evaluation, string key,
        Dictionary<string, object> candidate)
    {
        try
        {
            var title = candidate.TryGetValue("title", out var t) ? t as string : null;

            var option = Array.Find(evaluation.Options, o => o.Key == key);
            if (option == null) return title;

            var parts = new List<string>();
            foreach (var part in option.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.Reason)) continue;
                parts.Add(part.Reason);
                if (parts.Count >= 2) break;
            }

            var head = $"战略助手选了「{option.Name}」";
            return parts.Count == 0 ? head : head + "：" + string.Join("，", parts);
        }
        catch
        {
            return null;
        }
    }
}
