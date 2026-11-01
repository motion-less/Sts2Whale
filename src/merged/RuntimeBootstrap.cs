using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using STS2RitsuLib.Ui.Toast;
using Sts2Whale.Whale;

namespace Sts2AgentBridge;

/// <summary>
/// 首次启动的「补环境」：发布包不能带本机路径，所以 `live.json` 的 python 和
/// `vts.json` 的 speech.daemonPython 默认都是空的。
///
/// 这里在后台做三件事：
///   1. 找一个能用的 python3（弹幕只需要标准库）
///   2. 找一个能 `import edge_tts` 的 python；没有就在 DataDir 里建个独立 venv 装上
///   3. 把找到的路径写回配置，然后**提示玩家重启游戏生效**
///
/// 全程后台线程，不阻塞游戏启动；失败也只影响语音/弹幕，不影响其他功能。
/// </summary>
internal static class RuntimeBootstrap
{
    private const string FailedMarker = ".venv-install-failed";
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    public static void EnsureInBackground(string dataDir)
    {
        Task.Run(() =>
        {
            try
            {
                Ensure(dataDir);
            }
            catch (Exception e)
            {
                BridgeLog.Warn("[boot] 环境检查失败: " + e.Message);
            }
        });
    }

    private static void Ensure(string dataDir)
    {
        var vts = new JsonFile(Path.Combine(dataDir, "vts.json"));

        // 语音要的 python（必须能 import edge_tts）
        var ttsPy = FirstWorking(FirstNonEmpty(vts.GetString("speech.daemonPython")), "edge_tts")
                    ?? PyRuntime.WithModule("edge_tts");

        var installedNow = false;
        if (ttsPy == null)
        {
            ttsPy = InstallVenvKeepingQuiet(dataDir, PyRuntime.Any());
            installedNow = ttsPy != null;
        }

        if (ttsPy == null)
        {
            // 装失败（多半是没网）：只提示一次，6 小时内别再试
            if (ShouldRetry(dataDir))
            {
                MarkFailed(dataDir);
                Toast("鲸鱼爬塔：语音没装好", "联网后重启游戏会自动装 edge-tts；不装的话只有字幕，其他功能正常。");
            }
            return;
        }

        if (NeedsWrite(FirstNonEmpty(vts.GetString("speech.daemonPython"))))
        {
            vts.SetString("speech.daemonPython", ttsPy);
            var edge = Path.Combine(Path.GetDirectoryName(ttsPy) ?? "",
                                    OperatingSystem.IsWindows() ? "edge-tts.exe" : "edge-tts");
            if (File.Exists(edge)) vts.SetString("speech.edgeCommand", edge);
            vts.Save();
            BridgeLog.Info($"[boot] 语音 python 写入配置：{ttsPy}");
        }

        if (installedNow)
        {
            ClearFailed(dataDir);
            BridgeLog.Info("[boot] edge-tts 已装好，直接拉起 TTS（不用重启）");
            StartTtsNow(ttsPy, vts);
            Toast("鲸鱼爬塔：语音组件装好了", "现在就出声了，不用重启。");
        }
    }

    // ------------------------------------------------------------------ 装 venv

    /// <summary>在 DataDir 里建一个独立 venv 并装 edge-tts。成功返回它的 python 路径。</summary>
    private static string? InstallVenvKeepingQuiet(string dataDir, string? basePython)
    {
        if (!ShouldRetry(dataDir)) return null;
        if (basePython == null) return null;

        var venvDir = Path.Combine(dataDir, "venv");
        // POSIX 是 bin/，Windows 是 Scripts/ 且带 .exe
        var binDir = OperatingSystem.IsWindows()
            ? Path.Combine(venvDir, "Scripts")
            : Path.Combine(venvDir, "bin");
        var venvPy = Path.Combine(binDir, OperatingSystem.IsWindows() ? "python.exe" : "python");

        try
        {
            if (!File.Exists(venvPy))
            {
                BridgeLog.Info($"[boot] 建 venv：{venvDir}");
                if (!Run(basePython, new[] { "-m", "venv", venvDir }, 180_000))
                {
                    BridgeLog.Warn("[boot] 建 venv 失败");
                    return null;
                }
            }

            BridgeLog.Info("[boot] 安装 edge-tts（可能要几十秒，后台进行）");
            var ok = Run(venvPy,
                         new[] { "-m", "pip", "install", "--upgrade", "--quiet", "edge-tts" },
                         600_000);

            if (!ok)
            {
                // venv 里可能没有 pip，ensurepip 补一下再试一次
                BridgeLog.Warn("[boot] pip install 失败，用 ensurepip 补救后重试");
                Run(venvPy, new[] { "-m", "ensurepip", "--upgrade" }, 180_000);
                ok = Run(venvPy,
                         new[] { "-m", "pip", "install", "--upgrade", "--quiet", "edge-tts" },
                         600_000);
            }
            if (!ok) { BridgeLog.Warn("[boot] pip install edge-tts 失败"); return null; }

            return File.Exists(venvPy) ? venvPy : null;
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[boot] 装 venv 出错: " + e.Message);
            return null;
        }
    }

