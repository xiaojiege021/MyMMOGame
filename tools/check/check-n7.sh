#!/usr/bin/env bash
# N7 主线交任务挂上发奖（开发架构第 14 节第 86 行，依赖 N6、C1）。
#   bash tools/check/check-n7.sh
#   MMO_N7_HAS_DB=1 bash tools/check/check-n7.sh
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
CC="--config $ROOT/config/character.json"
HAS_DB="${MMO_N7_HAS_DB:-0}"
TITLE="N7"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p" 2>/dev/null || true; done; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Character/Character.csproj src/Gateway/Gateway.csproj; do
  n="$(basename "$proj" .csproj)"
  if ! "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ "$BUILD_OK" = "1" ] && ok "Character + Gateway 编译通过" || true

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP/static.txt" <<'PY'
import os, re, sys
root = sys.argv[1]
out = open(sys.argv[2], "w", encoding="utf-8")

def R(k, c, i=""):
    out.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")

def rd(p):
    if p.endswith("src/Character/Program.cs"):
        # D1a 把 CLI 拆进 Cli/：静态断言按 Program + Cli/* 合起来看（名字与内容未变）。
        return "".join(open(x, encoding="utf-8").read() for x in
                       [os.path.join(root, p)] + sorted(__import__("glob").glob(os.path.join(root, "src/Character/Cli/*.cs"))))
    return open(os.path.join(root, p), encoding="utf-8").read() if os.path.exists(os.path.join(root, p)) else ""

ml_drop = rd("config/mainline_drops.json")
ddrop = rd("config/dungeon_drops.json")
repo = rd("src/Character/MainlineRepository.cs")
gw = rd("src/Gateway/MainlineApi.cs")
allcards = rd("tools/check/check-all.sh")

R("mainline_drops_present", "mainline-drops/1" in ml_drop and '"stage": 1' in ml_drop)
R("main_not_in_dungeon_drops", '"dungeon_id": "main"' not in ddrop)
R("settlement_gate", "settlement_required" in repo and "exp_settlement" in repo)
R("grant_same_txn", "grant_txn" in repo and "PlanItemDeltas" in repo)
R("gateway_txn_body", "TxnId" in gw and "txnId.Length == 0" in gw)
R("cli_seed", "--mainline-seed-settlement" in rd("src/Character/Program.cs"))
R("all_cards", "n7)" in allcards and re.search(r"\bn7\b", allcards))
R("spirit_ban", "stone_1" in rd("src/Character/MainlineDropTable.cs"))
PY
while IFS= read -r line; do
  case "$line" in
    PASS*) ok "${line#PASS }" ;;
    FAIL*) bad "${line#FAIL  }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 坏配置 =="
BAD="$TMP/bad-ml-drop.json"
printf '%s\n' '{"schema":"mainline-drops/1","version":1,"note":"x","items_config":"items.json","entries":[{"stage":1,"lines":[{"item_id":"stone_1","count":1}]}]}' >"$BAD"
OUT="$("$DOTNET" "$CD" --mainline-drop-table-path "$BAD" $CC 2>&1)" || true
if printf '%s' "$OUT" | grep -q 'config_error='; then
  ok "灵石币种进主线发奖表起不来"
else
  bad "坏配置没拦住" && printf '%s\n' "$OUT" | head -3 | detail
fi
GOOD_OUT="$("$DOTNET" "$CD" --mainline-drop-table-path "$ROOT/config/mainline_drops.json" $CC 2>&1)" || true
if printf '%s' "$GOOD_OUT" | grep -q 'mainline_drop_table_state=ok'; then
  ok "正本主线发奖表可读"
else
  bad "正本读失败" && printf '%s' "$GOOD_OUT" | head -3 | detail
fi

echo "== 4. 真库：结算号 + 交任务入包 =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真库段（MMO_N7_HAS_DB=1 且 MMO_GAMEDB 才跑）\n'
else
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$GD" --config "$ROOT/config/gateway.json" >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  for _ in $(seq 1 80); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST \
      http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
      -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  TS="$(date +%s)"
  ACCT="n7_$TS"; PW="N7-$TS"; TXN="n7-settle-$TS"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --config "$ROOT/config/gateway.json" >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login \
    -H 'Content-Type: application/json' -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin).get("token",""))')"
  CID="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"n7$TS\",\"race\":\"mai\",\"class_id\":\"warrior\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin).get("character_id",""))')"
  if [ -z "$CID" ]; then
    bad "建角失败"
  else
    ok "建角 id=$CID"
    R="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/mainline/turn-in?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"stage\":1}")"
    printf '%s' "$R" | grep -q '"error":"bad_request"' \
      && ok "缺 txn_id 被拒" || bad "缺 txn_id 没拦住：$R"
    cli() { "$DOTNET" "$CD" $CC "$@" 2>&1; }
    cli --mainline-seed-settlement "$CID" "$TXN" | grep -q 'mainline_seed_settlement=written' \
      && ok "注入同号 exp_settlement" || bad "注入结算失败"
    R="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/mainline/turn-in?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"stage\":1,\"txn_id\":\"$TXN\"}")"
  printf '%s' "$R" | python3 -c 'import json,sys; o=json.load(sys.stdin); sys.exit(0 if o.get("ok") and o.get("main_stage")==1 else 1)' \
      && ok "交第 1 场推进且入包链路通" || bad "交任务失败：$R"
    INV="$(cli --item-count "$CID" stone_enhance_low 2>&1 | grep -m1 '^item_count=' || true)"
    [ "$INV" = "item_count=1 id=$CID item=stone_enhance_low" ] \
      && ok "背包 1 颗低级强化石" || bad "背包不对：$INV"
    R2="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/mainline/turn-in?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"stage\":1,\"txn_id\":\"$TXN\"}")"
    printf '%s' "$R2" | python3 -c 'import json,sys; o=json.load(sys.stdin); sys.exit(0 if o.get("duplicate") else 1)' \
      && ok "同 txn 重放 duplicate" || bad "幂等不对：$R2"
    cli --raw-level-write "$CID" 45 >/dev/null
    cli --mainline-seed-settlement "$CID" "${TXN}-s2" >/dev/null
    OUT="$(cli --mainline-turn-in "$CID" --mainline-stage 2 --mainline-txn "${TXN}-s2")"
    printf '%s' "$OUT" | grep -q 'mainline_turn_in=ok=true.*drop_lines=1' \
      && ok "第 2 场发奖 drop_lines=1" || bad "第 2 场发奖：$OUT"
  fi
fi

finish "$TITLE"