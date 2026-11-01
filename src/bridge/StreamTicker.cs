using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>
/// 主播「不冷场」：长时间没人说话时，鲸鱼自己冒一句垫话。
///
/// 垫话从人设卡（<c>persona.md</c>）里含「垫话」的小标题下解析；没有就用内置的一小撮兜底。
/// 只在「在局里 + 气泡没在显示」时补，间隔可调（<c>STS2_STREAM_GAP</c> 秒，默认 55）。
/// </summary>
public static class StreamTicker
{
    private static readonly string[] Fallback =
    {
        "有点困", "这局稳的", "弹幕呢", "手好痒", "喝口水", "先吃一口",
        "在想了 别催", "这牌怎么还不来", "盯着看久了眼睛疼",
    };

    private static readonly Random Rng = new();
    private static double _nextAt;
    private static double _lastSayAt;

    private static double GapSeconds
    {
        get
        {
            var raw = System.Environment.GetEnvironmentVariable("STS2_STREAM_GAP");
            return double.TryParse(raw, out var v) && v >= 15 ? v : 55d;
        }
    }

    public static void Tick()
    {
        try
        {
            var now = Time.GetTicksMsec() / 1000.0;

            if (!InRun())
            {
                _nextAt = 0;
                return;
            }

            // 气泡还在显示：说明刚说过，把下一次往后顺延，别打断。
            if (WhaleUi.BubbleVisible)
            {
                _nextAt = Math.Max(_nextAt, now + 8);
                return;
            }

            if (_nextAt <= 0 || now < _nextAt) return;

            var pool = Persona.Fillers;
            var lines = pool.Count > 0 ? pool : (IReadOnlyList<string>)Fallback;
            var line = lines[Rng.Next(lines.Count)];

            WhaleUi.Say(line, 9000, countsAsReason: false);
            _lastSayAt = now;
            var gap = GapSeconds;
            _nextAt = now + gap * (0.8 + Rng.NextDouble() * 0.5);   // 45~80 秒之间飘，不像定时器
        }
        catch { }
    }

    private static bool InRun()
    {
        try { return RunManager.Instance != null && RunManager.Instance.IsInProgress; }
        catch { return false; }
    }
}
