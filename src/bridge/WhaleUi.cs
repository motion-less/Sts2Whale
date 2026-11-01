using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Sts2AgentBridge;

/// <summary>
/// The little whale companion: a bottom-right avatar with a speech bubble. It shows whatever
/// short line the agent put into the command file.
/// </summary>
public static class WhaleUi
{
    private const float Margin = 0f;          // 贴窗口边缘
    private const float FontSize = 34f;       // 字幕字号上限
    private const int CharsPerLine = 40;      // 超过 40 个字就换行（不再截断）
    // 40 个字一行的基准宽度（实际宽度会按字号和字数再算）
    private const float BubbleWidth = CharsPerLine * FontSize + 36f;
    public const int DefaultDurationMs = 15000;

    private static CanvasLayer _layer;
    private static Control _root;
    private static PanelContainer _bubble;
    private static Label _label;
    private static Vector2 _bubbleNorm = new(-1, -1); // 归一化位置，-1 = 还没设置过
    private static bool _dragging;
    private static Vector2 _grabLocal;
    private static string _posPath;
    private static long _hideAt;
    private static bool _hooked;
    private static string _lastText = "";
    private static Vector2 _lastViewport = Vector2.Zero;
    private static double _lastSayAtSeconds;
    private static double _lastReasonAtSeconds;

    public static bool IsReady => _root != null && GodotObject.IsInstanceValid(_root);

    /// <summary>距离上一次「主动说明理由」（/whale/say）多少秒。垫话和自动播报不算。</summary>
    public static double LastReasonAgeSeconds
        => _lastReasonAtSeconds <= 0 ? double.MaxValue : Time.GetTicksMsec() / 1000.0 - _lastReasonAtSeconds;

    /// <summary>距离上一次「鲸鱼说话」多少秒（没说算 +∞）。</summary>
    public static double LastSayAgeSeconds
        => _lastSayAtSeconds <= 0 ? double.MaxValue : Time.GetTicksMsec() / 1000.0 - _lastSayAtSeconds;

    /// <summary>气泡当前是否挂在屏幕上（主播垫话要避开正在说的那一句）。</summary>
    public static bool BubbleVisible
        => _bubble != null && GodotObject.IsInstanceValid(_bubble) && _bubble.Visible;

    public static void Install()
    {
        try
        {
            if (IsReady) return;
            if (Engine.GetMainLoop() is not SceneTree tree) return;

            _layer = new CanvasLayer { Layer = 128, Name = "WhaleCompanion" };
            _root = new Control { Name = "WhaleRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
            _layer.AddChild(_root);

            _bubble = BuildBubble(out _label);
            _root.AddChild(_bubble);
            _bubble.Visible = false;

            LoadPosition();
            _bubble.GuiInput += OnBubbleInput;

            if (!_hooked)
            {
                _hooked = true;
                tree.ProcessFrame += OnFrame;
            }

            Reposition();
            BridgeLog.Info("whale ui installed");
        }
        catch (Exception e)
        {
            BridgeLog.Error("whale install failed", e);
        }
    }

    private static PanelContainer BuildBubble(out Label label)
    {
        var panel = new PanelContainer
        {
            Name = "WhaleBubble",
            MouseFilter = Control.MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(BubbleWidth, 0),
        };

        // 字幕风：背景完全透明，只留文字（文字带黑色描边）
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0f),
            BorderColor = new Color(0f, 0f, 0f, 0f),
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        };
        style.SetCornerRadiusAll(0);
        style.SetBorderWidthAll(0);
        panel.AddThemeStyleboxOverride("panel", style);

