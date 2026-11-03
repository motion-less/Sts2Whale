# 更新日志

## 0.16.1（2026-11-03）

### 新增
- **右上角连胜面板**：游戏顶部栏的设置/暂停按钮下面常驻一块小字，每个职业一行
  「当前连胜 / 最高连胜」，格式 `铁甲战士：30/90`。
  - 数据直接读 `SaveManager.Progress.CharacterStats` 的 `CurrentWinStreak` / `BestWinStreak`
    （和游戏统计页同源，结算完立刻更新），不解析 `progress.save`
  - 位置跟着真实的按钮矩形走，不写死坐标；主菜单、菜单浮层打开时自动隐藏
  - 五个原生职业固定顺序，模组角色自动追加；`/streak` 可看实际文字和落点
  - 每 10 秒才读一次存档统计；位置只在按钮/窗口变化时重算，平时只做一次很轻的可见性判断

### 改进
- **去掉换幕回顾**：`【回顾】` 不再生成、不再写进本局摘要，也不再持久化（旧存档里的 `recap` 字段直接忽略）。
- **金币进账 < 100 不再记录/播报**：战斗奖励这类小额金币不再叫鲸鱼娘说话，也不进 `【最近】`；
  支出仍按原阈值（≥25）记录，进账 ≥100 才播。
- **弹幕回复上下文保证带 `【最近】`**：`RunMemory.Digest` 正常已包含该段；
  不在局内 / 记忆还没热时用 `RecentEventsText()` 补一段，已包含则不重复。
- **鲸鱼娘的「本局」摘要加入连胜**：`【本局】` 末尾多两项
  `当前连胜 N · 最大连胜 M`，数据取存档 `CharacterStats`（和右上角连胜面板同源）。
- **鲸鱼娘记住最近三轮弹幕**：以前每次回复只带「她刚说过的话」，不知道谁问过什么；
  现在 `DanmakuBridge` 留最近 3 轮「谁：问题 → 你：回答」，拼进同一条 user 消息当上下文。
  - **省 token**：不另开 assistant 轮次（省掉 role 包装），固定 3 轮 + 单段截断，
    整段硬上限 240 字（约 60~150 token），不会随直播时长膨胀
  - **只记真答过的**：模型超时、或被后一条弹幕顶掉的那条不记账，
    上下文里不会出现「有人问过、她没答」的问题
  - 弹幕场景不再重复带「你刚说过」（内容已在问答里），其他场景从 6 句减到 4 句
  - `/danmaku` 新增 `recentContext` 字段，可直接看到喂给模型的上下文
- **`agent_bridge.log` 加大小轮转**：8 MB 切一份，保留 `.1` / `.2` 两份历史。
  以前只 append 不轮转，实测已经涨到 87 MB；现在写满自动 `当前 → .1 → .2`，更旧的删掉
- **鲸鱼气泡第二行显示「当前路线摘要」**：求解器搜世界线时，第一行仍是「已观测 N 条世界线（M 条/s）」，
  第二行显示当前最佳候选的预计战损：`预计战损 4 HP`。
  - 数据来自搜索会话的 `SpeculativeRoutePreview` / `CurrentTurnPreview`
    （`SolverController.CurrentProjectedLossText`），**不依赖求解器窗口是否显示**
  - 只剩阵亡路线时第二行改显示浮层同一句「未找到生还路线」；
    还没算到能结束战斗时第二行不显示（不拿「未知」占位）；搜索结束自动收起
  - 第二行只显示、不念（不触发皮套口型/语音）；主台词出现时自动清掉，不残留

### 新增
- **求解器只剩阵亡路线时播放失败曲**：`SolverResult.OnlyDeathRoutesFound` 一出现就开始播放（循环），
  角色死亡（HP ≤ 0 / 战斗结束）后 3 秒淡出停止；中途重算出存活路线则 1 秒淡出。
  - 触发点是求解器每轮的新结果（`SolverBridge.PublishPlan`），不是整局结束；
    搜索途中浮层显示的「未找到生还路线」只是候选预览（`SpeculativeRoutePreview`），**不会放歌**，
    只有搜索结束后的结果才会触发
  - 盯死亡走 RitsuLib 的 `CombatEndedEvent`（事件触发，零轮询）；
    另留一个 **10 秒**的兜底检查，防止事件没到导致音乐一直响
  - 音频不随包分发：优先读玩家数据目录 `<DataDir>/guanyu.mp3`，其次 `<ModDir>/assets/guanyu.mp3`；
    也认 `.ogg` / `.wav` 和 `关羽之歌.*`
  - 文件缺失只在日志提示一次（`[fanfare]`），不影响游戏

