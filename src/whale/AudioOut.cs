using System;
using System.IO;
using Godot;
using Sts2AgentBridge;

namespace Sts2Whale.Whale;

/// <summary>
/// 跨平台播放：用 Godot 自带的解码器放 mp3（Windows/Linux 没有 <c>afplay</c>）。
///
/// 只在主线程建节点，所以从后台线程调用时统一用 <see cref="Callable.CallDeferred"/> 派过去。
/// 同一时刻只保留一个播放器实例，新的一句会顶掉旧的。
/// </summary>
internal static class AudioOut
{
    private static AudioStreamPlayer? _current;

    /// <summary>播放一个 mp3 文件（失败静默：气泡和口型照常走）。</summary>
    public static void PlayMp3(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

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
        try { Callable.From(StopOnMain).CallDeferred(); } catch { }
    }

    private static void StartOnMain(byte[] bytes)
    {
        try
        {
            StopOnMain();

            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return;

            var stream = new AudioStreamMP3 { Data = bytes };
            var player = new AudioStreamPlayer
            {
                Stream = stream,
                VolumeDb = 0f,
                Name = "WhaleTts",
            };
            player.Finished += () =>
            {
                try { player.QueueFree(); } catch { }
                if (ReferenceEquals(_current, player)) _current = null;
            };

            tree.Root.AddChild(player);
            player.Play();
            _current = player;
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
            if (_current != null)
            {
                _current.Stop();
                _current.QueueFree();
                _current = null;
            }
        }
        catch { }
    }
}
