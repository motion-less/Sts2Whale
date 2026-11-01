using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sts2Whale.Whale;

/// <summary>
/// VTube Studio 的最小客户端：鉴权、热键、参数注入。
///
/// 只依赖 BCL（<see cref="ClientWebSocket"/>），不碰 Godot，可以在任意线程跑，
/// 也方便单独拿出去做单元测试。生命周期由 <see cref="WhaleAvatar"/> 管。
/// </summary>
public sealed class VtsClient : IDisposable
{
    private readonly string _url;
    private readonly string _tokenPath;
    private readonly string _pluginName;
    private readonly string _pluginDeveloper;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly StringBuilder _rx = new();
    private readonly Dictionary<string, string> _hotkeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _expressions = new(StringComparer.OrdinalIgnoreCase); // 名字 -> 文件名
    private readonly Dictionary<string, bool> _baselineActive = new(StringComparer.OrdinalIgnoreCase); // 连上时哪些表情本来开着
    private bool _expressionsReady;
    private readonly object _hotkeyGate = new();

    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly StringBuilder _json = new(256);
    private readonly object _jsonGate = new();
    private string? _token;          // 缓存，避免每次发指令都读盘
    private string _authFragment = ""; // 预拼的鉴权片段（plugin 名 + token）

    private ClientWebSocket? _ws;
    private Thread? _thread;
    private volatile bool _authed;
    private long _requestId;

    private readonly Queue<string> _recent = new();
    private long _lastParamLogAt;

    /// <summary>已发出去的请求数（诊断用）。</summary>
    public long Sent { get; private set; }

    /// <summary>最近发出的指令（给人看的一行行文本）。</summary>
    public string[] RecentCommands()
    {
        lock (_recent) return _recent.ToArray();
    }

    private void Trace(string line)
    {
        lock (_recent)
        {
            _recent.Enqueue(line);
            while (_recent.Count > 20) _recent.Dequeue();
        }
        Log?.Invoke("→ " + line);
    }

    public VtsClient(string url, string tokenPath, string pluginName, string pluginDeveloper)
    {
        _url = url;
        _tokenPath = tokenPath;
        _pluginName = pluginName;
        _pluginDeveloper = pluginDeveloper;
    }

    /// <summary>日志出口，由外层接到 BridgeLog。</summary>
    public event Action<string>? Log;

    public bool Authenticated => _authed;

