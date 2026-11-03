using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2AgentBridge;

/// <summary>
/// 右上角「设置 / 暂停」按钮下边的一小块连胜面板：每个职业一行「当前连胜 / 最高连胜」。
///
/// 数据不解析存档文件，直接读 <c>SaveManager.Instance.Progress.CharacterStats</c>：
/// 存档里的 <see cref="CharacterStats"/> 本来就带着 <c>CurrentWinStreak</c> / <c>BestWinStreak</c>，
/// 和游戏自己的统计页同源，结算完立刻就是新值，也不会读到过期的 profile.save。
///
/// 位置不写死：找游戏自己的顶部栏 → 取右上角那颗按钮 → 读它在视口里的矩形 → 贴在正下方。
/// 找不到（主菜单、读档、菜单浮层压上来）就整块隐藏，不去猜坐标。
/// </summary>
public static class StreakHud
{
    private const int LayerIndex = 126;        // 鲸鱼气泡是 128，别盖住它
    private const float GapY = 6f;             // 按钮底边到面板顶边的距离
    private const float RightPad = 2f;         // 面板右边和按钮右边对齐
    private const long RefreshMs = 10_000;     // 10 秒读一次就够（只有结算才变），别每秒去翻存档
    private const int FontSize = 18;

    /// <summary>游戏本地化取不到名字时的兜底（也保证英文环境下仍然认得出是谁）。</summary>
    private static readonly Dictionary<string, string> FallbackNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ironclad"] = "铁甲战士",
        ["silent"] = "静默猎手",
        ["defect"] = "故障机器人",
        ["regent"] = "储君",
        ["necrobinder"] = "亡灵契约师",
        ["watcher"] = "观者",
    };

    private static CanvasLayer? _layer;
    private static Control? _root;
    private static PanelContainer? _panel;
    private static Label? _label;

    private static long _nextRefreshAt;
    private static long _nextInstallAt;
    private static string _text = "";
    private static string _lastError = "";

    private static bool _layoutDirty = true;    // 文字变了 / 刚装好 → 必须重新贴一次
    private static Rect2 _lastRect;
    private static Vector2 _lastViewport;

    /// <summary>当前面板文字（每行一个职业）；没装好或读不到存档时是空串。</summary>
    public static string Text => _text;

    // ------------------------------------------------------------------ 生命周期

    public static void Install()
    {
        try
        {
            if (_root != null && GodotObject.IsInstanceValid(_root)) return;
            if (Engine.GetMainLoop() is not SceneTree tree) return;

            _layer = new CanvasLayer { Layer = LayerIndex, Name = "WhaleStreakHud" };
            _root = new Control { Name = "StreakRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
            _layer.AddChild(_root);

            BuildPanel();
            _root.AddChild(_panel);
            tree.Root.AddChild(_layer);
            _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

            _text = "";             // 新 Label 是空的，别让「文字没变」的短路挡住第一次填字
            _layoutDirty = true;
            Refresh();
            BridgeLog.Info("[streak] 连胜面板已装到右上角");
        }
        catch (Exception e)
        {
            // 只清掉「没真正挂上树」的半成品，交给 Tick 的补装逻辑重试
            if (_panel == null || !GodotObject.IsInstanceValid(_panel) || _panel.GetParent() == null)
            {
                _layer = null;
                _root = null;
                _panel = null;
                _label = null;
            }
            BridgeLog.Warn("[streak] 安装失败: " + e.Message);
        }
    }

    /// <summary>主线程每帧调一次（BridgePump）：每 10 秒重读一次数据，其余帧只做一次很轻的显示/隐藏判断。</summary>
    public static void Tick()
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel))
        {
            // 场景整棵被换掉（回主菜单/换局）时面板会跟着失效 —— 隔几秒自己补装一次
            _root = null;
            _panel = null;
            _label = null;
            _layer = null;

            var now = System.Environment.TickCount64;
            if (now < _nextInstallAt) return;
            _nextInstallAt = now + 5000;
            Install();
            return;
        }

        try
        {
            var now = System.Environment.TickCount64;
            if (now >= _nextRefreshAt)
            {
                _nextRefreshAt = now + RefreshMs;
                Refresh();
            }
            Sync();
        }
        catch (Exception e)
        {
            LogOnce(e);
        }
    }

    private static void BuildPanel()
    {
        _panel = new PanelContainer { Name = "StreakPanel", MouseFilter = Control.MouseFilterEnum.Ignore };

        // 和鲸鱼气泡一个路子：不抢眼，但压在亮色背景上也能看清
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.75f),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        };
        style.SetCornerRadiusAll(6);
        style.SetBorderWidthAll(0);
        _panel.AddThemeStyleboxOverride("panel", style);

        _label = new Label
        {
            Name = "StreakText",
            AutowrapMode = TextServer.AutowrapMode.Off,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _label.AddThemeFontSizeOverride("font_size", FontSize);
        _label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.95f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
        _label.AddThemeConstantOverride("outline_size", 5);
        _panel.AddChild(_label);
        _panel.Visible = false;
    }

    // ------------------------------------------------------------------ 数据

    private static void Refresh()
    {
        if (_panel == null || _label == null) return;

        var text = ReadText();
        if (text == _text) return;
        _text = text;

        _label.Text = text;
        _label.Visible = text.Length > 0;
        _panel.ResetSize();
        _layoutDirty = true;      // 宽度可能变了，右对齐要重算
    }

    /// <summary>
    /// 从存档进度里拼出五行文本。读不到（还没进游戏 / 存档没加载完）就返回空串，
    /// 下次刷新再试 —— 不缓存失败，免得开关一次菜单就再也不显示。
    /// </summary>
    private static string ReadText()
    {
        try
        {
            var progress = SaveManager.Instance?.Progress;
            if (progress == null) return "";

            var stats = new Dictionary<string, CharacterStats>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in progress.CharacterStats)
            {
                try
                {
                    if (pair.Key == null || pair.Value == null) continue;
                    var id = pair.Key.Entry;
                    if (!string.IsNullOrEmpty(id)) stats[id] = pair.Value;
                }
                catch { }
            }

            var characters = ModelDb.AllCharacters
                .Where(c => c != null && SafePlayable(c))
                .OrderBy(c => Rank(IdOf(c)))
                .ThenBy(IdOf, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (characters.Count == 0) return "";

            var sb = new StringBuilder();
            foreach (var character in characters)
            {
                var id = IdOf(character);
                stats.TryGetValue(id, out var stat);
                sb.Append(DisplayName(character, id))
                  .Append('：').Append(stat?.CurrentWinStreak ?? 0)
                  .Append('/').Append(stat?.BestWinStreak ?? 0)
                  .Append('\n');
            }
            var text = sb.ToString().TrimEnd('\n');
            _lastError = "";     // 读成功就把启动早期的一次性错误擦掉（/streak 里别一直挂着旧账）
            return text;
        }
        catch (Exception e)
        {
            LogOnce(e);
            return "";
        }
    }

    /// <summary>固定顺序：铁甲 → 猎手 → 机器人 → 储君 → 亡灵；模组角色排在后面按 id 排。</summary>
    private static int Rank(string id) => id.ToLowerInvariant() switch
    {
        "ironclad" => 0,
        "silent" => 1,
        "defect" => 2,
        "regent" => 3,
        "necrobinder" => 4,
        "watcher" => 5,
        _ => 99,
    };

    private static bool SafePlayable(CharacterModel character)
    {
        try { return character.IsPlayable; } catch { return true; }
    }

    private static string IdOf(CharacterModel character)
    {
        try { return character.Id.Entry ?? ""; } catch { return ""; }
    }

    private static string DisplayName(CharacterModel character, string id)
    {
        try
        {
            var text = character.Title.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(text) &&
                !text.StartsWith("LocString table ", StringComparison.Ordinal))
                return text.Trim();
        }
        catch { }

        if (FallbackNames.TryGetValue(id, out var name)) return name;
        return string.IsNullOrEmpty(id) ? "未知角色" : id;
    }

    // ------------------------------------------------------------------ 布局

    /// <summary>把面板贴到「设置 / 暂停」按钮正下方；按钮不可见（菜单、过场）时隐藏。</summary>
    private static void Sync()
    {
        if (_panel == null) return;

        var anchor = AnchorControl();
        if (anchor == null || _text.Length == 0)
        {
            if (_panel.Visible) _panel.Visible = false;
            _layoutDirty = true;
            return;
        }

        var rect = ScreenRectOf(anchor);
        if (rect.Size.X < 1f || rect.Size.Y < 1f)
        {
            if (_panel.Visible) _panel.Visible = false;
            _layoutDirty = true;
            return;
        }

        // 按钮和窗口没动就不重算布局（每帧两次原生变换调用也不贵，但没必要）
        var viewport = ViewportSize();
        if (!_layoutDirty && _panel.Visible && rect == _lastRect && viewport == _lastViewport) return;
        _layoutDirty = false;
        _lastRect = rect;
        _lastViewport = viewport;

        if (_panel.Size.X < 1f) _panel.ResetSize();

        var x = rect.End.X - RightPad - _panel.Size.X;
        var y = rect.End.Y + GapY;
        x = Mathf.Clamp(x, 2f, Mathf.Max(2f, viewport.X - _panel.Size.X - 2f));
        y = Mathf.Clamp(y, 2f, Mathf.Max(2f, viewport.Y - _panel.Size.Y - 2f));
        _panel.Position = new Vector2(x, y);
        _panel.Visible = true;
    }

    /// <summary>
    /// 右上角那颗按钮。优先取顶部栏的 <c>Pause</c>（游戏里点开设置/菜单的那颗齿轮），
    /// 取不到就退到顶部栏本身（它的右下角就在同一片区域）。
    /// </summary>
    private static Control? AnchorControl()
    {
        try
        {
            // 牌组/设置/暂停这些浮层压上来时先让位，别盖在人家脸上
            if ((NOverlayStack.Instance?.ScreenCount ?? 0) > 0) return null;

            var topBar = NRun.Instance?.GlobalUi?.TopBar;
            if (topBar == null || !GodotObject.IsInstanceValid(topBar) || !topBar.IsVisibleInTree()) return null;

            var pause = topBar.Pause;
            if (pause != null && GodotObject.IsInstanceValid(pause) && pause.IsVisibleInTree()) return pause;
            return topBar;
        }
        catch { return null; }
    }

    /// <summary>控件在视口坐标里的矩形（含 CanvasLayer 和祖先的缩放/位移）。</summary>
    private static Rect2 ScreenRectOf(Control control)
    {
        try
        {
            var size = control.Size;
            var xform = control.GetGlobalTransformWithCanvas();

            var p0 = xform * Vector2.Zero;
            var p1 = xform * new Vector2(size.X, 0f);
            var p2 = xform * size;
            var p3 = xform * new Vector2(0f, size.Y);

            var min = new Vector2(
                Mathf.Min(Mathf.Min(p0.X, p1.X), Mathf.Min(p2.X, p3.X)),
                Mathf.Min(Mathf.Min(p0.Y, p1.Y), Mathf.Min(p2.Y, p3.Y)));
            var max = new Vector2(
                Mathf.Max(Mathf.Max(p0.X, p1.X), Mathf.Max(p2.X, p3.X)),
                Mathf.Max(Mathf.Max(p0.Y, p1.Y), Mathf.Max(p2.Y, p3.Y)));
            return new Rect2(min, max - min);
        }
        catch
        {
            return new Rect2(control.GetScreenPosition(), control.Size);
        }
    }

    private static Vector2 ViewportSize()
    {
        try
        {
            var viewport = _root?.GetViewport();
            if (viewport != null)
            {
                var rect = viewport.GetVisibleRect();
                if (rect.Size.X >= 1f && rect.Size.Y >= 1f) return rect.Size;
            }
        }
        catch { }
        return new Vector2(1920f, 1080f);
    }

    // ------------------------------------------------------------------ 排障

    /// <summary>调试用：面板有没有显示、贴在哪个矩形下面、当前文字是什么（/streak）。</summary>
    public static Dictionary<string, object> Snapshot()
    {
        var anchor = AnchorControl();
        var rect = anchor != null ? ScreenRectOf(anchor) : default;
        var visible = _panel is { } panel && GodotObject.IsInstanceValid(panel) && panel.Visible;

        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["text"] = _text,
            ["visible"] = visible,
            ["anchor"] = anchor == null ? "" : anchor.GetType().Name,
            ["anchorRect"] = anchor == null
                ? ""
                : $"{Mathf.Round(rect.Position.X)},{Mathf.Round(rect.Position.Y)},{Mathf.Round(rect.Size.X)},{Mathf.Round(rect.Size.Y)}",
            ["panelRect"] = _panel is { } p && GodotObject.IsInstanceValid(p)
                ? $"{Mathf.Round(p.Position.X)},{Mathf.Round(p.Position.Y)},{Mathf.Round(p.Size.X)},{Mathf.Round(p.Size.Y)}"
                : "",
            ["lastError"] = _lastError,
        };
    }

    private static void LogOnce(Exception e)
    {
        var message = e.GetType().Name + ": " + e.Message;
        if (message == _lastError) return;
        _lastError = message;
        BridgeLog.Warn("[streak] " + message);
    }
}
