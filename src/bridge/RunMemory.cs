using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Utils.Persistence;

namespace Sts2AgentBridge;

/// <summary>
/// 本局记忆：给鲸鱼娘一份「这一局发生了什么」的短摘要。
///
/// 三层：
///   1. 事实快照（角色/层数/血量/金币/卡组/遗物）—— 每 2 秒读一次游戏状态，免费
///   2. 事件流水（最近若干条决定）—— 由 StrategistLog / 战斗结果写入
///   3. 本局回顾（换层时生成的 2~3 句）—— 目前用模板拼，不额外调模型
///
/// 落盘走 RitsuLib 的 <see cref="ModDataStore"/>（全局作用域，崩档/重开能续），
/// 另记一份 run_history.jsonl 全量日志留在 mod 目录里方便人看。
/// 所有字段都是短文本，拼进提示词约 150 token。
/// </summary>
public static class RunMemory
{
    private const string ModId = "Sts2Whale";
    private const string StoreKey = "run_memory";
    private const int MaxEvents = 40;
    private const int DigestEvents = 6;

    private static readonly object Gate = new();
    private static readonly List<Entry> Events = new();

    private static string _dir = "";
    private static string _legacyFile = "";   // 0.7 及以前的 run_memory.json（只读，用来做一次性迁移）
    private static string _historyFile = "";
    private static ModDataStore _store;

    private static string _runKey = "";
    private static string _character = "";
    private static int _ascension;
    private static int _act;
    private static int _actFloor;
    private static int _totalFloor;
    private static string _room = "";
    private static int _hp, _maxHp, _gold, _deck, _relics;
    private static string _recap = "";
    private static long _lastTickAt;
    private static long _lastSaveAt;
    private static bool _loaded;
    private static long _lastLiveAt;   // 最近一次真的读到对局状态的时间
    private static int _lastActFloor;  // 上一幕的层数，用来检测换幕

    private sealed class Entry
    {
        public long At { get; set; }
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public static void Init(string dir)
    {
        _dir = dir;
        _legacyFile = Path.Combine(dir, "run_memory.json");
        _historyFile = Path.Combine(dir, "run_history.jsonl");

        try
        {
            _store = ModDataStore.For(ModId);
            _store.Register<State>(StoreKey, "run_memory.json", SaveScope.Global,
                                   defaultFactory: () => new State(), autoCreateIfMissing: true);
            BridgeLog.Info("[memory] 落盘已交给 RitsuLib ModDataStore（Global 作用域）");
        }
        catch (Exception e)
        {
            _store = null;
            BridgeLog.Warn("[memory] ModDataStore 不可用，退回纯内存（不落盘）: " + e.Message);
        }

        Load();
    }

    // ------------------------------------------------------------------ 采集

    /// <summary>每帧调用；内部 2 秒节流，读一次游戏状态。</summary>
    public static void Tick()
    {
        var now = System.Environment.TickCount64;
        if (now - _lastTickAt < 2000) return;
        _lastTickAt = now;

        try
        {
            var rm = RunManager.Instance;
            var s = rm?.DebugOnlyGetState();
            if (s == null) return;

            var p = s.Players?.FirstOrDefault();
            var character = p?.Character?.Id?.ToString() ?? "";
            var asc = s.AscensionLevel;

            var key = $"{character}|A{asc}";
            if (_runKey != "" && !key.Equals(_runKey, StringComparison.Ordinal))
                ResetInternal(key, character, asc);
            else if (_runKey == "")
                _runKey = key;

            _character = character;
            _ascension = asc;
            // 换幕（ActFloor 从大变小）→ 用最近几条事件拼一句「回顾」，不额外调模型
            if (_lastActFloor > 0 && s.ActFloor > 0 && s.ActFloor < _lastActFloor)
            {
                List<Entry> tail;
                lock (Gate) tail = Events.Skip(Math.Max(0, Events.Count - 6)).ToList();
                if (tail.Count > 0)
                    _recap = "上一幕：" + string.Join(" / ", tail.Select(e => $"{TranslateKind(e.Kind)}{e.Text}"));
            }
            _lastActFloor = s.ActFloor > 0 ? s.ActFloor : _lastActFloor;
            _act = s.ActFloor > 0 ? s.ActFloor : _act;
            _actFloor = s.ActFloor;
            _totalFloor = s.TotalFloor;
            _room = s.CurrentRoom?.RoomType.ToString() ?? "";
            _hp = p?.Creature?.CurrentHp ?? 0;
            _maxHp = p?.Creature?.MaxHp ?? 0;
            _gold = p?.Gold ?? 0;
            _deck = p?.Deck?.Cards?.Count ?? 0;
            _relics = p?.Relics?.Count ?? 0;

            _lastLiveAt = now;

            if (now - _lastSaveAt > 10000)
            {
                _lastSaveAt = now;
                Save();
            }
        }
        catch
        {
            // 状态读不到就算了，别影响游戏
        }
    }

