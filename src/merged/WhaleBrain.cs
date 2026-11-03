using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using NetHttpClient = System.Net.Http.HttpClient;
using NetHttpRequest = System.Net.Http.HttpRequestMessage;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpContent = System.Net.Http.StringContent;

namespace Sts2AgentBridge;

/// <summary>
/// 「鲸鱼娘」的嘴：把战略助手做出来的决定，直接发给大模型 API，拿回一句人话，打进气泡。
///
/// 为什么要它：以前是让 DSH 的宿主插件把决定塞进某个会话、叫醒模型再说一句——链路长、
/// 而且插件少写一个 role 就能把整个会话毒死。改成模型 API 直连后，游戏进程自己就能说话，
/// 不需要会话、不需要插件、不占 agent 名额。
///
/// 配置查找顺序（先命中先用）：
///   &lt;mod 目录&gt;/llm.json   {"enabled":true,"baseUrl":"…","model":"…","apiKey":"…"}
///
/// API key **只认 llm.json 这一处**：不读环境变量、不读 ~/.dsh 的凭据文件。
/// 没有 key 就自动退回原来那套模板短句，不会哑火。
/// </summary>
public static class WhaleBrain
{
    private const string DefaultBaseUrl = "https://api.deepseek.com";
    private const string DefaultModel = "deepseek-flash";
    private const int DefaultMinGapMs = 10_000;  // 两次模型调用之间至少隔 10 秒（llm.json 的 minGapMs 可调）
    private const int MinAllowedGapMs = 2_000;
    private const int MaxAllowedGapMs = 600_000;
    private const int MaxLineChars = 42;        // 气泡最多显示这么多字
    private const int DefaultTimeoutSeconds = 15;
    private const int DefaultMaxTokens = 300;   // 推理类模型会把 token 先花在 thinking 上，给足余量

    private static readonly NetHttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private static readonly SemaphoreSlim Signal = new(0);
    private static readonly object Gate = new();
    private static readonly List<string> History = new();   // 最近说过的话，给模型接话用

    private static bool _loaded;
    private static string _baseUrl = DefaultBaseUrl;
    private static string _model = DefaultModel;
    private static string _apiKey = "";
    private static string _systemPrompt = "";
    private static string RunMemoryDigest = "";
    private static bool _enabled = true;
    private static bool _workerStarted;
    private static long _lastCallAt;
    private static int _timeoutSeconds = DefaultTimeoutSeconds;
    private static int _maxTokens = DefaultMaxTokens;
    private static int _minGapMs = DefaultMinGapMs;

    private static string _pendingKind;
    private static string _pendingText;
    private static string _pendingFallback;
    private static string _pendingFacts;              // 牌面（费用/类型/效果），只给选牌决策带
    private static Action<string>? _pendingCallback;   // 弹幕：真说出话之后把这一轮记进上下文
    private static string _lastShown;

    public static bool Enabled
    {
        get { EnsureLoaded(); return _enabled && !string.IsNullOrEmpty(_apiKey); }
    }

    public static string ModDir => ModPaths.ModDir;

    /// <summary>战略助手决定了一条 → 让模型用鲸鱼的口吻说一句。</summary>
    /// <param name="fallback">模型不可用/超时时说的模板句，保证气泡不空。</param>
    /// <param name="facts">
    /// 这一条决定涉及的硬事实（目前是选牌的一行牌面：费用 · 类型 · 稀有度 · 效果）。
    /// 模型看不见牌面，不给事实就会按牌名猜费用 —— 角色卡又要求带数字，于是经常报错价。
    /// </param>
    public static void Narrate(string kind, string text, string fallback, string? facts = null)
    {
        EnsureLoaded();
        if (!_enabled || string.IsNullOrEmpty(_apiKey))
        {
            DeferredSay(fallback, 9000);
            return;
        }

        lock (Gate)
        {
            if (text == _pendingText) return;   // 同一条决定别排两次
            _pendingKind = kind;
            _pendingText = text;
            _pendingFallback = fallback;
            _pendingFacts = facts;
            _pendingCallback = null;            // 新决定顶掉了还没答的弹幕，那条不该再记进上下文
        }
        StartWorker();
        TrySignal();
    }

