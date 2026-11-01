using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>
/// 把战斗整段交给「战斗路线求解器 / Combat Solver」这个创意工坊 mod。
///
/// 为什么走反射：求解器是**可选**依赖，而且它会跟着游戏版本自己更新。桥接不应该因为
/// 求解器没装、或者它改了内部签名就编不过/加载失败 —— 这里解析不到就把 available 报 false，
/// 桥接其余功能（地图、请求协议、手动出牌）照常工作。
///
/// 调到的入口（0.50.1 实测签名，均为 static）：
///   <c>SolverController.SetFullAuto(NGame, CombatState, bool)</c>
///   <c>SolverController.FullAutoEnabled / SolverDisabled / IsSearching / IsDeploying</c>
/// </summary>
public static class SolverBridge
{
    private const string SolverTypeName = "CombatSolver.SolverController";

    /// <summary>进战斗就开全自动。设 STS2_SOLVER_AUTO=0 可以关掉，交还给 agent 手动打。</summary>
    public static readonly bool Auto = System.Environment.GetEnvironmentVariable("STS2_SOLVER_AUTO") != "0";

    private static bool _bindingTried;
    private static double _nextResolveAt;
    private static volatile string _assembliesDebug;
    private static Type _type;
    private static MethodInfo _setFullAuto;
    private static PropertyInfo _pFullAuto, _pDisabled, _pSearching, _pDeploying, _pCanExecute;
    private static string _note = "求解器未加载";

    // ---- 缓存快照：Tick 在主线刷新，HTTP 线程只读这两个 ----
    private static volatile bool _available;
    private static volatile bool _fullAuto;
    private static volatile bool _disabled;
    private static volatile bool _searching;
    private static volatile bool _deploying;
    private static volatile bool _inCombat;
    private static volatile bool _blocking;
    private static volatile string _lastError;
    private static volatile int _enabledCombats;

    private static object _combatKey;
    private static volatile object _announcedCombat;
    private static PropertyInfo _pCurrentResult, _pLastCompleted, _pLastSetup, _pReviewed;
    private static PropertyInfo _pProjHp, _pEndedTurn, _pBattleHpSoFar, _pExpanded, _pPrevProj;
    private static object _lastPublishResult;
    private static volatile string _planLine;
    private static double _nextUiHideAt;
    private static double _lastUiLogAt;
    private static int _lastUiCount;
    private static volatile bool _uiHidden;
    private static double _lastTryAt;
    private static int _attempts;

    /// <summary>战斗里正在全自动 → agent 不应该再自己出牌（会和求解器的队列抢）。</summary>
    public static bool Blocking => _blocking;

    public static bool Available => _available;

    /// <summary>每帧主线调用：刷新快照 + 进战斗时确保求解器全自动已打开。</summary>
    public static void Tick()
    {
        try
        {
            var cs = CurrentCombat();
            _inCombat = cs != null;

            if (!Resolve())
            {
                _available = false;
                _blocking = false;
                return;
            }

            _available = true;
            Refresh(cs);

            if (cs == null)
            {
                // 出战斗就清掉本场记账；下一场重新尝试
                _combatKey = null;
                _announcedCombat = null;
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
            }

            // 从踏进战斗那一刻就接管：求解器要起搜索，agent 抢在这前 200ms 里出牌会跟它抢队列。
            // 只有「试够次数还没开起来」才交还手动 —— 那时是真的不支持这场战斗。
            _blocking = _fullAuto || _attempts < 10;

            if (_fullAuto)
            {
                // 求解器是「我」的战斗模块，不是场外的第三方：这里不播报、不自言自语，
                // 具体数字由 PublishPlan() 送到会话里（战损/回合数/世界线）。
                HideSolverUiTick();
                PublishPlan();
                return;
            }

            HideSolverUiTick();
            PublishPlan();

            // 战斗开头这几帧反复试（求解器自己要等回合建立完才肯接）
            if (_attempts >= 10) return;

            var now = Time.GetTicksMsec() / 1000.0;
            if (now - _lastTryAt < 0.35) { _blocking = false; return; }
            _lastTryAt = now;
            _attempts++;

            if (Enable(cs)) { _enabledCombats++; _blocking = true; }
            else _blocking = false;
        }
        catch (Exception e)
        {
            _blocking = false;
            _lastError = e.GetType().Name + ": " + e.Message;
            BridgeLog.Error("solver tick failed", e);
        }
    }

