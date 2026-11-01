# 皮套联动（VTube Studio）

鲸鱼娘的形象不在游戏里，而在 VTube Studio 里。这个模块把**游戏里发生的事**翻译成 VTS 指令：
说话对口型、打人黑脸、挨打吃痛、有格挡举锅盖、求解器算牌时圈圈眼、被打到会歪一下。

代码在 `src/whale/`，跑在游戏进程内，通过 WebSocket 直连 VTS（默认 `ws://127.0.0.1:8001`）。

## 1. 数据从哪来

| 来源 | 覆盖 | 说明 |
|---|---|---|
| `EventFeed.Publish` 回调 | 商店 / 奖励 / 篝火 / 选牌 / 精英 / 计划 | 进程内回调，无 HTTP 延迟 |
| **Harmony：`Creature.LoseHpInternal`** | 玩家掉血、敌人掉血 | 唯一真正扣血的出口，格挡伤害不走这里 |
| **Harmony：`Creature.DamageBlockInternal`** | 这一击被格挡掉多少 | 和掉血一起凑成「这一击的攻击力」 |
| **Harmony：`Creature.Block` setter** | 格挡状态（>0 / =0） | 卡牌、遗物、回合清空都经过这里 |
| `SolverBridge.Searching` | 求解器是否在算牌 | 静态布尔，每帧读一次 |
| `WhaleUi.Say` | 鲸鱼娘说的台词 | 只有 `source == "brain"`（DeepSeek 生成）才朗读 |

## 2. 表情映射

| 事件 | 触发条件 | 表情 | 时长 | 附加参数 |
|---|---|---|---|---|
| `attack` | 敌人掉血（打出去了） | 黑脸 | 0.9s | FaceAngry 0.7 |
| `hurt_small` | 掉血 ≤5 | ＞＜ | 0.8s | — |
| `hurt` | 掉血 6~14 | 流冷汗 | 1.2s | MouthSmile -0.4 |
| `hurt_big` | 掉血 ≥15 | 流泪 | 1.8s | MouthSmile -0.6 |
| `combat_start` | 进战斗第一回合 | 前倾 | 1.2s | — |
| `elite` | 精英 / Boss | 黑框眼镜 | 3s | — |
| `reward` | 奖励界面 | 爱心眼 | 2.6s | MouthSmile 0.7 |
| `victory` | 敌人清空 | 爱心眼 | 3s | MouthSmile 0.8 |
| `death` | 失败 | 流泪 | 4s | — |
| `shop` | 商店 | 屑屑眼 | 2.2s | — |
| `rest` | 篝火 | 脸红 | 2.2s | — |
| `level` | 升级 | 脸红 | 2s | MouthSmile 0.5 |
| `card_select` | 选牌 | 圈圈眼 | 1.6s | — |

### 状态型表情（常驻，不看时长）

| 状态 | 表情 | 优先级 |
|---|---|---|
| 格挡 > 0 | **钢盆**（锅盖） | 最高，压住其他表情 |
| 求解器正在算牌 | **圈圈眼 + 加载**（可配多个，同时激活） | 次高，格挡时让位 |
| 都没有 | 走上面的普通情绪 | — |

## 3. 受击倾斜（不倒翁）

这个皮套的头部绑定几乎不响应 `FaceAngle`（注入 90° 只偏几像素），所以改用 VTS 的
**整模相对旋转**（`MoveModelRequest`）来表现「被打歪」。

- 幅度按**这一击的攻击力**算，不是实际掉血：`格挡吸收 + 掉血` 在 40ms 内合并成一次
- 1 点攻击 ≈ 1°，**40 点封顶 40°**；完全被格挡也会被打歪
- 阻尼弹簧（ω=6、ζ=0.28）：冲出去 → 荡回来，允许 −6° 的向左过冲
- 没回正又挨打 → 冲量累加，最多 40°
- **绝对角度**：用 `valuesAreRelativeToModel: false` 直接发目标角度（不是增量）。
  每帧目标精确、回正发 0 就真的 0，不存在动画排队或累积误差；只有角度变化 >0.1° 才发
- 调试：`curl "127.0.0.1:47821/avatar/hit?damage=40"`

## 4. idle 摆头

Perlin 噪声打底 + 每步 50% 反向 + 弹簧惯性 + 微抖 + 偶尔停顿，10Hz 输出，再做三层约束：

| 约束 | 值 | 作用 |
|---|---|---|
| 输出平滑 `swaySmoothing` | 0.15 | 朝噪声目标追 15%/帧，不抽 |
| 软限幅 `swayMaxAngle` | 9° | 左右/歪头，tanh 压缩大角度 |
| 抬头上限 `swayMaxUp` | 2° | 真人直播几乎不抬头 |
| 低头上限 `swayMaxDown` | 9° | 低头可以多一点 |

## 4.5 气泡（原本的鲸鱼头像已移除）

游戏里只有一块**可拖拽的气泡**：

- **样式**：电影字幕——**背景完全透明**、白字 + **黑色描边（outline 8px）**、居中对齐
- **默认位置**：底部居中，距底边 56px
- **默认尺寸**：**一句一行**（不换行），字号 34、宽度按字数算；长句子字号会自动缩到能放下（最小 16）
- **拖动**：按住左键拖，松手记住；位置存 `mod_configs/Sts2AgentBridge/bubble.json`（归一化坐标，换分辨率也还原）
- 没有头像图片了，`assets/whale.png` 已删除

## 5. 语音