### 改进
- **`EventFeed` 不再每 400ms 全场景扫描**：先用「房间 / 浮层数 / 战斗 / 地图 / 层数」的
  cheap signature 判断，只有变化时才做完整的 `ScreenApi.Inspect()`，另留 2 秒兜底；
  选牌事件（`CardSelectCmd`）会主动 `RequestScan()`，不用等兜底。

### 修复
- **macOS 求解器内存条不再显示「系统可用未知」**：新增 macOS 的物理内存采样
  （`host_statistics64` 的 free + inactive + speculative，口径接近活动监视器），
  不再依赖只含 free 的 Godot `available`（那会把 4 GB 可用显示成 0.2 GB）。
- 新增 `GET /solver/memory` 调试快照：进程工作集 / 物理总量 / 系统可用 / 是否物理采样 / GC 模式。
- **鲸鱼娘报错选牌费用**：战略助手只发「牌名 + 理由」，模型看不见牌面，只能按牌名猜费用，
  角色卡又要求「理由带数字」→ 经常出现 0 费/1 费/4 费乱说。
  现在把 `DecisionOption.Card`（`SnapshotReader.ReadCard` 的快照）里的真值一起带上，
  决定正文保持原样，前面多一行牌面：
  `上勾拳：2 费 · 攻击牌 · 罕见 · '造成 13 点伤害，抽 1 张牌。'`
  - 效果文本去 BBCode、压成一行、最多 60 字
  - 支持 `X 费`、星费（`+ 2 星`）；「跳过」没有牌面，不硬塞
  - 没有 API key 时的模板句也带上费用（`拿上勾拳 2 费 …`）
  - 实测同一张 2 费牌：旧提示 4 次里 2 次报错（4 费 / 1 费，另 1 次没提费用），
    加事实后 4 次全对；0 费 / 1 费 / 3 费各测 3 次也全对
- **`autoConfigureServerGc` 在 macOS 上从未生效**：启动时的路径判断把
  `Contents/Resources/data_…/sts2.dll` 当成「可执行文件目录外」（相对 `Contents/MacOS` 是 `../Resources/…`），
  直接放弃写 runtimeconfig，日志一直是 `RUNTIME_GC_STARTUP failed: game assembly is outside the executable directory`
  且 `server_gc=false`。现在按安装根判断：普通布局用可执行文件目录，macOS `.app` 额外允许 `Contents/` 下的程序集；
  实测 bundle 内兄弟目录 / 同目录 / 子目录放行，bundle 外、`..` 逃逸、前缀相同的兄弟目录仍拒绝。
  - 写盘逻辑补了 `Prepared → AlreadyConfigured → Restored` 往返验证（`System.GC.Server` 正确增删）
  - 注意：**写完后要再启动一次游戏才生效**（第一次写配置，第二次才用上 Server GC）

## 0.16.0（2026-11-01）

### 升级
- **SpireStrategist v0.6.22 → v0.6.55**（跨 33 个版本）
  - 新增 Regent / Necrobinder / Silent / Defect 四个角色的策略与先验数据
  - 新增 9 个必须内嵌的资源文件，`Sts2Whale.csproj` 已同步补齐
    （**漏掉这些编译照样过，运行时读资源才会报错**）
  - 本地 8 处改动全部保留：命名空间过滤补丁、面板默认隐藏、`LeaveShopHook`、
    `LastShopPlan`、三幕清仓、卡面描述兜底、`SetUserHidden`、`RunOver` 守卫
  - 商店逻辑：上游新加的 `shopFoulUses`（金币换药水）和本地的三幕清仓并存，
    两者的复位合并成一次（否则第二个 `if` 永远进不去）

### 修复
- 同一个 `AutoLoop` 闩锁修复（0.15.3）

## 0.15.3（2026-11-01）

### 修复
- **局内每 5 秒误判「本局已结束」，反复关掉战略助手 → 求解器跟着停手**：
  `AutoLoop` 的 `_wantNextRun` 是个单向闩锁，上一把结束时置 true 之后没有任何地方清它。
  于是只要人还在局里，每 5 秒就会走一遍收尾分支 → `DisableForRunEnd()` 把接管 Pause 掉，
  下一帧又因为「没结束」把标记复位 → 无限循环。
  现在局内（`RunManager.IsInProgress`）会直接清掉这个意图并 return，不再进收尾分支。

## 0.15.2（2026-11-01）

### 改进
- **设置页的 API Key 不再明文显示**：输入框改成圆点（`LineEdit.Secret`），
  并且**不回填已保存的值** —— 控件里始终是空的，界面上不会出现 key 的任何部分
  - 只提示「已配置（51 位）」或「未配置」
  - 配「保存」「清空」，保存后立刻把输入框清空
  - 按 key 长度而不是 key 本身做状态提示，方便确认存没存上

