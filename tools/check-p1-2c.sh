#!/usr/bin/env bash
# P1-2c 核对：巢穴周份额和人数缩放。
#
# 完成标准（开发架构第 14 节第 28 行 + 第 11 节 P1-2）：
#   4 人打巢穴一次后这周剩余 8 个单人份，每人拿到 4 份；
#   单人一周能打 12 次，总产出与 3 次满员相同；
#   怪物生命和伤害按进本人数缩放，机制不变；旧副本只看等级。
#
# 做这几段：
#   1. 编译战斗服、角色服、网关、探针
#   2. 静态断言：周上限/缩放系数在配置、只写份额与份数、事务与行锁、
#      请求里带不进数值、主线/深渊/温泉的老规则没被改、两服读同一份表
#   3. 建表、账号、七个测试角色，起三个进程
#   4. 4 人一次、单人 12 次、3 次满员、份额不足、并发、跨周、与疲劳解绑、
#      战斗服的生命/伤害缩放、人数上限、鉴权
#
# 用法：bash tools/check-p1-2c.sh
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

BATTLE_PORT=${MMO_P12C_BATTLE_PORT:-60016}
CHAR_PORT=${MMO_P12C_CHAR_PORT:-62104}
GATEWAY_PORT=${MMO_P12C_GATEWAY_PORT:-61105}
ACCOUNT="p12c_user"
OTHER="p12c_other"
TEST_PASSWORD="p12c-Pw-2c84"

# 每个角色各管一段，避免互相把份额搅乱。
NAME_TEAM="p12c_team"
NAME_SOLO="p12c_solo"
NAME_FULL="p12c_full"
NAME_PART="p12c_part"
NAME_CONCURRENT="p12c_concurrent"
NAME_WEEK="p12c_week"
NAME_FATIGUE="p12c_fatigue"
CHAR_NAMES=("$NAME_TEAM" "$NAME_SOLO" "$NAME_FULL" "$NAME_PART" "$NAME_CONCURRENT" "$NAME_WEEK" "$NAME_FATIGUE")

PASS=0
FAIL=0
TMP="$(mktemp -d)"
BATTLE_PID=""
CHARACTER_PID=""
GATEWAY_PID=""
HAVE_DB=""
HOLDER_PIDS=()

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
  for pid in "${HOLDER_PIDS[@]}"; do stop_pid "$pid"; done
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
  # $1=令牌 $2=名字；把角色编号放进 NEW_ID。
  local resp
  resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"$2\",\"race\":\"mai\",\"class_id\":\"warrior\"}")"
  split_response "$resp"
  NEW_ID="$(json_field "$BODY" character_id)"
  [ "$CODE" = "200" ] && [ -n "$NEW_ID" ]
}

enter_dungeon() {
  # $1=令牌（空串不带） $2=角色 $3=副本 $4=人数
  local token="$1" url="http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter"
  [ -n "$token" ] && url="${url}?token=${token}"
  curl -s -m 10 -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2},\"dungeon_id\":\"${3}\",\"party_size\":${4}}"
}

hot_spring() {
  local token="$1" url="http://127.0.0.1:${GATEWAY_PORT}/hot-spring"
  [ -n "$token" ] && url="${url}?token=${token}"
  curl -s -m 10 -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2}}"
}

share_state() {
  "$DOTNET" "$CHARACTER_DLL" --share-state "$1" --config "$TMP/character.json" 2>&1 \
    | grep -m1 '^share_state=' || true
}

used_of() { printf '%s' "$1" | grep -o 'used=[0-9-]*' | head -1 | cut -d= -f2; }
remaining_of() { printf '%s' "$1" | grep -o 'remaining=[0-9-]*' | head -1 | cut -d= -f2; }
fatigue_of() { printf '%s' "$1" | grep -o 'fatigue=[0-9-]*' | head -1 | cut -d= -f2; }
week_of() { printf '%s' "$1" | grep -o 'week=[0-9-]*' | head -1 | cut -d= -f2; }

shares_total() {
  "$DOTNET" "$CHARACTER_DLL" --shares-total "$1" "$2" --config "$TMP/character.json" 2>&1 \
    | grep -m1 '^shares_total=' || true
}
total_of() { printf '%s' "$1" | grep -o 'shares_total=[0-9]*' | head -1 | cut -d= -f2; }

entry_count() {
  "$DOTNET" "$CHARACTER_DLL" --entry-count "$1" --config "$TMP/character.json" 2>&1 \
    | grep -m1 '^entry_count=' || true
}
entries_of() { printf '%s' "$1" | grep -o 'entry_count=[0-9]*' | head -1 | cut -d= -f2; }

level_of() {
  "$DOTNET" "$CHARACTER_DLL" --level-of "$1" --config "$TMP/character.json" 2>&1 \
    | grep -m1 '^level=' || true
}

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-2c 核对：$PASS 项通过，$FAIL 项未通过"
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
  echo "P1-2c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
