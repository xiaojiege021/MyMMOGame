#!/usr/bin/env bash
# P1-2d 核对：组队少量经验。
#
# 完成标准（开发架构第 14 节第 29 行 + 第 11 节 P1-2）：
#   单人结算没有组队经验；两人进同一场，经验比单人多；
#   伤害和掉落不变；等级停在 100。
#   活动、副本、任务都结算等级经验，走同一笔；加成只加经验。
#
# 做这几段：
#   1. 编译战斗服、角色服、网关、探针
#   2. 静态断言：数值只在配置里、请求带不进经验、只写 exp/level、
#      事务号幂等与行锁、封顶、P0 结算记录与老规则没被改
#   3. 建表、账号、测试角色，起三个进程（战斗服指向角色服）
#   4. 网关结算：单人 / 两人 / 3、4 人 / 幂等 / 并发 / 伪造数值 /
#      只加经验 / 封顶 / 鉴权
#   5. 战斗服两人同场通关自动结算：两人各一笔、伤害与 P0 结算记录不变
#   6. 改配置重启角色服，验数值只有一个正本
#   7. 主线 / 深渊 / 温泉 / 巢穴四条老规则不回退
#
# 用法：bash tools/check-p1-2d.sh
# 连库段要在有 gamedb 的机器上跑（笔记本）；缺凭据时明确失败。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
PROBE_DLL="$ROOT/tools/BattleProbe/bin/Release/net10.0/BattleProbe.dll"

BATTLE_PORT=${MMO_P12D_BATTLE_PORT:-60017}
CHAR_PORT=${MMO_P12D_CHAR_PORT:-62105}
GATEWAY_PORT=${MMO_P12D_GATEWAY_PORT:-61106}
ACCOUNT="p12d_user"
OTHER="p12d_other"
TEST_PASSWORD="p12d-Pw-4e73"

NAME_LEDGER="p12d_ledger"
NAME_CAP="p12d_cap"
NAME_CONCURRENT="p12d_concurrent"
NAME_OLDRULES="p12d_oldrules"
NAME_A="p12d_a"
NAME_B="p12d_b"
CHAR_NAMES=("$NAME_LEDGER" "$NAME_CAP" "$NAME_CONCURRENT" "$NAME_OLDRULES" "$NAME_A" "$NAME_B")

PASS=0
FAIL=0
TMP="$(mktemp -d)"
BATTLE_PID=""
CHARACTER_PID=""
GATEWAY_PID=""
HAVE_DB=""
BG_PIDS=()

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
detail() { sed 's/^/       /'; }

stop_pid() {
  local pid="$1"
  [ -n "$pid" ] || return 0
  if kill -0 "$pid" 2>/dev/null; then
    kill -TERM "$pid" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
}

cleanup() {
  for pid in "${BG_PIDS[@]:-}"; do stop_pid "$pid"; done
  stop_pid "$GATEWAY_PID"
  stop_pid "$CHARACTER_PID"
  stop_pid "$BATTLE_PID"
  if [ -n "$HAVE_DB" ]; then
    for name in "${CHAR_NAMES[@]}"; do
      "$DOTNET" "$CHARACTER_DLL" --delete-character "$name" --config "$TMP/character.json" >/dev/null 2>&1 || true
    done
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$OTHER" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

split_response() {
  BODY="${1%$'\n'*}"
  CODE="${1##*$'\n'}"
}

# curl -w '\n%{http_code}' 的回包拆开用；BSD head 不认 -n -1，别用管道截。
body_of() { printf '%s' "${1%$'\n'*}"; }
code_of() { printf '%s' "${1##*$'\n'}"; }

json_field() {
  printf '%s' "$1" | python3 -c 'import json,sys
try:
    v = json.load(sys.stdin).get("'"$2"'", "")
    print(v)
except Exception:
    print("")'
}

login_token() {
  curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/login" \
    -H 'Content-Type: application/json' \
    -d "{\"account\":\"$1\",\"password\":\"${TEST_PASSWORD}\"}" \
    | python3 -c 'import json,sys
try:
    print(json.load(sys.stdin).get("token",""))
except Exception:
    print("")'
}

create_char() {
  local resp
  resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"$2\",\"race\":\"mai\",\"class_id\":\"warrior\"}")"
  split_response "$resp"
  NEW_ID="$(json_field "$BODY" character_id)"
  [ "$CODE" = "200" ] && [ -n "$NEW_ID" ]
}

settle_exp() {
  # $1=令牌（空串不带） $2=角色 $3=事务号 $4=人数 $5=来源
  local token="$1" url="http://127.0.0.1:${GATEWAY_PORT}/exp/settle"
  [ -n "$token" ] && url="${url}?token=${token}"
  curl -s -m 10 -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2},\"txn_id\":\"${3}\",\"party_size\":${4},\"source\":\"${5}\"}"
}

exp_state() {
  "$DOTNET" "$CHARACTER_DLL" --exp-state "$1" --config "$TMP/character.json" 2>&1 \
    | grep -m1 '^exp_state=' || true
}

field_of() { printf '%s' "$1" | grep -o "$2=[0-9-]*" | head -1 | cut -d= -f2; }

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-2d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