## 0.15.1（2026-11-01）

Windows 实机验证后合并的语音链路修复。

- daemon 在 Windows 上的父进程看护（`os.kill` → `OpenProcess` + `WaitForSingleObject`）
- 命令行兜底在非 macOS 上直接放 mp3（不再依赖 `afconvert`）
- `TtsDaemon` 启动改为不阻塞主线程，并在说话前探活重拉
- VTS 未连接时「只出声一次」的问题
- 失败路径补日志

## 0.15.0（2026-11-01）

### 新增（语音跨平台）
- **Windows / Linux 也能出声了**：以前 `SpeechPlayer.Speak` 有 `IsMacOS()` 闸门，
  非 macOS 直接跳过整条语音链路
- **去掉了两处 macOS 专属命令**
  - 合成端：`tts_daemon.py` 在 macOS 上用 `afconvert` 转 24k wav（精确口型），
    其他平台直接返回 mp3（`audio/mpeg`）
  - 播放端：新增 `AudioOut`，用 Godot 内置的 `AudioStreamMP3` 放 mp3；
    macOS 的 wav 仍然走原来的 `afplay`（不改变已验证的行为）
- C# 侧按文件头（`RIFF`）自动判断音频类型，两种都能处理
- 口型：wav 走真实包络；mp3 按字数估包络（有声音、嘴也动，精度略低）

## 0.14.8（2026-11-01）

### 调整
- 气泡折行阈值 **20 字 → 40 字**（`WhaleUi.CharsPerLine`）
  - 一句话基本都能单行放完，只有特别长的才折成两行
  - 宽度上限仍受视口约束，窄屏会自动缩字号

## 0.14.7（2026-11-01）

### 变更（气泡不截断，改成折行）
- **气泡不再截断长句**：`FirstSentence` 去掉了「在 ｜/换行处截断」和「最多 30 字」两条限制，
  只保留去空白、去包裹引号；内容原样显示
- **超过 20 个字自动换行**：`WhaleUi` 每 20 字插一个换行，气泡高度按行数算
  （`fontSize × 1.35 × 行数 + 24`），宽度按「最长一行」算
- 字号自适应改成按最长一行计算 —— 以前是"句越长字越小"，现在是"20 字固定，多出来的往下排"
- 模型输出里原有的换行会保留

## 0.14.6（2026-11-01）

### 调整
- 人设末尾保留一句收尾，但缩短成：`——以上是你是谁。别复述规则。`
  - 0.14.5 里把它整句删了；实测还是需要一句「别把设定当台词念」的约束
  - 只保留「别复述规则」，不再夹带「只回一句话」这类格式要求

## 0.14.5（2026-11-01）

### 变更（代码与角色卡解耦）
- **提示词里不再规定「怎么说」**：删掉 `AskAsync` 里那串
  「只说一句 / 8~25 字 / 不要分句 / 不要提 AI / 不要编造数字」。
  现在 user 消息只放**事实和上下文**（本局摘要、刚说过的话、这次是什么事件），
  口吻、长度、句式全部交给 `persona.md`
- **不再往人设里追加规则**：`LoadPersona` 原样返回角色卡（以前会在末尾拼
  「——以上是你是谁。下面只回一句话，别复述规则。」）
- 兜底人设也精简成一句「你是谁」，不再带格式要求
- 删掉 TECH.md 里「分句播报」那一节 —— 那段描述的 `SplitLines` 功能早已不存在，
  代码实际是「取第一句」（`FirstSentence` 在 ｜ / 换行处截断）

### 说明
- 副作用：模型现在完全按角色卡的约束说话。如果角色卡没写长度限制，
  可能出现偏长的句子（气泡宽度会自动截断，不会溢出）

## 0.14.4（2026-11-01）

### 新增
- **角色卡可以在设置页里编辑**：多行编辑器直接改 `persona.md`，配「保存」和「恢复默认」
  - 保存后立刻 `WhaleBrain.Reload()`，下一句话就用新人设，不用重启游戏
  - 「恢复默认」从包里的模板拷回来
  - 内容只决定「怎么说话」，不影响求解器/战略助手的决策

## 0.14.3（2026-11-01）

### 修复
- **点「打开弹幕页」没反应**：浏览器已经在跑时，再启动一个进程会撞 `SingletonLock`，
  新进程直接退出、URL 也就没被打开；更糟的是可能留下**两个实例抢同一个端口**
  （一个占 IPv4、一个占 IPv6，`127.0.0.1` 打到的是没页面的那个）
  - 改成：调试端口活着就用 CDP `PUT /json/new?<url>` 在**现有实例里开标签页**，不再起新进程
  - 只有端口死了才清锁 + 启动浏览器
  - 错误信息区分「没开浏览器」和「浏览器开着但没有这个页面」