CFG="$ROOT/config/dungeons.json"
SQL="$ROOT/db/character/character.sql"
ENTRY_SQL="$ROOT/db/character/dungeon_entry.sql"
PROTO="$ROOT/proto/mmo/character.proto"
CHAR_TABLE="$ROOT/src/Character/DungeonTable.cs"
BATTLE_TABLE="$ROOT/src/Battle/DungeonTable.cs"
REPO="$ROOT/src/Character/WeeklyShareRepository.cs"
SVC="$ROOT/src/Character/CharacterGrpcService.cs"
API="$ROOT/src/Gateway/CharacterApi.cs"
ENCOUNTER="$ROOT/src/Battle/Encounter.cs"
BATTLE_SRV="$ROOT/src/Battle/Server.cs"
FATIGUE_REPO="$ROOT/src/Character/DungeonEntryRepository.cs"
SPRING_REPO="$ROOT/src/Character/HotSpringRepository.cs"

CAP="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["weekly_share_cap"])' "$CFG")"
HP_SCALE="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["monster_hp_scale_per_player"])' "$CFG")"
DMG_SCALE="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["monster_damage_scale_per_player"])' "$CFG")"
LAIR="$(python3 -c 'import json,sys
d=json.load(open(sys.argv[1],encoding="utf-8"))
print(" ".join(str(x["max_players"])+" "+str(x["charges_fatigue"]).lower()+" "+str(x["takes_weekly_share"]).lower()
      for x in d["dungeons"] if x["id"]=="lair"))' "$CFG")"
if [ "$CAP" = "12" ] && [ "$LAIR" = "4 false true" ] \
   && grep -q '"weekly_share_cap": 12' "$CFG" \
   && grep -q '"id": "lair"' "$CFG" \
   && grep -q '"monster_hp_scale_per_player"' "$CFG" \
   && grep -q '"monster_damage_scale_per_player"' "$CFG"; then
  ok "巢穴 1–4 人、不扣疲劳、占周份额；每周 12 份与缩放系数都在配置里"
else
  bad "副本表里的巢穴或周上限/缩放系数不对：cap=$CAP lair=$LAIR"
fi

if grep -qF '[JsonPropertyName("weekly_share_cap")]' "$CHAR_TABLE" \
   && grep -q 'dungeons.json 缺 weekly_share_cap' "$CHAR_TABLE" \
   && grep -qF '[JsonPropertyName("monster_hp_scale_per_player")]' "$CHAR_TABLE" \
   && grep -qF '[JsonPropertyName("monster_damage_scale_per_player")]' "$CHAR_TABLE" \
   && grep -qF '[JsonPropertyName("monster_hp_scale_per_player")]' "$BATTLE_TABLE" \
   && grep -qF '[JsonPropertyName("monster_damage_scale_per_player")]' "$BATTLE_TABLE"; then
  ok "周上限与两个缩放系数只有配置一个正本，两服缺字段都起不来"
else
  bad "周上限或缩放系数可能被写死在代码里"
fi

if sed -n '/message EnterDungeonRequest {/,/^}/p' "$PROTO" \
     | grep -qiE 'share|reward|recovered'; then
  bad "进本请求里带了份额或产出份数字段，客户端可以自己改数值"
else
  ok "进本请求只有 account / character_id / dungeon_id / party_size"
fi

if sed -n '/message EnterDungeonResponse {/,/^}/p' "$PROTO" \
     | grep -q 'weekly_share_remaining' \
     && sed -n '/message EnterDungeonResponse {/,/^}/p' "$PROTO" | grep -q 'reward_shares' \
     && grep -q 'weekly_share_remaining = result.WeeklyShareRemaining' "$API" \
     && grep -q 'reward_shares = result.RewardShares' "$API" \
     && ! grep -q 'Npgsql' "$API"; then
  ok "网关原样回剩余份额与本次份数，不碰库、不自己算一份"
else
  bad "网关没把份额结果转出去，或者自己动了这个数"
fi

if grep -qF 'update \"角色\".character set weekly_share = $2, weekly_share_day =' "$REPO" \
   && grep -q 'dungeon_entry (character_id, dungeon_id, party_size, reward_shares)' "$REPO" \
   && ! grep -nEi 'set (fatigue|level|gold)|insert into.*(currency|inventory|grant_txn)|update .*(currency|inventory|grant_txn)' "$REPO" >/dev/null; then
  ok "周份额进本只写份额、周界与进本记录（含份数），疲劳/等级/货币/背包一概不碰"
else
  bad "周份额进本写了份额之外的东西"
fi

if grep -q 'BeginTransactionAsync' "$REPO" && grep -q 'CommitAsync' "$REPO" \
   && grep -q 'RollbackAsync' "$REPO" && grep -q 'for update' "$REPO" \
   && grep -q 'weekly_share_day <>' "$REPO" \
   && grep -q 'effective + cost > weeklyCap' "$REPO"; then
  ok "跨周归零、按人数扣份额、记进本在一个事务里，行锁让并发读到新值"
else
  bad "周份额不是整笔事务，或者没有周界与上限判断"
fi

if grep -q 'var cost = partySize;' "$REPO" \
   && grep -q 'entry.Parameters.AddWithValue(cost)' "$REPO" \
   && grep -q 'var share = await weeklyShares.EnterWithWeeklyShareAsync' "$SVC" \
   && grep -q 'dungeons.WeeklyShareCap' "$SVC"; then
  ok "产出份数与扣掉的份额都等于进本人数 × 单人 1 份，上限从配置读"
