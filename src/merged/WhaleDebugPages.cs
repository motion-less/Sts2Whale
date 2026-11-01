using System;
using System.Text;
using System.Text.Json;
using Godot;
using Sts2AgentBridge;
using STS2RitsuLib.Diagnostics.DebugTools;
using STS2RitsuLib.Settings;

namespace Sts2Whale;

/// <summary>
/// 把桥接的消息流挂到 RitsuLib 的「Debug Tools」里，不再自己搭一套悬浮窗。
///
/// 数据还是 <see cref="ConsolePage"/> 那份（纯内存、最近 200 条），这里只负责画：
/// 一个刷新按钮 + 一块可滚动的等宽文本。挂上去之后游戏自带的调试入口就能看到。
/// </summary>
internal static class WhaleDebugPages
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        try
        {
            RitsuDebugToolsPageRegistry.Register(new RitsuDebugToolsPageDefinition(
                "Sts2Whale",
                "bridge-console",
                ModSettingsText.Literal("鲸鱼桥接"),
                Build)
            {
                SortOrder = 120,
                PreferredWidthFraction = 1f,
            });
            BridgeLog.Info("[debug] 桥接控制台已挂到 RitsuLib Debug Tools");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("[debug] Debug Tools 页注册失败: " + e.Message);
        }
    }

    private static Control Build(RitsuDebugToolsPageContext ctx)
    {
        var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 6);

        var bar = new HBoxContainer();
        var refresh = new Button { Text = "刷新" };
        var hint = new Label { Text = "最近 200 条 · 只在内存里，不落盘", Modulate = new Color(1, 1, 1, 0.6f) };
        bar.AddChild(refresh);
        bar.AddChild(hint);
        root.AddChild(bar);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var text = new RichTextLabel
        {
            BbcodeEnabled = false,
            FitContent = true,
            SelectionEnabled = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        scroll.AddChild(text);
        root.AddChild(scroll);

        void Refresh()
        {
            try { text.Text = Render(); }
            catch (Exception e) { text.Text = "渲染失败: " + e.Message; }
        }

        refresh.Pressed += Refresh;
        Refresh();
        return root;
    }

    private static string Render()
    {
        var json = ConsolePage.Since(0);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return "（还没有消息）";

        var sb = new StringBuilder();
        var n = 0;
        foreach (var e in events.EnumerateArray())
        {
            n++;
            var ok = e.TryGetProperty("status", out var st) && st.TryGetInt32(out var code) && code is >= 200 and < 300;
            sb.Append(Str(e, "t")).Append("  ")
              .Append(Str(e, "method")).Append(' ')
              .Append(Str(e, "path")).Append("  ")
              .Append(ok ? "✓" : "✗").Append(' ')
              .Append(Str(e, "status")).Append("  ")
              .Append(Str(e, "ms")).Append("ms\n");
            AppendBody(sb, "→ ", Str(e, "body"));
            AppendBody(sb, "← ", Str(e, "res"));
        }

        return n == 0 ? "（还没有消息）" : sb.ToString();
    }

    private static void AppendBody(StringBuilder sb, string arrow, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (value.Length > 240) value = value[..240] + "…";
        sb.Append("    ").Append(arrow).Append(value).Append('\n');
    }

    private static string Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Null => "",
            _ => v.GetRawText(),
        };
    }
}
