#!/usr/bin/env bash
# N13 境界子阶只读 HTTP（开发架构第 14 节第 96 行，依赖 N10、U5）。
#   网关转发 N10 已有的 PromoteRealmPhase / DescribeRealmPassive；客户端仍不算 passive、伤害公式不接。
#   bash tools/check/check-n13.sh
#   MMO_N13_HAS_DB=1 MMO_GAMEDB=... bash tools/check/check-n13.sh
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
CG="--config $ROOT/config/gateway.json"
CC="--config $ROOT/config/character.json"
HAS_DB="${MMO_N13_HAS_DB:-0}"
TITLE="N13"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p" 2>/dev/null || true; done; rm -rf "$TMP"; }
trap cleanup EXIT

# 一次请求，回 "body\ncode"。
req() {
  local method="$1" url="$2" body="${3:-}"
  if [ -n "$body" ]; then
    curl -s --noproxy '*' -w $'\n%{http_code}' -X "$method" "$url" \
      -H 'Content-Type: application/json' -d "$body" 2>/dev/null
  else
    curl -s --noproxy '*' -w $'\n%{http_code}' -X "$method" "$url" 2>/dev/null
  fi
}

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Gateway/Gateway.csproj src/Character/Character.csproj; do
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
import os, re, sys
root = sys.argv[1]
def R(k, c, i=""):
    print(("PASS" if c else "FAIL") + ":" + k + ("" if c else ":" + i))

def rd(p):
    f = os.path.join(root, p)
    return open(f, encoding="utf-8").read() if os.path.exists(f) else ""

def walk(rel):
    out = ""
    for dp, _, fs in os.walk(os.path.join(root, rel)):
        if "/obj" in dp or "/bin" in dp:
            continue
        for f in fs:
            if f.endswith(".cs"):
                out += open(os.path.join(dp, f), encoding="utf-8").read()
    return out

def walk_code(rel):
    """只留代码行：注释里写「不读 config/realms.json」这类说明不算读配置。"""
    keep = []
    for line in walk(rel).splitlines():
        s = line.strip()
        if s.startswith("//") or s.startswith("*") or s.startswith("/*"):
            continue
        keep.append(line)
    return "\n".join(keep)

arch = rd("docs/开发架构.md")
proto = rd("proto/mmo/character.proto")
gwp = rd("src/Gateway/GatewayProcess.cs")
api = rd("src/Gateway/RealmPhaseApi.cs")
cli = rd("tools/probe/CliProbe/CliParser.cs")
allcards = rd("tools/check/check-all.sh")
ci = rd("tools/check/ci.sh")

# 1) 这一行在排期表里，口径是「网关转发已有 gRPC」。
R("arch_row96", "| 96 | N13 |" in arch and "网关转发 N10 已有 gRPC" in arch)
# 2) proto 是 N10 定好的那几个，不新起一套消息。
R("proto_rpcs", "rpc PromoteRealmPhase" in proto and "rpc DescribeRealmPassive" in proto)

def block(name):
    m = re.search(r"message " + name + r"\s*\{(.*?)\n\}", proto, re.S)
    return m.group(1) if m else ""
R("proto_promote_req", all(f in block("PromoteRealmPhaseRequest")
    for f in ("string account", "uint64 character_id", "string txn_id")))
R("proto_passive_resp", all(f in block("DescribeRealmPassiveResponse")
    for f in ("string realm", "int32 realm_phase", "string phase_name", "int32 passive_pct_bps")))

# 3) 网关挂载、两条路径、都走 gRPC 客户端。
R("gateway_mount", "RealmPhaseApi.Map" in gwp)
R("gateway_paths", '"/realm/phase"' in api and '"/realm/phase/promote"' in api)
R("gateway_forward", "DescribeRealmPassiveAsync" in api and "PromoteRealmPhaseAsync" in api)
# 4) 事务号由网关生成，客户端不带。
body = re.search(r"record PromoteBody\(([^)]*)\)", api)
R("gateway_txn", "realm_phase:" in api and "Guid.NewGuid" in api
  and body is not None and "TxnId" not in (body.group(1) if body else "x"))
# 5) 网关不读境界配置、不算被动：阶名与万分比只从回包抄。
R("gateway_no_config", "realms.json" not in walk_code("src/Gateway")
  and "RealmSubPhases" not in walk_code("src/Gateway"))
