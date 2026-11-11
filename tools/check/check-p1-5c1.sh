#!/usr/bin/env bash
# P1-5c1 核对：商城清单与拒绝（方案第 12 章 12.1、开发架构第 14 节第 41 行）。
#
# 完成标准：清单外的商品请求被拒绝，元宝不扣。
#
# 三种模式：
#   bash tools/check/check-p1-5c1.sh
#       编译 + 静态 + 只读入口 + 坏清单，不连库不连服（Mac 上就能全跑）
#   MMO_P1_5C1_GATEWAY=http://127.0.0.1:61000 MMO_P1_5C1_TOKEN=<令牌> \
#     MMO_P1_5C1_CHARACTER=<角色号> bash tools/check/check-p1-5c1.sh
#       再加真网关段：清单外 / 清单内 / 无令牌 / 坏请求 / 不存在的角色，
#       并对比调用前后元宝余额相等
#   MMO_P1_5C1_SKIP_BUILD=1 bash tools/check/check-p1-5c1.sh
#       跳过编译（已经 build 过时用）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
GATEWAY="${MMO_P1_5C1_GATEWAY:-}"
TOKEN="${MMO_P1_5C1_TOKEN:-}"
CHARACTER="${MMO_P1_5C1_CHARACTER:-0}"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

# ---------------------------------------------------------------- 1. 编译

