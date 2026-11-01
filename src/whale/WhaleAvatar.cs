using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Sts2AgentBridge;

namespace Sts2Whale.Whale;

/// <summary>
/// 鲸鱼娘的「皮套导演」：把 mod 里发生的事翻译成 VTube Studio 的表情、动作和口型。
///
/// 三条输入：
///   1. 游戏事件（<see cref="OnGameEvent"/>，由 EventFeed 直接回调，走的是进程内，没有 HTTP 延迟）
///   2. 气泡台词（<see cref="OnSpeech"/>，由 WhaleUi.Say 回调）
///   3. 手动触发（<see cref="Test"/>，给 /avatar/test 用）
///
/// 判定分三层：状态差分（掉血/击杀）→ 规则（kind/summary）→ 默认不动。
/// 所有决策都会写一行日志，表情不对时能直接看出是哪个规则命中的。
/// </summary>
public static class WhaleAvatar
{
    private const int TickMs = 33; // ≈30Hz，足够跟嘴型

    private static readonly object Gate = new();
    private static VtsConfig _cfg = new();
    private static VtsClient? _client;
    private static Thread? _tickThread;
    private static Thread? _speechThread;
    private static bool _installed;

    private static readonly object SpeechGate = new();
    private static string? _pendingSpeech; // 只留一句，避免合成排队越积越多
    private static SpeechLine? _line;

    private static EmotionCue? _cue;
    private static string _cueKey = "";
    private static long _cueUntilMs;
    private static long _lastCueAtMs;
    private static string _lastCueKey = "";

    private static string? _pendingHotkey; // 主线程只登记，由心跳线程发，避免卡帧
    private static readonly Dictionary<string, double> Overrides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> LastSent = new(StringComparer.OrdinalIgnoreCase);

    private static int _lastHp = -1;
    private static int _lastEnemyCount = -1;
    private static int _lastTurn = -1;

    private static long _events;
    private static long _cues;
    private static string _lastEvent = "";

    // 表情是通过 exp3 显式开关的（热键是 toggle，不可靠）
    private static string _activeExpression = "";
    private static bool _mouthWasActive;   // 上帧是否在覆盖嘴
    private static double _mouthSmooth;    // 口型输出平滑（避免 30ms 一帧地抽）
    private static string? _stickyExpression;  // 状态型表情（有格挡 → 钢盆），优先级最高
    private static bool _exprForce;            // 这次表情切换要无视基线（摘钢盆用）
    private static readonly IdleSway Sway = new();
    private static readonly List<(string, float)> ValuesBuf = new(8);
    private static readonly List<(string, float)> ReleasesBuf = new(8);
    private static readonly HitReaction Hit = new();
    private static double _lastSentTilt = double.NaN;   // 上一次发给 VTS 的绝对角度
    private static long _lastTiltLogAt;
    private static bool _modelStraightened;
    private static long _lastExprResetAt;
    private static int _exprRefreshCount;
    private static bool _exprResetRunning;   // 启动后是否已经强制摆正
    private static readonly HashSet<string> _stickyActive = new(StringComparer.Ordinal);
    private static bool _thinking;            // 求解器正在算牌
    private static double _hitBlocked;   // 这一击被格挡掉的
    private static double _hitHpLost;    // 这一击实际掉的血
    private static long _hitPendingAt;
    private static string? _exprToSet;
    private static bool _exprActive;

    public static bool Enabled { get; private set; }
    public static string LastDecision { get; private set; } = "";