    /// <summary>
    /// 直播弹幕：让鲸鱼娘回一句（不回也行，模型不可用时直接跳过）。
    /// </summary>
    /// <param name="onSpoken">
    /// 真把这句话打到气泡之后回调，参数是实际说的那句。DanmakuBridge 用它记「谁问了什么、她怎么答的」；
    /// 模型超时/没回，或被后一条弹幕顶掉 → 不回调，上下文里就不会留下没答过的问题。
    /// </param>
    public static void ReplyToDanmaku(string user, string text, Action<string>? onSpoken = null)
    {
        EnsureLoaded();
        if (!_enabled || string.IsNullOrEmpty(_apiKey)) return;

        lock (Gate)
        {
            _pendingKind = "danmaku";
            _pendingText = $"{user}：{text}";
            _pendingFallback = "";
            _pendingFacts = null;                // 弹幕不继承上一条选牌的牌面事实
            _pendingCallback = onSpoken;
        }
        StartWorker();
        TrySignal();
    }

    /// <summary>给 /whale/state 之类的地方看当前状态，方便排查。</summary>
    public static Dictionary<string, object> Status()
    {
        EnsureLoaded();
        return new Dictionary<string, object>
        {
        ["ok"] = true,
        ["enabled"] = _enabled,
        ["hasKey"] = !string.IsNullOrEmpty(_apiKey),
        ["model"] = _model,
        ["baseUrl"] = _baseUrl,
        ["timeoutSeconds"] = _timeoutSeconds,
        ["maxTokens"] = _maxTokens,
        ["last"] = _lastShown ?? "",
        };
    }

    /// <summary>重读 llm.json / 环境变量 / 凭证库——换 key、换模型不用重启游戏。</summary>
    public static Dictionary<string, object> Reload()
    {
        lock (Gate) { _loaded = false; }
        EnsureLoaded();
        return Status();
    }

    private static void TrySignal()
    {
        try { if (Signal.CurrentCount == 0) Signal.Release(); } catch { }
    }

    private static void StartWorker()
    {
        if (_workerStarted) return;
        lock (Gate)
        {
            if (_workerStarted) return;
            _workerStarted = true;
        }
        Task.Run(WorkerLoop);
    }

    private static async Task WorkerLoop()
    {
        while (true)
        {
            try
            {
                await Signal.WaitAsync().ConfigureAwait(false);

                // 速率闸：两次模型调用至少隔 _minGapMs。
                // 等待期间**不去动**接收槽——这 10 秒里战略助手又做了新决定，就把它顶掉，
                // 只发最新的一条（旧的那句已经过时了，发了也是噪音）。
                var since = System.Environment.TickCount64 - _lastCallAt;
                if (since < _minGapMs) await Task.Delay((int)(_minGapMs - since)).ConfigureAwait(false);

                string kind, text, fallback, facts;
                Action<string>? callback;
                lock (Gate)
                {
                    kind = _pendingKind;
                    text = _pendingText;
                    fallback = _pendingFallback;
                    facts = _pendingFacts;
                    callback = _pendingCallback;
                    _pendingText = null;
                    _pendingFacts = null;
                    _pendingCallback = null;
                }
                if (string.IsNullOrEmpty(text)) continue;

                // 记账放在请求**之前**：超时/报错也算一次，免得模型挂了之后疯狂重试
                _lastCallAt = System.Environment.TickCount64;

                string line = null;
                try { line = await AskAsync(kind, text, facts).ConfigureAwait(false); }
                catch (Exception e) { BridgeLog.Warn("whale brain: " + e.Message); }

                if (string.IsNullOrWhiteSpace(line))
                {
                    if (!string.IsNullOrEmpty(fallback)) DeferredSay(fallback, 9000);
                    continue;
                }

                _lastShown = line;
                lock (History)
                {
                    History.Add(line);
                    if (History.Count > 4) History.RemoveAt(0);   // 只留 4 句，够接上话就行
                }

                // 只要一句：模型就算写了多句/多行，也只取第一句（用户要求：一次一句或不说）
                var single = FirstSentence(line);
                if (!string.IsNullOrEmpty(single))
                {
                    DeferredSay(single, 12000);

                    // 弹幕：说出口之后才记这一轮，给下一轮当上下文。
                    if (callback is not null)
                    {
                        try { callback(single); }
                        catch (Exception e) { BridgeLog.Warn("whale brain callback: " + e.Message); }
                    }
                }
            }
            catch (Exception e)
            {
                try { BridgeLog.Warn("whale brain loop: " + e.Message); } catch { }
            }
        }
    }