if [ "${MMO_P1_5C1_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_P1_5C1_SKIP_BUILD=1）\n'
else
  if "$DOTNET" build src/Character -c Release --nologo -v q >"$TMP/build.log" 2>&1 \
     && "$DOTNET" build src/Gateway -c Release --nologo -v q >>"$TMP/build.log" 2>&1; then
    ok "角色服与网关编译通过"
  else
    bad "编译失败"; tail -20 "$TMP/build.log" | detail
  fi
fi

# ---------------------------------------------------------------- 2. 静态断言

echo "== 2. 静态断言 =="
ROOT="$ROOT" python3 - "$TMP" <<'PY' > "$TMP/static.txt" 2>&1
import json, os, re, sys

root = os.environ["ROOT"]
shop_config = os.path.join(root, "config/shop.json")
currency_config = os.path.join(root, "config/currency.json")
table_cs = os.path.join(root, "src/Character/ShopTable.cs")
service_cs = os.path.join(root, "src/Character/CharacterGrpcService.cs")
api_cs = os.path.join(root, "src/Gateway/ShopApi.cs")
program_cs = os.path.join(root, "src/Character/Program.cs")
config_cs = os.path.join(root, "src/Character/CharacterConfig.cs")
gateway_cs = os.path.join(root, "src/Gateway/GatewayProcess.cs")

def read(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

files = {p: read(p) for p in
         [shop_config, table_cs, service_cs, api_cs, program_cs, config_cs, gateway_cs]}
report("files", all(v for v in files.values()),
       "missing " + ",".join(os.path.basename(k) for k, v in files.items() if not v))

shop = json.loads(read(shop_config))
table = files[table_cs]
service = files[service_cs]
api = files[api_cs]

# 12.1 的十八种。这一串是方案正文，改动就要停下来问。
kinds_121 = ["outfit", "weapon_skin", "realm_skin", "flight_trail", "mount_skin",
             "home_decor", "guild_display", "bag_expand", "teleport_talisman",
             "red_talisman", "blue_talisman", "revive_scroll", "unbind_item",
             "exp_booster", "monthly_bundle", "second_slot", "season_pass",
             "protect_ticket"]
allowed = re.findall(r'"([a-z_]+)"', table.split("AllowedKinds")[1].split("}")[0])
report("kinds_18", allowed == kinds_121,
       "code=%s" % (",".join(allowed) if len(allowed) < 30 else len(allowed)))

item_kinds = [i["kind"] for i in shop["items"]]
report("kinds_cover_121", sorted(item_kinds) == sorted(kinds_121),
       "items=%d uniq=%d" % (len(item_kinds), len(set(item_kinds))))

# 12.1 的「绝不上架」那一串，清单里一条都不许有。
forbidden = ["gold", "enhance_stone", "transfer_stone", "crystal", "spirit_stone",
             "pill", "ore", "herb", "equip_blueprint", "equip", "attr_item",
             "revive_coin", "fatigue", "dungeon_count", "weekly_share",
             "realm_speed", "guild_tech", "territory_bonus"]
hits = [k for k in item_kinds if k in forbidden]
report("forbidden_absent", not hits, ",".join(hits))

# 币种：写定元宝，且必须在 currency.json 里。
currencies = [c["id"] for c in json.loads(read(currency_config))["currencies"]]
report("currency_yuanbao", shop["currency"] == "yuanbao" and "yuanbao" in currencies,
       "currency=%s" % shop["currency"])

# 标价只从配置来：服务端只读 item.PriceYuanbao，代码里没有第二套数字。
begin = service.find("public override async Task<BuyShopItemResponse> BuyShopItem(")
end = service.find("// 拒绝与重放都不带", begin)
body = service[begin:end] if begin >= 0 and end > begin else ""
report("price_from_config", "PriceYuanbao = item.PriceYuanbao" in body, "len=%d" % len(body))

# 这张卡一分不扣：BuyShopItem 里只有一次币种调用，而且是只读的 AllAsync。
report("no_charge", body.count("currency.") == 1 and "AllAsync" in body
       and "AddAsync" not in body and "RawWriteAsync" not in body,
       "currency_calls=%d" % body.count("currency."))

# 不写库：入口与判定里都没有一句 INSERT/UPDATE/DELETE。
writes = [w for w in ("INSERT", "UPDATE ", "DELETE") if w in api or w in body]
report("no_write", not writes, ",".join(writes))

# 请求体范围：只带角色号与商品编号；标价、数量、种类客户端带不进来。
buy_body = re.search(r"record BuyBody\(([^)]*)\)", api)
fields = [f.split()[-1] for f in buy_body.group(1).split(",")] if buy_body else []
report("request_scope", fields == ["CharacterId", "ItemId"],
       "fields=" + ",".join(fields))
report("req_fill_scope",
       "Account = account" in api and "ItemId = body.ItemId" in api
       and "Price" not in api.split("BuyShopItemRequest")[1].split("deadline")[0],
       "grpc_fill")

# 路由只有一条，别处不许再开一个商城口。
routes = re.findall(r'MapPost\("(/shop/[a-z/]+)"', api)
report("single_route", routes == ["/shop/buy"], ",".join(routes))
report("route_mapped", "ShopApi.Map(app, tokens, config)" in files[gateway_cs], "gateway")

# 这张卡不发货：回包恒 delivered=false，代码里没有一处写 true。
report("not_delivered",
       "Delivered = false" in body and "Delivered = true" not in service,
       "delivered_true=%d" % service.count("Delivered = true"))

# 只读入口：--shop-list 不连库，--shop-state 连库读余额。
report("readonly_entry",
       '"--shop-list"' in files[program_cs] and '"--shop-state"' in files[program_cs],
       "program")
report("config_default", "DefaultShopTable" in files[config_cs], "character_config")
PY

grep -q '^FAIL ' "$TMP/static.txt" || true
while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static.txt"
if ! grep -qE '^(PASS|FAIL) ' "$TMP/static.txt"; then
  bad "静态断言没跑起来"; tail -12 "$TMP/static.txt" | detail
fi

# ---------------------------------------------------------------- 3. 只读入口

echo "== 3. 只读入口与坏清单 =="
if "$DOTNET" "$CHARACTER_DLL" --shop-list > "$TMP/list.txt" 2>&1; then
  if grep -q 'shop_list_state=ok version=1 currency=yuanbao items=18 kinds=18 delivered_kinds=0' "$TMP/list.txt"; then
    ok "清单读得出来：18 种、只收元宝、发货一种都没接"
  else
    bad "清单头部不对：$(head -1 "$TMP/list.txt")"
  fi
  items=$(grep -c '^shop_item=' "$TMP/list.txt" || true)
  [ "$items" = "18" ] && ok "清单 18 条商品" || bad "清单商品条数不是 18：$items"
else
  bad "清单读取失败"; tail -5 "$TMP/list.txt" | detail
fi

# 五类坏清单都必须起不来：种类表外、禁售、币种不是元宝、标价 0、编号重复。
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
base = json.load(open(os.path.join(root, "config/shop.json"), encoding="utf-8"))
cases = {
    "kind_unknown": lambda j: j["items"][0].__setitem__("kind", "unknown_kind"),
    "kind_forbidden": lambda j: j["items"][0].__setitem__("kind", "gold"),
    "currency_gold": lambda j: j.__setitem__("currency", "gold"),
    "price_zero": lambda j: j["items"][0].__setitem__("price_yuanbao", 0),
    "dup_id": lambda j: j["items"][1].__setitem__("id", j["items"][0]["id"]),
}
for name, mutate in cases.items():
    doc = copy.deepcopy(base)
    mutate(doc)
    with open(os.path.join(tmp, "bad_%s.json" % name), "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False)
PY

for case in kind_unknown kind_forbidden currency_gold price_zero dup_id; do
  out="$("$DOTNET" "$CHARACTER_DLL" --shop-list --shop-config "$TMP/bad_$case.json" 2>&1)"
  code=$?
  if [ "$code" = "2" ] && printf '%s' "$out" | grep -q 'config_error='; then
    ok "坏清单起不来：$case"
  else
    bad "坏清单没挡住：$case（退出码 $code）"
    printf '%s\n' "$out" | head -3 | detail
  fi
done

# ---------------------------------------------------------------- 4. 真网关段

echo "== 4. 真网关段 =="
if [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （跳过：没给 MMO_P1_5C1_GATEWAY / MMO_P1_5C1_TOKEN / MMO_P1_5C1_CHARACTER）\n'
  printf '       %s\n' "MMO_P1_5C1_GATEWAY=http://127.0.0.1:61000 MMO_P1_5C1_TOKEN=<令牌> MMO_P1_5C1_CHARACTER=<角色号> bash tools/check/check-p1-5c1.sh"
else
  yuanbao_of() {
    printf '%s' "$1" | python3 -c 'import json,sys
try:
    d = json.load(sys.stdin)
except Exception:
    print(""); raise SystemExit
for b in d.get("balances", []):
    if b.get("kind") == "yuanbao":
        print(b.get("amount")); break
else:
    print("")'
  }

  buy() {
    curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' \
      -X POST "$GATEWAY/shop/buy" -H "Authorization: Bearer $TOKEN" \
      -H 'Content-Type: application/json' -d "$1" 2>&1
  }

  before="$(curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
    "$GATEWAY/currency?character_id=$CHARACTER" 2>&1)"
  before_yuanbao="$(yuanbao_of "$before")"

  # 无令牌
  resp="$(curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$GATEWAY/shop/buy" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$CHARACTER,\"item_id\":\"shop_protect_ticket_1\"}" 2>&1)"
  [ "$(code_of "$resp")" = "401" ] && ok "无令牌：401" || { bad "无令牌不是 401（$(code_of "$resp")）"; }

  # 坏请求：没有商品编号
  resp="$(buy "{\"character_id\":$CHARACTER}")"
  [ "$(code_of "$resp")" = "400" ] && ok "没有商品编号：400 bad_request" \
    || { bad "坏请求不是 400（$(code_of "$resp")）"; }

  # 角色不属于这个账号
  resp="$(buy "{\"character_id\":99999999,\"item_id\":\"shop_protect_ticket_1\"}")"
  [ "$(code_of "$resp")" = "404" ] && ok "角色不是这个账号的：404 character_not_found" \
    || { bad "归属复核没挡住（$(code_of "$resp")）"; }

  # 清单外：拒绝，且不动元宝
  resp="$(buy "{\"character_id\":$CHARACTER,\"item_id\":\"shop_not_exists\"}")"
  body="$(body_of "$resp")"
  if [ "$(expect_json "$body" ok=false reason=not_in_list charged_yuanbao=0 price_yuanbao=0 state=not_in_list)" = "ok" ]; then
    ok "清单外商品：拒绝（not_in_list）且标价与扣款都是 0"
  else
    bad "清单外商品没按清单拒绝：$body"
  fi

  # 清单内：这张卡还没接发货，也不扣
  resp="$(buy "{\"character_id\":$CHARACTER,\"item_id\":\"shop_protect_ticket_1\"}")"
  body="$(body_of "$resp")"
  if [ "$(expect_json "$body" ok=false reason=delivery_pending kind=protect_ticket charged_yuanbao=0 delivered=false)" = "ok" ]; then
    ok "清单内商品：还没接发货，拒绝且不扣（delivery_pending）"
  else
    bad "清单内商品回包不对：$body"
  fi

  # 元宝余额前后相等：清单外那次一分没动
  after="$(curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
    "$GATEWAY/currency?character_id=$CHARACTER" 2>&1)"
  after_yuanbao="$(yuanbao_of "$after")"
  if [ -n "$before_yuanbao" ] && [ "$before_yuanbao" = "$after_yuanbao" ]; then
    ok "两次请求前后元宝相等（$before_yuanbao）"
  else
    bad "元宝被动了：before=${before_yuanbao:-?} after=${after_yuanbao:-?}"
  fi
fi

finish "P1-5c1（server）"
