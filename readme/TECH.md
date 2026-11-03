# 技术文档

> 代码怎么组织、关键机制怎么运作、改的时候别踩什么。
> 产品视角看 [BUSINESS.md](BUSINESS.md)；皮套与语音细节看 [AVATAR.md](AVATAR.md)。

## 1. 一句话架构

**一个程序集，跑在游戏进程内**，开一个本机 HTTP 服务；三个大脑共享同一个进程和同一棵场景树，
外加 `src/whale` 把游戏事件翻译成 VTube Studio 指令，`src/bridge` 里的 `ChromeDanmaku` /
`SpeechPlayer` 负责两个「外部进程」——浏览器和 Python。

```
《杀戮尖塔2》(Godot 4.5 / .NET 9)
└── Sts2Whale.dll（唯一 mod）
    ├── src/solver/      CombatSolver     战斗：搜索 + 出牌
    ├── src/strategist/  SpireStrategist  局外：选路/选牌/商店/营火/事件
    ├── src/bridge/      鲸鱼桥接          HTTP、语义化 request/respond、气泡 UI、弹幕
    ├── src/merged/      本项目胶水        入口、策略桥、自动收尾、LLM 嘴、环境引导
    └── src/whale/       皮套导演          VTS 客户端、伤害补丁、表情、口型、语音
```

外部进程（都可选，缺了只降级）：

```
python tools/tts_daemon.py   → 127.0.0.1:48765   Edge 在线语音合成
Chrome / Edge（CDP）          → 127.0.0.1:48790   读弹幕页 DOM
VTube Studio                 → ws://127.0.0.1:8001
```

## 2. 目录与关键文件

**两套目录**（`ModPaths`）：

| | 位置 | 内容 | 谁写 |
|---|---|---|---|
| `ModDir` | dll 所在目录 | 只读资源：`tts_daemon.py`、配置模板、README | 安装/更新覆盖 |
| `DataDir` | macOS `~/Library/Application Support/SlayTheSpire2/Sts2Whale`<br>Windows `%APPDATA%\SlayTheSpire2\Sts2Whale` | `llm.json` `vts.json` `live.json` `vts_token.json` `persona.md` `run_memory.json` `run_history.jsonl` `venv/` `browser/` | 玩家与 mod |

首次运行把 `ModDir` 里的同名模板搬一份到 `DataDir`（**不覆盖已有文件**），之后只认 `DataDir`。

```
Sts2Whale/
├── Sts2Whale.csproj          合并编译（把五棵树编进一个 DLL）
├── Sts2Whale.json            mod 清单（版本、依赖、入口）
├── package.sh                构建 + 打本地包 + 安装（macOS）
├── release.sh                构建工坊包 + 生成 SteamCMD 用的 VDF
├── tools/tts_daemon.py       常驻 TTS 服务（edge-tts 后端）
├── tools/make_workshop_preview.py  生成工坊预览图（拼接，不用 AI 生图）
├── src/merged/               本项目自己的代码（改动主要在这里）
│   ├── Entry.cs              唯一 [ModInitializer]：求解器 → 藏窗口 → 战略助手 → 桥接
│   ├── ModPaths.cs           ModDir / DataDir + 一次性迁移
│   ├── RuntimeBootstrap.cs   首次启动补环境（找 Python、建 venv 装 edge-tts）
│   ├── PyRuntime.cs          跨平台 Python 探测
│   ├── StrategistBridge.cs   接管层开关/兜底/看门狗/F8
│   ├── SolverBridge.Merged.cs 直接调用求解器：状态、隐藏、认输标记
│   ├── WhaleBrain.cs         模型 API：system=persona.md → 一句话
│   ├── WhaleSettings.cs      RitsuLib 设置页（模型/角色卡/皮套/弹幕）
│   ├── AutoLoop.cs           收尾/认输/播报/开下一把
│   └── StrategistLog / StrategistRoute / StrategistDelegate
├── src/bridge/               桥接（HTTP + 各界面通道）
│   ├── HttpBridge.cs         路由表（§5）
│   ├── RequestApi.cs         request/respond 的核心
│   ├── ScreenApi.cs          点击/选牌（uid 失效按牌名重找）
│   ├── GameApi.cs            直接调游戏命令（出牌、结束回合、移动…）
│   ├── WhaleUi.cs            可拖拽气泡（Say / 40 字折行 / 位置记忆）
│   ├── ChromeDanmaku.cs      CDP 读弹幕页（§6.5）
│   ├── DanmakuBridge.cs      弹幕过滤/限流/选谁回
│   ├── BridgeNode.cs         main-thread pump（SceneTree.ProcessFrame）
│   └── …                     EventFeed / StatePush / UiClicks / Potions / FpsProbe …
├── src/whale/                ★ 皮套联动（详见 AVATAR.md）
│   ├── VtsClient.cs          WebSocket 客户端：鉴权/token、热键、exp3、参数、整模旋转
│   ├── WhaleAvatar.cs        导演层：规则、优先级、口型心跳、姿态复位
│   ├── DamageHooks.cs        Harmony：掉血 / 格挡吸收 / Block 状态
│   ├── HitReaction.cs        受击倾斜的阻尼弹簧
│   ├── IdleSway.cs           idle 摆头（Perlin + 惯性 + 软限幅）
│   ├── SpeechPlayer.cs       语音：daemon / 命令行兜底 / 包络
│   ├── TtsDaemon.cs          常驻 TTS 服务的生命周期（异步启动、自愈）
│   ├── AudioOut.cs           Godot 解码播放 mp3（非 macOS）
│   └── VtsConfig.cs          vts.json 的模型与默认值
├── src/solver/               CombatSolver（上游，vendored）
├── src/strategist/           SpireStrategist（上游，vendored）
└── readme/                   文档
```

