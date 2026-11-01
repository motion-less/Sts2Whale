using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Whale.Whale;

/// <summary>
/// 皮套联动的配置（<c>vts.json</c>，放在 dll 同级）。缺文件时自动写一份带默认值的模板。
/// </summary>
public sealed class VtsConfig
{
    public bool Enabled { get; set; } = true;
    public string WsUrl { get; set; } = "ws://127.0.0.1:8001";
    public string PluginName { get; set; } = "Sts2Whale_Avatar";
    public string PluginDeveloper { get; set; } = "Sts2Whale";
    public string MouthParam { get; set; } = "MouthOpen";

    /// <summary>idle 时慢慢摆头（注入 FaceAngleX/Y/Z）。开了摄像头面捕就关掉。</summary>
    public bool IdleSway { get; set; } = true;

    /// <summary>摆头幅度倍数。</summary>
    public double SwayAmplitude { get; set; } = 1.0;

    /// <summary>输出平滑：越小越软越迟钝（0.02~1）。</summary>
    public double SwaySmoothing { get; set; } = 0.15;

    /// <summary>摆头的软上限（度）：超过这个值会被 tanh 压回去。</summary>
    public double SwayMaxAngle { get; set; } = 9;

    /// <summary>抬头上限（度）：真人直播几乎不抬头，默认只给 2 度。</summary>
    public double SwayMaxUp { get; set; } = 2.0;

    /// <summary>低头上限（度）。</summary>
    public double SwayMaxDown { get; set; } = 9.0;

    /// <summary>
    /// 连上后强制设定的表情：名字 -> 开关。
    /// 默认把「屑屑眼」关掉——这个模型默认开着它，眼睛是半眯的，看起来发愣。
    /// </summary>
    public Dictionary<string, bool> StartupExpressions { get; set; } = new() { ["屑屑眼"] = false, ["钢盆"] = false };

    /// <summary>受击时整模向右倾斜（不倒翁效果）。</summary>
    public bool HitTilt { get; set; } = true;

    /// <summary>每 1 点伤害倾斜多少度（1.0 = 40 伤 ≈ 40°）。</summary>
    public double HitDegreesPerDamage { get; set; } = 1.0;

    /// <summary>最大倾斜角度。</summary>
    public double HitMaxDegrees { get; set; } = 40;

    /// <summary>求解器算牌时挂的表情（可以是多个，同时激活）。</summary>
    public List<string> ThinkingExpressions { get; set; } = new() { "圈圈眼", "加载" };

    /// <summary>兼容旧配置的单个写法（thinkingExpression: "圈圈眼"）。</summary>
    public string ThinkingExpression { get; set; } = "";

    /// <summary>有格挡时常驻的表情（锅盖）。</summary>
    public string BlockExpression { get; set; } = "钢盆";

    /// <summary>idle 时常驻的输入参数（比如轻轻的笑）。</summary>
    public Dictionary<string, double> IdleParams { get; set; } = new() { ["MouthSmile"] = 0.15 };
    public int CooldownMs { get; set; } = 900;
    public SpeechConfig Speech { get; set; } = new();
    public Dictionary<string, EmotionCue> Emotions { get; set; } = DefaultEmotions();

    [JsonIgnore]
    public string SourcePath { get; private set; } = "";

    private static Dictionary<string, EmotionCue> DefaultEmotions() => new()
    {
        // 生气 / 攻击
        ["attack"] = new() { Expression = "黑脸", HoldMs = 900, Params = new() { ["FaceAngry"] = 0.7 } },
        // 吃痛（小掉血）
        ["hurt_small"] = new() { Expression = "＞＜", HoldMs = 800 },
        // 挨打（中等掉血）
        ["hurt"] = new() { Expression = "流冷汗", HoldMs = 1200, Params = new() { ["MouthSmile"] = -0.4 } },
        // 受伤重（大掉血）
        ["hurt_big"] = new() { Expression = "流泪", HoldMs = 1800, Params = new() { ["MouthSmile"] = -0.6 } },
        // 专注 / 进攻（进战斗）
        ["combat_start"] = new() { Expression = "前倾", HoldMs = 1200 },
        // 认真（精英 / Boss）
        ["elite"] = new() { Expression = "黑框眼镜", HoldMs = 3000 },
        // 奖励 / 胜利
        ["reward"] = new() { Expression = "爱心眼", HoldMs = 2600, Params = new() { ["MouthSmile"] = 0.7 } },
        ["victory"] = new() { Expression = "爱心眼", HoldMs = 3000, Params = new() { ["MouthSmile"] = 0.8 } },
        // 失败
        ["death"] = new() { Expression = "流泪", HoldMs = 4000 },
        // 得意 / 嘲讽
        ["shop"] = new() { Expression = "屑屑眼", HoldMs = 2200 },
        // 害羞 / 升级
        ["rest"] = new() { Expression = "脸红", HoldMs = 2200 },
        ["level"] = new() { Expression = "脸红", HoldMs = 2000, Params = new() { ["MouthSmile"] = 0.5 } },
        // 思考 / 选牌
        ["card_select"] = new() { Expression = "圈圈眼", HoldMs = 1600 },
    };

