#!/usr/bin/env bash
# P1-1a2 核对：网关只认令牌。
#
# 完成标准（开发架构第 14 节第 19 行）：没有令牌或令牌过期的连接不能再发技能。
#
# 做这几段：
#   1. 编译网关与探针
#   2. 静态断言：令牌从 query 与 Bearer 取、握手失败回 401、每转发一帧都再判过期、
#      过期按策略违规关连接、上游地址来自配置、网关不解析技能消息
#   3. 起战斗服与网关（临时配置：令牌 4 秒过期，上游指到测试战斗服）
#   4. 没令牌、伪造令牌：握手就被拒，连接建不起来
#   5. 有效令牌：从网关连进去出一手，能收到战斗服回包（转发链路通）
#   6. 连上之后令牌到期：同一连接再发技能，收不到任何回包
#   7. 已过期令牌重新连：握手被拒
#
# 用法：bash tools/check-p1-1a2.sh
# 连库段要在有 gamedb 的机器上跑（笔记本）；缺凭据时明确失败，不静默跳过。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
PROBE_DLL="$ROOT/tools/BattleProbe/bin/Release/net10.0/BattleProbe.dll"

BATTLE_PORT=${MMO_P11A2_BATTLE_PORT:-60011}
GATEWAY_PORT=${MMO_P11A2_GATEWAY_PORT:-61091}
TOKEN_TTL=4
ACCOUNT="p11a2_user"
TEST_PASSWORD="p11a2-Pw-91c4"
BOSS=9001
SKILL=1002

PASS=0
FAIL=0
TMP="$(mktemp -d)"
BATTLE_PID=""
GATEWAY_PID=""
HAVE_DB=""

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
detail() { sed 's/^/       /'; }

