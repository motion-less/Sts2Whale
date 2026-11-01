# SpireStrategist（vendored）

这个目录是**上游项目的快照**，不是本项目自己的代码。改之前先读这里。

| | |
|---|---|
| 上游 | https://github.com/shaokangqian4-art/SpireStrategist |
| 许可 | MIT，全文见仓库根目录 [`LICENSE-SpireStrategist-MIT.txt`](../../LICENSE-SpireStrategist-MIT.txt) |
| 版权 | Copyright (c) 2026 穷仔（shaokangqian4-art） |
| 快照 | **v0.6.22**（`SpireStrategist.Mod/AdvisorPanel.cs` 里的版本号可自证） |
| 位置对应 | 上游 `src/` → 本目录 |

> 上游现在已经到 **v0.6.55**，比这份快照多 54 个文件、另有一批改动。
> 也就是说：**本目录落后上游很多**，上游修的问题这里没有。

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

**新增 2 个目录**（上游不在 `src/` 下）：

| 目录 | 内容 |
|---|---|
| `data/` | `cards-v0.111.0.json`、`event-cards-v0.111.0.json`、`relic-test-ids.json`（上游在别处生成/存放） |
| `localization/` | `en.json` |

## 同步上游

```bash
git clone --depth 1 --branch v0.6.55 \
  https://github.com/shaokangqian4-art/SpireStrategist.git /tmp/ss
diff -rq /tmp/ss/src src/strategist
```

因为是跨了 33 个版本，**不能直接覆盖**。建议按文件挑：先看上游 CHANGELOG 里哪些修的是 bug，
再逐个合到本目录，合完保留上表那 8 个文件的本地改动。