BUILD_OK=1
for proj in src/Battle/Battle.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj tools/BattleProbe/BattleProbe.csproj; do
  if ! "$DOTNET" build "$ROOT/$proj" -c Release --nologo -v q >"$TMP/build-$(basename "$proj" .csproj).log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E "error " "$TMP/build-$(basename "$proj" .csproj).log" | head -4 | detail
  fi
done
if [ "$BUILD_OK" = "1" ]; then
  ok "战斗服、角色服、网关、探针编译通过"
else
  echo
  echo "P1-2d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
CFG="$ROOT/config/exp.json"
SQL="$ROOT/db/character/character.sql"
LEDGER_SQL="$ROOT/db/character/exp_settlement.sql"
PROTO="$ROOT/proto/mmo/character.proto"
EXP_TABLE="$ROOT/src/Character/ExpTable.cs"
EXP_REPO="$ROOT/src/Character/ExpSettlementRepository.cs"
SVC="$ROOT/src/Character/CharacterGrpcService.cs"
API="$ROOT/src/Gateway/CharacterApi.cs"
CHAR_PROCESS="$ROOT/src/Character/CharacterProcess.cs"
BATTLE_EXP="$ROOT/src/Battle/ExpSettlement.cs"
BATTLE_CFG="$ROOT/src/Battle/Config.cs"
ENCOUNTER="$ROOT/src/Battle/Encounter.cs"
BATTLE_SRV="$ROOT/src/Battle/Server.cs"
HOT_SPRING="$ROOT/src/Character/HotSpringRepository.cs"
SHARE_REPO="$ROOT/src/Character/WeeklyShareRepository.cs"
DUNGEONS="$ROOT/config/dungeons.json"

BASE="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["base_exp"])' "$CFG")"
BONUS_PCT="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["party_bonus_pct"])' "$CFG")"
MIN_SIZE="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["party_min_size"])' "$CFG")"
CAP="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["level_cap"])' "$CFG")"
PER_LEVEL="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["exp_per_level"])' "$CFG")"
if [ "$CAP" = "100" ] && [ "$BASE" = "120" ] && [ "$BONUS_PCT" = "10" ] \
   && [ "$MIN_SIZE" = "2" ] && [ "$PER_LEVEL" = "1000" ] \
   && grep -q '"level_cap": 100' "$CFG" \
   && grep -q '"base_exp"' "$CFG" \
   && grep -q '"party_bonus_pct"' "$CFG" \
   && grep -q '"exp_per_level"' "$CFG" \
   && grep -q '数值待定' "$CFG"; then
  ok "基础经验、加成幅度、每级经验、封顶 100 全在 config/exp.json，未写定的数值标明待定"
else
  bad "经验数值没进配置或被写死在代码里：base=$BASE bonus=$BONUS_PCT cap=$CAP"
fi

if grep -qF '[JsonPropertyName("base_exp")]' "$EXP_TABLE" \
   && grep -qF '[JsonPropertyName("party_bonus_pct")]' "$EXP_TABLE" \
   && grep -qF '[JsonPropertyName("exp_per_level")]' "$EXP_TABLE" \
   && grep -qF '[JsonPropertyName("level_cap")]' "$EXP_TABLE" \
   && grep -q 'exp.json 缺 base_exp' "$EXP_TABLE" \
   && grep -q 'level_cap 必须在 1 到 100 之间' "$EXP_TABLE" \
   && grep -q 'ExpTable.Load(config.ExpTable)' "$CHAR_PROCESS" \
   && grep -q 'exp_table_version=' "$CHAR_PROCESS"; then
  ok "角色服缺经验表字段就起不来，启动时把这一份数值打进日志"
else
  bad "经验表缺字段时可能有兜底数字，或角色服没读这份表"
fi

if grep -q 'partySize >= PartyMinSize' "$EXP_TABLE" \
   && grep -q 'BaseExp \* PartyBonusPct / 100' "$EXP_TABLE" \
   && grep -q 'BaseExp + PartyBonus(partySize)' "$EXP_TABLE" \
   && grep -q 'while (next < LevelCap' "$EXP_TABLE"; then
  ok "加成只在至少两人时给一截、只加经验；升级到 level_cap 就停"
else
  bad "组队加成或封顶逻辑不在这一笔里"
fi

if grep -qF 'update \"角色\".character set level = $2, exp = $3 where id = $1' "$EXP_REPO" \
   && grep -q 'for update' "$EXP_REPO" \
   && grep -q 'on conflict (txn_id) do nothing returning txn_id' "$EXP_REPO" \
   && grep -q 'BeginTransactionAsync' "$EXP_REPO" \
   && grep -q 'CommitAsync' "$EXP_REPO" \
   && grep -q 'RollbackAsync' "$EXP_REPO"; then
  ok "经验结算：行锁 + 事务号抢号 + 只写 level/exp，重复整笔回滚"
else
  bad "经验结算没走同一套事务口径，或碰了别的列"
fi

if grep -q 'character_exp_non_negative' "$SQL" \
   && grep -q 'ADD COLUMN IF NOT EXISTS exp bigint' "$SQL" \
   && grep -q 'level BETWEEN 1 AND 100' "$SQL" \
   && grep -q 'PRIMARY KEY' "$LEDGER_SQL" \
   && grep -q 'CHECK (exp_gain >= 0)' "$LEDGER_SQL"; then
  ok "库层仍有等级 1–100 与非负经验两道闸，结算流水有幂等主键"
else
  bad "库表或约束没跟上"