        label = new Label
        {
            Name = "WhaleText",
            AutowrapMode = TextServer.AutowrapMode.Off,   // 一句一行，不换行
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 24);
        label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        // 黑色描边：亮场景下也能看清
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
        label.AddThemeConstantOverride("outline_size", 8);
        panel.AddChild(label);
        return panel;
    }

    // ---------------------------------------------------------------- 说话

    public static Dictionary<string, object> Say(string text, int durationMs, bool countsAsReason = true, string source = "local")
    {
        try
        {
            Install();
            if (!IsReady) return Fail("whale not ready");

            _lastText = text ?? "";
            try { Sts2Whale.Whale.WhaleAvatar.OnSpeech(_lastText, durationMs, source); } catch { }

            _label.Text = Wrap(_lastText, CharsPerLine);
            _label.AddThemeFontSizeOverride("font_size", FontSizeFor(_lastText, ViewportSize().X));
            _bubble.Visible = true;
            _bubble.ResetSize();
            Reposition();
            EnsureAttached();

            var seconds = Math.Clamp(durationMs <= 0 ? DefaultDurationMs : durationMs, 800, 300000) / 1000.0;
            _hideAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(seconds * 1000);
            _lastSayAtSeconds = Time.GetTicksMsec() / 1000.0;
            if (countsAsReason) _lastReasonAtSeconds = _lastSayAtSeconds;

            BridgeLog.Info($"whale says: {_lastText} ({seconds:0.#}s)");
            return new Dictionary<string, object> { ["ok"] = true, ["text"] = _lastText, ["ms"] = (long)(seconds * 1000) };
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    /// <summary>头像已经移除，保留接口只为兼容旧调用。</summary>
    public static Dictionary<string, object> Reload()
        => Fail("头像已移除（现在只有可拖拽的气泡）");

    // ---------------------------------------------------------------- 布局

    private static void Reposition()
    {
        if (!IsReady) return;

        var size = ViewportSize();
        _lastViewport = size;
        _root.Position = Vector2.Zero;
        _root.Size = size;

        var fontSize = FontSizeFor(_lastText, size.X);
        var bubbleWidth = Math.Clamp(Math.Min(_lastText.Length, CharsPerLine) * fontSize + 36f, 180f, size.X - 40f);
        var lines = Math.Max(1, (int)Math.Ceiling(_lastText.Length / (float)CharsPerLine));
        var bubbleHeight = fontSize * 1.35f * lines + 24f;

        Vector2 pos;
        if (_bubbleNorm.X >= 0)
        {
            // 玩家拖过：按归一化坐标还原（换分辨率也不会跑丢）
            pos = new Vector2(_bubbleNorm.X * size.X, _bubbleNorm.Y * size.Y);
        }
        else
        {
            // 默认：底部居中，像电影字幕
            pos = new Vector2((size.X - bubbleWidth) * 0.5f, size.Y - bubbleHeight - 56f);
        }

        pos.X = Math.Clamp(pos.X, 0f, Math.Max(0f, size.X - bubbleWidth));
        pos.Y = Math.Clamp(pos.Y, 0f, Math.Max(0f, size.Y - bubbleHeight));

        _bubble.Position = pos;
        _bubble.Size = new Vector2(bubbleWidth, bubbleHeight);
    }

    /// <summary>按「最长一行」算字号：超过一行的字数就换行，不是缩小字号。</summary>
    private static int FontSizeFor(string text, float viewportWidth)
    {
        var length = Math.Clamp(text?.Length ?? 1, 1, CharsPerLine);
        var fit = (viewportWidth - 40f - 32f) / length;
        return (int)Math.Clamp(Math.Min(FontSize, fit), 16f, FontSize);
    }

    /// <summary>每 40 个字符插一个换行；原有的换行保留。</summary>
    private static string Wrap(string text, int perLine)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length + text.Length / perLine + 1);
        var count = 0;
        foreach (var ch in text)
        {
            if (ch == '\n') { sb.Append(ch); count = 0; continue; }
            if (count >= perLine) { sb.Append('\n'); count = 0; }
            sb.Append(ch);
            count++;
        }
        return sb.ToString();
    }

    private static float EstimateBubbleWidth(string text, int fontSize, float viewportWidth)
    {
        var length = Math.Max(1, text?.Length ?? 0);
        return Math.Clamp(length * fontSize + 32f, 180f, viewportWidth - 40f);
    }

    private static float EstimateBubbleHeight(int fontSize)
        => fontSize * 1.35f + 24f;

    private static Vector2 ViewportSize()
    {
        if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null) return tree.Root.GetVisibleRect().Size;
        return new Vector2(1920, 1080);
    }

    private static void EnsureAttached()
    {
        if (!IsReady) return;
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return;
        if (_layer.GetParent() == null) tree.Root.AddChild(_layer);
    }

    // ---------------------------------------------------------------- 拖拽

    private static void OnBubbleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _dragging = true;
                _grabLocal = mb.Position;
                _bubble.MoveToFront();
            }
            else if (_dragging)
            {
                _dragging = false;
                var size = ViewportSize();
                if (size.X > 1 && size.Y > 1)
                {
                    _bubbleNorm = new Vector2(_bubble.Position.X / size.X, _bubble.Position.Y / size.Y);
                    SavePosition();
                    BridgeLog.Info($"气泡位置保存 {_bubble.Position}");
                }
            }
            return;
        }

        if (_dragging && @event is InputEventMouseMotion)
        {
            var size = ViewportSize();
            var pos = _root.GetLocalMousePosition() - _grabLocal;
            pos.X = Math.Clamp(pos.X, 0f, Math.Max(0f, size.X - _bubble.Size.X));
            pos.Y = Math.Clamp(pos.Y, 0f, Math.Max(0f, size.Y - _bubble.Size.Y));
            _bubble.Position = pos;
        }
    }

    private static string PositionPath
    {
        get
        {
            if (_posPath != null) return _posPath;
            var dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2", "mod_configs", "Sts2AgentBridge");
            Directory.CreateDirectory(dir);
            _posPath = Path.Combine(dir, "bubble.json");
            return _posPath;
        }
    }

    private static void LoadPosition()
    {
        try
        {
            if (!File.Exists(PositionPath)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(PositionPath));
            var x = doc.RootElement.GetProperty("x").GetSingle();
            var y = doc.RootElement.GetProperty("y").GetSingle();
            _bubbleNorm = new Vector2(x, y);
            BridgeLog.Info($"气泡位置已加载 {x:0.000},{y:0.000}");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("气泡位置读取失败: " + e.Message);
        }
    }

    private static void SavePosition()
    {
        try
        {
            File.WriteAllText(PositionPath,
                $"{{\"x\":{_bubbleNorm.X.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}," +
                $"\"y\":{_bubbleNorm.Y.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}}}");
        }
        catch (Exception e)
        {
            BridgeLog.Warn("气泡位置保存失败: " + e.Message);
        }
    }

    public static Dictionary<string, object> Hide()
    {
        try
        {
            if (_bubble != null && GodotObject.IsInstanceValid(_bubble)) _bubble.Visible = false;
            _hideAt = 0;
            return new Dictionary<string, object> { ["ok"] = true, ["hidden"] = true };
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    public static Dictionary<string, object> State() => new()
    {
        ["ok"] = true,
        ["installed"] = IsReady,
        ["visible"] = _bubble != null && GodotObject.IsInstanceValid(_bubble) && _bubble.Visible,
        ["text"] = _lastText,
        ["viewport"] = new[] { ViewportSize().X, ViewportSize().Y },
        ["dragging"] = _dragging,
        ["position"] = _bubble != null && GodotObject.IsInstanceValid(_bubble)
            ? new[] { _bubble.Position.X, _bubble.Position.Y }
            : new[] { 0f, 0f },
        ["size"] = _bubble != null && GodotObject.IsInstanceValid(_bubble)
            ? new[] { _bubble.Size.X, _bubble.Size.Y }
            : new[] { 0f, 0f },
    };

    private static void OnFrame()
    {
        if (!IsReady) return;

        try
        {
            if (_hideAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > _hideAt)
            {
                _hideAt = 0;
                if (_bubble != null && GodotObject.IsInstanceValid(_bubble)) _bubble.Visible = false;
            }

            // 只在视口尺寸变了才重排：以前每帧都算一遍位置/大小
            if (_bubble.Visible && ViewportSize() != _lastViewport) Reposition();
            EnsureAttached();
        }
        catch { }
    }

    private static Dictionary<string, object> Fail(string message) => new() { ["ok"] = false, ["error"] = message };
}

