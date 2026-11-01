using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Sts2AgentBridge;

namespace Sts2Whale.Whale;

/// <summary>
/// 把一句话变成「音频包络 + 播放」。
///
/// mod 本身不说话（只有气泡文字），所以这里用 macOS 自带的 <c>say</c> 合成、
/// <c>afconvert</c> 转成 24k 单声道 wav，再从 wav 里算每 30ms 的 RMS 作为口型强度。
/// 播放交给 <c>afplay</c>，口型按 <see cref="ElapsedMs"/> 对齐——比按字数随机张嘴真得多。
/// </summary>
public sealed class SpeechLine : IDisposable
{
    public const int StepMs = 40;

    private readonly string? _wavPath;
    private Process? _player;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public float[] Envelope { get; }
    public string Text { get; }
    public bool Audible { get; }

    private bool _isMp3;

    internal SpeechLine(string text, float[] envelope, string? mediaPath, bool audible, bool isMp3 = false)
    {
        Text = text;
        Envelope = envelope;
        _wavPath = mediaPath;
        Audible = audible;
        _isMp3 = isMp3;
    }

    internal void StartPlayback()
    {
        if (_wavPath == null) return;

        if (_isMp3)
        {
            AudioOut.PlayMp3(_wavPath);      // Godot 内置解码，Windows/Linux/macOS 都能放
        }
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                _player = Process.Start(new ProcessStartInfo("afplay", Quote(_wavPath))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                });
            }
            catch { _player = null; }
        }
        _clock.Restart();
    }

    /// <summary>已经过了多少毫秒（口型对齐用）。</summary>
    public int ElapsedMs => (int)_clock.ElapsedMilliseconds;

    public int DurationMs => Envelope.Length * StepMs;

    public bool Finished => ElapsedMs > DurationMs + 150;

    public float ValueAt(int elapsedMs)
    {
        if (Envelope.Length == 0) return 0f;
        var index = elapsedMs / StepMs;
        if (index < 0 || index >= Envelope.Length) return 0f;
        return Envelope[index];
    }

    public void Dispose()
    {
        try { if (_player is { HasExited: false }) _player.Kill(); } catch { }
        try { _player?.Dispose(); } catch { }
        // 缓存文件留着给下一句复用（同名文本直接命中），只在临时目录里，系统会自己清

    }

    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
}

public static class SpeechPlayer
{
    // 复用一个 client：每句新建会多一次 TCP/TLS 建连，还堆 TIME_WAIT
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    /// <summary>
    /// 启动预热：跑一次极短的合成，把 python 启动、TLS 握手、afconvert 都热起来。
    /// 不做的话第一句话要 5 秒，做一次之后稳定在 1~2 秒。
    /// </summary>
    public static void Warmup(SpeechConfig cfg)
    {
        try
        {
            if (!cfg.Engine.Equals("edge", StringComparison.OrdinalIgnoreCase)) return;
            var exe = string.IsNullOrWhiteSpace(cfg.EdgeCommand) ? "edge-tts" : cfg.EdgeCommand;
            var dir = Path.Combine(Path.GetTempPath(), "sts2whale-tts");
            Directory.CreateDirectory(dir);
            var mp3 = Path.Combine(dir, "warmup.mp3");
            var wav = Path.Combine(dir, "warmup.wav");
            if (File.Exists(wav)) return;

            var args = new List<string>
            {
                "--voice", cfg.EdgeVoice, "--text", "。", "--write-media", mp3,
            };
            if (RunArgs(exe, args, 25000))
                RunArgs("afconvert", new List<string> { "-f", "WAVE", "-d", "LEI16@24000", "-c", "1", mp3, wav }, 10000);
        }
        catch { }
    }

    /// <summary>合成一句话；失败时退回「按字数」的假包络，保证嘴还是会动。</summary>
    public static SpeechLine Speak(string text, SpeechConfig cfg)
    {
        var clean = (text ?? "").Trim();
        if (clean.Length == 0) return new SpeechLine("", Array.Empty<float>(), null, false);

        if (cfg.Mode.Equals("tts", StringComparison.OrdinalIgnoreCase))
        {
            SpeechLine? line = null;
            if (cfg.Engine.Equals("daemon", StringComparison.OrdinalIgnoreCase))
                line = TryDaemon(clean, cfg);     // 常驻服务，最快
            if (line == null && cfg.Engine is "daemon" or "edge")
                line = TryEdge(clean, cfg);       // 命令行 edge 兜底
            if (line != null) return line;
        }

        return new SpeechLine(clean, TextEnvelope(clean), null, false);
    }