fi

if sed -n '/message SettleExpRequest {/,/^}/p' "$PROTO" \
     | grep -qiE 'base_exp|party_bonus|exp_gain|reward|[0-9]+ = .*(exp|bonus)'; then
  bad "结算请求里带了经验数值字段，改包能改加成"
else
  ok "结算请求只有账号、角色、事务号、来源与人数，没有经验数值"
fi

if grep -q 'public sealed record SettleExpBody(' "$API" \
   && grep -q 'app.MapPost("/exp/settle"' "$API" \
   && grep -q 'TryAuthorize(context, tokens, "exp_settle"' "$API" \
   && grep -q 'Account = account' "$API" \
   && grep -q 'WriteRpcErrorAsync(context, "exp_settle"' "$API" \
   && grep -q 'rpc SettleExp(SettleExpRequest)' "$PROTO" \
   && grep -q 'int64 base_exp = 2;' "$PROTO" \
   && grep -q 'int32 level_cap = 8;' "$PROTO"; then
  ok "网关只鉴权转发，角色服同一笔回基础/加成/总额/封顶"
else
  bad "网关结算入口或回包口径不对"
fi

if grep -q 'expSettlements.SettleAsync' "$SVC" \
   && grep -q 'account.Length == 0 ? null : account' "$SVC" \
   && grep -q 'inventory.OwnsAsync(account, (long)request.CharacterId' "$SVC" \
   && grep -q 'party_size_invalid' "$SVC" \
   && grep -q 'AtCap = outcome.Level >= expTable.LevelCap' "$SVC" \
   && grep -q 'request.Account' "$SVC"; then
  ok "角色服按账号复核归属，服间直连才允许空账号；人数越界被挡"
else
  bad "角色服结算没复核归属或没判人数"
fi

if grep -q 'SettleParty(_settlementId' "$ENCOUNTER" \
   && grep -q '"dungeon"' "$ENCOUNTER" \
   && grep -q 'ExpGain = _config.Boss.ExpOnClear' "$ENCOUNTER" \
   && grep -q 'ExpGain = string.IsNullOrEmpty(_settlementId) ? 0 : _config.Boss.ExpOnClear' "$ENCOUNTER" \
   && grep -q 'NextSessionId(), partySize' "$BATTLE_SRV" \
   && [ "$(grep -c '_settlements.Write(' "$ENCOUNTER")" = "1" ]; then
  ok "战斗服通关才把这一场交给角色服，P0 结算记录与回包经验仍走老口径"
else
  bad "战斗服结算路径改动了 P0 的结算记录或写入点"
fi

if grep -q 'new SettleExpRequest' "$BATTLE_EXP" \
   && grep -q 'TxnId = txnId' "$BATTLE_EXP" \
   && grep -q 'PartySize = (uint)partySize' "$BATTLE_EXP" \
   && ! grep -qE 'BaseExp =|ExpGain =|PartyBonus =' "$BATTLE_EXP" \
   && grep -q 'class NoExpSettlement' "$BATTLE_EXP" \
   && grep -q 'exp_upstream=' "$ROOT/src/Battle/BattleProcess.cs" \
   && grep -q 'ExpUpstream' "$ROOT/src/Battle/BattleProcess.cs"; then
  ok "战斗服只传事务号/来源/人数，没配上游就退回 P0 本机口径"
else
  bad "战斗服带了经验数值，或没配上游时也会改角色数据"
fi

if grep -q 'long DamageOf(Member member, SkillDef skill)' "$ENCOUNTER" \
   && grep -q 'config.Dungeons.MonsterHpScalePerPlayer' "$ENCOUNTER" \
   && grep -qF 'update \"角色\".character set fatigue = $2, fatigue_day = current_date' "$HOT_SPRING" \
   && grep -q 'var cost = partySize' "$SHARE_REPO" \
   && grep -q '"fatigue_daily_cap": 6000' "$DUNGEONS" \
   && grep -q '"hot_spring_recovery": 1500' "$DUNGEONS" \
   && grep -q '"weekly_share_cap": 12' "$DUNGEONS"; then
  ok "经验数值只有一个正本；伤害公式、怪物缩放、疲劳/温泉/份额老规则都没回退"
else
  bad "经验数值可能分叉，或前面的进本规则被本行改动"
fi

echo "== 3. 环境与启动 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env"
  echo
  echo "P1-2d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
HAVE_DB=1
ok "库凭据在位：$( [ -n "${MMO_GAMEDB:-}" ] && echo "env:MMO_GAMEDB" || echo "file:/srv/mmo/gamedb.env" )"

BOSS_ID="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["boss"]["id"])' "$ROOT/config/battle.json")"
SKILL_ID="$(python3 -c 'import json,sys
d=json.load(open(sys.argv[1],encoding="utf-8"))
print([s["id"] for s in d["skills"] if s["class_id"]=="warrior" and s["kind"]=="active"][0])' "$ROOT/config/skills.json")"