    // ---------------------------------------------------------------- 生命周期

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        try
        {
            var dir = ModPaths.DataDir;
            var cfgPath = Path.Combine(dir, "vts.json");
            _cfg = VtsConfig.Load(cfgPath);
            Enabled = _cfg.Enabled;

            BridgeLog.Info($"皮套联动: enabled={_cfg.Enabled} config={cfgPath}");
            if (!Enabled) return;

            _client = new VtsClient(_cfg.WsUrl, ModPaths.Data("vts_token.json"),
                                    _cfg.PluginName, _cfg.PluginDeveloper);
            _client.Log += m => BridgeLog.Info("[vts] " + m);
            _client.Start();
            _client.ExpressionsReady += ApplyStartupExpressions;

            // 常驻 TTS：游戏起来就拉，退出时收（另有父进程/空闲两道保险）
            var ttsScript = string.IsNullOrWhiteSpace(_cfg.Speech.DaemonScript)
                ? ModPaths.Script("tts_daemon.py")
                : _cfg.Speech.DaemonScript;
            TtsDaemon.Start(_cfg.Speech.DaemonPython, ttsScript, _cfg.Speech.DaemonPort, _cfg.Speech.DaemonIdleMinutes);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                // 从游戏菜单退出时也清一次表情，别把「圈圈眼」留给下一次启动
                try { ClearExpressions(); } catch { }
                TtsDaemon.Stop();
            };
            DamageHooks.Apply();

            _tickThread = new Thread(TickLoop) { IsBackground = true, Name = "whale-avatar" };
            _tickThread.Start();

            _speechThread = new Thread(SpeechLoop) { IsBackground = true, Name = "whale-speech" };
            _speechThread.Start();