    private static async Task<string> AskAsync(string kind, string text, string? facts)
    {
        var user = new StringBuilder();

        // 本局事实：角色/层数/血量/金币 + 最近几条决定（约 150 token）
        try
        {
            RunMemoryDigest = Sts2AgentBridge.RunMemory.Digest();
            if (!string.IsNullOrEmpty(RunMemoryDigest)) user.Append(RunMemoryDigest);
        }
        catch { }

        // 弹幕回复也带「最近」：RunMemory.Digest 正常情况下已经包含【最近】；
        // 不在局内/刚开局或记忆还没热时它可能是空的，这里用最近事件补一段，保证弹幕上下文里一定有。
        if (kind == "danmaku" && !RunMemoryDigest.Contains("【最近】", StringComparison.Ordinal))
        {
            try
            {
                var recent = Sts2AgentBridge.RunMemory.RecentEventsText();
                if (!string.IsNullOrEmpty(recent)) user.Append(recent);
            }
            catch { }
        }

        // 上下文按场景只带一份，别叠着发：
        //   弹幕 → 最近三轮问答（谁问的、问什么、她怎么答的），塞在同一条 user 里；
        //          还没有问答时退回「你刚说过」，让她先接得上自己的话。
        //   其他 → 最近说过的话，免得重复。
        var chatContext = kind == "danmaku" ? DanmakuContext() : "";
        if (chatContext.Length > 0)
        {
            user.Append(chatContext);
        }
        else
        {
            lock (History)
            {
                if (History.Count > 0) user.Append("你刚说过：").Append(string.Join(" / ", History)).Append('\n');
            }
        }
        if (!string.IsNullOrEmpty(RunMemoryDigest) || chatContext.Length > 0)
            BridgeLog.Info($"[brain] 上下文：记忆 {RunMemoryDigest.Length} 字 / 弹幕 {chatContext.Length} 字 / {kind}");

        // 牌面事实紧挨着决定放：模型看不见牌，费用只能靠我们给。
        // 角色卡要求「理由里带数字」，没有事实它就会编一个 —— 这里给真值，正文里也带一遍。
        if (!string.IsNullOrWhiteSpace(facts))
        {
            user.Append(facts).Append('\n');
            BridgeLog.Info($"[brain] 牌面事实：{facts}");
        }

        // 这里**只放事实和上下文**：说什么、说多长、什么口吻，全部由角色卡决定。
        // 代码不掺和风格，免得角色卡写一套、代码又盖一套。
        if (kind == "danmaku")
            user.Append("【直播间观众弹幕】").Append(text).Append('\n');
        else
            user.Append("【战略脑的决定】").Append(kind ?? "decision").Append("：").Append(text).Append('\n');

        var payload = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = new object[]
            {
                new Dictionary<string, object> { ["role"] = "system", ["content"] = _systemPrompt },
                new Dictionary<string, object> { ["role"] = "user", ["content"] = user.ToString() },
            },
            ["max_tokens"] = _maxTokens,
            ["temperature"] = 0.9,
            ["stream"] = false,
        };

        using var request = new NetHttpRequest(NetHttpMethod.Post, _baseUrl.TrimEnd('/') + "/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
        request.Content = new NetHttpContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
        using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Short(body, 160)}");

