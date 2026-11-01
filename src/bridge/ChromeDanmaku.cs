using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Sts2AgentBridge;

/// <summary>
/// 用无头 Chrome（或 Edge）读弹幕页，走 DevTools Protocol。
///
/// 为什么不用 Safari + AppleScript：那个要求玩家手动勾「允许 Apple 事件中的 JavaScript」，
/// 而且只有 macOS 能用。CDP 是纯 HTTP + WebSocket：
///   · 不需要任何浏览器设置
///   · mod 自己拉起一个独立的无头实例，玩家不用先开浏览器
///   · Windows / macOS / Linux 都能用（Windows 上 Edge 几乎必装）
///
/// 流程：找浏览器 → 选空闲端口 → 起无头实例打开弹幕页 → 连 CDP → Runtime.evaluate 读 DOM → 差分。
/// </summary>
internal static class ChromeDanmaku
{
    public static string LastPageUrl { get; private set; } = "";
    public static string LastError { get; private set; } = "";

    /// <summary>
    /// 固定调试端口。必须是固定的：浏览器是玩家自己点按钮开的，mod 重启后要能重新连上同一个实例。
    /// </summary>
    public const int DebugPort = 48790;

    /// <summary>在页面里跑的 JS：取最后 30 条弹幕节点的「用户名 + 最后一行文本」。</summary>
    private const string Js =
        "var NL=String.fromCharCode(10);" +
        "JSON.stringify({url:location.href,items:Array.from(document.querySelectorAll('.event.event--message')).slice(-30)" +
        ".map(function(e){var l=e.innerText.split(NL).map(function(s){return s.trim()}).filter(Boolean);" +
        "return {user:(l.length>1?l[0]:''),text:(l.length?l[l.length-1]:'')}})})";

    private static readonly object Gate = new();
    private static readonly HttpClientHolder Http = new();

    private static ClientWebSocket? _ws;
    private static int _msgId;
    private static string _targetUrl = "";

    private static List<(string user, string text)> _prev = new();
    private static bool _seeded;

    public static void Reset()
    {
        lock (Gate)
        {
            _prev = new List<(string, string)>();
            _seeded = false;
            LastPageUrl = "";
        }
    }

    /// <summary>只断开 CDP 连接 —— 浏览器是玩家自己开的，不替人家关。</summary>
    public static void Shutdown()
    {
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }

