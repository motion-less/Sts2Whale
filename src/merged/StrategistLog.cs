using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpireStrategist.Mod;

namespace Sts2AgentBridge;

/// <summary>
/// 把「全局战略助手」每一次决定记成一条事件，供鲸鱼（agent）拿去用角色卡的口吻讲给玩家。
///
/// 为什么要这层：它接管局外时自己点、不经过我们的 /request，agent 看不到它做了什么。
/// 这里每秒对一次它的状态（建议里的 key / 地图坐标 / 遗物列表 / 金币），有变化就记一条：
///   card / route / shop / relic / rest / event
/// agent 读 <c>GET /strategist/log?since=</c> 拿到增量，再用 persona 的口吻说出来。
/// </summary>
public static class StrategistLog
{
    private const int Capacity = 60;
    private static readonly List<Entry> Entries = new();
    private static long _seq;
    private static double _nextPollAt;

    private static string _lastDecisionKey;
    private static (int col, int row)? _lastCoord;
    private static string _lastRelicSet;
    private static int _lastGold = -1;
    private static string _lastSpoken;
    private static ulong _lastSpokenAt;
    private static bool _attached;          // 上一 tick 有没有拿到 AdvisorService
    private static double _quietUntil;      // 重新接上后的静默窗口：别把旧状态重播一遍
    private static readonly bool SayEnabled =
        System.Environment.GetEnvironmentVariable("STS2_STRATEGIST_SAY") != "0";

    public sealed record Entry(long Seq, string At, string Kind, string Text);

    public static void Tick()
    {
        try
        {
            var now = Time.GetTicksMsec() / 1000.0;
            if (now < _nextPollAt) return;
            _nextPollAt = now + 1.0;

            var service = AdvisorService.Current;
            if (service == null)
            {
                _attached = false;
                _lastDecisionKey = null;
                _lastCoord = null;
                _lastRelicSet = null;
                _lastGold = -1;
                return;
            }

            // 读档/回主菜单再回来时，它会把整份状态当成「新变化」重报一遍（路线、遗物、营火全重播，
            // 鲸鱼也就跟着重复说话）。接上的头几秒只重建基线，不记录、不播。
            if (!_attached)
            {
                _attached = true;
                _quietUntil = now + 3.0;
                BridgeLog.Info("strategist log: reattached — 跳过重播，只重建基线");
            }

            CaptureDecision(service);
            CaptureCoord(service);
            CaptureRelics(service);
        }
        catch { }
    }

    private static void CaptureDecision(AdvisorService service)
    {
        try
        {
            var decision = service.Decision;
            var key = decision == null ? null : decision.Kind + "|" + decision.Evaluation?.RecommendedKey;
            if (key == _lastDecisionKey) return;
            _lastDecisionKey = key;
            if (decision == null) return;

            var recommended = decision.Evaluation?.RecommendedKey;
            if (string.IsNullOrEmpty(recommended)) return;

            var option = Array.Find(decision.Options ?? Array.Empty<SpireStrategist.Core.DecisionOption>(),
                o => o.Key == recommended);

            var what = decision.Kind switch
            {
                "cards" => "选牌",
                "rest" => "营火",
                "route" => "选路",
                _ => decision.Kind,
            };

            var name = option?.Name ?? recommended;
            var why = option == null ? "" : Describe(decision.Evaluation, recommended);

            // 选牌必须把牌面信息带上：鲸鱼看得见牌名和理由、看不见牌，费用/效果全靠猜，
            // 而角色卡又要求「理由里带数字」——只给它牌名，它就会编一个费用。
            // 费用、类型、效果都从快照里取（SnapshotReader.ReadCard），是这一刻游戏里的真值。
            var card = decision.Kind == "cards" ? option?.Card : null;
            var cost = CardCost(card);

            var speak = decision.Kind switch
            {
                "cards" => string.Join(" ", new[] { $"拿{name}", cost, why.Length == 0 ? "" : Short(why) }
                    .Where(part => !string.IsNullOrEmpty(part))),
                "rest" => $"营火选{name}",
                _ => null,
            };
            Add(decision.Kind == "cards" ? "card" : decision.Kind,
                why.Length == 0
                    ? $"战略助手{what}「{name}」"
                    : $"战略助手{what}「{name}」：{why}",
                speak,
                CardFacts(card));
        }
        catch { }
    }

