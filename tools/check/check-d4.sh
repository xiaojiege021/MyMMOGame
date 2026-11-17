#!/usr/bin/env bash
# tools/check/check-d4.sh —— D4 最小 CI（开发架构 14.6 顺序 2）。
#
# 完成标准（14.6 的 D4 行）：
#   build + 静态断言 + 无库段 check 每次提交自动跑，失败即红。
#
# 本脚本核对三件事：
#   1. 入口：tools/check/ci.sh 存在、只跑无库段卡、覆盖 >= 10 张且每张都有脚本；
#   2. 触发：.github / .gitea 两个 workflow 都在 push / PR 上跑 ci.sh；
#   3. 真跑：ci.sh 退 0（MMO_D4_SKIP_CI=1 可跳过）。
#
#   bash tools/check/check-d4.sh
#   MMO_D4_SKIP_BUILD=1 bash tools/check/check-d4.sh
#   MMO_D4_SKIP_CI=1 bash tools/check/check-d4.sh   只做静态断言
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
TMP="$(mktemp -d)"
TITLE="D4（ci）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_D4_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_D4_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/MMOGame.sln -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "服务端 sln 编译通过"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言 =="
ROOT="$ROOT" python3 - > "$TMP/static.txt" 2>&1 <<'PY'
import glob, os, re

root = os.environ["ROOT"]

def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

ci = read("tools/check/ci.sh")
report("ci.sh 存在", bool(ci))
m = re.search(r'^CHECKS="([^"]+)"$', ci, re.M)
cards = m.group(1).split() if m else []
report("ci.sh 覆盖 >= 10 张卡（实际 %d）" % len(cards), len(cards) >= 10, str(cards[:8]))
missing = [c for c in cards if not os.path.exists(os.path.join(root, "tools/check/check-%s.sh" % c))]
report("ci.sh 列的卡都有核对脚本", bool(cards) and not missing, ",".join(missing[:6]))
# 无库段：CI 里不出现真库 / 网关 / Unity / 平板依赖。
forbidden = ["MMO_GAMEDB", "gamedb.env", "adb ", "Unity", "curl "]
code = "\n".join(l for l in ci.splitlines() if not l.strip().startswith("#"))
hits = [t for t in forbidden if t in code]
report("ci.sh 不依赖库 / 网关 / Unity / 平板", not hits, ",".join(hits))
# 跑的还是同一套核对脚本，不另写断言。
report("ci.sh 调 check-<卡>.sh", "check-$c.sh" in ci)

for wf in [".github/workflows/ci.yml", ".gitea/workflows/ci.yml"]:
    text = read(wf)
    report("%s 存在" % wf, bool(text))
    report("%s 在 push 上触发" % wf, re.search(r'^on:\s*$|push:', text, re.M) is not None)
    report("%s 装 dotnet 10" % wf, "dotnet-version" in text and "10.0" in text)
    report("%s 跑 ci.sh" % wf, "tools/check/ci.sh" in text)

report("check-all 已挂 d4", "d4)" in read("tools/check/check-all.sh") and " d4" in read("tools/check/check-all.sh"))
PY
while IFS= read -r line; do
  case "$line" in
    PASS\ *) ok "${line#PASS }" ;;
    FAIL\ *) bad "${line#FAIL }" ;;
    *) printf '%s\n' "$line" | detail ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 真跑 ci.sh =="
if [ "${MMO_D4_SKIP_CI:-0}" = "1" ]; then
  printf '  ..   跳过（MMO_D4_SKIP_CI=1）\n'
elif bash tools/check/ci.sh >"$TMP/ci.log" 2>&1; then
  ok "ci.sh 全绿（$(grep -c '^  ok' "$TMP/ci.log") 项）"
else
  bad "ci.sh 有红项"; grep -E '^  FAIL' "$TMP/ci.log" | head -6 | detail
fi

finish "$TITLE"