    /// <summary>拉一次：返回新增的弹幕（首次只建基线，返回空）。阻塞调用，放后台线程。</summary>
    public static List<(string user, string text)> Poll(long roomId)
    {
        lock (Gate)
        {
            var added = new List<(string, string)>();
            if (roomId <= 0) { LastError = "没填房间号"; return added; }

            var pageUrl = ObsPage(roomId);
            try
            {
                if (!EnsureAttached(pageUrl))
                    return added;   // EnsureAttached 已经把原因写进 LastError

                var raw = Evaluate(Js, 15000);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    LastError = "浏览器没返回内容（页面可能还在加载）";
                    _seeded = false;
                    return added;
                }

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                LastPageUrl = root.TryGetProperty("url", out var u) ? u.GetString() ?? pageUrl : pageUrl;

                var cur = new List<(string, string)>();
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in items.EnumerateArray())
                    {
                        var user = it.TryGetProperty("user", out var usr) ? usr.GetString() ?? "" : "";
                        var text = it.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(text)) cur.Add((user, text));
                    }
                }
                if (cur.Count > 30) cur = cur.GetRange(cur.Count - 30, 30);

                LastError = "";

                if (!_seeded)
                {
                    _prev = cur;
                    _seeded = true;
                    return added;
                }

                var addedItems = cur;
                for (var shift = 0; shift <= _prev.Count; shift++)
                {
                    var tail = _prev.Skip(shift).ToList();
                    var head = cur.Take(_prev.Count - shift).ToList();
                    if (tail.SequenceEqual(head))
                    {
                        addedItems = cur.Skip(_prev.Count - shift).ToList();
                        break;
                    }
                }

                _prev = cur;
                return addedItems;
            }
            catch (Exception e)
            {
                LastError = $"{e.GetType().Name}: {e.Message}";
                _seeded = false;
                try { _ws?.Dispose(); } catch { }
                _ws = null;
                return added;
            }
        }
    }

    private static string ObsPage(long roomId)
    {
        var url = DanmakuBridge.ObsUrl(roomId);
        var q = url.IndexOf('?');
        return q > 0 ? url.Substring(0, q) : url;
    }

    // ------------------------------------------------------------------ 浏览器生命周期

    /// <summary>
    /// 连上**玩家已经打开**的浏览器（设置页「打开弹幕页」按钮负责开）。
    /// 这里不会自己拉起进程 —— 玩家没开就报错，让他去点按钮。
    /// </summary>
    private static bool EnsureAttached(string pageUrl)
    {
        if (_ws != null && _ws.State == WebSocketState.Open && _targetUrl == pageUrl) return true;

        try { _ws?.Dispose(); } catch { }
        _ws = null;

        var wsUrl = TryGetPageWebSocket(DebugPort, pageUrl);
        if (wsUrl == null)
        {
            LastError = PortAlive(DebugPort)
                ? "浏览器开着，但里面没有弹幕页 —— 点设置页的「打开弹幕页」再开一次"
                : $"还没打开弹幕页：点设置页的「打开弹幕页」按钮（需要 Chrome 或 Edge；调试端口 {DebugPort}）";
            return false;
        }

        try
        {
            var ws = new ClientWebSocket();
            ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).Wait(5000);
            _ws = ws;
            _msgId = 0;
            _targetUrl = pageUrl;
            BridgeLog.Info($"[danmaku] 已连上浏览器（CDP {DebugPort}）");
            return true;
        }
        catch (Exception e)
        {
            LastError = "连不上浏览器调试端口: " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// 由设置页按钮调用：用带调试端口的专用 profile 打开弹幕页。
    /// 玩家已有实例开着就复用（直接返回成功）。
    /// </summary>
    public static Dictionary<string, object> OpenPage(long roomId)
    {
        if (roomId <= 0) return new() { ["ok"] = false, ["error"] = "先填房间号" };

        var pageUrl = DanmakuBridge.ObsUrl(roomId);
        var baseUrl = ObsPage(roomId);

        lock (Gate)
        {
            // 已经开着就用现成的
            if (TryGetPageWebSocket(DebugPort, baseUrl) != null)
            {
                try { _ws?.Dispose(); } catch { }
                _ws = null;
                _targetUrl = "";
                return new() { ["ok"] = true, ["reused"] = true, ["url"] = pageUrl };
            }

            // 浏览器已经在跑、只是没有这个标签页 → 让它自己开一个，**不要**再起进程
            // （两个实例抢同一个 profile 会变成「一个占端口、一个占锁」的分脑状态）
            if (PortAlive(DebugPort))
            {
                if (NewTab(DebugPort, pageUrl))
                {
                    try { _ws?.Dispose(); } catch { }
                    _ws = null;
                    _targetUrl = "";
                    BridgeLog.Info("[danmaku] 已在运行中的浏览器里新开弹幕页");
                    return new() { ["ok"] = true, ["reused"] = false, ["url"] = pageUrl };
                }
                return new() { ["ok"] = false, ["error"] = "浏览器开着，但打不开新标签页" };
            }

            var exe = FindBrowser();
            if (exe == null)
                return new() { ["ok"] = false, ["error"] = "没找到 Chrome / Edge / Chromium" };

            var profileDir = Path.Combine(ModPaths.DataDir, "browser");
            try { Directory.CreateDirectory(profileDir); } catch { }

            // 上次被强杀会留下 Singleton* 锁，Chrome 会直接 abort（实测过）
            foreach (var name in new[] { "SingletonLock", "SingletonSocket", "SingletonCookie" })
            {
                try { File.Delete(Path.Combine(profileDir, name)); } catch { }
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = false,      // 这次要有窗口，玩家要看得见
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            foreach (var a in new[]
            {
                "--no-first-run",
                "--no-default-browser-check",
                $"--remote-debugging-port={DebugPort}",
                $"--user-data-dir={profileDir}",
                pageUrl,
            })
                psi.ArgumentList.Add(a);

            try
            {
                Process.Start(psi);
                BridgeLog.Info($"[danmaku] 已请求浏览器打开弹幕页：{pageUrl}");
                return new() { ["ok"] = true, ["reused"] = false, ["url"] = pageUrl };
            }
            catch (Exception e)
            {
                return new() { ["ok"] = false, ["error"] = "启动浏览器失败: " + e.Message };
            }
        }
    }

    /// <summary>调试端口上有没有活着一个浏览器。</summary>
    private static bool PortAlive(int port) => Http.GetString($"http://127.0.0.1:{port}/json/version", 2000) != null;

    /// <summary>让已运行的浏览器新开一个标签页（Chrome 111+ 要求 PUT）。</summary>
    private static bool NewTab(int port, string url)
    {
        var res = Http.Put($"http://127.0.0.1:{port}/json/new?{Uri.EscapeDataString(url)}", 8000);
        return res != null;
    }

    /// <summary>从 /json/list 里挑出弹幕页那个 target 的 WebSocket 地址。</summary>
    private static string? TryGetPageWebSocket(int port, string pageUrl)
    {
        try
        {
            var json = Http.GetString($"http://127.0.0.1:{port}/json/list", 3000);
            if (json == null) return null;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            string? fallback = null;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (!t.TryGetProperty("type", out var type) || type.GetString() != "page") continue;
                if (!t.TryGetProperty("webSocketDebuggerUrl", out var ws)) continue;
                var url = t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var wsUrl = ws.GetString() ?? "";

                if (url.Contains("/obs/", StringComparison.Ordinal)) return wsUrl;
                if (url.Contains("laplace", StringComparison.OrdinalIgnoreCase)) return wsUrl;
                fallback ??= wsUrl;
            }
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ CDP

    /// <summary>发一条 Runtime.evaluate，等同一 id 的回包，返回字符串结果。</summary>
    private static string Evaluate(string expression, int timeoutMs)
    {
        var ws = _ws ?? throw new InvalidOperationException("CDP 未连接");
        var id = ++_msgId;
        var payload = JsonSerializer.Serialize(new
        {
            id,
            method = "Runtime.evaluate",
            @params = new { expression, returnByValue = true, awaitPromise = false },
        });

        var bytes = Encoding.UTF8.GetBytes(payload);
        ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
          .Wait(timeoutMs);

        var deadline = System.Environment.TickCount64 + timeoutMs;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (System.Environment.TickCount64 < deadline)
        {
            buffer.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                var receive = ws.ReceiveAsync(new ArraySegment<byte>(chunk), CancellationToken.None);
                if (!receive.Wait(Math.Max(1000, timeoutMs - (int)(System.Environment.TickCount64 - (deadline - timeoutMs)))))
                    return "";
                result = receive.Result;
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("CDP 连接被关闭");
                buffer.Write(chunk, 0, result.Count);
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(buffer.ToArray());
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("id", out var rid) || rid.GetInt32() != id) continue;

            if (!doc.RootElement.TryGetProperty("result", out var res)) return "";
            if (!res.TryGetProperty("result", out var inner)) return "";
            return inner.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
        }
        return "";
    }

    // ------------------------------------------------------------------ 找浏览器 / 找端口

    private static string? FindBrowser()
    {
        var candidates = new List<string>();

        if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
            candidates.Add("/Applications/Brave Browser.app/Contents/MacOS/Brave Browser");
        }
        else if (OperatingSystem.IsWindows())
        {
            var pf = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
            var pf86 = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
            var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            foreach (var root in new[] { pf, pf86, local })
            {
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(Path.Combine(root, @"Google\Chrome\Application\chrome.exe"));
                candidates.Add(Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe"));
            }
        }
        else
        {
            candidates.AddRange(new[] { "google-chrome", "chromium", "chromium-browser", "microsoft-edge" });
        }

        foreach (var c in candidates)
        {
            try
            {
                if (Path.IsPathRooted(c)) { if (File.Exists(c)) return c; }
                else
                {
                    var found = Which(c);
                    if (found != null) return found;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? Which(string name)
    {
        try
        {
            foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }
        catch { }
        return null;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>极简 HTTP GET（只用一次 /json/list，不值得拖 HttpClient 进来）。</summary>
    private sealed class HttpClientHolder
    {
        private readonly System.Net.Http.HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(5) };

        public string? Put(string url, int timeoutMs)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Put, url);
                using var resp = _client.SendAsync(req, cts.Token).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return null;
                return resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        public string? GetString(string url, int timeoutMs)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                return _client.GetStringAsync(url, cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }
    }
}
