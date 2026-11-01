using System;
using System.Collections.Generic;
using Godot;

namespace Sts2AgentBridge;

/// <summary>
/// 帧时间探针：每帧只记一个 tick 差，环形缓冲 180 帧，用来判断「卡」是帧率低还是偶发卡顿。
/// 通过 /state 的 fps 字段暴露。
/// </summary>
public static class FpsProbe
{
    private const int Capacity = 180;

    private static readonly double[] Frames = new double[Capacity];
    private static int _index;
    private static int _count;
    private static long _lastUsec;
    private static double _maxMs;
    private static long _maxAtMs;

    private static readonly Dictionary<string, double[]> PerTick = new();
    private static readonly Dictionary<string, int> PerTickIndex = new();

    public static void Mark(string name, double ms)
    {
        if (!PerTick.TryGetValue(name, out var buf))
        {
            buf = new double[60];
            PerTick[name] = buf;
            PerTickIndex[name] = 0;
        }
        var i = PerTickIndex[name];
        buf[i] = ms;
        PerTickIndex[name] = (i + 1) % buf.Length;
    }

    private static Dictionary<string, object> TickStats()
    {
        var d = new Dictionary<string, object>();
        foreach (var kv in PerTick)
        {
            var copy = (double[])kv.Value.Clone();
            Array.Sort(copy);
            var sum = 0.0;
            foreach (var v in copy) sum += v;
            d[kv.Key] = Math.Round(sum / copy.Length, 3);
        }
        return d;
    }

    private static readonly double[] DrainMs = new double[Capacity];
    private static readonly double[] HandMs = new double[Capacity];
    private static int _di, _hi;

    public static void MarkDrain(double ms) { DrainMs[_di] = ms; _di = (_di + 1) % Capacity; }
    public static void MarkHand(double ms) { HandMs[_hi] = ms; _hi = (_hi + 1) % Capacity; }

    private static (double avg, double p95) Stats(double[] buf)
    {
        var copy = new double[buf.Length];
        Array.Copy(buf, copy, buf.Length);
        Array.Sort(copy);
        var sum = 0.0;
        foreach (var v in copy) sum += v;
        return (sum / copy.Length, copy[(int)(copy.Length * 0.95)]);
    }

    public static void Tick()
    {
        var now = (long)Time.GetTicksUsec();
        if (_lastUsec > 0)
        {
            var ms = (now - _lastUsec) / 1000.0;
            Frames[_index] = ms;
            _index = (_index + 1) % Capacity;
            if (_count < Capacity) _count++;
            if (ms > _maxMs)
            {
                _maxMs = ms;
                _maxAtMs = now / 1000;
            }
        }
        _lastUsec = now;
    }

    public static Dictionary<string, object> Snapshot()
    {
        if (_count == 0) return new() { ["ok"] = false };

        var copy = new double[_count];
        Array.Copy(Frames, copy, _count);
        Array.Sort(copy);

        double Pct(double p) => copy[Math.Clamp((int)(copy.Length * p), 0, copy.Length - 1)];
        var avg = 0.0;
        foreach (var v in copy) avg += v;
        avg /= copy.Length;

        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["avgMs"] = Math.Round(avg, 2),
            ["p50Ms"] = Math.Round(Pct(0.5), 2),
            ["p95Ms"] = Math.Round(Pct(0.95), 2),
            ["p99Ms"] = Math.Round(Pct(0.99), 2),
            ["maxMs"] = Math.Round(_maxMs, 2),
            ["maxAgoSec"] = _maxAtMs == 0 ? -1 : Math.Max(0, System.Environment.TickCount64 / 1000 - _maxAtMs),
            ["fpsAvg"] = Math.Round(avg > 0 ? 1000.0 / avg : 0, 1),
            ["fpsP95"] = Math.Round(Pct(0.95) > 0 ? 1000.0 / Pct(0.95) : 0, 1),
            ["samples"] = _count,
            ["drainAvgMs"] = Math.Round(Stats(DrainMs).avg, 3),
            ["drainP95Ms"] = Math.Round(Stats(DrainMs).p95, 3),
            ["handAvgMs"] = Math.Round(Stats(HandMs).avg, 3),
            ["handP95Ms"] = Math.Round(Stats(HandMs).p95, 3),
            ["ticks"] = TickStats(),
        };
    }
}