## 3. 线程模型（最重要的一条）

- **一切 Godot 节点操作都必须在主线程**。桥接开了 `BridgePump`：
  `SceneTree.ProcessFrame` 上挂 `Drain()`，HTTP 线程通过 `BridgePump.OnMain(fn)` 投递。
- 主线程每帧跑的还有：`AgentUi / EventFeed / StatePush / StreamTicker / StrategistBridge /
  StrategistLog / AutoLoop / SolverBridge / RequestApi` 的 `Tick`。
  **皮套的补丁回调也在主线程**，所以里面只做「记账」，实际发指令交给皮套自己的 30Hz 心跳线程。
- **性能红线**：`RequestApi.Current()` 会扫场景树 + 读局面，不能每帧调（约定 5 秒节流）。
- 求解器搜索在后台线程，主线程只读快照；皮套读 `SolverBridge.Searching`（一个静态布尔）。
- 皮套的 VTS 连接、语音合成、定时器都在**后台线程**，主线程只投递。
- `TtsDaemon.Start()` 只下单不阻塞（`Task.Run`），真正说话前由 `EnsureAlive()` 等它就绪。

## 4. 三条接口边界

### 4.1 求解器（CombatSolver）

| 事实 | 位置 |
|---|---|
| 一场战斗全自动：搜世界线 → 挑最佳序列 → 逐张执行 | `SolverController` |
| 状态：`IsSearching`、`ReviewedWorldlinesTotal`、`LastStopWasDeathPrediction` | `SolverController` |
| 实时进度：`WorldlineProgressSnapshot()`（不依赖 UI 显示） | 搜索会话 `Interaction.Progress` |
| 分析窗口：`SolverOverlay.SetUserHidden(true)` 一次即永久隐藏 | 启动时 |

### 4.2 战略助手（SpireStrategist）

| 事实 | 位置 |
|---|---|
| 每局一个 `AdvisorService`（含 `Autopilot`） | `SpireStrategist.Mod` |
| `Enabled` = 是否接管局外 | `Autopilot.Enabled` |
| 自己点按钮（反射调游戏方法），不经过桥接 | `PartialAutopilot.TickCore` |
| 它接管时桥接让位：`RequestApi.YieldToAutomation` | `StrategistBridge.Tick` |
| 决定 → 事件流：`StrategistLog` 读 `Decision` 差异 | `src/merged/StrategistLog.cs` |

### 4.3 鲸鱼（agent 通道 + LLM 嘴 + 皮套）