    /// <summary>常驻服务合成：POST /say 拿回 24k wav 字节，落盘后算包络 + 播放。</summary>
    private static SpeechLine? TryDaemon(string text, SpeechConfig cfg)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sts2whale-tts");
        Directory.CreateDirectory(dir);

        var key = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(cfg.Engine + "|" + cfg.EdgeVoice + "|" + cfg.EdgeRate + "|" + cfg.EdgePitch + "|" + text)))[..16];
        var wav = Path.Combine(dir, "tts_" + key + ".wav");
        var mp3 = Path.Combine(dir, "tts_" + key + ".mp3");

        try
        {
            // daemon 可能还在冷启动（1~2 秒），也可能空闲自退 / 崩了：先等它或重拉一次
            if (!TtsDaemon.Ready && !TtsDaemon.EnsureAlive())
            {
                BridgeLog.Warn("[tts] daemon 不可用，改走命令行兜底");
                return null;
            }

            if (!File.Exists(wav) && !File.Exists(mp3))
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    text,
                    voice = cfg.EdgeVoice,
                    rate = cfg.EdgeRate,
                    pitch = cfg.EdgePitch,
                });
                using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var resp = Http.PostAsync(TtsDaemon.BaseUrl + "/say", content).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    BridgeLog.Warn($"[tts] /say 返回 {(int)resp.StatusCode}");
                    return null;
                }

                var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes.Length < 1000)
                {
                    BridgeLog.Warn($"[tts] /say 只回了 {bytes.Length} 字节");
                    return null;
                }

                // "RIFF" = wav（macOS 的 daemon 会转换）；否则当 mp3（其他平台）
                var isWav = bytes.Length > 4 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F';
                File.WriteAllBytes(isWav ? wav : mp3, bytes);
            }

            if (File.Exists(wav))
            {
                var envelope = EnvelopeFromWav(wav, cfg.Gain);
                if (envelope.Length == 0) return null;
                var wavLine = new SpeechLine(text, envelope, wav, true);
                wavLine.StartPlayback();
                return wavLine;
            }

            if (File.Exists(mp3))
            {
                // 没解码 mp3：口型按字数估（有声音、嘴也动，只是不如 wav 精确）
                var line = new SpeechLine(text, TextEnvelope(text), mp3, true, isMp3: true);
                line.StartPlayback();
                return line;
            }
            return null;
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[tts] daemon 合成失败: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// 命令行 edge-tts 兜底：macOS 转 24k wav 算精确包络；Windows/Linux 没有
    /// afconvert，直接放 mp3（口型按字数估）——以前这里无条件调 afconvert，
    /// 等于 Windows 上根本没有兜底。
    /// </summary>
    private static SpeechLine? TryEdge(string text, SpeechConfig cfg)
    {
        var exe = string.IsNullOrWhiteSpace(cfg.EdgeCommand)
            ? "edge-tts"
            : cfg.EdgeCommand;
        if (Path.IsPathRooted(exe) && !File.Exists(exe)) return null;

        var dir = Path.Combine(Path.GetTempPath(), "sts2whale-tts");
        Directory.CreateDirectory(dir);

        // 同一句话直接复用（垫话/重复台词省一次网络）
        var key = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(cfg.EdgeVoice + "|" + cfg.EdgeRate + "|" + cfg.EdgePitch + "|" + text)))[..16];
        var wav = Path.Combine(dir, "edge_" + key + ".wav");
        var mp3 = Path.Combine(dir, "edge_" + key + ".mp3");

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (!File.Exists(wav))
                {
                    if (!SynthEdge(exe, text, cfg, mp3)) return null;
                    if (!RunArgs("afconvert", new List<string> { "-f", "WAVE", "-d", "LEI16@24000", "-c", "1", mp3, wav }, 15000))
                        return null;
                    try { File.Delete(mp3); } catch { }
                }

                var envelope = EnvelopeFromWav(wav, cfg.Gain);
                var line = new SpeechLine(text, envelope, wav, true);
                line.StartPlayback();
                return line;
            }

            // Windows / Linux：edge-tts 出 mp3，直接交给 Godot 解码播放
            if (!File.Exists(mp3) && !SynthEdge(exe, text, cfg, mp3)) return null;
            if (new FileInfo(mp3).Length < 1000) return null;

            var mp3Line = new SpeechLine(text, TextEnvelope(text), mp3, true, isMp3: true);
            mp3Line.StartPlayback();
            return mp3Line;
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[tts] 命令行合成失败: " + e.Message);
            return null;
        }
    }

    /// <summary>命令行走一遍 edge-tts，把音频写到 mp3。</summary>
    private static bool SynthEdge(string exe, string text, SpeechConfig cfg, string mp3)
    {
        var args = new List<string>
        {
            "--voice", cfg.EdgeVoice,
            "--rate=" + cfg.EdgeRate,
            "--pitch=" + cfg.EdgePitch,
            "--text", text,
            "--write-media", mp3,
        };
        return RunArgs(exe, args, 20000)
               && File.Exists(mp3)
               && new FileInfo(mp3).Length >= 1000;
    }

    private static bool RunArgs(string file, List<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static SpeechLine? TryTts(string text, SpeechConfig cfg)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sts2whale-tts");
        var stem = Guid.NewGuid().ToString("N");
        var aiff = Path.Combine(dir, stem + ".aiff");
        var wav = Path.Combine(dir, stem + ".wav");

        try
        {
            Directory.CreateDirectory(dir);
            if (!Run("say", $"-v {cfg.Voice} -o {Q(aiff)} -- {Q(text)}", 15000)) return null;
            if (!Run("afconvert", $"-f WAVE -d LEI16@24000 -c 1 {Q(aiff)} {Q(wav)}", 15000)) return null;

            var envelope = EnvelopeFromWav(wav, cfg.Gain);
            var line = new SpeechLine(text, envelope, wav, true);
            line.StartPlayback();
            return line;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(aiff)) File.Delete(aiff); } catch { }
        }
    }

    private static bool Run(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读 16bit PCM wav，按 30ms 窗算 RMS，归一化到 0..1 并做轻度包络平滑。</summary>
    private static float[] EnvelopeFromWav(string path, double gain)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44) return Array.Empty<float>();

        var channels = BitConverter.ToInt16(bytes, 22);
        var rate = BitConverter.ToInt32(bytes, 24);
        var bits = BitConverter.ToInt16(bytes, 34);
        if (channels <= 0 || rate <= 0 || bits != 16) return Array.Empty<float>();

        var dataStart = 44;
        for (var i = 12; i + 8 < bytes.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, i, 4);
            var size = BitConverter.ToInt32(bytes, i + 4);
            if (id == "data") { dataStart = i + 8; break; }
            i += 8 + size + (size % 2);
        }

        var sampleCount = (bytes.Length - dataStart) / 2 / channels;
        if (sampleCount <= 0) return Array.Empty<float>();

        var window = Math.Max(1, rate * SpeechLine.StepMs / 1000);
        var frames = sampleCount / window;
        var envelope = new float[frames];
        var max = 1e-6;

        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var s = 0; s < window; s++)
            {
                var offset = dataStart + (f * window + s) * 2 * channels;
                if (offset + 1 >= bytes.Length) break;
                var sample = BitConverter.ToInt16(bytes, offset) / 32768.0;
                sum += sample * sample;
            }
            var rms = Math.Sqrt(sum / window);
            envelope[f] = (float)rms;
            if (rms > max) max = rms;
        }

        // 归一化 + 增益；再做 3 帧平均 + 轻度起音/释放平滑，避免嘴抖
        var normalized = new float[envelope.Length];
        for (var i = 0; i < envelope.Length; i++)
            normalized[i] = (float)Math.Clamp(envelope[i] / max * gain, 0, 1);

        for (var i = 0; i < envelope.Length; i++)
        {
            var sum = normalized[i];
            var count = 1;
            if (i > 0) { sum += normalized[i - 1]; count++; }
            if (i + 1 < normalized.Length) { sum += normalized[i + 1]; count++; }
            var v = sum / count;

            if (i > 0) v = Math.Max(v, envelope[i - 1] * 0.35f);
            envelope[i] = v;
        }

        return envelope;
    }

    /// <summary>没有 TTS 时的兜底：每个字给一个随机的开口量，标点处闭嘴。</summary>
    private static float[] TextEnvelope(string text)
    {
        var rng = new Random(text.GetHashCode());
        var frames = new List<float>();
        foreach (var ch in text)
        {
            if ("，。！？、…,.!? \n".IndexOf(ch) >= 0)
            {
                frames.Add(0f);
                continue;
            }
            var baseValue = 0.35f + (float)rng.NextDouble() * 0.5f;
            frames.Add(baseValue);
            frames.Add(baseValue * 0.75f);
            frames.Add(baseValue * 0.35f);
        }
        return frames.ToArray();
    }

    private static string Q(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