    private static bool Run(string exe, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return proc.WaitForExit(timeoutMs) && proc.ExitCode == 0;
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"[boot] 执行 {Path.GetFileName(exe)} 失败: {e.Message}");
            return false;
        }
    }

    private static string? FirstWorking(string? configured, string? module)
    {
        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured)) return null;
        try
        {
            var psi = new ProcessStartInfo(configured)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(module == null ? "import sys" : $"import {module}");
            using var proc = Process.Start(psi);
            return proc != null && proc.WaitForExit(20_000) && proc.ExitCode == 0 ? configured : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>装完直接起 TTS 常驻服务，省掉"重启一次"这一步。</summary>
    private static void StartTtsNow(string python, JsonFile vts)
    {
        try
        {
            var port = vts.GetInt("speech.daemonPort", 48765);
            var idle = vts.GetInt("speech.daemonIdleMinutes", 30);
            TtsDaemon.Start(python, ModPaths.Script("tts_daemon.py"), port, idle);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[boot] 拉起 TTS 失败: " + e.Message);
        }
    }

    private static bool NeedsWrite(string? current)
        => string.IsNullOrWhiteSpace(current) || !File.Exists(current);

    private static string FirstNonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s!.Trim();

    // ------------------------------------------------------------------ 失败标记

    private static bool ShouldRetry(string dataDir)
    {
        try
        {
            var marker = Path.Combine(dataDir, FailedMarker);
            if (!File.Exists(marker)) return true;
            var text = File.ReadAllText(marker).Trim();
            if (!long.TryParse(text, out var ticks)) return true;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > RetryAfter;
        }
        catch
        {
            return true;
        }
    }

    private static void MarkFailed(string dataDir)
    {
        try { File.WriteAllText(Path.Combine(dataDir, FailedMarker), DateTime.UtcNow.Ticks.ToString()); } catch { }
    }

    private static void ClearFailed(string dataDir)
    {
        try { File.Delete(Path.Combine(dataDir, FailedMarker)); } catch { }
    }

    // ------------------------------------------------------------------ 提示

    /// <summary>气泡通知（RitsuLib 的 toast），必须在主线程调用。</summary>
    private static void Toast(string title, string body)
    {
        try
        {
            Callable.From(() =>
            {
                try { RitsuToastService.ShowInfo(title, body, null); }
                catch { }
            }).CallDeferred();
        }
        catch
        {
            // 拿不到 UI 就算了，日志里有
        }
    }

    /// <summary>
    /// 小号 json 读写：只改一个键，其他结构原样保留。
    /// 和设置页用的是两份实现，但写的是同一批文件，冲突概率低（都是"读-改-写"瞬时完成）。
    /// </summary>
    private sealed class JsonFile
    {
        private readonly string _path;
        private readonly JsonObject _root;

        public JsonFile(string path)
        {
            _path = path;
            try
            {
                _root = File.Exists(path)
                    ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
            }
            catch
            {
                _root = new JsonObject();
            }
        }

        public string? GetString(string dotted)
        {
            try
            {
                var parts = dotted.Split('.');
                JsonNode? node = _root;
                foreach (var p in parts[..^1])
                {
                    node = node?[p];
                }
                return node?[parts[^1]]?.GetValue<string>();
            }
            catch
            {
                return null;
            }
        }

        public int GetInt(string dotted, int fallback)
        {
            try
            {
                var parts = dotted.Split('.');
                JsonNode? node = _root;
                foreach (var p in parts[..^1]) node = node?[p];
                return node?[parts[^1]] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public void SetString(string dotted, string value)
        {
            try
            {
                var parts = dotted.Split('.');
                JsonObject node = _root;
                foreach (var p in parts[..^1])
                {
                    if (node[p] is not JsonObject child)
                    {
                        child = new JsonObject();
                        node[p] = child;
                    }
                    node = child;
                }
                node[parts[^1]] = JsonValue.Create(value);
            }
            catch (Exception e)
            {
                BridgeLog.Warn($"[boot] 写 {Path.GetFileName(_path)} 失败: {e.Message}");
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
                File.WriteAllText(_path, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
                                         System.Environment.NewLine);
            }
            catch (Exception e)
            {
                BridgeLog.Warn($"[boot] 保存 {Path.GetFileName(_path)} 失败: {e.Message}");
            }
        }
    }
}