python3 - "$ROOT" "$TMP" "$BATTLE_PORT" "$CHAR_PORT" <<'PY'
import json, os, sys
root, tmp, battle_port, char_port = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
cfg = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
cfg["listen"] = "127.0.0.1:" + battle_port
cfg["settlement_dir"] = os.path.join(tmp, "settlements")
cfg["character_dir"] = os.path.join(tmp, "characters")
cfg["skill_table"] = "skills.json"
cfg["class_table"] = "classes.json"
cfg["dungeon_table"] = "dungeons.json"
# 本行只验经验结算，Boss 不出手，免得放大后的攻击把施法者打倒。
cfg.pop("attack", None)
cfg["boss"].pop("attack", None)
cfg["boss"].pop("phases", None)
cfg["exp_upstream"] = "http://127.0.0.1:" + char_port
json.dump(cfg, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)
PY
cp "$ROOT/config/skills.json" "$ROOT/config/classes.json" "$ROOT/config/dungeons.json" "$ROOT/config/exp.json" "$TMP/"
cp "$ROOT/config/exp.json" "$TMP/exp.json"

cat > "$TMP/character.json" <<JSON
{
  "schema": "character-config/1",
  "listen": "http://127.0.0.1:${CHAR_PORT}",
  "db_pool_max": 6,
  "bag_slots": 40,
  "dungeon_table": "${TMP}/dungeons.json",
  "exp_table": "${TMP}/exp.json"
}
JSON

cat > "$TMP/gateway.json" <<JSON
{
  "schema": "gateway-config/1",
  "listen": "http://127.0.0.1:${GATEWAY_PORT}",
  "token_ttl_s": 3600,
  "pbkdf2_iterations": 10000,
  "character_upstream": "http://127.0.0.1:${CHAR_PORT}",
  "battle_upstream": "ws://127.0.0.1:${BATTLE_PORT}/battle"
}
JSON

if ! "$DOTNET" "$GATEWAY_DLL" --migrate --config "$TMP/gateway.json" \
      --sql "$ROOT/db/character/account.sql" >"$TMP/migrate-account.log" 2>&1; then
  bad "账号表建不出来"
  tail -5 "$TMP/migrate-account.log" | detail
  echo
  echo "P1-2d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

MIGRATE_OK=1
for sql in character.sql dungeon_entry.sql inventory.sql currency.sql transaction.sql exp_settlement.sql; do
  if ! "$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" \
        --sql "$ROOT/db/character/$sql" >"$TMP/migrate-$sql.log" 2>&1; then
    MIGRATE_OK=0
    bad "表建不出来：$sql"
    tail -3 "$TMP/migrate-$sql.log" | detail
  fi
done
if [ "$MIGRATE_OK" = "1" ]; then
  ok "角色表（含 exp 与非负闸）与经验结算流水表都在位"
else
  echo
  echo "P1-2d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

for acct in "$ACCOUNT" "$OTHER"; do
  printf '%s\n' "$TEST_PASSWORD" \
    | "$DOTNET" "$GATEWAY_DLL" --seed-account "$acct" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
done
ok "两个测试账号已写入"

: > "$TMP/battle.log"
"$DOTNET" "$BATTLE_DLL" --config "$TMP/battle.json" >>"$TMP/battle.log" 2>&1 &
BATTLE_PID=$!
BATTLE_READY=0
for _ in $(seq 1 120); do
  if grep -q "^listen=http://127.0.0.1:${BATTLE_PORT}\$" "$TMP/battle.log" 2>/dev/null; then
    BATTLE_READY=1
    break
  fi
  if ! kill -0 "$BATTLE_PID" 2>/dev/null; then break; fi
  sleep 0.1
done
if [ "$BATTLE_READY" = "1" ] && grep -q "^exp_upstream=http://127.0.0.1:${CHAR_PORT}\$" "$TMP/battle.log"; then
  ok "战斗服在 127.0.0.1:${BATTLE_PORT} 上，经验结算指向角色服"
else
  bad "战斗服没起来或没读到 exp_upstream"
  tail -8 "$TMP/battle.log" | detail
  echo
  echo "P1-2d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

: > "$TMP/character.log"
"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >>"$TMP/character.log" 2>&1 &
CHARACTER_PID=$!
CHAR_READY=0
for _ in $(seq 1 100); do
  if grep -q '^ready name=Character' "$TMP/character.log" 2>/dev/null; then
    CHAR_READY=1
    break
  fi
  if ! kill -0 "$CHARACTER_PID" 2>/dev/null; then break; fi
  sleep 0.1
done
if [ "$CHAR_READY" = "1" ]; then
  ok "角色服在 127.0.0.1:${CHAR_PORT} 上"
else
  bad "角色服没起来"
  tail -8 "$TMP/character.log" | detail
  echo
  echo "P1-2d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
if grep -m1 '^exp_table_version=' "$TMP/character.log" \
     | grep -q 'base_exp=120 party_bonus_pct=10 party_min_size=2 exp_per_level=1000'; then
  ok "角色服启动时读到的就是配置里的 120 / 10 / 2 / 1000"
else
  bad "角色服没读到经验表配置"
  grep -m1 '^exp_table_version=' "$TMP/character.log" | detail
fi

: > "$TMP/gateway.log"
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >>"$TMP/gateway.log" 2>&1 &
GATEWAY_PID=$!
GATEWAY_READY=0
for _ in $(seq 1 150); do
  if grep -q '^ready name=Gateway' "$TMP/gateway.log" 2>/dev/null; then
    if curl -s -m 2 "http://127.0.0.1:${GATEWAY_PORT}/health" | grep -q '"status":"ok"'; then
      GATEWAY_READY=1
      break
    fi
  fi
  if ! kill -0 "$GATEWAY_PID" 2>/dev/null; then break; fi
  sleep 0.2