            // 后台预热一档，别让第一句话等 5 秒
            _ = Task.Run(() => SpeechPlayer.Warmup(_cfg.Speech));
        }
        catch (Exception e)
        {
            Enabled = false;
            BridgeLog.Error("皮套联动启动失败", e);
        }
    }

    public static void Shutdown()
    {
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    /// <summary>连上后把模型的表情调整成我们想要的基础状态。</summary>
    private static void ApplyStartupExpressions()
    {
        // 连上 / 模型加载 / 重连都会通知一次：800ms 内只做一次，且同一时间只跑一个后台任务
        _exprRefreshCount++;
        var nowMs = System.Environment.TickCount64;
        BridgeLog.Info($"[avatar] 表情列表刷新 #{_exprRefreshCount}");
        if (nowMs - _lastExprResetAt < 800)
        {
            BridgeLog.Info("[avatar] 距上次复位太近，跳过这次");
            return;
        }
        if (_exprResetRunning) return;
        _lastExprResetAt = nowMs;
        _exprResetRunning = true;

        try
        {
            // VTS 会记住模型上一次的表情（比如上次退出时还挂着「圈圈眼」「黑脸」），
            // 所以启动时把**我们可能开过**的 exp3 全部强制关一遍，再按配置打开要的
            var liveAtConnect = _client?.ActiveExpressions() ?? new List<string>();
            if (liveAtConnect.Count > 0)
                BridgeLog.Info($"[avatar] VTS 连上时挂着的表情：{string.Join("、", liveAtConnect)}");

            var names = KnownExpressions();
            // VTS 报告「当前挂着」的全部表情也一起关（包括我们没用到过的）
            foreach (var live in _client?.ActiveExpressions() ?? new List<string>()) names.Add(live);

            var toClose = new List<string>(names);
            var toOpen = _cfg.StartupExpressions.Where(k => k.Value).Select(k => k.Key).ToList();

            BridgeLog.Info($"[avatar] 启动表情复位：关闭 {toClose.Count} 个，保留 {toOpen.Count} 个");

            // 一次发十几条 VTS 可能吃不下：后台慢慢发，每条隔 30ms
            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var name in toClose)
                    {
                        _client?.ActivateExpression(name, false, force: true);
                        Thread.Sleep(30);
                    }
                    foreach (var name in toOpen)
                    {
                        _client?.ActivateExpression(name, true, force: true);
                        Thread.Sleep(30);
                    }
                    // 刷新一次状态做校验：VTS 有时会晚一步把保存的表情套回来
                    Thread.Sleep(400);
                    _client?.RequestExpressions();
                    Thread.Sleep(500);
                    var still = _client?.ActiveExpressions() ?? new List<string>();
                    if (still.Count > 0)
                    {
                        BridgeLog.Warn($"[avatar] 复位校验：还剩 {string.Join("、", still)}，补关一次");
                        foreach (var name in still)
                        {
                            _client?.ActivateExpression(name, false, force: true);
                            Thread.Sleep(40);
                        }
                    }
                    else
                    {
                        BridgeLog.Info("[avatar] 表情复位完成（校验通过：无激活表情）");
                    }
                }
                finally
                {
                    _exprResetRunning = false;
                }
            });
        }
        catch (Exception e)
        {
            _exprResetRunning = false;
            BridgeLog.Warn("[avatar] 套用启动表情失败: " + e.Message);
        }
    }

    /// <summary>求解器算牌时要挂的表情（可多个）。</summary>
    private static List<string> ThinkingNames()
    {
        var list = new List<string>();
        if (_cfg.ThinkingExpressions != null)
            foreach (var n in _cfg.ThinkingExpressions)
                if (!string.IsNullOrWhiteSpace(n) && !list.Contains(n)) list.Add(n);
        if (list.Count == 0 && !string.IsNullOrWhiteSpace(_cfg.ThinkingExpression))
            list.Add(_cfg.ThinkingExpression);
        return list;
    }

    /// <summary>mod 可能激活过的所有 exp3 名字。</summary>
    private static HashSet<string> KnownExpressions()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cue in _cfg.Emotions.Values)
            if (!string.IsNullOrEmpty(cue.Expression)) names.Add(cue.Expression);
        if (!string.IsNullOrEmpty(_cfg.BlockExpression)) names.Add(_cfg.BlockExpression);
        foreach (var n in ThinkingNames()) names.Add(n);
        foreach (var key in _cfg.StartupExpressions.Keys) names.Add(key);
        return names;
    }

    /// <summary>退出前把我们开过的表情全关掉，免得 VTS 记住下次开局一脸哭相。</summary>
    public static void ClearExpressions()
    {
        if (!Enabled || _client == null) return;
        try
        {
            foreach (var name in KnownExpressions())
            {
                _client.ActivateExpression(name, false, force: true);
                Thread.Sleep(20);
            }
            _stickyExpression = null;
            _stickyActive.Clear();
            _thinking = false;
            _lastSentTilt = double.NaN;
            _client.ResetModelRotation();
            BridgeLog.Info("[avatar] 退出清理：表情已关、角度归零");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[avatar] 退出清理失败: " + e.Message);
        }
    }

    // ---------------------------------------------------------------- 输入

    /// <summary>游戏事件（EventFeed 每次 Publish 时同步调用，必须很快返回）。</summary>
    public static void OnGameEvent(string screen, string kind, string summary, Dictionary<string, object>? data)
    {
        if (!Enabled) return;
        try
        {
            _events++;
            var brief = summary != null && summary.Length > 70 ? summary[..70] + "…" : (summary ?? "");
            _lastEvent = $"{kind} · {brief}";

            var cue = Decide(kind, summary, data, out var intensity, out var reason, out var key);
            if (cue == null)
            {
                // 只有非 turn 的事件才逐条记「无动作」，turn 太密会刷屏
                if (kind != "turn") BridgeLog.Info($"[avatar] 事件 {kind} → 没有匹配的规则");
                else BridgeLog.Info($"[avatar] 事件 turn | {brief}");
                return;
            }
            BridgeLog.Info($"[avatar] 事件 {kind} | {brief}");
            Apply(cue, intensity, reason, key);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("皮套事件处理失败: " + e.Message);
        }
    }

    /// <summary>鲸鱼娘说话（气泡出现时）。source 只有 "brain" 才是 DeepSeek 生成的台词。</summary>
    public static void OnSpeech(string text, int durationMs, string source = "local")
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return;
        if (_cfg.Speech.Mode.Equals("off", StringComparison.OrdinalIgnoreCase)) return;

        // 不是 DeepSeek 返回的（本地垫话、solver 提示、进度条、外部推送）就不念
        if (_cfg.Speech.OnlyBrain && !string.Equals(source, "brain", StringComparison.OrdinalIgnoreCase)) return;

        if (text.Length > _cfg.Speech.MaxChars) text = text[.._cfg.Speech.MaxChars] + "…";
        lock (SpeechGate) _pendingSpeech = text; // 新的一句顶掉还没念的旧的

        // 说话时顺手给一点「表情同步」：句子短就保持当前表情，不额外动
        if (_line == null && durationMs > 0) LastDecision = $"speech: {Trim(text, 24)}";
    }

    // ---------------------------------------------------------------- 判定

    private static EmotionCue? Decide(string kind, string summary, Dictionary<string, object>? data,
                                      out double intensity, out string reason, out string key)
    {
        intensity = 0.6;
        reason = "";
        key = "";

        if (kind == "turn" && data != null)
        {
            var hp = ReadInt(data, "hp");
            var maxHp = Math.Max(1, ReadInt(data, "maxHp"));
            var turn = ReadInt(data, "turn");
            var enemies = ReadCount(data, "enemies");

            var drop = _lastHp >= 0 ? _lastHp - hp : 0;
            var killed = _lastEnemyCount >= 0 ? _lastEnemyCount - enemies : 0;
            var newTurn = _lastTurn >= 0 && turn != _lastTurn;

            _lastHp = hp;
            _lastEnemyCount = enemies;
            _lastTurn = turn;

            if (drop > 0)
            {
                intensity = Math.Clamp(0.35 + drop / (double)maxHp * 3.0, 0.35, 1.0);
                reason = $"damage -{drop}hp ({hp}/{maxHp})";
                key = "hurt";
                return Cue(key);
            }

            if (killed > 0)
            {
                intensity = 0.8;
                reason = $"kill x{killed}";
                key = "victory";
                return Cue(key);
            }

            if (newTurn && turn <= 1 && enemies > 0)
            {
                reason = "combat start";
                key = "combat_start";
                return Cue(key);
            }

            if (newTurn && enemies > 0 && summary.Contains("精英", StringComparison.Ordinal))
            {
                reason = "elite turn";
                key = "elite";
                return Cue(key);
            }

            return null;
        }

        switch (kind)
        {
            case "reward":
                reason = "reward screen";
                key = "reward";
                return Cue(key);
            case "shop":
                reason = "shop";
                key = "shop";
                return Cue(key);
            case "rest":
                reason = "rest site";
                key = "rest";
                return Cue(key);
            case "card_select":
                reason = "card select";
                key = "card_select";
                return Cue(key);
        }

        // 房间/屏幕级别的兜底规则
        var hay = (summary ?? "") + " " + (kind ?? "");
        if (hay.Contains("精英", StringComparison.Ordinal) || hay.Contains("Elite", StringComparison.Ordinal))
        {
            reason = "elite room";
            key = "elite";
            return Cue(key);
        }
        if (hay.Contains("Boss", StringComparison.Ordinal) || hay.Contains("BOSS", StringComparison.Ordinal))
        {
            reason = "boss";
            key = "elite";
            return Cue(key);
        }
        if (hay.Contains("死亡", StringComparison.Ordinal) || hay.Contains("败北", StringComparison.Ordinal))
        {
            reason = "defeat";
            key = "death";
            return Cue(key);
        }

        return null;
    }

    private static EmotionCue? Cue(string key)
        => _cfg.Emotions.TryGetValue(key, out var cue) ? cue : null;

    private static void Apply(EmotionCue cue, double intensity, string reason, string key)
    {
        var now = Environment.TickCount64;

        lock (Gate)
        {
            if (key == _lastCueKey && now - _lastCueAtMs < _cfg.CooldownMs) return;

            // 高优先级的表情正在 hold 时，低优先级的不许顶掉它（比如格挡期间不换表情）
            if (_cue != null && now <= _cueUntilMs)
            {
                var activePriority = Cue(_cueKey)?.Priority ?? 0;
                if (activePriority > cue.Priority)
                {
                    BridgeLog.Info($"[avatar] 跳过「{key}」：{_cueKey}(优先级 {activePriority}) 还在 hold");
                    return;
                }
            }

            _lastCueKey = key;
            _lastCueAtMs = now;

            _cue = cue;
            _cueKey = key;
            _cueUntilMs = now + Math.Max(400, cue.HoldMs);

            Overrides.Clear();
            if (cue.Params != null)
                foreach (var kv in cue.Params)
                    Overrides[kv.Key] = kv.Value * intensity;

            _cues++;
            LastDecision = $"{key} ← {reason} (intensity {intensity:0.00}, hold {cue.HoldMs}ms)";
            if (!string.IsNullOrEmpty(cue.Expression))
            {
                _exprToSet = cue.Expression;
                _exprActive = true;
            }
            else if (!string.IsNullOrEmpty(cue.Hotkey)) _pendingHotkey = cue.Hotkey;
            BridgeLog.Info("[avatar] " + LastDecision);
        }
    }

    // ---------------------------------------------------------------- 心跳

    private static void TickLoop()
    {
        while (true)
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                BridgeLog.Warn("皮套心跳失败: " + e.Message);
            }
            Thread.Sleep(TickMs);
        }
    }

    private static void Tick()
    {
        var client = _client;
        if (client == null || !client.Authenticated) return;

        var now = Environment.TickCount64;
        var values = ValuesBuf;
        var releases = ReleasesBuf;
        values.Clear();
        releases.Clear();
        string? hotkey;
        var stickyOn = new List<string>();
        var stickyOff = new List<string>();
        string? exprToSet;
        bool exprActive;
        bool exprForce;

        lock (Gate)
        {
            hotkey = _pendingHotkey;
            _pendingHotkey = null;
            // 状态型表情（可多个）：格挡（钢盆）优先；否则求解器算牌时挂 thinking 列表（圈圈眼 + 加载）
            var wantSticky = new List<string>();
            if (_stickyExpression != null) wantSticky.Add(_stickyExpression);
            else if (_thinking) wantSticky.AddRange(ThinkingNames());

            foreach (var name in wantSticky)
            {
                if (_stickyActive.Contains(name)) continue;
                _stickyActive.Add(name);
                stickyOn.Add(name);
            }
            foreach (var name in new List<string>(_stickyActive))
            {
                if (wantSticky.Contains(name)) continue;
                _stickyActive.Remove(name);
                stickyOff.Add(name);
            }

            // 有常驻表情时，普通 cue 不许改表情
            if (wantSticky.Count > 0) _exprToSet = null;

            exprToSet = _exprToSet;
            exprActive = _exprActive;
            exprForce = _exprForce;
            _exprToSet = null;
            _exprForce = false;

            if (_cue != null && now > _cueUntilMs)
            {
                _cue = null;
                _cueKey = "";
                // 用 weight=0 释放，而不是压成 0：压 0 会把模型自己的默认表情（微笑/呼吸）也盖掉
                foreach (var k in Overrides.Keys) releases.Add((k, 0f));
                Overrides.Clear();
                foreach (var k in new List<string>(LastSent.Keys)) LastSent.Remove(k);
                if (_activeExpression != "") { _exprToSet = _activeExpression; _exprActive = false; }
            }

            // 口型：只有说话时才覆盖 MouthOpen；说完发一次 0 就撒手，
            // 让模型自己的 idle 去动嘴（否则会是一张焊死的三角嘴）
            var line = _line;
            if (line != null && line.Finished)
            {
                line.Dispose();
                _line = null;
                line = null;
            }

            if (line != null)
            {
                if (!_mouthWasActive) _mouthSmooth = 0;   // 新的一句从闭合开始
                var raw = line.ValueAt(line.ElapsedMs);
                _mouthSmooth += (raw - _mouthSmooth) * Math.Clamp(_cfg.Speech.Smoothing, 0.05, 1.0);

                var value = Math.Min(_cfg.Speech.MaxOpen, _mouthSmooth * Math.Max(0.0, _cfg.Speech.Gain));
                Push(values, _cfg.MouthParam, (float)value, force: true);
                _mouthWasActive = true;
            }
            else if (_mouthWasActive)
            {
                releases.Add((_cfg.MouthParam, 0f));
                LastSent.Remove(_cfg.MouthParam);
                _mouthWasActive = false;
            }

            foreach (var kv in Overrides)
                Push(values, kv.Key, (float)kv.Value, force: false);

            // idle 常驻表情参数（比如一点点微笑），有 cue 覆盖时让位
            if (_cue == null && _cfg.IdleParams != null)
                foreach (var kv in _cfg.IdleParams)
                    if (!Overrides.ContainsKey(kv.Key))
                        Push(values, kv.Key, (float)kv.Value, force: false);

            // idle 摆头：噪声驱动的，比正弦自然（10Hz 更新）
            if (_cfg.IdleSway &&
                (Sway.SoftLimit = Math.Max(4.0, _cfg.SwayMaxAngle)) > 0 &&
                (Sway.UpLimit = Math.Max(1.0, _cfg.SwayMaxUp)) > 0 &&
                (Sway.DownLimit = Math.Max(1.0, _cfg.SwayMaxDown)) > 0 &&
                Sway.TryStep(now, Math.Max(0.0, _cfg.SwayAmplitude), _cfg.SwaySmoothing,
                             out var sx, out var sy, out var sz))
            {
                Push(values, "FaceAngleX", sx, force: true);
                Push(values, "FaceAngleY", sy, force: true);
                Push(values, "FaceAngleZ", sz, force: true);
            }
        }

        if (hotkey != null) client.TriggerHotkey(hotkey);

        StraightenOnce();
        UpdateThinking();

        if (_cfg.HitTilt)
        {
            double target;
            lock (Gate)
            {
                if ((_hitBlocked > 0 || _hitHpLost > 0) && now - _hitPendingAt > 40)
                {
                    var incoming = _hitBlocked + _hitHpLost;
                    Hit.Hit(incoming, _cfg.HitDegreesPerDamage, _cfg.HitMaxDegrees);
                    BridgeLog.Info($"[avatar] 受击 攻{incoming:0}（格挡 {_hitBlocked:0}，掉血 {_hitHpLost:0}）");
                    _hitBlocked = 0;
                    _hitHpLost = 0;
                }

                target = Hit.Tick(now, _cfg.HitMaxDegrees, out var tiltNow);
                if (now - _lastTiltLogAt > 150 && Math.Abs(target - _lastSentTilt) > 0.2)
                {
                    _lastTiltLogAt = now;
                    BridgeLog.Info($"[avatar] 受击倾斜 {tiltNow:0.0}°");
                }
            }

            // 绝对角度：目标是多少就是多少，回正发 0 就真的回正，没有排队动画和累积误差
            if (double.IsNaN(_lastSentTilt) || Math.Abs(target - _lastSentTilt) > 0.1)
            {
                client.SetModelRotation(target, 0.05);
                _lastSentTilt = target;
            }
        }

        // 常驻表情（格挡 / 思考列表）：只在状态变化时发一次
        foreach (var name in stickyOff) client.ActivateExpression(name, false, force: true);
        foreach (var name in stickyOn) client.ActivateExpression(name, true, force: true);

        if (exprToSet != null)
        {
            if (exprActive)
            {
                if (_activeExpression != "" && _activeExpression != exprToSet)
                    client.ActivateExpression(_activeExpression, false, force: exprForce);
                client.ActivateExpression(exprToSet, true, force: exprForce);
                _activeExpression = exprToSet;
            }
            else
            {
                client.ActivateExpression(exprToSet, false, force: exprForce);
                if (_activeExpression == exprToSet) _activeExpression = "";
            }
        }

        if (releases.Count > 0) client.InjectParameters(releases, release: true);
        if (values.Count > 0) client.InjectParameters(values);
    }

    /// <summary>事件路径和伤害补丁共用的一次触发。</summary>
    private static void Fire(string key, double intensity, string reason)
    {
        var cue = Cue(key);
        if (cue == null) return;
        Apply(cue, intensity, reason, key);
    }

    // ---------------------------------------------------------------- 伤害补丁入口

    /// <summary>玩家掉血（Harmony postfix 调，主线程）。</summary>
    public static void OnPlayerHpLost(double hpLost)
    {
        if (!Enabled || hpLost <= 0) return;
        _events++;
        lock (Gate)
        {
            _hitHpLost += hpLost;
            _hitPendingAt = Environment.TickCount64;
        }
        _lastEvent = $"掉血 {hpLost:0}";

        var key = hpLost >= 15 ? "hurt_big" : hpLost >= 6 ? "hurt" : "hurt_small";
        Fire(key, Math.Clamp(0.4 + hpLost / 40.0, 0.4, 1.0), $"玩家掉血 -{hpLost:0}");
    }

    /// <summary>被格挡掉的那部分伤害（和掉血一起凑成「这一击的攻击力」）。</summary>
    public static void OnPlayerBlockAbsorbed(double blocked)
    {
        if (!Enabled || blocked <= 0) return;
        lock (Gate)
        {
            _hitBlocked += blocked;
            _hitPendingAt = Environment.TickCount64;
        }
    }

    /// <summary>
    /// 玩家格挡值变化（Harmony postfix 调，主线程）。
    /// 有格挡就一直挂钢盆，掉到 0 就摘掉——不看时间，只看状态。
    /// </summary>
    public static void OnPlayerBlockChanged(int block)
    {
        if (!Enabled) return;

        var want = block > 0 ? _cfg.BlockExpression : null;
        if (string.IsNullOrEmpty(want)) want = null;

        lock (Gate)
        {
            if (_stickyExpression == want) return;
            var had = _stickyExpression;
            _stickyExpression = want;
            if (want != null)
            {
                _events++;
                _lastEvent = $"格挡 {block}";
                _exprToSet = want;
                _exprActive = true;
                LastDecision = $"格挡 {block} → 常驻「{want}」";
                BridgeLog.Info($"[avatar] 格挡 {block}，挂上「{want}」");
            }
            else
            {
                _events++;
                _lastEvent = $"格挡 0";
                if (had != null) { _exprToSet = had; _exprActive = false; _exprForce = true; }
                LastDecision = "格挡清零，摘掉锅盖";
                BridgeLog.Info($"[avatar] 格挡清零，摘掉「{had}」");
            }
        }
    }

    /// <summary>敌人掉血（求解器打出去了）。</summary>
    public static void OnEnemyHurt(double damage)
    {
        if (!Enabled) return;
        _events++;
        _lastEvent = $"敌人受伤 -{damage:0}";
        Fire("attack", Math.Clamp(0.5 + damage / 60.0, 0.5, 1.0), $"敌人掉血 -{damage:0}");
    }

    private static void Push(List<(string, float)> values, string id, float value, bool force)
    {
        LastSent.TryGetValue(id, out var last);
        if (!force && !double.IsNaN(last) && Math.Abs(last - value) < 0.02) return;
        LastSent[id] = value;
        values.Add((id, value));
    }

    // ---------------------------------------------------------------- 语音线程

    private static void SpeechLoop()
    {
        while (true)
        {
            // 上一句念完就回收——绝对不能等 Tick() 来做：VTS 没连上时 Tick 第一行就 return，
            // _line 永远清不掉，之后每一句都会在下面被当成「还在说话」丢掉（只出声一次就哑了）。
            var done = _line;
            if (done != null && done.Finished)
            {
                lock (Gate)
                {
                    if (ReferenceEquals(_line, done)) _line = null;
                }
                done.Dispose();
            }

            string? text = null;
            lock (Gate)
            {
                if (_line != null) text = null;           // 上一句还没念完，先不合成
                else
                {
                    lock (SpeechGate)
                    {
                        text = _pendingSpeech;
                        _pendingSpeech = null;
                    }
                }
            }

            if (text == null)
            {
                Thread.Sleep(50);
                continue;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var line = SpeechPlayer.Speak(text, _cfg.Speech);
                sw.Stop();
                BridgeLog.Info($"[avatar] 语音 {line.DurationMs}ms / 合成 {sw.ElapsedMilliseconds}ms / {(line.Audible ? _cfg.Speech.Engine : "静音")}");
                SpeechLine? old;
                lock (Gate)
                {
                    old = _line;
                    _line = line;
                }
                old?.Dispose();
            }
            catch (Exception e)
            {
                BridgeLog.Warn("语音生成失败: " + e.Message);
            }
        }
    }

    // ---------------------------------------------------------------- 调试接口

    /// <summary>
    /// 求解器算牌 → 思考脸（圈圈眼）。没有事件回调，只能读它自己的 IsSearching
    /// （一个静态布尔，读一次等于一次空引用判断）。
    /// </summary>
    private static void UpdateThinking()
    {
        bool nowThinking;
        try { nowThinking = SolverBridge.Searching; }
        catch { nowThinking = false; }

        if (nowThinking == _thinking) return;
        _thinking = nowThinking;
        BridgeLog.Info(nowThinking ? "[avatar] 求解器开始算牌 → 思考脸" : "[avatar] 求解器算完 → 收起思考脸");
    }

    /// <summary>
    /// 启动后把皮套强制摆正（绝对 rotation=0）。
    /// 不记文件、不管上一次歪了多少——直接归零，表情不动。
    /// </summary>
    private static void StraightenOnce()
    {
        if (_modelStraightened || _client == null || !_client.Authenticated) return;
        _modelStraightened = true;
        _client.ResetModelRotation();
        BridgeLog.Info("[avatar] 启动摆正：模型 rotation 归零");
    }

    /// <summary>调试：手动模拟一次受击（/avatar/hit?damage=40）。</summary>
    public static Dictionary<string, object> DebugHit(double damage)
    {
        if (!Enabled) return new() { ["ok"] = false, ["error"] = "皮套联动没启用" };
        lock (Gate)
        {
            Hit.Hit(damage, _cfg.HitDegreesPerDamage, _cfg.HitMaxDegrees);
        }
        return new() { ["ok"] = true, ["damage"] = damage };
    }

    /// <summary>手动触发一个情绪（/avatar/test?emotion=reward）。</summary>
    public static Dictionary<string, object> Test(string emotion)
    {
        if (!Enabled) return new() { ["ok"] = false, ["error"] = "皮套联动没启用（看 vts.json）" };
        if (!_cfg.Emotions.TryGetValue(emotion, out var cue))
            return new() { ["ok"] = false, ["error"] = $"没有这个情绪: {emotion}", ["available"] = new List<string>(_cfg.Emotions.Keys) };

        Apply(cue, 1.0, "manual:" + emotion, emotion);
        return new() { ["ok"] = true, ["decision"] = LastDecision };
    }

    public static Dictionary<string, object> State() => new()
    {
        ["ok"] = true,
        ["enabled"] = Enabled,
        ["authenticated"] = _client?.Authenticated ?? false,
        ["hotkeys"] = _client != null ? new List<string>(_client.Hotkeys.Keys) : new List<string>(),
        ["speaking"] = _line != null,
        ["speechMode"] = _cfg.Speech.Mode,
        ["cue"] = _cueKey,
        ["lastDecision"] = LastDecision,
        ["events"] = _events,
        ["cues"] = _cues,
        ["lastEvent"] = _lastEvent,
        ["recentCommands"] = _client?.RecentCommands() ?? Array.Empty<string>(),
        ["activeExpressions"] = _client?.ActiveExpressions() ?? new List<string>(),
    };

    // ---------------------------------------------------------------- 小工具

    private static int ReadInt(Dictionary<string, object> data, string key)
        => data.TryGetValue(key, out var v) && v is not null ? Convert.ToInt32(v) : 0;

    private static int ReadCount(Dictionary<string, object> data, string key)
        => data.TryGetValue(key, out var v) && v is System.Collections.ICollection c ? c.Count : 0;

    private static string Trim(string text, int max)
        => text.Length <= max ? text : text.Substring(0, max) + "…";
}
