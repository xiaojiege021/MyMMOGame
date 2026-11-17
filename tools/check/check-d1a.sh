#!/usr/bin/env bash
# tools/check/check-d1a.sh —— D1a `Character/Program.cs` 的 CLI 分文件（开发架构 14.8 顺序 1）。
#
# 完成标准（14.8 的 D1a 行，逐条对应下面的断言）：
#   1. 入口只留启动与迁移：Program.cs 不再有 flag 分支 / 连库 / 表加载；
#   2. 133 个 flag 名不变：Cli/CliParser.cs 的 `case "--xxx"` 与 D1a 前的名单逐字一致；
#   3. `Character/Cli/*` 一卡一类：每个文件恰好一个类型，单文件 ≤ 600 行；
#   4. check 全绿：编译 + 只读入口 / 解析 / 连库入口的运行断言。
#
#   bash tools/check/check-d1a.sh
#   MMO_D1A_SKIP_BUILD=1 bash tools/check/check-d1a.sh   跳过编译
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
TMP="$(mktemp -d)"
TITLE="D1a（server）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_D1A_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_D1A_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/Character/Character.csproj -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "角色服编译通过"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言 =="
ROOT="$ROOT" python3 - > "$TMP/static.txt" 2>&1 <<'PY'
import glob, os, re

root = os.environ["ROOT"]
cli_dir = os.path.join(root, "src/Character/Cli")

def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

program = read("src/Character/Program.cs")
program_lines = len(program.rstrip("\n").split("\n")) if program else 0

# 1. 入口只留启动与迁移。
report("入口 < 300 行（实际 %d）" % program_lines, 0 < program_lines < 300)
report("入口无 flag 分支", 'case "' not in program and '"--' not in program)
report("入口无控制台输出", "Console." not in program)
report("入口只转 CliEntry", "CliEntry.RunAsync(args)" in program)
report("入口不碰库 / 配置表 / 文件",
       not any(t in program for t in ["Npgsql", "DungeonTable", "CharacterRepository", "File.ReadAllText"]))

# 2. Cli/* 一卡一类。
files = sorted(glob.glob(os.path.join(cli_dir, "*.cs")))
report("Cli 类文件 >= 8（实际 %d）" % len(files), len(files) >= 8)
multi = []
too_long = []
for path in files:
    text = open(path, encoding="utf-8").read()
    types = re.findall(r"^\s*(?:internal|public)\s+(?:static\s+|sealed\s+)?(?:class|record|struct|interface)\s+\w+",
                       text, re.M)
    if len(types) != 1:
        multi.append("%s(%d)" % (os.path.basename(path), len(types)))
    if len(text.rstrip("\n").split("\n")) > 600:
        too_long.append("%s(%d 行)" % (os.path.basename(path), len(text.rstrip("\n").split("\n"))))
report("每个 Cli 文件恰好一个类型", not multi, ",".join(multi))
report("每个 Cli 文件 <= 600 行", not too_long, ",".join(too_long))

# 3. 133 个 flag 名与 D1a 前逐字一致。
EXPECTED = """
--advance-branch --advance-realm --apply-siege-result --auction-config --auction-ledger
--auction-state --binding-of --branch-state --buy-season-pass --can-equip
--character-report --character-slot-list --character-slot-path --character-slots
--character-stats --city-state --city-table --city-table-path --city-tax-row
--claim-season-pass --complete-quest --config --coupon-count --crystal-config
--crystal-sockets --crystal-state --crystal-txn-count --delete-character
--dungeon-drop-table-path --dungeon-table --dungeon-table-path --enhance-config
--enhance-level --enhance-state --enhance-txn-count --entry-count --exchange-stone
--exp-booster-config --exp-booster-expire --exp-booster-list --exp-booster-reset
--exp-booster-state --exp-config --exp-config-path --exp-state --fatigue-state
--hard-entry-state --hero-codex-state --hero-codex-table-path --ingot-ledger
--item-count --level-of --mainline-alignment --mainline-breakthrough
--mainline-drop-table-path --mainline-list --mainline-path --mainline-seed-settlement
--mainline-stage --mainline-state --mainline-turn-in --mainline-txn --migrate
--monthly-bundle-list --monthly-bundle-path --pass-buy-txn --pass-claim-txn
--pass-season --pass-stage --pass-track --promote-realm-phase --raw-bound-at
--raw-currency-write --raw-drill-write --raw-enhance-write --raw-exp-write
--raw-fatigue-write --raw-grant-character-slot --raw-level-write
--raw-main-stage-write --raw-realm-phase-write --raw-realm-write --raw-share-write
--realm-break-state --realm-passive-state --realm-state --realm-table
--realm-table-path --recharge-config --recharge-list --recharge-state --reserves-of
--revive-scroll-classes --revive-scroll-config --revive-scroll-list
--revive-scroll-state --scroll-cooldown-reset --season-pass-list --season-pass-path
--season-pass-state --set-fatigue-day --set-week-day --settle-drill --share-state
--shares-total --shift-entry-week --shop-config --shop-ledger --shop-list
--shop-state --siege-result-state --siege-territory-table
--siege-territory-table-path --sql --stone-exchange-state --stone-exchange-table
--stone-exchange-table-path --stone-listing-state --talisman-config --talisman-list
--talisman-state --transfer-config --transfer-level --transfer-state
--transfer-txn-count --txn-count --unbind-config --unbind-list --unbind-quote
--unbind-state --wardrobe --weapon-rules --weapon-rules-path
"""
expected = sorted(EXPECTED.split())
parser = read("src/Character/Cli/CliParser.cs")
got = re.findall(r'case "(--[a-z0-9-]+)"', parser)
missing = [f for f in expected if f not in got]
extra = [f for f in set(got) if f not in expected]
report("flag 名 133 个逐字一致（实际 %d）" % len(set(got)), len(set(got)) == 133 and not missing and not extra,
       "缺=%s 多=%s" % (missing[:5], extra[:5]))
