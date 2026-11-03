#!/usr/bin/env bash
# 把「鲸鱼爬塔」装进《杀戮尖塔2》，并停用会打架的两份独立 mod。
# 用法：./install.sh            （装到默认 Steam 库）
#      GAME_MODS=/别的/路径/mods ./install.sh
#
# ⚠️ 游戏开着时也能装，但**必须用 put() 而不是 cp**：
#    cp 到已存在的文件是"原地覆盖同一个 inode"，游戏进程 mmap 着这个 inode，
#    之后 JIT 编译还没编译过的方法时就会读到改过的字节，抛
#    `BadImageFormatException: Bad IL range`（实测踩过）。
#    rm 再 cp 会生成新 inode，老 inode 被 unlink 但仍被进程持有，映射内容不变、安全。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
GAME_MODS="${GAME_MODS:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods}"
DEST="$GAME_MODS/Sts2Whale"

if [ ! -d "$GAME_MODS" ]; then
  echo "找不到 mods 目录：$GAME_MODS" >&2
  echo "确认游戏装在默认位置，或用 GAME_MODS=... 指定。" >&2
  exit 1
fi

mkdir -p "$DEST"                        # 首次安装时目标目录还不存在
put() { rm -f "$2" && cp "$1" "$2"; }   # 新 inode，别原地覆盖（见文件头说明）
put "$HERE/Sts2Whale.dll"  "$DEST/Sts2Whale.dll"
put "$HERE/Sts2Whale.json" "$DEST/Sts2Whale.json"
# 皮套联动配置：已有就保留用户的，别覆盖
if [ ! -f "$DEST/vts.json" ]; then cp "$HERE/vts.json" "$DEST/vts.json"; fi
if [ ! -f "$DEST/live.json" ]; then cp "$HERE/live.json" "$DEST/live.json"; fi
put "$HERE/tts_daemon.py" "$DEST/tts_daemon.py"
put "$HERE/persona.md" "$DEST/persona.md"
put "$HERE/LICENSE-CombatSolver-MIT.txt" "$DEST/LICENSE-CombatSolver-MIT.txt"
# 资源（失败曲之类）：有就带过去，别覆盖玩家自己放的
if [ -d "$HERE/assets" ]; then
  mkdir -p "$DEST/assets"
  for f in "$HERE"/assets/*; do
    [ -f "$f" ] || continue
    [ "$(basename "$f")" = "README.md" ] && continue
    put "$f" "$DEST/assets/$(basename "$f")"
  done
fi
# 文档（有就带上）
if [ -f "$HERE/README.md" ]; then put "$HERE/README.md" "$DEST/README.md"; fi
if [ -d "$HERE/readme" ]; then mkdir -p "$DEST/readme" && cp "$HERE"/readme/*.md "$DEST/readme/"; fi
echo "已安装 → $DEST"

# llm.json 是**玩家自己的配置**（里面有 key）：目标已有就绝不覆盖，没有才放一份模板。
if [ -f "$DEST/llm.json" ]; then
  echo "保留已有配置 → $DEST/llm.json"
else
  cp "$HERE/llm.json" "$DEST/llm.json"
  chmod 600 "$DEST/llm.json"
  echo "已放模板 → $DEST/llm.json（把 apiKey 填进去，或留空让它读环境变量/~/.dsh/.credentials.yaml）"
fi

# 停用「独立版鲸鱼桥接」和「工坊版战斗求解器」：否则两个桥接抢 47821 端口、两套补丁打架。
disable() {
  local f="$1"
  if [ -f "$f" ]; then
    mv "$f" "$f.disabled"
    echo "已停用 → $f.disabled"
  fi
}
disable "$GAME_MODS/Sts2AgentBridge/Sts2AgentBridge.json"
disable "$HOME/Library/Application Support/Steam/steamapps/workshop/content/2868840/3790899961/CombatSolver.json"

cat <<'TIP'

下一步：
  1) open "steam://rungameid/2868840"
  2) 直接开局即可（代理模式常开，界面上没有开关）
  3) 自检：curl -s 127.0.0.1:47821/health   应当返回 attached:true
  4) 鲸鱼娘的嘴：curl -s 127.0.0.1:47821/whale/brain
     hasKey:true 说明 key 读到了；curl "127.0.0.1:47821/whale/brain/test?text=测试" 看气泡
TIP
