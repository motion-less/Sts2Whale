using System;
using Godot;

using System.IO;

namespace Sts2AgentBridge;

public static class BridgeBootstrap
{
    public static void Start()
    {
        BridgeLog.Info("mod initializer called");

        // 先定好「只读资源目录」和「用户可写目录」，后面所有配置都走 DataDir —— 工坊更新才不会冲掉玩家配置
        ModPaths.Init();

        BridgePump.Install();

        // 语义请求/应答：默认接管 CardSelectCmd（selector 通道，数据层最可靠），
        // /respond 时由 mod 自己播一段本地动画（亮牌 + 鲸鱼气泡 + 延迟），见 RequestApi。
        // 只有启动前显式设了 STS2_AGENT_SELECTOR=0 才退回「扫界面 + 模拟点击」的保留通道。
        RequestApi.InstallSelector();

        var dataDir = ModPaths.DataDir;

        // 本局记忆：0.8 起落盘交给 RitsuLib 的 ModDataStore（首次会从 0.7 的 run_memory.json 迁移）
        RunMemory.Init(dataDir);

        // 设置页：把 llm/vts/live 三个 json 镜像到游戏的 mod 设置里，玩家不用手改文件
        Sts2Whale.WhaleSettings.Install(dataDir);

        // 桥接自己的调试页：挂到 RitsuLib 的 Debug Tools（原来自绘的 ConsolePage 只留数据层）
        Sts2Whale.WhaleDebugPages.Install();

        // 首次启动补环境：找 python、没有 edge-tts 就在后台装一个 venv，装完提示重启
        RuntimeBootstrap.EnsureInBackground(dataDir);

        HttpBridge.Start();
        AgentUi.Load();
        WhaleUi.Install();
        Sts2Whale.Whale.WhaleAvatar.Install();

        // 直播弹幕（live.json 存在且 enabled 才开）
        DanmakuBridge.Start(dataDir);

        // 到这里 RitsuLib 一定已经起来了：重试一次「日志进游戏通道」（启动早期那次多半失败）
        BridgeLog.RetryGameLog();
    }
}