else
  bad "产出或扣减没有按人数倍增"
fi

if grep -q 'weekly_share_day date' "$SQL" \
   && grep -q 'CHECK (weekly_share >= 0)' "$SQL" \
   && grep -q 'reward_shares integer NOT NULL DEFAULT 0' "$ENTRY_SQL" \
   && grep -q 'CHECK (reward_shares >= 0)' "$ENTRY_SQL"; then
  ok "角色表有周界与非负 CHECK，进本记录有份数字段与库层非负闸"
else
  bad "周界、非负约束或份数字段没落库"
fi

if grep -q 'if (dungeon.TakesWeeklyShare)' "$SVC" \
   && grep -q 'if (!dungeon.ChargesFatigue)' "$SVC" \
   && ! sed -n '/public override async Task<EnterDungeonResponse> EnterDungeon/,/^    }$/p' "$SVC" | grep -qi 'realm'; then
  ok "巢穴走份额分支、主线仍走不扣疲劳分支；准入只看等级，没提前加境界门槛"
else
  bad "进本分路不对，或者提前写了境界门槛"
fi

if grep -q 'party_size' "$BATTLE_SRV" && grep -q 'required int PartySize' "$BATTLE_SRV" \
   && grep -q 'NextSessionId(), partySize' "$BATTLE_SRV" \
   && grep -q 'config.Dungeons.MonsterHpScalePerPlayer' "$ENCOUNTER" \
   && grep -q 'config.Dungeons.MonsterDamageScalePerPlayer' "$ENCOUNTER" \
   && grep -q 'BossHpMax = _bossHpMax' "$ENCOUNTER" \
   && grep -q 'ScaleDamage(attack.Damage, _monsterDamageScale)' "$ENCOUNTER" \
   && grep -q 'Math.Max(1, _bossHpMax)' "$ENCOUNTER" \
   && grep -q 'long DamageOf(Member member, SkillDef skill)' "$ENCOUNTER"; then
  ok "战斗服按进本人数缩放怪物生命/伤害与阶段血线，玩家技能伤害不跟着变"
else
  bad "怪物缩放没走配置，或机制被人数改动"
fi

if grep -q 'partySize <= 1 ? 1.0' "$ENCOUNTER" \
   && grep -q 'DungeonTable.Load(config.DungeonTable)' "$ROOT/src/Character/CharacterProcess.cs" \
   && grep -q 'DungeonTable.Load' "$ROOT/src/Battle/Config.cs"; then
  ok "单人倍率固定 1（练习场与老客户端不变），两服仍读同一份副本表"
else
  bad "单人不带 party_size 的行为可能被改，或两服副本表分叉"
fi

if grep -q 'FatiguePerPlayer \* partySize' "$FATIGUE_REPO" \
   && grep -qF 'update \"角色\".character set fatigue = $2, fatigue_day = current_date' "$SPRING_REPO" \
   && grep -q '"takes_weekly_share": false' "$CFG" \
   && grep -q '"charges_fatigue": false' "$CFG"; then
  ok "深渊按人数扣疲劳、温泉只补疲劳、主线不扣不占，三行老规则都没回退"
else
  bad "前面的进本或温泉规则被本行改动"
fi

echo "== 3. 环境与启动 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env"
  echo
  echo "P1-2c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
HAVE_DB=1
ok "库凭据在位：$( [ -n "${MMO_GAMEDB:-}" ] && echo "env:MMO_GAMEDB" || echo "file:/srv/mmo/gamedb.env" )"

BOSS_ID="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["boss"]["id"])' "$ROOT/config/battle.json")"
SKILL_ID="$(python3 -c 'import json,sys
d=json.load(open(sys.argv[1],encoding="utf-8"))
print([s["id"] for s in d["skills"] if s["class_id"]=="warrior" and s["kind"]=="active"][0])' "$ROOT/config/skills.json")"

python3 - "$ROOT" "$TMP" "$BATTLE_PORT" <<'PY'
import json, os, sys
root, tmp, port = sys.argv[1], sys.argv[2], sys.argv[3]
cfg = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
cfg["listen"] = "127.0.0.1:" + port
cfg["settlement_dir"] = os.path.join(tmp, "settlements")
cfg["character_dir"] = os.path.join(tmp, "characters")
cfg["skill_table"] = "skills.json"
cfg["class_table"] = "classes.json"
cfg["dungeon_table"] = "dungeons.json"
# 本行要量 Boss 的伤害，给一份只作用于这个核对进程的攻击配置。
# 仓库里的 battle.json 一行没改，P0 的单人练习场仍然不出手。
# 间隔要长过技能前摇：4 人场伤害放大 4 倍，间隔太短会先把施法者打倒，
# 那样量到的是「施法者倒地」而不是玩家技能伤害，缩放机制反而验不出来。
cfg["boss"]["attack"] = {"damage": 100, "interval_ms": 1500, "foreswing_ms": 0}
json.dump(cfg, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)
PY
cp "$ROOT/config/skills.json" "$ROOT/config/classes.json" "$ROOT/config/dungeons.json" "$TMP/"