        using var doc = JsonDocument.Parse(body);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var content = choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var cc) ? cc.GetString() : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "?";
            throw new InvalidOperationException(
                $"模型没给正文（finish_reason={finish}）。推理类模型（如 deepseek-flash）会把 token 先花在思考上，" +
                $"把 llm.json 的 maxTokens 调大（现在 {_maxTokens}）。");
        }
        return Cleanup(content);
    }

    /// <summary>弹幕上下文由 DanmakuBridge 维护（最近三轮问答）；那边出任何问题都不该拖垮这次回复。</summary>
    private static string DanmakuContext()
    {
        try { return DanmakuBridge.RecentContext() ?? ""; }
        catch (Exception e) { BridgeLog.Warn("danmaku context: " + e.Message); return ""; }
    }

    private static string Cleanup(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var text = line.Trim();
        text = text.Trim('"', '“', '”', '「', '」', '\'', '‘', '’').Trim();
        text = text.Replace("\r", " ").Replace("\n", " ");
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return text.Length <= MaxLineChars ? text : text.Substring(0, MaxLineChars);
    }

    private static string Short(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n);

    /// <summary>气泡必须在主线程碰节点：从工作线程 deferred 回主线程。</summary>
    /// <summary>
    /// 规范化模型输出：去空白、去包裹的引号/符号。**不截断**内容。
    /// 用户要求「一次只回一句或者不回」，不做分句播报。
    /// </summary>
    private static string FirstSentence(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Replace("\r", "").Trim();
        text = text.TrimStart('-', '·', '*', ' ');
        text = text.Trim('"', '「', '」', '\'', '“', '”');
        // 不再截断：气泡按 20 字一行折行显示（见 WhaleUi.CharsPerLine）
        return text;
    }

    private static void DeferredSay(string line, int ms)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            Callable.From(() =>
            {
                try { WhaleUi.Say(line, ms, countsAsReason: false, source: "brain"); }
                catch (Exception e) { BridgeLog.Warn("whale say failed: " + e.Message); }
            }).CallDeferred();
        }
        catch (Exception e)
        {
            BridgeLog.Warn("whale deferred say failed: " + e.Message);
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            _enabled = true;
            _baseUrl = DefaultBaseUrl;
            _model = DefaultModel;
            _apiKey = "";
            _timeoutSeconds = DefaultTimeoutSeconds;
            _maxTokens = DefaultMaxTokens;
            _minGapMs = DefaultMinGapMs;

            try
            {
                var file = ModPaths.Data("llm.json");
                if (File.Exists(file))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.False) _enabled = false;
                    if (root.TryGetProperty("baseUrl", out var b) && b.ValueKind == JsonValueKind.String) _baseUrl = b.GetString()!;
                    if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) _model = m.GetString()!;
                    if (root.TryGetProperty("apiKey", out var k) && k.ValueKind == JsonValueKind.String) _apiKey = k.GetString()!;
                    if (root.TryGetProperty("timeout", out var to) && to.ValueKind == JsonValueKind.Number && to.TryGetInt32(out var tv) && tv > 0)
                        _timeoutSeconds = tv;
                    if (root.TryGetProperty("maxTokens", out var mt) && mt.ValueKind == JsonValueKind.Number && mt.TryGetInt32(out var mtv) && mtv > 0)
                        _maxTokens = mtv;
                    if (root.TryGetProperty("minGapMs", out var gp) && gp.ValueKind == JsonValueKind.Number && gp.TryGetInt32(out var gpv) && gpv > 0)
                        _minGapMs = Math.Clamp(gpv, MinAllowedGapMs, MaxAllowedGapMs);
                }
            }
            catch (Exception e) { BridgeLog.Warn("llm.json: " + e.Message); }

            _baseUrl = FirstNonEmpty(System.Environment.GetEnvironmentVariable("STS2_LLM_BASE"), _baseUrl, DefaultBaseUrl);
            _model = FirstNonEmpty(System.Environment.GetEnvironmentVariable("STS2_LLM_MODEL"), _model, DefaultModel);
            // key 只认 llm.json：不读环境变量，也不读 ~/.dsh/.credentials.yaml

            _systemPrompt = LoadPersona();
            BridgeLog.Info($"whale brain: enabled={_enabled} model={_model} key={(string.IsNullOrEmpty(_apiKey) ? "missing" : "ok")} persona={_systemPrompt.Length} chars");
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "";
    }

    private static string LoadPersona()
    {
        try
        {
            var candidates = new[]
            {
                ModPaths.Data("persona.md"),                          // 玩家自己改的那份
                ModPaths.Script("persona.md"),                        // 随包模板
                Path.Combine(ModDir, "..", "persona.md"),             // 老布局兜底
            };
            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path).Trim();
                if (text.Length > 0)
                    return text + "\n\n——以上是你是谁。别复述规则。";
            }
        }
        catch { }

        // 连人设文件都没有时的兜底：只说"你是谁"，规矩仍然不写在代码里
        return "你是《杀戮尖塔2》里跟着玩家爬塔的鲸鱼娘，用第一人称说话。";
    }

}
