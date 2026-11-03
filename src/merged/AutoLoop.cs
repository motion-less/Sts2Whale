using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Sts2AgentBridge;

/// <summary>
/// 本局结束后的收尾 + 自动开下一把。
///
/// 触发点：见到「建筑师」被杀死后 <c>RunManager.IsGameOver=true</c>。
/// 流程：收尾界面上点「继续」→「主菜单」→ 主菜单上依次答「单人模式」→「标准模式」→
///       上一把的角色 → 上一把的进阶 → 新的一局开始。
///
/// 关掉它：环境变量 <c>STS2_AUTO_NEXT_RUN=0</c>。
/// </summary>
public static class AutoLoop
{
    public static readonly bool Enabled =
        !string.Equals(System.Environment.GetEnvironmentVariable("STS2_AUTO_NEXT_RUN"), "0", StringComparison.Ordinal);

    private const double GapSeconds = 1.5;      // 两个动作之间至少隔这么久

    private static double _nextAt;
    private static bool _wantNextRun;           // 上一把结束后要开新一把
    private static bool _closedForRun;          // 这一把的接管已经关过了
    private static double _nextProbeAt;          // /request 探测节流（5 秒）
    private static double _runOverAt;            // 本局结束的时刻
    private static bool _mapCloseDone;           // 关地图只做一次
    private static double _nextMapCloseAt;       // 关地图的节流
    private static bool _lastStandDone;          // 这一场已经认过输了
    private static double _lastStandEndAt;       // 认输后延时结束回合
    private static double _worldlineNextAt;      // 世界线进度播报节流（0.3 秒）
    private static string _worldlineLast;        // 上一条进度文案，避免重复刷
    private static long _worldlineMax;           // 本场最大值（每回合搜索会重新计数）
    private static string _characterKey =
        System.Environment.GetEnvironmentVariable("STS2_AUTO_CHARACTER") is { Length: > 0 } ch ? ch : "ironclad#1";
    private static int _ascension =
        int.TryParse(System.Environment.GetEnvironmentVariable("STS2_AUTO_ASCENSION"), out var a0) ? a0 : 10;

