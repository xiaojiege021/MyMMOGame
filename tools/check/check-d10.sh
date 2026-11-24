#!/usr/bin/env bash
# D10 网关校验中间件（开发架构 14.6 第 11 行，风险 3）。
#   鉴权与归属只写一处：令牌只在管道里解析一次（GatewayAuth.UseTokenGate），
#   路由体只取账号（GatewayAuth.AccountOf），归属复核只留一份 GatewayAuth.OwnsAsync；
#   拒绝码不变：401 invalid_token / 404 character_not_found / 业务 200 + ok=false。
#   bash tools/check/check-d10.sh
#   MMO_D10_GATEWAY=http://127.0.0.1:61000 MMO_D10_TOKEN=<令牌> MMO_D10_CHARACTER=<角色号> \
#     MMO_D10_OTHER=<别人的角色号> bash tools/check/check-d10.sh
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
TITLE="D10"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p" 2>/dev/null || true; done; rm -rf "$TMP"; }
trap cleanup EXIT

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
if "$DOTNET" build src/Gateway/Gateway.csproj -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "编译 Gateway"
else
  bad "编译失败 Gateway"
  grep -E 'error ' "$TMP/build.log" | head -3 | detail
  finish "$TITLE"
  exit 1
fi

echo "== 2. 静态段（只写一处 / 全路由覆盖 / 豁免表）=="
python3 - "$ROOT" >"$TMP/static.out" <<'PY'
import os, re, sys
root = sys.argv[1]

def R(k, c, i=""):
    print(("PASS" if c else "FAIL") + ":" + k + ("" if c else ":" + str(i)))

def rd(p):
    f = os.path.join(root, p)
    return open(f, encoding="utf-8").read() if os.path.exists(f) else ""

def walk(rel):
    out = {}
    for dp, _, fs in os.walk(os.path.join(root, rel)):
        if "/obj" in dp or "/bin" in dp:
            continue
        for f in fs:
            if f.endswith(".cs"):
                p = os.path.join(dp, f)
                out[os.path.relpath(p, root)] = open(p, encoding="utf-8").read()
    return out

gw = walk("src/Gateway")
auth = rd("src/Gateway/GatewayAuth.cs")
proc = rd("src/Gateway/GatewayProcess.cs")

# 1) 三件事各只有一份定义，且都在 GatewayAuth.cs。
def defs(pat):
    return sorted(p for p, t in gw.items() if re.search(pat, t))

for label, pat in [("token_parse", r"static\s+string\?\s+TokenFrom\("),
                   ("owns", r"static\s+async\s+Task<bool>\s+OwnsAsync\(")]:
    hit = defs(pat)
    R(label + "_single", hit == ["src/Gateway/GatewayAuth.cs"], hit)

# 2) 鉴权判定只有一份：管道里的 TryResolve，旧名字（Authorize / TryAuthorize）一份都不许留。
R("authorize_single",
  defs(r"static\s+bool\s+TryResolve\(HttpContext") == ["src/Gateway/GatewayAuth.cs"]
  and not defs(r"static\s+bool\s+(?:Try)?Authorize\("),
  defs(r"static\s+bool\s+(?:Try)?Authorize\("))

# 3) 令牌判定也不许散出去：只有 GatewayAuth 与两条长连接代理（握手要 expiresAt，走豁免表）。
resolve_sites = defs(r"tokens\.TryResolve\(")
R("resolve_sites", resolve_sites == ["src/Gateway/BattleProxy.cs",
                                     "src/Gateway/GatewayAuth.cs",
                                     "src/Gateway/WorldStreamProxy.cs"], resolve_sites)

# 4) 管道挂上了，且在 app.RunAsync 之前（挂载行在前才算「路由都过闸」）。
use_at = proc.find("GatewayAuth.UseTokenGate(app, tokens)")
run_at = proc.find("app.RunAsync")
R("middleware_mounted", use_at >= 0 and run_at > use_at, "use=%s run=%s" % (use_at, run_at))

# 5) 豁免表正好四条，且是健康检查 / 登入 / 两条长连接。
ex = re.search(r"Exempt\s*=\s*\{([^}]*)\}", auth)
items = re.findall(r'"([^"]+)"', ex.group(1)) if ex else []
R("exempt_table", items == ["/health", "/login", "/battle", "/world/stream"], items)

# 6) 401 只在三处出现：管道一份 + 两条长连接的握手（连接语义，不进管道）。
hit = sorted(p for p, t in gw.items() if "invalid_token" in t)
R("invalid_token_central",
  hit == ["src/Gateway/BattleProxy.cs", "src/Gateway/GatewayAuth.cs",
          "src/Gateway/WorldStreamProxy.cs"], hit)

# 7) 路由体不再自己解析令牌 / 自己判定鉴权。
per_route = [p for p, t in gw.items()
             if re.search(r"if \(!(?:TryAuthorize|Authorize)\(", t)]
R("no_per_route_auth", not per_route, per_route)

# 8) 全路由覆盖：注册的路由数（去掉两条长连接与两条豁免）都得取一次账号。
codes = "".join(t for p, t in gw.items() if "Proxy" not in p)
routes = len(re.findall(r'app\.Map(?:Get|Post)\("', codes))
accounted = len(re.findall(r"GatewayAuth\.AccountOf\(context\)", codes))
R("routes_covered", routes > 0 and accounted >= routes - 2, "routes=%d account_of=%d" % (routes, accounted))

