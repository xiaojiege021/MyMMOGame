#!/usr/bin/env bash
# tools/check/check-n3.sh —— N3 名册接到 F01（开发架构第 14 节第 82 行）。
#
# 完成标准：屏上逐条名单只来自 N2 的回包。没有回包仍写尚未请求。
# 不读 config、不动 proto、不改任免和人数上限。
#
#   bash tools/check/check-n3.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
TITLE="N3（client）"
TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

if [ ! -d "$ROOT/client/MMOGame" ]; then
  echo "  skip 这台机器没有客户端工程"
  finish "$TITLE"
fi

echo "== 1. 静态断言 =="
PYBIN=""
for c in python3 python; do
  if command -v "$c" >/dev/null 2>&1; then PYBIN="$c"; break; fi
done
if [ -z "$PYBIN" ]; then
  bad "找不到 python3 / python"
  finish "$TITLE"
fi
if ROOT="$ROOT" "$PYBIN" - >"$TMP/static.out" 2>&1 <<'PY'
import os
import sys

root = os.environ["ROOT"]
sys.path.insert(0, root + "/tools/check")
from lib_read import read_text, ui_text_assert
ui = root + "/client/MMOGame/Assets/HotUpdate/Ui"
net = root + "/client/MMOGame/Assets/Net"
sect = open(ui + "/UiSectScreen.cs", encoding="utf-8").read()
flow = read_text(ui + "/UiSocialFlow.cs")
guild = open(net + "/SectGuild.cs", encoding="utf-8").read()
codex = open(ui + "/UiSocialCodex.cs", encoding="utf-8").read()
proto = open(root + "/proto/mmo/social.proto", encoding="utf-8").read()
allcards = open(root + "/tools/check/check-all.sh", encoding="utf-8").read()

parse = guild.split("ParseRoster", 1)[1].split("static string U(", 1)[0]
assert "member_cap" not in parse and "seat_cap" not in parse, "名册拆装读了上限"
assert "character_id" in parse and "position" in parse, "名册拆装没读角色号或职位"
assert "RosterLine" in guild, "没有逐行原文"

assert '"/sect/roster?sect_id="' in flow and "SectGuild.ParseRoster" in flow, "流程没走只读名册"
assert "LoadRoster" in flow and "HasRoster" in flow

# D9 之后屏上中文在 config/ui_text/UiSectScreen.json，断言改成查表 + 查引用。
ui_text_assert(root, "UiSectScreen", "尚未请求", sect, "宗门屏")
ui_text_assert(root, "UiSectScreen", "读取名册", sect, "宗门屏")
assert "SectGuild.RosterLine" in sect, "屏没逐条写回包"
assert "n3_roster_screen=ready" in sect
assert "flow.Appoint" in sect, "任免按钮被拿掉了"
assert "config/" not in sect and "sect.json" not in sect, "宗门屏读了配置"
assert "RosterGap" not in sect and "还没有读接口" not in sect, "屏上仍写没有读接口"
assert "RosterGap" not in codex and "还没有读接口" not in codex

assert "n3" not in proto.lower(), "这张卡不该改 proto"
assert "DescribeSectRoster" in proto, "N2 的名册消息不在"
assert "n3)" in allcards, "check-all 没有 n3"

print("n3_static=ok")
PY
then ok "名单只接回包、没回包写尚未请求、总控已挂 N3"
else bad "N3 静态断言失败"; cat "$TMP/static.out" | detail
fi

echo "== 2. Unity 编辑器自检 =="
if [ ! -x "$UNITY" ] && [ ! -f "$UNITY" ]; then
  bad "找不到 Unity：$UNITY"
  finish "$TITLE"
fi
if "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod N3Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1; then
  ok "Unity N3Check.Run 退出码 0"
else
  bad "Unity N3Check.Run 失败"
  tail -40 "$TMP/unity.log" | detail
fi

for key in files parse flow screen no_config no_proto self; do
  if grep -q "n3_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "编辑器自检 n3_${key}=ok"
  else
    bad "编辑器自检 n3_${key} 没过"
    grep "n3_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

for key in pending rows rows_second empty reject no_cap page skin_layout skin_copy; do
  if grep -q "n3_self_${key}=ok" "$TMP/unity.log" 2>/dev/null; then
    ok "名册自检 n3_self_${key}=ok"
  else
    bad "名册自检 n3_self_${key} 没过"
    grep "n3_self_${key}" "$TMP/unity.log" 2>/dev/null | tail -2 | detail
  fi
done

if grep -q "n3_roster_screen=ready" "$TMP/unity.log" 2>/dev/null; then
  ok "屏就绪 n3_roster_screen=ready"
else
  bad "屏没念就绪行"
fi

finish "$TITLE"