R("gateway_from_reply", "passive_pct_bps = reply.PassivePctBps" in api)
# 6) 伤害公式不接被动。
R("no_battle", "RealmPhase" not in walk("src/Battle") and "realm_phase" not in walk("src/Battle"))
# 7) 客户端不读境界配置、也没有第二份阶名表（阶名只认回包）。
hot = walk("client/MMOGame/Assets/HotUpdate")
R("client_no_config", "realms.json" not in hot and "config/realms" not in hot)
R("client_no_phase_table", "大圆满" not in hot and "初期" not in hot)
# 8) 只读入口齐着，真库段才有得跑。
R("probe_flags", "--promote-realm-phase" in cli and "--realm-passive-state" in cli)
# 9) 接进总控与 CI。
R("all_cards", re.search(r"n13\)\s*echo\s*\"n10 u5\"", allcards) is not None)
R("ci", re.search(r"\bn13\b", ci) is not None)
PY
while IFS=: read -r st k; do
  case "$st" in PASS) ok "$k" ;; FAIL) bad "$k" ;; esac
done < "$TMP/static.out"

echo "== 3. 无库 HTTP 段（只验挂载与先认令牌）=="
free_port() { python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1]);s.close()'; }
GW_PORT="$(free_port)"
DEAD_PORT="$(free_port)"
python3 - "$ROOT/config/gateway.json" "$TMP/gw-nodb.json" "$GW_PORT" "$DEAD_PORT" <<'PY'
import json, sys
src, dst, gw, dead = sys.argv[1:5]
j = json.load(open(src, encoding="utf-8"))
j["listen"] = "http://127.0.0.1:%s" % gw
j["battle_upstream"] = "ws://127.0.0.1:%s/battle" % dead
for k in ("character_upstream", "world_upstream", "global_upstream", "social_upstream"):
    j[k] = "http://127.0.0.1:%s" % dead
json.dump(j, open(dst, "w", encoding="utf-8"), ensure_ascii=False)
PY
"$DOTNET" "$GD" --config "$TMP/gw-nodb.json" >"$TMP/gw-nodb.log" 2>&1 &
GWPID=$!
PIDS="$PIDS $GWPID"
if wait_ready "$TMP/gw-nodb.log" 'ready name=Gateway' "网关（无库）"; then
  BASE="http://127.0.0.1:$GW_PORT"
  # 起服后先轮询 /health，别拿 ready 之后立刻发的请求当结论（Kestrel 与 ready 行不是同一刻）。
  for _ in $(seq 1 60); do
    H="$(req GET "$BASE/health")"
    [ "$(code_of "$H")" = "200" ] && break
    sleep 0.2
  done
  [ "$(code_of "$H")" = "200" ] && ok "网关 /health 200" || bad "网关 /health 不对：$(code_of "$H")"

  R1="$(req GET "$BASE/realm/phase?character_id=1")"
  [ "$(code_of "$R1")" = "401" ] && ok "只读口无令牌 401" || bad "只读口无令牌回 $(code_of "$R1")"
  printf '%s' "$(body_of "$R1")" | grep -q 'invalid_token' \
    && ok "只读口无令牌回 invalid_token" || bad "只读口无令牌回包：$(body_of "$R1")"

  R2="$(req GET "$BASE/realm/phase?token=deadbeef&character_id=1")"
  [ "$(code_of "$R2")" = "401" ] && ok "只读口错令牌 401" || bad "只读口错令牌回 $(code_of "$R2")"

  R3="$(req POST "$BASE/realm/phase/promote" '{"character_id":1}')"
  [ "$(code_of "$R3")" = "401" ] && ok "升阶口无令牌 401" || bad "升阶口无令牌回 $(code_of "$R3")"

  R4="$(req POST "$BASE/realm/phase/promote" '{}' )"
  [ "$(code_of "$R4")" = "401" ] && ok "升阶口先认令牌再校验请求体" || bad "升阶口回 $(code_of "$R4")"

  grep -q 'realm_phase_reject reason=token' "$TMP/gw-nodb.log" \
    && grep -q 'realm_phase_promote_reject reason=token' "$TMP/gw-nodb.log" \
    && ok "网关日志留下两条令牌拒绝" || bad "网关日志没有令牌拒绝行"
fi
stop_pid "$GWPID"

if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  echo "（跳过真库段：MMO_N13_HAS_DB=1 且配置 MMO_GAMEDB）"
  finish "$TITLE"
  exit 0
fi

echo "== 4. 真服真库（网关转 gRPC，回包即事实）=="
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

ACCT="n13_$$"; PW="N13-$$-$(date +%s)"
printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" $CG >/dev/null 2>&1
TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
  -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