- **agent 通道**：`GET /request` → pending → `POST /respond`（语义化，不点坐标）。
- **LLM 嘴**：`WhaleBrain` —— `persona.md` 当 system，决定原文当 user，回一句给 `WhaleUi.Say`。
  **代码不规定说话风格**（长度、句式、口吻都在角色卡里），提示词里只有事实：
  本局摘要、刚说过的话、这次是什么事件。
- **牌面**：选牌时在决定前面带一行
  `上勾拳：2 费 · 攻击牌 · 罕见 · '造成 13 点伤害，抽 1 张牌。'`。
  模型看不见牌，不给的话就会按牌名猜费用（角色卡又要求带数字）；数据来自
  `DecisionOption.Card`（`SnapshotReader.ReadCard`）在决策那一刻的快照，
  含 X 费/星费，效果文本去 BBCode、压成一行（≤60 字）。
- **气泡第二行**：`WhaleUi.SetStatus(text)` 只显示、不念。求解器搜索时由 `AutoLoop` 每 0.3 秒
  把 `SolverController.CurrentProjectedLossText()`（`预计战损 4 HP`，只剩阵亡路线时是 `未找到生还路线`）填进去，搜索结束 `ClearStatus()`；
  路线还没算到战斗结束时留空。
- **皮套**：`WhaleAvatar` 订阅 `EventFeed.Publish` + 三个 Harmony 补丁 + `WhaleUi.Say`，
  翻译成 VTS 的表情/口型/整模旋转。它**不调用任何 LLM**，只说已经生成好的台词。

## 5. HTTP 接口（127.0.0.1:47821）

| 分类 | 路由 |
|---|---|
| 基础 | `/health` `/state` `/dump` `/probe` `/log` |
| 决定 | `/request` `/respond` `/proceed` |
| 战斗 | `/acts` `/act` `/solver` `/solver/wait` `/solver/dump` `/solver/ui` `/solver/worldlines` |
| 地图 | `/map` `/map/next` |
| 卡牌/遗物/药水 | `/card` `/relic` `/potions` `/potion` `/potion/use` `/potion/discard` |
| UI 兜底 | `/screen` `/tap` `/click` `/click_at` `/drag` `/key` `/ui` `/event/advance` |
| 战略助手 | `/strategist` `/strategist/log` `/strategist/ui` `/strategist/pause` |
| 鲸鱼 | `/whale/say` `/whale/state` `/whale/hide` `/whale/reload` `/whale/brain` `/whale/brain/test` |
| 皮套 | `/avatar/state` `/avatar/test?emotion=` `/avatar/hit?damage=` |
| 弹幕 | `/danmaku`（源/连接/实际读取的页面/收到数/上次回复/上次错误） |
| 人设 | `/persona` `/persona/reload` |
| 流程 | `/save-quit`（存档回菜单） `/quit`（存档并退出游戏） `/reload` `/loop/next-run` |

`/request` 的 kind：`menu / reward / treasure / shop / rest / event / gameover / select /
upgrade / remove / transform / simple / discard / exhaust / bundle / potion / divination / proceed`。

## 6. 自愈机制

| 机制 | 触发 | 实现 | 日志 |
|---|---|---|---|
| 鲸鱼兜底 | 助手 `Pause` | 按它的策略答当前 pending | `whale fallback:` |
| 卡住看门狗 | 同一请求挂 >15 秒 | `force:true`，按 `min` 补齐 | `stuck watchdog:` |
| 商店离店 | 没东西可买 | `LeaveShopHook` 点「前进」 | `leave-shop:` |
| 事件推进 | 事件房 10 秒没 pending | `UiClicks.ClickLongestText()` | `event 卡住` |
| 收尾 | `IsGameOver` | 关助手 → 关地图 → gameover 页 | `auto-loop:` |
| 开下一把 | 主菜单 + `_wantNextRun` | 菜单四页依次答 | `auto-loop: 开局` |
| 投子认负 | `LastStopWasDeathPrediction` | 播报后 2.5s `ForceEndTurn()` | `last stand:` |
| 皮套断线 | VTS 关闭/重连 | 3 秒重试；token 失效才重新申请（30 秒限流） | `[vts]` |
| TTS daemon 掉了 | `EnsureAlive()` 探活失败 | 重拉 daemon；还不行回落命令行 `edge-tts` | `[tts]` |
| 弹幕页丢了 | `/json/list` 里没有匹配页面 | 报告错误，等玩家再点「打开弹幕页」 | `[danmaku]` |