    /// <summary>记录本局角色/进阶，下一把沿用。每帧主线调用（很便宜：无 pending 就直接返回）。</summary>
    public static void Tick()
    {
        if (!Enabled) return;
        try
        {
            var now = Time.GetTicksMsec() / 1000.0;
            if (now < _nextAt) return;

            // 战斗里求解器在搜世界线：每秒把进度打到鲸鱼气泡上
            if (SolverBridge.Searching)
            {
                // 0.3 秒采样：小战斗的搜索只有几百毫秒，1 秒采样只能看到「0 条」。
                if (now >= _worldlineNextAt)
                {
                    _worldlineNextAt = now + 0.3;
                    var (total, perSecond) = SolverBridge.WorldlineSnapshot();
                    // 每一回合的搜索会重新计数，数字会往回跳；这里取「本场最大」，只往上涨。
                    if (total > _worldlineMax) _worldlineMax = total;
                    var progress = _worldlineMax <= 0
                        ? null
                        : perSecond > 0
                            ? $"已观测 {_worldlineMax:N0} 条世界线（{perSecond:N0} 条每秒）"
                            : $"已观测 {_worldlineMax:N0} 条世界线";
                    if (!string.IsNullOrEmpty(progress) && progress != _worldlineLast)
                    {
                        _worldlineLast = progress;
                        try { WhaleUi.Say(progress, 900, countsAsReason: false); } catch { }
                    }
                    // 第二行：求解器当前考虑的那条路线（「当前路线摘要」那块的文字版）
                    try { WhaleUi.SetStatus(SolverBridge.ProjectedLoss()); } catch { }
                }
            }
            else
            {
                _worldlineNextAt = 0;
                _worldlineLast = null;
                _worldlineMax = 0;
                if (WhaleUi.BubbleVisible) try { WhaleUi.ClearStatus(); } catch { }
            }

            // 投子认负：求解器刚因为「预计本回合死亡」交还了操作权
            if (!RunOver() && SolverBridge.DeathPredicted && !_lastStandDone)
            {
                _lastStandDone = true;
                _lastStandEndAt = now + 2.5;
                try { WhaleUi.Say("拼尽全力无法战胜，投子认负", 12000, countsAsReason: false); } catch { }
                BridgeLog.Info("last stand: 求解器预计本回合死亡 → 投子认负，2.5 秒后结束回合");
            }
            if (_lastStandEndAt > 0 && now >= _lastStandEndAt)
            {
                _lastStandEndAt = 0;
                GameApi.ForceEndTurn();
            }
            if (!SolverBridge.DeathPredicted && !RunOver()) _lastStandDone = false;

            if (!RunOver())
            {
                RememberRunIdentity();
                EnsureStrategistOn();
                _closedForRun = false;
                _runOverAt = 0;
                _mapCloseDone = false;
                // 局内：清掉「等下一把」的意图。
                // 这是踩过的坑：_wantNextRun 一旦被上一把置 true 就没人清，
                // 之后每 5 秒都会在局内误走一遍「本局已结束」→ 反复暂停战略助手。
                if (InRunNow()) { _wantNextRun = false; return; }
                // 不在局内（主菜单 / 收尾页）：只有「等着开下一把」才继续往下走菜单。
                if (!_wantNextRun) return;
            }

            // ---- 以下都是「本局已结束」 ----
            // 探测降到 5 秒一次：RequestApi.Current() 会扫场景/读状态，调用太勤会把游戏拖卡。
            if (now < _nextProbeAt) return;
            _nextProbeAt = now + 5.0;

            if (!_closedForRun)
            {
                _closedForRun = true;
                _runOverAt = now;
                _wantNextRun = true;
                StrategistBridge.DisableForRunEnd();     // 建筑师：先把全局助手关掉，让收尾界面正常跑
            }

            // 关地图：到建筑师节点（本局结束）之后 5 秒，只做一次。
            if (!_mapCloseDone && _runOverAt > 0 && now >= _runOverAt + 5.0)
            {
                _mapCloseDone = true;
                CloseMapIfOpen();
            }

            var snap = RequestApi.Current();
            if (snap == null) return;
            if (!snap.TryGetValue("pending", out var po) || po is not Dictionary<string, object> pending) return;

            var kind = pending.TryGetValue("kind", out var k) ? k as string : null;
            var id = pending.TryGetValue("id", out var ido) && ido != null ? Convert.ToInt64(ido) : -1L;
            if (string.IsNullOrEmpty(kind) || id < 0) return;

            if (kind == "gameover")
            {
                // 见到建筑师 = 这局结束；收尾页要走「继续」→「主菜单」两步。
                _wantNextRun = true;
                var pick = FirstKey(pending, "继续") ?? FirstKey(pending, "主菜单") ?? FirstKey(pending);
                if (pick != null) { Act(id, pick, "收尾"); return; }
                DriveGameOverScreen();                  // pending 给不出候选才直连按钮
                return;
            }

            if (kind == "menu" && _wantNextRun)
            {
                var stage = pending.TryGetValue("stage", out var s) ? s as string : null;
                // 主菜单上还有存档（上一把没正常结束，比如游戏被强退）：先继续那一局，
                // 等它自己结束时再走「开下一把」这条路。
                if (stage == "main" && FirstKey(pending, "单人模式") == null && FirstKey(pending, "继续游戏") is { } resumeKey)
                {
                    _wantNextRun = false;
                    BridgeLog.Info("auto-loop: 主菜单上还有存档 → 先继续这局，等它结束再开下一把");
                    Act(id, resumeKey, "继续存档");
                    return;
                }

                var pick = stage switch
                {
                    "main" => FirstKey(pending, "单人模式"),
                    "mode" => FirstKey(pending, "标准模式"),
                    "character" => PreferKey(pending, _characterKey),
                    "ascension" => PreferKey(pending, $"Ascension {_ascension}#") ?? FirstKey(pending, "Ascension"),
                    _ => null,
                };
                if (pick == null) return;
                if (stage == "character") _characterKey = pick;
                if (stage == "ascension") _ascension = ParseAscension(pick) ?? _ascension;
                if (stage == "ascension")
                {
                    _wantNextRun = false;
                    _closedForRun = false;
                    _nextAt = Time.GetTicksMsec() / 1000.0 + 3.0;   // 等局建好再确认接管
                    BridgeLog.Info($"auto-loop: 新局开始（{_characterKey} / A{_ascension}）");
                }
                Act(id, pick, $"开局 {stage}");
                return;
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("auto-loop: " + e.Message);
        }
    }

    /// <summary>新局开始后确认接管是开着的（最多等几秒）。</summary>
    private static void EnsureStrategistOn()
    {
        StrategistBridge.EnsureEnabledForRun();
    }

    /// <summary>调试用：立刻在主菜单开下一把（POST /loop/next-run）。</summary>
    public static Dictionary<string, object> Arm()
    {
        _wantNextRun = true;
        _nextAt = 0;
        return new Dictionary<string, object> { ["ok"] = true, ["wantNextRun"] = true, ["character"] = _characterKey, ["ascension"] = _ascension };
    }

    /// <summary>
    /// 本局已经结束但地图被打开（战略助手的 NMapRoom 分支 / 游戏自己切回地图）：
    /// 把地图关掉，让收尾界面重新露出来。方法名按几个候选反射试，失败就直接隐藏节点。
    /// </summary>
    private static void CloseMapIfOpen()
    {
        try
        {
            var now2 = Time.GetTicksMsec() / 1000.0;
            if (now2 < _nextMapCloseAt) return;            // 别每帧都调，Close 会触发界面重建
            var map = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance;
            if (map == null || !Godot.GodotObject.IsInstanceValid(map) || !map.IsOpen) return;
            _nextMapCloseAt = now2 + 0.5;

            // 真正能关掉的是 Close(bool animateOut)（反射确认过签名）；Hide() 只藏节点，IsOpen 还是 true。
            var closeMi = map.GetType().GetMethod("Close",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(bool) }, null);
            if (closeMi != null)
            {
                closeMi.Invoke(map, new object[] { false });
                Say("auto-loop: 本局已结束，地图开着 → NMapScreen.Close(false)");
                if (!map.IsOpen) return;
            }

            map.Visible = false;
            Say("auto-loop: 本局已结束，地图开着 → Close 没生效，先 Visible=false");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("auto-loop: 关地图失败 " + e.Message);
        }
    }