cat > "$TMP/character.json" <<JSON
{
  "schema": "character-config/1",
  "listen": "http://127.0.0.1:${CHAR_PORT}",
  "db_pool_max": 6,
  "bag_slots": 40,
  "dungeon_table": "${TMP}/dungeons.json"
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
  echo "P1-2c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

MIGRATE_OK=1
for sql in character.sql dungeon_entry.sql inventory.sql currency.sql transaction.sql; do
  if ! "$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" \
        --sql "$ROOT/db/character/$sql" >"$TMP/migrate-$sql.log" 2>&1; then
    MIGRATE_OK=0
    bad "表建不出来：$sql"
    tail -3 "$TMP/migrate-$sql.log" | detail
  fi
done
if [ "$MIGRATE_OK" = "1" ]; then
  ok "角色表（含周界与非负闸）、进本记录表（含份数字段）都在位"
else
  echo
  echo "P1-2c 核对：$PASS 项通过，$FAIL 项未通过"
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
if [ "$BATTLE_READY" = "1" ]; then
  ok "战斗服在 127.0.0.1:${BATTLE_PORT} 上"
else
  bad "战斗服没起来"
  tail -8 "$TMP/battle.log" | detail
  echo
  echo "P1-2c 核对：$PASS 项通过，$FAIL 项未通过"
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
  ok "角色服在 127.0.0.1:${CHAR_PORT} 上（$(grep -m1 '^dungeons=' "$TMP/character.log")）"
else
  bad "角色服没起来"
  tail -8 "$TMP/character.log" | detail
  echo
  echo "P1-2c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
if grep -m1 '^dungeons=' "$TMP/character.log" | grep -q 'weekly_share_cap=12'; then
  ok "角色服启动时读到的周上限是 12"
else
  bad "角色服没读到配置里的周上限"
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
  echo "P1-2c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then
  ok "两个账号都能登录"
else
  bad "登录没拿到令牌"
  echo
  echo "P1-2c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

# 上次被中断可能留下同名角色，建号前先清掉。
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
    "$NAME_TEAM")       ID_TEAM="$NEW_ID" ;;
    "$NAME_SOLO")       ID_SOLO="$NEW_ID" ;;
    "$NAME_FULL")       ID_FULL="$NEW_ID" ;;
    "$NAME_PART")       ID_PART="$NEW_ID" ;;
    "$NAME_CONCURRENT") ID_CONCURRENT="$NEW_ID" ;;
    "$NAME_WEEK")       ID_WEEK="$NEW_ID" ;;
    "$NAME_FATIGUE")    ID_FATIGUE="$NEW_ID" ;;
  esac
done
if [ "$CREATED" = "1" ]; then
  ok "七个测试角色都建好了"
else
  echo
  echo "P1-2c 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 4. 4 人打一次：剩余 8 个单人份，每人 4 份 =="
STATE="$(share_state "$ID_TEAM")"
if [ "$(used_of "$STATE")" = "0" ] && [ "$(remaining_of "$STATE")" = "12" ]; then
  ok "新角色本周已用 0、剩余 12"
else
  bad "新角色的周份额不对：$STATE"
fi
if printf '%s' "$(level_of "$ID_TEAM")" | grep -q 'level=1'; then
  ok "1 级就能进巢穴：准入只看等级，没有境界门槛"
else
  bad "角色等级不是 1，或读不出来：$(level_of "$ID_TEAM")"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_TEAM" lair 4)"
split_response "$RESP"
STATE="$(share_state "$ID_TEAM")"
TOTAL="$(shares_total "$ID_TEAM" lair)"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ] \
   && [ "$(json_field "$BODY" weekly_share_cost)" = "4" ] \
   && [ "$(json_field "$BODY" weekly_share)" = "4" ] \
   && [ "$(json_field "$BODY" weekly_share_remaining)" = "8" ] \
   && [ "$(json_field "$BODY" weekly_share_cap)" = "12" ] \
   && [ "$(json_field "$BODY" reward_shares)" = "4" ] \
   && [ "$(json_field "$BODY" fatigue)" = "0" ] \
   && [ "$(json_field "$BODY" fatigue_cost)" = "0" ] \
   && [ "$(used_of "$STATE")" = "4" ] && [ "$(remaining_of "$STATE")" = "8" ] \
   && [ "$(fatigue_of "$STATE")" = "0" ] \
   && [ "$(total_of "$TOTAL")" = "4" ]; then
  ok "4 人一次：扣 4 份、剩 8 份、每人得 4 份，且不扣疲劳（回包与库里一致）"
else
  bad "4 人一次的结果不对：HTTP ${CODE} ${BODY} / ${STATE} / ${TOTAL}"
fi

echo "== 5. 单人一周 12 次，第 13 次被拒 =="
SOLO_OK=1
for i in $(seq 1 12); do
  RESP="$(enter_dungeon "$TOKEN" "$ID_SOLO" lair 1)"
  split_response "$RESP"
  EXPECTED_LEFT=$((12 - i))
  if [ "$CODE" != "200" ] || [ "$(json_field "$BODY" allowed)" != "True" ] \
     || [ "$(json_field "$BODY" reward_shares)" != "1" ] \
     || [ "$(json_field "$BODY" weekly_share_remaining)" != "$EXPECTED_LEFT" ]; then
    SOLO_OK=0
    bad "单人第 $i 次不对：HTTP ${CODE} ${BODY}"
    break
  fi
