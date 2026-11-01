using System.Collections.Generic;

namespace Sts2AgentBridge;

/// <summary>
/// 「说话门禁」：选路线、选牌这类真正做决定的动作，必须先在鲸鱼气泡里说一句理由。
///
/// 规则来自玩家要求：每次选路线/选牌，主播都要说明理由。人设卡里也写了同样的规矩，
/// 但靠自觉会漏，所以在 HTTP 层兜一道：没说过话就返回 <c>reason:"need_reason"</c>，不执行。
///
/// 判定只看「最近有没有说话」：气泡正在显示，或上一次说话在窗口内，都算说过。
/// 说一句「走右边 商店能删牌」就够，不要求长篇。
/// </summary>
public static class NarrationGate
{
    /// <summary>说完多久之内算「刚说过」。气泡默认 9~14 秒，所以给到 25 秒。</summary>
    public const double WindowSeconds = 25;

    public static bool Recent()
    {
        // 只认「主动说明理由」（/whale/say）；垫话、求解器的自动播报都不算，
        // 否则随便一句「有点困」就把门禁糊弄过去了。
        try { return WhaleUi.LastReasonAgeSeconds <= WindowSeconds; }
        catch { return true; }   // 读不到就别卡玩家
    }

    public static Dictionary<string, object> Demand(string what)
    {
        var d = new Dictionary<string, object>
        {
            ["ok"] = false,
            ["reason"] = "need_reason",
            ["stopped"] = $"这次{what}还没说明理由",
            ["hint"] = "先 POST /whale/say 说一句理由（一句话，带数字最好，例如「走右边 商店能删牌」），再重试这次决定。垫话和自动播报不算。",
            ["windowSeconds"] = WindowSeconds,
        };
        var age = WhaleUi.LastSayAgeSeconds;
        if (age < double.MaxValue) d["lastSaidSecondsAgo"] = System.Math.Round(age, 1);
        return d;
    }
}
