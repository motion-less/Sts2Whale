using System;
using System.IO;
using MegaCrit.Sts2.Core.Logging;
using STS2RitsuLib;

namespace Sts2AgentBridge;

/// <summary>
/// 双写日志：自己的 <c>agent_bridge.log</c>（方便 tail）+ RitsuLib 的 <see cref="Logger"/>（进游戏日志通道，
/// 玩家出问题时能连着游戏日志一起打包发过来）。
///
/// 文件那本账按 8 MB 轮转：写满就把当前文件改名成 <c>.1</c>（<c>.1</c> → <c>.2</c>，更旧的删掉），
/// 只留两份历史 —— 长期直播也不会长成一个几百 MB 的大文件。
///
/// 游戏日志那一路是**附加**的：拿不到 Logger 就只写文件，绝不让日志本身把游戏搞崩。
/// </summary>
public static class BridgeLog
{
    private const long MaxBytes = 8L * 1024 * 1024;   // 单份上限 8 MB
    private const int KeepFiles = 2;                  // 历史份数：.1 / .2

    private static readonly object Gate = new();
    private static string _path;
    private static Logger _gameLog;
    private static bool _gameLogOff;
    private static long _bytes = -1;                  // -1 = 还没量过

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
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}" +
                   (e != null ? Environment.NewLine + e : string.Empty) + Environment.NewLine;
        try
        {
            lock (Gate)
            {
                if (_bytes < 0) _bytes = File.Exists(Path) ? new FileInfo(Path).Length : 0;
                var size = System.Text.Encoding.UTF8.GetByteCount(text);
                if (_bytes + size > MaxBytes)
                {
                    RotateOwned();
                    _bytes = 0;
                }
                File.AppendAllText(Path, text);
                _bytes += size;
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

    /// <summary>调用方已持有 <see cref="Gate"/>。轮转失败就把计数归零继续往老文件写，下次再说。</summary>
    private static void RotateOwned()
    {
        try { RotateFiles(Path, KeepFiles); }
        catch { }
    }

    /// <summary>
    /// `path` → `path.1` → `path.2`（更旧的删掉）。单独抽出来便于测试，
    /// 也保证「删/改名」的顺序不会把最新那份覆盖掉。
    /// </summary>
    private static void RotateFiles(string path, int keep)
    {
        var oldest = $"{path}.{keep}";
        if (File.Exists(oldest)) File.Delete(oldest);

        for (var i = keep - 1; i >= 1; i--)
        {
            var source = $"{path}.{i}";
            if (File.Exists(source)) File.Move(source, $"{path}.{i + 1}", overwrite: true);
        }

        if (File.Exists(path)) File.Move(path, path + ".1", overwrite: true);
    }
}