done
if [ "$GATEWAY_READY" = "1" ]; then
  ok "网关在 127.0.0.1:${GATEWAY_PORT} 上"
else
  bad "网关没起来"
  tail -8 "$TMP/gateway.log" | detail
  echo
  echo "P1-2d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then
  ok "两个账号都能登录"
else
  bad "登录没拿到令牌"
  echo
  echo "P1-2d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

for name in "${CHAR_NAMES[@]}"; do
  "$DOTNET" "$CHARACTER_DLL" --delete-character "$name" --config "$TMP/character.json" >/dev/null 2>&1 || true
done
CREATED=1
for name in "${CHAR_NAMES[@]}"; do
  if ! create_char "$TOKEN" "$name"; then
    CREATED=0
    bad "角色没建出来：$name（HTTP ${CODE}）"
  fi
  case "$name" in
    "$NAME_LEDGER")     ID_LEDGER="$NEW_ID" ;;
    "$NAME_CAP")        ID_CAP="$NEW_ID" ;;
    "$NAME_CONCURRENT") ID_CONCURRENT="$NEW_ID" ;;
    "$NAME_OLDRULES")   ID_OLDRULES="$NEW_ID" ;;
    "$NAME_A")          ID_A="$NEW_ID" ;;
    "$NAME_B")          ID_B="$NEW_ID" ;;
  esac
done
if [ "$CREATED" = "1" ]; then
  ok "六个测试角色都建好了"
else
  echo
  echo "P1-2d 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 4. 网关结算：单人没有加成，两人这一笔更多 =="
RESERVES_BEFORE="$("$DOTNET" "$CHARACTER_DLL" --reserves-of "$ID_LEDGER" --config "$TMP/character.json" 2>&1 | grep -m1 '^reserves=' || true)"
CURRENCY_BEFORE="$(curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/currency?token=${TOKEN}&character_id=${ID_LEDGER}")"
BAG_BEFORE="$(curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/inventory?token=${TOKEN}&character_id=${ID_LEDGER}")"

ONE="$(settle_exp "$TOKEN" "$ID_LEDGER" "p12d-solo-1" 1 "dungeon")"
split_response "$ONE"
S1_BASE="$(json_field "$BODY" base_exp)"
S1_BONUS="$(json_field "$BODY" party_bonus)"
S1_GAIN="$(json_field "$BODY" exp_gain)"
S1_LEVEL="$(json_field "$BODY" level)"
S1_EXP="$(json_field "$BODY" exp)"
S1_CAP="$(json_field "$BODY" level_cap)"
S1_ATCAP="$(json_field "$BODY" at_cap)"
STATE1="$(exp_state "$ID_LEDGER")"
if [ "$CODE" = "200" ] && [ "$S1_BASE" = "120" ] && [ "$S1_BONUS" = "0" ] \
   && [ "$S1_GAIN" = "120" ] && [ "$S1_LEVEL" = "1" ] && [ "$S1_EXP" = "120" ] \
   && [ "$S1_CAP" = "100" ] && [ "$S1_ATCAP" = "False" ] \
   && [ "$(field_of "$STATE1" exp)" = "120" ] \
   && [ "$(field_of "$STATE1" rows)" = "1" ] \
   && [ "$(field_of "$STATE1" total_gain)" = "120" ]; then
  ok "单人结算：基础 120、组队加成 0、这一笔 120，库里也只有 120"
else
  bad "单人结算口径不对：HTTP ${CODE} ${BODY} / ${STATE1}"
fi

TWO="$(settle_exp "$TOKEN" "$ID_LEDGER" "p12d-party-2" 2 "dungeon")"
split_response "$TWO"
S2_BASE="$(json_field "$BODY" base_exp)"
S2_BONUS="$(json_field "$BODY" party_bonus)"
S2_GAIN="$(json_field "$BODY" exp_gain)"
STATE2="$(exp_state "$ID_LEDGER")"
if [ "$CODE" = "200" ] && [ "$S2_BASE" = "120" ] && [ "$S2_BONUS" = "12" ] \
   && [ "$S2_GAIN" = "132" ] && [ "$S2_GAIN" -gt "$S1_GAIN" ] \
   && [ "$(field_of "$STATE2" exp)" = "252" ] \
   && [ "$(field_of "$STATE2" rows)" = "2" ] \
   && [ "$(field_of "$STATE2" total_gain)" = "252" ]; then
  ok "两人同一场：基础 120 + 一截 12 = 132，严格多于单人的 120"
else
  bad "两人结算口径不对：HTTP ${CODE} ${BODY} / ${STATE2}"
fi

THREE="$(settle_exp "$TOKEN" "$ID_LEDGER" "p12d-party-3" 3 "activity")"
split_response "$THREE"
FOUR="$(settle_exp "$TOKEN" "$ID_LEDGER" "p12d-party-4" 4 "task")"
split_response "$FOUR"
if [ "$(json_field "$(body_of "$THREE")" party_bonus)" = "12" ] \
   && [ "$(json_field "$(body_of "$FOUR")" party_bonus)" = "12" ] \
   && [ "$(json_field "$(body_of "$THREE")" exp_gain)" = "132" ] \
   && [ "$(json_field "$(body_of "$FOUR")" exp_gain)" = "132" ] \
   && [ "$(json_field "$(body_of "$THREE")" source)" = "activity" ] \
   && [ "$(json_field "$(body_of "$FOUR")" source)" = "task" ]; then
  ok "3、4 人仍是同一截加成；活动与任务走同一笔，不另写一套"
