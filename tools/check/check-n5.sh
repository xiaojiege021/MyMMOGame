#!/usr/bin/env bash
# N5 竞技场与战场的进场口（开发架构第 14 节第 84 行，依赖 P2-3a、P2-3b）。
#   bash tools/check/check-n5.sh
#   MMO_N5_HAS_DB=1 bash tools/check/check-n5.sh   # 加真库端到端（经网关 HTTP + WS）
# 完成标准：网关能把人送进战斗服已有的竞技场或战场。屏上的假入口不在这张卡做。
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
CC="--config $ROOT/config/character.json"
CG="--config $ROOT/config/gateway.json"
HAS_DB="${MMO_N5_HAS_DB:-0}"
TITLE="N5"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p" 2>/dev/null || true; done; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Gateway/Gateway.csproj src/Battle/Battle.csproj src/Character/Character.csproj \
  tools/probe/BattleProbe/BattleProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  if ! "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
[ "$BUILD_OK" = "1" ] && ok "Gateway + Battle + Character + 探针编译通过" || true

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP/static.txt" <<'PY'
import json, os, re, sys

root = sys.argv[1]
out = open(sys.argv[2], "w", encoding="utf-8")

def R(k, c, i=""):
    out.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")

def rd(p):
    return open(os.path.join(root, p), encoding="utf-8").read() if os.path.exists(os.path.join(root, p)) else ""

versus = rd("src/Gateway/VersusApi.cs")
proc = rd("src/Gateway/GatewayProcess.cs")
modes = rd("src/Gateway/VersusArenaModes.cs")
arena_ui = rd("client/MMOGame/Assets/HotUpdate/Ui/UiArenaScreen.cs")
allcards = rd("tools/check/check-all.sh")

R("arena_enter_route", 'MapPost("/versus/arena/enter"' in versus)
R("battlefield_enter_route", 'MapPost("/versus/battlefield/enter"' in versus)
R("profile_from_character", "DescribeBattleProfileAsync" in versus)
R("no_class_in_body", "class_id" not in versus.split("ArenaEnterBody")[1].split("BattlefieldEnterBody")[0]
  if "ArenaEnterBody" in versus else False)
R("arena_modes_loader", "arena_match.json" in modes and "team_size" in modes)
R("gateway_registered", "VersusApi.Map" in proc)
R("battle_path_response", "battle_path" in versus and '"/battle"' in versus)
R("ui_no_fake_entry", "VersusApi" not in arena_ui and "无匹配入口" not in arena_ui)
# 屏不直接调网关类、也不自己编一个进场口：路径与模式只从回包来（U12 接线后仍是这条口径）。
R("no_proto_change", "versus" not in rd("proto/mmo/battle.proto").lower()
  and "versus" not in rd("proto/mmo/character.proto").lower())
R("all_cards", "n5)" in allcards and re.search(r"\bn5\b", allcards))
R("log_arena", "versus_arena_enter account=" in versus)
R("log_battlefield", "versus_battlefield_enter account=" in versus)
PY
while IFS= read -r line; do
  case "$line" in
    PASS*) ok "${line#PASS }" ;;
    FAIL*) bad "${line#FAIL  }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 真库：HTTP 进场口 + 网关 /battle =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真库段（MMO_N5_HAS_DB=1 且 MMO_GAMEDB 才跑）\n'
else
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
b = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
b["listen"] = "127.0.0.1:0"
b["settlement_dir"] = os.path.join(tmp, "settle")
b["character_dir"] = os.path.join(tmp, "chr")
b["exp_upstream"] = "http://127.0.0.1:62000"
b["character_upstream"] = "http://127.0.0.1:62000"
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json"),
             ("battlefield_table", "battlefield.json")):
    b[k] = os.path.join(root, "config", f)