台词来自 mod 自己的 LLM（`persona.md` + `llm.json`，DeepSeek），皮套层**不调用任何模型**。
`onlyBrain: true` 时只有 DeepSeek 返回的句子会被念，本地垫话/进度提示只显示气泡。

链路：

```
台词语句 → 常驻 TTS 服务（edge-tts，127.0.0.1:48765）
        → 拿回 24kHz wav → 算 40ms RMS 包络 → afplay 播放
        → 口型按播放进度注入 MouthOpen（平滑 0.22、上限 0.75、增益 0.9）
```

- **常驻服务跟游戏走**：mod 初始化拉起，`/quit` 时收掉
- **三道保险**：`--parent <游戏PID>`（游戏被强杀自己退）、空闲 30 分钟自退、启动先探活
- **缓存**：同一句话第二次直接复用 wav（65ms）
- **兜底**：daemon 不可用 → 命令行 `edge-tts`；再不行 → 只动嘴不出声（不再用 `say`）

实测：新句子约 1.4~1.5 秒（瓶颈是 edge 每次请求新建 WebSocket），缓存命中 65ms。

## 6. 配置 `vts.json`

```jsonc
{
  "enabled": true,
  "wsUrl": "ws://127.0.0.1:8001",
  "pluginName": "Sts2Whale_Avatar",
  "mouthParam": "MouthOpen",

  // idle 摆头
  "idleSway": true,
  "swayAmplitude": 1.0,
  "swaySmoothing": 0.15,
  "swayMaxAngle": 9,
  "swayMaxUp": 2.0,
  "swayMaxDown": 9.0,

  // 常驻/默认表情
  "blockExpression": "钢盆",
  "thinkingExpressions": ["圈圈眼", "加载"],   // 求解器算牌时同时挂的表情
  "startupExpressions": { "屑屑眼": false, "钢盆": false },
  "idleParams": { "MouthSmile": 0.15 },

  // 受击倾斜
  "hitTilt": true,
  "hitDegreesPerDamage": 1.0,
  "hitMaxDegrees": 40,

  // 语音
  "speech": {
    "mode": "tts",                  // tts | text | off
    "engine": "daemon",             // daemon | edge | say
    "edgeVoice": "zh-CN-XiaoyiNeural", // 温柔：zh-CN-XiaoxiaoNeural
    "edgeRate": "+8%",
    "edgePitch": "+0Hz",
    "daemonPort": 48765,
    "daemonPython": "/path/to/venv/bin/python",
    "daemonScript": "",             // 留空 = mod 目录下的 tts_daemon.py
    "daemonIdleMinutes": 30,
    "onlyBrain": true,
    "gain": 0.9,
    "maxOpen": 0.75,
    "smoothing": 0.22,
    "maxChars": 80
  },

  "emotions": {
    "attack": { "expression": "黑脸", "holdMs": 900, "params": { "FaceAngry": 0.7 } }
    // … 见 §2 的完整表
  }
}
```

改完重启游戏生效（读一次配置）。`startupExpressions` 会把模型默认开着的
「屑屑眼」「钢盆」关掉——否则眼睛半眯、像发愣。

## 7. 调试

| 命令 | 作用 |
|---|---|
| `curl -s 127.0.0.1:47821/avatar/state` | 连接/鉴权/热键/事件数/最近决策/最近 20 条指令 |
| `curl "…/avatar/test?emotion=attack"` | 手动触发一个情绪 |
| `curl "…/avatar/hit?damage=40"` | 手动模拟 40 点受击（看倾斜） |
| `curl "…/avatar/state" \| jq .recentCommands` | 刚才到底给 VTS 发了什么 |

日志关键字：`[avatar]`（决策/事件/倾斜）、`[vts] →`（发出的指令）、`[tts]`（服务启停）。

## 8. 踩过的坑

1. **exp3 的字段**：`ExpressionActivationRequest` 要 `expressionFile`（`xxx.exp3.json`），
   传显示名会报 `errorID 650`。
2. **热键是 toggle**：连点两次会关掉，所以优先用 exp3 显式开关，热键只在没有 exp3 时用。
3. **参数注入会盖住模型**：`mode:"set"` 会覆盖 idle；说完话要用 `weight:0` **释放** `MouthOpen`，
   否则嘴被钉住（之前那个「小三角嘴」就是它）。
4. **参数有归属权**：别的插件占着同一参数会返回 `errorID 454`，同时只跑一个客户端。
5. **VTS 会记住模型旋转**：启动时发一次绝对 `MoveModelRequest(rotation:0)` 强制摆正。
6. **这个皮套头绑不动**：`FaceAngle` / `FacePosition` 几乎没效果，受击倾斜只能整模旋转。
7. **token 弹窗**：复用 `vts_token.json`；只有鉴权失败才重新申请，且 30 秒限流，避免刷屏。
8. **补丁不能抛异常**：Harmony postfix 里一律 `try/catch`。
9. **表情会在模型加载后被 VTS 再套一遍**：连上时发的复位可能早于模型加载完成，
   所以要监听 `ModelLoadedEvent` 再复位一次；复位要关「我们认识的名字 ∪ VTS 报告 active 的名字」，
   并且每条隔 30ms 发（一次十几条会被吞）。

## 9. 授权

大肥鱼（DeepSeek-chan）的使用须知：个人直播/视频/收益可以，**禁止二次分发模型文件**、
禁止二次销售。署名：原画/拆分 **神宫凉子**，Live2D 建模 **霓虹猫_NeonNeko**。

所以分发包里不含 `.moc3` / 贴图 / PSD；要给别人用，让他自己去下模型，或者只放你自己画的替代图。
