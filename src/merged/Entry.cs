using System;
using MegaCrit.Sts2.Core.Modding;
using Sts2AgentBridge;

namespace Sts2Whale;

/// <summary>
/// 合并版唯一的入口：先起「战斗路线求解器」，再起鲸鱼桥接。
///
/// 顺序有讲究：求解器要先把它的生命周期钩子（CombatStarting/CombatEnded）和 Harmony 补丁装上，
/// 桥接随后接管 HTTP/UI；两边都在同一程序集里，桥接直接调 <c>SolverController</c>，不走反射。
/// </summary>
[ModInitializer(nameof(Init))]
public static class WhaleEntry
{
    public static void Init()
    {
        try
        {
            CombatSolver.Entry.Initialize();
        }
        catch (Exception e)
        {
            try { BridgeLog.Error("CombatSolver init failed", e); } catch { }
        }

        // 战斗求解器的分析窗口默认藏起来：SetUserHidden 是「意图」，它每次呈现都会沿用，
        // 所以这里设一次就一直是隐藏的（想调出来：POST /solver/ui {"hide":false} 或 Ctrl+F9）。
        try
        {
            SolverBridge.HideSolverOverlay(true);
        }
        catch (Exception e)
        {
            try { BridgeLog.Warn("hide solver overlay failed: " + e.Message); } catch { }
        }

        try
        {
            SpireStrategist.Mod.Entry.Initialize();
        }
        catch (Exception e)
        {
            try { BridgeLog.Error("SpireStrategist init failed", e); } catch { }
        }

        // 战略助手「离店」用的钩子：桥接在场时直接点界面上的「前进」（HideScreen 只关面板、走不出去）。
        try
        {
            SpireStrategist.Mod.PartialAutopilot.LeaveShopHook = () =>
            {
                var r = UiClicks.ClickText("前进");
                var ok = r.TryGetValue("ok", out var v) && v is bool b && b;
                BridgeLog.Info("strategist leave-shop: 点前进 " + (ok ? "ok" : "没点到"));
            };
        }
        catch (Exception e)
        {
            try { BridgeLog.Warn("leave-shop hook failed: " + e.Message); } catch { }
        }

        try
        {
            BridgeBootstrap.Start();
        }
        catch (Exception e)
        {
            try { BridgeLog.Error("bridge bootstrap failed", e); } catch { }
        }
    }
}
