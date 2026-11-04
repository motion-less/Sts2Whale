using System;
using System.Diagnostics;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Interop;

namespace Sts2AgentBridge;

/// <summary>
/// 战斗求解器算完、只剩阵亡路线（<c>SolverResult.OnlyDeathRoutesFound</c>）时开始放「失败曲」，
/// 一直循环到角色死亡；死亡（HP ≤ 0 / 战斗结束）后 3 秒淡出停止。
/// 中途如果重算出了存活路线，1 秒淡出停止。
///
/// 音频不随包分发（版权/体积）。按顺序找：
///   1. &lt;DataDir&gt;/guanyu.mp3（玩家数据目录，更新 mod 不丢）
///   2. &lt;ModDir&gt;/assets/guanyu.mp3（随包资源）
/// 也接受 `.ogg` / `.wav`，文件名也认 `关羽之歌`。
/// </summary>
internal static class LossSong
{
    private const double FadeSeconds = 3.0;      // 角色死亡后的淡出
    private const double QuickFadeSeconds = 1.0; // 又算出存活路线时的收尾
    private const float SilentDb = -80f;
    // 正常靠 RitsuLib 的 CombatEndedEvent 收尾；这个只是兜底（事件没到时 10 秒查一次）
    private const long FallbackCheckIntervalMs = 10_000;

    private static readonly string[] BaseNames = { "guanyu", "关羽之歌", "guan-yu", "guanyu_zhi_ge" };
    private static readonly string[] Extensions = { ".mp3", ".ogg", ".wav" };

    private static readonly object Gate = new();
    private static AudioStreamPlayer? _player;
    private static Process? _process;
    private static string _externalPath = "";
    private static long _lastExternalExitAt;
    private static long _lastStartAt;
    private static int _spawnCount;
    private static Tween? _fade;
    private static bool _watching;
    private static bool _fading;
    private static bool _installed;
    private static long _nextDeathCheckAt;
    private static string _missingLogged = "";