    private static void CaptureCoord(AdvisorService service)
    {
        try
        {
            var pair = service.State.CurrentMapCoord is { } c ? ((int, int)?)(c.col, c.row) : null;
            if (pair == _lastCoord) return;
            _lastCoord = pair;
            if (pair == null) return;

            var node = service.State.CurrentMapPoint?.PointType.ToString() ?? "?";
            // 只在关键节点说；普通怪/未知不吵
            var speak = node is "Elite" or "Boss" ? $"去打{node}了" : null;
            Add("route", $"走到 {node}（{pair.Value.Item1},{pair.Value.Item2}）", speak);
        }
        catch { }
    }

    private static void CaptureRelics(AdvisorService service)
    {
        try
        {
            var player = service.Player;
            if (player == null) return;

            var relics = player.Relics.Select(r => r.Id.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var fingerprint = string.Join(",", relics);
            if (fingerprint != _lastRelicSet)
            {
                var previous = (_lastRelicSet ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                var added = relics.Except(previous).ToArray();
                _lastRelicSet = fingerprint;
                foreach (var relic in added)
                {
                    var shortName = relic.Replace("RELIC.", "");
                    Add("relic", $"拿到遗物 {relic}", $"拿到{shortName}");
                }
            }

            var gold = player.Gold;
            if (_lastGold >= 0 && gold != _lastGold)
            {
                var delta = gold - _lastGold;
                if (delta < 0)
                {
                    if (-delta >= 25)
                        Add("shop", $"商店花了 {-delta} 金（剩 {gold}）", $"花{-delta}金 剩{gold}");
                }
                else if (delta >= 100)
                {
                    // 小额进账（<100）太吵：不记事件、也不叫鲸鱼娘说话
                    Add("shop", $"金币 +{delta}（现 {gold}）");
                }
            }
            _lastGold = gold;
        }
        catch { }
    }

    /// <summary>
    /// 牌面费用的短写法：`1 费` / `X 费` / `无法打出`；有星费时再补 `+ 2 星`。
    /// 取的是 <c>SnapshotReader.ReadCard</c> 在决策那一刻抓到的快照，不是猜的。
    /// </summary>
    private static string CardCost(SpireStrategist.Core.CardSnapshot card)
    {
        if (card == null) return "";
        var cost = card.CostsX ? "X 费" : card.Cost < 0 ? "无法打出" : $"{card.Cost} 费";
        if (card.CostsStarX) cost += " + X 星";
        else if (card.StarCost > 0) cost += $" + {card.StarCost} 星";
        return cost;
    }

    /// <summary>
    /// 给模型的一行牌面：`剑柄打击：1 费 · 攻击牌 · 普通 · '造成 9 点伤害，抽 1 张牌。'`
    /// 费用、类型、效果都是决策那一刻从 <c>SnapshotReader.ReadCard</c> 抓的，不是猜的。
    /// </summary>
    private static string CardFacts(SpireStrategist.Core.CardSnapshot card)
    {
        if (card == null) return null;
        var type = card.Type switch
        {
            "Attack" => "攻击牌",
            "Skill" => "技能牌",
            "Power" => "能力牌",
            "Status" => "状态牌",
            "Curse" => "诅咒牌",
            _ => "",
        };
        var rarity = card.Rarity switch
        {
            "Basic" => "基础",
            "Common" => "普通",
            "Uncommon" => "罕见",
            "Rare" => "稀有",
            "Special" => "特殊",
            _ => "",
        };

        var parts = new[] { CardCost(card), type, rarity }.Where(p => !string.IsNullOrEmpty(p)).ToList();
        var text = CardText(card.Description);
        if (text.Length > 0) parts.Add($"'{text}'");
        return $"{card.Name}：{string.Join(" · ", parts)}";
    }

    /// <summary>卡面效果压成一行：去 BBCode / 换行，最多 60 字（再长会挤掉决定本身）。</summary>
    private static string CardText(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var text = ScreenApi.CleanLong(description);
        // CleanLong 把 [gold]…[/gold] 换成空格，贴着中文标点时会留下「伤害 ，」这种缝
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+([，。、；：！？）】」』…])", "$1");
        if (text.Length > 60) text = text.Substring(0, 60).TrimEnd() + "…";
        return text;
    }

    private static string Describe(SpireStrategist.Core.Evaluation evaluation, string key)
    {
        try
        {
            var option = Array.Find(evaluation?.Options ?? Array.Empty<SpireStrategist.Core.OptionScore>(), o => o.Key == key);
            if (option?.Parts == null) return "";
            var parts = new List<string>();
            foreach (var part in option.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.Reason)) continue;
                parts.Add(part.Reason);
                if (parts.Count >= 2) break;
            }
            return string.Join("，", parts);
        }
        catch { return ""; }
    }

