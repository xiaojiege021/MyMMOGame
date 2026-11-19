#!/usr/bin/env bash
# B10 核对：强化与结晶进副本伤害。
#
# 完成标准（开发架构 14.5 第 4 行 / 任务队列 #35）：
#   同一角色 +0 与 +12 的同一技能伤害按 7.2 分乘区变高；结晶装上 / 拆下有差；
#   竞技场仍模板、战场仍压 1.3。
#
# 两种模式：
#   bash tools/check/check-b10.sh                      无库段（CI 跑这一段）
#   MMO_B10_HAS_DB=1 bash tools/check/check-b10.sh     加真库段（要 gamedb）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
HAS_DB="${MMO_B10_HAS_DB:-0}"
TITLE="B10"

CHAR_PORT=${MMO_B10_CHAR_PORT:-62155}
GATEWAY_PORT=${MMO_B10_GATEWAY_PORT:-61156}
ACCOUNT="p4b10_wear"
TEST_PASSWORD="p4b10-Pw-3d17"
CHAR_W="p4b10a$RANDOM"
CHARACTER_PID=""
GATEWAY_PID=""
TOKEN=""
WAR=""
TMP="$(mktemp -d)"

cleanup() {
  stop_pid "$GATEWAY_PID"
  stop_pid "$CHARACTER_PID"
  if [ -n "${HAVE_DB:-}" ]; then
    "$DOTNET" "$PROBE_DLL" --delete-character "$CHAR_W" --config "$TMP/character.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" \
      --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Character/Character.csproj src/Battle/Battle.csproj \
            src/Gateway/Gateway.csproj tools/probe/CliProbe/CliProbe.csproj; do
  name="$(basename "$proj" .csproj)"
  "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$name.log" 2>&1 || {
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E 'error ' "$TMP/build-$name.log" | head -4 | detail
  }
done
[ "$BUILD_OK" = "1" ] && ok "角色服、战斗服、网关与探针编译通过"

echo "== 2. 静态断言 =="
if python3 "$ROOT/tools/check/check-b10-static.py" >"$TMP/static.out" 2>&1; then
  ok "两个乘区接上了、数值只在配置里、竞技场 / 战场 / 城战三条未动"
  sed 's/^/       /' "$TMP/static.out"
else
  bad "静态断言失败"
  tail -8 "$TMP/static.out" | detail
fi

echo "== 3. 强化系数从配置现取（不写死） =="
COEF="$(python3 -c 'import json;print(json.load(open("config/enhance.json",encoding="utf-8"))["coef_bps_per_level"])')"
CRY="$(python3 -c 'import json;c=json.load(open("config/crystal.json",encoding="utf-8"))["crystals"];print([x["fixed_value"] for x in c if x["category"]=="attack"][0])')"
OUT="$("$DOTNET" "$PROBE_DLL" --enhance-state --config config/character.json 2>&1)"
if printf '%s' "$OUT" | grep -q "coef_bps_per_level=${COEF}\$"; then
  ok "强化表只读口读到的系数与配置一致（${COEF} bps/级）"
else
  bad "强化表只读口的系数不是配置现值 ${COEF}"; printf '%s' "$OUT" | head -2 | detail
fi

echo "== 4. 四段伤害：+0 / +12 / +12+结晶 =="
preview() {
  "$DOTNET" "$BD" --damage-preview --config config/battle.json --multiplier 1 --defense 0 \
    --random-pct 100 "$@" 2>&1
}
stage_of() { printf '%s' "$1" | sed -n "s/^damage_stage=$2 value=\(.*\)/\1/p"; }
BASE0="$(preview --attack 200 --enhance-coef 0)"
BASE12="$(preview --attack 200 --enhance-coef "$(python3 -c "print(12*${COEF}/10000)")")"
BASE12C="$(preview --attack 200 --enhance-coef "$(python3 -c "print(12*${COEF}/10000)")" --crystal-flat "$CRY")"

E0="$(stage_of "$BASE0" enhance)"
E12="$(stage_of "$BASE12" enhance)"
E12C="$(stage_of "$BASE12C" enhance)"
B0="$(stage_of "$BASE0" base)"
B12="$(stage_of "$BASE12" base)"

if python3 -c "import sys;sys.exit(0 if float('$E12')>float('$E0') else 1)"; then
  ok "同一角色 +0 → +12：强化段 ${E0} → ${E12}（按 7.2 第二段变高）"
else
  bad "强化等级没有让伤害变高：[${E0}] → [${E12}]"
fi

# 期望值现场算：基础 × (1 + 12 × bps/10000)，不写死 248。
if python3 -c "
import sys
want = 200 * (1 + 12 * ${COEF} / 10000)
sys.exit(0 if abs(float('$E12') - want) < 0.01 else 1)"; then
  ok "+12 的强化段等于「基础 × (1 + 12 级 × 系数)」"
else
  bad "+12 强化段不等于公式期望：实际 ${E12}"; fi

if [ "$B0" = "$B12" ]; then
  ok "第一段没被改：+0 与 +12 的基础攻击都是 ${B0}（强化只走第二段乘区）"
else
  bad "强化改到了第一段：基础 ${B0} → ${B12}"
fi

if python3 -c "
import sys
want = 200 * (1 + 12 * ${COEF} / 10000) + ${CRY}
sys.exit(0 if abs(float('$E12C') - want) < 0.01 else 1)"; then
  ok "装上一颗攻击结晶（+${CRY}）：强化段 ${E12} → ${E12C}，加的就是 fixed_value"
else
  bad "结晶固定值没有进第二段：实际 ${E12C}"; fi

echo "== 5. 坏配置起不来（系数越界） =="
python3 - "$TMP" <<'PY'
import json, sys
tmp = sys.argv[1]
base = json.load(open("config/enhance.json", encoding="utf-8"))
for name, value in (("neg", -1), ("huge", 20000)):
    cfg = json.loads(json.dumps(base))
    cfg["coef_bps_per_level"] = value
    json.dump(cfg, open(tmp + "/bad-" + name + ".json", "w", encoding="utf-8"), ensure_ascii=False)
PY
for c in neg huge; do
  out="$("$DOTNET" "$PROBE_DLL" --enhance-state --enhance-config "$TMP/bad-$c.json" 2>&1)"
  rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q 'config_error='; then
    ok "坏配置 ${c}：起不来（config_error=）"
  else
    bad "坏配置 ${c} 没挡住（rc=${rc}）：$(printf '%s' "$out" | head -2)"
  fi
done

if [ "$HAS_DB" != "1" ] || { [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; }; then
  printf '  ..   跳过真库段（MMO_B10_HAS_DB 未开或缺 gamedb 凭据）\n'
  finish "$TITLE"
fi

echo "== 6. 真库段：穿上 +12 与结晶，快照与库对得上 =="
HAVE_DB=1
"$DOTNET" "$PROBE_DLL" --migrate --config config/character.json >"$TMP/migrate.log" 2>&1
grep -q '^migrate=ok' "$TMP/migrate.log" && ok "迁移清单跑通" || { bad "迁移失败"; tail -6 "$TMP/migrate.log" | detail; finish "$TITLE"; }

ROOT="$ROOT" TMP="$TMP" CHAR_PORT="$CHAR_PORT" GATEWAY_PORT="$GATEWAY_PORT" python3 - <<'PY'
import json, os
tmp = os.environ['TMP']
json.dump({
    'schema': 'character-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'db_pool_max': 6, 'bag_slots': 40,
    'dungeon_table': 'config/dungeons.json', 'exp_table': 'config/exp.json',
    'enhance_table': 'config/enhance.json', 'transfer_table': 'config/transfer.json',
    'auction_table': 'config/auction.json',
}, open(tmp + '/character.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
json.dump({
    'schema': 'gateway-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['GATEWAY_PORT'],
    'token_ttl_s': 7200, 'pbkdf2_iterations': 210000,
    'battle_upstream': 'ws://127.0.0.1:60001/battle',
    'character_upstream': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'world_upstream': 'http://127.0.0.1:63108',
}, open(tmp + '/gateway.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
printf '%s\n' "$TEST_PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$ACCOUNT" \
  --seed-slots 8 --config config/gateway.json >>"$TMP/seed.log" 2>&1

"$DOTNET" "$PROBE_DLL" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & CHARACTER_PID=$!
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >"$TMP/gateway.log" 2>&1 & GATEWAY_PID=$!
if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
   && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关"; then
  ok "角色服与网关起来"
else
  tail -10 "$TMP/character.log" | detail; finish "$TITLE"
fi

post() {
  curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}$1?token=${TOKEN}" \
    -H 'Content-Type: application/json' -d "$2"
}
equip_row() {
  "$DOTNET" "$PROBE_DLL" --equip-row "$1" --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^equip_row=ok //p'
}

TOKEN="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/login" -H 'Content-Type: application/json' \
  -d "{\"account\":\"$ACCOUNT\",\"password\":\"${TEST_PASSWORD}\"}" \
  | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("token",""))
except Exception: print("")')"
[ -n "$TOKEN" ] || { bad "登录没拿到令牌"; finish "$TITLE"; }

WAR="$(curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/character?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"name\":\"$CHAR_W\",\"race\":\"mai\",\"class_id\":\"warrior\"}" \
  | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")')"
[ -n "$WAR" ] || { bad "角色没建起来"; finish "$TITLE"; }
"$DOTNET" "$PROBE_DLL" --raw-level-write "$WAR" 20 --config "$TMP/character.json" >/dev/null 2>&1
post /inventory/add "{\"character_id\":$WAR,\"slot\":7,\"item_id\":\"sword_trial\",\"count\":1}" >/dev/null
post /inventory/add "{\"character_id\":$WAR,\"slot\":8,\"item_id\":\"crystal_attack\",\"count\":1}" >/dev/null
R="$(post /character/equip "{\"character_id\":$WAR,\"slot\":7}")"
printf '%s' "$R" | grep -Eq '"ok": *true' || { bad "穿不上武器：$R"; finish "$TITLE"; }

# 直接写 +12：强化成功率是配置里的待定值，真库里点十二次会不确定。
"$DOTNET" "$PROBE_DLL" --raw-enhance-write "$WAR" 7 12 --config "$TMP/character.json" >/dev/null 2>&1
ROW="$(equip_row "$WAR")"
if printf '%s' "$ROW" | grep -q "enhance=12 enhance_coef_bps=$(python3 -c "print(12*${COEF})")"; then
  ok "穿上 +12 的剑：已穿汇总读到 enhance=12、系数 $(python3 -c "print(12*${COEF})") bps"
else
  bad "已穿汇总没读到 +12：$ROW"
fi

R="$(post /character/crystal/install "{\"character_id\":$WAR,\"slot\":7,\"crystal_slot\":8,\"socket\":1}")"
printf '%s' "$R" | grep -Eq '"ok": *true' || { bad "结晶装不上：$R"; finish "$TITLE"; }
ROW="$(equip_row "$WAR")"
if printf '%s' "$ROW" | grep -q "crystal_flat=${CRY}\$"; then
  ok "装上攻击结晶：已穿汇总读到 crystal_flat=${CRY}"
else
  bad "装上结晶后 crystal_flat 不是 ${CRY}：$ROW"
fi

post /character/crystal/remove "{\"character_id\":$WAR,\"slot\":7,\"socket\":1}" >/dev/null
ROW="$(equip_row "$WAR")"
if printf '%s' "$ROW" | grep -q "crystal_flat=0\$"; then
  ok "拆下结晶：crystal_flat 回到 0（装上 / 拆下有差）"
else
  bad "拆下结晶后 crystal_flat 没归零：$ROW"
fi

finish "$TITLE"
