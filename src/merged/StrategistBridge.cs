using System;
using System.Collections.Generic;
using Godot;
using SpireStrategist.Mod;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.RuntimeInput;

namespace Sts2AgentBridge;

/// <summary>
/// 全局战略助手（SpireStrategist）的接管层。合并后它和桥接在同一个程序集里，可以直接调它的内部接口。
///
/// 分工：
///   * 局外（选路 / 奖励 / 商店 / 营火 / 事件 / 先古）→ 战略助手接管；
///   * 局内战斗 → 内置求解器（CombatSolver）接管；
///   * 两边都不支持的界面 → selector 交回给鲸鱼（agent 走 /request + /respond）。
///
/// 自动接管策略：进入一局时自动打开「接管」（不点全自动/连续爬塔），同一局只自动开一次——
/// 玩家手动点「停止接管」后不会被我们抢回来。设 STS2_STRATEGIST_AUTO=0 可完全关掉自动接管。
/// </summary>
public static class StrategistBridge
{
    // 默认**自动接管**（玩家要求：局外操作交给战略助手，慢一步都不行）。
    // 已知风险：它的接管偶发会把游戏主线卡住；要临时关掉设 STS2_STRATEGIST_AUTO=0，
    // 或运行时 POST /strategist {"on":false}。
    public static readonly bool Auto =
        !string.Equals(System.Environment.GetEnvironmentVariable("STS2_STRATEGIST_AUTO"), "0", StringComparison.Ordinal);

    private static object _autoStartedRun;
    private static object _panelHiddenFor;      // 这个 service 实例的面板已经藏过了
    private static double _shopLeaveAt;         // 商店里它断开时的兜底离开时刻
    private static bool _panelKeyDown;          // 轮询兜底用的边沿检测
    private static bool _hotkeyTried;           // 已经尝试过注册 RitsuLib 热键
    private static bool _hotkeyFallback;        // 注册失败 → 退回每帧轮询
    private static double _fallbackNextAt;      // 鲸鱼兜底的节流
    private static double _eventAdvanceAt;      // 事件房间卡住时的「点长句选项」节流
    private static long _stuckId = -1;          // 当前挂着的请求 id
    private static double _stuckProbeAt;
    private static double _eventProbeAt;        // 看门狗探测节流
    private static double _ensureEnableAt;      // EnsureEnabledForRun 的限流
    private static double _stuckSince;          // 它从什么时候开始挂着
    private static long _fallbackLastId = -1;   // 上一条兜底处理过的请求，避免死循环
    private static string _fallbackLastSig;
    private const Key PanelKey = Key.F8;        // F8：显示/隐藏策略助手面板
    private static bool _panelShownByUser;      // 玩家用 /strategist/ui 打开过：我们不再抢着藏
    private static object _resumeRunKey;
    private static bool _userDisabled;          // 玩家/接口主动关的：本局不再自动开
    private static double _nextResumeAt;
    private static int _resumeCount;
    private const int MaxResumesPerRun = 40;
    private static string _lastError;

    public static bool Available
    {
        get
        {
            try { return AdvisorService.Current != null; }
            catch { return false; }
        }
    }

    /// <summary>它卡住了、明确等人工处理（这时该由鲸鱼接手）。</summary>
    public static bool WaitingForManual
    {
        get
        {
            try { return AdvisorService.Current?.Autopilot.WaitingForManual ?? false; }
            catch { return false; }
        }
    }

    public static bool Enabled
    {
        get
        {
            try { return AdvisorService.Current?.Autopilot.Enabled ?? false; }
            catch { return false; }
        }
    }

