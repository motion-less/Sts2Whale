using System;
using System.Runtime;
using System.Threading;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Interop;

namespace Sts2AgentBridge;

/// <summary>
/// 局末内存压缩：回到主菜单（一局结束）时做一次「Gen2 + LOH 压缩」。
///
/// 为什么需要它：实测跑满 9.7 小时后托管堆提交到 2.8 GB、LOH 1,023 MB（新开局只有 4–75 MB），
/// 进程 footprint 从 3.0 GB 涨到 7.3 GB 并在那里平台化。这些空间里相当一部分是
/// **LOH 的空闲段**（大数组换代数次后留下的洞），普通 GC 回收不掉，只有压缩回收能还回去。
///
/// 时机很关键：压缩回收会 STW 数秒，所以只在「一局结束、人在主菜单」时做，界面上没人看；
/// 放在局中会把游戏冻住，比不优化还糟。
/// </summary>
internal static class RunEndCompaction
{
    private static bool _wasInRun;
    private static long _lastAt = long.MinValue / 2;
    private static int _running;
    private static long _lastReclaimedMb;

    public static long LastReclaimedMb => Interlocked.Read(ref _lastReclaimedMb);

    /// <summary>应急开关：STS2_WHALE_CLEAR_MODEL_IDS=0 可关掉身份表清理。</summary>
    private static bool ClearModelIdentitiesDisabled
        => Environment.GetEnvironmentVariable("STS2_WHALE_CLEAR_MODEL_IDS") == "0";

    private static bool _installed;
    private static long _lastCombatCompactAt = long.MinValue / 2;

    /// <summary>
    /// 订阅「战斗结束」：一局要打几小时的人永远等不到局末，但每场战斗结束都会经过地图/奖励界面。
    /// 这里只做压缩回收（不动身份表——战斗外的实体仍然引用着它们），每次至少间隔 120 秒。
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(
                _ => ScheduleCombatEnd());
            BridgeLog.Info("[compact] 已挂战斗结束压缩");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[compact] 订阅战斗结束失败（只保留局末清理）: " + e.Message);
        }
    }

    private static void ScheduleCombatEnd()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastCombatCompactAt) < 120_000) return;
        Interlocked.Exchange(ref _lastCombatCompactAt, now);
        Schedule("combat_end");
    }

    /// <summary>每帧调用（主线程）：只读一个布尔，检测「在局里 → 不在局里」的沿。</summary>
    public static void Tick()
    {
        bool inRun;
        try { inRun = RunManager.Instance?.IsInProgress == true; }
        catch { return; }

        bool ended = _wasInRun && !inRun;
        _wasInRun = inRun;
        if (ended) Schedule("run_end");
    }

    public static void Schedule(string reason)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;

        long now = Environment.TickCount64;
        // 局末可能连着触发几次（结算界面/主菜单来回切），一分钟内只做一次。
        if (reason == "run_end" && now - Interlocked.Read(ref _lastAt) < 60_000)
        {
            Volatile.Write(ref _running, 0);
            return;
        }
        Interlocked.Exchange(ref _lastAt, now);

        var thread = new Thread(() => Run(reason)) { IsBackground = true, Name = "whale-compaction" };
        thread.Start();
    }

    private static void Run(string reason)
    {
        try
        {
            long before = GC.GetTotalMemory(false);
            GCMemoryInfo infoBefore = GC.GetGCMemoryInfo();
            BridgeLog.Info(
                $"[compact] start reason={reason} managed={before / 1048576}MB " +
                $"committed={infoBefore.TotalCommittedBytes / 1048576}MB " +
                $"fragmented={infoBefore.FragmentedBytes / 1048576}MB");

            // RitsuLib 的模型身份表：每次搜索克隆出的模型都会往这两张表里登记，永不删除。
            // 实测 9.7 小时后各约 600 万条、各占 144 MB（数组还在 LOH 里）。只在「已经回主菜单」
            // 这个时刻清，此时没有任何战斗/实体在引用这些 id；身份是按需重建的缓存语义。
            if (reason == "run_end" && !ClearModelIdentitiesDisabled)
            {
                bool a = DiagDicts.Clear("STS2RitsuLib.Models.Identity.ModModelIdentityRegistry",
                    "ObjectToIdentity", out int objectCount);
                bool b = DiagDicts.Clear("STS2RitsuLib.Models.Identity.ModModelIdentityRegistry",
                    "IdentityToObject", out int identityCount);
                BridgeLog.Info(
                    $"[compact] model_identities cleared objectToIdentity={objectCount} " +
                    $"identityToObject={identityCount} ok={a && b}");
            }

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            long after = GC.GetTotalMemory(false);
            GCMemoryInfo infoAfter = GC.GetGCMemoryInfo();
            long reclaimed = (before - after) / 1048576;
            Interlocked.Exchange(ref _lastReclaimedMb, reclaimed);
            BridgeLog.Info(
                $"[compact] done reason={reason} managed={after / 1048576}MB " +
                $"committed={infoAfter.TotalCommittedBytes / 1048576}MB " +
                $"fragmented={infoAfter.FragmentedBytes / 1048576}MB " +
                $"reclaimed={reclaimed}MB");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[compact] failed: " + e.Message);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