    public static VtsConfig Load(string path)
    {
        var cfg = new VtsConfig { SourcePath = path };

        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<VtsConfig>(File.ReadAllText(path), Options);
                if (loaded != null)
                {
                    loaded.SourcePath = path;
                    if (loaded.Emotions == null || loaded.Emotions.Count == 0)
                    {
                        loaded.Emotions = DefaultEmotions();
                    }
                    else
                    {
                        // 升级兼容：新版本加的键补进去，但用户改过的绝不覆盖
                        foreach (var kv in DefaultEmotions())
                            loaded.Emotions.TryAdd(kv.Key, kv.Value);
                    }
                    if (loaded.Speech == null) loaded.Speech = new SpeechConfig();
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置坏了就用默认值，别让游戏起不来
        }

        try { File.WriteAllText(path, JsonSerializer.Serialize(cfg, Options)); } catch { }
        return cfg;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed class SpeechConfig
{
    /// <summary>tts = 合成并播放；text = 不出声，只按字数动嘴；off = 不动嘴。</summary>
    public string Mode { get; set; } = "tts";

    /// <summary>合成引擎：edge = 微软 Edge 在线语音（自然）；say = macOS 自带（生硬但离线）。</summary>
    public string Engine { get; set; } = "daemon";   // daemon | edge | say

    /// <summary>say 的声音（engine=say 时用）。</summary>
    public string Voice { get; set; } = "Tingting";

    /// <summary>edge-tts 的声音，少女音：zh-CN-XiaoyiNeural / 温柔：zh-CN-XiaoxiaoNeural。</summary>
    public string EdgeVoice { get; set; } = "zh-CN-XiaoyiNeural";

    /// <summary>edge-tts 语速，例如 "+8%"。</summary>
    public string EdgeRate { get; set; } = "+8%";

    /// <summary>edge-tts 音调，例如 "+2Hz"。</summary>
    public string EdgePitch { get; set; } = "+0Hz";

    /// <summary>edge-tts 可执行文件路径（daemon 不可用时的命令行兜底）。</summary>
    public string EdgeCommand { get; set; } = "";

    /// <summary>常驻 TTS 服务的端口。</summary>
    public int DaemonPort { get; set; } = 48765;

    /// <summary>跑 daemon 的 python（装 edge-tts 的那个 venv）。</summary>
    public string DaemonPython { get; set; } = "";

    /// <summary>daemon 脚本路径（留空 = mod 目录下的 tts_daemon.py）。</summary>
    public string DaemonScript { get; set; } = "";

    /// <summary>空闲多少分钟自动退出。</summary>
    public int DaemonIdleMinutes { get; set; } = 30;
    public double Gain { get; set; } = 0.78;
    public double Rate { get; set; } = 1.0;

    /// <summary>最大张口（0~1）。</summary>
    public double MaxOpen { get; set; } = 0.75;

    /// <summary>口型输出平滑：越小嘴动得越慢越软（0.05~1）。</summary>
    public double Smoothing { get; set; } = 0.22;

    /// <summary>超过这个字数的句子只念前面的（气泡照常显示全文），避免一句话念 20 秒。</summary>
    public int MaxChars { get; set; } = 80;

    /// <summary>只念 DeepSeek（WhaleBrain）返回的台词；本地垫话/提示/进度不念。默认开。</summary>
    public bool OnlyBrain { get; set; } = true;
}

public sealed class EmotionCue
{
    /// <summary>VTS 里的热键名（每个皮套不一样，按 Ctrl 看自己的热键列表）。</summary>
    public string? Hotkey { get; set; }

    /// <summary>可选：exp3 表情名（和热键二选一，热键优先）。</summary>
    public string? Expression { get; set; }

    /// <summary>可选：直接注入的输入参数（-1..1），用来做强度。</summary>
    public Dictionary<string, double>? Params { get; set; }

    public int HoldMs { get; set; } = 2000;

    /// <summary>优先级：高的表情在 hold 期间不会被低的顶掉（格挡=10，其余默认 0）。</summary>
    public int Priority { get; set; } = 0;
}