    /// <summary>启动时挂一次战斗结束事件：角色死了/战斗结束立刻收尾，不靠轮询。</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(_ => OnCombatEnded());
            BridgeLog.Info("[fanfare] 已挂战斗结束事件");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[fanfare] 订阅战斗结束事件失败（走 10 秒兜底检查）: " + e.Message);
        }
    }

    /// <summary>求解器每出一次新结果就调一次：只剩阵亡路线就开播，算出存活路线就收。</summary>
    public static void OnSolverResult(bool onlyDeathRoutes)
    {
        try
        {
            if (onlyDeathRoutes) Begin();
            else if (_watching) FadeOut(QuickFadeSeconds);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[fanfare] " + e.Message);
        }
    }

    private static void OnCombatEnded()
    {
        if (!_watching || _fading) return;
        try { Callable.From(OnCombatEndedOnMain).CallDeferred(); }
        catch (Exception e) { BridgeLog.Warn("[fanfare] 战斗结束回调失败: " + e.Message); }
    }

    private static void OnCombatEndedOnMain()
    {
        if (!_watching || _fading) return;
        // 赢了/没死就快速收；角色死亡就走正常淡出
        FadeOut(PlayerHp() <= 0 ? FadeSeconds : QuickFadeSeconds);
    }

    /// <summary>兜底：事件没到时每 10 秒读一次局面。正常流程走 <see cref="OnCombatEnded"/>。</summary>
    public static void Tick()
    {
        if (!_watching || _fading) return;

        var now = System.Environment.TickCount64;
        if (now < _nextDeathCheckAt) return;
        _nextDeathCheckAt = now + FallbackCheckIntervalMs;

        try
        {
            if (PlayerHp() <= 0 || RunOver() || !CombatInProgress())
                FadeOut(FadeSeconds);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[fanfare] tick: " + e.Message);
        }
    }

    private static void Begin()
    {
        if (_watching) return;
        if (DisplayServer.GetName() == "headless") return;

        var path = FindSong();
        if (path == null)
        {
            LogMissingOnce();
            return;
        }
        // 求解器结果会在「只剩阵亡路线」和「又算出生路」之间来回翻，5 秒内不重复起播：
        // 每次起播都要 fork 一个 afplay 子进程，来回切会把主线程拖成一小段一小段的。
        long now = System.Environment.TickCount64;
        if (now - _lastStartAt < 5000) return;
        _lastStartAt = now;
        _watching = true;
        _nextDeathCheckAt = 0;   // 开播后第一帧就查一次，别等 3 秒
        Callable.From(() => StartOnMain(path)).CallDeferred();
        BridgeLog.Info("[fanfare] 求解器只剩阵亡路线，开始播放失败曲（循环到角色死亡）");
    }

    private static string? FindSong()
    {
        // 文件名允许带后缀（例如「关羽之歌-赵季平.mp3」），按名字包含关键词匹配，
        // 不要求叫得一模一样；数据目录优先于 mod 自带 assets。
        foreach (var dir in new[] { ModPaths.DataDir, Path.Combine(ModPaths.ModDir, "assets") })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (BaseNames.Any(key => name.Contains(key, StringComparison.OrdinalIgnoreCase)))
                        return file;
                }
            }
            catch (Exception e)
            {
                BridgeLog.Warn("[fanfare] 扫描目录失败: " + e.Message);
            }
        }
        return null;
    }

    private static void LogMissingOnce()
    {
        var hint = $"把失败曲放到 {ModPaths.Data("guanyu.mp3")} 或 {Path.Combine(ModPaths.ModDir, "assets", "guanyu.mp3")}";
        if (hint == _missingLogged) return;
        _missingLogged = hint;
        BridgeLog.Warn("[fanfare] 没找到失败曲；" + hint);
    }

    private static void StartOnMain(string path)
    {
        try
        {
            StopPlayback("switch");
            if (!_watching) return;
            _fading = false;

            // macOS 走 afplay 子进程：解码和音频通道都不在游戏进程里，FMOD 那条
            // 「Stop 通道时等全局临界区」的死锁路径就不存在了。
            if (OperatingSystem.IsMacOS())
            {
                _externalPath = path;
                SpawnExternalPlayer();
            }
            else
            {
                StartGodotPlayer(path);
            }
            BridgeLog.Info($"[fanfare] 播放 {Path.GetFileName(path)}（循环，死亡后 {FadeSeconds:0}s 淡出）");
        }
        catch (Exception e)
        {
            _watching = false;
            BridgeLog.Warn("[fanfare] 播放失败: " + e.Message);
        }
    }

    private static void SpawnExternalPlayer()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_externalPath) || !File.Exists(_externalPath)) return;
            var psi = new ProcessStartInfo("/usr/bin/afplay")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(_externalPath);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Exited += (_, _) =>
            {
                try { process.Dispose(); } catch { }
                if (!_watching || _fading) return;
                // 播完再来（循环）。解码失败时会立刻退出，用 300ms 闸门防空转。
                long now = System.Environment.TickCount64;
                if (now - _lastExternalExitAt < 300) return;
                _lastExternalExitAt = now;
                try { Callable.From(SpawnExternalPlayer).CallDeferred(); } catch { }
            };
            if (!process.Start())
            {
                BridgeLog.Warn("[fanfare] afplay 没起来");
                return;
            }
            lock (Gate) _process = process;
            _spawnCount++;
            if (_spawnCount % 20 == 0)
                BridgeLog.Info($"[fanfare] afplay 已起播 {_spawnCount} 次");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[fanfare] afplay 失败: " + e.Message);
        }
    }

    private static void StartGodotPlayer(string path)
    {
        if (!EnsurePlayer()) return;
        var stream = LoadStream(path);
        if (stream == null)
        {
            BridgeLog.Warn("[fanfare] 无法解码：" + path);
            _watching = false;
            return;
        }
        _player!.Stream = stream;
        _player.VolumeDb = 0f;
        _player.StreamPaused = false;
        _player.Play();
    }

    /// <summary>
    /// 非 macOS 的常驻播放器：只建一次、永不 QueueFree。
    /// 销毁仍在播放的通道会在 FMOD 里等全局锁（实测能把主线程挂死），这里彻底不做这件事。
    /// </summary>
    private static bool EnsurePlayer()
    {
        if (_player != null && GodotObject.IsInstanceValid(_player))
            return true;
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            return false;

        var player = new AudioStreamPlayer { Name = "WhaleLossSong", VolumeDb = 0f };
        player.Finished += () =>
        {
            try
            {
                if (!_watching || _fading || !GodotObject.IsInstanceValid(player)) return;
                player.Play();
            }
            catch { }
        };
        tree.Root.AddChild(player);
        _player = player;
        return true;
    }

    private static AudioStream? LoadStream(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".mp3" => new AudioStreamMP3 { Data = File.ReadAllBytes(path) },
                ".ogg" => AudioStreamOggVorbis.LoadFromFile(path),
                ".wav" => AudioStreamWav.LoadFromFile(path),
                _ => null,
            };
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[fanfare] 读音频失败: " + e.Message);
            return null;
        }
    }

    private static void FadeOut(double seconds)
    {
        _watching = false;
        if (_fading) return;
        _fading = true;
        try { Callable.From(() => FadeOutOnMain(seconds)).CallDeferred(); }
        catch { StopPlayback("fade-dispatch-failed"); }
    }

    private static void FadeOutOnMain(double seconds)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var tree = Engine.GetMainLoop() as SceneTree;
                var timer = tree?.CreateTimer(seconds);
                if (timer != null) timer.Timeout += () => StopPlayback("fade");
                else StopPlayback("fade");
                return;
            }

            if (_player == null || !GodotObject.IsInstanceValid(_player))
            {
                StopPlayback("no-player");
                return;
            }
            var player = _player;
            _fade = player.CreateTween();
            _fade.TweenProperty(player, "volume_db", SilentDb, seconds);
            // 到点只暂停 + 静音，不 Stop、不销毁通道。
            _fade.TweenCallback(Callable.From(() => StopPlayback("fade")));
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[fanfare] 淡出失败: " + e.Message);
            StopPlayback("fade-failed");
        }
    }

    private static void StopCurrent(bool keepWatch)
    {
        StopPlayback("stop-current");
        if (!keepWatch) _watching = false;
    }

    /// <summary>
    /// 停止失败曲。macOS 只杀 afplay 子进程；其它平台只做「静音 + 暂停」，
    /// 播放器常驻复用 —— 销毁正在播放的通道会在 FMOD 里等全局锁，能把主线程挂死。
    /// </summary>
    private static void StopPlayback(string reason)
    {
        Process? process;
        lock (Gate)
        {
            process = _process;
            _process = null;
        }
        if (process != null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                BridgeLog.Info($"[fanfare] 停止失败曲（{reason}）");
            }
            catch (Exception e)
            {
                BridgeLog.Warn("[fanfare] 结束 afplay 失败: " + e.Message);
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        if (_player != null && GodotObject.IsInstanceValid(_player))
        {
            try
            {
                _player.StreamPaused = true;
                _player.VolumeDb = 0f;
            }
            catch { }
        }

        _fading = false;
        if (_fade != null)
        {
            try { _fade.Kill(); } catch { }
            _fade = null;
        }
    }

    // ------------------------------------------------------------------ 局面

    private static int PlayerHp()
    {
        try
        {
            var run = RunManager.Instance?.DebugOnlyGetState();
            var player = run == null ? null : LocalContext.GetMe(run);
            return player?.Creature?.CurrentHp ?? -1;
        }
        catch { return -1; }
    }

    private static bool RunOver()
    {
        try { return RunManager.Instance?.DebugOnlyGetState()?.IsGameOver == true; }
        catch { return false; }
    }

    private static bool CombatInProgress()
    {
        try { return CombatManager.Instance?.IsInProgress == true; }
        catch { return false; }
    }
}