    /// <summary>
    /// 不依赖 /request，直接在场景里找 NGameOverScreen，点它的「继续 / 主菜单」。
    /// 收尾界面被地图盖住时 pending 通道可能读不到按钮，这条路照样能点。
    /// </summary>
    private static bool DriveGameOverScreen()
    {
        try
        {
            var now = Time.GetTicksMsec() / 1000.0;
            if (now < _gameOverClickAt) return false;

            var screen = FindByTypeName("NGameOverScreen");
            if (screen != null)
            {
                foreach (var field in new[] { "_continueButton", "_mainMenuButton" })
                {
                    var btn = FindField(screen, field) as MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton;
                    if (btn == null || !Godot.GodotObject.IsInstanceValid(btn) || !btn.IsVisibleInTree()) continue;
                    btn.ForceClick();
                    _gameOverClickAt = now + GapSeconds;
                    Say($"auto-loop: 收尾页点 {field}");
                    return true;
                }
            }

            // 兜底：收尾界面的类名/字段名可能变，直接按文字点。
            foreach (var text in new[] { "继续", "主菜单" })
            {
                var res = UiClicks.ClickText(text);
                if (res.TryGetValue("ok", out var ok) && ok is bool b && b)
                {
                    _gameOverClickAt = now + GapSeconds;
                    Say($"auto-loop: 按文字点「{text}」");
                    return true;
                }
            }
            return false;
        }
        catch (Exception e)
        {
            BridgeLog.Warn("auto-loop: 收尾按钮失败 " + e.Message);
            return false;
        }
    }

