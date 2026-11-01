using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using Sts2AgentBridge;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Ui.Toast;
using STS2RitsuLib.Utils.Persistence;

namespace Sts2Whale;

/// <summary>
/// 把三个 json 配置（llm / vts / live）镜像成游戏里的设置页，玩家不用再手改文件。
///
/// 设计原则：**文件仍然是唯一真相**。设置页只是读写同一批 json，所以
///   * 老玩家手里的配置原样有效（不需要迁移）；
///   * 手改 json 仍然能用（下次打开设置页会读到新值）；
///   * 设置页崩了也只是少了界面，不影响 mod 本身。
///
/// 副作用：改完 llm 的项会顺手调 <see cref="WhaleBrain.Reload"/>，模型配置立即生效；
/// 皮套相关的项要重启游戏才生效（连接在启动时建立）；弹幕只有房间号是即时切换的，其余同样要重启。
/// </summary>
internal static class WhaleSettings
{
    private const string ModId = "Sts2Whale";

    private static bool _installed;
    private static JsonConfig? _llm;
    private static JsonConfig? _vts;
    private static JsonConfig? _live;

    public static void Install(string dir)
    {
        if (_installed) return;
        _installed = true;

        try
        {
            _llm = new JsonConfig(dir, "llm.json");
            _vts = new JsonConfig(dir, "vts.json");
            _live = new JsonConfig(dir, "live.json");

            RitsuLibFramework.RegisterModSettings(ModId, Build);
            BridgeLog.Info("[settings] 设置页已注册（模型 / 皮套 / 直播）");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[settings] 设置页注册失败（mod 其余功能不受影响）: " + e.Message);
        }
    }