    /// <summary>给 /state 用的只读快照。</summary>

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
            ["available"] = _available,
            ["auto"] = Auto,
            ["inCombat"] = _inCombat,
            ["fullAuto"] = _fullAuto,
            ["blocking"] = _blocking,
            ["solverDisabled"] = _disabled,
            ["searching"] = _searching,
            ["deploying"] = _deploying,
            ["combatsEnabled"] = _enabledCombats,
            ["note"] = _note,
        };
        var plan = _planLine;
        if (!string.IsNullOrEmpty(plan)) d["plan"] = plan;
        d["uiHidden"] = _uiHidden;
        d["selectorYielded"] = RequestApi.YieldToSolver;
        var dbg = _assembliesDebug;
        if (!string.IsNullOrEmpty(dbg)) d["assemblies"] = dbg;
        var err = _lastError;
        if (!string.IsNullOrEmpty(err)) d["lastError"] = err;
        if (!_available)
            d["hint"] = "没找到 CombatSolver.SolverController：求解器没装/没加载。战斗要自己出牌（sts2_acts）。";
        else if (_disabled)
            d["hint"] = "求解器被设置里的「关闭求解器」停用了。要么在设置里打开，要么手动出牌。";
        else if (_inCombat && _blocking && _fullAuto)
            d["hint"] = "本场战斗由求解器全自动执行：不要调 sts2_acts 出牌，等战斗结束看 /request 拿奖励。搜索期间主线会卡顿，/state 慢是正常的。";
        else if (_inCombat && _blocking)
            d["hint"] = "已接管本场战斗，但求解器的全自动还没置上：等 2~3 秒再看 fullAuto，不要在此期间出牌。";
        else if (_inCombat)
            d["hint"] = "求解器在战斗里但没接管（还在搜索/未能开启）。等 2~3 秒再看 blocking；仍然是 false 才会退化成手动出牌。";
        return d;
    }

    /// <summary>
    /// 把求解器这一轮的结论送到会话里：预计战损多少血、预计第几回合结束、总共看过多少世界线。
    /// 只在「换了新结果」时播一次，避免每帧刷屏。
    /// </summary>
    private static void PublishPlan()
    {
        try
        {
            // 三个来源依次试：战斗中 CurrentResultForBugReport 有时是空的，
            // 上一场的结论在 LastCompletedResultForTesting / LastTurnSetupResultForTesting 里。
            var result = _pCurrentResult?.GetValue(null)
                         ?? _pLastCompleted?.GetValue(null)
                         ?? _pLastSetup?.GetValue(null);
            if (result == null || ReferenceEquals(result, _lastPublishResult)) return;
            _lastPublishResult = result;

            var hpLost = ReadInt(_pProjHp, result);
            var endedTurn = ReadNullableInt(_pEndedTurn, result);
            var hpSoFar = ReadInt(_pBattleHpSoFar, result);
            var expanded = ReadInt(_pExpanded, result);
            var worldlines = ReadLong(_pReviewed);
            var prevHp = ReadNullableInt(_pPrevProj, result);

            var parts = new List<string>();
            if (worldlines > 0) parts.Add($"已观测 {worldlines:N0} 条世界线");
            if (hpLost >= 0) parts.Add($"预计战损 {hpLost} 血");
            if (endedTurn > 0) parts.Add($"{endedTurn} 回合内结束战斗");
            if (prevHp != null && hpLost >= 0 && prevHp.Value != hpLost)
                parts.Add($"战损较上次 {prevHp.Value} → {hpLost}");
            if (hpSoFar > 0) parts.Add($"本场已掉 {hpSoFar} 血");
            if (parts.Count == 0) return;

            _planLine = string.Join("，", parts);
            var signal = _planSignal;
            _planSignal = NewSignal();
            signal.TrySetResult(_planLine);

            var data = new Dictionary<string, object>
            {
                ["hpLost"] = hpLost,
                ["endedTurn"] = endedTurn,
                ["battleHpLostSoFar"] = hpSoFar,
                ["expandedNodes"] = expanded,
                ["reviewedWorldlines"] = worldlines,
            };
            EventFeed.Publish(screen: "Combat", kind: "plan", summary: _planLine, items: new List<object>(), data: data);
            BridgeLog.Info("solver plan: " + _planLine);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("solver plan failed: " + e.Message);
        }
    }

    /// <summary>调试：把求解器各个结果来源 + 关键字段一次性报出来（排查「为什么没有 plan」）。</summary>
    public static Dictionary<string, object> Dump()
    {
        var d = new Dictionary<string, object>();
        if (_type == null) { d["ok"] = false; d["error"] = _note; return d; }
        try
        {
            foreach (var (name, prop) in new (string, PropertyInfo)[]
                     {
                         ("current", _pCurrentResult), ("lastCompleted", _pLastCompleted), ("lastSetup", _pLastSetup),
                     })
            {
                object v = null;
                try { v = prop?.GetValue(null); } catch (Exception e) { d[name + "_error"] = e.Message; }
                d[name] = v == null ? null : new Dictionary<string, object>
                {
                    ["type"] = v.GetType().Name,
                    ["hpLost"] = ReadInt(_pProjHp, v),
                    ["endedTurn"] = ReadNullableInt(_pEndedTurn, v),
                    ["hpSoFar"] = ReadInt(_pBattleHpSoFar, v),
                    ["expanded"] = ReadInt(_pExpanded, v),
                };
            }
            d["reviewedWorldlines"] = _pReviewed?.GetValue(null);
            d["planLine"] = _planLine;
            d["ok"] = true;
        }
        catch (Exception e) { d["ok"] = false; d["error"] = e.GetType().Name + ": " + e.Message; }
        return d;
    }

    /// <summary>把求解器自己画的那层界面藏起来（它属于「我」，不该在屏幕上单开一个窗口）。</summary>
    private static void HideSolverUiTick()
    {
        if (_type == null) return;
        var now = Time.GetTicksMsec() / 1000.0;
        if (now < _nextUiHideAt) return;
        _nextUiHideAt = now + 1.0;
        HideUi(true);
        // 求解器每秒都会重建自己的界面节点，所以每秒都要再藏一次；日志 10s 一条就够。
        if (now - _lastUiLogAt > 10 && _lastUiCount > 0)
        {
            _lastUiLogAt = now;
            BridgeLog.Info($"solver ui hidden: {_lastUiCount} nodes (re-hiding each second)");
        }
    }

    /// <summary>hide=true 藏起求解器自己的 CanvasItem 节点；hide=false 让它们回来（调试用）。</summary>
    public static Dictionary<string, object> HideUi(bool hide)
    {
        var hidden = 0;
        var shown = 0;
        try
        {
            var asm = _type?.Assembly;
            var tree = Engine.GetMainLoop() as SceneTree;
            var root = tree?.Root;
            if (asm == null || root == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "求解器未加载或没有场景树" };

            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                foreach (var child in node.GetChildren()) stack.Push(child);
                if (node is not CanvasItem item) continue;
                if (node.GetType().Assembly != asm) continue;
                if (hide)
                {
                    if (item.Visible) { item.Visible = false; hidden++; }
                }
                else if (!item.Visible) { item.Visible = true; shown++; }
            }
            if (hide) _uiHidden = true;
            else _uiHidden = false;
            _lastUiCount = hidden;
            return new Dictionary<string, object> { ["ok"] = true, ["hidden"] = hidden, ["shown"] = shown, ["uiHidden"] = _uiHidden };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = e.GetType().Name + ": " + e.Message };
        }
    }

    private static int ReadInt(PropertyInfo p, object target)
    {
        try { return p?.GetValue(target) is int v ? v : -1; } catch { return -1; }
    }

    private static long ReadLong(PropertyInfo p)
    {
        try { return p?.GetValue(null) is long v ? v : 0; } catch { return 0; }
    }

    private static int? ReadNullableInt(PropertyInfo p, object target)
    {
        try { return p?.GetValue(target) is int v ? v : null; } catch { return null; }
    }

    /// <summary>手动开关全自动（/solver POST）。必须在主线调用。</summary>
    public static Dictionary<string, object> Set(bool on)
    {
        var cs = CurrentCombat();
        var ok = false;
        var detail = "";
        if (!Resolve()) detail = _note;
        else if (cs == null) detail = "当前不在战斗里：求解器全自动只在战斗中有意义";
        else
        {
            try
            {
                if (on)
                {
                    _attempts = 0;
                    ok = Enable(cs);
                    detail = ok ? "已开启" : "SetFullAuto 调用没有生效";
                }
                else
                {
                    _setFullAuto.Invoke(null, new object[] { NGame.Instance, cs, false });
                    ok = true;
                    detail = "已关闭";
                    _attempts = 99; // 本场别再自动开回来
                }
            }
            catch (Exception e)
            {
                detail = e.GetType().Name + ": " + e.Message;
                _lastError = detail;
            }
        }
        Refresh(cs);
        _blocking = ok && on && _fullAuto;
        var d = Snapshot();
        d["ok"] = ok;
        d["detail"] = detail;
        return d;
    }

    // ------------------------------------------------------------------ 内部

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

    private static bool Enable(CombatState cs)
    {
        try
        {
            var game = NGame.Instance;
            if (game == null || !GodotObject.IsInstanceValid(game)) return false;
            _setFullAuto.Invoke(null, new object[] { game, cs, true });
            Refresh(cs);
            if (_fullAuto) return true;
            // SetFullAuto 是异步起搜索的：本帧没置上不算失败，下一帧再读一次
            return false;
        }
        catch (Exception e)
        {
            _lastError = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    private static void Refresh(CombatState cs)
    {
        try
        {
            if (!_bindingTried) { Bind(); }
            if (_type == null) return;
            _fullAuto = ReadBool(_pFullAuto);
            _disabled = ReadBool(_pDisabled);
            _searching = ReadBool(_pSearching);
            _deploying = ReadBool(_pDeploying);
        }
        catch { }
    }

    private static bool ReadBool(PropertyInfo p)
    {
        if (p == null) return false;
        try { return p.GetValue(null) is bool b && b; } catch { return false; }
    }

    /// <summary>
    /// 找求解器。mod 的装配顺序不由我们决定，第一帧可能还没轮到它，所以这里按 1.5s 冷却
    /// 反复找（找不到就返回 false，绝不去 Assembly.Load 一个副本 —— 那会加载出第二份
    /// CombatSolver，静态状态和正在跑的那份不是同一个，开关形同虚设）。
    /// </summary>
    private static bool Resolve()
    {
        if (_type != null) return true;

        var now = Time.GetTicksMsec() / 1000.0;
        if (now < _nextResolveAt) return false;
        _nextResolveAt = now + 1.5;

        var names = new List<string>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = null;
            var asmName = "?";
            try { asmName = asm.GetName().Name; } catch { }
            try { t = asm.GetType(SolverTypeName, false); } catch { }
            if (t == null)
            {
                if (asmName != null && asmName.IndexOf("Combat", StringComparison.OrdinalIgnoreCase) >= 0)
                    names.Add(asmName);
                continue;
            }
            _type = t;
            _note = "已找到 " + asmName + " v" + asm.GetName().Version;
            BridgeLog.Info("solver: " + _note);
            Bind();
            if (names.Count > 0) _assembliesDebug = string.Join(",", names);
            return _type != null;
        }

        _assembliesDebug = names.Count == 0 ? "(没有名字带 Combat 的装配)" : string.Join(",", names);
        _note = "未加载：装配里没有 " + SolverTypeName + "（当前带 Combat 的装配：" + _assembliesDebug + "）";
        BridgeLog.Warn("solver: " + _note);
        return false;
    }

    private static void Bind()
    {
        if (_bindingTried || _type == null) return;
        _bindingTried = true;
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        foreach (var m in _type.GetMethods(Flags))
        {
            if (m.Name != "SetFullAuto") continue;
            var ps = m.GetParameters();
            if (ps.Length == 3 && ps[2].ParameterType == typeof(bool)) { _setFullAuto = m; break; }
        }
        _pFullAuto = _type.GetProperty("FullAutoEnabled", BindingFlags.Public | BindingFlags.Static);
        _pDisabled = _type.GetProperty("SolverDisabled", BindingFlags.Public | BindingFlags.Static);
        _pSearching = _type.GetProperty("IsSearching", BindingFlags.Public | BindingFlags.Static);
        _pDeploying = _type.GetProperty("IsDeploying", BindingFlags.Public | BindingFlags.Static);
        _pCanExecute = _type.GetProperty("CanExecuteCurrentTurn", BindingFlags.Public | BindingFlags.Static);
        // 这三个在求解器里是 internal（Assembly 可见性）：必须带 NonPublic，否则静默拿到 null。
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        _pCurrentResult = _type.GetProperty("CurrentResultForBugReport", Any);
        _pLastCompleted = _type.GetProperty("LastCompletedResultForTesting", Any);
        _pLastSetup = _type.GetProperty("LastTurnSetupResultForTesting", Any);
        _pReviewed = _type.GetProperty("ReviewedWorldlinesTotal", Any);

        // 结果对象的字段（SolverResult）：预计战损 / 预计结束回合 / 已损失 / 展开节点
        var rt = _pCurrentResult?.PropertyType;
        if (rt != null)
        {
            const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _pProjHp = rt.GetProperty("ProjectedBattleHpLost", AnyInst);
            _pEndedTurn = rt.GetProperty("CombatEndedTurn", AnyInst);
            _pBattleHpSoFar = rt.GetProperty("BattleHpLostSoFar", AnyInst);
            _pExpanded = rt.GetProperty("ExpandedNodes", AnyInst);
            _pPrevProj = rt.GetProperty("PreviousProjectedBattleHpLost", AnyInst);
        }

        if (_setFullAuto == null || _pFullAuto == null)
        {
            _type = null;
            _note = "找到类型但签名对不上（SetFullAuto/FullAutoEnabled 缺失）：求解器版本不兼容";
            BridgeLog.Warn("solver: " + _note);
        }
        else
        {
            BridgeLog.Info($"solver: 已绑定 setFullAuto={_setFullAuto != null} fullAuto={_pFullAuto != null} " +
                           $"current={_pCurrentResult != null} lastCompleted={_pLastCompleted != null} " +
                           $"reviewed={_pReviewed != null} hpLost={_pProjHp != null} endedTurn={_pEndedTurn != null}");
        }
    }
}