## 6.5 直播弹幕

```
弹幕页（Laplace OBS Widget）
  ← Chrome / Edge（玩家点按钮打开，带 --remote-debugging-port=48790）
  ← ChromeDanmaku：GET /json/list → WebSocket → Runtime.evaluate 读 DOM
  ← DanmakuBridge：差分 → 过滤 → 限流 → WhaleBrain.ReplyToDanmaku → WhaleUi.Say
```

- **玩家点按钮才开**：设置页「打开弹幕页」用固定端口 48790 + 独立 profile
  （`<DataDir>/browser`）打开页面，窗口可见。mod **不会自己拉浏览器** ——
  两个实例共享同一个 profile 时会抢 `SingletonLock`，表现为反复重开、留下孤儿进程。
- **端口活着时只连不拉**：手动打开浏览器后，若端口上已有实例但缺少这个页签，
  用 CDP `PUT /json/new?<url>` 在现有实例里开标签页；只有端口没人监听才清残留锁并启动。
- **严格按房间号匹配页签**：URL 等于 `https://chat.laplace.live/obs/<roomId>`，
  或其后紧跟 `?` / `&` / `/`（`/obs/10000000` 不会误命中 `/obs/100000001`）。
- **差分**：取 `.event.event--message` 最后 30 条，用「后缀 vs 前缀」找重叠，多出来的就是新弹幕；
  首次只建基线，不重播页面里已有的内容。
- **限流**：滚动 60 秒最多 N 条（默认 3）；同一句 5 分钟内不重复回；纯符号/表情跳过。
- **选谁回**：带问号 +5 分，含「鲸鱼/爬塔/主播/你好/为什么…」+3 分，长度 4~24 +1 分。
- **上下文**：记住最近 3 轮「谁问了什么 → 她怎么答的」，拼成 3 行塞进**同一条 user 消息**
  （整段硬上限 240 字，约 60~150 token），不另开 assistant 轮次、不重复 system。
  这一轮在她**真说出话之后**才记：超时、被新弹幕顶掉的那条不会留下「没答的问题」。
  弹幕场景不再重复带「你刚说过」（内容已经在问答里）；`/danmaku` 的 `recentContext` 可看这份上下文。
- **换房间**：重置基线、换目标页，不用重启游戏。
- 排查：`/danmaku` 的 `pageUrl` 是实际读到的页面，`lastError` 区分
  「没找到浏览器」「浏览器开着但没有这个页面」「连不上调试端口」。

## 6.6 本局记忆（RunMemory）

给鲸鱼娘的提示词带一份本局摘要（约 150 token）：

```
【本局】铁甲战士 A10 · 第 31 层 · 房间 精英 · 91/122 血 · 金币 768 · 卡组 22 张 · 遗物 14 件
【最近】选牌：拿剑柄打击 / 商店：买无色药水 / 战斗：掉 12 血收工
【回顾】…（留空）
【你刚说过】…
```

- **数据来源**：`RunManager` 状态（2 秒读一次）、`StrategistLog` 的决定、战斗结束播报
- **落盘**：`run_memory.json`（最近 40 条事件 + 回顾）；全量流水写 `run_history.jsonl`
- **换局自动清空**：角色/进阶变化时重置
- **新鲜度校验**：5 秒内没读到实时状态就不注入（避免把上一局的数字喂给模型）
- 调试：`curl -s 127.0.0.1:47821/memory`

## 6.7 连胜 HUD（右上角）

`src/bridge/StreakHud.cs`：一块常驻的小面板，每个职业一行「当前连胜 / 最高连胜」。

```
存档进度 SaveManager.Instance.Progress.CharacterStats
  ← CurrentWinStreak / BestWinStreak（和游戏统计页同源，结算完立刻是新值）
  ← ModelDb.AllCharacters（只取 IsPlayable，模组角色也会出现）
  ← 每 10 秒读一次 → Label（右上角，游戏顶部栏按钮正下方）
```

