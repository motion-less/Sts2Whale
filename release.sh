#!/usr/bin/env bash
# 打「创意工坊发布包」：只挑该发的文件、清掉个人数据、生成工坊目录 + VDF 草稿。
#
#   ./release.sh              构建 → 组装 → 生成本地工坊目录和 build.vdf
#
# 和 package.sh 的区别：
#   package.sh 是给「自己/朋友装进 mods/ 目录」用的，会带上模板配置；
#   release.sh 是给「上传 Steam 创意工坊」用的，**绝不带**API key、VTS token、
#   对局记录、你自己的直播间号、dll 备份。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
OUT="$ROOT/_archive/releases/workshop"
DOTNET_ROOT="${DOTNET_ROOT:-$ROOT/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.dotnet-home}"
STS2_DATA="${STS2_DATA:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64}"

VER="$(python3 -c "import json;print(json.load(open('$HERE/Sts2Whale.json'))['version'])")"
PKG="$OUT/sts2-whale-workshop-$VER"
VDF="$OUT/build-$VER.vdf"
APPID="2868840"
# 创意工坊条目 id：首次上传成功后写进 workshop-id.txt，之后每次更新都用它
WFID="$(cat "$HERE/workshop-id.txt" 2>/dev/null || echo 0)"
WFID="${WFID:-0}"

echo "== 构建 Sts2Whale $VER =="
# 关掉调试符号 + 强制全量：否则 PDB 的绝对路径会被写进 dll 的 RSDS 段，
# 包里就带着构建机的 /Users/<名字>/… 了（增量构建还会让这些开关不生效）
dotnet build "$HERE/Sts2Whale.csproj" -c Release -p:Sts2DataDir="$STS2_DATA" \
  -p:DebugType=None -p:DebugSymbols=false --no-incremental | tail -2

DLL="$HERE/.godot/mono/temp/bin/Release/Sts2Whale.dll"
[ -f "$DLL" ] || { echo "构建产物不存在：$DLL" >&2; exit 1; }

echo "== 组装工坊目录 =="
rm -rf "$PKG" && mkdir -p "$PKG/readme"