# 9) 归属复核仍是「按账号读角色」，不许信请求体里的 account。
R("owns_read_only", "ListInventoryRequest { Account = account" in auth
  and "NotFound" in auth and "OwnsAsync(client, account" not in auth)

# 10) 业务拒绝码还在：404 character_not_found 的下限，不钉现值（会随卡增长）。
own404 = sum(t.count('character_not_found') for t in gw.values())
R("character_not_found_floor", own404 >= 40, own404)
PY
while IFS=: read -r st k i; do
  case "$st" in
    PASS) ok "$k" ;;
    FAIL)
      if [ -n "${i:-}" ]; then bad "$k [$i]"; else bad "$k"; fi
      ;;
  esac
done < "$TMP/static.out"

echo "== 3. 无库 HTTP 段（管道先拒 / 豁免放行 / 不抢 404）=="
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
  for _ in $(seq 1 60); do
    H="$(req GET "$BASE/health")"
    [ "$(code_of "$H")" = "200" ] && break
    sleep 0.2
  done

  [ "$(code_of "$H")" = "200" ] && ok "豁免 /health 无令牌 200" || bad "豁免 /health 回 $(code_of "$H")"

  L="$(req POST "$BASE/login" '{"account":"d10","password":"d10-pass"}')"
  [ "$(code_of "$L")" != "401" ] && ok "豁免 /login 不被管道拦（回 $(code_of "$L")）" \
    || bad "豁免 /login 被管道拦成 401"

  A="$(req GET "$BASE/inventory?character_id=1")"
  [ "$(code_of "$A")" = "401" ] && ok "无令牌 GET /inventory 401" || bad "无令牌 GET /inventory 回 $(code_of "$A")"
  printf '%s' "$(body_of "$A")" | grep -q 'invalid_token' \
    && ok "401 回包是 invalid_token" || bad "401 回包：$(body_of "$A")"

  B="$(req POST "$BASE/inventory/sort" '{"character_id":1}')"
  [ "$(code_of "$B")" = "401" ] && ok "无令牌 POST /inventory/sort 401" || bad "无令牌 sort 回 $(code_of "$B")"

  # 先认令牌再校验请求体：坏 body + 无令牌必须是 401，不是 400。
  C="$(req POST "$BASE/inventory/sort" 'not-json')"
  [ "$(code_of "$C")" = "401" ] && ok "无令牌先 401（不看请求体）" || bad "无令牌坏 body 回 $(code_of "$C")"

  D="$(req GET "$BASE/inventory?token=deadbeef&character_id=1")"
  [ "$(code_of "$D")" = "401" ] && ok "错令牌 401" || bad "错令牌回 $(code_of "$D")"

  N="$(req GET "$BASE/no-such-route")"
  [ "$(code_of "$N")" = "404" ] && ok "未注册路径仍 404（管道不抢回包）" || bad "未注册路径回 $(code_of "$N")"

  grep -q 'auth_reject path=/inventory reason=token' "$TMP/gw-nodb.log" \
    && ok "管道日志留 auth_reject path=/inventory" || bad "管道日志没有 auth_reject 行"
  grep -q 'auth_reject path=/inventory/sort reason=token' "$TMP/gw-nodb.log" \
    && ok "管道日志留 auth_reject path=/inventory/sort" || bad "sort 路径没有 auth_reject 行"
else
  bad "网关（无库）没起来"
  tail -5 "$TMP/gw-nodb.log" | detail
fi
stop_pid "$GWPID" 2>/dev/null || true

echo "== 4. 真网关段（拒绝码不变 / 归属仍按账号）=="
if [ -z "${MMO_D10_GATEWAY:-}" ] || [ -z "${MMO_D10_TOKEN:-}" ] || [ -z "${MMO_D10_CHARACTER:-}" ]; then
  echo "  (跳过：没给 MMO_D10_GATEWAY / MMO_D10_TOKEN / MMO_D10_CHARACTER)"
else
  G="${MMO_D10_GATEWAY%/}"
  Q="token=${MMO_D10_TOKEN}&character_id=${MMO_D10_CHARACTER}"
  MINE="$(req GET "$G/inventory?$Q")"
  [ "$(code_of "$MINE")" = "200" ] && ok "本人角色 /inventory 200" || bad "本人角色回 $(code_of "$MINE")"

  if [ -n "${MMO_D10_OTHER:-}" ]; then
    OTHER="$(req GET "$G/inventory?token=${MMO_D10_TOKEN}&character_id=${MMO_D10_OTHER}")"
    [ "$(code_of "$OTHER")" = "404" ] && ok "别人的角色 404" || bad "别人的角色回 $(code_of "$OTHER")"
    printf '%s' "$(body_of "$OTHER")" | grep -q 'character_not_found' \
      && ok "别人的角色回 character_not_found" || bad "别人的角色回包：$(body_of "$OTHER")"
  fi

  # 业务拒绝仍是 200 + ok=false，且被拒时数值为 0（不变量口径）。
  BIZ="$(req POST "$G/inventory/move?token=${MMO_D10_TOKEN}" \
    "{\"character_id\":${MMO_D10_CHARACTER},\"from_slot\":1,\"to_slot\":1}")"
  if [ "$(code_of "$BIZ")" = "200" ] && printf '%s' "$(body_of "$BIZ")" | grep -q '"ok":false'; then
    ok "业务拒绝回 200 + ok=false"
  else
    bad "业务拒绝回 $(code_of "$BIZ")：$(body_of "$BIZ")"
  fi
fi

finish "$TITLE"