get_token() {
  curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/login" \
    -H 'Content-Type: application/json' \
    -d "{\"account\":\"${ACCOUNT}\",\"password\":\"${TEST_PASSWORD}\"}" \
    | python3 -c 'import json,sys
try:
    print(json.load(sys.stdin).get("token", ""))
except Exception:
    print("")'
}

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
  stop_pid "$GATEWAY_PID"
  stop_pid "$BATTLE_PID"
  if [ -n "$HAVE_DB" ]; then
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" \
      --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-1a2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Gateway/Gateway.csproj" -c Release --nologo -v q >"$TMP/build-gw.log" 2>&1 \
  && "$DOTNET" build "$ROOT/src/Battle/Battle.csproj" -c Release --nologo -v q >"$TMP/build-battle.log" 2>&1 \
  && "$DOTNET" build "$ROOT/tools/BattleProbe/BattleProbe.csproj" -c Release --nologo -v q >"$TMP/build-probe.log" 2>&1; then
  ok "网关、战斗服与探针编译通过"
else
  bad "编译失败"
  grep -E "error " "$TMP/build-gw.log" "$TMP/build-battle.log" "$TMP/build-probe.log" | head -5 | detail
  echo
  echo "P1-1a2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
PROXY="$ROOT/src/Gateway/BattleProxy.cs"

if grep -q 'Query\["token"\]' "$PROXY" && grep -q 'Bearer ' "$PROXY"; then
  ok "令牌从 query 取，也认 Authorization: Bearer"
else
  bad "令牌来源不全"
fi

if grep -q 'StatusCode = 401' "$PROXY"; then
  ok "认不过的令牌在握手就回 401"
else
  bad "握手没拒，没令牌也能建连接"
fi

if grep -q 'DateTimeOffset.UtcNow >= expiresAt' "$PROXY"; then
  ok "每转发一帧都重新判过期，不靠握手那一次"
else
  bad "只在握手时判过期，连上之后能一直发"
fi

if grep -q 'WebSocketCloseStatus.PolicyViolation' "$PROXY"; then
  ok "过期按策略违规关连接"
else
  bad "过期没有关连接"
fi

if grep -q 'config.BattleUpstream' "$PROXY" && ! grep -qE '60001' "$PROXY"; then
  ok "上游战斗服地址来自配置，代码里没写死端口"
else
  bad "上游地址写死在代码里"
fi

if grep -qE 'Mmo\.Proto|SkillRequest|ServerMessage|SkillResult' "$PROXY"; then
  bad "网关在解析战斗消息，网关不该懂技能"
else
  ok "网关只转发字节流，不解析战斗消息"
fi

echo "== 3. 环境 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env，登录拿不到令牌"
  echo
  echo "P1-1a2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
HAVE_DB=1
ok "库凭据在位：$( [ -n "${MMO_GAMEDB:-}" ] && echo "env:MMO_GAMEDB" || echo "file:/srv/mmo/gamedb.env" )"

# 战斗服：一份临时配置，端口固定，其余用仓库里的表。
python3 - "$ROOT" "$TMP" "$BATTLE_PORT" <<'PY'
import json, os, sys
root, tmp, port = sys.argv[1], sys.argv[2], sys.argv[3]
cfg = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
cfg["listen"] = "127.0.0.1:" + port
cfg["settlement_dir"] = os.path.join(tmp, "settlements")
cfg["character_dir"] = os.path.join(tmp, "characters")
cfg["skill_table"] = "skills.json"
cfg["class_table"] = "classes.json"
json.dump(cfg, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)
PY
cp "$ROOT/config/skills.json" "$ROOT/config/classes.json" "$TMP/"

cat > "$TMP/gateway.json" <<JSON
{
  "schema": "gateway-config/1",
  "listen": "http://127.0.0.1:${GATEWAY_PORT}",
  "token_ttl_s": ${TOKEN_TTL},
  "pbkdf2_iterations": 10000,
  "battle_upstream": "ws://127.0.0.1:${BATTLE_PORT}/battle"
}
JSON

: > "$TMP/battle.log"
"$DOTNET" "$BATTLE_DLL" --config "$TMP/battle.json" >>"$TMP/battle.log" 2>&1 &
BATTLE_PID=$!

BATTLE_READY=0
for _ in $(seq 1 120); do
  if grep -q "^listen=http://127.0.0.1:${BATTLE_PORT}\$" "$TMP/battle.log" 2>/dev/null; then
    BATTLE_READY=1
    break
  fi
  if ! kill -0 "$BATTLE_PID" 2>/dev/null; then
    break
  fi
  sleep 0.1
done

if [ "$BATTLE_READY" = "1" ]; then
  ok "战斗服在 127.0.0.1:${BATTLE_PORT} 上"
else
  bad "战斗服没起来"
  tail -8 "$TMP/battle.log" | detail
  echo
  echo "P1-1a2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

# 账号：建表 + 种子（P1-1a1 的表结构，这一行不改表）。
if ! "$DOTNET" "$GATEWAY_DLL" --migrate --config "$TMP/gateway.json" --sql "$ROOT/db/character/account.sql" >"$TMP/migrate.log" 2>&1; then
  bad "账号表建不出来"
  tail -5 "$TMP/migrate.log" | detail
  echo
  echo "P1-1a2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if printf '%s\n' "$TEST_PASSWORD" \
   | "$DOTNET" "$GATEWAY_DLL" --seed-account "$ACCOUNT" --config "$TMP/gateway.json" 2>&1 \
   | grep -q '^seed=ok'; then
  ok "测试账号已写入：${ACCOUNT}"
else
  bad "测试账号写入失败"
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
  if ! kill -0 "$GATEWAY_PID" 2>/dev/null; then
    break
  fi
  sleep 0.2
done

if [ "$GATEWAY_READY" = "1" ]; then
  ok "网关在 127.0.0.1:${GATEWAY_PORT} 上，上游是 $(grep -m1 '^battle_upstream=' "$TMP/gateway.log" | cut -d= -f2-)"
else
  bad "网关没起来"
  tail -8 "$TMP/gateway.log" | detail
  echo
  echo "P1-1a2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

# 配置里的有效期必须真的被读到。折名字折错时会悄悄用默认的 7200，
# 过期场景就永远等不到，这一条专门盯这个。
TTL_SEEN="$(grep -m1 '^token_ttl_s=' "$TMP/gateway.log" | cut -d= -f2-)"
if [ "$TTL_SEEN" = "$TOKEN_TTL" ]; then
  ok "有效期按配置生效：${TTL_SEEN} 秒"
else
  bad "有效期没按配置走：读到 ${TTL_SEEN:-空}，应为 ${TOKEN_TTL}"
fi

GW_URL="ws://127.0.0.1:${GATEWAY_PORT}/battle"

probe() {
  "$DOTNET" "$PROBE_DLL" "$@" 2>&1
}

echo "== 4. 没有令牌连不进来 =="
OUT="$(probe --url "$GW_URL" --case p11a2_reject)"
if printf '%s' "$OUT" | grep -q '^verdict=ok'; then
  ok "不带令牌：握手被拒（$(printf '%s' "$OUT" | grep -m1 'refused=' | sed 's/^case=p11a2_reject //')）"
else
  bad "不带令牌也连上了"
  printf '%s\n' "$OUT" | head -4 | detail
fi

OUT="$(probe --url "${GW_URL}?token=not-a-real-token" --case p11a2_reject)"
if printf '%s' "$OUT" | grep -q '^verdict=ok'; then
  ok "伪造令牌：握手被拒"
else
  bad "伪造令牌也连上了"
  printf '%s\n' "$OUT" | head -4 | detail
fi

OUT="$(probe --url "${GW_URL}?token=" --case p11a2_reject)"
if printf '%s' "$OUT" | grep -q '^verdict=ok'; then
  ok "空令牌：握手被拒"
else
  bad "空令牌也连上了"
fi

echo "== 5. 有效令牌：转发链路通 =="
TOKEN="$(get_token)"
if [ -n "$TOKEN" ]; then
  ok "登录拿到令牌：长度 ${#TOKEN}"
else
  bad "登录没拿到令牌，后面的连不上"
  tail -5 "$TMP/gateway.log" | detail
  echo
  echo "P1-1a2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

OUT="$(probe --url "${GW_URL}?token=${TOKEN}" --case p11a2_connect --boss "$BOSS" --hit "$SKILL")"
if printf '%s' "$OUT" | grep -q '^verdict=ok'; then
  ok "带令牌连进去出一手，收到战斗服回包：$(printf '%s' "$OUT" | grep -m1 'got=' | sed 's/^case=p11a2_connect //')"
else
  bad "带令牌也拿不到回包，转发链路不通"
  printf '%s\n' "$OUT" | head -5 | detail
  tail -5 "$TMP/gateway.log" | detail
fi

if grep -q '^proxy_open account=p11a2_user' "$TMP/gateway.log"; then
  ok "网关日志记了这次转发：$(grep -m1 '^proxy_open' "$TMP/gateway.log")"
else
  bad "网关日志里没有 proxy_open"
fi

echo "== 6. 连上之后令牌到期，就发不出技能了 =="
# 新令牌：连上时有效，等过 TTL 再发一手，网关必须在转发前把连接关掉。
TOKEN2="$(get_token)"
if [ -z "$TOKEN2" ]; then
  bad "第二次登录没拿到令牌"
else
  OUT="$(probe --url "${GW_URL}?token=${TOKEN2}" --case p11a2_expire \
        --boss "$BOSS" --hit "$SKILL" --wait-ms $(( (TOKEN_TTL + 2) * 1000 )))"
  if printf '%s' "$OUT" | grep -q '^verdict=ok'; then
    ok "令牌到期后同一连接发技能：收不到回包（$(printf '%s' "$OUT" | grep -m1 'got=' | sed 's/^case=p11a2_expire //')）"
  else
    bad "令牌过期后还能发技能"
    printf '%s\n' "$OUT" | head -5 | detail
  fi

  if grep -q 'proxy_expired' "$TMP/gateway.log"; then
    ok "网关日志记了这次到期断连"
  else
    bad "网关没有按到期断连"
  fi
fi

echo "== 7. 过期令牌重新连 =="
# TOKEN 和 TOKEN2 都超过 TTL 了，重新拿来连必须被拒。
OUT="$(probe --url "${GW_URL}?token=${TOKEN}" --case p11a2_reject)"
if printf '%s' "$OUT" | grep -q '^verdict=ok'; then
  ok "过期令牌重新连：握手被拒"
else
  bad "过期令牌还能重新连上"
fi

if grep -q 'proxy_reject reason=token' "$TMP/gateway.log"; then
  REJECTS="$(grep -c 'proxy_reject reason=token' "$TMP/gateway.log")"
  ok "网关一共拒了 ${REJECTS} 次无效令牌"
else
  bad "网关日志里没有拒绝记录"
fi

if grep -q "$TEST_PASSWORD" "$TMP/gateway.log" 2>/dev/null; then
  bad "网关日志里出现了口令"
else
  ok "网关日志里没有口令"
fi

echo
echo "P1-1a2 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