else
  bad "人数叠加或来源分叉：3 人 ${THREE} / 4 人 ${FOUR}"
fi

DUP="$(settle_exp "$TOKEN" "$ID_LEDGER" "p12d-solo-1" 1 "dungeon")"
split_response "$DUP"
STATE_DUP="$(exp_state "$ID_LEDGER")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" duplicate)" = "True" ] \
   && [ "$(json_field "$BODY" exp_gain)" = "0" ] \
   && [ "$(field_of "$STATE_DUP" rows)" = "4" ] \
   && [ "$(field_of "$STATE_DUP" total_gain)" = "516" ]; then
  ok "同一个事务号再提交：duplicate、这一笔 0，账上一笔不多"
else
  bad "幂等没生效：HTTP ${CODE} ${BODY} / ${STATE_DUP}"
fi

FORGED="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/exp/settle?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID_LEDGER},\"txn_id\":\"p12d-forged\",\
\"party_size\":1,\"source\":\"dungeon\",\"exp_gain\":999999,\
\"base_exp\":999,\"party_bonus\":999,\"level\":100}")"
split_response "$FORGED"
STATE_FORGED="$(exp_state "$ID_LEDGER")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" exp_gain)" = "120" ] \
   && [ "$(json_field "$BODY" party_bonus)" = "0" ] \
   && [ "$(field_of "$STATE_FORGED" rows)" = "5" ] \
   && [ "$(field_of "$STATE_FORGED" total_gain)" = "636" ]; then
  ok "请求里伪造经验/加成/等级都无效，仍按配置的 120 入账"
else
  bad "伪造字段改到了这一笔：HTTP ${CODE} ${BODY} / ${STATE_FORGED}"
fi

echo "== 5. 只加经验、并发与封顶 =="
RESERVES_AFTER="$("$DOTNET" "$CHARACTER_DLL" --reserves-of "$ID_LEDGER" --config "$TMP/character.json" 2>&1 | grep -m1 '^reserves=' || true)"
CURRENCY_AFTER="$(curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/currency?token=${TOKEN}&character_id=${ID_LEDGER}")"
BAG_AFTER="$(curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/inventory?token=${TOKEN}&character_id=${ID_LEDGER}")"
ENTRY_AFTER="$("$DOTNET" "$CHARACTER_DLL" --entry-count "$ID_LEDGER" --config "$TMP/character.json" 2>&1 | grep -m1 '^entry_count=' || true)"
if [ "$RESERVES_BEFORE" = "$RESERVES_AFTER" ] \
   && [ "$CURRENCY_BEFORE" = "$CURRENCY_AFTER" ] \
   && [ "$BAG_BEFORE" = "$BAG_AFTER" ] \
   && [ "$ENTRY_AFTER" = "entry_count=0 id=${ID_LEDGER}" ]; then
  ok "只加经验：疲劳/周份额、货币、背包、进本记录前后都没动"
else
  bad "经验结算碰了别的账：${RESERVES_BEFORE} -> ${RESERVES_AFTER}，进本 ${ENTRY_AFTER}"
fi

STATE_C0="$(exp_state "$ID_CONCURRENT")"
settle_exp "$TOKEN" "$ID_CONCURRENT" "p12d-con-a" 2 "dungeon" >"$TMP/con-a.txt" 2>&1 &
BG_PIDS+=("$!")
settle_exp "$TOKEN" "$ID_CONCURRENT" "p12d-con-b" 2 "dungeon" >"$TMP/con-b.txt" 2>&1 &
BG_PIDS+=("$!")
wait "${BG_PIDS[0]}" "${BG_PIDS[1]}" 2>/dev/null || true
STATE_C1="$(exp_state "$ID_CONCURRENT")"
if [ "$(field_of "$STATE_C0" rows)" = "0" ] \
   && [ "$(field_of "$STATE_C1" rows)" = "2" ] \
   && [ "$(field_of "$STATE_C1" total_gain)" = "264" ] \
   && [ "$(field_of "$STATE_C1" exp)" = "264" ]; then
  ok "并发两笔不同事务号冲同一角色：行锁串行，两笔都在、没有算丢"
else
  bad "并发结算算丢了：${STATE_C0} -> ${STATE_C1}"
fi

LEVEL_SET="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/character/level?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID_CAP},\"level\":100}")"
split_response "$LEVEL_SET"
CAP1="$(settle_exp "$TOKEN" "$ID_CAP" "p12d-cap-1" 4 "dungeon")"
split_response "$CAP1"
CAP2="$(settle_exp "$TOKEN" "$ID_CAP" "p12d-cap-2" 4 "arena")"
split_response "$CAP2"
STATE_CAP="$(exp_state "$ID_CAP")"
RAW_LEVEL="$("$DOTNET" "$CHARACTER_DLL" --raw-level-write "$ID_CAP" 101 --config "$TMP/character.json" 2>&1 | grep -m1 '^raw_level=' || true)"
RAW_EXP="$("$DOTNET" "$CHARACTER_DLL" --raw-exp-write "$ID_CAP" -1 --config "$TMP/character.json" 2>&1 | grep -m1 '^raw_exp=' || true)"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" at_cap)" = "True" ] \
   && [ "$(field_of "$STATE_CAP" level)" = "100" ] \
   && [ "$(field_of "$STATE_CAP" total_gain)" = "264" ] \
   && printf '%s' "$RAW_LEVEL" | grep -q 'raw_level=rejected' \
   && printf '%s' "$RAW_EXP" | grep -q 'raw_exp=rejected'; then
  ok "等级停在 100：经验照记但等级不涨，直写 101 与负经验都被库层 CHECK 挡"
