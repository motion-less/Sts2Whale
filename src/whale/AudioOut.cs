using System;
using System.Diagnostics;
using System.IO;
using Godot;
using Sts2AgentBridge;

namespace Sts2Whale.Whale;

/// <summary>
/// 播放 TTS 的 mp3。
///
/// **macOS 走 <c>afplay</c> 子进程，完全不碰 Godot/FMOD 的音频通道**。原因是实测到的一次死锁：
/// 主线程卡在 <c>FMOD_Memory_GetStats</c> 等 FMOD 全局临界区，同时 FMOD Studio 命令线程卡在
/// <c>FMOD::ChannelControl::stop()</c> 等同一把锁 —— 也就是「每次新建播放器、播完 Stop 再
/// QueueFree」这条路径在机器被内存压力拖慢（这次跑到 11 GB、系统在压缩换页）时会把主线程永久挂住。
/// 用子进程放音，解码和音频通道都不在游戏进程里，这条死锁路径就不存在了。
///
/// Windows/Linux 没有 <c>afplay</c>，退回 Godot 解码，但**播放器常驻、不再 QueueFree**：
/// 换一句只改 <c>Stream</c> 并重新 <c>Play()</c>，把「销毁仍在播放的通道」这个动作彻底去掉。
/// </summary>
internal static class AudioOut
{
    private static readonly object Gate = new();

    /// <summary>macOS 的 afplay 子进程；同时只留一个。</summary>
    private static Process? _process;

    /// <summary>非 macOS 用的常驻播放器（只建一次，永不销毁）。</summary>
    private static AudioStreamPlayer? _player;

    private static bool UseExternalPlayer => OperatingSystem.IsMacOS();

    /// <summary>播放一个 mp3 文件（失败静默：气泡和口型照常走）。</summary>
    public static void PlayMp3(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (UseExternalPlayer)
        {
            StartAfplay(path);
            return;
        }

        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[audio] 读 mp3 失败: " + e.Message);
            return;
        }

        try
        {
            Callable.From(() => StartOnMain(bytes)).CallDeferred();
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[audio] 派发播放失败: " + e.Message);
        }
    }

    public static void Stop()
    {
        if (UseExternalPlayer)
        {
            StopAfplay("stop");
            return;
        }

        try { Callable.From(StopOnMain).CallDeferred(); } catch { }
    }

    // ---------------------------------------------------------------- macOS：afplay

    private static void StartAfplay(string path)
    {
        StopAfplay("switch");
        try
        {
            if (!File.Exists(path)) return;
            var psi = new ProcessStartInfo("/usr/bin/afplay")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(path);

            var process = Process.Start(psi);
            if (process == null)
            {
                BridgeLog.Warn("[audio] afplay 没起来");
                return;
            }

            lock (Gate) _process = process;
            BridgeLog.Info($"[audio] afplay 播放（{new FileInfo(path).Length / 1024} KB）");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[audio] afplay 播放失败: " + e.Message);
        }
    }

    private static void StopAfplay(string reason)
    {
        Process? process;
        lock (Gate)
        {
            process = _process;
            _process = null;
        }
        if (process == null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                BridgeLog.Info($"[audio] afplay 停止（{reason}）");
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[audio] 结束 afplay 失败: " + e.Message);
        }
        finally
        {
            try { process.Dispose(); } catch { }
        }
    }

    // ---------------------------------------------------------------- 其它平台：常驻播放器

    private static void StartOnMain(byte[] bytes)
    {
        try
        {
            if (!EnsurePlayer()) return;

            var stream = new AudioStreamMP3 { Data = bytes };
            // 同一时刻只放一句：改 Stream 并重播，不 Stop、不销毁节点。
            _player!.Stream = stream;
            _player.Play();
            BridgeLog.Info($"[audio] 播放 mp3（{bytes.Length / 1024} KB）");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[audio] 播放失败: " + e.Message);
        }
    }

    private static void StopOnMain()
    {
        try
        {
            if (_player != null && GodotObject.IsInstanceValid(_player))
                _player.Stop();
        }
        catch { }
    }

    private static bool EnsurePlayer()
    {
        if (_player != null && GodotObject.IsInstanceValid(_player))
            return true;
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            return false;

        _player = new AudioStreamPlayer
        {
            Name = "WhaleTts",
            VolumeDb = 0f,
        };
        tree.Root.AddChild(_player);
        return true;
    }
}