    /// <summary>每帧主线调用。</summary>
    public static void Tick()
    {
        try
        {
            // 它在接管时，卡牌选择要交给它的自动化（我们的 selector 必须让位）。
            RequestApi.YieldToAutomation = Enabled;

            // F8：显示/隐藏策略助手面板。优先用 RitsuLib 的热键服务注册（玩家可在设置里重绑、
            // 也支持 Steam Input）；注册不上才退回每帧轮询。
            try
            {
                if (!_hotkeyTried) EnsureHotkey();
                if (_hotkeyFallback)
                {
                    var down = Input.IsKeyPressed(PanelKey);
                    if (down && !_panelKeyDown) TogglePanel();
                    _panelKeyDown = down;
                }
            }
            catch { }

            // 策略助手的两个面板（主面板 + 循环爬塔的浮动条）默认收起来：
            // 它的自动化读的是游戏状态，不靠面板。玩家用 /strategist/ui 打开后我们不再抢着藏。
            try
            {
                if (!_panelShownByUser)
                {
                    // 循环爬塔那块浮层在主菜单就建好了，所以这里不等「开局」就压。
                    ClimbController.SetUserHidden(true);

                    var svc = AdvisorService.Current;
                    if (svc != null && !ReferenceEquals(_panelHiddenFor, svc))
                    {
                        _panelHiddenFor = svc;
                        svc.Panel.SetUserHidden(true);
                    }
                }
            }
            catch { }

            if (!Auto || _userDisabled) return;

            var service = AdvisorService.Current;
            if (service == null) return;          // 还没开一局 / 已回主菜单

            // 卡住检测：同一个请求挂超过 15 秒，不管它在不在接管，都交给鲸鱼兜底做掉。
            // （例：先古之民「美味饼干」后面是 min=4 的升级界面，战略助手只会选 1 张 → 永远确认不了。）
            try
            {
                var nowStuck = Time.GetTicksMsec() / 1000.0;
                if (_stuckProbeAt <= 0) _stuckProbeAt = nowStuck;
                // 5 秒才探一次：RequestApi.Current() 要扫场景树，不能每帧调
                if (nowStuck >= _stuckProbeAt)
                {
                    _stuckProbeAt = nowStuck + 5.0;

                    long pid = -1;
                    if (RequestApi.Current() is { } snapStuck
                        && snapStuck.TryGetValue("pending", out var pendStuck)
                        && pendStuck is Dictionary<string, object> pdStuck
                        && pdStuck.TryGetValue("id", out var idStuck) && idStuck != null)
                        pid = Convert.ToInt64(idStuck);

                    if (pid >= 0 && pid == _stuckId)
                    {
                        if (nowStuck - _stuckSince >= 15)
                        {
                            _stuckSince = nowStuck;            // 再给它 15 秒，别连点
                            BridgeLog.Info($"stuck watchdog: 请求 id={pid} 挂了 15 秒 → 鲸鱼兜底");
                            WhaleFallback(service, force: true);
                        }
                    }
                    else { _stuckId = pid; _stuckSince = nowStuck; }
                }
            }
            catch { }

            // 兜底（鲸鱼）：它一旦不接管了，就由我们把「现在该做的事」做完；
            // 房间处理完回到地图，下面的续约逻辑会自动把接管重新打开。
            if (!Enabled) WhaleFallback(service);

            // 先古之民这类事件：选项是一整句按钮，而且答完之后还要再点一次才进下一步。
            // 没 pending、人还在事件房里超过 10 秒，就替它点一次「文字最长的按钮」。
            try
            {
                var nowEv = Time.GetTicksMsec() / 1000.0;
                var inEvent = service.State.CurrentRoom is MegaCrit.Sts2.Core.Rooms.EventRoom;

                // 只有在事件房里才需要看 pending；一次探测要扫场景树，1 秒一次够了
                var hasPending = false;
                if (inEvent && nowEv >= _eventProbeAt)
                {
                    _eventProbeAt = nowEv + 1.0;
                    hasPending = RequestApi.Current() is { } snapEv
                                 && snapEv.TryGetValue("pending", out var pe) && pe != null;
                }

                if (inEvent && !hasPending && !InCombat() && !RunOver())
                {
                    if (_eventAdvanceAt <= 0) _eventAdvanceAt = nowEv + 10;
                    else if (nowEv >= _eventAdvanceAt)
                    {
                        _eventAdvanceAt = nowEv + 6;
                        var res = UiClicks.ClickLongestText();
                        BridgeLog.Info("event 卡住 → 点长句选项：" +
                            (res.TryGetValue("ok", out var eok) ? eok : "?"));
                    }
                }
                else _eventAdvanceAt = 0;
            }
            catch { }

            // 兜底：它在商店里把自己 Pause 了（策略算完、把遗物药水交回人工），又没有鲸鱼在轮询，
            // 就会一直卡在商店。等 8 秒，替它按「前进」离店；回到地图后下面的续约逻辑会把它接回来。
            try
            {
                var nowShop = Time.GetTicksMsec() / 1000.0;
                if (!Enabled && !InCombat() && InShop(service))
                {
                    if (_shopLeaveAt <= 0) _shopLeaveAt = nowShop + 8;
                    else if (nowShop >= _shopLeaveAt)
                    {
                        _shopLeaveAt = 0;
                        var res = UiClicks.ClickText("前进");
                        BridgeLog.Info("strategist paused in shop → 按前进（" +
                            (res.TryGetValue("ok", out var okv) ? okv : "?") + "）");
                    }
                }
                else _shopLeaveAt = 0;
            }
            catch { }

            var runKey = CurrentRunKey();
            if (runKey == null) return;

            if (!Equals(runKey, _resumeRunKey))
            {
                _resumeRunKey = runKey;
                _resumeCount = 0;
                _nextResumeAt = 0;
            }

            var now = Time.GetTicksMsec() / 1000.0;

            if (Enabled)
            {
                _nextResumeAt = now + 3;           // 在跑：把续约冷却往后推
                return;
            }

            // ① 这一局还没开过 → 直接开
            if (!Equals(runKey, _autoStartedRun))
            {
                _autoStartedRun = runKey;
                service.Autopilot.Enable(fullAutomatic: false);
                BridgeLog.Info("strategist: autopilot enabled for this run");
                return;
            }

            // ② 它自己暂停过（例如「遗物/药水购买请手动处理」）→ 鲸鱼处理完再续上
            if (now < _nextResumeAt) return;
            if (_resumeCount >= MaxResumesPerRun) return;
            if (WaitingForManual) return;                       // 它明确在等人
            if (IsUnsupportedCharacter(service)) return;        // 「仅支持单人战士」之类，续了也没用
            if (InCombat()) return;                             // 战斗归求解器，别在这时候抢
            if (!AtMap()) return;                               // 人还卡在房间/商店/奖励里：等鲸鱼处理完（回到地图）再续

            _resumeCount++;
            _nextResumeAt = now + 3;
            service.Autopilot.Enable(fullAutomatic: false);
            BridgeLog.Info($"strategist: autopilot resumed after whale interlude (#{_resumeCount})");
        }
        catch (Exception e)
        {
            _lastError = e.GetType().Name + ": " + e.GetMessageSafe();
            BridgeLog.Warn("strategist tick failed: " + _lastError);
        }
    }

