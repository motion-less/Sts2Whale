using System.Collections.Generic;

namespace Sts2AgentBridge;

/// <summary>
/// 代理模式常开。
///
/// 以前这里往主菜单/暂停菜单/邀请按钮旁边塞「肥鱼代理」「邀请肥鱼」开关，可以关掉代理模式；
/// 现在按产品要求**去掉所有开关、固定为开**：mod 一加载就接管，玩家没法从界面关掉。
/// 保留 <see cref="Load"/> / <see cref="Tick"/> / <see cref="Snapshot"/> / <see cref="Set"/>
/// 只是为了让桥接里的调用点不用改。
/// </summary>
public static class AgentUi
{
    /// <summary>永远为 true —— 界面上的开关已经删掉，接口也关不掉。</summary>
    public static bool AgentMode => true;

    /// <summary>历史上从 ui.json 读开关状态；现在固定为开，什么都不做。</summary>
    public static void Load()
    {
        BridgeLog.Info("agent mode: always on（开关已移除）");
    }

    /// <summary>每帧节流调用；没有按钮要维护了，空实现。</summary>
    public static void Tick()
    {
    }

    public static Dictionary<string, object> Snapshot() => new()
    {
        ["main"] = true,
        ["pause"] = true,
        ["invite"] = true,
        ["mainButton"] = false,
        ["pauseButton"] = false,
        ["inviteButton"] = false,
        ["agentMode"] = true,
        ["locked"] = true,
    };

    /// <summary>保留旧接口；任何想关掉代理模式的调用都会被拒绝。</summary>
    public static bool Set(string which, bool value)
    {
        BridgeLog.Warn($"agent mode is locked on（忽略 /ui 请求 {which}={value}）");
        return false;
    }
}
