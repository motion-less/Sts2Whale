using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CombatSolver;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>
/// 合并版：桥接与求解器在**同一个程序集**里，所以这里全部是直接调用 —— 没有反射、没有字符串类型名、
/// 也没有 HTTP 轮询。`SolverController.CurrentResultForBugReport` 之类的成员本来是 internal，
/// 合并后同程序集直接可读。
///
/// 职责照旧：
///   * 进战斗 → 打开求解器全自动；
///   * 战斗期间 agent 不再自己出牌（/acts 会被拒）；
///   * 每次求解器给出新结论，就把「预计战损 / 回合数 / 世界线」发进事件流，供 agent 说给玩家听；
///   * 把求解器自己画的那层 UI 藏起来（它属于「我」，不该单开窗口）。
/// </summary>
public static class SolverBridge
{
    public static readonly bool Auto = System.Environment.GetEnvironmentVariable("STS2_SOLVER_AUTO") != "0";

    private static volatile bool _fullAuto;
    private static volatile bool _disabled;
    private static volatile bool _searching;
    private static volatile bool _deploying;
    private static volatile bool _inCombat;
    private static volatile bool _blocking;
    private static volatile string _lastError;
    private static volatile string _planLine;
    private static volatile bool _uiHidden;

    private static object _combatKey;
    private static object _lastPublishResult;
    private static object _spokeCombat;
    private static int _spokeHp = int.MinValue;
    private static int _spokeTurn = -1;
    private static int _hpAtCombatStart = -1;
    private static double _lastTryAt;
    private static double _nextUiHideAt;
    private static double _lastUiLogAt;
    private static int _lastUiCount;
    private static int _attempts;
    private static int _enabledCombats;

    public static bool Blocking => _blocking;
    public static bool Available => true;   // 合并版里求解器就是这个程序集的一部分

    public static void Tick()
    {
        try
        {
            var cs = CurrentCombat();
            // 房间里可能还挂着上一场战斗的 CombatState（胜利后要等奖励结算才换房间），
            // 真正该看的是 CombatManager 说的「战斗还在不在进行」——否则「收工」会拖到离开房间才说、
            // selector 也会一直让位到那时候。
            _inCombat = cs != null && CombatInProgress();

            Refresh();

            if (!_inCombat)
            {
                if (RequestApi.YieldToSolver) RequestApi.YieldToSolver = false;   // 战斗结束，selector 收回来
                if (_hpAtCombatStart > 0) SayCombatResult();
                _combatKey = null;
                _lastPublishResult = null;
                _spokeCombat = null;
                _spokeHp = int.MinValue;
                _spokeTurn = -1;
                _hpAtCombatStart = -1;
                _attempts = 0;
                _blocking = false;
                return;
            }

            if (!Auto || _disabled)
            {
                _blocking = false;
                return;
            }

            var key = (object)RuntimeHelpers.GetHashCode(cs);
            if (!Equals(key, _combatKey))
            {
                _combatKey = key;
                _attempts = 0;
                _lastTryAt = 0;
                _hpAtCombatStart = PlayerHp();
            }

            // 求解器要出牌：selector 立刻让位（ChooseACardScreen / 弃牌堆这类选择它自己有模拟）
            if (!RequestApi.YieldToSolver) RequestApi.YieldToSolver = true;

            // 踏进战斗就接管：求解器要起搜索，agent 抢在这前 200ms 里出牌会跟它抢队列。
            _blocking = _fullAuto || _attempts < 10;

            if (!_uiHidden) HideSolverOverlay(true);
            PublishPlan();

            if (_fullAuto) return;

            if (_attempts >= 10) return;   // 试够了还没开起来 → 交还手动

            var now = Time.GetTicksMsec() / 1000.0;
            if (now - _lastTryAt < 0.35) { _blocking = false; return; }
            _lastTryAt = now;
            _attempts++;

            if (SetFullAuto(cs, true)) { _enabledCombats++; _blocking = true; }
            else _blocking = false;
        }
        catch (Exception e)
        {
            _blocking = false;
            _lastError = e.GetType().Name + ": " + e.Message;
            BridgeLog.Error("solver tick failed", e);
        }
    }