else
  bad "封顶或库层闸不对：${STATE_CAP} / ${RAW_LEVEL} / ${RAW_EXP}"
fi

echo "== 6. 网关鉴权与参数 =="
NO_TOKEN="$(settle_exp "" "$ID_LEDGER" "p12d-no-token" 1 "dungeon")"
split_response "$NO_TOKEN"
OTHERS="$(settle_exp "$OTHER_TOKEN" "$ID_LEDGER" "p12d-other" 2 "dungeon")"
split_response "$OTHERS"
BAD_BODY="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/exp/settle?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID_LEDGER}}")"
split_response "$BAD_BODY"
BAD_PARTY="$(settle_exp "$TOKEN" "$ID_LEDGER" "p12d-bad-party" 99 "dungeon")"
split_response "$BAD_PARTY"
if [ "$(code_of "$NO_TOKEN")" = "401" ] \
   && [ "$(code_of "$OTHERS")" = "404" ] \
   && [ "$(code_of "$BAD_BODY")" = "400" ] \
   && [ "$(code_of "$BAD_PARTY")" = "400" ]; then
  ok "无令牌 401、别人的角色 404、缺参数 400、人数越界 400"
else
  bad "鉴权或参数校验不对：${NO_TOKEN##*$'\n'} / ${OTHERS##*$'\n'} / ${BAD_BODY##*$'\n'} / ${BAD_PARTY##*$'\n'}"
  printf '       others_body=%s\n' "$(body_of "$OTHERS")"
  grep -E 'exp_settle|character_not_found' "$TMP/character.log" | tail -4 | detail
fi

echo "== 7. 战斗服：两人同一场通关，自动走同一笔 =="
STATE_A0="$(exp_state "$ID_A")"
STATE_B0="$(exp_state "$ID_B")"
BATTLE_URL="ws://127.0.0.1:${BATTLE_PORT}/battle"
CLEAR="$("$DOTNET" "$PROBE_DLL" \
  --url "${BATTLE_URL}?party_size=2" \
  --case p12d_clear --boss "$BOSS_ID" --hit "$SKILL_ID" \
  --a-char "$ID_A" --b-char "$ID_B" --party-size 2 2>&1)"
CLEAR_ID="$(printf '%s' "$CLEAR" | grep -o 'settlement_id=[a-z0-9]*' | head -1 | cut -d= -f2)"
CLEAR_EXP="$(printf '%s' "$CLEAR" | grep -o 'exp_gain=[0-9]*' | head -1 | cut -d= -f2)"
CLEAR_DMG="$(printf '%s' "$CLEAR" | grep -o 'damage=[0-9]*' | head -1 | cut -d= -f2)"
STATE_A1="$(exp_state "$ID_A")"
STATE_B1="$(exp_state "$ID_B")"
SETTLE_LOG_LINES="$(grep -c '^exp_settle=ok ' "$TMP/battle.log" || true)"
if printf '%s' "$CLEAR" | grep -q '^verdict=ok' \
   && [ -n "$CLEAR_ID" ] && [ "$CLEAR_EXP" = "120" ] \
   && [ "$(field_of "$STATE_A0" rows)" = "0" ] \
   && [ "$(field_of "$STATE_B0" rows)" = "0" ] \
   && [ "$(field_of "$STATE_A1" rows)" = "1" ] \
   && [ "$(field_of "$STATE_B1" rows)" = "1" ] \
   && [ "$(field_of "$STATE_A1" total_gain)" = "132" ] \
   && [ "$(field_of "$STATE_B1" total_gain)" = "132" ] \
   && [ "$(field_of "$STATE_A1" exp)" = "132" ] \
   && [ "$(field_of "$STATE_B1" exp)" = "132" ] \
   && [ "$SETTLE_LOG_LINES" = "2" ] \
   && grep -q 'gain=132 bonus=12' "$TMP/battle.log"; then
  ok "两人同场打完：每人各入账 132（含 12 加成），战斗服两笔都走角色服"
else
  bad "自动结算没到账或加成不对：${CLEAR} / A=${STATE_A1} / B=${STATE_B1} / log=${SETTLE_LOG_LINES}"
  grep -E '^exp_settle' "$TMP/battle.log" | tail -4 | detail
fi

RECORD="$(python3 - "$TMP/settlements" "$CLEAR_ID" <<'PY'
import json, os, sys
p = os.path.join(sys.argv[1], sys.argv[2] + ".json")
if not os.path.isfile(p):
    print("missing")
else:
    d = json.load(open(p, encoding="utf-8"))
    print("exp_gain=%s damage_total=%s cast_count=%s caster_id=%s"
          % (d.get("exp_gain"), d.get("damage_total"), d.get("cast_count"), d.get("caster_id")))