## 0.14.2（2026-11-01）

### 修复（都是「新装第一次用」的体验问题）
- **新装后弹幕点了按钮也不读**：`live.json` 模板是 `enabled:false`，而 `enabled` 只在启动时读一次，
  于是浏览器开好了、模块却拒绝工作（`/danmaku` 显示 `enabled:false, roomId:0`）
  - 新增 `DanmakuBridge.EnsureRunning()`：点「打开弹幕页」、或打开「启用弹幕互动」开关时，
    当场把轮询线程拉起来，不用重启
  - `Retarget` 不再因为 `enabled=false` 直接返回
- **新装后没声音，还必须重启一次**：`WhaleAvatar` 启动时 venv 还没装好，放弃了就再也不试
  - `RuntimeBootstrap` 装完 edge-tts 后**直接拉起 TTS 常驻服务**，当场出声
  - 提示文案从「重启一次游戏后出声」改成「现在就出声了，不用重启」

## 0.14.1（2026-11-01）

### 变更
- **不再自动拉起浏览器**：改成设置页的「打开弹幕页」按钮，玩家填完房间号点一下即可
  - 原因：自动拉起实测会踩坑——随机端口 + 共享 profile 导致 `SingletonLock` 冲突，
    日志里每 2 秒重开一个浏览器，游戏退出后还留一堆孤儿进程
  - 现在用**固定调试端口 48790** + 独立 profile（`<userData>/Sts2Whale/browser`），窗口可见
  - 启动前清掉残留的 `SingletonLock/SingletonSocket/SingletonCookie`
  - 浏览器由玩家自己开、自己关；mod 只连不杀

## 0.14.0（2026-11-01）

### 变更（换驱动方式）
- **弹幕改成「无头 Chrome + DevTools Protocol」**，Safari/AppleScript 那条整个删除
  - 不再需要玩家在 Safari 里勾「显示网页开发者功能」「允许 Apple 事件中的 JavaScript」
  - mod 自己拉起 `--headless=new` 的独立实例（独立 profile），玩家不用先开浏览器
  - 纯 HTTP + WebSocket（`/json/list` → `Runtime.evaluate`），Windows 上 Edge 同样可用
  - 浏览器查找：macOS 四大 Chromium 浏览器；Windows `Program Files*` / `LocalAppData`；Linux PATH
  - 无头实例随游戏退出一起收掉
  - 代价：电脑上要有 Chrome 或 Edge（Windows 自带）；无头实例常驻约 150–250MB 内存

## 0.13.1（2026-11-01）

### 改进
- **Python 探测跨平台**：候选列表按系统分流
  - macOS/Linux：`/usr/bin/python3`（系统自带）**最优先** → PATH → homebrew → `~/Library/Python/*`
  - Windows：`%LOCALAPPDATA%\Programs\Python\Python3*\python.exe` → `Program Files` →
    `py` 启动器（自动补 `-3`）→ PATH 里的 `python3.exe` / `python.exe`
  - 候选去重且保持顺序
- venv 安装适配 Windows：`Scripts\python.exe` 而不是 `bin/python`，`edge-tts.exe` 而不是 `edge-tts`；
  pip 缺失时先用 ensurepip 补再重试
- 工坊说明页补上「环境要求」：RitsuLib / Python 3（仅语音）/ 语音与弹幕仅 macOS

## 0.13.0（2026-11-01）

### 变更（去依赖）
- **弹幕抓取去掉 Python**：`tools/danmaku_daemon.py` 整个删除，改由 `SafariDanmaku` 在进程内
  用 `osascript` 直接读 Safari 页面
  - 少一个常驻进程、少一个本地端口（48767 不再监听）、少一次 python 路径探测
  - 严格按房间号匹配标签页（+ 边界精确）、首次只建基线、1.5 秒一轮
  - `live.json` 去掉 `python` / `script` / `port` 三个键
  - 换房间不再重启任何进程，只是换个目标页
- `RuntimeBootstrap` 相应简化：只负责语音（edge_tts），弹幕不再参与
- 语音**保持原样**（仍走 edge-tts，音质优先）

## 0.12.0（2026-11-01）

