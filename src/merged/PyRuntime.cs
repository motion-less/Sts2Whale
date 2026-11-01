using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;

namespace Sts2AgentBridge;

/// <summary>
/// 找一个能用的 python3。
///
/// 为什么需要：发布包不能带本机路径，所以 `vts.json` / `live.json` 里的 python 默认是空的。
/// 新玩家装完如果这里不兜住，就只剩一行「找不到 python：」——字幕有、声音没有，或者弹幕直接不启动。
///
/// 探测顺序：PATH 上的 python3 → 常见安装位置 → 用户自己 pip --user 装的位置。
/// 结果缓存，探测本身跑在调用方决定（TTS 在后台线程试，弹幕在启动时试一次）。
/// </summary>
internal static class PyRuntime
{
    private static readonly object Gate = new();
    private static string? _any;
    private static bool _anyProbed;
    private static readonly Dictionary<string, string?> ByModule = new(StringComparer.Ordinal);

    /// <summary>任意一个 python3（跑弹幕 daemon 用，只要标准库就够）。</summary>
    public static string? Any()
    {
        lock (Gate)
        {
            if (_anyProbed) return _any;
            _anyProbed = true;
            foreach (var py in Candidates())
            {
                if (Probe(py, null, 3000)) { _any = py; break; }
            }
            return _any;
        }
    }

    /// <summary>能 `import &lt;module&gt;` 的 python3（语音要 edge_tts）。</summary>
    public static string? WithModule(string module)
    {
        lock (Gate)
        {
            if (ByModule.TryGetValue(module, out var cached)) return cached;

            string? found = null;
            foreach (var py in Candidates())
            {
                if (Probe(py, module, 5000)) { found = py; break; }
            }
            ByModule[module] = found;
            return found;
        }
    }

    private static List<string> Candidates()
    {
        var list = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            // 系统装法（python.org 安装器）优先：%LOCALAPPDATA%\Programs\Python\Python3xx\python.exe
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            foreach (var root in roots)
            {
                try
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    foreach (var dir in Directory.GetDirectories(root, "Python3*").OrderByDescending(d => d, StringComparer.Ordinal))
                    {
                        var exe = Path.Combine(dir, "python.exe");
                        if (File.Exists(exe)) list.Add(exe);
                    }
                }
                catch { }
            }

            // Python 官方启动器（`py -3`）和 PATH 里的
            var launcher = Which("py.exe") ?? Which("py");
            if (launcher != null) list.Add(launcher);
            var winPy3 = Which("python3.exe") ?? Which("python3");
            if (winPy3 != null) list.Add(winPy3);
            var winPy = Which("python.exe") ?? Which("python");
            if (winPy != null) list.Add(winPy);
        }
        else
        {
            // 系统自带的最优先（/usr/bin/python3）
            list.Add("/usr/bin/python3");

            var onPath = Which("python3");
            if (onPath != null) list.Add(onPath);

            list.Add("/opt/homebrew/bin/python3");
            list.Add("/usr/local/bin/python3");
            list.Add("/usr/bin/python");

            // pip install --user 装在 ~/Library/Python/<ver>/bin 的情况
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var lib = Path.Combine(home, "Library", "Python");
                if (Directory.Exists(lib))
                {
                    foreach (var v in Directory.GetDirectories(lib))
                    {
                        var py = Path.Combine(v, "bin", "python3");
                        if (File.Exists(py)) list.Add(py);
                    }
                }
            }
            catch { }
        }

        // 去重，保持顺序（系统路径在前）
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>();
        foreach (var c in list)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            if (seen.Add(c)) unique.Add(c);
        }
        return unique;
    }

    private static bool Probe(string python, string? module, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python)) return false;
        try
        {
            var psi = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Windows 的 py 启动器需要显式指定主版本
            var name = Path.GetFileNameWithoutExtension(python);
            if (string.Equals(name, "py", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add("-3");

            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(module == null ? "import sys;print(sys.version_info[0])" : $"import {module}");
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            return proc.WaitForExit(timeoutMs) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? Which(string name)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }
        catch
        {
        }
        return null;
    }
}
