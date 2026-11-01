#!/usr/bin/env bash
# 构建 + 打包 Sts2Whale（唯一维护入口）。
#
#   ./package.sh              构建并打包到 _archive/releases/sts2-whale-mod-v<版本>/
#   ./package.sh --install    构建 → 打包 → 直接装进游戏
#
# 产物（都在仓库外的 releases 目录，源码目录不留副本）：
#   <root>/_archive/releases/sts2-whale-mod-v<ver>/        可直接运行的 mod 目录
#   <root>/_archive/releases/sts2-whale-mod-v<ver>.zip     分发包
#   <root>/_archive/releases/sts2-whale-mod-v<ver>.sha256  校验和
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
OUT="$ROOT/_archive/releases"
DOTNET_ROOT="${DOTNET_ROOT:-$ROOT/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.dotnet-home}"
STS2_DATA="${STS2_DATA:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64}"

if [ ! -x "$DOTNET_ROOT/dotnet" ]; then
  echo "找不到 dotnet：$DOTNET_ROOT/dotnet（用 DOTNET_ROOT=… 指定）" >&2
  exit 1
fi

VER="$(python3 -c "import json;print(json.load(open('$HERE/Sts2Whale.json'))['version'])")"
PKG="$OUT/sts2-whale-mod-v$VER"
ZIP="$PKG.zip"

echo "== 构建 Sts2Whale $VER =="
dotnet build "$HERE/Sts2Whale.csproj" -c Release -p:Sts2DataDir="$STS2_DATA" | tail -3

DLL="$HERE/.godot/mono/temp/bin/Release/Sts2Whale.dll"
[ -f "$DLL" ] || { echo "构建产物不存在：$DLL" >&2; exit 1; }

echo "== 组装 $PKG =="
mkdir -p "$OUT"
rm -rf "$PKG" "$ZIP" "$PKG.sha256"

mkdir -p "$PKG/readme"
cp "$DLL" "$PKG/Sts2Whale.dll"
cp "$HERE/Sts2Whale.json" "$HERE/persona.md" "$HERE/llm.json" "$HERE/vts.json" "$HERE/live.json" \
   "$HERE/install.sh" "$HERE/LICENSE-CombatSolver-MIT.txt" "$HERE/README.md" "$HERE/CHANGELOG.md" "$PKG/"
cp "$HERE"/readme/*.md "$PKG/readme/"
cp "$HERE/tools/tts_daemon.py" "$PKG/"
chmod 600 "$PKG/llm.json"

# 发布包里的 llm.json 清空 apiKey：别把私人 key 打进要分发的 zip
python3 - "$PKG/llm.json" <<'PYEOF'
import json, sys
p = sys.argv[1]
d = json.load(open(p, encoding='utf-8'))
if d.get("apiKey"):
    d["apiKey"] = ""
    json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    open(p, "a").write("\n")
    print("  （打包时已清空 llm.json 的 apiKey，避免 key 跟着 zip 泄漏）")
PYEOF

(cd "$OUT" && zip -qr "sts2-whale-mod-v$VER.zip" "sts2-whale-mod-v$VER" -x "*.DS_Store")
(cd "$OUT" && shasum -a 256 "sts2-whale-mod-v$VER.zip" > "sts2-whale-mod-v$VER.sha256")

echo "== 完成 =="
printf '  %s\n  %s\n' "$PKG" "$ZIP"
shasum -a 256 "$PKG/Sts2Whale.dll" | awk '{print "  dll sha256: " $1}'
cat "$PKG.sha256" | awk '{print "  zip sha256: " $1}'

if [ "${1:-}" = "--install" ]; then
  GAME_MODS="${GAME_MODS:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods}"
  echo "== 安装到游戏 =="
  GAME_MODS="$GAME_MODS" "$PKG/install.sh"
fi