    private static bool InCombat()
    {
        try { return MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress; }
        catch { return false; }
    }

    /// <summary>现在是不是「已经回到地图、可以走了」——这是鲸鱼处理完一段局外操作的信号。</summary>
    /// <summary>本局结束（建筑师）：把接管关掉，让游戏自己的收尾界面跑完。</summary>
    public static void DisableForRunEnd()
    {
        try
        {
            var svc = AdvisorService.Current;
            if (svc == null || !svc.Autopilot.Enabled) return;
            svc.Autopilot.Pause("本局结束（建筑师），收尾中");
            BridgeLog.Info("strategist: 本局结束 → 关闭接管，准备收尾");
        }
        catch (Exception e) { BridgeLog.Warn("strategist disable failed: " + e.Message); }
    }

    /// <summary>新一局开始后确保接管是开着的（不覆盖玩家手动关掉的情况）。</summary>
    public static void EnsureEnabledForRun()
    {
        try
        {
            var svc = AdvisorService.Current;
            if (svc == null || svc.Autopilot.Enabled || _userDisabled) return;

            var now = Time.GetTicksMsec() / 1000.0;
            if (now < _ensureEnableAt) return;           // 别每帧都调 Enable 刷日志
            _ensureEnableAt = now + 10.0;
            svc.Autopilot.Enable(fullAutomatic: false);
            BridgeLog.Info("strategist: 新局 → 接管已重新打开");
        }
        catch (Exception e) { BridgeLog.Warn("strategist enable failed: " + e.Message); }
    }