    private static TaskCompletionSource<string> _planSignal = NewSignal();
    private static TaskCompletionSource<string> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 阻塞等「下一条新结论」：agent 每场战斗只需要调一次，拿到就直接说给玩家听，不用轮询。
    /// 超时就返回当前快照（可能已经有一条旧结论，看 plan 字段）。
    /// </summary>
    public static async Task<Dictionary<string, object>> Wait(double timeoutSeconds)
    {
        // 已经有结论就直接给（进战斗后 1~3 秒就会有一条），别让调用方白等；
        // 没有才挂起等下一条。
        var existing = _planLine;
        if (!string.IsNullOrEmpty(existing))
        {
            var now = Snapshot();
            now["timedOut"] = false;
            now["cached"] = true;
            now["plan"] = existing;
            return now;
        }

        var seconds = Math.Clamp(timeoutSeconds <= 0 ? 20 : timeoutSeconds, 1, 120);
        TaskCompletionSource<string> signal;
        lock (typeof(SolverBridge)) signal = _planSignal;

        var completed = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        var d = Snapshot();
        d["cached"] = false;
        d["timedOut"] = completed != signal.Task;
        if (completed == signal.Task) d["plan"] = await signal.Task;
        return d;
    }

    public static Dictionary<string, object> Snapshot()
    {
        var d = new Dictionary<string, object>
        {
            ["available"] = true,
            ["merged"] = true,
            ["auto"] = Auto,
            ["inCombat"] = _inCombat,
            ["fullAuto"] = _fullAuto,
            ["blocking"] = _blocking,
            ["solverDisabled"] = _disabled,
            ["searching"] = _searching,
            ["deploying"] = _deploying,
            ["combatsEnabled"] = _enabledCombats,
            ["uiHidden"] = _uiHidden,
            ["overlayVisible"] = SafeOverlayVisible(),
            // 选牌现在是「求解器自己管」还是「我（selector）在管」
            ["selectorYielded"] = RequestApi.YieldToSolver,
        };
        var plan = _planLine;
        if (!string.IsNullOrEmpty(plan)) d["plan"] = plan;
        var err = _lastError;
        if (!string.IsNullOrEmpty(err)) d["lastError"] = err;

        if (!Auto)
            d["hint"] = "STS2_SOLVER_AUTO=0：战斗交回 agent 手动打（sts2_acts）。";
        else if (_disabled)
            d["hint"] = "求解器被设置里的「关闭求解器」停用了。要么打开，要么手动出牌。";
        else if (_inCombat && _blocking && _fullAuto)
            d["hint"] = "这一场由我的求解模块在打：不要调 sts2_acts，等战斗结束的奖励请求。";
        else if (_inCombat && _blocking)
            d["hint"] = "已接管，求解器的全自动还没置上：等 2~3 秒，不要在此期间出牌。";
        else if (_inCombat)
            d["hint"] = "战斗里但没接管（搜索未完成或求解器放弃）。blocking 持续 false 超 5 秒才手动打。";
        return d;
    }

    public static Dictionary<string, object> Set(bool on)
    {
        var cs = CurrentCombat();
        var d = new Dictionary<string, object> { ["ok"] = false };
        if (cs == null)
        {
            d["detail"] = "当前不在战斗里：求解器全自动只在战斗中有意义";
            return d;
        }

        try
        {
            var ok = SetFullAuto(cs, on);
            if (!on) _attempts = 99;   // 本场别再自动开回来
            else _attempts = 0;
            Refresh();
            _blocking = ok && on && _fullAuto;
            d["ok"] = ok;
            d["detail"] = ok ? (on ? "已开启" : "已关闭") : "SetFullAuto 调用没有生效";
            foreach (var kv in Snapshot()) d[kv.Key] = kv.Value;
        }
        catch (Exception e)
        {
            d["detail"] = e.GetType().Name + ": " + e.Message;
            _lastError = (string)d["detail"];
        }
        return d;
    }

    /// <summary>调试：直接读求解器的三个结果来源（合并后不需要反射）。</summary>
    public static Dictionary<string, object> Dump()
    {
        var d = new Dictionary<string, object>();
        try
        {
            d["current"] = Describe(SolverController.CurrentResultForBugReport);
            d["lastCompleted"] = Describe(SolverController.LastCompletedResultForTesting);
            d["lastSetup"] = Describe(SolverController.LastTurnSetupResultForTesting);
            d["reviewedWorldlines"] = SolverController.ReviewedWorldlinesTotal;
            d["planLine"] = _planLine;
            d["ok"] = true;
        }
        catch (Exception e)
        {
            d["ok"] = false;
            d["error"] = e.GetType().Name + ": " + e.Message;
        }
        return d;
    }

    private static object Describe(SolverResult result)
    {
        if (result == null) return null;
        return new Dictionary<string, object>
        {
            ["type"] = result.GetType().Name,
            ["hpLost"] = result.ProjectedBattleHpLost,
            ["endedTurn"] = result.CombatEndedTurn,
            ["hpSoFar"] = result.BattleHpLostSoFar,
            ["expanded"] = result.ExpandedNodes,
        };
    }

