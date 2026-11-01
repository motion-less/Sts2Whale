# Sts2Whale（鲸鱼爬塔）v0.14.0

《杀戮尖塔 2》的三合一 mod：**战斗路线求解器 + 全局战略助手 + 鲸鱼娘**（LLM 解说 + VTube Studio 皮套），
编译成单个 `Sts2Whale.dll` 跑在游戏进程内，并通过 `127.0.0.1:47821` 暴露语义化 HTTP。

## 一张表看懂

| 部分 | 管什么 | 出处 |
|---|---|---|
| **战斗路线求解器** | 战斗内出牌：搜世界线、选最优序列 | CombatSolver（MIT，© Torch1230） |
| **全局战略助手** | 局外：选路、选牌、商店、营火、事件 | SpireStrategist（MIT，© 穷仔） |
| **鲸鱼娘** | 说话：角色卡口吻播报决定、兜底不认识的界面 | 本项目（`src/merged`） |
| **皮套联动** | 把台词/战斗事件翻译成 VTS 的表情、动作、语音 | 本项目（`src/whale`） |

玩家只要开着游戏：局外助手自己点，战斗求解器自己打，鲸鱼娘用字幕条解说，
VTube Studio 里的 Live2D 皮套同步说话、皱眉、举锅盖、被打歪。

## 0.9.0 有什么

这一版是**「全部换成 RitsuLib 的公开接口」** + **限流** + **弹幕改走登录态轮询**。

- **弹幕页链接自动生成**（新）：设置页里填房间号，下面直接给出完整弹幕页链接，一键复制。
  链接格式固定（OBS 那一套参数），daemon 优先认这个链接对应的标签页
- **换房间即时生效**（新）：改完房间号约 1 秒自动切到新的弹幕页，不用重启游戏
- **弹幕改用 Chrome + CDP**（新）：不再依赖 Safari 和「允许 Apple 事件中的 JavaScript」。
  设置页填好房间号后点「打开弹幕页」，mod 用带调试端口的独立 profile 打开它，之后读 DOM ——
  Windows 上 Edge 也能用。同时去掉了 python daemon 和本地 48767 端口
- **首次启动自动补语音环境**（新）：发布包里 `python` 路径留空，第一次启动会在后台
  - 找一个能用的 `python3`（PATH / homebrew / ~/Library/Python）
  - 没有 `edge_tts` 就在**用户数据目录里建独立 venv**，`pip install edge-tts`
  - 装好后写回配置，并弹气泡提示「重启一次游戏后鲸鱼娘就会出声」
  - 装不上（多半是没网）只提示一次，6 小时内不重试；字幕和其他功能不受影响
- **弹幕只走 Safari**：删掉 B 站直连（匿名收不到任何 `DANMU_MSG`，带登录也不稳定），
  只保留「读 Safari 里已经开着的 Laplace 弹幕页」。不需要账号、不需要 `websocket-client`，
  代价是要按下面三步把 Safari 配好

- **弹幕只保留直连 B 站**：删掉 Safari + Laplace 的网页抓取通道（要装插件、要开 Safari 的
  「允许 Apple 事件中的 JavaScript」、还要定时抢 JS 执行权）。现在只有一条路：`roomId` →
  WebSocket → `DANMU_MSG`。房间号因此变成必填，`live.json` 里的 `source` 键已经不需要

- **模型调用限流**（新）：两次 DeepSeek 会话**至少隔 10 秒**（`llm.json` 的 `minGapMs`，设置页可调）。
  以前战略助手每做一个决定（含「走到 Monster(1,5)」这种）就调一次模型，约 700 次/小时；
  现在最多 360 次/小时，且等待期间来了更新的决定会顶掉旧的那条——**只发最新的一条**，
  不会积压、也不会把过时的话补发出来

- **不再挂任何 Harmony 补丁**（新）：皮套的掉血 / 格挡 / 敌人受击全部改走 RitsuLib 生命周期事件
  （`CurrentHpChangedEvent` / `AttackEndedEvent` / `Block*Event`）。以前盯的是
  `Creature.LoseHpInternal`、`DamageBlockInternal` 这些私有方法名，游戏换个签名就静默失效；
  现在换的是公开契约，求解器的补丁也统一由 `ModPatcher` 管，能整批摘掉
- **游戏内设置页**（新）：设置 → Mod 设置 → 鲸鱼爬塔，改模型 / 皮套 / 直播参数，
  不用再手改 json（三个 json 仍然是唯一真相，手改照样有效）
- **F8 可重绑**（新）：策略助手面板的快捷键从每帧轮询换成 RitsuLib 热键服务，
  玩家能在设置里改键，也支持 Steam Input
- **本局记忆换存储**（新）：`run_memory.json` 交给 RitsuLib 的 `ModDataStore`
  （全局作用域、崩溃可续），升级后首次启动会自动从旧文件迁移