d = json.load(open(os.path.join(root, "config", "battlefield.json"), encoding="utf-8"))
d["fill_timeout_ms"] = 300
json.dump(d, open(os.path.join(tmp, "bf-fast.json"), "w", encoding="utf-8"), ensure_ascii=False)
b["battlefield_table"] = os.path.join(tmp, "bf-fast.json")
json.dump(b, open(os.path.join(tmp, "battle-real.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 &
  PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$BD" --config "$TMP/battle-real.json" >"$TMP/battle.log" 2>&1 &
  PIDS="$PIDS $!"
  # 等 listen 行落盘再抓端口：慢盘 / 冷启动时 dotnet 写第一行比 start 晚几十到几百毫秒，
  # 立刻 grep 会抓到空串，后面网关上游就成了 ws:///battle（U12 在 ubu 上撞过）。
  BU=""
  for _ in $(seq 1 160); do
    BU="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/battle.log" | tail -1 | sed 's/^listen=//')"
    [ -n "$BU" ] && break
    sleep 0.25
  done
  [ -n "$BU" ] && ok "战斗服就绪" || bad "战斗服没起来"

  # 网关战斗上游指向本次战斗服
  python3 - "$ROOT" "$TMP" "$BU" <<'PY'
import json, os, sys
root, tmp, bu = sys.argv[1], sys.argv[2], sys.argv[3]
g = json.load(open(os.path.join(root, "config", "gateway.json"), encoding="utf-8"))
g["listen"] = "http://127.0.0.1:61000"
g["battle_upstream"] = "ws://" + bu.split("://", 1)[-1] + "/battle"
json.dump(g, open(os.path.join(tmp, "gw.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  GWCFG="--config $TMP/gw.json"
  "$DOTNET" "$GD" $GWCFG >"$TMP/gw.log" 2>&1 &
  PIDS="$PIDS $!"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
      -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break
    sleep 0.2
  done

  ACCT="n5_$$"; PW="N5-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 4 $GWCFG >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
    | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
  [ -n "$TOK" ] && ok "登录拿到令牌" || bad "登录失败"

  mkchar() {
    local cls="$1" out id
    for _ in $(seq 1 10); do
      out="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
        -H 'Content-Type: application/json' \
        -d "{\"name\":\"n5_$cls$RANDOM\",\"race\":\"mai\",\"class_id\":\"$cls\"}")"
      id="$(printf '%s' "$out" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")' 2>/dev/null)"
      [ -n "$id" ] && { printf '%s' "$id"; return 0; }
      sleep 0.5
    done
    return 1
  }
  A="$(mkchar warrior)"; B="$(mkchar priest)"
  [ -n "$A" ] && [ -n "$B" ] && ok "造角 warrior=${A} priest=${B}" || bad "造角失败"

  ARENA="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/versus/arena/enter?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$A,\"arena_mode\":\"duel\"}")"
  OK_A="$(printf '%s' "$ARENA" | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("ok"), d.get("class_id"), d.get("party_size"), d.get("party","")[:8])' 2>/dev/null)"
  case "$OK_A" in
    True\ warrior\ 2\ *) ok "竞技场 HTTP 进场：class=warrior party_size=2" ;;
    *) bad "竞技场 HTTP 回包不对：$ARENA" ;;
  esac
  PARTY="$(printf '%s' "$ARENA" | python3 -c 'import json,sys;print(json.load(sys.stdin).get("party",""))' 2>/dev/null)"

  GWWS="ws://127.0.0.1:61000/battle?token=${TOK}&char=${A}&class=warrior&arena_mode=duel&party_size=2&party=${PARTY}"
  if "$DOTNET" "$PROBE" --url "$GWWS" --case p12a_join --expect open --hold-ms 800 >"$TMP/arena-ws.log" 2>&1 \
     && grep -q 'joined=ok' "$TMP/arena-ws.log"; then
    ok "经网关 WS 进竞技场 duel"
  else
    bad "网关 WS 竞技场没开场"
    head -5 "$TMP/arena-ws.log" | detail
  fi
  grep -q 'proxy_open account=' "$TMP/gw.log" && ok "网关 proxy_open 有日志" || bad "没 proxy_open"
  grep -q 'stats_source=arena_template' "$TMP/battle.log" && ok "战斗服仍是模板场" || bad "战斗服模板行没了"

  BAD="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/versus/arena/enter?token=$TOK" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$A,\"arena_mode\":\"no_such\"}")"
  printf '%s' "$BAD" | python3 -c 'import json,sys; d=json.load(sys.stdin); exit(0 if d.get("ok") is False and d.get("reason")=="arena_mode_unknown" else 1)' \
    && ok "未知模式：ok=false arena_mode_unknown" || bad "未知模式没拒：$BAD"

  MATCH="n5-m$$"
  BF="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/versus/battlefield/enter?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$A,\"match\":\"$MATCH\",\"team\":\"a\"}")"
  printf '%s' "$BF" | python3 -c 'import json,sys; d=json.load(sys.stdin); exit(0 if d.get("ok") and d.get("class_id")=="warrior" and d.get("battlefield") else 1)' \
    && ok "战场 HTTP 进场回 class 与 match" || bad "战场 HTTP 不对：$BF"

  GWB="ws://127.0.0.1:61000/battle?token=${TOK}&char=${A}&class=warrior&battlefield=${MATCH}&team=a"
  "$DOTNET" "$PROBE" --url "$GWB" --case battlefield_open >"$TMP/bfa.log" 2>&1 &
  P1=$!
  GWB2="ws://127.0.0.1:61000/battle?token=${TOK}&char=${B}&class=priest&battlefield=${MATCH}&team=a"
  "$DOTNET" "$PROBE" --url "$GWB2" --case battlefield_open >"$TMP/bfb.log" 2>&1 &
  P2=$!
  wait "$P1" "$P2" 2>/dev/null || true
  grep -q 'verdict=ok' "$TMP/bfa.log" && grep -q 'verdict=ok' "$TMP/bfb.log" \
    && ok "经网关 WS 两人进同队战场" || bad "战场 WS 没双开"
  grep -q "battlefield_open=match=${MATCH}" "$TMP/battle.log" && ok "战斗服 battlefield_open" || bad "无 battlefield_open"
fi

finish "$TITLE"