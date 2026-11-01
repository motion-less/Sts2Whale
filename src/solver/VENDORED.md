# CombatSolver（vendored）

这个目录是**上游项目的快照**，不是本项目自己的代码。改之前先读这里。

| | |
|---|---|
| 上游 | https://github.com/Torch1230/CombatSolver |
| 许可 | MIT，全文见仓库根目录 [`LICENSE-CombatSolver-MIT.txt`](../../LICENSE-CombatSolver-MIT.txt) |
| 版权 | Copyright (c) 2026 Torch1230 and contributors；部分衍生自 Random Foreseer（© hotwords123） |
| 快照 | `main` @ `5c773ca`（2026-10-07） |
| 位置对应 | 上游 `src/` → 本目录 |

## 本地改动（只有 3 个文件）

其余文件与上游逐字节一致，上游更新时可以直接覆盖。

| 文件 | 改了什么 | 为什么 |
|---|---|---|
| `Runtime/Entry.cs` | 删掉 `[ModInitializer]` 特性 | 合并版只有一个入口（`src/merged/Entry.cs`），两个 mod 共用一个程序集 |
| `Runtime/SolverController.cs` | 新增 `LastStopWasDeathPrediction` 静态属性 | 桥接读它决定「投子认负、放弃本局」 |
| `UI/SolverOverlay.cs` | 新增 `SearchInProgress` 与 `WorldlineProgressText()` | 桥接要读搜索进度打到气泡上，不能依赖 UI 显示状态 |

## 同步上游

```bash
git clone --depth 1 https://github.com/Torch1230/CombatSolver.git /tmp/cs
diff -rq /tmp/cs/src src/solver        # 看差了多少
```

有冲突时优先**保留上面三个文件的本地改动**，其余照上游覆盖。