- **日志进游戏通道**（新）：`agent_bridge.log` 之外同时写游戏日志，
  玩家出问题发一份日志就能定位
- **Debug Tools 里多一个「鲸鱼桥接」页**（新）：不用开浏览器也能看桥接最近 200 条消息

### 从 0.7 升级

- 直接覆盖安装即可，配置文件和 `vts_token.json` 都保留
- `run_memory.json` 会自动迁移到新存储（旧的留着不动，可以删）
- 皮套 / 弹幕的连接类配置改完要重启游戏才生效，模型配置即时生效

## 快速开始

```bash
# 1) 构建 + 打包
cd Sts2Whale && ./package.sh

# 2) 装进游戏（默认 Steam macOS 路径，可用 GAME_MODS=… 覆盖）
./package.sh --install

# 3) 开游戏
open "steam://rungameid/2868840"
```

前提：macOS(Apple Silicon) + Steam 版游戏（≥0.111.0）+ 创意工坊订阅 **STS2-RitsuLib**。
不想自己编的话，直接用 `_archive/releases/sts2-whale-mod-v0.14.0.zip`，见下面「安装到另一台机器」。

## 运行时配置（在用户数据目录，不在 mod 目录）

> 0.8 起这些都能在游戏里改：设置 → Mod 设置 → 鲸鱼爬塔。json 仍是唯一真相，手改也认。
>
> **0.11 起路径变了**：配置在用户数据目录，不在 mod 目录 ——
> macOS: `~/Library/Application Support/SlayTheSpire2/Sts2Whale/`
> Windows: `%APPDATA%\SlayTheSpire2\Sts2Whale\`
> 首次运行会把 mod 目录里的同名文件搬一份过去（老配置不丢）。这么做是因为创意工坊更新会覆盖 mod 目录。

| 文件 | 作用 |
|---|---|
| `llm.json` | 模型通道（`enabled / baseUrl / model / apiKey / maxTokens / timeout / minGapMs`），也可在游戏内设置页改 |
| `persona.md` | 角色卡（system prompt，改完 `POST /persona/reload`） |
| `vts.json` | 皮套：VTS 地址、表情映射、倾斜、语音引擎（见 [AVATAR.md](readme/AVATAR.md)） |
| `vts_token.json` | VTS 插件授权后自动生成，别删 |

| `tts_daemon.py` | 语音服务的脚本（mod 自己拉起，不用手动跑） |
| `live.json` | 直播弹幕：房间号、限流（弹幕页链接由房间号生成，见 [TECH.md](readme/TECH.md) 6.5） |

## 文档

| 文档 | 内容 |
|---|---|
| [BUSINESS.md](readme/BUSINESS.md) | 产品是什么、玩家看到什么、边界、成本、验收标准 |
| [TECH.md](readme/TECH.md) | 架构、线程模型、接口表、自愈机制、构建、踩坑 |
| [AVATAR.md](readme/AVATAR.md) | 皮套联动：表情映射、受击倾斜、语音、配置、调试 |

## 安装到另一台机器

目标机只需要「能跑游戏 + 有这个 zip」，不需要 .NET SDK。

```bash
unzip sts2-whale-mod-v0.14.0.zip && cd sts2-whale-mod-v0.14.0
./install.sh
# 游戏不在默认路径：GAME_MODS="/别的/路径/mods" ./install.sh
```

安装位置：`…/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/Sts2Whale/`
`install.sh` 会保留已有的 `llm.json` / `vts.json` / `live.json`。

## 版本记录

见 [CHANGELOG.md](CHANGELOG.md)（0.8.0 / 0.7.0 / 0.5.0 / 0.1.0）。

## 许可

求解器与战略助手分别是 MIT（见 `LICENSE-CombatSolver-MIT.txt` 与上游仓库）；本项目自身 MIT。

> 皮套素材（Live2D 模型）**不在分发包里**，也禁止二次分发；见 [readme/AVATAR.md](readme/AVATAR.md) 的授权一节。

## 第三方代码

本仓库直接内嵌（vendored）两个上游项目的源码，它们不是本项目原创：

| 目录 | 上游 | 许可 | 快照 |
|---|---|---|---|
| `src/solver/` | [CombatSolver](https://github.com/Torch1230/CombatSolver) | MIT（`LICENSE-CombatSolver-MIT.txt`） | `main` @ `5c773ca` |
| `src/strategist/` | [SpireStrategist](https://github.com/shaokangqian4-art/SpireStrategist) | MIT（`LICENSE-SpireStrategist-MIT.txt`） | v0.6.22 |

每个目录下都有 `VENDORED.md`，写明上游地址、快照版本和本地改动了哪几个文件。