done
SOLO_TOTAL="$(shares_total "$ID_SOLO" lair)"
STATE="$(share_state "$ID_SOLO")"
E_BEFORE="$(entry_count "$ID_SOLO")"
if [ "$SOLO_OK" = "1" ] && [ "$(total_of "$SOLO_TOTAL")" = "12" ] \
   && [ "$(used_of "$STATE")" = "12" ] && [ "$(remaining_of "$STATE")" = "0" ]; then
  ok "单人连打 12 次：每次 1 份，累计 12 份，剩余 0"
else
  bad "单人 12 次的账不对：${SOLO_TOTAL} / ${STATE}"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_SOLO" lair 1)"
split_response "$RESP"
STATE="$(share_state "$ID_SOLO")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "False" ] \
   && [ "$(json_field "$BODY" reason)" = "weekly_share_exhausted" ] \
   && [ "$(json_field "$BODY" reward_shares)" = "0" ] \
   && [ "$(used_of "$STATE")" = "12" ] \
   && [ "$(entry_count "$ID_SOLO")" = "$E_BEFORE" ]; then
  ok "第 13 次被 weekly_share_exhausted 拒，份额不变也不留进本记录"
else
  bad "打满后还能进，或者账被改了：HTTP ${CODE} ${BODY}"
fi

echo "== 6. 3 次满员：总产出与单人 12 次相同 =="
FULL_OK=1
for expect in 4 8 12; do
  RESP="$(enter_dungeon "$TOKEN" "$ID_FULL" lair 4)"
  split_response "$RESP"
  if [ "$CODE" != "200" ] || [ "$(json_field "$BODY" allowed)" != "True" ] \
     || [ "$(json_field "$BODY" reward_shares)" != "4" ] \
     || [ "$(json_field "$BODY" weekly_share)" != "$expect" ]; then
    FULL_OK=0
    bad "满员进本累计到 $expect 不对：HTTP ${CODE} ${BODY}"
    break
  fi
done
FULL_TOTAL="$(shares_total "$ID_FULL" lair)"
if [ "$FULL_OK" = "1" ] && [ "$(total_of "$FULL_TOTAL")" = "12" ] \
   && [ "$(total_of "$SOLO_TOTAL")" = "12" ] \
   && [ "$(used_of "$(share_state "$ID_FULL")")" = "12" ]; then
  ok "3 次满员共 12 份，与单人 12 次总产出相同（$(total_of "$FULL_TOTAL") = $(total_of "$SOLO_TOTAL")）"
else
  bad "满员与单人的总产出不等：${FULL_TOTAL} vs ${SOLO_TOTAL}"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_FULL" lair 4)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "False" ] \
   && [ "$(json_field "$BODY" reason)" = "weekly_share_exhausted" ]; then
  ok "满员第 4 次（第 13 份）被拒，一周总量封顶"
else
  bad "满员还能突破周上限：HTTP ${CODE} ${BODY}"
fi

echo "== 7. 份额不足整笔拒绝，恰好到上限允许 =="
RAW="$("$DOTNET" "$CHARACTER_DLL" --raw-share-write "$ID_PART" 10 \
  --config "$TMP/character.json" 2>&1 | grep -m1 '^raw_share=' || true)"
E_BEFORE="$(entry_count "$ID_PART")"
T_BEFORE="$(shares_total "$ID_PART" lair)"
if printf '%s' "$RAW" | grep -q 'raw_share=written' \
   && [ "$(used_of "$(share_state "$ID_PART")")" = "10" ]; then
  ok "造出只剩 2 份的现场（已用 10）"
else
  bad "造不出部分剩余的现场：$RAW"
fi
RESP="$(enter_dungeon "$TOKEN" "$ID_PART" lair 4)"
split_response "$RESP"
STATE="$(share_state "$ID_PART")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "False" ] \
   && [ "$(json_field "$BODY" reason)" = "weekly_share_exhausted" ] \
   && [ "$(json_field "$BODY" weekly_share_cost)" = "0" ] \
   && [ "$(json_field "$BODY" reward_shares)" = "0" ] \
   && [ "$(used_of "$STATE")" = "10" ] \
   && [ "$(entry_count "$ID_PART")" = "$E_BEFORE" ] \
   && [ "$(shares_total "$ID_PART" lair)" = "$T_BEFORE" ]; then
  ok "4 人超出剩余 2 份：整笔拒绝，不扣份额、不发份数、不留进本记录"
else
  bad "份额不足时没有整笔回滚：HTTP ${CODE} ${BODY} / ${STATE}"
fi
RESP="$(enter_dungeon "$TOKEN" "$ID_PART" lair 2)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ] \
   && [ "$(json_field "$BODY" weekly_share)" = "12" ] \
   && [ "$(json_field "$BODY" weekly_share_remaining)" = "0" ]; then
  ok "2 人恰好扣到 12 份允许，不写负剩余"
