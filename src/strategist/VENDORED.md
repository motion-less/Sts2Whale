# SpireStrategist（vendored）

这个目录是**上游项目的快照**，不是本项目自己的代码。改之前先读这里。

| | |
|---|---|
| 上游 | https://github.com/shaokangqian4-art/SpireStrategist |
| 许可 | MIT，全文见仓库根目录 [`LICENSE-SpireStrategist-MIT.txt`](../../LICENSE-SpireStrategist-MIT.txt) |
| 版权 | Copyright (c) 2026 穷仔（shaokangqian4-art） |
| 快照 | **v0.6.55**（`SpireStrategist.Mod/Entry.cs` 的加载日志可自证） |
| 位置对应 | 上游 `src/` → 本目录 |

> 2026-11-01 已从 v0.6.22 升到 **v0.6.55**（跨 33 个版本），本地 8 处改动全部保留。

## 本地改动

**8 个文件被改过**，其余与上游 v0.6.22 逐字节一致：

| 文件 | 改了什么 | 为什么 |
|---|---|---|
| `SpireStrategist.Mod/Entry.cs` | 删掉 `[ModInitializer]` 和 `Harmony.PatchAll` | 合并版统一由 `src/merged/Entry.cs` 注册与打补丁 |
| `SpireStrategist.Mod/AdvisorPanel.cs` | 默认隐藏（`userHidden = true`），新增 `UserHidden` | 界面上不要面板，但接管逻辑照跑 |
| `SpireStrategist.Mod/AdvisorService.cs` | 新增 `LastShopPlan` | 把最近一次商店建议留给鲸鱼桥接读 |
| `SpireStrategist.Mod/PartialAutopilot.cs` | 新增 `LeaveShopHook` + 按金币离店 | 桥接用点击按钮的方式离店，独立版没有这个入口 |
| `SpireStrategist.Mod/SolverBridge.cs` | 判定改成「同程序集」而不是「加载了 id=CombatSolver 的 mod」 | 两个 mod 已合并进一个 DLL |
| `SpireStrategist.Mod/ClimbController.cs` | 新增 `SetUserHidden(bool)` | 整块浮动面板的显隐控制 |
| `SpireStrategist.Mod/ClimbShop.cs` | 第三幕清仓：金币 > 400 就随机买遗物 | 独立版没有清仓逻辑 |
| `SpireStrategist.Mod/SnapshotReader.cs` | 卡面描述走游戏本地化 + 兜底异常 | 商店预览卡的变量没填齐时游戏会抛异常 |

**额外带进来的 2 个目录**（上游放在仓库根，不在 `src/` 下）：

| 目录 | 内容 | 用途 |
|---|---|---|
| `data/` | 12 个 json：卡表 / 事件卡 / 三个新角色的 prior+cards / Defect 系列 / `relic-test-ids` | `Sts2Whale.csproj` 以 `EmbeddedResource` 嵌入 |
| `localization/` | `en.json` | 同上 |

v0.6.55 新增了 9 个必须嵌入的资源（Regent / Necrobinder / Silent / Defect 的 prior 与卡表），
`Sts2Whale.csproj` 已同步补齐 —— **漏掉这些编译照样过，运行时读资源才会报错**。

## 同步上游

```bash
git clone --depth 1 --branch <新版本> \
  https://github.com/shaokangqian4-art/SpireStrategist.git /tmp/ss
diff -rq /tmp/ss/src src/strategist
```

0.6.22 → 0.6.55 的实际步骤：

1. 整个 `src/` 换成上游的
2. 本地 8 处改动用三方合并重放：`git merge-file -p <我们的> <旧基线> <新版> > 结果`
3. 补 `data/`、`localization/`，并把上游新增的 `EmbeddedResource` 抄进 `Sts2Whale.csproj`
4. 编译（0 错误只代表符号对得上，**必须再实跑验证**）