PY
)"
if printf '%s' "$RECORD" | grep -q 'exp_gain=120' \
   && printf '%s' "$RECORD" | grep -qE 'damage_total=[1-9][0-9]*' \
   && printf '%s' "$RECORD" | grep -qE 'cast_count=[1-9][0-9]*'; then
  ok "P0 本地结算记录仍是 exp_gain=120、伤害合计与施法次数照旧，掉落没被人数改"
else
  bad "P0 结算记录被本行改动：${RECORD}"
fi

PD1="$("$DOTNET" "$PROBE_DLL" --url "${BATTLE_URL}?party_size=1&party=p12d_d1&char=71" \
  --case p12c_player_damage --boss "$BOSS_ID" --hit "$SKILL_ID" 2>&1)"
PD2="$("$DOTNET" "$PROBE_DLL" --url "${BATTLE_URL}?party_size=2&party=p12d_d2&char=72" \
  --case p12c_player_damage --boss "$BOSS_ID" --hit "$SKILL_ID" 2>&1)"
PD1_DMG="$(printf '%s' "$PD1" | grep -o 'damage=[0-9]*' | head -1 | cut -d= -f2)"
PD2_DMG="$(printf '%s' "$PD2" | grep -o 'damage=[0-9]*' | head -1 | cut -d= -f2)"
if [ -n "$PD1_DMG" ] && [ "$PD1_DMG" = "$PD2_DMG" ] && [ "$PD1_DMG" = "$CLEAR_DMG" ] \
   && printf '%s' "$PD1" | grep -q '^verdict=ok' \
   && printf '%s' "$PD2" | grep -q '^verdict=ok'; then
  ok "玩家技能伤害 1 人、2 人、两人清场都是 $PD1_DMG，只加经验不改伤害"
else
  bad "技能伤害被人数改动：$PD1 / $PD2 / clear=$CLEAR_DMG"
fi

echo "== 8. 改配置即生效，数值只有一个正本 =="
stop_pid "$CHARACTER_PID"
CHARACTER_PID=""
python3 - "$TMP/exp.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
d["base_exp"] = 200
d["party_bonus_pct"] = 25
json.dump(d, open(sys.argv[1], "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
: > "$TMP/character2.log"
"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >>"$TMP/character2.log" 2>&1 &
CHARACTER_PID=$!
CHAR2_READY=0
for _ in $(seq 1 100); do
  if grep -q '^ready name=Character' "$TMP/character2.log" 2>/dev/null; then
    CHAR2_READY=1
    break
  fi
  if ! kill -0 "$CHARACTER_PID" 2>/dev/null; then break; fi
  sleep 0.1
done
RECONFIG=""
for _ in $(seq 1 15); do
  RECONFIG="$(settle_exp "$TOKEN" "$ID_LEDGER" "p12d-reconfig" 2 "dungeon")"
  [ "${RECONFIG##*$'\n'}" = "200" ] && break
  sleep 0.4
done
split_response "$RECONFIG"
if [ "$CHAR2_READY" = "1" ] && [ "$CODE" = "200" ] \
   && grep -m1 '^exp_table_version=' "$TMP/character2.log" \
        | grep -q 'base_exp=200 party_bonus_pct=25' \
   && [ "$(json_field "$BODY" base_exp)" = "200" ] \
   && [ "$(json_field "$BODY" party_bonus)" = "50" ] \
   && [ "$(json_field "$BODY" exp_gain)" = "250" ]; then
  ok "只改 exp.json 重启角色服，这一笔就变成 200 + 50 = 250，代码里没有第二套数字"
else
  bad "改配置没生效：HTTP ${CODE} ${BODY}"
  tail -6 "$TMP/character2.log" | detail
fi

echo "== 9. 四条老规则不回退 =="
MAIN="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID_OLDRULES},\"dungeon_id\":\"main\",\"party_size\":1}")"
split_response "$MAIN"
ABYSS="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID_OLDRULES},\"dungeon_id\":\"abyss\",\"party_size\":4}")"
split_response "$ABYSS"
SPRING="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/hot-spring?token=${TOKEN}" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID_OLDRULES}}")"
split_response "$SPRING"
LAIR="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID_OLDRULES},\"dungeon_id\":\"lair\",\"party_size\":4}")"
split_response "$LAIR"
STATE_OLD="$(exp_state "$ID_OLDRULES")"
if [ "$(json_field "$(body_of "$MAIN")" fatigue_cost)" = "0" ] \
   && [ "$(json_field "$(body_of "$MAIN")" weekly_share_cost)" = "0" ] \
   && [ "$(json_field "$(body_of "$ABYSS")" fatigue_cost)" = "6000" ] \
   && [ "$(json_field "$(body_of "$SPRING")" recovered)" = "1500" ] \
   && [ "$(json_field "$(body_of "$LAIR")" weekly_share_cost)" = "4" ] \
   && [ "$(json_field "$(body_of "$LAIR")" weekly_share_remaining)" = "8" ] \
   && [ "$(field_of "$STATE_OLD" rows)" = "0" ]; then
  ok "主线不扣不占、深渊 4 人扣 6000、温泉补 1500、巢穴扣 4 剩 8；进本不自动发经验"
else
  bad "老规则被本行改动：main/abyss/spring/lair 见上，经验流水 ${STATE_OLD}"
fi

echo
echo "P1-2d 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" = "0" ]