- **读的是内存里的存档进度，不解析 `progress.save`**：不用管 modded / 原版两套存档目录，也不怕文件写到一半
- **位置跟着游戏自己走**：`NRun.Instance.GlobalUi.TopBar.Pause`（右上角那颗齿轮）→
  `GetGlobalTransformWithCanvas()` 换算成视口矩形 → 面板右对齐贴在它下面
- **什么时候不显示**：主菜单 / 读档（没有 TopBar）、菜单或牌组浮层压上来（`NOverlayStack.ScreenCount > 0`）
- **顺序**：铁甲战士 → 静默猎手 → 故障机器人 → 储君 → 亡灵契约师，模组角色按 id 排在后面
- 调试：`curl -s 127.0.0.1:47821/streak`（`text` / `visible` / `anchorRect` / `panelRect`）

## 6.8 语音链路

```
WhaleUi.Say → WhaleAvatar.OnSpeech → SpeechPlayer.Speak
   ├── TryDaemon：POST 127.0.0.1:48765/say
   │     └── tts_daemon.py（Python + edge-tts）
   │           macOS → afconvert 转 24k mono wav（可算精确包络）
   │           其他   → 直接返回 mp3
   ├── 兜底 TryEdge：命令行 edge-tts
   │     macOS → afconvert → wav；其他 → mp3
   └── 都不行 → TextEnvelope（只动嘴，不出声）
```

播放：

| 音频 | 平台 | 方式 |
|---|---|---|
| wav | macOS | `afplay` |
| mp3 | 全部 | `AudioOut`：Godot `AudioStreamMP3` 解码播放 |

- **包络（口型）**：wav 用 `EnvelopeFromWav` 算每 30ms 的 RMS；mp3 用 `TextEnvelope` 按字数估。
- **daemon 生命周期**：`Start()` 异步拉起（`--port --parent --idle --voice`），
  `EnsureAlive()` 在说话前探活并重拉；`--parent` 让它在游戏退出后自杀
  （POSIX 用 `kill(pid,0)` 探活；**Windows 必须用 `OpenProcess(SYNCHRONIZE)` + `WaitForSingleObject`**，
  `os.kill` 在 Windows 上不是探活）。
- **空闲 30 分钟自退**：之后本局走命令行兜底（能出声，每句慢到 3~4 秒），下次启动再拉起。
- **首次启动补环境**（`RuntimeBootstrap`）：找不到带 `edge_tts` 的 Python 时，
  在 `DataDir/venv` 建独立环境并 `pip install edge-tts`，装完把绝对路径写回
  `vts.json` 的 `speech.daemonPython` / `speech.edgeCommand`，并立即拉起 daemon；
  失败写 `.venv-install-failed` 标记，6 小时内不重试。

## 7. 依赖

### 7.1 运行时

| 依赖 | 要求 | 必需性 | 缺失时 |
|---|---|---|---|
| 游戏《杀戮尖塔 2》 | ≥ 0.111.0 | 必需 | —— |
| `STS2-RitsuLib` | ≥ 0.6.10 | **必需**（manifest 声明） | mod 不加载 |
| .NET 9 运行时 | 9.0.x | 必需 | 游戏自带，无需另装 |
| Python | 3.8+（实测 3.12） | 语音 | 只有字幕 |
| `edge-tts` | 实测 7.3.1 | 语音 | 同上（首次启动会自动装） |
| DeepSeek API key | —— | 可选 | 只有本地垫话 |
| VTube Studio | —— | 可选 | 无皮套联动，语音照常 |
| Chrome / Edge / Chromium | —— | 弹幕 | 无弹幕 |
| BaseLib | —— | 可选 | 求解器少一层兼容，正常跑 |

用到 RitsuLib 的东西：`RitsuToastService`、`ModDataStore`（本局记忆）、游戏内设置页、
热键服务、`CurrentHpChangedEvent` / `AttackEndedEvent` / `Block*Event` 等生命周期事件。

### 7.2 构建

| 项 | 版本 | 说明 |
|---|---|---|
| .NET SDK | 9.0.x | `TargetFramework=net9.0`；7.0 / 3.0 编不了 |
| `Godot.NET.Sdk` | 4.5.1 | csproj 的 `Sdk=` 拉取 |
| `Krafs.Publicizer` | 2.3.0 | 把 `sts2.dll` 的 internal 公开化 |
| `sts2.dll` / `0Harmony.dll` / `GodotSharp.dll` | 随游戏 | 构建引用，不随包分发 |
| RitsuLib 参考程序集 | 工坊 `3747602295/RitsuLib.References.props` | 自动引用 `compat/<ver>/*` 与 `shared/*` |

