#!/usr/bin/env bash
# N12 英灵图鉴的收集度（开发架构第 14 节第 91 行，依赖 N10）。
#   bash tools/check/check-n12.sh
#   MMO_N12_HAS_DB=1 bash tools/check/check-n12.sh
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
CG="--config $ROOT/config/gateway.json"
HAS_DB="${MMO_N12_HAS_DB:-0}"
TITLE="N12"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p" 2>/dev/null || true; done; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Shared/Shared.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/b-$n.log" 2>&1; then
    ok "编译 $n"
  else
    BUILD_OK=0
    bad "编译失败 $n"
    grep -E 'error ' "$TMP/b-$n.log" | head -3 | detail
  fi
done
[ "$BUILD_OK" = "1" ] || { finish "$TITLE"; exit 1; }

echo "== 2. 静态断言 =="
python3 - "$ROOT" >"$TMP/static.out" <<'PY'
import json, os, re, sys
root = sys.argv[1]
def R(k,c): print(("PASS" if c else "FAIL")+":"+k)

hc = json.load(open(os.path.join(root,"config/hero_codex.json"),encoding="utf-8"))
arch = open(os.path.join(root,"docs/开发架构.md"),encoding="utf-8").read()
html = open(os.path.join(root,"docs/MMO系统架构方案.html"),encoding="utf-8").read()

R("14节N12", "| 91 | N12 |" in arch and "不做成卡组战斗" in arch)
R("方案口径", "英灵图鉴" in html and "被动小加成" in html)
R("entries9", len(hc.get("entries",[]))==9)
R("life_assist", hc.get("passive_kind")=="life_assist")
R("names", [e["name"] for e in hc["entries"]]==[
    "乔小满","苏晚织","阮青","阿弦","闻嫂","孟七","澄川","灰生","周晚棠"])
R("repo", "HeroCodexRepository" in open(os.path.join(root,"src/Character/HeroCodexRepository.cs"),encoding="utf-8").read())
R("proto", "rpc DescribeHeroCodex" in open(os.path.join(root,"proto/mmo/character.proto"),encoding="utf-8").read())
R("gateway", "/hero-codex" in open(os.path.join(root,"src/Gateway/HeroCodexApi.cs"),encoding="utf-8").read())
battle = ""
for dp, _, fs in os.walk(os.path.join(root,"src/Battle")):
    for f in fs:
        if f.endswith(".cs"):
            battle += open(os.path.join(dp,f),encoding="utf-8").read()
R("no_battle", "HeroCodex" not in battle)
R("no_deck", "deck" not in open(os.path.join(root,"src/Character/HeroCodexTable.cs"),encoding="utf-8").read().lower())
R("client_note", "CodexNote" in open(os.path.join(root,"client/MMOGame/Assets/HotUpdate/Ui/UiSocialCodex.cs"),encoding="utf-8").read())
R("all_cards", "n12)" in open(os.path.join(root,"tools/check/check-all.sh"),encoding="utf-8").read())
PY
while IFS=: read -r st k; do
  case "$st" in PASS) ok "$k" ;; FAIL) bad "$k" ;; esac
done < "$TMP/static.out"

echo "== 3. 坏配置 =="
HC="$ROOT/config/hero_codex.json"
HCBAK="$HC.n12bak"
cp "$HC" "$HCBAK"
python3 - "$HC" <<'PY'
import json, sys
p = sys.argv[1]
j = json.load(open(p, encoding="utf-8"))
j["passive_kind"] = "damage_bonus"
json.dump(j, open(p,"w"), ensure_ascii=False)
PY
OUT="$("$DOTNET" "$CD" $CC 2>&1)" || true
mv "$HCBAK" "$HC"
if printf '%s' "$OUT" | grep -q 'config_error='; then
  ok "damage_kind 加载期拒绝"
else
  bad "坏 passive_kind 没拦住"
fi

echo "== 4. 缺条目 =="
cp "$HC" "$HCBAK"
python3 - "$HC" <<'PY'
import json, sys
p = sys.argv[1]
j = json.load(open(p, encoding="utf-8"))
j["entries"] = j["entries"][:3]
json.dump(j, open(p,"w"), ensure_ascii=False)
PY
OUT2="$("$DOTNET" "$CD" $CC 2>&1)" || true
mv "$HCBAK" "$HC"
printf '%s' "$OUT2" | grep -q 'config_error=' && ok "不足 9 条起不来" || bad "缺条目没报错"

if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  echo "（跳过真库段：MMO_N12_HAS_DB=1 且配置 MMO_GAMEDB）"
  finish "$TITLE"
  exit 0
fi

echo "== 5. 真库收集度 =="
"$DOTNET" build src/Gateway/Gateway.csproj -c Release --nologo -v q >/dev/null 2>&1 || true
"$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/account.sql" $CC >"$TMP/mig-account.log" 2>&1 || true
"$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/character.sql" $CC >"$TMP/mig-char.log" 2>&1 || true
"$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 &
PIDS="$PIDS $!"
wait_ready "$TMP/char.log" '^ready' "角色服" || true
"$DOTNET" "$GD" $CG >"$TMP/gw.log" 2>&1 &
PIDS="$PIDS $!"
for _ in $(seq 1 120); do
  code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
    -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
  [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
done
ACCT="n12_$$"; PW="N12-$$-$(date +%s)"
printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" $CG >/dev/null 2>&1
TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
  -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
[ -n "$TOK" ] && ok "登录" || bad "登录失败"
ID="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"n12$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin).get("character_id",""))' 2>/dev/null)"
[ -n "$ID" ] && ok "建角 id=$ID" || { bad "建角失败"; finish "$TITLE"; exit 1; }

S0="$("$DOTNET" "$CD" --hero-codex-state "$ID" $CC 2>&1 | grep -m1 '^hero_codex_state=')"
printf '%s' "$S0" | grep -q 'collected=0/9 total_passive_pct_bps=0 passive_kind=life_assist' \
  && ok "初始 0 收集" || bad "初始状态不对：$S0"

"$DOTNET" "$CD" --raw-main-stage-write "$ID" 4 $CC >/dev/null
S4="$("$DOTNET" "$CD" --hero-codex-state "$ID" $CC 2>&1 | grep -m1 '^hero_codex_state=')"
printf '%s' "$S4" | grep -q 'collected=4/9' \
  && printf '%s' "$S4" | grep -q 'total_passive_pct_bps=90' \
  && ok "main_stage=4 收集四位被动 90bps" || bad "stage4 不对：$S4"

HTTP="$(curl -s --noproxy '*' "http://127.0.0.1:61000/hero-codex?token=$TOK&character_id=$ID")"
printf '%s' "$HTTP" | python3 -c 'import json,sys; j=json.load(sys.stdin); sys.exit(0 if j.get("ok") and j.get("passive_kind")=="life_assist" and j.get("collected_count")==4 else 1)' \
  && ok "HTTP 图鉴与 gRPC 一致" || bad "HTTP 回包：$HTTP"

finish "$TITLE"