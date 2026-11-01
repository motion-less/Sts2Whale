using System;
using System.IO;
using Godot;

namespace Sts2AgentBridge;

/// <summary>
/// 两套目录，别混：
///
/// <list type="bullet">
/// <item><b>ModDir</b> —— dll 所在目录。工坊里 Steam 会覆盖/清理它，**只能当只读资源**：
///   两个 python 脚本、assets、随包模板。</item>
/// <item><b>DataDir</b> —— 用户可写的配置目录：
///   <c>~/Library/Application Support/SlayTheSpire2/Sts2Whale/</c>（Windows 在 %APPDATA% 下）。
///   llm.json / vts.json / live.json / persona.md / vts_token.json / 本局记忆都在这。</item>
/// </list>
///
/// 首次运行会把 ModDir 里的用户文件搬一份到 DataDir（老用户从 0.10 升级上来配置不丢）。
/// 之后只认 DataDir —— 工坊更新不会冲掉玩家的配置。
/// </summary>
public static class ModPaths
{
    /// <summary>dll 所在目录（只读资源）。</summary>
    public static string ModDir { get; private set; } = "";

    /// <summary>用户可写目录（配置与记忆）。</summary>
    public static string DataDir { get; private set; } = "";

    /// <summary>首次运行时需要从 ModDir 搬到 DataDir 的文件。</summary>
    private static readonly string[] UserFiles =
    {
        "llm.json",
        "vts.json",
        "live.json",
        "persona.md",
        "vts_token.json",
        "run_memory.json",
        "run_history.jsonl",
    };

    private static bool _initialized;

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        ModDir = Path.GetDirectoryName(typeof(ModPaths).Assembly.Location) ?? ".";

        var data = "";
        try
        {
            var userData = OS.GetUserDataDir();
            if (!string.IsNullOrWhiteSpace(userData))
                data = Path.Combine(userData, "Sts2Whale");
        }
        catch
        {
            // Godot 没准备好就退回 mod 目录
        }

        if (string.IsNullOrWhiteSpace(data))
            data = Path.Combine(ModDir, "userdata");

        try
        {
            Directory.CreateDirectory(data);
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"[paths] 建不了数据目录 {data}，退回 mod 目录: {e.Message}");
            data = ModDir;
        }

        DataDir = data;
        BridgeLog.Info($"[paths] mod={ModDir} data={DataDir}");
        Migrate();
    }

    /// <summary>DataDir 下的文件路径。</summary>
    public static string Data(string fileName) => System.IO.Path.Combine(DataDir, fileName);

    /// <summary>ModDir 下的只读资源路径（python 脚本等）。</summary>
    public static string Script(string fileName) => System.IO.Path.Combine(ModDir, fileName);

    /// <summary>把 ModDir 里的用户文件搬到 DataDir（只补缺，不覆盖已有配置）。</summary>
    private static void Migrate()
    {
        if (string.Equals(DataDir, ModDir, StringComparison.Ordinal)) return;

        foreach (var name in UserFiles)
        {
            try
            {
                var dst = System.IO.Path.Combine(DataDir, name);
                var src = System.IO.Path.Combine(ModDir, name);
                if (File.Exists(dst) || !File.Exists(src)) continue;
                File.Copy(src, dst);
                BridgeLog.Info($"[paths] 迁移 {name} → {DataDir}");
            }
            catch (Exception e)
            {
                BridgeLog.Warn($"[paths] 迁移 {name} 失败: {e.Message}");
            }
        }
    }
}