report("flag 无重复", len(got) == len(set(got)), "%d 处重复" % (len(got) - len(set(got))))

# 4. 每个 flag 字段都被分派侧引用（没有「解析了却没人用」的孤儿）。
flags_text = read("src/Character/Cli/CliFlags.cs")
fields = re.findall(r"public static [\w?]+ (\w+) =", flags_text)
others = "".join(open(p, encoding="utf-8").read() for p in files
                 if not p.endswith("CliFlags.cs"))
orphans = [f for f in fields if not re.search(r"\b" + f + r"\b", others)]
report("flag 字段无孤儿（%d 个）" % len(fields), bool(fields) and not orphans, ",".join(orphans[:6]))

# 5. 启动与迁移仍在。
report("迁移入口仍在 CoreCli", "migrate=ok" in read("src/Character/Cli/CoreCli.cs"))
report("启动仍在 CliEntry", "new CharacterProcess(configPath)" in read("src/Character/Cli/CliEntry.cs"))
report("连库总入口按域分发", read("src/Character/Cli/DbCli.cs").count(".TryRunDbAsync(ctx)") >= 6)
PY
while IFS= read -r line; do
  case "$line" in
    PASS\ *) ok "${line#PASS }" ;;
    FAIL\ *) bad "${line#FAIL }" ;;
    *) printf '%s\n' "$line" | detail ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 只读入口（不连库）=="
run_readonly() {
  # run_readonly <说明> <期望退出码> <输出里的关键行> <参数...>
  local desc="$1" want="$2" pattern="$3"
  shift 3
  local out code
  out="$("$DOTNET" "$CHARACTER_DLL" "$@" 2>&1)"; code=$?
  if [ "$code" = "$want" ] && grep -q "$pattern" <<< "$out"; then
    ok "${desc}（exit=${code}）"
  else
    bad "${desc}：exit=${code} 期望 ${want}，输出=$(head -1 <<< "$out")"
  fi
}
run_readonly "--shop-list 回清单" 0 '^shop_list_state=ok' --shop-list
run_readonly "--dungeon-table-path 回副本表" 0 '^dungeon_table_state=ok' --dungeon-table-path config/dungeons.json
run_readonly "--exp-config-path 回经验表" 0 '^exp_config_state=ok' --exp-config-path config/exp.json
run_readonly "未知参数回 64" 64 '^unknown_arg=--d1a_nonsense' --d1a_nonsense

# 坏配置仍走只读入口报 config_error（拆分前就在这一步，不连库）。
python3 - "$ROOT/config/shop.json" "$TMP/bad-shop.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
d["items"][0]["price"] = -1
json.dump(d, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
out="$("$DOTNET" "$CHARACTER_DLL" --shop-list --shop-config "$TMP/bad-shop.json" 2>&1)"; code=$?
if [ "$code" = 2 ] && grep -q 'config_error=' <<< "$out"; then
  ok "坏配置在只读入口报 config_error（exit=2）"
else
  bad "坏配置没在只读入口报错：exit=${code} 输出=$(head -1 <<< "$out")"
fi

echo "== 4. 连库入口（无库时仍回 db_env=missing）=="
if [ -n "${MMO_GAMEDB:-}" ] || [ -f /srv/mmo/gamedb.env ]; then
  printf '  ..   本机有角色库（MMO_GAMEDB / gamedb.env），连库段留给 P 卡与真库回归\n'
else
  check_db_missing() {
    local desc="$1"
    shift
    local out code
    out="$("$DOTNET" "$CHARACTER_DLL" "$@" 2>&1)"; code=$?
    if [ "$code" = 3 ] && grep -q '^db_env=missing' <<< "$out"; then
      ok "${desc}（exit=3）"
    else
      bad "${desc}：exit=${code} 输出=$(head -1 <<< "$out")"
    fi
  }
  check_db_missing "--fatigue-state 走进连库总入口" --fatigue-state 1
  check_db_missing "--character-report 走进连库总入口" --character-report 1
  check_db_missing "--migrate 与 --stats 仍能一起解析" --migrate --character-stats
fi

finish "$TITLE"