    public IReadOnlyDictionary<string, string> Hotkeys
    {
        get { lock (_hotkeyGate) return new Dictionary<string, string>(_hotkeys, StringComparer.OrdinalIgnoreCase); }
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "vts-avatar" };
        _thread.Start();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _sendLock.Dispose();
        _cts.Dispose();
    }

    // ---------------------------------------------------------------- 连接循环

    private void RunLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                RunOnce().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                if (!_cts.IsCancellationRequested) Log?.Invoke("VTS 连接异常: " + e.Message);
            }

            _authed = false;
            if (_cts.IsCancellationRequested) return;
            Thread.Sleep(3000); // VTS 没开时别空转
        }
    }

    private async Task RunOnce()
    {
        using var ws = new ClientWebSocket();
        _ws = ws;
        await ws.ConnectAsync(new Uri(_url), _cts.Token).ConfigureAwait(false);
        Log?.Invoke("VTS 已连接 " + _url);

        SendAuth();

        var buffer = new byte[16 * 1024];
        while (!_cts.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) break;

            _rx.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            foreach (var message in Drain(_rx))
                Handle(message);
        }
    }

    /// <summary>把缓冲区里完整的 JSON 对象切出来（VTS 的消息都是一个对象一帧）。</summary>
    private static IEnumerable<string> Drain(StringBuilder sb)
    {
        var messages = new List<string>();
        var start = -1;
        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = 0; i < sb.Length; i++)
        {
            var c = sb[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    messages.Add(sb.ToString(start, i - start + 1));
                    start = -1;
                }
            }
        }

        if (messages.Count > 0)
        {
            // 只保留没解析完的尾巴
            var lastEnd = sb.ToString().LastIndexOf('}');
            if (lastEnd >= 0 && lastEnd < sb.Length - 1) sb.Remove(0, lastEnd + 1);
            else sb.Clear();
        }

        return messages;
    }

    // ---------------------------------------------------------------- 收发

    private void Handle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("messageType", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "AuthenticationTokenResponse":
                    if (root.TryGetProperty("data", out var td) &&
                        td.TryGetProperty("authenticationToken", out var tokenEl))
                    {
                        var token = tokenEl.GetString();
                        if (!string.IsNullOrEmpty(token))
                        {
                            SaveToken(token!);
                            SendAuth(token);
                        }
                    }
                    break;

                case "AuthenticationResponse":
                    if (root.TryGetProperty("data", out var ad) &&
                        ad.TryGetProperty("authenticated", out var ok) && ok.ValueKind == JsonValueKind.True)
                    {
                        _authed = true;
                        Log?.Invoke("VTS 鉴权成功");
                        RequestHotkeys();
                        RequestExpressions();
                    }
                    else
                    {
                        _authed = false;
                        var reason = root.TryGetProperty("data", out var rd) &&
                                     rd.TryGetProperty("reason", out var r) ? r.GetString() : "?";
                        Log?.Invoke($"VTS 鉴权失败（{reason}）");
                        ResetToken();
                        RequestNewToken();
                    }
                    break;

                case "HotkeysInCurrentModelResponse":
                    if (root.TryGetProperty("data", out var hd) &&
                        hd.TryGetProperty("availableHotkeys", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        lock (_hotkeyGate)
                        {
                            _hotkeys.Clear();
                            foreach (var item in list.EnumerateArray())
                            {
                                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                                var id = item.TryGetProperty("hotkeyID", out var h) ? h.GetString() : null;
                                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id)) _hotkeys[name!] = id!;
                            }
                        }
                        var model = hd.TryGetProperty("modelName", out var mn) ? mn.GetString() : null;
                        Log?.Invoke($"VTS 模型 {model} 热键 {Hotkeys.Count} 个");
                    }
                    break;

                case "ExpressionStateResponse":
                    if (root.TryGetProperty("data", out var exd) &&
                        exd.TryGetProperty("expressions", out var exList) && exList.ValueKind == JsonValueKind.Array)
                    {
                        lock (_hotkeyGate)
                        {
                            _expressions.Clear();
                            _baselineActive.Clear();
                            _active.Clear();
                            foreach (var item in exList.EnumerateArray())
                            {
                                var name = item.TryGetProperty("name", out var en) ? en.GetString() : null;
                                var file = item.TryGetProperty("file", out var ef) ? ef.GetString() : null;
                                var active = item.TryGetProperty("active", out var ea) && ea.ValueKind == JsonValueKind.True;
                                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(file)) continue;
                                _expressions[name!] = file!;
                                _baselineActive[name!] = active;
                                if (active) _active.Add(name!);
                            }
                        }
                        Log?.Invoke($"VTS 表情 {Expressions.Count} 个");
                        // 每次刷新都通知：模型加载完 VTS 会重套保存的表情，需要再复位一次
                        _expressionsReady = true;
                        ExpressionsReady?.Invoke();
                    }
                    break;

                case "EventReceived":
                    {
                        var ev = root.TryGetProperty("data", out var evd) && evd.TryGetProperty("eventName", out var en)
                            ? en.GetString() : null;
                        if (ev == "ModelLoadedEvent")
                        {
                            Log?.Invoke("VTS 模型已加载 → 重新拉取表情（会再复位一次）");
                            RequestExpressions();
                        }
                        else if (ev == "ModelConfigChangedEvent" || ev == "BackgroundChangedEvent")
                        {
                            // 无关，忽略
                        }
                    }
                    break;

                case "APIError":
                    var errId = root.TryGetProperty("data", out var ed) &&
                                ed.TryGetProperty("errorID", out var eid) ? eid.GetInt32() : 0;
                    if (errId == 51) break; // 授权窗还开着，等用户点
                    var msg = root.TryGetProperty("data", out var ed2) &&
                              ed2.TryGetProperty("message", out var m) ? m.GetString() : "?";
                    Log?.Invoke($"VTS API 错误: {msg}");
                    break;
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("VTS 消息解析失败: " + e.Message);
        }
    }

    private void SendRaw(string json)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;

        try
        {
            _sendLock.Wait(200);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token)
                  .GetAwaiter().GetResult();
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("VTS 发送失败: " + e.Message);
        }
    }

    private string Envelope(string messageType, string dataJson)
        => $"{{\"apiName\":\"VTubeStudioPublicAPI\",\"apiVersion\":\"1.0\",\"messageType\":\"{messageType}\"," +
           $"\"requestID\":\"sts2whale-{Interlocked.Increment(ref _requestId)}\",\"data\":{dataJson}}}";

    private string AuthData(string? token = null)
    {
        var tokenPart = token == null ? "" : $",\"authenticationToken\":\"{token}\"";
        return $"{{\"pluginName\":\"{_pluginName}\",\"pluginDeveloper\":\"{_pluginDeveloper}\"{tokenPart}}}";
    }

    // ---------------------------------------------------------------- 对外 API

    public void SendAuth(string? token = null)
    {
        if (token != null)
        {
            Trace("鉴权（用刚拿到的 token）");
            SendRaw(Envelope("AuthenticationRequest", AuthData(token)));
            return;
        }

        var saved = LoadToken();
        if (!string.IsNullOrEmpty(saved))
        {
            Trace("复用已保存的 token 鉴权");
            SendRaw(Envelope("AuthenticationRequest", AuthData(saved)));
            return;
        }

        RequestNewToken();
    }

    private long _lastTokenRequestAt;

    /// <summary>申请新 token（VTS 会弹授权窗）—— 限流，别把弹窗刷屏。</summary>
    private void RequestNewToken()
    {
        var now = Environment.TickCount64;
        if (now - _lastTokenRequestAt < 30000)
        {
            Trace("刚申请过 token，30 秒内不重复弹窗");
            return;
        }
        _lastTokenRequestAt = now;
        Trace("申请 token（AuthenticationTokenRequest，VTS 会弹窗）");
        SendRaw(Envelope("AuthenticationTokenRequest", AuthData()));
    }

    /// <summary>表情列表就绪（可以在这之后套用启动表情）。</summary>
    public event Action? ExpressionsReady;

    /// <summary>VTS 报告「当前正挂着」的表情名字（连上时 + 我们自己改过之后）。</summary>
    public List<string> ActiveExpressions()
    {
        lock (_hotkeyGate) return new List<string>(_active);
    }

    public IReadOnlyDictionary<string, string> Expressions
    {
        get { lock (_hotkeyGate) return new Dictionary<string, string>(_expressions, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>发一条带鉴权的请求：<paramref name="body"/> 是 auth 字段之后的部分（含闭合的 }）。</summary>
    private void SendWithAuth(string messageType, string body)
        => SendRaw(Envelope(messageType, AuthFragment() + body));

    public void RequestExpressions()
    {
        Trace("拉取表情列表（ExpressionStateRequest）");
        SendWithAuth("ExpressionStateRequest", ",\"details\":true}");
    }

    public void RequestHotkeys()
    {
        Trace("拉取热键列表（HotkeysInCurrentModelRequest）");
        SendWithAuth("HotkeysInCurrentModelRequest", "}");
    }

    public void TriggerHotkey(string name)
    {
        if (!_authed)
        {
            Trace($"跳过热键「{name}」：还没鉴权");
            return;
        }

        string? id;
        lock (_hotkeyGate) _hotkeys.TryGetValue(name, out id);
        if (id == null)
        {
            string names;
            lock (_hotkeyGate) names = string.Join("/", _hotkeys.Keys);
            Trace($"跳过热键「{name}」：当前模型没有这个名字（有：{names}）");
            return;
        }

        Trace($"热键「{name}」id={id}");
        SendWithAuth("HotkeyTriggerRequest", $",\"hotkeyID\":\"{id}\"}}");
    }

    /// <summary>把模型旋转角度强制归零（绝对模式），用于启动时摆正。</summary>
    public void ResetModelRotation()
    {
        if (!_authed) return;
        Trace("启动摆正（rotation=0）");
        SendWithAuth("MoveModelRequest", ",\"timeInSeconds\":0.2,\"valuesAreRelativeToModel\":false,\"rotation\":0}");
    }

    /// <summary>把模型设成绝对角度（度）。相对模式会累积动画误差，绝对模式每帧精确。</summary>
    public void SetModelRotation(double degrees, double seconds = 0.05)
    {
        if (!_authed) return;
        SendWithAuth("MoveModelRequest",
            $",\"timeInSeconds\":{seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}," +
            $"\"valuesAreRelativeToModel\":false," +
            $"\"rotation\":{degrees.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}}}");
    }

    /// <summary>开/关一个 exp3 表情（比热键可靠：热键是 toggle，这里是显式开关）。</summary>
    public void ActivateExpression(string name, bool active, bool force = false)
    {
        if (!_authed)
        {
            Trace($"跳过表情「{name}」：还没鉴权");
            return;
        }

        // 收尾时回「连上时的状态」，别把模型默认开着的表情（比如脸红/屑屑眼）关掉。
        // 状态型表情（格挡的钢盆）必须能强制关掉，不受基线影响。
        if (!active && !force)
        {
            bool wasActive;
            lock (_hotkeyGate) _baselineActive.TryGetValue(name, out wasActive);
            if (wasActive) active = true;
        }

        // VTS 要的是文件名（xxx.exp3.json），不是显示名；先按名字查，查不到就补后缀
        string file;
        lock (_hotkeyGate) _expressions.TryGetValue(name, out file!);
        if (string.IsNullOrEmpty(file))
            file = name.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase) ? name : name + ".exp3.json";

        Trace($"表情「{name}」{(active ? "开" : "关")} → {file}");
        lock (_hotkeyGate)
        {
            if (active) _active.Add(name); else _active.Remove(name);
        }
        SendWithAuth("ExpressionActivationRequest",
            $",\"expressionFile\":\"{file}\",\"active\":{(active ? "true" : "false")}}}");
    }

    /// <summary>一次注入一批参数（同帧的参数合并成一条消息）。</summary>
    public void InjectParameters(IReadOnlyList<(string Id, float Value)> values, bool release = false)
    {
        if (!_authed || values.Count == 0) return;

        var now = Environment.TickCount64;
        if (now - _lastParamLogAt > 400)  // 口型 30Hz，全打会淹掉日志
        {
            _lastParamLogAt = now;
            Trace((release ? "释放参数 " : "参数 ") + string.Join(" ", values.Select(v => $"{v.Id}={v.Value:0.00}")));
        }

        Sent++;
        SendWithAuth("InjectParameterDataRequest",
            ",\"faceFound\":true,\"mode\":\"set\",\"parameterValues\":[" + BuildParameterJson(values, release) + "]}");
    }

    /// <summary>参数列表拼 JSON（复用 StringBuilder，避免 30Hz 反复分配）。</summary>
    private string BuildParameterJson(IReadOnlyList<(string Id, float Value)> values, bool release)
    {
        lock (_jsonGate)
        {
            _json.Clear();
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0) _json.Append(',');
                _json.Append("{\"id\":\"").Append(values[i].Id).Append("\",\"weight\":")
                     .Append(release ? "0" : "1").Append(",\"value\":")
                     .Append(values[i].Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                     .Append('}');
            }
            return _json.ToString();
        }
    }

    // ---------------------------------------------------------------- token

    private string? LoadToken()
    {
        if (_token != null) return _token;
        try
        {
            if (!File.Exists(_tokenPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_tokenPath));
            _token = doc.RootElement.TryGetProperty("authenticationToken", out var t) ? t.GetString() : null;
            return _token;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>预拼鉴权片段，30Hz 注入时不再每次拼接 plugin/token。</summary>
    private string AuthFragment()
    {
        var token = LoadToken() ?? "";
        var fragment = $"{{\"pluginName\":\"{_pluginName}\",\"pluginDeveloper\":\"{_pluginDeveloper}\"," +
                       $"\"authenticationToken\":\"{token}\"";
        if (_authFragment != fragment) _authFragment = fragment;
        return _authFragment;
    }

    private void ResetToken()
    {
        _token = null;
        _authFragment = "";
        try { if (File.Exists(_tokenPath)) File.Delete(_tokenPath); } catch { }
    }

    private void SaveToken(string token)
    {
        try
        {
            _token = token;
            _authFragment = "";
            File.WriteAllText(_tokenPath, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["authenticationToken"] = token,
            }));
            Log?.Invoke("VTS token 已保存到 " + Path.GetFileName(_tokenPath));
        }
        catch (Exception e)
        {
            Log?.Invoke("VTS token 保存失败: " + e.Message);
        }
    }
}
