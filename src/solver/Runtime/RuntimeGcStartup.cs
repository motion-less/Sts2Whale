using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal static class RuntimeGcStartup
{
    internal static string Status { get; private set; } = "Unchanged";

    internal static void Prepare(bool enabled)
    {
        // Freeze actual startup state before touching the file for the next run.
        _ = RuntimeGcProfile.Current;
        if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(RuntimeGcProfile.EnvironmentVariable))
            || (DisplayServer.GetName() == "headless"
                && System.Environment.GetEnvironmentVariable("COMBATSOLVER_TEST_AUTO_GC_CONFIG") != "1"))
        {
            Status = "Skipped";
            return;
        }
        string assemblyPath = typeof(NGame).Assembly.Location;
        string executablePath = OS.GetExecutablePath();
        if (string.IsNullOrWhiteSpace(assemblyPath) || string.IsNullOrWhiteSpace(executablePath)
            || !Path.IsPathFullyQualified(assemblyPath) || !Path.IsPathFullyQualified(executablePath)
            || Path.GetDirectoryName(executablePath) is not { Length: > 0 } executableDirectory)
        {
            Status = "Failed";
            Entry.Logger.Warn("[CombatSolver] RUNTIME_GC_STARTUP failed: game startup path is unavailable.");
            return;
        }
        if (Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) is not { Length: > 0 } assemblyDirectory
            || !IsAssemblyInsideGameInstall(executableDirectory, assemblyDirectory))
        {
            Status = "Failed";
            Entry.Logger.Warn(
                "[CombatSolver] RUNTIME_GC_STARTUP failed: game assembly is outside the game install directory " +
                $"(exe_dir={Path.GetFullPath(executableDirectory)}, assembly={Path.GetFullPath(assemblyPath)}).");
            return;
        }
        string path = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        try
        {
            Status = RuntimeGcStartupConfig.Apply(path, enabled);
            // 这条走桥接日志：求解器自己的日志会在战斗结束时被清掉，而 GC 配置到底有没有生效
            // 必须能在 agent_bridge.log 里直接查到（之前就因为文件被还原成「无 GC 键」而误判过）。
            Sts2AgentBridge.BridgeLog.Info(
                $"[gc] enabled={enabled} status={Status} heapCount={RuntimeGcStartupConfig.ResolveHeapCount()} " +
                $"isServerGc={System.Runtime.GCSettings.IsServerGC}");
            Entry.Logger.Info(
                $"[CombatSolver] RUNTIME_GC_STARTUP status={Status} next_launch_enabled={enabled} config={path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Status = "Failed";
            Entry.Logger.Warn($"[CombatSolver] RUNTIME_GC_STARTUP failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 程序集是否在「游戏安装目录」里（只有这种路径才允许改它的 runtimeconfig）。
    ///
    /// Windows / Linux：<c>sts2.dll</c> 在可执行文件旁边或它的子目录里。
    /// macOS .app：可执行文件在 <c>Contents/MacOS</c>，程序集在 <c>Contents/Resources/data_…/</c>，
    /// 两者是**兄弟目录**。旧实现看的是「相对可执行文件目录」是否以 <c>..</c> 开头，
    /// macOS 上永远是 <c>../Resources/…</c>，于是 <c>autoConfigureServerGc</c> 一次都没生效
    /// （日志：game assembly is outside the executable directory）。这里改成按安装根判断：
    /// 普通布局用可执行文件目录，macOS .app 布局额外允许 <c>Contents/</c> 下的内容，其他位置仍然拒绝。
    /// </summary>
    internal static bool IsAssemblyInsideGameInstall(string executableDirectory, string assemblyDirectory)
    {
        if (string.IsNullOrWhiteSpace(executableDirectory) || string.IsNullOrWhiteSpace(assemblyDirectory))
            return false;

        string exeDirectory = Path.GetFullPath(executableDirectory);
        string asmDirectory = Path.GetFullPath(assemblyDirectory);
        if (IsSameOrInside(asmDirectory, exeDirectory)) return true;

        string installRoot = Path.GetDirectoryName(exeDirectory) ?? exeDirectory;
        bool macAppBundle = OperatingSystem.IsMacOS()
            && string.Equals(Path.GetFileName(exeDirectory), "MacOS", StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(installRoot), "Contents", StringComparison.Ordinal);
        return macAppBundle && IsSameOrInside(asmDirectory, installRoot);
    }

    private static bool IsSameOrInside(string path, string root)
    {
        // macOS 默认文件系统不区分大小写；Windows 也是。Linux 保持区分。
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(path, root, comparison)) return true;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }
}