# 要发的：dll + 清单 + 人设 + 两个脚本 + 文档 + 许可证
cp "$DLL"                       "$PKG/Sts2Whale.dll"
cp "$HERE/Sts2Whale.json"       "$PKG/Sts2Whale.json"
cp "$HERE/persona.md"           "$PKG/persona.md"
cp "$HERE/README.md"            "$PKG/README.md"
cp "$HERE/CHANGELOG.md"         "$PKG/CHANGELOG.md"
cp "$HERE/LICENSE-CombatSolver-MIT.txt" "$PKG/LICENSE-CombatSolver-MIT.txt"
cp "$HERE/tools/tts_daemon.py"     "$PKG/tts_daemon.py"
cp "$HERE"/readme/*.md          "$PKG/readme/"

# 三个配置只发「干净模板」：去 key、去个人路径、去你自己的房间号
python3 - "$HERE" "$PKG" <<'PYEOF'
import json, sys, pathlib
here, pkg = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])

# llm.json：留结构，清 key
llm = json.loads((here / "llm.json").read_text(encoding="utf-8"))
llm["apiKey"] = ""
llm.setdefault("model", "deepseek-flash")
(pkg / "llm.json").write_text(json.dumps(llm, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

# vts.json：清掉本机 python / edge-tts 路径（留空 → 回落到 PATH 里的 edge-tts）
vts = json.loads((here / "vts.json").read_text(encoding="utf-8"))
speech = vts.get("speech") or {}
for k in ("daemonPython", "edgeCommand", "daemonScript"):
    speech[k] = ""
vts["speech"] = speech
(pkg / "vts.json").write_text(json.dumps(vts, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

# live.json：不预置任何房间号，也不默认开启
live = json.loads((here / "live.json").read_text(encoding="utf-8"))
live["enabled"] = False
live["roomId"] = 0
live.pop("python", None)   # 弹幕已改成进程内调 osascript，不再需要 python
live.pop("script", None)
live.pop("port", None)   # 弹幕不再起本地 HTTP daemon
(pkg / "live.json").write_text(json.dumps(live, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print("  已生成干净模板：llm.json / vts.json / live.json")
PYEOF

# 预览图（有就带上，没有只警告）
PREVIEW=""
for cand in "$HERE/workshop-preview.png" "$HERE/preview.png" "$HERE/readme/preview.png"; do
  if [ -f "$cand" ]; then PREVIEW="$cand"; break; fi
done
PREVIEW_OUT=""
if [ -n "$PREVIEW" ]; then
  PREVIEW_OUT="$OUT/preview-$VER.png"
  cp "$PREVIEW" "$PREVIEW_OUT"
  echo "  预览图：${PREVIEW} → ${PREVIEW_OUT}"
else
  echo "  ⚠️ 没找到预览图（workshop-preview.png / preview.png），上传时 Steam 会要求补一张"
fi

echo "== 发布前自检 =="
FAIL=0
check() { if [ -e "$PKG/$1" ]; then echo "  ✗ 不该出现：$1"; FAIL=1; fi; }
for f in vts_token.json run_memory.json run_history.jsonl Sts2Whale.dll.bak bili_auth.json live.json.bak; do check "$f"; done
# 文件里不能残留 key / 登录态 / 本机绝对路径
if grep -rIl "sk-[A-Za-z0-9]\{16,\}" "$PKG" >/dev/null 2>&1; then
  echo "  ✗ 有文件里还带着 API key（sk-…）"; FAIL=1
fi
if grep -rIl "SESSDATA" "$PKG" --include="*.json" --include="*.py" --include="*.sh" >/dev/null 2>&1; then
  echo "  ✗ 有文件里带着 B 站登录态"; FAIL=1
fi
if grep -rIl "$HOME" "$PKG" --exclude="*.dll" >/dev/null 2>&1; then
  echo "  ✗ 有文件里带着本机绝对路径（$HOME）"; FAIL=1
fi
if [ "$FAIL" -ne 0 ]; then
  echo "自检没通过，已中止" >&2
  exit 1
fi
echo "  ✓ 无 token / 无 key / 无本机路径 / 无对局记录"

python3 - "$VDF" "$PKG" "$HERE" "$APPID" "$VER" "${PREVIEW_OUT:-}" "$WFID" <<'PYEOF'
import sys, pathlib
vdf_path, pkg, here, appid, ver = sys.argv[1:6]
preview = pathlib.Path(sys.argv[6]) if len(sys.argv) > 6 and sys.argv[6] else None
wfid = (sys.argv[7] if len(sys.argv) > 7 else "0") or "0"
pkg, here = pathlib.Path(pkg), pathlib.Path(here)

desc_file = here / "workshop-description.md"
if desc_file.is_file():
    desc = desc_file.read_text(encoding="utf-8").strip()
else:
    desc = "战斗内自动出牌、局外自动选路选牌，鲸鱼娘用 LLM 解说并驱动 VTube Studio 皮套。"

title = "鲸鱼爬塔 · 战斗求解器 + 战略助手 + 鲸鱼娘"

def esc(s):
    """KeyValues 对 \" 和多行转义的处理不稳定（实测 steamcmd 会在结尾报
    "got } in key"）。所以直接抹平：双引号换成弯引号，换行压成空格，
    保证描述是一行、里面没有 " 也没有 \\n。BBCode 本身就是纯文本，不受影响。"""
    s = s.replace('"', "”").replace("\r", " ").replace("\n", " ")
    return " ".join(s.split())

lines = [
    '"workshopitem"',
    "{",
    '\t"appid"\t\t"%s"' % appid,
    '\t"publishedfileid"\t\t"%s"' % wfid,
    '\t"contentfolder"\t\t"%s"' % pkg,
]
if preview is not None and preview.is_file():
    lines.append('\t"previewfile"\t\t"%s"' % preview)
lines += [
    '\t"visibility"\t\t"2"',
    '\t"title"\t\t"%s"' % esc(title),
    '\t"description"\t\t"%s"' % esc(desc),
    '\t"changenote"\t\t"%s"' % esc(ver),
    "}",
]
pathlib.Path(vdf_path).write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"  VDF 已生成（描述来自 {desc_file.name if desc_file.is_file() else '默认文案'}，{len(desc)} 字符）")
PYEOF

cat > "$OUT/UPLOAD.md" <<TIPEOF
# 上传到创意工坊

一次上传用：

\`\`\`bash
steamcmd +login <你的 Steam 账号> +workshop_build_item "$VDF" +quit
\`\`\`

- 首次上传：\`publishedfileid\` 填 \`0\`，Steam 会返回一个新 id
- 更新：把 \`publishedfileid\` 改成那个 id，重跑同一条命令
- \`visibility\` 现在填的是 \`2\`（私有）—— 自测没问题再改成 \`0\`（公开）

自测顺序：私有上传 → 自己订阅 → 启动游戏 → 看 mod 列表里有没有它 → 确认依赖提示 → 转公开。
TIPEOF

echo "== 完成 =="
echo "  工坊目录: $PKG"
echo "  VDF     : $VDF"
echo "  说明    : $OUT/UPLOAD.md"
du -sh "$PKG" | awk '{print "  体积    : " $1}'