    private static Node FindByTypeName(string fragment)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            var seen = 0;
            while (stack.Count > 0 && seen++ < 20000)
            {
                var node = stack.Pop();
                for (var t = node.GetType(); t != null; t = t.BaseType)
                    if (t.Name.Contains(fragment, StringComparison.Ordinal)) return node;
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch { }
        return null;
    }

    private static object FindField(object target, string name)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (f != null) return f.GetValue(target);
        }
        return null;
    }

    private static double _gameOverClickAt;
    private static double _lastSay;

    /// <summary>同一条日志 3 秒内只写一次，避免每帧刷屏。</summary>
    private static void Say(string message)
    {
        var now = Time.GetTicksMsec() / 1000.0;
        if (now - _lastSay < 3) return;
        _lastSay = now;
        BridgeLog.Info(message);
    }

    private static void Act(long id, string pick, string what)
    {
        _nextAt = Time.GetTicksMsec() / 1000.0 + GapSeconds;
        BridgeLog.Info($"auto-loop: {what} → {pick}");
        _ = RequestApi.Respond(id, new[] { pick });
    }

    /// <summary>是否正处在一次对局里（主菜单为 false）——用来区分「局内」和「等下一把」。</summary>
    private static bool InRunNow()
    {
        try { return MegaCrit.Sts2.Core.Runs.RunManager.Instance is { IsInProgress: true }; }
        catch { return false; }
    }

    private static bool RunOver()
    {
        try { return MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState()?.IsGameOver == true; }
        catch { return false; }
    }

    /// <summary>把本局的角色 / 进阶记下来，下一把沿用。</summary>
    private static void RememberRunIdentity()
    {
        try
        {
            var st = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
            if (st == null) return;
            // 用户显式指定了 STS2_AUTO_ASCENSION 就按它来，否则沿用上一把的实际进阶。
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("STS2_AUTO_ASCENSION"))) return;
            var asc = (int)st.AscensionLevel;
            if (asc > 0) _ascension = asc;
        }
        catch { }
    }

    private static int? ParseAscension(string key)
    {
        var i = key.IndexOf("Ascension ", StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = key.Substring(i + "Ascension ".Length);
        var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    private static List<Dictionary<string, object>> Candidates(Dictionary<string, object> pending)
    {
        var list = new List<Dictionary<string, object>>();
        try
        {
            if (pending.TryGetValue("candidates", out var cs) && cs is System.Collections.IEnumerable seq)
                foreach (var item in seq)
                    if (item is Dictionary<string, object> d) list.Add(d);
        }
        catch { }
        return list;
    }

    private static string KeyOf(Dictionary<string, object> c)
        => c.TryGetValue("key", out var k) && k is string s ? s : null;

    private static string FirstKey(Dictionary<string, object> pending, string contains = null)
    {
        var cands = Candidates(pending);
        if (contains == null) return cands.Count > 0 ? KeyOf(cands[0]) : null;
        return cands.Select(KeyOf).FirstOrDefault(k => k != null && k.Contains(contains, StringComparison.Ordinal));
    }

    private static string PreferKey(Dictionary<string, object> pending, string preferred)
    {
        var keys = Candidates(pending).Select(KeyOf).Where(k => k != null).ToList();
        if (preferred != null)
        {
            var hit = keys.FirstOrDefault(k => k == preferred || k.StartsWith(preferred, StringComparison.Ordinal));
            if (hit != null) return hit;
        }
        return keys.FirstOrDefault();
    }
}
