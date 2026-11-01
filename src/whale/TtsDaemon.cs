using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sts2AgentBridge;

namespace Sts2Whale.Whale;

/// <summary>
/// 常驻 TTS 服务的生命周期管理：游戏起来就拉，游戏退出就收。
///
/// 三道保险：
///   1. 启动前先探活（端口能应答就复用，不重复拉）
///   2. 拉起来时传 --parent 游戏 PID，游戏被强杀时 daemon 自己退出
///   3. 进程退出事件里再收一次，正常退出不留尾巴
///
/// 线程约定：<b>Start 绝不阻塞调用线程</b>。以前它在主线程上同步探活 + 轮询
/// 最多 15 秒，开局会白卡好几秒；现在整体丢到线程池，说话前用
/// <see cref="EnsureAlive"/> 等它/重拉。
/// </summary>
internal static class TtsDaemon
{
    private static Process? _process;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly object Gate = new();

    private static string _python = "";
    private static string _script = "";
    private static int _idle = 30;
    private static bool _starting;

    public static int Port { get; private set; } = 48765;
    public static bool Ready { get; private set; }

    public static string BaseUrl => $"http://127.0.0.1:{Port}";

    public static void Start(string python, string script, int port, int idleMinutes)
    {
        lock (Gate)
        {
            Port = port;
            _python = python ?? "";
            _script = script ?? "";
            _idle = idleMinutes;
        }

        // 主线程只负责下单，探活/探测/拉起全在后台
        Task.Run(() =>
        {
            try { StartCore(); }
            catch (Exception e) { BridgeLog.Warn("[tts] 启动 daemon 失败: " + e.Message); }
        });
    }

    private static void StartCore()
    {
        lock (Gate)
        {
            if (_starting) return;
            _starting = true;
        }

        try
        {
            if (IsAlive())
            {
                Ready = true;
                BridgeLog.Info($"[tts] 复用已在运行的 daemon（{Port}）");
                return;
            }

            var script = _script;
            var python = _python;
            if (!File.Exists(script))
            {
                BridgeLog.Warn($"[tts] 找不到 daemon 脚本：{script}");
                return;
            }

            // 没配 python（新装的默认就是这样）：后台找一个装了 edge_tts 的解释器，
            // 别在主线程上跑探测，也别因为找不到就静默不出声
            if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
            {
                BridgeLog.Info("[tts] 没配置 python，开始自动探测…");
                var found = PyRuntime.WithModule("edge_tts");
                if (found == null)
                {
                    BridgeLog.Warn("[tts] 没找到装了 edge_tts 的 python，语音关闭（字幕照常）。" +
                                   "装法：pip install edge-tts，或在设置页填 python 路径");
                    return;
                }
                BridgeLog.Info($"[tts] 自动选中 python：{found}");
                python = found;
            }

            Launch(python, script, Port, _idle);
        }
        finally
        {
            lock (Gate) _starting = false;
        }
    }

    private static void Launch(string python, string script, int port, int idleMinutes)
    {
        try
        {
            var psi = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
            psi.ArgumentList.Add("--parent"); psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("--idle"); psi.ArgumentList.Add(idleMinutes.ToString());

            _process = Process.Start(psi);
            if (_process == null) return;

            // 日志别丢，写到临时文件（读不完的管道会把子进程卡死）
            var logPath = Path.Combine(Path.GetTempPath(), "sts2whale-tts-daemon.log");
            _process.OutputDataReceived += (_, e) => AppendLog(logPath, e.Data);
            _process.ErrorDataReceived += (_, e) => AppendLog(logPath, e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // 等它起来（预热要几秒）
            for (var i = 0; i < 60; i++)
            {
                if (IsAlive())
                {
                    Ready = true;
                    BridgeLog.Info($"[tts] daemon 已就绪（{port}，pid {_process.Id}）");
                    return;
                }
                if (_process.HasExited) break;   // 起不来就别傻等 15 秒
                Thread.Sleep(250);
            }
            BridgeLog.Warn("[tts] daemon 没能起来，先走命令行兜底");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[tts] 启动 daemon 失败: " + e.Message);
        }
    }

    private static void AppendLog(string path, string? line)
    {
        if (line == null) return;
        try { File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + line + "\n"); } catch { }
    }

    public static bool IsAlive()
    {
        try
        {
            using var resp = Http.GetAsync(BaseUrl + "/health").GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 说话前调用：daemon 活着就直接说；掉了（空闲自退 / 崩溃 / 首次还没起来）就拉一次并等它就绪。
    /// 只会在语音线程上调用，不会卡主线程。
    /// </summary>
    public static bool EnsureAlive(int timeoutMs = 15000)
    {
        if (Ready && IsAlive()) return true;

        Ready = false;
        StartCore();          // 同步拉一次；内部用 _starting 去重，别的线程正在拉就直接返回

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Ready) return true;

            bool starting;
            lock (Gate) starting = _starting;
            if (!starting)
            {
                // 拉起流程已经结束：要么成功（Ready），要么就是没装 python 之类的硬失败
                if (IsAlive()) { Ready = true; return true; }
                return false;
            }
            Thread.Sleep(200);
        }
        return Ready || IsAlive();
    }

    public static void Stop()
    {
        try
        {
            using var resp = Http.PostAsync(BaseUrl + "/shutdown", new StringContent("{}", Encoding.UTF8, "application/json"))
                                .GetAwaiter().GetResult();
        }
        catch { }

        try
        {
            if (_process is { HasExited: false } && !_process.WaitForExit(2000)) _process.Kill();
        }
        catch { }

        _process = null;
        Ready = false;
        BridgeLog.Info("[tts] daemon 已停止");
    }
}
