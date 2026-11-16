#!/usr/bin/env bash
# N9 任务发放记城池税（开发架构第 14 节第 88 行，依赖 N7、P3-3a4）。
#   bash tools/check/check-n9.sh
#   MMO_N9_HAS_DB=1 bash tools/check/check-n9.sh
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
WD="$ROOT/src/World/bin/Release/net10.0/World.dll"
CC="--config $ROOT/config/character.json"
HAS_DB="${MMO_N9_HAS_DB:-0}"
TITLE="N9"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p" 2>/dev/null || true; done; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Character/Character.csproj src/Gateway/Gateway.csproj src/World/World.csproj; do
  n="$(basename "$proj" .csproj)"
  if ! "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ "$BUILD_OK" = "1" ] && ok "Character + Gateway + World 编译通过" || true

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP/static.txt" <<'PY'
import json, os, re, sys
root = sys.argv[1]
out = open(sys.argv[2], "w", encoding="utf-8")

def R(k, c, i=""):
    out.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")

def rd(p):
    return open(os.path.join(root, p), encoding="utf-8").read() if os.path.exists(os.path.join(root, p)) else ""

ci = json.load(open(os.path.join(root, "config/city.json"), encoding="utf-8"))
repo = rd("src/Character/CityTaxRepository.cs")
grant = rd("src/Character/GrantRepository.cs")
allcards = rd("tools/check/check-all.sh")

R("task_grant_city_id", ci.get("task_grant_city_id") == "qiyun")
R("ledger_source", "task_grant" in repo and "city_tax_ledger" in repo)
R("tax_from_db", "select tax_pct" in repo and "city_tax_ledger" in repo)
R("shop_buy_exempt", "shop_buy:" in repo)
R("grant_hooks_tax", "RecordTaskGrantInTransactionAsync" in grant)
R("no_deduct_player", "TaxGoldOf" in repo)
R("siege_no_ledger", "insert into \"社交\".city_tax_ledger" not in rd("src/Character/SiegeTerritoryRepository.cs"))
R("all_cards", "n9)" in allcards and re.search(r"\bn9\b", allcards))
PY
while IFS= read -r line; do
  case "$line" in
    PASS*) ok "${line#PASS }" ;;
    FAIL*) bad "${line#FAIL  }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 坏配置 =="
BAD="$TMP/bad-city.json"
python3 - "$ROOT/config/city.json" "$BAD" <<'PY'
import copy, json, sys
j = json.load(open(sys.argv[1], encoding="utf-8"))
j["task_grant_city_id"] = "no_such_city"
json.dump(j, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
OUT="$("$DOTNET" "$CD" --city-table-path "$BAD" $CC 2>&1)" || true
if printf '%s' "$OUT" | grep -q 'config_error='; then
  ok "task_grant_city_id 对不上 cities 时起不来"
else
  bad "坏城池配置没拦住" && printf '%s' "$OUT" | head -3 | detail
fi
GOOD_OUT="$("$DOTNET" "$CD" --city-table $CC 2>&1)" || true
if printf '%s' "$GOOD_OUT" | grep -q 'task_grant_city=qiyun'; then
  ok "正本城池表含 task_grant_city"
else
  bad "正本读失败" && printf '%s' "$GOOD_OUT" | head -3 | detail
fi

echo "== 4. 真库：发放金币记账 + 采集仍拒 =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真库段（MMO_N9_HAS_DB=1 且 MMO_GAMEDB 才跑）\n'
else
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$GD" --config "$ROOT/config/gateway.json" >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  "$DOTNET" "$WD" --config "$ROOT/config/world.json" >"$TMP/world.log" 2>&1 & PIDS="$PIDS $!"
  for _ in $(seq 1 80); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST \
      http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
      -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  TS="$(date +%s)"
  ACCT="n9_$TS"; PW="N9-$TS"; TXN="n9-grant-$TS"; TXN2="n9-dup-$TXN"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --config "$ROOT/config/gateway.json" >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login \
    -H 'Content-Type: application/json' -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin).get("token",""))')"
  CID="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"n9$TS\",\"race\":\"mai\",\"class_id\":\"warrior\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin).get("character_id",""))')"
  if [ -z "$CID" ]; then
    bad "建角失败"
  else
    ok "建角 id=$CID"
    python3 - "$MMO_GAMEDB" <<'PY'
import os, psycopg2
cs = os.environ["MMO_GAMEDB"]
with psycopg2.connect(cs) as c:
    with c.cursor() as cur:
        cur.execute('update "社交".city set tax_pct = 7 where city_id = %s', ("qiyun",))
    c.commit()
print("tax_pct_set=7")
PY
    ok "真库：栖云城 tax_pct=7（模拟城战写回后的运行时事实）"
    GR="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/grant?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"txn_id\":\"$TXN\",\"entries\":[{\"currency\":{\"kind\":\"gold\",\"delta\":1000}}]}")"
    printf '%s' "$GR" | grep -q '"duplicate":false' \
      && ok "任务发放 1000 金币到账" || bad "发放失败：$GR"
    ROW="$("$DOTNET" "$CD" --city-tax-row "$TXN" $CC 2>&1 | grep -m1 '^city_tax_row=')"
  printf '%s' "$ROW" | grep -q 'city_tax_row=ok txn='"$TXN"' city=qiyun source=task_grant gross=1000 tax=70' \
      && ok "账本：gross=1000 tax=70（7% 向下取整）" || bad "账本不对：$ROW"
    GR2="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/grant?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"txn_id\":\"$TXN\",\"entries\":[{\"currency\":{\"kind\":\"gold\",\"delta\":1000}}]}")"
    printf '%s' "$GR2" | grep -q '"duplicate":true' \
      && ok "重放 duplicate" || bad "重放没 duplicate：$GR2"
    CNT="$(python3 - "$MMO_GAMEDB" "$TXN" <<'PY'
import os, psycopg2, sys
with psycopg2.connect(sys.argv[1]) as c:
    with c.cursor() as cur:
        cur.execute('select count(*) from "社交".city_tax_ledger where txn_id = %s', (sys.argv[2],))
        print(cur.fetchone()[0])
PY
)"
    [ "$CNT" = "1" ] && ok "账本仍 1 行" || bad "账本行数=$CNT"
    R="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/world/gather?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"node_id\":\"spirit_qiyun_1\"}")"
    printf '%s' "$R" | grep -q '"reason":"spirit_stone_forbidden"' \
      && ok "个人采集灵石矿脉仍被拒" || bad "灵石采集没拒：$R"
  fi
fi

finish "$TITLE"