### 新增
- **首次启动自动补环境**：订阅工坊后 `python` 两个路径都是空的，以前只会安静地没声音/没弹幕，
  现在启动时在后台自动处理：
  1. 找 `python3`（PATH → `/opt/homebrew/bin` → `/usr/local/bin` → `~/Library/Python/*`）
  2. 找能 `import edge_tts` 的解释器；没有就在 `DataDir/venv` 建**独立 venv** 并
     `pip install edge-tts`（实测建 venv 2 秒、装包 15 秒）
  3. 把找到的路径写回 `live.json` / `vts.json`，然后弹 RitsuLib 气泡：
     「语音组件装好了，重启一次游戏后鲸鱼娘就会出声」
  - 装不上（没网）只提示一次并写 6 小时标记，不会每次启动都卡一遍
  - 弹幕只要标准库，绝大多数机器上系统自带 `python3` 就够，无需安装

## 0.11.2（2026-11-01）

### 修复
- **新装后弹幕起不来**：发布包的 `live.json` 同样把 `python` 清空了，于是
  `[danmaku] 缺 python 或脚本（ / …）` 一直重试、48767 端口没人监听
  - 把「找 python」抽成公共的 `PyRuntime`，TTS 和弹幕共用
  - 弹幕只要标准库，所以用 `PyRuntime.Any()`（PATH 的 python3 / homebrew / ~/Library/Python）
  - TTS 用 `PyRuntime.WithModule("edge_tts")`
  - 都找不到时日志写明怎么装，不再只报「缺 python」

## 0.11.1（2026-11-01）

### 修复
- **新装后没有语音**：发布包里的 `vts.json` 把 `daemonPython` 清空了（避免带本机路径），
  于是常驻 TTS 起不来，日志只有一行 `[tts] 找不到 python：`，玩家只看到字幕没有声音
  - 新增自动探测：PATH 上的 `python3`、`/opt/homebrew/bin/python3`、`/usr/local/bin/python3`、
    `~/Library/Python/*/bin/python3`，逐个试 `import edge_tts`，取第一个能用的
  - 探测放在后台线程，不卡主线程
  - **一个都找不到时给明确提示**：「没找到装了 edge_tts 的 python，语音关闭（字幕照常）」，
    不再静默

## 0.11.0（2026-11-01）

### 变更（为发布创意工坊做准备）
- **配置和记忆搬到用户数据目录**，不再写在 mod 目录：
  - macOS `~/Library/Application Support/SlayTheSpire2/Sts2Whale/`，Windows `%APPDATA%\SlayTheSpire2\Sts2Whale\`
  - 搬过去的：`llm.json` `vts.json` `live.json` `persona.md` `vts_token.json` `run_memory.json` `run_history.jsonl`
  - 留在 mod 目录的：`tts_daemon.py` `danmaku_daemon.py` `assets/`（只读资源）
  - **首次运行自动迁移**：DataDir 里没有、mod 目录里有的文件会复制一份过去，老配置不丢
  - 起因：创意工坊更新会覆盖 mod 目录，玩家改的配置会被冲掉；Windows 上 Steam 库还可能不可写
- `ModPaths` 统一管理这两套路径，代码里不再有散落的 `Assembly.Location` 拼路径

## 0.10.8（2026-11-01）

### 修复
- **模型名统一为 `deepseek-flash`**。仓库模板原先写的是 `deepseek-v4.1-flash`，实测接口不认：
  `The supported API model names are deepseek-flash, deepseek-v4-pro, but you passed deepseek-v4.1-flash`
  ——「V4.1 Flash」是版本宣传名，不是 API id。新机器装上去第一次调用就 400
  - 模板 `llm.json` → `deepseek-flash`
  - 代码默认 `WhaleBrain.DefaultModel`（`deepseek-chat` → `deepseek-flash`）
  - 设置页该字段的兜底值与占位文案同步
  - 「模型没给正文」的提示里不再建议切到 `deepseek-chat`

## 0.10.7（2026-11-01）

### 修复
- 设置页「直播弹幕」的段说明还写着「改完要重启游戏才生效」，而且仍写着「直连 B 站弹幕」——
  两处都过时了。改成：从 Safari 的弹幕页读；**房间号即时切换，限流等其他项重启生效**

## 0.10.6（2026-11-01）

### 修复
- **换房间后 daemon 没了**：`RestartDaemon` 先 `shutdown` 旧 daemon，再检查 `/health`。
  旧进程关停的那一两百毫秒里 `/health` 还能应答，于是被判定「复用已在运行的 daemon」→
  没有起新进程；旧进程随后退出，端口就空着，`127.0.0.1:48767` 打不开
  - `EnsureDaemon(force: true)`：换房间时最多等 5 秒让端口真正空出来，再无条件起新进程
  - **自愈**：轮询连续失败 10 秒就自动重启 daemon（覆盖「它死了没人管」这类情况）

## 0.10.5（2026-11-01）

### 修复
- `/danmaku` 的 `lastError` 成功后不清空，会一直挂着过期报错（比如重启 daemon 那 1 秒的
  `Connection refused`），排查时容易误判。现在每次成功拉取都会清空

## 0.10.4（2026-11-01）

### 修复
- **切到别的标签页后就读不到弹幕**：Safari 的 AppleScript `documents` 只返回
  **最前面那个窗口的当前标签页**，不是全部标签页。所以一旦切到浏览器别的页（比如去看
  `127.0.0.1:48767/health`），弹幕页就从列表里消失，daemon 报「没找到房间 XXX 的弹幕页」
  - 枚举改成 `windows → tabs of w`，能拿到所有窗口的所有标签页
  - `do JavaScript` 也改成遍历 `windows → tabs` 找目标页

## 0.10.3（2026-11-01）

### 变更
- **严格按房间号挑 Safari 标签页**：以前是「先找 `/obs/<room>`，找不到就退回任意 laplace 页」，
  于是 `https://laplace.live/` 这种无关页面也会被当成弹幕源（等于没在按房间读）
  - 现在先列所有标签页，只认 URL 等于 `/obs/<roomId>` 或其后紧跟 `?` / `&` / `/` 的那个
  - 边界精确：`/obs/10000000` 不会误命中 `/obs/100000001`
  - **找不到就不读**，`/health` 的 `lastError` 直接写「没找到房间 XXX 的弹幕页：<链接>」
  - 页面找到但取不到内容时单独报「检查 Safari → 开发 → 允许 Apple 事件中的 JavaScript」
  - `/health` 增加 `pageUrl`，回报实际读的是哪一页

