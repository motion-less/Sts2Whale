using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sts2AgentBridge;

/// <summary>
/// Minimal HTTP/1.1 server over TcpListener. Avoids HttpListener, which needs a URL ACL
/// on Windows for anything but the exact "localhost" prefix.
/// </summary>
public static class HttpBridge
{
    public const int Port = 47821;
    private static TcpListener _listener;
    private static readonly CancellationTokenSource Cts = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        BridgeLog.Info($"http bridge listening on http://127.0.0.1:{Port}/");
        _ = Task.Run(AcceptLoop);
    }

    public static void Stop()
    {
        try { Cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
    }

    private static async Task AcceptLoop()
    {
        while (!Cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(Cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { BridgeLog.Error("accept failed", e); continue; }

            _ = Task.Run(() => Handle(client));
        }
    }

    private static async Task Handle(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                var request = await ReadRequest(stream);
                if (request == null) return;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (status, payload) = await Dispatch(request);
                sw.Stop();

                var route = request.Path.Split('?')[0].TrimEnd('/');
                var isConsole = route.StartsWith("/console", StringComparison.Ordinal);
                if (!isConsole)
                    ConsolePage.Record(request.Method, request.Path, request.Body, status, payload, sw.ElapsedMilliseconds);

                var body = Encoding.UTF8.GetBytes(payload);
                var contentType = route == "/console"
                    ? "text/html; charset=utf-8"
                    : "application/json; charset=utf-8";
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\n" +
                    $"Content-Type: {contentType}\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Cache-Control: no-store\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
            catch (Exception e)
            {
                BridgeLog.Error("request failed", e);
            }
        }
    }

    private sealed class Request
    {
        public string Method;
        public string Path;
        public string Body;
    }

    private static async Task<Request> ReadRequest(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length);
            if (read <= 0) return null;
            buffer.Write(chunk, 0, read);
            var text = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (buffer.Length > 1024 * 1024) throw new InvalidOperationException("header too large");
        }

        var headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var contentLength = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line.Substring(15).Trim(), out contentLength);
        }

        var bodyStart = headerEnd + 4;
        var body = new MemoryStream();
        var already = (int)buffer.Length - bodyStart;
        if (already > 0) body.Write(buffer.GetBuffer(), bodyStart, already);

        while (body.Length < contentLength)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length);
            if (read <= 0) break;
            body.Write(chunk, 0, read);
        }

        return new Request
        {
            Method = parts[0].ToUpperInvariant(),
            Path = parts[1],
            Body = Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length),
        };
    }

    private static async Task<(int Status, string Payload)> Dispatch(Request request)
    {
        var path = request.Path.Split('?')[0].TrimEnd('/');
        if (path.Length == 0) path = "/";

        try
        {
            switch (path)
            {
                case "/health":
                    return (200, Json(new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["attached"] = BridgePump.IsAttached,
                        ["mod"] = "Sts2AgentBridge",
                        ["version"] = "0.7.0",
                    }));

                case "/state":
                    {
                        var state = await BridgePump.OnMain(GameApi.State);
                        return (200, Json(state));
                    }

                case "/act":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;
                        var kind = root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                            ? k.GetString()
                            : null;

                        // move 是「发起 + 轮询确认」两段式：它自己要 await 主线程读状态，
                        // 所以不能塞进 OnMain（主线程上 await 主线程任务 = 死锁）。
                        if (kind == "move")
                        {
#if MERGED
                            // {"kind":"move","auto":true}：走「全局战略助手」推荐的下一格（局外决策交给它）。
                            if (root.TryGetProperty("auto", out var autoEl) && autoEl.ValueKind == JsonValueKind.True
                                && StrategistRoute.TryRecommend(out var autoCol, out var autoRow, out var autoWhy))
                            {
                                using var patchedDoc = JsonDocument.Parse(
                                    $"{{\"kind\":\"move\",\"col\":{autoCol},\"row\":{autoRow}}}");
                                var moved = await GameApi.MoveAndConfirm(patchedDoc.RootElement);
                                moved["strategist"] = new Dictionary<string, object>
                                {
                                    ["col"] = autoCol,
                                    ["row"] = autoRow,
                                    ["reason"] = autoWhy,
                                };
                                return (200, Json(moved));
                            }
#endif
                            // 玩家要求：选路线必须先说明理由。有多个岔口才算「选路线」；
                            // 只有一条路时不必念一遍。
                            if (!NarrationGate.Recent() && await HasBranchingChoice())
                                return (200, Json(NarrationGate.Demand("选路线")));

                            return (200, Json(await GameApi.MoveAndConfirm(root)));
                        }

                        // 求解器在打这一场：出牌/结束回合/药水/autoslay 这些手动战斗入口一律关掉。
                        if (kind is "play_card" or "end_turn" or "use_potion" or "discard_potion" or "autoslay"
                            && SolverDriving())
                            return (200, Json(SolverDenied($"/act kind={kind}")));

                        var result = await BridgePump.OnMain(() => GameApi.Act(root));
                        return (200, Json(result));
                    }

                case "/log":
                    {
                        var lines = File.Exists(BridgeLog.Path)
                            ? File.ReadAllLines(BridgeLog.Path)
                            : Array.Empty<string>();
                        var tail = new List<string>();
                        for (var i = Math.Max(0, lines.Length - 100); i < lines.Length; i++) tail.Add(lines[i]);
                        return (200, Json(new Dictionary<string, object> { ["path"] = BridgeLog.Path, ["lines"] = tail }));
                    }

                case "/ui":
                    {
                        if (request.Method == "POST")
                        {
                            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                            var root = doc.RootElement;
                            var which = root.TryGetProperty("which", out var w) ? w.GetString() : "main";
                            var value = !root.TryGetProperty("value", out var v) || v.GetBoolean();
                            var changed = await BridgePump.OnMain(() => AgentUi.Set(which, value));
                            var after = await BridgePump.OnMain(AgentUi.Snapshot);
                            return (200, Json(new Dictionary<string, object> { ["ok"] = changed, ["ui"] = after }));
                        }
                        return (200, Json(await BridgePump.OnMain(AgentUi.Snapshot)));
                    }

                // ---- 调试用：按像素/控件驱动界面。正常流程不要用（走 /request + /respond）----
                case "/click":
                    if (SolverDriving()) return (200, Json(SolverDenied("POST /click")));
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("uid", out var u) && u.ValueKind == JsonValueKind.Number)
                            return (200, Json(await BridgePump.OnMain(() => UiClicks.ClickByUid(u.GetUInt64()))));
                        if (root.TryGetProperty("text", out var t) && t.GetString() is { } wanted)
                            return (200, Json(await BridgePump.OnMain(() => UiClicks.ClickText(wanted))));
                        var idx = root.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                        return (200, Json(await BridgePump.OnMain(() => UiClicks.Click(idx))));
                    }

                case "/click_at":
                    if (SolverDriving()) return (200, Json(SolverDenied("POST /click_at")));
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(request.Body);
                        var x = doc.RootElement.GetProperty("x").GetSingle();
                        var y = doc.RootElement.GetProperty("y").GetSingle();
                        return (200, Json(await BridgePump.OnMain(() => UiClicks.ClickAt(x, y))));
                    }

                case "/map":
                    return (200, Json(await BridgePump.OnMain(DataApi.Map)));
                // 明确的「现在能进哪」：canMove 与 /map 的 available 同源（游戏自己的 MapTravel），
                // cannot 里写清「看着像候选但进不去」的原因，agent 就不用再猜坐标去试了。
                case "/map/next":
                    {
                        var next = await BridgePump.OnMain(MapApi.Next);
#if MERGED
                        try
                        {
                            // 战略助手已经在接管局外（它自己会走图）：桥接不再重复算一遍。
                            if (StrategistBridge.Enabled)
                                next["strategist"] = new Dictionary<string, object> { ["reason"] = "局外由战略助手接管中（它自己推进）" };
                            else if (StrategistRoute.TryRecommend(out var rcol, out var rrow, out var rreason))
                            {
                                next["strategist"] = new Dictionary<string, object>
                                {
                                    ["col"] = rcol,
                                    ["row"] = rrow,
                                    ["reason"] = rreason,
                                    ["movable"] = next.TryGetValue("canMove", out var cm)
                                                  && cm is System.Collections.IEnumerable seq2
                                                  && seq2.Cast<object>().Any(o => o is Dictionary<string, object> c
                                                      && c.TryGetValue("col", out var cc) && cc is int ci && ci == rcol
                                                      && c.TryGetValue("row", out var rr) && rr is int ri && ri == rrow),
                                };
                            }
                            else if (rreason != null)
                            {
                                next["strategist"] = new Dictionary<string, object> { ["reason"] = rreason };
                            }
                        }
                        catch { }
#endif
                        return (200, Json(next));
                    }
                case "/start":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;
                        var character = root.TryGetProperty("character", out var c) ? c.GetString() : null;
                        var seed = root.TryGetProperty("seed", out var s) ? s.GetString() : null;
                        return (200, Json(await BridgePump.OnMain(() => DataApi.Start(character, seed), 15000)));
                    }

                case "/attach/inspect":
                    return (200, Json(await BridgePump.OnMain(RunAttach.Inspect)));

                case "/attach":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        return (200, Json(await BridgePump.OnMain(RunAttach.AttachCurrentRun, 15000)));
                    }

                case "/relic":
                case "/potion":
                    {
                        var query = request.Path.Contains('?') ? request.Path[(request.Path.IndexOf('?') + 1)..] : string.Empty;
                        var pairs = query.Split('&').Select(part => part.Split('=', 2)).Where(p => p.Length == 2).ToList();
                        var id = pairs.Where(p => p[0] == "id").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
                        var isRelic = request.Path.StartsWith("/relic");
                        return (200, Json(await BridgePump.OnMain(() => isRelic ? DataApi.Relic(id) : DataApi.Potion(id))));
                    }

                case "/card":
                    {
                        var query = request.Path.Contains('?') ? request.Path[(request.Path.IndexOf('?') + 1)..] : string.Empty;
                        var pairs = query.Split('&').Select(part => part.Split('=', 2)).Where(p => p.Length == 2).ToList();
                        var id = pairs.Where(p => p[0] == "id").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
                        int? uid = pairs.Where(p => p[0] == "uid").Select(p => int.TryParse(p[1], out var v) ? v : (int?)null).FirstOrDefault();
                        return (200, Json(await BridgePump.OnMain(() => DataApi.Card(id, uid))));
                    }

                // /choices、/choose（按索引选牌）已删除：选牌走 /request + /respond。
                case "/dump":
                    return (200, Json(await BridgePump.OnMain(Choices.Dump)));

                case "/probe":
                    return (200, Json(await BridgePump.OnMain(Choices.Probe)));

                case "/potions":
                    return (200, Json(await BridgePump.OnMain(Potions.List)));

                case "/potion/use":
                    // 用药也是战斗决策：求解器在打时不放行
                    if (SolverDriving()) return (200, Json(SolverDenied("POST /potion/use")));
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;

                        // potion（推荐）= 稳定 key「标题#序号」；uid / index 只作兼容。
                        // 三个都不给就是错（别再默认槽 0 了，那会悄悄喝掉一瓶不该喝的药水）。
                        var used = await Potions.Use(
                            OptionalString(root, "potion"),
                            OptionalInt(root, "uid"),
                            OptionalInt(root, "index"),
                            OptionalInt(root, "target"));
                        return (200, Json(used));
                    }

                case "/potion/discard":
                    if (SolverDriving()) return (200, Json(SolverDenied("POST /potion/discard")));
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;
                        var discarded = await Potions.Discard(
                            OptionalString(root, "potion"),
                            OptionalInt(root, "uid"),
                            OptionalInt(root, "index"));
                        return (200, Json(discarded));
                    }

                case "/acts":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);

                        // 战斗只由内置求解器打：这里没有任何绕过方式（没有 force，也不能靠 /solver 关掉）。
                        if (SolverBridge.Blocking && WantsCombatActions(doc.RootElement))
                            return (200, Json(SolverDenied("POST /acts")));

                        return (200, Json(Batch.Run(doc.RootElement)));
                    }

                case "/solver/dump":
                    return (200, Json(await BridgePump.OnMain(SolverBridge.Dump)));

                // ---- 等「下一条新结论」：一场战斗调一次就够，不用轮询 ----
                case "/solver/wait":
                    {
                        var q = path.IndexOf("timeout=", StringComparison.Ordinal);
                        var timeout = 20.0;
                        if (q >= 0) double.TryParse(path.Substring(q + 8).Split('&')[0], out timeout);
                        return (200, Json(await SolverBridge.Wait(timeout)));
                    }

