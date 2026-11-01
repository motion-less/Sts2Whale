using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Creatures;
using Sts2AgentBridge;
using STS2RitsuLib;

namespace Sts2Whale.Whale;

/// <summary>
/// 皮套的情绪来源，全部走 RitsuLib 的生命周期事件，**不再挂任何 Harmony 补丁**。
///
/// 以前是三个补丁（LetHpInternal / DamageBlockInternal / Block setter），问题在于它们盯的是
/// 私有内部方法名：游戏换个签名就静默失效。现在改成框架的公开契约：
///
/// | 事件                      | 拿到的信息                              | 干什么                     |
/// |---------------------------|-----------------------------------------|----------------------------|
/// | <see cref="CurrentHpChangedEvent"/> | Creature + 血量变化量 Delta     | 玩家掉血 → hurt 三档表情   |
/// | <see cref="AttackEndedEvent"/>      | AttackCommand.Results（每个受击者的格挡/掉血） | 格挡吸收 → 受击倾斜；敌人掉血 → 黑脸 |
/// | <see cref="BlockGainedEvent"/> 等   | Creature                                | 读实时 Block → 钢盆常驻    |
///
/// 分工上刻意不重叠：**玩家掉血只由 CurrentHpChanged 报，格挡只由 AttackEnded 报**，
/// 所以同一个 DamageResult 不会被算两次（倾斜幅度 = 格挡 + 掉血）。
/// </summary>
internal static class DamageHooks
{
    private static bool _applied;

    /// <summary>上一次看到的血量。只用来把 Delta 的符号约定兜住：以真实 HP 变化为准。</summary>
    private static readonly Dictionary<Creature, int> LastHp = new();

    private static readonly List<IDisposable> Subscriptions = new();

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            // replayCurrentState: false —— 只要「从现在开始」的事件。默认值会把最后一次可重放事件
            // 立刻补发一遍，那会让皮套在启动瞬间无端挨一下。
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<CurrentHpChangedEvent>(OnHpChanged, false));
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<AttackEndedEvent>(OnAttackEnded, false));
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<BlockGainedEvent>(OnBlockChanged, false));
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<BlockClearedEvent>(OnBlockChanged, false));
            Subscriptions.Add(RitsuLibFramework.SubscribeLifecycle<BlockBrokenEvent>(OnBlockChanged, false));
            BridgeLog.Info("[avatar] 情绪钩子已挂上（生命周期事件：血量 / 攻击结算 / 格挡）");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[avatar] 生命周期事件订阅失败: " + e.Message);
        }
    }

    /// <summary>卸掉全部订阅（留给「运行中挂起」用：退订之后皮套不再跟着掉血/格挡做表情）。</summary>
    public static void Detach()
    {
        foreach (var s in Subscriptions)
        {
            try { s.Dispose(); } catch { }
        }
        Subscriptions.Clear();
        lock (LastHp) LastHp.Clear();
        _applied = false;
    }

    // ------------------------------------------------------------------ 血量

    private static void OnHpChanged(CurrentHpChangedEvent evt)
    {
        try
        {
            var c = evt.Creature;
            if (c == null) return;

            var cur = c.CurrentHp;

            // 用自己记的上一格血反推真实变化：Delta 的正负约定变了也不会把「治疗」当成「受伤」
            int lost;
            lock (LastHp)
            {
                if (LastHp.TryGetValue(c, out var prev)) lost = prev - cur;
                else lost = evt.Delta < 0 ? (int)-evt.Delta : 0;
                LastHp[c] = cur;
            }

            if (lost <= 0) return;
            if (c.IsPlayer) WhaleAvatar.OnPlayerHpLost(lost);
        }
        catch
        {
            // 事件回调里绝不能抛：会把游戏的结算带崩
        }
    }

    // ------------------------------------------------------------------ 攻击结算

    private static void OnAttackEnded(AttackEndedEvent evt)
    {
        try
        {
            var attack = evt.Attack;
            if (attack == null) return;

            foreach (var group in attack.Results)
            {
                if (group == null) continue;
                foreach (var r in group)
                {
                    if (r?.Receiver == null) continue;

                    if (r.Receiver.IsPlayer)
                    {
                        // 被格挡掉的那部分：掉血由 CurrentHpChanged 报，这里只报盾扛下来的
                        if (r.BlockedDamage > 0) WhaleAvatar.OnPlayerBlockAbsorbed(r.BlockedDamage);
                    }
                    else if (r.Receiver.IsEnemy && r.UnblockedDamage > 0)
                    {
                        WhaleAvatar.OnEnemyHurt(r.UnblockedDamage);
                    }
                }
            }
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------ 格挡

    private static void OnBlockChanged<T>(T evt) where T : struct
    {
        try
        {
            var c = evt switch
            {
                BlockGainedEvent g => g.Creature,
                BlockClearedEvent cl => cl.Creature,
                BlockBrokenEvent b => b.Creature,
                _ => null,
            };
            if (c == null || !c.IsPlayer) return;
            WhaleAvatar.OnPlayerBlockChanged(c.Block);
        }
        catch
        {
        }
    }
}