## 0.10.2（2026-11-01）

### 修复
- **改房间号后整个弹幕模块起不来**：设置页把 `roomId` 写成字符串（`"10000000"`），
  而 `DanmakuBridge.Start()` 用 `GetInt64()` 读，抛
  `InvalidOperationException: The requested operation requires an element of type 'Number'`
  → 被外层 catch 吞掉，表现为 `/danmaku` 里 `enabled:false, roomId:0`，什么都不收
  - 读取改成容错：数字、字符串都认（`ReadLong` / `ReadInt`）
  - 写入保持数值：键名以 `Id` 结尾且能解析成整数时，一律写数字
  - 启动失败的原因现在会写进 `/danmaku` 的 `lastError`，不再只留一行日志

## 0.10.1（2026-11-01）

### 新增
- **换直播间即时生效**：设置页改完房间号约 1 秒自动切换，不用重启游戏
  - `DanmakuBridge.RetargetDelayed` 防抖 800ms（设置页是边打字边写盘），
    然后 `/shutdown` 旧 daemon → 等端口释放 → 按新链接起新 daemon → 游标清零
  - 房间号没变时直接 no-op，不会白重启
  - 启动时因为房间号为空而没跑起来的场景，也会在这里把轮询线程拉起来

## 0.10.0（2026-11-01）

### 新增
- **设置页生成弹幕页链接**：填好房间号后，直播弹幕段下面会显示完整链接
  （`https://chat.laplace.live/obs/<房间号>?…`，参数固定为 OBS 那一套），
  带「刷新」和「复制」两个按钮，复制即可粘到 Safari 打开
- **daemon 优先认这个链接**：启动时通过 `--url` 收到链接的「协议+域名+路径」，
  在 Safari 里先找 URL 含这段的标签页；找不到才退回原来的「URL 含 laplace」。
  实际读到哪一页会打印在 `/tmp/sts2whale-danmaku.log`（`page: …`），抓错一眼可见

### 说明
- 改房间号后要重启游戏才生效（daemon 在启动时拿链接）
- 版本号跳过 0.9.3：那一版是未采纳的改动，已整包回退

## 0.9.2（2026-11-01）

> 版本号说明：这一版的行为基线回到 **0.8.1**（保留 Safari 弹幕通道），
> 再按实测结论把 **B 站直连**整个删掉。0.9.0 / 0.9.1 的轮询方案作废。

### 变更
- **只保留 Safari 弹幕通道**，删掉 B 站直连：
  - daemon 里移除 `websocket-client` / `brotli` 依赖、`getConf` 握手、protobuf 解包、
    `gethistory` 轮询、登录态加载（`SESSDATA` 相关全部删除）
  - `live.json` 不再有 `source` / `authFile` 两个键
  - `roomId` 降级为「状态展示用」，填 0 也能跑（以前填 0 会让整个功能不启动）
  - 实测依据：匿名连接收不到任何 `DANMU_MSG`（只回心跳 `op3` 和 `LOG_IN_NOTICE`），
    连 400 万人在线的房间也一样；`gethistory` 匿名返回 0 条、带登录才有数据