    /// <summary>记录一条本局事件（决定/战斗结果/商店…），text 建议 ≤14 字。</summary>
    public static void AddEvent(string kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var clean = text.Trim();
        if (clean.Length > 14) clean = clean[..14];

        lock (Gate)
        {
            Events.Add(new Entry { At = System.Environment.TickCount64, Kind = kind ?? "", Text = clean });
            if (Events.Count > MaxEvents) Events.RemoveRange(0, Events.Count - MaxEvents);
        }

        try
        {
            File.AppendAllText(_historyFile,
                JsonSerializer.Serialize(new { t = DateTime.Now.ToString("HH:mm:ss"), kind, text = clean }) + "\n");
        }
        catch { }
    }

    // ------------------------------------------------------------------ 提示词

    /// <summary>拼成给模型看的一小段（约 120~160 token）。读不到状态时返回空串。</summary>
    public static string Digest()
    {
        // 记忆是上一局留下的、当前又没在局里 → 别把这些数字塞给模型
        if (System.Environment.TickCount64 - _lastLiveAt > 5000) return "";
        if (string.IsNullOrEmpty(_runKey)) return "";

        var sb = new StringBuilder();

        var hpText = _maxHp > 0 ? $"{_hp}/{_maxHp} 血" : "";
        var where = _room.Length > 0 ? $"房间 {TranslateRoom(_room)}" : "";
        var parts = new[]
        {
            $"{TranslateCharacter(_character)} A{_ascension}",
            $"第 {Math.Max(1, _totalFloor)} 层",
            where,
            hpText,
            $"金币 {_gold}",
            $"卡组 {_deck} 张",
            $"遗物 {_relics} 件",
        }.Where(x => !string.IsNullOrEmpty(x));

        sb.Append("【本局】").Append(string.Join(" · ", parts)).Append('\n');

        List<Entry> recent;
        lock (Gate) recent = Events.Skip(Math.Max(0, Events.Count - DigestEvents)).ToList();
        if (recent.Count > 0)
        {
            sb.Append("【最近】").Append(string.Join(" / ",
                recent.Select(e => $"{TranslateKind(e.Kind)}：{e.Text}"))).Append('\n');
        }

        if (!string.IsNullOrEmpty(_recap)) sb.Append("【回顾】").Append(_recap).Append('\n');

        return sb.ToString();
    }

    public static Dictionary<string, object> Snapshot()
    {
        List<Entry> recent;
        lock (Gate) recent = Events.ToList();
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["runKey"] = _runKey,
            ["character"] = _character,
            ["ascension"] = _ascension,
            ["actFloor"] = _actFloor,
            ["totalFloor"] = _totalFloor,
            ["room"] = _room,
            ["hp"] = _hp,
            ["maxHp"] = _maxHp,
            ["gold"] = _gold,
            ["deck"] = _deck,
            ["relics"] = _relics,
            ["recap"] = _recap,
            ["events"] = recent.Select(e => new { t = e.At, kind = e.Kind, text = e.Text }).ToList<object>(),
            ["digest"] = Digest(),
        };
    }

    public static void SetRecap(string recap) => _recap = (recap ?? "").Trim();

    // ------------------------------------------------------------------ 存取

    private static void ResetInternal(string key, string character, int asc)
    {
        lock (Gate) Events.Clear();
        _recap = "";
        _runKey = key;
        _character = character;
        _ascension = asc;
        BridgeLog.Info($"[memory] 新的一局：{key}");
    }

    // RitsuLib 的持久化模型。字段全是短文本，和 0.7 的 run_memory.json 一一对应。
    private sealed class State
    {
        public string RunKey { get; set; } = "";
        public string Character { get; set; } = "";
        public int Ascension { get; set; }
        public int ActFloor { get; set; }
        public int TotalFloor { get; set; }
        public int LastActFloor { get; set; }
        public string Room { get; set; } = "";
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Gold { get; set; }
        public int Deck { get; set; }
        public int Relics { get; set; }
        public long LastLiveAt { get; set; }
        public string Recap { get; set; } = "";
        public List<Entry> Events { get; set; } = new();
    }

