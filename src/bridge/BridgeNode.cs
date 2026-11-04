using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Godot;

namespace Sts2AgentBridge;

/// <summary>
/// Runs work on the Godot main thread. A code-created Node subclass does not get a script
/// instance (no Godot source generator in a plain mod assembly), so its _Process is never
/// called; the SceneTree's ProcessFrame signal is used instead.
/// </summary>
public static class BridgePump
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static SceneTree _tree;
    private static Action _handler;

    public static bool IsAttached => _tree != null;

    public static void Install()
    {
        _tree = Engine.GetMainLoop() as SceneTree;
        if (_tree == null)
        {
            BridgeLog.Error("no SceneTree; cannot install main-thread pump");
            return;
        }

        _handler = Drain;
        _tree.ProcessFrame += _handler;
        MainThreadWatchdog.Start();
        BridgeLog.Info("main-thread pump installed on SceneTree.ProcessFrame");
    }

    private static void Drain()
    {
        MainThreadWatchdog.Beat("drain");
        FpsProbe.Tick();
        RunMemory.Tick();
        var d0 = (long)Godot.Time.GetTicksUsec();
        void T(string name, Action fn)
        {
            // 心跳打在调用之前：真卡在某一步时，看门狗读到的「最后阶段」就是卡住的那一步。
            MainThreadWatchdog.Beat(name);
            var t0 = (long)Godot.Time.GetTicksUsec();
            fn();
            FpsProbe.Mark(name, ((long)Godot.Time.GetTicksUsec() - t0) / 1000.0);
        }

        T("agent", AgentUi.Tick);
        T("events", EventFeed.Tick);
        T("state", StatePush.Tick);
        T("stream", StreamTicker.Tick);
        T("streak", StreakHud.Tick);
#if MERGED
        T("strat", StrategistBridge.Tick);
        T("stratlog", StrategistLog.Tick);
        T("autoloop", AutoLoop.Tick);
        T("fanfare", LossSong.Tick);
#endif
        T("solver", SolverBridge.Tick);
        T("request", RequestApi.Tick);
        var h0 = (long)Godot.Time.GetTicksUsec();
        GameApi.TickHandWatch();
        FpsProbe.MarkHand(((long)Godot.Time.GetTicksUsec() - h0) / 1000.0);
        FpsProbe.Mark("hand", ((long)Godot.Time.GetTicksUsec() - h0) / 1000.0);
        FpsProbe.MarkDrain(((long)Godot.Time.GetTicksUsec() - d0) / 1000.0);

        for (var i = 0; i < 64 && Queue.TryDequeue(out var work); i++)
        {
            try { work(); }
            catch (Exception e) { BridgeLog.Error("main-thread work threw", e); }
        }
    }

    public static Task<T> OnMain<T>(Func<T> fn, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(() =>
        {
            try { tcs.TrySetResult(fn()); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    public static Task OnMain(Action action, int timeoutMs = 5000) =>
        OnMain<object>(() => { action(); return null; }, timeoutMs);
}