关键 MSBuild 属性（csproj 有 macOS 默认值，其他平台显式传）：

```
-p:Sts2Dir=<游戏根目录>
-p:Sts2DataDir=<游戏根目录>/data_sts2_windows_x86_64   （macOS 是 .../Resources/data_sts2_macos_arm64）
-p:RitsuWorkshopRoot=<工坊>/3747602295
```

`.NET` 源：只需要公开的 `Godot.NET.Sdk` 和 `Krafs.Publicizer`，都来自 nuget.org；
仓库不带 `NuGet.Config`，用各机器自己的配置。

## 8. 构建与发布

```bash
# macOS：本地包 + 安装
cd <repo>/Sts2Whale
./package.sh              # → _archive/releases/sts2-whale-mod-v<ver>/ 与同名 zip
./package.sh --install    # 再装进 <游戏>/mods/Sts2Whale（保留用户配置）

# 工坊包（含 VDF，供 SteamCMD 上传）
./release.sh              # → _archive/releases/workshop/sts2-whale-workshop-<ver>/
                          #   + build-<ver>.vdf（publishedfileid 来自 workshop-id.txt）

# Windows（没有 bash，直接调 dotnet；产物放临时目录，别落在仓库里）
dotnet build Sts2Whale.csproj -c Release `
  -p:Sts2Dir="<游戏根目录>" `
  -p:Sts2DataDir="<游戏根目录>\data_sts2_windows_x86_64" `
  -p:RitsuWorkshopRoot="<工坊>\3747602295" `
  -p:OutputPath="$env:TEMP\sts2whale-build\bin\Release\" `
  -p:BaseIntermediateOutputPath="$env:TEMP\sts2whale-build\obj\"
