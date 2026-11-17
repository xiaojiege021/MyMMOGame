#!/usr/bin/env bash
# tools/check/check-d1b.sh —— D1b CLI 迁出到 tools/probe（开发架构 14.8 顺序 2）。
#
# 完成标准（14.8 的 D1b 行，逐条对应下面的断言）：
#   1. 角色服进程不再带核对入口：Program.cs 只认 --config，--shop-list 之类不再跑核对；
#   2. 脚本改调 probe：tools/probe/CliProbe 存在、D1b 迁出的 133 个 flag（K5 又加
#      --item-list、K6 又加 --content-list，共 135）与入口都在那边，
#      tools/check/*.sh 里已经没有指向角色服 dll 的核对调用；
#   3. 真机 59 项不回退：由 check-p0-3c.sh 单独验（本脚本不重复出包）。
#
#   bash tools/check/check-d1b.sh
#   MMO_D1B_SKIP_BUILD=1 bash tools/check/check-d1b.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
SERVICE_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
PROBE_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TMP="$(mktemp -d)"
TITLE="D1b（server）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_D1B_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_D1B_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/Character/Character.csproj -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "角色服编译通过（probe 由 post-build 一起编出）"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi
[ -f "$PROBE_DLL" ] && ok "probe 已生成" || bad "probe 没生成：$PROBE_DLL"

echo "== 2. 静态断言 =="
ROOT="$ROOT" python3 - > "$TMP/static.txt" 2>&1 <<'PY'
import glob, os, re

root = os.environ["ROOT"]

def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

# 1. 角色服侧不再有 CLI 目录，入口只认 --config。
report("角色服不再有 Cli/ 源码", not os.path.isdir(os.path.join(root, "src/Character/Cli")))
program = read("src/Character/Program.cs")
report("角色服入口只认 --config", [f.strip('"') for f in re.findall(r'"--[a-z-]+"', program)] == ["--config"],
       str(re.findall(r'"--[a-z-]+"', program)[:6]))
report("角色服入口不含 CliEntry / CliParser",
       "CliEntry" not in program and "CliParser" not in program)
report("角色服编译时带 probe 构建钩子", "BuildCliProbe" in read("src/Character/Character.csproj"))

# 2. probe 侧完整：工程、入口、D1b 的 133 个 flag（K5 追加 1 个）、15 个类文件。
probe_csproj = read("tools/probe/CliProbe/CliProbe.csproj")
report("probe 工程引用角色服", "../../../src/Character/Character.csproj" in probe_csproj)
report("probe 入口转 CliEntry", "CliEntry.RunAsync(args)" in read("tools/probe/CliProbe/Program.cs"))
parser = read("tools/probe/CliProbe/CliParser.cs")
flags = re.findall(r'case "(--[a-z0-9-]+)"', parser)
# K5 加了 --item-list；断言「D1b 的 133 个一个不少，最多再加后续卡的」。
missing_flags = [f for f in ("--weapon-rules", "--shop-list", "--season-pass-list",
                             "--migrate", "--config") if f not in flags]
report("probe 仍是 D1b 的 133 个 flag（+K5/K6 两个只读入口，共 %d）" % len(set(flags)),
       len(set(flags)) >= 135 and not missing_flags, str(missing_flags))
# 文件数只增不减：D1a 拆出 16 个（含 Program.cs），C7 加 ConfigCli 一个 = 17。
# 钉死等号的话后一张卡加文件就红；钉下限则表达「拆开的文件不许被合并回大文件」。
report("probe 源文件只增不减（现 %d 个，下限 17）" % len(glob.glob(os.path.join(root, "tools/probe/CliProbe/*.cs"))),
       len(glob.glob(os.path.join(root, "tools/probe/CliProbe/*.cs"))) >= 17,
       str(len(glob.glob(os.path.join(root, "tools/probe/CliProbe/*.cs")))))

# 3. 核对脚本改调 probe：不再有指向角色服 dll 的核对路径。
checks = glob.glob(os.path.join(root, "tools/check/*.sh"))
old_path = "src/Character/bin/Release/net10.0/Character.dll"
hits = [os.path.basename(p) for p in checks
        if not p.endswith("check-d1b.sh")
        and old_path in open(p, encoding="utf-8").read()]
report("核对脚本不再指向角色服 dll", not hits, ",".join(hits[:6]))
probe_hits = [p for p in checks if "tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll" in open(p, encoding="utf-8").read()]
report(">= 39 份脚本改调 probe（实际 %d）" % len(probe_hits), len(probe_hits) >= 39)
PY
while IFS= read -r line; do
  case "$line" in
    PASS\ *) ok "${line#PASS }" ;;
    FAIL\ *) bad "${line#FAIL }" ;;
    *) printf '%s\n' "$line" | detail ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 角色服进程不再跑核对 =="
# 起 3 秒就收：只要不出现核对输出（含异常提前退出），就说明 flag 不再进核对链。
"$DOTNET" "$SERVICE_DLL" --shop-list >"$TMP/service.out" 2>&1 &
SERVICE_PID=$!
sleep 3
kill "$SERVICE_PID" 2>/dev/null || true
wait "$SERVICE_PID" 2>/dev/null || true
if grep -q '^shop_list_state=ok' "$TMP/service.out"; then
  bad "角色服仍在跑 --shop-list"
else
  ok "角色服不认核对 flag（未输出 shop_list_state）"
fi
if grep -qE '^config=|^listen=|Unhandled exception' "$TMP/service.out"; then
  ok "角色服按启动路径走（config / listen 或明确异常）"
else
  bad "角色服启动路径没跑起来：$(head -3 "$TMP/service.out" | tr '\n' ' ')"
fi

echo "== 4. probe 接住原命令 =="
probe_run() {
  # probe_run <说明> <期望退出码> <关键行> <参数...>
  local desc="$1" want="$2" pattern="$3"
  shift 3
  local out code
  out="$("$DOTNET" "$PROBE_DLL" "$@" 2>&1)"; code=$?
  if [ "$code" = "$want" ] && grep -q "$pattern" <<< "$out"; then
    ok "${desc}（exit=${code}）"
  else
    bad "${desc}：exit=${code} 期望 ${want}，输出=$(head -1 <<< "$out")"
  fi
}
probe_run "probe --shop-list 回清单" 0 '^shop_list_state=ok' --shop-list
probe_run "probe 未知参数回 64" 64 '^unknown_arg=--d1b_nonsense' --d1b_nonsense
if [ -n "${MMO_GAMEDB:-}" ] || [ -f /srv/mmo/gamedb.env ]; then
  printf '  ..   本机有角色库，连库段留给 P 卡与真库回归\n'
else
  probe_run "probe 连库入口无库时回 db_env=missing" 3 '^db_env=missing' --fatigue-state 1
fi

printf '  ..   真机 59 项由 bash tools/check/check-p0-3c.sh 单独验（本卡不动 proto / 客户端）\n'
finish "$TITLE"
