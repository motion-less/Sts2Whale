using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sts2AgentBridge;

/// <summary>
/// 直播间弹幕 → 鲸鱼娘互动。
///
/// 链路：无头 Chrome 里的 Laplace 弹幕页（<see cref="ChromeDanmaku"/> 走 DevTools Protocol 读）
/// → 过滤/限流 → WhaleBrain 生成一句 → WhaleUi.Say（字幕 + 语音）。
///
/// **不依赖 Python**：以前这活是 tools/danmaku_daemon.py 干的，要多一个进程、一个 HTTP 端口、
/// 还要探测 python 路径。现在直接在本进程里调 osascript。
///
/// 只保留网页这条路：B 站弹幕接口实测匿名连接收不到任何 DANMU_MSG，不值得维护。
///
/// 原则：不保证每条都回，最多 N 条/分钟；优先回提问和带关键词的。
/// </summary>
public static class DanmakuBridge
{
    /// <summary>
    /// 弹幕页模板（Laplace 的 OBS 页面）。{0} 是直播间号。
    /// 设置页把它生成给玩家复制，抓取时无头浏览器打开的就是这一页。
    /// </summary>
    public const string ObsUrlTemplate =
        "https://chat.laplace.live/obs/{0}" +
        "?baseFontSize=20&colorScheme=light&connectionMode=direct" +
        "&limitEventAmount=50&limitStickyAmount=10&loginSyncToken=" +
        "&showAutoDanmaku=false&showAvatar=true&showAvatarFrame=true&showCurrentRank=true" +
        "&showDanmaku=true&showEnterEvent=false&showEnterEventCurrentGuardOnly=false" +
        "&showFollowEvent=false&showGift=true&showGiftEffect=false&showGiftEffectAbove=99" +
        "&showGiftFree=false&showGiftHighlightAbove=29.99&showGiftPriceAbove=1" +
        "&showGiftStickyAbove=29.99&showGuardBadge=true&showLottery=true&showMedal=false" +
        "&showMedalLightenedOnly=true&showModBadge=true&showNotice=false" +
        "&showPhoneNotVerified=false&showRedEnvelop=true&showSpoiler=true&showStickyBar=true" +
        "&showSuperChat=true&showSystemMessage=true&showToast=true&showUsername=true" +
        "&showWealthMedal=false&useCst=false";

    /// <summary>按房间号生成完整弹幕页链接。</summary>
    public static string ObsUrl(long roomId) => string.Format(ObsUrlTemplate, roomId);

    /// <summary>链接的「协议+域名+路径」部分（查询串可能被浏览器重排，匹配标签页时用它）。</summary>
    public static string ObsUrlBase(long roomId)
    {
        var url = ObsUrl(roomId);
        var q = url.IndexOf('?');
        return q > 0 ? url.Substring(0, q) : url;
    }

    /// <summary>两次拉取之间的间隔。osascript 一次 0.3~0.6 秒，1.5 秒足够及时又不烧 CPU。</summary>
    private const int PollMs = 1500;

    private static readonly object Gate = new();
    private static readonly Queue<long> ReplyTimes = new();
    private static readonly Dictionary<string, long> RepliedTexts = new(StringComparer.Ordinal);

    private static Thread? _thread;
    private static bool _enabled;
    private static bool _running;
    private static bool _exitHooked;
    private static long _roomId;
    private static int _maxPerMinute = 3;
    private static int _minChars = 2;
    private static int _maxChars = 60;

    private static int _seq;
    private static int _received;
    private static int _sent;
    private static string _lastMsg = "";
    private static string _lastReply = "";
    private static string _lastError = "";
    private static long _lastPollAt;

    public static bool Enabled => _enabled && _running;

    // ------------------------------------------------------------------ 生命周期