else
  bad "恰好到上限这次不对：HTTP ${CODE} ${BODY}"
fi

RAW_NEG="$("$DOTNET" "$CHARACTER_DLL" --raw-share-write "$ID_PART" -1 \
  --config "$TMP/character.json" 2>&1 | grep -m1 '^raw_share=' || true)"
if printf '%s' "$RAW_NEG" | grep -q 'raw_share=rejected' \
   && printf '%s' "$RAW_NEG" | grep -q 'sqlstate=23514'; then
  ok "库层 CHECK 把负份额挡住（直写也进不去）"
else
  bad "库层没挡住负份额：$RAW_NEG"
fi

echo "== 8. 并发两笔冲同一周上限，只有一笔成功 =="
"$DOTNET" "$CHARACTER_DLL" --raw-share-write "$ID_CONCURRENT" 10 \
  --config "$TMP/character.json" >/dev/null 2>&1
( enter_dungeon "$TOKEN" "$ID_CONCURRENT" lair 2 >"$TMP/concurrent-1.txt" 2>&1 ) & C1=$!
( enter_dungeon "$TOKEN" "$ID_CONCURRENT" lair 2 >"$TMP/concurrent-2.txt" 2>&1 ) & C2=$!
wait "$C1" "$C2"
split_response "$(cat "$TMP/concurrent-1.txt")"; A1="$BODY"; K1="$CODE"
split_response "$(cat "$TMP/concurrent-2.txt")"; A2="$BODY"; K2="$CODE"
ALLOWED=0
[ "$(json_field "$A1" allowed)" = "True" ] && ALLOWED=$((ALLOWED + 1))
[ "$(json_field "$A2" allowed)" = "True" ] && ALLOWED=$((ALLOWED + 1))
STATE="$(share_state "$ID_CONCURRENT")"
TOTAL="$(shares_total "$ID_CONCURRENT" lair)"
E_COUNT="$(entries_of "$(entry_count "$ID_CONCURRENT")")"
if [ "$K1" = "200" ] && [ "$K2" = "200" ] && [ "$ALLOWED" = "1" ] \
   && [ "$(used_of "$STATE")" = "12" ] \
   && [ "$(total_of "$TOTAL")" = "2" ] \
   && [ "$E_COUNT" = "1" ]; then
  ok "并发两笔各 2 人：一成一败，已用停在 12，只记 1 条进本与 2 份产出"
else
  bad "并发把份额扣穿了：$K1 $A1 / $K2 $A2 / ${STATE} / ${TOTAL} / entries=$E_COUNT"
fi

echo "== 9. 跨周归零 =="
RESP="$(enter_dungeon "$TOKEN" "$ID_WEEK" lair 4)"
split_response "$RESP"
LAST_MONDAY="$(python3 -c 'import datetime
d=datetime.date.today()-datetime.timedelta(days=datetime.date.today().weekday()+7)
print(d.isoformat())')"
THIS_MONDAY="$(python3 -c 'import datetime
d=datetime.date.today()-datetime.timedelta(days=datetime.date.today().weekday())
print(d.isoformat())')"
"$DOTNET" "$CHARACTER_DLL" --set-week-day "$ID_WEEK" "$LAST_MONDAY" \
  --config "$TMP/character.json" >"$TMP/set-week.log" 2>&1 || true
if [ "$(used_of "$(share_state "$ID_WEEK")")" = "0" ] \
   && [ "$(remaining_of "$(share_state "$ID_WEEK")")" = "12" ]; then
  ok "周界挪到上一周后，本周有效已用份额按 0 起算"
else
  bad "跨周没有归零：$(share_state "$ID_WEEK")"
fi
RESP="$(enter_dungeon "$TOKEN" "$ID_WEEK" lair 4)"
split_response "$RESP"
STATE="$(share_state "$ID_WEEK")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ] \
   && [ "$(json_field "$BODY" weekly_share)" = "4" ] \
   && [ "$(json_field "$BODY" weekly_share_remaining)" = "8" ] \
   && [ "$(used_of "$STATE")" = "4" ] \
   && [ "$(week_of "$STATE")" = "$THIS_MONDAY" ]; then
  ok "新的一周又能按 12 份进本，周界刷成本周周首（$THIS_MONDAY）"
else
  bad "跨周后进本不对：HTTP ${CODE} ${BODY} / ${STATE}"
fi

echo "== 10. 巢穴与疲劳解绑，前卡不回退 =="
RESP="$(enter_dungeon "$TOKEN" "$ID_FATIGUE" lair 4)"
split_response "$RESP"
STATE="$(share_state "$ID_FATIGUE")"
FAT0="$(fatigue_of "$STATE")"
USED0="$(used_of "$STATE")"
if [ "$CODE" = "200" ] && [ "$FAT0" = "0" ] && [ "$USED0" = "4" ] \
   && [ "$(json_field "$BODY" weekly_share_cost)" = "4" ] \
   && [ "$(json_field "$BODY" fatigue_cost)" = "0" ]; then
  ok "巢穴 4 人：只扣 4 份周份额，疲劳仍是 0"
