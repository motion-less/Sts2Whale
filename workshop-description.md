[h1]鲸鱼爬塔 · 战斗求解器 + 战略助手 + 鲸鱼娘[/h1]

一个「替你把《杀戮尖塔 2》打完」的 AI 助手：战斗里自动搜世界线出牌，局外自动选路选牌，
鲸鱼娘用 LLM 把你做的每个决定解说一遍，并联动 VTube Studio 皮套做表情和口型。
支持 Windows 与 macOS。

[b][color=#ff6b6b]⚠️ 先装依赖[/color][/b]
本 mod 依赖 [b]STS2-RitsuLib[/b]（创意工坊搜索订阅即可）。没装它，游戏不会加载本 mod。

[h2]环境要求[/h2]
[list]
[*]《杀戮尖塔 2》本体，游戏 API ≥ 0.111.0（Windows / macOS）
[*][b]STS2-RitsuLib[/b] —— 运行库，必须先订阅
[*][b]Python 3.8+[/b] —— 只有「语音」需要（实测 3.12 / edge-tts 7.3.1）。
    首次启动会在后台自动建独立环境并安装 edge-tts，装完直接出声；
    不装就只有字幕，其他功能全部正常
[*][b]Chrome 或 Edge[/b] —— 只有「直播弹幕」需要（Windows 自带 Edge 就够）。
    设置页填好房间号后点「打开弹幕页」，mod 用独立 profile 打开那页并开始读取，
    不需要装插件，也不需要改浏览器设置
[/list]

[h2]它做什么[/h2]
[list]
[*][b]战斗求解器[/b]：实时枚举世界线，给出期望战损最低的出牌顺序并自动执行
[*][b]全局战略助手[/b]：地图选路、选牌、商店、营火、事件，全部自动决策
[*][b]鲸鱼娘[/b]：把上面的决定翻译成一句话播报（字幕条 + 语音），口吻由角色卡控制
[*][b]皮套联动[/b]（可选）：连 VTube Studio，说话对口型、挨打分档表情、格挡举锅盖、被打歪
[*][b]直播弹幕[/b]（可选）：读直播间弹幕，挑提问和带关键词的回一句，最多 3 条/分钟
[/list]

[h2]怎么用[/h2]
[list]
[*]订阅后启动游戏，开局即接管，界面上没有开关
[*][b]F8[/b] 显示/隐藏战略助手面板（可在设置里改键）
[*]设置 → Mod 设置 → 鲸鱼爬塔：模型、角色卡、皮套、直播参数都在这里
    （角色卡是决定「怎么说话」的唯一来源，直接编辑、保存即生效）
[*]想让它用 LLM 解说，需要自己的 DeepSeek API key（填在设置页，只存在你本机；没有就只用本地垫话）
[*]想开弹幕：填房间号 → 点「打开弹幕页」→ 等页面加载几秒，之后自动开始读
[/list]

[h2]首次启动会做什么[/h2]
[list]
[*]在用户目录建配置目录（macOS 在 ~/Library/Application Support/SlayTheSpire2/Sts2Whale，
    Windows 在 %APPDATA%\\SlayTheSpire2\\Sts2Whale）；工坊更新不会覆盖你自己改的配置
[*]找 Python、装 edge-tts（只影响语音），装好立刻生效，不用重启
[*]不会自己开浏览器 —— 弹幕要你点按钮
[/list]

[h2]已知限制[/h2]
[list]
[*]它是 AI，会犯错：偶尔算错伤害、选错牌，遇到不确定的界面会保守处理
[*][b]建议另开一个存档[/b]，或者只在你不在意的局里用；它会替你做所有决定
[*]语音：macOS 走系统音频转换，口型按真实音频包络；Windows 没有该系统命令，
    口型按字数估 —— 有声音、嘴也动，但不完全对齐
[*]弹幕要电脑上有 Chrome 或 Edge，而且房间得在开播
[*]语音走微软 Edge 的在线接口（非官方），接口变动可能导致失效；届时回落到只动嘴
[*]皮套模型素材不随本包分发（授权原因），只提供联动客户端
[/list]

[h2]许可与致谢[/h2]
[list]
[*]战斗求解器来自 CombatSolver（MIT，© Torch1230）
[*]全局战略助手来自 SpireStrategist（MIT，© 穷仔）
[*]本项目自身 MIT
[/list]

[h2]English[/h2]
An AI autopilot for Slay the Spire 2 (Windows and macOS): a combat solver, a strategy autopilot, and an
LLM-narrated mascot with VTube Studio integration. [b]Requires STS2-RitsuLib.[/b]
Voice needs Python 3.8+ with edge-tts (installed automatically on first run); live-chat reading needs
Chrome or Edge and is started from the mod settings page. Lip-sync is sampled from the real audio on
macOS and estimated from text length on Windows. The AI does make mistakes — use it on a save you don't
mind losing.