    private static void Build(ModSettingsPageBuilder page)
    {
        var sections = 0;
        page.WithTitle(ModSettingsText.Literal("鲸鱼爬塔"))
            .WithDescription(ModSettingsText.Literal("战斗求解器、战略助手、鲸鱼娘的连接参数。改完即时写入 mod 目录下的 json。"))
            .WithModDisplayName(ModSettingsText.Literal("鲸鱼爬塔"));

        if (_llm != null)
        {
            sections++;
            page.AddSection("llm", s => s
                .WithTitle(ModSettingsText.Literal("鲸鱼娘的嘴（模型）"))
                .WithDescription(ModSettingsText.Literal("DeepSeek 兼容接口。改完立即生效，不用重启。"))
                .AddToggle("llm.enabled", ModSettingsText.Literal("启用模型解说"),
                    BoolBind(_llm, "llm.enabled", "enabled", true, ReloadBrain),
                    ModSettingsText.Literal("关掉之后鲸鱼娘只用本地模板说话，不联网。"))
                .AddString("llm.baseUrl", ModSettingsText.Literal("接口地址"),
                    StringBind(_llm, "llm.baseUrl", "baseUrl", "https://api.deepseek.com", ReloadBrain),
                    ModSettingsText.Literal("https://api.deepseek.com"))
                .AddString("llm.model", ModSettingsText.Literal("模型名"),
                    StringBind(_llm, "llm.model", "model", "deepseek-flash", ReloadBrain),
                    ModSettingsText.Literal("deepseek-flash"))
                .AddCustom("llm.apiKey", ModSettingsText.Literal("API Key"),
                    _ => BuildApiKeyRow(),
                    ModSettingsText.Literal("只存在你本机的 llm.json。输入框始终以圆点显示，界面上不会出现明文。"))
                .AddIntSlider("llm.maxTokens", ModSettingsText.Literal("单句最大 token"),
                    IntBind(_llm, "llm.maxTokens", "maxTokens", 800, ReloadBrain),
                    128, 4096, 64, v => v.ToString(),
                    ModSettingsText.Literal("人设要求一句话 8~16 字，800 就够；推理型模型要调大。"))
                .AddIntSlider("llm.minGapMs", ModSettingsText.Literal("两次调用的最小间隔（毫秒）"),
                    IntBind(_llm, "llm.minGapMs", "minGapMs", 10000, ReloadBrain),
                    2000, 60000, 1000, v => $"{v} ms（{v / 1000.0:0.#} 秒）",
                    ModSettingsText.Literal("默认 10000 = 每 10 秒最多问一次模型。调大更省钱，调小更话痨。")))
            ;
        }

        // 角色卡：改「怎么说话」，不影响「怎么决策」
        sections++;
        page.AddSection("persona", s2 => s2
            .WithTitle(ModSettingsText.Literal("角色卡（鲸鱼娘怎么说话）"))
            .WithDescription(ModSettingsText.Literal("决定她的口吻和说话方式，不影响求解器/战略助手的决策。保存后立即生效。"))
            .AddCustom("persona.edit", ModSettingsText.Literal("人设正文"),
                _ => BuildPersonaEditor(),
                ModSettingsText.Literal("Markdown。改完点「保存」；改坏了点「恢复默认」拿回包里那份。")));

        if (_vts != null)
        {
            sections++;
            page.AddSection("vts", s => s
                .WithTitle(ModSettingsText.Literal("皮套（VTube Studio）"))
                .WithDescription(ModSettingsText.Literal("连接地址和基础开关。**改完要重启游戏**才生效。"))
                .AddToggle("vts.enabled", ModSettingsText.Literal("启用皮套联动"),
                    BoolBind(_vts, "vts.enabled", "enabled", true),
                    ModSettingsText.Literal("关掉之后鲸鱼娘只在游戏里说话，不动皮套。"))
                .AddString("vts.wsUrl", ModSettingsText.Literal("VTS 地址"),
                    StringBind(_vts, "vts.wsUrl", "wsUrl", "ws://127.0.0.1:8001"),
                    ModSettingsText.Literal("ws://127.0.0.1:8001"))
                .AddToggle("vts.idleSway", ModSettingsText.Literal("待机摆头"),
                    BoolBind(_vts, "vts.idleSway", "idleSway", true),
                    ModSettingsText.Literal("开了摄像头面捕的话建议关掉。")))
            ;
        }

        if (_live != null)
        {
            sections++;
            page.AddSection("live", s => s
                .WithTitle(ModSettingsText.Literal("直播弹幕"))
                .WithDescription(ModSettingsText.Literal("mod 会自己拉起一个无头浏览器打开弹幕页（需要 Chrome 或 Edge），" +
                                                       "挑提问和关键词回一句。房间号改完即时切换；限流等其他项改完要重启游戏。"))
                .AddToggle("live.enabled", ModSettingsText.Literal("启用弹幕互动"),
                    BoolBind(_live, "live.enabled", "enabled", true, after: () =>
                    {
                        try
                        {
                            if (_live?.GetBool("enabled", true) == true) DanmakuBridge.EnsureRunning();
                        }
                        catch { }
                    }))
                .AddString("live.roomId", ModSettingsText.Literal("房间号"),
                    StringBind(_live, "live.roomId", "roomId", "", after: RetargetDanmaku),
                    ModSettingsText.Literal("B 站直播间号。改完约 1 秒自动切换，不用重启游戏。"))
                .AddCustom("live.url", ModSettingsText.Literal("弹幕页链接"),
                    _ => BuildUrlRow(),
                    ModSettingsText.Literal("填好房间号后点「打开弹幕页」——mod 会用一个带调试端口的独立 profile 打开它，" +
                                           "之后弹幕就从这一页读。换了房间号先点「刷新」。"))
                .AddIntSlider("live.maxPerMinute", ModSettingsText.Literal("每分钟最多回几句"),
                    IntBind(_live, "live.maxPerMinute", "maxPerMinute", 3),
                    1, 10, 1, v => v.ToString(),
                    ModSettingsText.Literal("默认 3。开太大会被平台当机器人。")))
            ;
        }

        // RitsuLib 要求至少一个 section，否则 Build 会抛。三个配置都读不到时给一张说明卡，
        // 别让「设置页打不开」变成玩家的第一印象。
        if (sections == 0)
        {
            page.AddSection("broken", s => s
                .WithTitle(ModSettingsText.Literal("配置读不到"))
                .AddInfoCard("broken.info",
                    ModSettingsText.Literal("llm.json / vts.json / live.json 都没读出来"),
                    ModSettingsText.Literal("mod 仍在运行"),
                    ModSettingsText.Literal("直接编辑 mod 目录下的三个 json 文件即可；改完重启游戏。")));
        }
    }