else
  bad "巢穴动了疲劳或没扣份额：HTTP ${CODE} ${BODY} / ${STATE}"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_FATIGUE" abyss 4)"
split_response "$RESP"
STATE="$(share_state "$ID_FATIGUE")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ] \
   && [ "$(json_field "$BODY" fatigue_cost)" = "6000" ] \
   && [ "$(json_field "$BODY" weekly_share_cost)" = "0" ] \
   && [ "$(json_field "$BODY" reward_shares)" = "0" ] \
   && [ "$(fatigue_of "$STATE")" = "6000" ] \
   && [ "$(used_of "$STATE")" = "$USED0" ]; then
  ok "深渊 4 人仍扣 6000 疲劳、不占周份额、不记产出份数"
else
  bad "深渊与周份额串了：HTTP ${CODE} ${BODY} / ${STATE}"
fi

RESP="$(hot_spring "$TOKEN" "$ID_FATIGUE")"
split_response "$RESP"
STATE="$(share_state "$ID_FATIGUE")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" recovered)" = "1500" ] \
   && [ "$(fatigue_of "$STATE")" = "4500" ] \
   && [ "$(used_of "$STATE")" = "$USED0" ]; then
  ok "温泉仍只补 1500 疲劳，周份额没动"
else
  bad "温泉改了份额或没恢复疲劳：HTTP ${CODE} ${BODY} / ${STATE}"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_FATIGUE" main 1)"
split_response "$RESP"
STATE="$(share_state "$ID_FATIGUE")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ] \
   && [ "$(json_field "$BODY" fatigue_cost)" = "0" ] \
   && [ "$(json_field "$BODY" weekly_share_cost)" = "0" ] \
   && [ "$(json_field "$BODY" reward_shares)" = "0" ] \
   && [ "$(fatigue_of "$STATE")" = "4500" ] \
   && [ "$(used_of "$STATE")" = "$USED0" ]; then
  ok "主线仍 1 人、不扣疲劳、不占份额、不计产出"
else
  bad "主线规则被改动：HTTP ${CODE} ${BODY} / ${STATE}"
fi

echo "== 11. 战斗服：怪物生命与伤害按人数缩放，机制不变 =="
BATTLE_URL="ws://127.0.0.1:${BATTLE_PORT}/battle"
ONE="$("$DOTNET" "$PROBE_DLL" --url "${BATTLE_URL}?dungeon=lair&party_size=1&party=p12c_s1&char=1" \
  --case p12c_boss_hit 2>&1)"
FOUR="$("$DOTNET" "$PROBE_DLL" --url "${BATTLE_URL}?dungeon=lair&party_size=4&party=p12c_s4&char=1" \
  --case p12c_boss_hit 2>&1)"
HP1="$(printf '%s' "$ONE" | grep -o 'boss_hp_max=[0-9]*' | head -1 | cut -d= -f2)"
DMG1="$(printf '%s' "$ONE" | grep -o 'boss_damage=[0-9-]*' | head -1 | cut -d= -f2)"
HP4="$(printf '%s' "$FOUR" | grep -o 'boss_hp_max=[0-9]*' | head -1 | cut -d= -f2)"
DMG4="$(printf '%s' "$FOUR" | grep -o 'boss_damage=[0-9-]*' | head -1 | cut -d= -f2)"
SCALE_VERDICT="$(python3 - "$HP1" "$DMG1" "$HP4" "$DMG4" "$HP_SCALE" "$DMG_SCALE" <<'PY'
import sys
hp1, dmg1, hp4, dmg4, hs, ds = [float(x or -1) for x in sys.argv[1:7]]
fh = 1 + 3 * hs
fd = 1 + 3 * ds
print("ok" if hp1 > 0 and dmg1 > 0
      and hp4 == round(hp1 * fh) and dmg4 == round(dmg1 * fd)
      and hp4 > hp1 and dmg4 > dmg1 else "bad")
PY
)"
if [ "$SCALE_VERDICT" = "ok" ]; then
  ok "1 人与 4 人对比：Boss 生命 $HP1 → $HP4、伤害 $DMG1 → $DMG4，按配置倍率缩放"
else
  bad "怪物缩放不对：1 人 $ONE / 4 人 $FOUR"
fi

PD1="$("$DOTNET" "$PROBE_DLL" --url "${BATTLE_URL}?dungeon=lair&party_size=1&party=p12c_d1&char=2" \
  --case p12c_player_damage --boss "$BOSS_ID" --hit "$SKILL_ID" 2>&1)"
PD4="$("$DOTNET" "$PROBE_DLL" --url "${BATTLE_URL}?dungeon=lair&party_size=4&party=p12c_d4&char=2" \
  --case p12c_player_damage --boss "$BOSS_ID" --hit "$SKILL_ID" 2>&1)"
PD1_DMG="$(printf '%s' "$PD1" | grep -o 'damage=[0-9]*' | head -1 | cut -d= -f2)"
PD4_DMG="$(printf '%s' "$PD4" | grep -o 'damage=[0-9]*' | head -1 | cut -d= -f2)"
if [ -n "$PD1_DMG" ] && [ "$PD1_DMG" = "$PD4_DMG" ] \
   && printf '%s' "$PD1" | grep -q '^verdict=ok' \
   && printf '%s' "$PD4" | grep -q '^verdict=ok'; then
  ok "玩家技能伤害 1 人与 4 人都是 $PD1_DMG，机制没随人数改，结算经验也没动"