    private static void Save()
    {
        if (_store == null) return;
        try
        {
            List<Entry> snapshot;
            lock (Gate) snapshot = Events.Skip(Math.Max(0, Events.Count - MaxEvents)).ToList();

            _store.Modify<State>(StoreKey, s =>
            {
                s.RunKey = _runKey;
                s.Character = _character;
                s.Ascension = _ascension;
                s.ActFloor = _actFloor;
                s.TotalFloor = _totalFloor;
                s.LastActFloor = _lastActFloor;
                s.Room = _room;
                s.Hp = _hp;
                s.MaxHp = _maxHp;
                s.Gold = _gold;
                s.Deck = _deck;
                s.Relics = _relics;
                s.LastLiveAt = _lastLiveAt;
                s.Recap = _recap;
                s.Events = snapshot;
            });
            _store.Save(StoreKey);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[memory] 保存失败: " + e.Message);
        }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            // 1) 先看 RitsuLib 的数据仓
            if (_store != null)
            {
                var s = _store.Get<State>(StoreKey);
                if (s != null && (!string.IsNullOrEmpty(s.RunKey) || s.Events.Count > 0))
                {
                    Apply(s);
                    BridgeLog.Info($"[memory] 读到上一局记忆：{_runKey}（{Events.Count} 条事件）");
                    return;
                }
            }

            // 2) 没有就从 0.7 的 run_memory.json 迁移一次
            if (File.Exists(_legacyFile))
            {
                var s = ParseLegacy(File.ReadAllText(_legacyFile));
                if (s != null)
                {
                    Apply(s);
                    BridgeLog.Info($"[memory] 已从 0.7 的 run_memory.json 迁移：{_runKey}（{Events.Count} 条事件）");
                    if (_store != null) Save();
                }
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[memory] 读取失败: " + e.Message);
        }
    }

    private static void Apply(State s)
    {
        _runKey = s.RunKey ?? "";
        _character = s.Character ?? "";
        _ascension = s.Ascension;
        _actFloor = s.ActFloor;
        _totalFloor = s.TotalFloor;
        _lastActFloor = s.LastActFloor;
        _room = s.Room ?? "";
        _hp = s.Hp;
        _maxHp = s.MaxHp;
        _gold = s.Gold;
        _deck = s.Deck;
        _relics = s.Relics;
        _lastLiveAt = s.LastLiveAt;
        _recap = s.Recap ?? "";

        lock (Gate)
        {
            Events.Clear();
            if (s.Events != null)
                Events.AddRange(s.Events.Where(e => e != null).TakeLast(MaxEvents));
        }
    }

    /// <summary>0.7 的落盘格式（camelCase 外壳 + PascalCase 事件项）。</summary>
    private static State ParseLegacy(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var s = new State
        {
            RunKey = root.TryGetProperty("runKey", out var k) ? k.GetString() ?? "" : "",
            Character = root.TryGetProperty("character", out var c) ? c.GetString() ?? "" : "",
            Ascension = root.TryGetProperty("ascension", out var a) && a.TryGetInt32(out var av) ? av : 0,
            ActFloor = root.TryGetProperty("actFloor", out var af) && af.TryGetInt32(out var afv) ? afv : 0,
            TotalFloor = root.TryGetProperty("totalFloor", out var f) && f.TryGetInt32(out var fv) ? fv : 0,
            Recap = root.TryGetProperty("recap", out var r) ? r.GetString() ?? "" : "",
        };

        if (root.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in evs.EnumerateArray())
            {
                s.Events.Add(new Entry
                {
                    At = item.TryGetProperty("At", out var at) && at.TryGetInt64(out var atv) ? atv : 0,
                    Kind = item.TryGetProperty("Kind", out var kd) ? kd.GetString() ?? "" : "",
                    Text = item.TryGetProperty("Text", out var tx) ? tx.GetString() ?? "" : "",
                });
            }
        }
        return s;
    }

    // ------------------------------------------------------------------ 文案

    private static string TranslateRoom(string room) => room switch
    {
        "Combat" or "CombatRoom" => "战斗",
        "Elite" or "EliteRoom" => "精英",
        "Boss" or "BossRoom" => "Boss",
        "Shop" or "ShopRoom" => "商店",
        "RestSite" or "RestSiteRoom" => "营火",
        "Event" or "EventRoom" => "事件",
        "Treasure" or "TreasureRoom" => "宝箱",
        "Map" or "MapRoom" => "地图",
        _ => room,
    };

    private static string TranslateCharacter(string id)
    {
        var raw = (id ?? "").ToUpperInvariant();
        if (raw.StartsWith("CHARACTER.")) raw = raw["CHARACTER.".Length..];
        return raw switch
        {
            "IRONCLAD" => "铁甲战士",
            "SILENT" => "静默猎手",
            "DEFECT" => "故障机器人",
            "WATCHER" => "观者",
            _ => id ?? "",
        };
    }

    private static string TranslateKind(string kind) => kind switch
    {
        "card" or "card_select" => "选牌",
        "route" or "map" => "选路",
        "shop" => "商店",
        "rest" => "营火",
        "event" => "事件",
        "relic" => "遗物",
        "potion" => "药水",
        "combat" => "战斗",
        "elite" => "精英",
        "boss" => "Boss",
        _ => string.IsNullOrEmpty(kind) ? "事件" : kind,
    };
}