# 然后把 Sts2Whale.dll / Sts2Whale.json / tts_daemon.py / persona.md /
# llm.json / vts.json / live.json / LICENSE-CombatSolver-MIT.txt 拷进 <游戏>\mods\Sts2Whale
# 注意：先删再拷（新 inode），原地覆盖被 mmap 的 dll 会 Bad IL format
```

- `package.sh` 自动找 `<repo>/.dotnet` 的 SDK；用 `DOTNET_ROOT=` / `STS2_DATA=` / `GAME_MODS=` 覆盖。
- **发布包会清掉本机痕迹**：`llm.json` 的 `apiKey`、`live.json` 的 `enabled/roomId`、
  `vts.json` 的 `daemonPython/edgeCommand` 一律置空；`release.sh` 末尾有自检，
  扫 token/key/本机路径，有残留就失败。
- 工坊上传：`workshop_build_item <vdf>`（VDF 里带 `publishedfileid` 时是更新）。
- 更新已安装的 dll 用 `rm` + `cp`（新 inode）。原地覆盖正在被 mmap 的文件会让游戏报
  `BadImageFormatException: Bad IL range`。

## 9. 改代码时的硬约束（踩过的坑）

1. **别每帧调 `RequestApi.Current()`** —— 扫场景 + 读局面，5 秒节流。
   节流要把**整段探测**包进 `if (now >= nextAt)`，只更新时间戳不算。
2. **选牌 uid 会过期** —— `ScreenApi.ResolveCardUid` 按牌名重找，多选要 `exclude` 已点过的。
3. **多选必须给够 `min` 条**，否则界面永远不确认。
4. **关地图只能 `NMapScreen.Close(bool)`**（`Hide()` 只藏节点，`IsOpen` 仍是 true）。
5. **死亡瞬间有一帧窗口**：结束后立刻关助手，否则地图会压住收尾界面。
6. **别在选牌/商店删牌界面挂着时 `save-quit` / 强退** —— 会踩 Harmony 补丁导致 `Bad IL range`。
7. **`/start` 是陷阱**：只造幽灵局，开新局只能走菜单协议（`/request` → `/respond`）。
8. **战斗内别让桥接抢出牌**：`solver.blocking == true` 时出牌接口返回 `solver_full_auto`。
9. **中文 curl 要 UTF-8**（`-H "Content-Type: application/json"`）。
10. **皮套补丁里绝不能抛异常**，否则会把游戏的伤害/格挡结算带崩（一律 `try/catch` 吞掉）。
11. **VTS 的 `ExpressionActivationRequest` 要 `expressionFile`（`xxx.exp3.json`）**，不是显示名。
12. **VTS 参数有归属权**：同一参数被别的插件占着会返回 `errorID 454`；同时只跑一个客户端。
13. **角色卡是唯一的话语规则来源**：提示词里不要再加「只说一句」「多少字」这类约束，
    要改风格就改 `persona.md`（设置页可编辑）。
14. **改 `live.json` 的数值键要写成数字**：设置页曾把它写成字符串，导致 `GetInt64()` 抛
    `requires an element of type 'Number'`。读取侧现在两种都认。

## 10. 常用调试

```bash
curl -s 127.0.0.1:47821/state | jq '{inRun,room,act,solver}'
curl -s 127.0.0.1:47821/request
curl -s 127.0.0.1:47821/solver/worldlines
curl -s 127.0.0.1:47821/state | jq .fps          # 帧时间 / 各 tick 耗时（排障卡顿）
curl -s 127.0.0.1:47821/avatar/state | jq '{enabled,authenticated,events,cues,lastDecision}'
curl -s 127.0.0.1:47821/danmaku | jq '{enabled,roomId,connected,pageUrl,lastError}'
curl -s 127.0.0.1:48765/health                    # TTS daemon（requests 递增说明活着）
curl -s "127.0.0.1:47821/avatar/test?emotion=attack"
curl -s "127.0.0.1:47821/avatar/hit?damage=40"
curl -X POST 127.0.0.1:47821/quit                 # 存档并退出游戏
tail -f ~/Library/Application\ Support/SlayTheSpire2/logs/agent_bridge.log   # 8 MB 轮转，历史 .1/.2
```

端口速查：

| 东西 | 端口 / 位置 |
|---|---|
| 桥接 HTTP | 47821 |
| TTS daemon | 48765（`/health` `/say` `/shutdown`） |
| 弹幕 CDP | 48790 |
| VTube Studio | 8001 |
| 音频缓存 | `<temp>/sts2whale-tts`（`tts_*.mp3/wav` = daemon，`edge_*.mp3/wav` = 命令行） |

## 11. 环境变量

| 变量 | 作用 |
|---|---|
| `STS2_AUTO_NEXT_RUN=0` | 关掉「结束后自动开下一把」 |
| `STS2_AUTO_CHARACTER` / `STS2_AUTO_ASCENSION` | 下一把角色 / 进阶（默认 `ironclad#1` / `10`） |
| `STS2_STRATEGIST_AUTO=0` | 关掉战略助手自动接管 |
| `STS2_STRATEGIST_SAY=0` | 关掉「助手决定 → 模板短句」播报 |
| `STS2_AGENT_SELECTOR=0` | 代理模式下不接管卡牌选择（调试） |
| `STS2_SOLVER_UNLOCK=1` | 解锁求解器开关（排障用） |
| `STS2_LLM_BASE` / `STS2_LLM_MODEL` | 覆盖端点/模型（**apiKey 不读环境变量，只认 llm.json**） |

## 12. 代码阅读顺序

1. `src/merged/Entry.cs` —— 启动顺序。
2. `src/bridge/BridgeNode.cs` 的 `Drain()` —— 每帧都在跑什么。
3. `src/bridge/RequestApi.cs` 的 `Current()` —— pending 怎么被识别。
4. `src/merged/StrategistBridge.cs` —— 接管/让位/兜底/看门狗。
5. `src/merged/AutoLoop.cs` —— 收尾、认输、播报、下一把。
6. `src/merged/WhaleBrain.cs` —— 一次「说话」的完整请求。
7. `src/whale/WhaleAvatar.cs` —— 皮套的规则与优先级（配合 [AVATAR.md](AVATAR.md)）。
8. `src/whale/SpeechPlayer.cs` + `TtsDaemon.cs` —— 语音链路。
9. `src/bridge/ChromeDanmaku.cs` —— 弹幕读数。