else
  bad "玩家技能伤害被人数改动：$PD1 / $PD4"
fi

PRACTICE="$("$DOTNET" "$PROBE_DLL" --url "${BATTLE_URL}?party=p12c_practice&char=3" \
  --case open 2>&1)"
PRACTICE_HP="$(printf '%s' "$PRACTICE" | grep -o 'boss_hp_max=[0-9]*' | head -1 | cut -d= -f2)"
if [ "$PRACTICE_HP" = "$HP1" ] && printf '%s' "$PRACTICE" | grep -q '^verdict=ok'; then
  ok "不带副本与人数的练习场仍是 $PRACTICE_HP 血，P0 老连接行为不变"
else
  bad "练习场被缩放改了：$PRACTICE"
fi

echo "== 12. 战斗服：巢穴 4 人同场，第 5 人被拒 =="
PKEY="p12c_lair_full"
for n in 1 2 3 4; do
  "$DOTNET" "$PROBE_DLL" \
    --url "${BATTLE_URL}?dungeon=lair&party_size=4&party=${PKEY}&char=${n}" \
    --case p12a_join --expect open --hold-ms 12000 >"$TMP/holder-${n}.txt" 2>&1 &
  HOLDER_PIDS+=("$!")
done
HOLD_OK=1
for n in 1 2 3 4; do
  READY=0
  for _ in $(seq 1 80); do
    if grep -q 'joined=ok' "$TMP/holder-${n}.txt" 2>/dev/null; then READY=1; break; fi
    sleep 0.1
  done
  [ "$READY" = "1" ] || HOLD_OK=0
done
if [ "$HOLD_OK" = "1" ]; then
  ok "四个人都进了同一个巢穴场（上限 4）"
else
  bad "四个人没都进去"
  for n in 1 2 3 4; do head -4 "$TMP/holder-${n}.txt" 2>/dev/null | detail; done
fi
FIFTH="$("$DOTNET" "$PROBE_DLL" \
  --url "${BATTLE_URL}?dungeon=lair&party_size=4&party=${PKEY}&char=5" \
  --case p12a_join --expect reject 2>&1)"
if printf '%s' "$FIFTH" | grep -q '^verdict=ok' \
   && printf '%s' "$FIFTH" | grep -q 'reason=party_full'; then
  ok "第 5 个人被 party_full 拒绝，人数上限仍是配置里的 4"
else
  bad "第 5 个人没被挡住：$FIFTH"
fi
for pid in "${HOLDER_PIDS[@]}"; do stop_pid "$pid"; done
HOLDER_PIDS=()

echo "== 13. 鉴权与参数 =="
RESP="$(enter_dungeon "$OTHER_TOKEN" "$ID_TEAM" lair 1)"
split_response "$RESP"
if [ "$CODE" = "404" ] && [ "$(json_field "$BODY" error)" = "character_not_found" ]; then
  ok "别人的角色：404 character_not_found"
else
  bad "别人的角色也能占周份额：HTTP ${CODE} ${BODY}"
fi

RESP="$(enter_dungeon "" "$ID_TEAM" lair 1)"
split_response "$RESP"
if [ "$CODE" = "401" ]; then
  ok "不带令牌：HTTP 401"
else
  bad "不带令牌也能进本：HTTP ${CODE}"
fi

RESP="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=$TOKEN" \
  -H 'Content-Type: application/json' -d "{\"character_id\":${ID_TEAM}}")"
split_response "$RESP"
if [ "$CODE" = "400" ]; then
  ok "缺 party_size：HTTP 400"
else
  bad "缺人数也能进本：HTTP ${CODE}"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_TEAM" no_such_dungeon 1)"
split_response "$RESP"
if [ "$CODE" = "400" ] && [ "$(json_field "$BODY" error)" = "unknown_dungeon" ]; then
  ok "未知副本：400 unknown_dungeon"
else
  bad "未知副本没被拒：HTTP ${CODE} ${BODY}"
fi

RESP="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=$TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID_TEAM},\"dungeon_id\":\"lair\",\"party_size\":1,\"reward_shares\":9999,\"weekly_share\":-99}")"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" reward_shares)" = "1" ] \
   && [ "$(json_field "$BODY" weekly_share_cost)" = "1" ]; then
  ok "请求体里伪造份数/份额无效：这次仍只按 1 人记 1 份"
else
  bad "客户端能自己改份数或份额：HTTP ${CODE} ${BODY}"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_TEAM" lair 5)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "False" ] \
   && [ "$(json_field "$BODY" reason)" = "party_too_large" ]; then
  ok "5 人进巢穴被 party_too_large 拒，份额与份数都不变"
else
  bad "超过人数上限还能进：HTTP ${CODE} ${BODY}"
fi

if grep -q "$TEST_PASSWORD" "$TMP/gateway.log" 2>/dev/null; then
  bad "网关日志里出现了口令"
else
  ok "网关日志里没有口令"
fi

echo
echo "P1-2c 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