[ -n "$TOK" ] && ok "登录" || bad "登录失败"
BASE="http://127.0.0.1:61000"
ID="$(curl -s --noproxy '*' -X POST "$BASE/character?token=$TOK" -H 'Content-Type: application/json' \
  -d "{\"name\":\"n13$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin).get("character_id",""))' 2>/dev/null)"
[ -n "$ID" ] && ok "建角 id=$ID" || { bad "建角失败"; finish "$TITLE"; exit 1; }

# 只读口：新建角色是炼体一阶、被动 0，值与角色服只读入口同源。
G="$(req GET "$BASE/realm/phase?token=$TOK&character_id=$ID")"
GB="$(body_of "$G")"
[ "$(code_of "$G")" = "200" ] && ok "只读口 200" || bad "只读口回 $(code_of "$G")"
EX="$(expect_json "$GB" ok=true realm=炼体 realm_phase=0 phase_name=一阶 passive_pct_bps=0)"
[ "$EX" = "ok" ] && ok "新建角色炼体一阶、被动 0（HTTP 与 gRPC 一致）" || bad "只读口回包：$EX $GB"

# 等级不够：业务拒绝走 HTTP 200 + ok=false，阶不动。
P0="$(req POST "$BASE/realm/phase/promote" "{\"character_id\":$ID}" )"
# 上面这条没带令牌，先确认它被令牌挡住；下面带令牌再测门槛。
[ "$(code_of "$P0")" = "401" ] && ok "升阶口漏带令牌被挡" || bad "升阶口漏带令牌回 $(code_of "$P0")"

P1="$(req POST "$BASE/realm/phase/promote?token=$TOK" "{\"character_id\":$ID}")"
P1B="$(body_of "$P1")"
[ "$(code_of "$P1")" = "200" ] && ok "等级不足仍是 HTTP 200" || bad "等级不足回 $(code_of "$P1")"
EX="$(expect_json "$P1B" ok=false reason=level_not_enough from_phase=0 to_phase=0 level_required=6)"
[ "$EX" = "ok" ] && ok "1 级升二阶被拒且阶不动" || bad "等级门槛回包：$EX $P1B"

"$DOTNET" "$CD" --raw-level-write "$ID" 6 $CC >/dev/null
P2="$(req POST "$BASE/realm/phase/promote?token=$TOK" "{\"character_id\":$ID}")"
P2B="$(body_of "$P2")"
EX="$(expect_json "$P2B" ok=true to_phase=1 phase_name=二阶 passive_pct_bps=30)"
[ "$EX" = "ok" ] && ok "6 级升到炼体二阶、被动 30bps" || bad "升阶回包：$EX $P2B"
printf '%s' "$P2B" | grep -q '"txn_id":"realm_phase:' \
  && ok "回包带网关生成的事务号" || bad "回包没有事务号：$P2B"

G2="$(req GET "$BASE/realm/phase?token=$TOK&character_id=$ID")"
EX="$(expect_json "$(body_of "$G2")" ok=true realm_phase=1 phase_name=二阶 passive_pct_bps=30)"
[ "$EX" = "ok" ] && ok "只读口读到二阶（升阶真的落库）" || bad "升阶后只读口：$EX"

# 下一阶门槛 11：仍读配置，不在网关或客户端算。
P3="$(req POST "$BASE/realm/phase/promote?token=$TOK" "{\"character_id\":$ID}")"
EX="$(expect_json "$(body_of "$P3")" ok=false reason=level_not_enough from_phase=1 level_required=11)"
[ "$EX" = "ok" ] && ok "第二阶门槛 11 来自配置" || bad "二阶门槛回包：$EX $(body_of "$P3")"

# 别人的角色：只读口与升阶口都按归属拒。
ACCT2="n13b_$$"; PW2="N13B-$$-$(date +%s)"
printf '%s\n' "$PW2" | "$DOTNET" "$GD" --seed-account "$ACCT2" $CG >/dev/null 2>&1
TOK2="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
  -d "{\"account\":\"$ACCT2\",\"password\":\"$PW2\"}" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
O1="$(req GET "$BASE/realm/phase?token=$TOK2&character_id=$ID")"
[ "$(code_of "$O1")" = "404" ] && ok "别人的角色只读口 404" || bad "别人的角色只读口回 $(code_of "$O1")"
O2="$(req POST "$BASE/realm/phase/promote?token=$TOK2" "{\"character_id\":$ID}")"
[ "$(code_of "$O2")" = "404" ] && ok "别人的角色升阶口 404" || bad "别人的角色升阶口回 $(code_of "$O2")"

# 坏请求体。
B1="$(req POST "$BASE/realm/phase/promote?token=$TOK" '{}')"
[ "$(code_of "$B1")" = "400" ] && ok "缺角色号 400" || bad "缺角色号回 $(code_of "$B1")"
B2="$(req GET "$BASE/realm/phase?token=$TOK")"
[ "$(code_of "$B2")" = "400" ] && ok "只读口缺角色号 400" || bad "只读口缺角色号回 $(code_of "$B2")"

finish "$TITLE"