- **Safari 读不到页面时不再静默**：`/health` 的 `lastError` 会明确写
  「Safari 里没找到 laplace 弹幕页（或未允许 Apple 事件中的 JavaScript）」

## 0.9.1（2026-11-01）

### 修复
- **安装脚本会破坏正在运行的游戏进程**：`install.sh` 原来用 `cp` 覆盖 `Sts2Whale.dll`，
  那是"原地改写同一个 inode"，游戏进程 mmap 着这个 inode，之后 JIT 编译新方法时会读到
  改过的字节，抛 `BadImageFormatException: Bad IL range`（实测：弹幕收到了但回复抛异常）
  - 改成 `rm -f` 再 `cp`，生成新 inode；老 inode 被 unlink 但仍归进程所有，映射不变
  - 代价：装完仍然**必须重启游戏**才用得上新代码（这一点没变，只是不再把正在跑的那个搞坏）

## 0.9.0（2026-11-01）

### 变更（重要）
- **弹幕采集从 WebSocket 改成 HTTP 轮询**：B 站现在**匿名连接收不到任何 `DANMU_MSG`**
  （WS 只回心跳 `op3`、登录提示 `LOG_IN_NOTICE`，连 400 万人在线的房间也一样），
  而同一房间的 `xlive/web-room/v1/dM/gethistory` 带上登录 Cookie 就能拿到真实弹幕
  - 改为 2.5 秒轮询一次，按 `id_str` 去重；启动时把已有 10 条当基线，不重播旧话
  - **需要登录态**：`bili_auth.json`（`SESSDATA` / `buvid3` / `dedeuserid`），
    或 `live.json` 里 `authFile` 指向已有文件；都没有时 `/danmaku` 会明确报
    「没有 B 站登录态」而不是静默不工作
  - 不再需要 `websocket-client` / `brotli`
- 顺带修掉 0.8.3 里「服务器推 JSON 整包」的解析问题（现在两条路径都已经不需要了，
  因为不再走 WS）

## 0.8.3（2026-11-01）

### 修复
- **弹幕一条都收不到**：daemon 只认 protobuf 子帧格式，而 B 站现在（至少这台服务器）
  直接推 **JSON 整包**（`{"cmd":"DANMU_MSG","info":[...]}`），于是被当成垃圾丢掉 ——
  表现为 `/health` 里 `packets` 在涨、`cmds` 一直是空的
  - 现在两种格式都认：包体以 `{` 开头就按 JSON 解，否则走原来的 protobuf 子帧路径
  - 两条路共用同一个 `emit(cmd, info)`，`DANMU_MSG` 之外的控制包只记数不入队
  - 加健壮性：`info[1]` 不是字符串、或 info 结构残缺，跳过而不是抛异常

## 0.8.2（2026-11-01）

### 变更
- **直播弹幕只保留「直连 B 站」一条通道**，删掉 Safari + Laplace 网页抓取
  - 去掉 `--source` 参数、`run_safari` / `safari_eval` / 页面 DOM 差分逻辑
  - `live.json` 不再需要 `source` 键（留着也无害，会被忽略）
  - `roomId` 现在是必填项（以前 safari 模式其实用不到它，却仍会卡住启动）
  - 依赖变明确：daemon 必须有 `websocket-client`，否则直接退出并在
    `/tmp/sts2whale-danmaku.log` 里写 `missing dependency`

## 0.8.1（2026-11-01）

### 修复
- **模型调用限流**：两次 DeepSeek 会话之间至少隔 **10 秒**（此前是 4 秒，且失败不计时）
  - 起因：战略助手每记一条决定就调一次模型，普通地图移动、事件「继续」、金币增加都在说，
    实测约 700 次/小时，绝大多数是噪音
  - 现在：等待期间来了更新的决定会**顶掉**旧的那条，只发最新一条（不积压、不补发过时的话）
  - 失败/超时也会记账，模型挂了不会疯狂重试
  - 可配：`llm.json` 的 `minGapMs`（默认 10000，范围 2~600 秒），设置页里也能拖
- 附带：直播弹幕回复走同一条通道，同样受这个闸限制

## 0.8.0（2026-11-01）

主题：把「自己造的那一层」换成 RitsuLib 的公开接口。功能不变，抗版本变动能力和可诊断性变强。

### 变更
- **皮套情绪钩子改为生命周期事件**：删掉 `Creature.LoseHpInternal` / `DamageBlockInternal` /
  `Block` setter 三个 Harmony 补丁，改成订阅 `CurrentHpChangedEvent`（玩家掉血）、
  `AttackEndedEvent`（`DamageResult.BlockedDamage` → 受击倾斜；敌人 `UnblockedDamage` → 黑脸）、
  `BlockGained/Cleared/BrokenEvent`（读实时 `Block` → 钢盆常驻）
  - 分工刻意不重叠：**掉血只由血量事件报，格挡只由攻击结算报**，同一击不会被算两次
  - 血量用自记的上一格血反推变化量，不依赖 `Delta` 的正负约定
