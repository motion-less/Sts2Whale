using System;
using System.Threading;
using Godot;

namespace Sts2AgentBridge;

/// <summary>
/// 主线程看门狗。
///
/// 已经踩过一次：主线程卡在 FMOD 全局临界区上，游戏界面彻底不动，但 HTTP 线程和皮套线程都还活着——
/// 从外面只能看到「/state 超时」，看不出卡在哪。这里每帧打一个心跳，另开一条后台线程盯着：
/// 超过阈值就写一条带「最后执行到哪一步 + 卡了多久」的日志，下一次再出问题直接看日志就行，
/// 不用再现场 attach 采样。
///
/// 只读心跳、只写日志，不做任何恢复动作（主线程一旦卡死，进程内没有安全的自救手段）。
/// </summary>
internal static class MainThreadWatchdog
{
    private const int CheckIntervalMs = 5_000;
    private static readonly long[] ReportThresholdsMs = { 20_000, 60_000, 180_000, 600_000 };

    private static long _lastBeatMs;
    private static long _startedMs;
    private static string _lastStage = "-";
    private static int _reportedLevel;
    private static long _peakStalledMs;
    private static Thread? _thread;

    /// <summary>主线程已经多久没跳过了（毫秒）。给 /health 之类的接口读。</summary>
    public static long StalledMs
    {
        get
        {
            long last = Volatile.Read(ref _lastBeatMs);
            return last <= 0 ? 0 : Math.Max(0, System.Environment.TickCount64 - last);
        }
    }

    public static string LastStage => Volatile.Read(ref _lastStage) ?? "-";

    /// <summary>每帧调用（主线程）。参数是当前正在跑的阶段名，卡死时它就是第一现场。</summary>
    public static void Beat(string stage)
    {
        Volatile.Write(ref _lastStage, stage);
        Volatile.Write(ref _lastBeatMs, System.Environment.TickCount64);
    }

    public static void Start()
    {
        if (_thread != null) return;
        _startedMs = System.Environment.TickCount64;
        Beat("startup");
        _thread = new Thread(Loop) { IsBackground = true, Name = "whale-watchdog" };
        _thread.Start();
    }

    private static void Loop()
    {
        while (true)
        {
            try
            {
                Thread.Sleep(CheckIntervalMs);
                long stalled = StalledMs;
                if (stalled < ReportThresholdsMs[0])
                {
                    // 恢复过来了：把等级归零，下次卡死重新从第一档报起。
                    if (Volatile.Read(ref _reportedLevel) != 0)
                    {
                        Volatile.Write(ref _reportedLevel, 0);
                        long peak = Volatile.Read(ref _peakStalledMs);
                        Volatile.Write(ref _peakStalledMs, 0);
                        BridgeLog.Info(
                            $"[watchdog] 主线程已恢复（最长卡住 {peak / 1000.0:0.0}s，最后阶段={LastStage}）");
                    }
                    continue;
                }
                if (stalled > Volatile.Read(ref _peakStalledMs))
                    Volatile.Write(ref _peakStalledMs, stalled);

                int level = 0;
                for (int i = 0; i < ReportThresholdsMs.Length; i++)
                    if (stalled >= ReportThresholdsMs[i]) level = i + 1;
                if (level <= Volatile.Read(ref _reportedLevel)) continue;
                Volatile.Write(ref _reportedLevel, level);

                long uptimeSec = (System.Environment.TickCount64 - _startedMs) / 1000;
                BridgeLog.Warn(
                    $"[watchdog] 主线程已停 {stalled / 1000.0:0.0}s（最后阶段={LastStage}，" +
                    $"进程已运行 {uptimeSec / 60}min，托管堆={GC.GetTotalMemory(false) / 1048576}MB，" +
                    $"已提交={GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576}MB）；" +
                    $"若持续不恢复，用 sample {System.Environment.ProcessId} 3 -file /tmp/sts2-hang.txt 抓栈");
            }
            catch
            {
                // 看门狗自身绝不能把游戏搞崩。
            }
        }
    }
}