    public static void Start(string dir)
    {
        try
        {
            var cfgPath = System.IO.Path.Combine(dir, "live.json");
            if (!System.IO.File.Exists(cfgPath))
            {
                BridgeLog.Info("[danmaku] 没有 live.json，弹幕互动关闭");
                return;
            }

            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(cfgPath));
            var root = doc.RootElement;

            _enabled = !root.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
            if (!_enabled) { BridgeLog.Info("[danmaku] live.json enabled=false"); return; }

            _roomId = ReadLong(root, "roomId", 0);
            _maxPerMinute = Math.Max(1, ReadInt(root, "maxPerMinute", 3));
            _minChars = ReadInt(root, "minChars", 2);
            _maxChars = ReadInt(root, "maxChars", 60);

            if (_roomId <= 0) BridgeLog.Warn("[danmaku] 没配房间号，先不抓（设置页填了会自动开）");

            ChromeDanmaku.Reset();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "whale-danmaku" };
            _thread.Start();
            HookExit();

            BridgeLog.Info($"[danmaku] 已开启（无头浏览器 + CDP）：房间 {_roomId}，限 {_maxPerMinute} 条/分钟");
        }
        catch (Exception e)
        {
            _running = false;
            _lastError = e.GetType().Name + ": " + e.Message;
            BridgeLog.Warn("[danmaku] 启动失败: " + e.Message);
        }
    }

    public static void Stop()
    {
        _running = false;
        ChromeDanmaku.Shutdown();
    }

    private static void HookExit()
    {
        if (_exitHooked) return;
        _exitHooked = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    /// <summary>
    /// 运行时把弹幕模块拉起来。新装时 live.json 是 enabled=false，玩家在设置页填完房间号、
    /// 点了「打开弹幕页」还得重启才能生效 —— 这里让它当场就能开始工作。
    /// </summary>
    public static void EnsureRunning()
    {
        _enabled = true;
        if (_running) return;

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "whale-danmaku" };
        _thread.Start();
        HookExit();
        BridgeLog.Info("[danmaku] 运行中被开启（不用重启游戏）");
    }

    /// <summary>换直播间：清基线、换目标页，不用重启任何进程。</summary>
    public static Dictionary<string, object> Retarget(long roomId)
    {
        if (roomId <= 0) return new() { ["ok"] = false, ["error"] = "房间号无效" };
        EnsureRunning();
        if (_running && roomId == _roomId) return new() { ["ok"] = true, ["changed"] = false, ["roomId"] = _roomId };

        _roomId = roomId;
        _received = 0;
        _sent = 0;
        _lastError = "";
        ChromeDanmaku.Reset();

        if (!_running)
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "whale-danmaku" };
            _thread.Start();
            HookExit();
        }

        BridgeLog.Info($"[danmaku] 已切到房间 {roomId}（不用重启游戏）");
        return new() { ["ok"] = true, ["changed"] = true, ["roomId"] = _roomId };
    }

    // 设置页是边打字边写盘的，防抖一下：停下 800ms 再真正切换
    private static long _pendingRoom;
    private static long _retargetAt;
    private static int _retargetScheduled;

    /// <summary>设置页改完房间号调这个：防抖 800ms 后切换。</summary>
    public static void RetargetDelayed(long roomId)
    {
        if (roomId <= 0) return;
        Interlocked.Exchange(ref _pendingRoom, roomId);
        Interlocked.Exchange(ref _retargetAt, System.Environment.TickCount64 + 800);
        if (Interlocked.Exchange(ref _retargetScheduled, 1) == 1) return;

        Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    var wait = Interlocked.Read(ref _retargetAt) - System.Environment.TickCount64;
                    if (wait > 0) { Thread.Sleep((int)wait); continue; }
                    var target = Interlocked.Read(ref _pendingRoom);
                    Interlocked.Exchange(ref _retargetScheduled, 0);
                    Retarget(target);
                    return;
                }
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _retargetScheduled, 0);
                BridgeLog.Warn("[danmaku] 切换房间失败: " + e.Message);
            }
        });
    }

    // ------------------------------------------------------------------ 轮询

    private static void Loop()
    {
        while (_running)
        {
            if (_roomId <= 0)
            {
                Thread.Sleep(PollMs);
                continue;
            }

            try
            {
                var added = ChromeDanmaku.Poll(_roomId);
                _lastPollAt = System.Environment.TickCount64;
                _lastError = ChromeDanmaku.LastError;

                if (added.Count > 0)
                {
                    var batch = new List<(int seq, string user, string text)>();
                    foreach (var (user, text) in added)
                    {
                        _received++;
                        _lastMsg = $"{user}: {text}";
                        batch.Add((++_seq, user, text));
                    }
                    PickAndReply(batch);
                }
            }
            catch (Exception e)
            {
                _lastError = e.GetType().Name + ": " + e.Message;
            }

            Thread.Sleep(PollMs);
        }
    }

    // ------------------------------------------------------------------ 挑选与回复

    private static void PickAndReply(List<(int seq, string user, string text)> batch)
    {
        if (!CanReply()) return;

        (int score, int seq, string user, string text) best = (-1, 0, "", "");
        foreach (var (seq, user, text) in batch)
        {
            var clean = text.Trim();
            if (clean.Length < _minChars || clean.Length > _maxChars) continue;

            // 纯表情/符号、重复刷屏不回
            if (clean.All(c => char.IsPunctuation(c) || char.IsSymbol(c) || char.IsWhiteSpace(c))) continue;
            lock (RepliedTexts)
            {
                if (RepliedTexts.TryGetValue(clean, out var lastAt) &&
                    System.Environment.TickCount64 - lastAt < 5 * 60_000) continue;
            }

            var score = Score(clean);
            if (score > best.score) best = (score, seq, user, clean);
        }

        if (best.score < 0) return;

        lock (RepliedTexts)
        {
            RepliedTexts[best.text] = System.Environment.TickCount64;
            if (RepliedTexts.Count > 200)
            {
                var cutoff = System.Environment.TickCount64 - 10 * 60_000;
                foreach (var key in RepliedTexts.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                    RepliedTexts.Remove(key);
            }
        }

        lock (Gate) ReplyTimes.Enqueue(System.Environment.TickCount64);
        _sent++;
        _lastReply = $"{best.user}: {best.text}";
        BridgeLog.Info($"[danmaku] 回 @{best.user}：{best.text}");
        WhaleBrain.ReplyToDanmaku(best.user, best.text);
    }

    private static bool CanReply()
    {
        var now = System.Environment.TickCount64;
        lock (Gate)
        {
            while (ReplyTimes.Count > 0 && now - ReplyTimes.Peek() > 60_000) ReplyTimes.Dequeue();
            return ReplyTimes.Count < _maxPerMinute;
        }
    }

    /// <summary>提问/带关键词的优先回复，纯粹「哈哈哈」放后面。</summary>
    private static int Score(string text)
    {
        var score = 0;
        if (text.Contains('?') || text.Contains('？')) score += 5;
        foreach (var kw in new[] { "鲸鱼", "爬塔", "主播", "你好", "在吗", "为什么", "怎么", "能不能", "加油", "厉害", "多少" })
            if (text.Contains(kw)) { score += 3; break; }
        if (text.Length is >= 4 and <= 24) score += 1;
        return score;
    }

    // ------------------------------------------------------------------ 状态

    public static Dictionary<string, object> Snapshot()
    {
        var now = System.Environment.TickCount64;
        var repliesLastMinute = ReplyTimes.Count(t => now - t <= 60_000);
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["enabled"] = Enabled,
            ["roomId"] = _roomId,
            ["source"] = "chrome (cdp)",
            ["connected"] = _lastPollAt > 0 && now - _lastPollAt < 5000,
            ["pageUrl"] = ChromeDanmaku.LastPageUrl,
            ["received"] = _received,
            ["replied"] = _sent,
            ["repliesLastMinute"] = repliesLastMinute,
            ["maxPerMinute"] = _maxPerMinute,
            ["lastMessage"] = _lastMsg,
            ["lastReply"] = _lastReply,
            ["lastError"] = _lastError,
        };
    }

    // ------------------------------------------------------------------ 配置读取

    /// <summary>live.json 里的数值可能是数字，也可能是设置页写成的字符串，两种都认。</summary>
    private static long ReadLong(JsonElement root, string name, long fallback)
    {
        if (!root.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : fallback,
            JsonValueKind.String => long.TryParse((v.GetString() ?? "").Trim(), out var s) ? s : fallback,
            _ => fallback,
        };
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
        => (int)Math.Clamp(ReadLong(root, name, fallback), int.MinValue, int.MaxValue);
}