    // ------------------------------------------------------------------ API Key

    /// <summary>
    /// API Key 专用行：输入框 <c>Secret = true</c>（圆点显示），且**不回填已保存的值** ——
    /// 界面上不出现明文，连控件里都不放。只提示「已配置 / 未配置」和长度。
    /// </summary>
    private static Control BuildApiKeyRow()
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 4);

        var row = new HBoxContainer();
        var edit = new LineEdit
        {
            Secret = true,                       // 圆点，不信手能看到
            PlaceholderText = "sk-...（输入后点保存）",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        var save = new Button { Text = "保存" };
        var clear = new Button { Text = "清空" };
        var status = new Label { Text = "" };
        status.Modulate = new Color(1, 1, 1, 0.65f);

        void ShowState(string prefix)
        {
            try
            {
                var key = _llm?.GetString("apiKey", "") ?? "";
                status.Text = key.Length == 0
                    ? prefix + "未配置（只用本地垫话）"
                    : prefix + $"已配置（{key.Length} 位）";
            }
            catch { status.Text = prefix; }
        }

        save.Pressed += () =>
        {
            try
            {
                var key = (edit.Text ?? "").Trim();
                if (key.Length == 0) { ShowState("输入框是空的，没有改动。"); return; }
                _llm?.SetString("apiKey", key);
                _llm?.Save();
                edit.Text = "";                  // 存完立刻清空，不留在控件里
                ReloadBrain();
                ShowState("已保存。");
                BridgeLog.Info("[settings] API Key 已更新");
            }
            catch (Exception e)
            {
                status.Text = "保存失败：" + e.Message;
            }
        };

        clear.Pressed += () =>
        {
            try
            {
                _llm?.SetString("apiKey", "");
                _llm?.Save();
                edit.Text = "";
                ReloadBrain();
                ShowState("已清空。");
                BridgeLog.Info("[settings] API Key 已清空");
            }
            catch (Exception e)
            {
                status.Text = "清空失败：" + e.Message;
            }
        };

        row.AddChild(edit);
        row.AddChild(save);
        row.AddChild(clear);
        box.AddChild(row);
        box.AddChild(status);
        ShowState("");
        return box;
    }

    // ------------------------------------------------------------------ 角色卡

    private static string ReadPersonaText()
    {
        try
        {
            var path = ModPaths.Data("persona.md");
            if (File.Exists(path)) return File.ReadAllText(path);
            var template = ModPaths.Script("persona.md");
            if (File.Exists(template)) return File.ReadAllText(template);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[settings] 读 persona.md 失败: " + e.Message);
        }
        return "";
    }

    /// <summary>多行编辑器 + 保存 / 恢复默认。保存后立刻让模型重读，不用重启游戏。</summary>
    private static Control BuildPersonaEditor()
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);

        var edit = new TextEdit
        {
            Text = ReadPersonaText(),
            CustomMinimumSize = new Vector2(0, 280),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            PlaceholderText = "（空的话就用包里自带的角色卡）",
        };

        var bar = new HBoxContainer();
        var save = new Button { Text = "保存" };
        var reset = new Button { Text = "恢复默认" };
        var status = new Label { Text = "" };
        status.Modulate = new Color(1, 1, 1, 0.65f);

        save.Pressed += () =>
        {
            try
            {
                var path = ModPaths.Data("persona.md");
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path, edit.Text);
                WhaleBrain.Reload();          // 让模型立刻用新人设
                status.Text = "已保存，下一句就用新的人设";
                BridgeLog.Info("[settings] 角色卡已保存");
            }
            catch (Exception e)
            {
                status.Text = "保存失败：" + e.Message;
            }
        };

        reset.Pressed += () =>
        {
            try
            {
                var template = ModPaths.Script("persona.md");
                if (!File.Exists(template)) { status.Text = "包里没有默认角色卡"; return; }
                var path = ModPaths.Data("persona.md");
                File.Copy(template, path, true);
                edit.Text = File.ReadAllText(path);
                WhaleBrain.Reload();
                status.Text = "已恢复默认";
            }
            catch (Exception e)
            {
                status.Text = "恢复失败：" + e.Message;
            }
        };

        bar.AddChild(save);
        bar.AddChild(reset);
        bar.AddChild(status);

        box.AddChild(edit);
        box.AddChild(bar);
        return box;
    }

    // ------------------------------------------------------------------ 弹幕页链接

    /// <summary>
    /// 「打开弹幕页」按钮：用带调试端口的独立 profile 打开弹幕页，然后让弹幕模块立刻去读。
    /// 失败时弹一条警告（多半是没装 Chrome / Edge）。
    /// </summary>
    private static void OpenDanmakuPage()
    {
        try
        {
            var room = CurrentRoomId();
            if (room <= 0)
            {
                Toast("先填房间号", "直播弹幕那段上面的「房间号」还没填。", true);
                return;
            }

            DanmakuBridge.EnsureRunning();
            var result = ChromeDanmaku.OpenPage(room);
            var ok = result.TryGetValue("ok", out var v) && v is bool b && b;
            if (!ok)
            {
                var err = result.TryGetValue("error", out var e) ? e?.ToString() ?? "" : "";
                Toast("打不开弹幕页", err, true);
                return;
            }

            var reused = result.TryGetValue("reused", out var r) && r is bool rb && rb;
            Toast("弹幕页已打开", reused ? "浏览器里已经有这一页了。" : "等页面加载完，弹幕就会开始读。", false);
            DanmakuBridge.RetargetDelayed(room);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[settings] 打开弹幕页失败: " + e.Message);
        }
    }

    /// <summary>从 live.json 读房间号。</summary>
    private static long CurrentRoomId()
    {
        try
        {
            var raw = _live?.GetString("roomId", "0") ?? "0";
            return long.TryParse(raw.Trim(), out var room) ? room : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>给玩家看的即时反馈；拿不到 UI 就只写日志。</summary>
    private static void Toast(string title, string body, bool warn)
    {
        try
        {
            if (warn) RitsuToastService.ShowWarning(title, body, null);
            else RitsuToastService.ShowInfo(title, body, null);
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"[settings] {title}: {body} ({e.Message})");
        }
    }

    /// <summary>房间号写盘后调用：让弹幕模块立刻换到新的弹幕页（内部有 800ms 防抖）。</summary>
    private static void RetargetDanmaku()
    {
        try
        {
            var raw = _live?.GetString("roomId", "0") ?? "0";
            if (long.TryParse(raw.Trim(), out var room) && room > 0)
                DanmakuBridge.RetargetDelayed(room);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[settings] 切换房间失败: " + e.Message);
        }
    }

    /// <summary>按当前房间号拼出弹幕页完整链接（房间号未填时给一句提示）。</summary>
    private static string CurrentObsUrl()
    {
        try
        {
            var raw = _live?.GetString("roomId", "0") ?? "0";
            if (long.TryParse(raw.Trim(), out var room) && room > 0)
                return DanmakuBridge.ObsUrl(room);
        }
        catch { }
        return "（先填房间号）";
    }

    /// <summary>只读文本框 + 刷新 / 复制：给玩家看 mod 实际打开的是哪一页，也方便自己排查。</summary>
    private static Control BuildUrlRow()
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 6);

        var edit = new LineEdit
        {
            Text = CurrentObsUrl(),
            Editable = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "完整链接，可选中复制",
        };
        var refresh = new Button { Text = "刷新" };
        var copy = new Button { Text = "复制" };
        var open = new Button { Text = "打开弹幕页" };

        refresh.Pressed += () => { try { edit.Text = CurrentObsUrl(); } catch { } };
        copy.Pressed += () =>
        {
            try { DisplayServer.ClipboardSet(edit.Text); } catch { }
        };
        open.Pressed += () => OpenDanmakuPage();

        row.AddChild(edit);
        row.AddChild(refresh);
        row.AddChild(copy);
        row.AddChild(open);
        return row;
    }

    // ------------------------------------------------------------------ 绑定工厂

    private static void ReloadBrain()
    {
        try { WhaleBrain.Reload(); } catch { }
    }

    private static IModSettingsValueBinding<bool> BoolBind(JsonConfig cfg, string dataKey, string key, bool fallback, Action? after = null)
        => ModSettingsBindings.Callback<bool>(ModId, dataKey,
            () => cfg.GetBool(key, fallback),
            v => cfg.SetBool(key, v),
            () => { cfg.Save(); after?.Invoke(); },
            SaveScope.Global);

    private static IModSettingsValueBinding<string> StringBind(JsonConfig cfg, string dataKey, string key, string fallback, Action? after = null)
        => ModSettingsBindings.Callback<string>(ModId, dataKey,
            () => cfg.GetString(key, fallback),
            v => cfg.SetString(key, v),
            () => { cfg.Save(); after?.Invoke(); },
            SaveScope.Global);

    private static IModSettingsValueBinding<int> IntBind(JsonConfig cfg, string dataKey, string key, int fallback, Action? after = null)
        => ModSettingsBindings.Callback<int>(ModId, dataKey,
            () => cfg.GetInt(key, fallback),
            v => cfg.SetInt(key, v),
            () => { cfg.Save(); after?.Invoke(); },
            SaveScope.Global);

    // ------------------------------------------------------------------ json 读写

    /// <summary>
    /// 一个 json 文件的轻量读写壳：整份读进来、改顶层键、整份写回去。
    /// 嵌套结构（vts.json 的 speech / emotions）因为整份保留，不会被设置页抹掉。
    /// </summary>
    private sealed class JsonConfig
    {
        private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

        private readonly object _gate = new();
        private readonly string _path;
        private JsonObject _root = new();

        public JsonConfig(string dir, string file)
        {
            _path = Path.Combine(dir, file);
            lock (_gate)
            {
                try
                {
                    _root = File.Exists(_path)
                        ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new JsonObject()
                        : new JsonObject();
                }
                catch
                {
                    _root = new JsonObject();
                }
            }
        }

        public bool GetBool(string key, bool fallback)
        {
            lock (_gate) return _root[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
        }

        public string GetString(string key, string fallback)
        {
            lock (_gate)
            {
                if (_root[key] is not JsonValue v) return fallback;
                if (v.TryGetValue<string>(out var s)) return s;
                // 数字型的键（比如 live.roomId）当字符串返回，设置页用文本框编辑
                return v.ToJsonString().Trim('"');
            }
        }

        public int GetInt(string key, int fallback)
        {
            lock (_gate) return _root[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;
        }

        public void SetBool(string key, bool value) { lock (_gate) _root[key] = JsonValue.Create(value); }

        public void SetString(string key, string value)
        {
            lock (_gate)
            {
                // 数值型键（roomId 这类以 Id 结尾的）始终写数字：
                // 别让设置页把 10000000 写成 "10000000"，读取方还得兼容字符串
                var numericKey = key.EndsWith("Id", StringComparison.Ordinal);
                if (numericKey && long.TryParse(value.Trim(), out var asLong))
                    _root[key] = JsonValue.Create(asLong);
                else if (_root[key] is JsonValue old && old.TryGetValue<int>(out _) && int.TryParse(value, out var asInt))
                    _root[key] = JsonValue.Create(asInt);
                else
                    _root[key] = JsonValue.Create(value);
            }
        }

        public void SetInt(string key, int value) { lock (_gate) _root[key] = JsonValue.Create(value); }

        public void Save()
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
                    File.WriteAllText(_path, _root.ToJsonString(Pretty) + System.Environment.NewLine);
                }
                catch (Exception e)
                {
                    BridgeLog.Warn("[settings] 写 " + Path.GetFileName(_path) + " 失败: " + e.Message);
                }
            }
        }
    }
}