    /// <summary>把它的决定同时用鲸鱼的口吻说进气泡（第一人称、短句、不提第三方）。</summary>
    private static void Speak(string line)
    {
        try
        {
            if (!SayEnabled || string.IsNullOrWhiteSpace(line)) return;
            if (line == _lastSpoken && Time.GetTicksMsec() - _lastSpokenAt < 20000) return;   // 20 秒内不重复
            _lastSpoken = line;
            _lastSpokenAt = Time.GetTicksMsec();
            WhaleUi.Say(line, 9000, countsAsReason: false);
        }
        catch { }
    }

    /// <summary>把一句长理由压成「最多 10 个字的前半句」。</summary>
    private static string Short(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "";
        var text = reason;
        var sep = text.LastIndexOf('：');
        if (sep >= 0 && sep + 1 < text.Length) text = text.Substring(sep + 1);
        foreach (var mark in new[] { '；', '，', '。', '、' })
        {
            var i = text.IndexOf(mark);
            if (i > 0) text = text.Substring(0, i);
        }
        text = text.Trim();
        return text.Length <= 10 ? text : text.Substring(0, 10);
    }

    private static void Add(string kind, string text, string speakLine = null, string facts = null)
    {
        if (Time.GetTicksMsec() / 1000.0 < _quietUntil) return;   // 重建基线阶段：不记、不播
        var entry = new Entry(++_seq, DateTime.Now.ToString("HH:mm:ss"), kind, text);
        lock (Entries)
        {
            Entries.Add(entry);
            if (Entries.Count > Capacity) Entries.RemoveRange(0, Entries.Count - Capacity);
        }
        BridgeLog.Info($"[strategist] {entry.Text}");
        try { Sts2AgentBridge.RunMemory.AddEvent(kind, text); } catch { }

        // 有 API key 就让模型自己组织这句话（气泡里显示的是它的回复）；没有就退回模板短句。
        if (!SayEnabled) return;
        if (WhaleBrain.Enabled)
            WhaleBrain.Narrate(kind, text, speakLine ?? text, facts);
        else if (!string.IsNullOrEmpty(speakLine))
            Speak(speakLine);
    }

    public static Dictionary<string, object> Since(long since)
    {
        List<Entry> items;
        long latest;
        lock (Entries)
        {
            items = Entries.Where(e => e.Seq > since).ToList();
            latest = _seq;
        }

        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["latest"] = latest,
            ["count"] = items.Count,
            ["entries"] = items.Select(e => new Dictionary<string, object>
            {
                ["seq"] = e.Seq,
                ["at"] = e.At,
                ["kind"] = e.Kind,
                ["text"] = e.Text,
            }).ToList(),
        };
    }
}