    /// <summary>调试用：强制把接管停掉，用来验证「鲸鱼兜底」这条路（POST /strategist/pause）。</summary>
    public static Dictionary<string, object> ForcePause()
    {
        try
        {
            var svc = AdvisorService.Current;
            if (svc == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "还没开局" };
            svc.Autopilot.Pause("测试：手动触发掉线");
            return new Dictionary<string, object> { ["ok"] = true, ["enabled"] = svc.Autopilot.Enabled };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>F8 用：翻转面板显示状态。</summary>
    /// <summary>
    /// 把 F8 注册成 RitsuLib 运行时热键。绑定的文本格式各个框架版本不完全一样，
    /// 所以候选一个个试，全都解析不了就退回轮询——绝不让「按了没反应」成为静默失败。
    /// </summary>
    private static void EnsureHotkey()
    {
        _hotkeyTried = true;
        try
        {
            RuntimeHotkeyService.Initialize();
        }
        catch
        {
            // Initialize 只是安排路由器挂载，失败不影响 Register 尝试
        }

        foreach (var candidate in new[] { "F8", "Key.F8", "key:F8", "KEY_F8" })
        {
            try
            {
                if (!RuntimeHotkeyService.TryNormalizeBinding(candidate, out var binding)) continue;
                RuntimeHotkeyService.Register(binding, () => { try { TogglePanel(); } catch { } }, new RuntimeHotkeyOptions
                {
                    Id = "Sts2Whale.strategist.panel",
                    DisplayName = RuntimeHotkeyText.Literal("策略助手面板"),
                    Description = RuntimeHotkeyText.Literal("显示 / 隐藏全局战略助手的面板"),
                    Category = RuntimeHotkeyText.Literal("鲸鱼爬塔"),
                    Purpose = "strategist-panel",
                    DebugName = "strategist-panel",
                    MarkInputHandled = true,
                    SuppressWhenTextInputFocused = true,
                });
                BridgeLog.Info($"[hotkey] F8 已注册为 RitsuLib 热键（{binding}，可在设置里重绑）");
                return;
            }
            catch
            {
                // 这个写法不被接受，试下一个
            }
        }

        _hotkeyFallback = true;
        BridgeLog.Warn("[hotkey] RitsuLib 热键注册失败，F8 退回每帧轮询");
    }

    public static Dictionary<string, object> TogglePanel()
    {
        var show = !_panelShownByUser;
        var result = ShowPanel(!show);
        result["visible"] = show;
        result["by"] = "toggle";
        try { BridgeLog.Info("strategist panel toggled → " + (show ? "显示" : "隐藏")); } catch { }
        return result;
    }

    /// <summary>显示/隐藏策略助手面板（POST /strategist/ui）。</summary>
    public static Dictionary<string, object> ShowPanel(bool hide)
    {
        try
        {
            var svc = AdvisorService.Current;
            if (svc == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "还没开局" };
            _panelShownByUser = !hide;
            svc.Panel.SetUserHidden(hide);
            ClimbController.SetUserHidden(hide);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["hidden"] = hide,
                ["panelVisible"] = svc.Panel.Visible,
                ["climbHidden"] = ClimbController.UserHidden,
            };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>
    /// 战略助手掉线时的「鲸鱼兜底」：把当前挂着的决定替他做掉（选牌/奖励/商店/营火/事件/选牌界面），
    /// 一次只做一件，1.5 秒节流；做完后回到地图，续约逻辑会把接管重新打开。
    /// </summary>
    private static void WhaleFallback(AdvisorService service, bool force = false)
    {
        try
        {
            var now = Time.GetTicksMsec() / 1000.0;
            if (now < _fallbackNextAt) return;
            if (InCombat()) return;                                  // 战斗（含战斗内选牌）归求解器

            var snap = RequestApi.Current();
            if (snap == null) return;
            if (!snap.TryGetValue("pending", out var po) || po is not Dictionary<string, object> pending) return;

            var kind = pending.TryGetValue("kind", out var k) ? k as string : null;
            if (string.IsNullOrEmpty(kind) || kind is "menu" or "gameover") return;

            var id = pending.TryGetValue("id", out var ido) && ido != null ? Convert.ToInt64(ido) : -1L;
            if (id < 0) return;

            var picks = new List<string>();
            string why = null;
            switch (kind)
            {
                case "reward":
                    if (StrategistDelegate.TryPickCard(pending, out var ck, out why) && !string.IsNullOrEmpty(ck)) picks.Add(ck);
                    break;                                            // 挑不出来就跳过这张卡
                case "shop":
                    if (StrategistDelegate.TryPickShop(pending, out var sk, out why) && !string.IsNullOrEmpty(sk)) picks.Add(sk);
                    else
                    {
                        // 没想买的：直接离店（点「前进」），别卡在这里
                        var leave = UiClicks.ClickText("前进");
                        BridgeLog.Info("whale fallback: 商店没可买的 → 点前进 " +
                            (leave.TryGetValue("ok", out var lok) && lok is bool lb && lb ? "ok" : "没点到"));
                        _fallbackNextAt = now + 1.5;
                        return;
                    }
                    break;
                case "rest":
                    if (StrategistDelegate.TryPickRest(pending, out var rk, out why) && !string.IsNullOrEmpty(rk)) picks.Add(rk);
                    break;
                case "event":
                    if (StrategistDelegate.TryPickEvent(pending, out var ek, out why) && !string.IsNullOrEmpty(ek)) picks.Add(ek);
                    break;
            }

            // 自动决策给不出答案（选牌界面 / 宝箱 / 前进 / 药水目标…）：按 min 条数补齐，能取消才跳过。
            var raw = RawCandidates(pending);
            if (raw.Count > 0)
            {
                var min = pending.TryGetValue("min", out var mv) && mv is int mi ? mi : 0;
                var cancelable = pending.TryGetValue("cancelable", out var cl) && cl is bool cb && cb;
                var forced = !cancelable || kind is "treasure" or "proceed" or "upgrade" or "remove" or "simple" or "select" or "discard" or "exhaust" or "potion";

                if (picks.Count == 0 && forced) picks.Add(CardKey(raw[0]));

                // 多选（例如先古祝福「大～抱抱」要删 4 张）：必须凑够 min 条，否则界面不会收
                if (picks.Count < min)
                {
                    var pool = raw.Where(c => !picks.Contains(CardKey(c)));
                    // 删牌/消耗/丢弃：优先丢基础打击防御，别把好牌扔掉
                    if (kind is "remove" or "exhaust" or "discard")
                        pool = pool.OrderByDescending(c => IsBasic(c));
                    foreach (var c in pool)
                    {
                        var ck = CardKey(c);
                        if (string.IsNullOrEmpty(ck) || picks.Contains(ck)) continue;
                        picks.Add(ck);
                        if (picks.Count >= min) break;
                    }
                }
            }

            var sig = id + "|" + kind + "|" + string.Join(",", picks);
            if (!force && sig == _fallbackLastSig && id == _fallbackLastId) return;   // 同一条已经处理过，等它自己变
            _fallbackLastSig = sig;
            _fallbackLastId = id;
            _fallbackNextAt = now + 1.5;

            BridgeLog.Info($"whale fallback: 它没在接管 → 处理 {kind} id={id} picks=[{string.Join(",", picks)}]");
            _ = RequestApi.Respond(id, picks.ToArray());
        }
        catch (Exception e)
        {
            BridgeLog.Warn("whale fallback failed: " + e.Message);
        }
    }

    private static string CardKey(Dictionary<string, object> c)
        => c != null && c.TryGetValue("key", out var k) && k is string s ? s : null;

    /// <summary>基础牌（打击/防御）——删牌时优先丢它们。</summary>
    private static bool IsBasic(Dictionary<string, object> c)
    {
        try
        {
            var title = c.TryGetValue("title", out var t) ? t as string : null;
            var id = c.TryGetValue("id", out var i) ? i as string : null;
            if (title is "打击" or "防御") return true;
            return id != null && (id.Contains("STRIKE") || id.Contains("DEFEND"));
        }
        catch { return false; }
    }

    private static List<Dictionary<string, object>> RawCandidates(Dictionary<string, object> pending)
    {
        var list = new List<Dictionary<string, object>>();
        try
        {
            if (!pending.TryGetValue("candidates", out var cs) || cs is not System.Collections.IEnumerable seq) return list;
            foreach (var item in seq)
                if (item is Dictionary<string, object> d) list.Add(d);
        }
        catch { }
        return list;
    }

    private static List<string> Candidates(Dictionary<string, object> pending)
    {
        try
        {
            if (!pending.TryGetValue("candidates", out var cs) || cs is not System.Collections.IEnumerable seq) return null;
            var keys = new List<string>();
            foreach (var item in seq)
                if (item is Dictionary<string, object> d && d.TryGetValue("key", out var kk) && kk is string key)
                    keys.Add(key);
            return keys;
        }
        catch { return null; }
    }

    /// <summary>人在商店里吗（商店 = 唯一会 Pause 在房间里的地方）。</summary>
    private static bool InShop(AdvisorService service)
    {
        try { return service.State.CurrentRoom is MegaCrit.Sts2.Core.Rooms.MerchantRoom; }
        catch { return false; }
    }

    /// <summary>本局是否已结束（建筑师之后）。</summary>
    private static bool RunOver()
    {
        try { return MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState()?.IsGameOver == true; }
        catch { return false; }
    }

    private static bool AtMap()
    {
        try
        {
            var next = MapApi.Next();
            return next != null
                   && next.TryGetValue("canMove", out var cm)
                   && cm is System.Collections.IList list
                   && list.Count > 0;
        }
        catch { return false; }
    }

    private static bool IsUnsupportedCharacter(AdvisorService service)
    {
        try
        {
            var status = service.Autopilot.Status ?? "";
            return status.Contains("仅支持");
        }
        catch { return false; }
    }

    public static Dictionary<string, object> Snapshot()
    {
        var d = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["available"] = Available,
            ["auto"] = Auto,
            ["enabled"] = Enabled,
            ["mode"] = "局外交给战略助手 · 战斗交给求解器 · 其余归鲸鱼",
            ["userDisabled"] = _userDisabled,
            ["resumesThisRun"] = _resumeCount,
        };

        try
        {
            var service = AdvisorService.Current;
            if (service != null)
            {
                d["status"] = service.Autopilot.Status;
                d["fullAuto"] = service.Autopilot.FullAutomatic;
                d["waitingForManual"] = service.Autopilot.WaitingForManual;
                d["character"] = service.Player?.Character?.GetType().Name;
            }
            else
            {
                d["status"] = "未开局（战略助手只在局内挂载）";
            }
        }
        catch (Exception e)
        {
            d["statusError"] = e.GetType().Name + ": " + e.GetMessageSafe();
        }

        // 诊断：它当前算出来的建议（选牌/路线/事件…），以及理由条数
        try
        {
            var decision = AdvisorService.Current?.Decision;
            if (decision != null)
            {
                d["decisionKind"] = decision.Kind;
                d["recommendedKey"] = decision.Evaluation?.RecommendedKey;
                d["decisionOptions"] = decision.Options?.Length ?? 0;
                d["decisionWarnings"] = decision.Evaluation?.Warnings?.Length ?? 0;
            }
            else
            {
                d["decisionKind"] = null;
                d["recommendedKey"] = null;
            }
        }
        catch (Exception e)
        {
            d["decisionError"] = e.GetType().Name + ": " + e.Message;
        }

        if (!Available)
            d["hint"] = "战略助手还没挂上：它只在局内（NRun 就绪）后可用；或者本局不是单人铁甲战士。";
        else if (!Enabled)
            d["hint"] = "现在是建议模式：POST /strategist {\"on\":true} 让它接管局外操作。";
        else
            d["hint"] = "战略助手正在接管局外操作；战斗仍由求解器全自动。";

        var err = _lastError;
        if (!string.IsNullOrEmpty(err)) d["lastError"] = err;
        return d;
    }

    /// <summary>手动开关（/strategist POST）。</summary>
    public static Dictionary<string, object> Set(bool on)
    {
        var d = new Dictionary<string, object>();
        try
        {
            var service = AdvisorService.Current;
            if (service == null)
            {
                d["ok"] = false;
                d["detail"] = "当前没有可接管的对局（战略助手只在局内可用）";
                return d;
            }

            if (on)
            {
                _userDisabled = false;
                _resumeCount = 0;
                _nextResumeAt = 0;
                service.Autopilot.Enable(fullAutomatic: false);
            }
            else
            {
                _userDisabled = true;               // 玩家/接口主动关：本局不再自动开
                service.Autopilot.Pause("接口请求停止接管");
            }

            _autoStartedRun = CurrentRunKey();
            foreach (var kv in Snapshot()) d[kv.Key] = kv.Value;
            d["ok"] = true;
            d["detail"] = on ? "已让战略助手接管局外操作" : "已停止接管，交回给鲸鱼";
        }
        catch (Exception e)
        {
            d["ok"] = false;
            d["detail"] = e.GetType().Name + ": " + e.GetMessageSafe();
        }
        return d;
    }

    private static object CurrentRunKey()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            if (state == null) return null;
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(state);
        }
        catch { return null; }
    }

    private static string GetMessageSafe(this Exception e) => e?.Message ?? "";
}