- **F8 改成 RitsuLib 运行时热键**：可重绑、支持 Steam Input；绑定文本解析失败时自动退回每帧轮询（不会静默失效）
- **本局记忆改用 `ModDataStore`**（Global 作用域）：`run_memory.json` 首次启动自动迁移，`run_history.jsonl` 仍在 mod 目录
- **日志双写**：`agent_bridge.log` 之外同时进 RitsuLib 的 `Logger`（游戏日志通道）
- **最低 RitsuLib 版本提到 0.6.10**（`mod_manifest` 依赖声明），这是实际验证过的版本

### 新增
- **游戏内设置页**：设置 → Mod 设置 → 鲸鱼爬塔，覆盖模型（enabled/baseUrl/model/apiKey/maxTokens）、
  皮套（enabled/wsUrl/idleSway）、直播（enabled/roomId/maxPerMinute）
  - json 仍是唯一真相：老配置不用迁移，手改照旧有效，设置页只是读写同一批文件
  - 模型项改完立即生效（内部调 `WhaleBrain.Reload`）；皮套/直播的连接参数改完需重启
- **Debug Tools 页「鲸鱼桥接」**：游戏内直接看桥接最近 200 条消息（原来只能开浏览器看 `/console`）

### 已知边界
- 敌人因中毒/荆棘等**非攻击**掉血，不再触发「黑脸」（以前会）；攻击造成的照旧
- 皮套/弹幕的连接类配置仍需重启生效（连接在启动时建立）

## 0.7.0（2026-10-31）

### 新增
- **VTube Studio 皮套联动**：说话对口型；打人「黑脸」、挨打三档表情、格挡时「钢盆」常驻、
  求解器算牌时「圈圈眼」、被打到整模倾斜再弹回（1 伤 ≈ 1°，40° 封顶，连击累加）
- **语音**：常驻 edge-tts 服务（跟游戏启停），`zh-CN-XiaoyiNeural` 少女音；同句缓存命中 65ms
- **字幕条**：白字 + 黑描边、背景透明、底部居中、一句一行，可拖拽并记住位置
- **模型切到 deepseek-flash**（V4.1 Flash）：maxTokens 800 / 超时 30s；apiKey 只读 llm.json
- **求解器算牌表情可配多个**：`thinkingExpressions: ["圈圈眼","加载"]`，搜索时两个同时激活
- **分句播报**：人设改成「每句 8~16 字，多句用｜分隔」，模型自己决定分几句，
  mod 逐句播报（3 秒间隔），并在播报期间暂停世界线数字，避免气泡被覆盖
- **本局记忆**：给模型的提示词带「角色/层数/血量/金币/卡组/遗物 + 最近决定」，
  约 150 token；落盘 run_memory.json，全量流水 run_history.jsonl，换局自动清空
- **直播弹幕互动**：读 Safari 里打开的 Laplace 弹幕页，挑提问/关键词的弹幕回一句，
  滚动 60 秒最多 3 条；回复走字幕 + 语音
- **`POST /quit`**：存档并退出游戏
- 文档合并到 `readme/`

### 变更
- 「肥鱼代理」开关移除，代理模式固定为开
- 移除游戏内鲸鱼头像（`assets/whale.png`）
- 启动时强制摆正模型（绝对 `rotation: 0`）

### 修复
- **受击后回不正**：倾斜从「相对增量」改为**绝对角度**（`valuesAreRelativeToModel: false`），
  每帧目标精确、回正发 0 即真 0，不再有动画排队与累积误差
- **进游戏表情残留**：启动强制关闭 mod 可能开过的全部表情；退出时主动清理
- 字幕高度按字数估算（`GetCombinedMinimumSize` 在自动换行下会算错）

### 性能
- **场景树扫描提速**：`ScreenApi` 对「类型→属性」和「类型→控件种类」做缓存（原来每个节点都反射一次 `GetProperty`），
  并把「取界面类型」和「收集可点控件」两趟 DFS 合并成一趟、用 `GetChild(i)` 免掉每节点数组分配；
  `events` tick 平均耗时 4.18ms → ~0.9ms，帧率 37 → 47
- VTS token 内存缓存（去掉口型 30Hz 时的每秒 30 次读盘）
- 鉴权片段预拼、参数 JSON 复用缓冲、心跳零分配
- TTS 复用 HttpClient；气泡只在视口变化时重排
