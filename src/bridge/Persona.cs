using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sts2AgentBridge;

/// <summary>
/// 人设卡：直接读 mod 目录里的本地文件（<c>persona.md</c>，其次 <c>assets/persona.md</c>）。
///
/// 文件是唯一事实来源 —— 改文件、<c>POST /persona/reload</c> 就生效，不用重编、不用重启。
/// 这张卡只管「鲸鱼怎么说话」，不参与任何决策逻辑。
/// </summary>
public static class Persona
{
    private const string FileName = "persona.md";
    private static readonly object Gate = new();

    private static string _text;
    private static string _loadedPath;
    private static DateTime _loadedWriteTime;

    /// <summary>期望的路径（找不到时也返回首选路径，方便报错里带出来）。</summary>
    public static string ResolvePath()
    {
        var dir = Path.GetDirectoryName(typeof(Persona).Assembly.Location) ?? "";
        var data = Path.Combine(ModPaths.DataDir, FileName);          // 玩家自己改的那份
        if (File.Exists(data)) return data;
        var root = Path.Combine(dir, FileName);                       // 随包模板
        if (File.Exists(root)) return root;
        var assets = Path.Combine(dir, "assets", FileName);
        if (File.Exists(assets)) return assets;
        return data;
    }

    /// <summary>人设正文；文件不存在或读不出来时返回 null（调用方自己决定怎么降级）。</summary>
    public static string Text
    {
        get
        {
            lock (Gate)
            {
                if (_text == null) Load();
                // 文件被改过就自动重读，省得每次都要 reload
                TryAutoReload();
                return _text;
            }
        }
    }

    public static Dictionary<string, object> Snapshot(bool includeText = true)
    {
        lock (Gate)
        {
            if (_text == null) Load();
            TryAutoReload();

            var path = ResolvePath();
            var d = new Dictionary<string, object>
            {
                ["ok"] = _text != null,
                ["path"] = path,
                ["exists"] = File.Exists(path),
                ["name"] = "DeepSeek 小鲸鱼（打牌搭子）",
                ["chars"] = _text?.Length ?? 0,
                ["note"] = _text == null
                    ? "没读到人设卡：把 persona.md 放在 mod 目录（和 dll 同级）或 assets/ 下。"
                    : "这张卡只管语气，不影响决策。改完 POST /persona/reload 生效。",
            };
            if (includeText && _text != null) d["text"] = _text;
            return d;
        }
    }

    /// <summary>从人设卡里解析出的「冷场垫话」：位于含「垫话」的小标题下、每行以 "- " 开头的短句。</summary>
    public static IReadOnlyList<string> Fillers
    {
        get
        {
            var text = Text;
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();

            var lines = new List<string>();
            var inside = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("## "))
                {
                    inside = line.Contains("垫话");
                    continue;
                }
                if (!inside) continue;
                if (!line.StartsWith("- ")) continue;
                var item = line.Substring(2).Trim();
                if (item.Length > 0) lines.Add(item);
            }
            return lines;
        }
    }

    /// <summary>强制重读。</summary>
    public static Dictionary<string, object> Reload()
    {
        lock (Gate)
        {
            _text = null;
            _loadedPath = null;
            Load();
            var d = Snapshot();
            d["reloaded"] = true;
            return d;
        }
    }

    private static void Load()
    {
        try
        {
            var path = ResolvePath();
            if (!File.Exists(path))
            {
                _text = null;
                BridgeLog.Warn("persona missing at " + path);
                return;
            }

            var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(text))
            {
                _text = null;
                BridgeLog.Warn("persona empty at " + path);
                return;
            }

            _text = text.Trim();
            _loadedPath = path;
            _loadedWriteTime = File.GetLastWriteTimeUtc(path);
            BridgeLog.Info($"persona loaded: {path} ({_text.Length} chars)");
        }
        catch (Exception e)
        {
            _text = null;
            BridgeLog.Warn("persona load failed: " + e.Message);
        }
    }

    private static void TryAutoReload()
    {
        if (_text == null || _loadedPath == null) return;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(_loadedPath);
            if (stamp != _loadedWriteTime)
            {
                _text = null;
                Load();
            }
        }
        catch { }
    }
}
