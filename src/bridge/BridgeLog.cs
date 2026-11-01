using System;
using System.IO;
using MegaCrit.Sts2.Core.Logging;
using STS2RitsuLib;

namespace Sts2AgentBridge;

/// <summary>
/// 双写日志：自己的 <c>agent_bridge.log</c>（方便 tail）+ RitsuLib 的 <see cref="Logger"/>（进游戏日志通道，
/// 玩家出问题时能连着游戏日志一起打包发过来）。
///
/// 游戏日志那一路是**附加**的：拿不到 Logger 就只写文件，绝不让日志本身把游戏搞崩。
/// </summary>
public static class BridgeLog
{
    private static readonly object Gate = new();
    private static string _path;
    private static Logger _gameLog;
    private static bool _gameLogOff;

    public static string Path
    {
        get
        {
            if (_path != null) return _path;
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2", "logs");
            Directory.CreateDirectory(dir);
            _path = System.IO.Path.Combine(dir, "agent_bridge.log");
            return _path;
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception e = null) => Write("ERROR", message, e);

    /// <summary>
    /// 启动早期 RitsuLib 可能还没就绪，那会儿创建 Logger 会失败。所以失败只记一个标记，
    /// 等桥接完全起来后调 <see cref="RetryGameLog"/> 再试一次——否则「进游戏日志」这条会静默失效。
    /// </summary>
    public static void RetryGameLog()
    {
        _gameLogOff = false;
        _gameLog = null;
    }

    /// <summary>RitsuLib 那本账；框架没起来或创建失败时返回 null，且不再重试刷屏。</summary>
    private static Logger GameLog()
    {
        if (_gameLog != null) return _gameLog;
        if (_gameLogOff) return null;
        try
        {
            _gameLog = RitsuLibFramework.CreateLogger("Sts2Whale", LogType.Generic);
        }
        catch
        {
            _gameLogOff = true;
        }
        return _gameLog;
    }

    private static void Write(string level, string message, Exception e)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}" +
                                         (e != null ? Environment.NewLine + e : string.Empty) + Environment.NewLine);
            }
        }
        catch
        {
            // logging must never break the game
        }

        try
        {
            var log = GameLog();
            if (log == null) return;
            var line = message + (e != null ? " :: " + e.GetType().Name + ": " + e.Message : string.Empty);
            switch (level)
            {
                case "ERROR": log.Error(line); break;
                case "WARN": log.Warn(line); break;
                default: log.Info(line); break;
            }
        }
        catch
        {
            // 游戏日志写失败也无所谓，文件那份才是主账
        }
    }
}