#if MERGED
#if MERGED
                // ---- 战略助手的决策流水：agent 拿去用人设念给玩家 ----
                case "/strategist/log":
                    {
                        var q = path.IndexOf("since=", StringComparison.Ordinal);
                        long since = 0;
                        if (q >= 0) long.TryParse(path.Substring(q + 6).Split('&')[0], out since);
                        return (200, Json(StrategistLog.Since(since)));
                    }
#endif

                // ---- 全局战略助手：局外接管开关 ----
                case "/strategist":
                    {
                        if (request.Method == "POST")
                        {
                            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                            var on = !doc.RootElement.TryGetProperty("on", out var onEl)
                                     || onEl.ValueKind != JsonValueKind.False;
                            return (200, Json(await BridgePump.OnMain(() => StrategistBridge.Set(on))));
                        }
                        return (200, Json(StrategistBridge.Snapshot()));
                    }
#endif

                // ---- 人设卡：读 mod 目录里的 persona.md ----
                case "/persona":
                    {
                        if (request.Method == "POST") return (200, Json(Persona.Reload()));
                        var q = path.IndexOf("text=0", StringComparison.Ordinal);
                        return (200, Json(Persona.Snapshot(includeText: q < 0)));
                    }

                case "/persona/reload":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        return (200, Json(Persona.Reload()));
                    }

                // ---- 求解器界面显隐（默认藏起来：它是「我」的一部分，不单开窗口）----
                case "/solver/ui":
                    {
                        if (request.Method != "POST") return (200, Json(SolverBridge.Snapshot()));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var hide = !doc.RootElement.TryGetProperty("hide", out var hideEl) || hideEl.ValueKind != JsonValueKind.False;
                        return (200, Json(await BridgePump.OnMain(() => SolverBridge.HideUi(hide))));
                    }

                // ---- 「战斗路线求解器」全自动开关（战斗中把出牌整段交给它）----
                case "/solver":
                    {
                        if (request.Method == "POST")
                        {
                            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                            var on = !doc.RootElement.TryGetProperty("on", out var onEl)
                                     || onEl.ValueKind != JsonValueKind.False;

                            // 关掉求解器 = 打开手动出牌的通道，所以这条被锁死。
                            // 只有启动游戏前设了 STS2_SOLVER_UNLOCK=1（开发/排障用）才允许。
                            if (!on && !UnlockEnabled())
                                return (200, Json(new Dictionary<string, object>
                                {
                                    ["ok"] = false,
                                    ["reason"] = "solver_locked",
                                    ["stopped"] = "求解器是唯一的出牌通道，已锁定：不能从接口关掉它。",
                                    ["hint"] = "要排障就重启游戏并在启动前设 STS2_SOLVER_UNLOCK=1。",
                                    ["solver"] = SolverBridge.Snapshot(),
                                }));

                            return (200, Json(await BridgePump.OnMain(() => SolverBridge.Set(on))));
                        }
                        return (200, Json(SolverBridge.Snapshot()));
                    }

                case "/events":
                    {
                        long since = 0;
                        var q = request.Path.IndexOf("since=", StringComparison.Ordinal);
                        if (q >= 0) long.TryParse(request.Path.Substring(q + 6).Split('&')[0], out since);
                        return (200, Json(EventFeed.Since(since)));
                    }

                case "/console":
                    if (SolverDriving()) return (200, Json(SolverDenied("POST /console")));
                    return (200, ConsolePage.Html);

                case "/console/log":
                    {
                        long since = 0;
                        var q = request.Path.IndexOf("since=", StringComparison.Ordinal);
                        if (q >= 0) long.TryParse(request.Path.Substring(q + 6).Split('&')[0], out since);
                        return (200, ConsolePage.Since(since));
                    }
                case "/whale/say":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var text = doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                        var ms = doc.RootElement.TryGetProperty("ms", out var d) ? d.GetInt32() : 15000;
                        var src = doc.RootElement.TryGetProperty("source", out var sEl) ? sEl.GetString() : "external";
                        return (200, Json(await BridgePump.OnMain(() => WhaleUi.Say(text, ms, source: src ?? "external"))));
                    }

                case "/drag":
                    if (SolverDriving()) return (200, Json(SolverDenied("POST /drag")));
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(request.Body);
                        var r = doc.RootElement;
                        var x1 = r.GetProperty("x1").GetSingle();
                        var y1 = r.GetProperty("y1").GetSingle();
                        var x2 = r.GetProperty("x2").GetSingle();
                        var y2 = r.GetProperty("y2").GetSingle();
                        return (200, Json(await BridgePump.OnMain(() => UiClicks.DragAt(x1, y1, x2, y2))));
                    }

                case "/whale/hide":
                    return (200, Json(await BridgePump.OnMain(WhaleUi.Hide)));

                // 换头像不用重启游戏：重新读 assets/whale.png
                case "/whale/reload":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        return (200, Json(await BridgePump.OnMain(WhaleUi.Reload)));
                    }

                case "/whale/state":
                    return (200, Json(await BridgePump.OnMain(WhaleUi.State)));

                // 皮套联动（VTube Studio）：看状态 / 手动触发一个情绪
                case "/scan":
                    {
                        // 排障：量一下场景扫描到底多贵
                        var times = new List<double>();
                        for (var i = 0; i < 5; i++)
                        {
                            var t0 = (long)Godot.Time.GetTicksUsec();
                            try { ScreenApi.Inspect(); } catch { }
                            times.Add(((long)Godot.Time.GetTicksUsec() - t0) / 1000.0);
                        }
                        times.Sort();
                        return (200, Json(new Dictionary<string, object>
                        {
                            ["ok"] = true,
                            ["medianMs"] = Math.Round(times[times.Count / 2], 2),
                            ["minMs"] = Math.Round(times[0], 2),
                            ["maxMs"] = Math.Round(times[^1], 2),
                        }));
                    }

                case "/memory":
                    return (200, Json(RunMemory.Snapshot()));

                case "/danmaku":
                    return (200, Json(DanmakuBridge.Snapshot()));

                case "/avatar/state":
                    return (200, Json(Sts2Whale.Whale.WhaleAvatar.State()));

                case "/avatar/hit":
                    {
                        var dmg = 10.0;
                        var qi = request.Path.IndexOf("damage=", StringComparison.Ordinal);
                        if (qi >= 0) double.TryParse(request.Path.Substring(qi + 7).Split('&')[0], out dmg);
                        return (200, Json(Sts2Whale.Whale.WhaleAvatar.DebugHit(dmg)));
                    }

                case "/avatar/test":
                    {
                        var emotion = "reward";
                        var qi = request.Path.IndexOf("emotion=", StringComparison.Ordinal);
                        if (qi >= 0) emotion = request.Path.Substring(qi + 8).Split('&')[0];
                        return (200, Json(Sts2Whale.Whale.WhaleAvatar.Test(Uri.UnescapeDataString(emotion))));
                    }

                // 鲸鱼娘的大脑（直连模型 API）：看状态、手动灌一条决定试打
                // 事件卡住时的手动推进（点文字最长的那个按钮）
                case "/event/advance":
                    if (SolverDriving()) return (200, Json(SolverDenied("POST /event/advance")));
                    return (200, Json(await BridgePump.OnMain(UiClicks.ClickLongestText)));

                // 求解器实时世界线进度（debug）
                case "/solver/worldlines":
                    return (200, Json(new Dictionary<string, object>
                    {
                        ["searching"] = SolverBridge.Searching,
                        ["total"] = SolverBridge.WorldlineSnapshot().Total,
                        ["perSecond"] = SolverBridge.WorldlineSnapshot().PerSecond,
                    }));

                // 调试：本局结束后自动开下一把（也可手动 arm 一次）
                case "/loop/next-run":
                    return (200, Json(AutoLoop.Arm()));

                // 调试：强制掉线，验证鲸鱼兜底
                case "/strategist/pause":
                    return (200, Json(await BridgePump.OnMain(StrategistBridge.ForcePause)));

                // 策略助手面板：默认隐藏，想看就 {"hide":false}
                case "/strategist/ui":
                    {
                        var hidePanel = !(request.Method == "POST" && request.Body != null && request.Body.Contains("\"hide\":false"));
                        return (200, Json(await BridgePump.OnMain(() => StrategistBridge.ShowPanel(hidePanel))));
                    }

                case "/whale/brain":
                    return (200, Json(WhaleBrain.Status()));

                // 换 key / 换模型后不用重启游戏
                case "/whale/brain/reload":
                    if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                    return (200, Json(WhaleBrain.Reload()));

                case "/whale/brain/test":
                    {
                        var q = request.Path.IndexOf("text=", StringComparison.Ordinal);
                        var text = q >= 0 ? Uri.UnescapeDataString(request.Path.Substring(q + 5).Split('&')[0]) : "";
                        if (string.IsNullOrWhiteSpace(text)) text = "战略助手选牌「劫掠」：卡牌基础先验，补足前期输出";
                        WhaleBrain.Narrate("card", text, "拿劫掠 前期输出不够");
                        return (200, Json(new { ok = true, queued = text }));
                    }

                case "/screen":
                    return (200, Json(await BridgePump.OnMain(ScreenApi.Inspect)));

                case "/request":
                    {
#if MERGED
                        // 卡牌三选一：先问战略助手（它给建议就由桥接代答，不再打扰鲸鱼）。
                        // 它的建议在卡牌界面 RefreshOptions 之后才算出来，卡面刚弹出那一瞬可能还没有 → 最多等 ~1.5 秒。
                        for (var attempt = 0; ; attempt++)
                        {
                            var snapshot = await BridgePump.OnMain(RequestApi.Current);
                            Dictionary<string, object> pendingDict = null;
                            if (snapshot != null
                                && snapshot.TryGetValue("pending", out var pendObj)
                                && pendObj is Dictionary<string, object> pd)
                                pendingDict = pd;

                            if (pendingDict != null
                                && StrategistDelegate.TryPickCard(pendingDict, out var pickKey, out var reason)
                                && pendingDict.TryGetValue("id", out var idObj)
                                && idObj != null)
                            {
                                var id = Convert.ToInt64(idObj);
                                // pickKey 为空 = 战略助手建议跳过（空 picks）
                                var picks2 = string.IsNullOrEmpty(pickKey) ? Array.Empty<string>() : new[] { pickKey };
                                var answered = await RequestApi.Respond(id, picks2);
                                return (200, Json(new Dictionary<string, object>
                                {
                                    ["ok"] = true,
                                    ["pending"] = null,
                                    ["strategistPick"] = string.IsNullOrEmpty(pickKey) ? "（跳过）" : pickKey,
                                    ["strategistReason"] = reason,
                                    ["note"] = "这一手由全局战略助手决定（桥接代为提交）；接下来 GET /request 拿下一步。",
                                    ["applied"] = answered,
                                }));
                            }

                            if (attempt >= 5) break;
                            if (!await HasCardChoicePending()) break;
                            await Task.Delay(300);
                        }

                        // 营火 / 事件：同样先问战略助手（它的建议也是界面弹出后一拍才出现）。
                        for (var attempt = 0; ; attempt++)
                        {
                            var snapshot = await BridgePump.OnMain(RequestApi.Current);
                            Dictionary<string, object> pendingDict = null;
                            if (snapshot != null
                                && snapshot.TryGetValue("pending", out var pendObj)
                                && pendObj is Dictionary<string, object> pd)
                                pendingDict = pd;

                            if (pendingDict != null
                                && (StrategistDelegate.TryPickRest(pendingDict, out var pickKey, out var reason)
                                    || StrategistDelegate.TryPickEvent(pendingDict, out pickKey, out reason))
                                && pendingDict.TryGetValue("id", out var idObj)
                                && idObj != null)
                            {
                                var id = Convert.ToInt64(idObj);
                                var answered = await RequestApi.Respond(id, new[] { pickKey });
                                return (200, Json(new Dictionary<string, object>
                                {
                                    ["ok"] = true,
                                    ["pending"] = null,
                                    ["strategistPick"] = pickKey,
                                    ["strategistReason"] = reason,
                                    ["note"] = "这手由全局战略助手决定（桥接代为提交）。",
                                    ["applied"] = answered,
                                }));
                            }

                            if (attempt >= 5) break;
                            var anyPending = await BridgePump.OnMain(RequestApi.Current);
                            var stillSame = anyPending != null
                                && anyPending.TryGetValue("pending", out var pp)
                                && pp is Dictionary<string, object> pdd
                                && (pdd.TryGetValue("kind", out var kk) && (kk as string == "rest" || kk as string == "event"));
                            if (!stillSame) break;
                            await Task.Delay(300);
                        }

                        // 商店：战略助手要买什么就代买（一次 /request 最多买 1 件，每店最多 3 件，剩下交给鲸鱼）。
                        {
                            var snapshot = await BridgePump.OnMain(RequestApi.Current);
                            Dictionary<string, object> pendingDict = null;
                            if (snapshot != null
                                && snapshot.TryGetValue("pending", out var pendObj)
                                && pendObj is Dictionary<string, object> pd)
                                pendingDict = pd;

                            var isShop = pendingDict != null
                                && pendingDict.TryGetValue("kind", out var kk) && kk as string == "shop";
                            if (!isShop) _shopBuys = 0;

                            if (isShop && _shopBuys < 3
                                && StrategistDelegate.TryPickShop(pendingDict, out var pickKey, out var reason)
                                && pendingDict.TryGetValue("id", out var idObj) && idObj != null)
                            {
                                var id = Convert.ToInt64(idObj);
                                _shopBuys++;
                                var answered = await RequestApi.Respond(id, new[] { pickKey });
                                return (200, Json(new Dictionary<string, object>
                                {
                                    ["ok"] = true,
                                    ["pending"] = null,
                                    ["strategistPick"] = pickKey,
                                    ["strategistReason"] = reason,
                                    ["shopBuys"] = _shopBuys,
                                    ["note"] = "这件是战略助手挑的（桥接代买）；再 GET /request 继续，或直接 /click 前进离开商店。",
                                    ["applied"] = answered,
                                }));
                            }
                        }

                        // 战略助手在「接管模式」时，请求不让 agent 抢；它卡住（waitingForManual）才交回。
                        if (StrategistBridge.Enabled && !StrategistBridge.WaitingForManual)
                            return (200, Json(new Dictionary<string, object>
                            {
                                ["ok"] = true,
                                ["pending"] = null,
                                ["note"] = "局外交给战略助手了；它处理不了的界面会自动交回（waitingForManual=true）。",
                                ["strategist"] = StrategistBridge.Snapshot(),
                            }));
#endif
                        return (200, Json(await BridgePump.OnMain(RequestApi.Current)));
                    }

                case "/respond":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                            return (400, Json(new { ok = false, error = "id required（先 GET /request 拿 id）" }));
                        if (!root.TryGetProperty("picks", out var picksEl) || picksEl.ValueKind != JsonValueKind.Array)
                            return (400, Json(new { ok = false, error = "picks required（字符串数组，例如 [\"打击#2\"]）" }));

                        var rid = idEl.GetInt64();
                        var picks = new List<string>();
                        foreach (var el in picksEl.EnumerateArray())
                            if (el.ValueKind == JsonValueKind.String) picks.Add(el.GetString());

                        // 玩家要求：选牌必须先说明理由。跳过一次选择（picks 为空）不算选。
                        if (picks.Count > 0 && !NarrationGate.Recent())
                        {
                            var snapshot = await BridgePump.OnMain(RequestApi.Current);
                            if (WantsReasonForPending(snapshot))
                                return (200, Json(NarrationGate.Demand("选牌")));
                        }

                        return (200, Json(await RequestApi.Respond(rid, picks.ToArray())));
                    }

                // ---- 调试用：按控件 uid 点一下。正常流程不要用（走 /request + /respond）----
                case "/tap":
                    if (SolverDriving()) return (200, Json(SolverDenied("POST /tap")));
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        if (!doc.RootElement.TryGetProperty("uid", out var u) || u.ValueKind != JsonValueKind.Number)
                            return (400, Json(new { ok = false, error = "uid required" }));
                        var uid = u.GetUInt64();
                        return (200, Json(await BridgePump.OnMain(() => ScreenApi.Tap(uid))));
                    }

                case "/proceed":
                    return (200, Json(await BridgePump.OnMain(ScreenApi.Proceed)));

                // ---- 存档并退出 / 重新读档（重开本局）----
                // 都是「游戏自己的入口」：SaveManager.SaveRun + NGame.ReturnToMainMenu，
                // 以及主菜单的「继续游戏」按钮。它们自己要 await 主线程任务，所以不走 OnMain。
                // 存盘 + 退出游戏（一条命令，不用点按钮）
                case "/quit":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        var keep = !(request.Path.Contains("save=0") || (request.Body ?? "").Contains("\"save\":false"));
                        return (200, Json(await RunControl.QuitApp(keep)));
                    }

                case "/save-quit":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        return (200, Json(await RunControl.SaveQuit()));
                    }

                case "/reload":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        return (200, Json(await RunControl.Reload()));
                    }

                // ---- 调试用：按 Godot 键码发一次按键。正常流程不要用 ----
                case "/key":
                    if (SolverDriving()) return (200, Json(SolverDenied("POST /key")));
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var key = doc.RootElement.TryGetProperty("code", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : 69;
                        return (200, Json(await BridgePump.OnMain(() => ScreenApi.PressKey(key))));
                    }

                default:
                    return (404, Json(new { ok = false, error = $"no route {path}" }));
            }
        }
        catch (Exception e)
        {
            BridgeLog.Error($"dispatch {path} failed", e);
            return (500, Json(new { ok = false, error = e.GetType().Name + ": " + e.Message }));
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);

    /// <summary>排障用的逃生开关：只有游戏启动前设了 STS2_SOLVER_UNLOCK=1 才允许关掉求解器。</summary>
    private static bool UnlockEnabled()
    {
        var v = System.Environment.GetEnvironmentVariable("STS2_SOLVER_UNLOCK");
        return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前 pending 是不是「卡牌三选一」（战略助手的建议会晚一拍出现）。</summary>
    private static async Task<bool> HasCardChoicePending()
    {
        try
        {
            var snapshot = await BridgePump.OnMain(RequestApi.Current);
            if (snapshot == null || !snapshot.TryGetValue("pending", out var p) || p is not Dictionary<string, object> pending) return false;
            var kind = pending.TryGetValue("kind", out var k) ? k as string : null;
            var stage = pending.TryGetValue("stage", out var st) ? st as string : null;
            return kind == "reward" && stage == "card-choice";
        }
        catch { return false; }
    }

    /// <summary>求解器正在打这一场：手动战斗入口全部关闭（move / 走路不在此列）。</summary>
    private static bool SolverDriving() => SolverBridge.Blocking;

    private static Dictionary<string, object> SolverDenied(string what)
    {
        return new Dictionary<string, object>
        {
            ["ok"] = false,
            ["reason"] = "solver_full_auto",
            ["stopped"] = $"{what} 被拒：这一场由内置求解器在打，手动战斗入口已关闭",
            ["hint"] = "战斗只由内置求解器打；接口层没有任何手动出牌通道。等战斗结束再走 /request。",
            ["solver"] = SolverBridge.Snapshot(),
        };
    }

    /// <summary>本次进店已经代买了几件（换界面就清零）。</summary>
    private static int _shopBuys;

    /// <summary>当前地图是不是有多个可走的岔口（有 = 这是一次「选路线」）。</summary>
    private static async Task<bool> HasBranchingChoice()
    {
        try
        {
            var next = await BridgePump.OnMain(MapApi.Next);
            if (next == null) return false;
            if (!next.TryGetValue("canMove", out var cm) || cm is not List<object> list) return false;
            return list.Count > 1;
        }
        catch { return false; }
    }

    /// <summary>这次待答请求算不算「选牌」：卡牌奖励三选一、商店买东西、以及各种选牌族。</summary>
    private static bool WantsReasonForPending(Dictionary<string, object> snapshot)
    {
        if (snapshot == null) return false;
        if (!snapshot.TryGetValue("pending", out var pending) || pending is not Dictionary<string, object> p) return false;

        var kind = p.TryGetValue("kind", out var k) ? k as string : null;
        var stage = p.TryGetValue("stage", out var st) ? st as string : null;

        switch (kind)
        {
            case "reward": return stage == "card-choice";
            case "shop": return stage == "items";
            case "simple":
            case "upgrade":
            case "remove":
            case "transform":
            case "exhaust":
            case "discard":
            case "enchant":
                return true;
            default:
                return false;
        }
    }

    /// <summary>这批动作里有没有「出牌/结束回合/药水」这类会跟求解器抢队列的动作。</summary>
    private static bool WantsCombatActions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array) return false;
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object) continue;
            if (!action.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String) continue;
            switch (kindEl.GetString())
            {
                case "play_card":
                case "end_turn":
                case "use_potion":
                case "discard_potion":
                    return true;
            }
        }
        return false;
    }

    /// <summary>可选整数字段：没给 / null / 不是整数，一律当"没给"（返回 null）。</summary>
    private static int? OptionalInt(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Number) return null;
        return el.TryGetInt32(out var value) ? value : null;
    }

    /// <summary>可选字符串字段：空白字符串也算"没给"。</summary>
    private static string OptionalString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var text = el.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}