    // ------------------------------------------------------------------ 内部

    private static void Refresh()
    {
        _fullAuto = SolverController.FullAutoEnabled;
        _disabled = SolverController.SolverDisabled;
        _searching = SolverController.IsSearching;
        _deploying = SolverController.IsDeploying;
    }

    /// <summary>战斗是不是真的还在打（房间残留 CombatState 不算）。</summary>
    private static bool CombatInProgress()
    {
        try { return MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress; }
        catch { return false; }
    }

    private static CombatState CurrentCombat()
    {
        try
        {
            var s = RunManager.Instance?.DebugOnlyGetState();
            if (s?.CurrentRoom is CombatRoom room) return room.CombatState;
        }
        catch { }
        return null;
    }

    private static bool SetFullAuto(CombatState cs, bool on)
    {
        try
        {
            var game = NGame.Instance;
            if (game == null || !GodotObject.IsInstanceValid(game)) return false;
            SolverController.SetFullAuto(game, cs, on);
            Refresh();
            return _fullAuto == on;
        }
        catch (Exception e)
        {
            _lastError = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    /// <summary>求解器这一轮的结论 → 事件流（agent 在聊天里用自己的话说出来）。</summary>
    private static void PublishPlan()
    {
        try
        {
            var result = SolverController.CurrentResultForBugReport
                         ?? SolverController.LastCompletedResultForTesting
                         ?? SolverController.LastTurnSetupResultForTesting;
            if (result == null || ReferenceEquals(result, _lastPublishResult)) return;
            _lastPublishResult = result;

            // 求解器这一轮算完、只剩阵亡路线 → 开始放失败曲；之后重算出存活路线会自己收。
            // 注意：搜索中途的「未找到生还路线」只是当前候选的预览（SpeculativeRoutePreview），
            // 不进 LatestResult，也不会走到这里；真开始只认「本次搜索已经结束」的结果。
            if (!result.OnlyDeathRoutesFound || !SolverController.IsSearching)
                LossSong.OnSolverResult(result.OnlyDeathRoutesFound);

            var hpLost = result.ProjectedBattleHpLost;
            var endedTurn = result.CombatEndedTurn;
            var hpSoFar = result.BattleHpLostSoFar;
            var expanded = result.ExpandedNodes;
            var worldlines = SolverController.ReviewedWorldlinesTotal;
            var prevHp = result.PreviousProjectedBattleHpLost;

            var parts = new List<string>();
            if (worldlines > 0) parts.Add($"已观测 {worldlines:N0} 条世界线");
            if (hpLost >= 0) parts.Add($"预计战损 {hpLost} 血");
            if (endedTurn is > 0) parts.Add($"{endedTurn} 回合内结束战斗");
            if (prevHp is not null && prevHp.Value != hpLost) parts.Add($"战损较上次 {prevHp.Value} → {hpLost}");
            if (hpSoFar > 0) parts.Add($"本场已掉 {hpSoFar} 血");
            if (parts.Count == 0) return;

            _planLine = string.Join("，", parts);
            SpeakPlan(_planLine, hpLost, endedTurn);
            var signal = _planSignal;
            _planSignal = NewSignal();
            signal.TrySetResult(_planLine);
            EventFeed.Publish(screen: "Combat", kind: "plan", summary: _planLine, items: new List<object>(),
                data: new Dictionary<string, object>
                {
                    ["hpLost"] = hpLost,
                    ["endedTurn"] = endedTurn,
                    ["battleHpLostSoFar"] = hpSoFar,
                    ["expandedNodes"] = expanded,
                    ["reviewedWorldlines"] = worldlines,
                });
            BridgeLog.Info("solver plan: " + _planLine);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("solver plan failed: " + e.Message);
        }
    }

    /// <summary>
    /// 把结论说进鲸鱼气泡（就是玩家看到的那个「鲸鱼聊天窗」）。
    /// 每场说一次；同一场里重算后战损变化 ≥2 血才再补一句，免得刷屏。
    /// </summary>
    /// <summary>战斗结束时报一句净结果（含燃烧之血这类回血，所以按「进场 → 现在」的差值算）。</summary>
    private static void SayCombatResult()
    {
        try
        {
            var now = PlayerHp();
            if (now <= 0) return;
            var lost = Math.Max(0, _hpAtCombatStart - now);
            var line = lost <= 0 ? $"收工，一滴血没掉，还剩 {now}。" : $"收工，掉了 {lost} 血，还剩 {now}。";
            try { Sts2AgentBridge.RunMemory.AddEvent("combat", lost <= 0 ? "无伤收工" : $"掉 {lost} 血收工"); } catch { }
            WhaleUi.Say(line, 10000, countsAsReason: false);
        }
        catch { }
    }

    /// <summary>当前主角色血量（借 GameApi 的 /state 构造，只在战斗起止各调一次）。</summary>
    private static int PlayerHp()
    {
        try
        {
            var st = GameApi.State();
            if (st != null
                && st.TryGetValue("players", out var ps)
                && ps is List<object> list && list.Count > 0
                && list[0] is Dictionary<string, object> p
                && p.TryGetValue("hp", out var hp)
                && hp is int v)
                return v;
        }
        catch { }
        return -1;
    }

    private static void SpeakPlan(string line, int hpLost, int? endedTurn)
    {
        try
        {
            var changed = !Equals(_combatKey, _spokeCombat)
                          || hpLost != _spokeHp
                          || (endedTurn ?? -1) != _spokeTurn;
            if (!changed) return;

            var first = !Equals(_combatKey, _spokeCombat);
            _spokeCombat = _combatKey;
            _spokeHp = hpLost;
            _spokeTurn = endedTurn ?? -1;

            WhaleUi.Say(first ? line : "重算后：" + line, 14000, countsAsReason: false);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("whale speak plan failed: " + e.Message);
        }
    }

    /// <summary>
    /// 用求解器自己的「用户隐藏意图」（Ctrl+F9 用的就是它）把整层藏掉：
    /// 它每次呈现都会读这个 flag，所以隐藏是持久的，不用我们每秒去猜节点。
    /// </summary>
    private static bool SafeOverlayVisible()
    {
        try { return SolverOverlay.IsVisible; } catch { return false; }
    }

    /// <summary>求解器正在搜世界线。</summary>
    public static bool Searching
    {
        // 用求解器自己的 IsSearching：SolverOverlay 的 presentation 在离开战斗后会残留，
        // 主菜单里也会报「正在搜索」。
        get { try { return SolverController.IsSearching; } catch { return false; } }
    }

    /// <summary>实时进度数字（累计、每秒）。不依赖 overlay 显示。</summary>
    public static (long Total, long PerSecond) WorldlineSnapshot()
    {
        try { return SolverController.WorldlineProgressSnapshot(); }
        catch { return (0, 0); }
    }

    /// <summary>求解器的内存快照（/solver/memory 排障用）。</summary>
    public static Dictionary<string, object> MemorySnapshot()
    {
        try
        {
            var snapshot = SolverController.CaptureSearchMemoryUsage();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["processWorkingSetMB"] = snapshot.ProcessWorkingSetBytes / 1048576L,
                ["physicalTotalMB"] = snapshot.PhysicalMemoryTotalBytes / 1048576L,
                ["physicalUsedMB"] = snapshot.PhysicalMemoryUsedBytes / 1048576L,
                ["physicalAvailableMB"] = snapshot.PhysicalMemoryAvailableBytes is long available
                    ? available / 1048576L
                    : null,
                ["hasPhysicalSample"] = snapshot.HasPhysicalMemorySample,
                ["systemMemoryLimitMB"] = snapshot.SystemMemoryLimitBytes == long.MaxValue
                    ? null
                    : snapshot.SystemMemoryLimitBytes / 1048576L,
                ["configuredBudgetMB"] = snapshot.ConfiguredMemoryBudgetBytes / 1048576L,
                ["serverGc"] = snapshot.IsServerGc,
                ["searchActive"] = snapshot.SearchActive,
            };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = e.GetType().Name + ": " + e.Message,
            };
        }
    }

    /// <summary>当前搜索的「当前路线摘要」：预计战损（气泡第二行用，不依赖 overlay 显隐）。</summary>
    public static string ProjectedLoss()
    {
        try { return SolverController.CurrentProjectedLossText(); }
        catch { return ""; }
    }

    /// <summary>求解器刚因为「预计本回合死亡」交还了操作权。</summary>
    public static bool DeathPredicted
    {
        get { try { return SolverController.LastStopWasDeathPrediction; } catch { return false; } }
    }

    /// <summary>HTTP /solver/ui 用的兼容入口。</summary>
    public static Dictionary<string, object> HideUi(bool hide)
    {
        HideSolverOverlay(hide);
        return new Dictionary<string, object> { ["ok"] = true, ["uiHidden"] = _uiHidden, ["method"] = "SolverOverlay.SetUserHidden" };
    }

    public static void HideSolverOverlay(bool hidden)
    {
        try
        {
            SolverOverlay.SetUserHidden(hidden);
            _uiHidden = hidden;
        }
        catch (Exception e)
        {
            _lastError = "overlay hide failed: " + e.Message;
        }
    }

}
