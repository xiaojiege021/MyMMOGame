#!/usr/bin/env bash
# P1-5c2 核对：保护券绑定发货（方案 9.2 / 12.1、开发架构第 14 节第 42 行）。
#
# 完成标准：买到的保护券不能交易。
#
# 三种模式：
#   bash tools/check/check-p1-5c2.sh
#       编译 + 静态 + 只读入口 + 坏清单，不连库不连服（Mac 上就能全跑）
#   MMO_P1_5C2_GATEWAY=http://127.0.0.1:61000 MMO_P1_5C2_TOKEN=<令牌> \
#     MMO_P1_5C2_CHARACTER=<角色号> [MMO_P1_5C2_HAS_DB=1] bash tools/check/check-p1-5c2.sh
#       再加真网关段：买一张保护券（扣款、入包、绑定）、can-trade 与上架都拒、
#       没接发货的种类仍 delivery_pending、清单外仍 not_in_list、
#       余额不足不扣不发。带 HAS_DB=1 时额外用只读入口与 raw 写核对库里的状态。
#   MMO_P1_5C2_SKIP_BUILD=1 bash tools/check/check-p1-5c2.sh
#       跳过编译（已经 build 过时用）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY="${MMO_P1_5C2_GATEWAY:-}"
TOKEN="${MMO_P1_5C2_TOKEN:-}"
CHARACTER="${MMO_P1_5C2_CHARACTER:-0}"
HAS_DB="${MMO_P1_5C2_HAS_DB:-0}"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

# ---------------------------------------------------------------- 1. 编译

if [ "${MMO_P1_5C2_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_P1_5C2_SKIP_BUILD=1）\n'
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
items_config = os.path.join(root, "config/items.json")
enhance_config = os.path.join(root, "config/enhance.json")
table_cs = os.path.join(root, "src/Character/ShopTable.cs")
service_cs = os.path.join(root, "src/Character/CharacterGrpcService.cs")
grant_cs = os.path.join(root, "src/Character/GrantRepository.cs")
api_cs = os.path.join(root, "src/Gateway/ShopApi.cs")
program_cs = os.path.join(root, "src/Character/Program.cs")
proto = os.path.join(root, "proto/mmo/character.proto")
gateway_cs = os.path.join(root, "src/Gateway/GatewayProcess.cs")

def read(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

files = {p: read(p) for p in
         [shop_config, currency_config, items_config, enhance_config,
          table_cs, service_cs, grant_cs, api_cs, program_cs, proto, gateway_cs]}
report("files", all(v for v in files.values()),
       "missing " + ",".join(os.path.basename(k) for k, v in files.items() if not v))

shop = json.loads(read(shop_config))
items = {i["id"]: i for i in json.loads(read(items_config))["items"]}
enhance = json.loads(read(enhance_config))
table = files[table_cs]
service = files[service_cs]
api = files[api_cs]
proto_text = files[proto]

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

forbidden = ["gold", "enhance_stone", "transfer_stone", "crystal", "spirit_stone",
             "pill", "ore", "herb", "equip_blueprint", "equip", "attr_item",
             "revive_coin", "fatigue", "dungeon_count", "weekly_share",
             "realm_speed", "guild_tech", "territory_bonus"]
hits = [k for k in item_kinds if k in forbidden]
report("forbidden_absent", not hits, ",".join(hits))

currencies = [c["id"] for c in json.loads(read(currency_config))["currencies"]]
report("currency_yuanbao", shop["currency"] == "yuanbao" and "yuanbao" in currencies,
       "currency=%s" % shop["currency"])

# 这张卡只接保护券一种发货。后面的卡还会往白名单里加种类、接更多发货，
# 所以这里只钉「保护券在白名单里 / 有一条保护券发货、且都不重复」，
# 不写死当时的全集——写死过一次，接到第五种发货就整段假失败。
delivered_code = re.findall(r'"([a-z_]+)"', table.split("DeliveredKinds")[1].split("}")[0])
report("delivered_kinds_scope",
       "protect_ticket" in delivered_code
       and len(delivered_code) == len(set(delivered_code)),
       "code=%s" % ",".join(delivered_code))

filled = [i["kind"] for i in shop["items"] if (i.get("delivery_item_id") or "")]
report("delivery_only_ticket",
       "protect_ticket" in filled and len(filled) == len(set(filled)),
       ",".join(filled))
half = [i["kind"] for i in shop["items"]
        if not (i.get("delivery_item_id") or "") and (i.get("delivery_count") or 0) != 0]
report("no_half_delivery", not half, ",".join(half))
empty_count = [i["kind"] for i in shop["items"]
               if (i.get("delivery_item_id") or "") and (i.get("delivery_count") or 0) < 1]
report("delivery_count_positive", not empty_count, ",".join(empty_count))

# 方案 9.2 / 12.1：商城与通行证发的是同一种券，所以发货物必须就是 enhance 那个编号。
ticket = next(i for i in shop["items"] if i["kind"] == "protect_ticket")
report("ticket_same_coupon",
       ticket["delivery_item_id"] == enhance["protect_item_id"] == "ticket_protect",
       "shop=%s enhance=%s" % (ticket["delivery_item_id"], enhance["protect_item_id"]))
goods = items.get(ticket["delivery_item_id"], {})
report("ticket_item_bound",
       goods.get("kind") == "voucher" and goods.get("bind") == "always"
       and goods.get("stack_max", 0) >= ticket["delivery_count"],
       "kind=%s bind=%s stack=%s" % (goods.get("kind"), goods.get("bind"), goods.get("stack_max")))
report("ticket_one_per_buy", ticket["delivery_count"] == 1, "count=%s" % ticket["delivery_count"])

# 成交段的边界：扣款与入包在同一笔事务里，别处不许再扣。
begin = service.find("public override async Task<BuyShopItemResponse> BuyShopItem(")
end = service.find("BuyShopItemResponse ShopRejected(", begin)
if end < 0:
    end = service.find("// 拒绝与重放都不带", begin)
body = service[begin:end] if begin >= 0 and end > begin else ""
report("section_found", len(body) > 2000, "len=%d" % len(body))

report("one_transaction", body.count("grants.ApplyAsync") == 1,
       "apply=%d" % body.count("grants.ApplyAsync"))
# 扣款与入包必须是同一次 ApplyAsync 的实参：从调用点到它的收尾之间同时有两条变更。
call_tail = body[body.find("grants.ApplyAsync"):] if "grants.ApplyAsync" in body else ""
call_end = call_tail.find("context.CancellationToken")
charge_at = call_tail.find("CurrencyDelta")
goods_at = call_tail.find("ItemDelta")
report("charge_and_goods_same_txn",
       0 <= call_end and 0 <= charge_at < call_end and 0 <= goods_at < call_end,
       "end=%d charge=%d goods=%d" % (call_end, charge_at, goods_at))
report("no_second_charge",
       "currency.AddAsync" not in body and "RawWriteAsync" not in body,
       "direct_charge=%d" % (body.count("currency.AddAsync") + body.count("RawWriteAsync")))
report("charge_negative_price",
       "Delta = -item.PriceYuanbao" in body, "delta_from_config")
report("bound_from_config", "Bound = goods.BindsOnArrival" in body, "bind_rule")

# 未成交一律不给生效值：charged / delivered 只在成功那一支写。
report("charged_zero_on_reject", body.count("Delivered = true") == 1,
       "delivered_true=%d" % body.count("Delivered = true"))
report("reject_codes",
       all(c in body for c in ("not_in_list", "delivery_pending", "not_enough_yuanbao", "bag_full")),
       "codes")
report("txn_guard", "txn_missing" in body and "MaxTxnLength" in body, "txn")
report("price_from_config", "PriceYuanbao = item.PriceYuanbao" in body, "price")

# 请求体范围：只带角色号与商品编号；数量、标价、绑定态客户端带不进来。
buy_body = re.search(r"record BuyBody\(([^)]*)\)", api)
fields = [f.split()[-1] for f in buy_body.group(1).split(",")] if buy_body else []
report("request_scope", fields == ["CharacterId", "ItemId"], "fields=" + ",".join(fields))
report("txn_from_gateway", '"shop_buy:" + Guid.NewGuid()' in api and "TxnId = txnId" in api, "gateway")
report("gateway_no_write",
       not [w for w in ("INSERT", "UPDATE ", "DELETE") if w in api], "api_writes")

routes = re.findall(r'MapPost\("(/shop/[a-z/]+)"', api)
report("single_route", routes == ["/shop/buy"], ",".join(routes))
report("route_mapped", "ShopApi.Map(app, tokens, config)" in files[gateway_cs], "gateway")

# 回包要有「发到哪、绑没绑、重不重」，否则客户端只能自己猜。
reply = proto_text.split("message BuyShopItemResponse")[1].split("}")[0]
report("reply_fields",
       all(f in reply for f in ("delivery_item_id = 13", "bag_slot = 14",
                                "bound = 15", "duplicate = 16")),
       "reply")

# 只读入口：清单、库里的券与余额、商城成交笔数。
report("readonly_entry",
       all(k in files[program_cs] for k in ('"--shop-list"', '"--shop-state"', '"--shop-ledger"')),
       "program")
report("ledger_prefix", "shop_buy:" in files[grant_cs] or "shop\\_buy:%" in files[grant_cs], "grant")
PY

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
  # 已接种类数与可发货物数会随后面的卡增长，只钉「两个数字相等且至少有一种」，
  # 不写死 delivered_kinds=1 / deliverable_items=1。
  if [ "$(LIST="$TMP/list.txt" python3 -c 'import os, re
line = open(os.environ["LIST"], encoding="utf-8").readline()
def f(k):
    m = re.search(r"\b" + k + r"=(\d+)", line)
    return int(m.group(1)) if m else -1
d, v = f("delivered_kinds"), f("deliverable_items")
print("ok" if "shop_list_state=ok" in line and d >= 1 and d == v else "bad")')" = "ok" ]; then
    ok "清单读得出来：已接发货种类与可发货物一一对应"
  else
    bad "清单头部不对：$(head -1 "$TMP/list.txt")"
  fi
  items=$(grep -c '^shop_item=' "$TMP/list.txt" || true)
  [ "$items" = "18" ] && ok "清单 18 条商品" || bad "清单商品条数不是 18：$items"
  if grep -q 'shop_item=shop_protect_ticket_1 .* delivery=ticket_protectx1 ' "$TMP/list.txt"; then
    ok "保护券的发货物是同一种券、一次一张"
  else
    bad "保护券的发货物不对：$(grep 'shop_protect_ticket_1' "$TMP/list.txt" | head -1)"
  fi
  pending=$(grep -c ' delivery=pending ' "$TMP/list.txt" || true)
  filled_n=$(python3 -c 'import json; d=json.load(open("config/shop.json", encoding="utf-8")); print(sum(1 for i in d["items"] if i.get("delivery_item_id")))')
  if [ "$filled_n" -ge 1 ] && [ "$((pending + filled_n))" -eq "$items" ]; then
    ok "清单 ${items} 条 = ${filled_n} 条已接发货 + ${pending} 条 pending（不钉当时的种类数）"
  else
    bad "pending 与已接发货对不上：pending=$pending filled=$filled_n items=$items"
  fi
else
  bad "清单读取失败"; tail -5 "$TMP/list.txt" | detail
fi

# 六类坏清单都必须起不来：别处的发货物、保护券留空、一次两张、
# 发货物不在物品表、还没接发货却填了发货物、只填数量不填物品。
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
base = json.load(open(os.path.join(root, "config/shop.json"), encoding="utf-8"))

def at(j, kind):
    return next(i for i, x in enumerate(j["items"]) if x["kind"] == kind)

cases = {
    "ticket_other_goods": lambda j: j["items"][at(j, "protect_ticket")].__setitem__(
        "delivery_item_id", "stone_transfer"),
    "ticket_empty": lambda j: j["items"][at(j, "protect_ticket")].__setitem__(
        "delivery_item_id", ""),
    "ticket_count_two": lambda j: j["items"][at(j, "protect_ticket")].__setitem__(
        "delivery_count", 2),
    "ticket_unknown_goods": lambda j: j["items"][at(j, "protect_ticket")].__setitem__(
        "delivery_item_id", "no_such_item"),
    "red_filled": lambda j: (j["items"][at(j, "red_talisman")].__setitem__(
        "delivery_item_id", "ticket_protect"), j["items"][at(j, "red_talisman")].__setitem__(
        "delivery_count", 1)),
    "outfit_half": lambda j: j["items"][at(j, "outfit")].__setitem__("delivery_count", 1),
}
for name, mutate in cases.items():
    doc = copy.deepcopy(base)
    mutate(doc)
    with open(os.path.join(tmp, "bad_%s.json" % name), "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False)
PY

for case in ticket_other_goods ticket_empty ticket_count_two ticket_unknown_goods red_filled outfit_half; do
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
  printf '       （跳过：没给 MMO_P1_5C2_GATEWAY / MMO_P1_5C2_TOKEN / MMO_P1_5C2_CHARACTER）\n'
  printf '       %s\n' "MMO_P1_5C2_GATEWAY=http://127.0.0.1:61000 MMO_P1_5C2_TOKEN=<令牌> MMO_P1_5C2_CHARACTER=<角色号> bash tools/check/check-p1-5c2.sh"
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

  # 背包里保护券合计几张。只认券，不认别的格。
  tickets_of() {
    printf '%s' "$1" | python3 -c 'import json,sys
try:
    d = json.load(sys.stdin)
except Exception:
    print(-1); raise SystemExit
n = 0
for s in d.get("slots", []):
    if s.get("item_id") == "ticket_protect":
        n += int(s.get("count") or 0)
print(n)'
  }

  buy() {
    curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' \
      -X POST "$GATEWAY/shop/buy" -H "Authorization: Bearer $TOKEN" \
      -H 'Content-Type: application/json' -d "$1" 2>&1
  }

  bag() {
    curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
      "$GATEWAY/inventory?character_id=$CHARACTER" 2>&1
  }

  money() {
    curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
      "$GATEWAY/currency?character_id=$CHARACTER" 2>&1
  }

  before_yuanbao="$(yuanbao_of "$(money)")"
  before_tickets="$(tickets_of "$(bag)")"

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
  if [ "$(expect_json "$body" ok=false reason=not_in_list charged_yuanbao=0 price_yuanbao=0)" = "ok" ]; then
    ok "清单外商品：not_in_list，标价与扣款都是 0"
  else
    bad "清单外商品没按清单拒绝：$body"
  fi

  # 还没接发货的种类：从清单里动态挑一条，不写死「红符」——
  # 红符从 P1-5c3 起就真发货了，写死会在后面的卡里假失败。
  pending_goods="$(python3 -c 'import json; d=json.load(open("config/shop.json", encoding="utf-8")); g=next((i for i in d["items"] if not i.get("delivery_item_id")), None); print((g["id"] + " " + g["kind"]) if g else "")')"
  pending_id="${pending_goods%% *}"
  pending_kind="${pending_goods##* }"
  resp="$(buy "{\"character_id\":$CHARACTER,\"item_id\":\"$pending_id\"}")"
  body="$(body_of "$resp")"
  if [ -n "$pending_id" ] \
     && [ "$(expect_json "$body" ok=false reason=delivery_pending kind="$pending_kind" charged_yuanbao=0 delivered=false)" = "ok" ]; then
    ok "未接发货的种类（${pending_id}）：delivery_pending，不扣不发"
  else
    bad "未接发货的种类回包不对：$body"
  fi

  # 买一张保护券：扣 50、入包一张、到账即绑
  resp="$(buy "{\"character_id\":$CHARACTER,\"item_id\":\"shop_protect_ticket_1\"}")"
  body="$(body_of "$resp")"
  slot="$(printf '%s' "$body" | python3 -c 'import json,sys
try:
    d = json.load(sys.stdin)
except Exception:
    print(0); raise SystemExit
print(d.get("bag_slot") or 0)')"
  after_buy_yuanbao="$(printf '%s' "$body" | python3 -c 'import json,sys
try:
    d = json.load(sys.stdin)
except Exception:
    print(""); raise SystemExit
print(d.get("balance", ""))')"
  if [ "$(expect_json "$body" ok=true reason= delivered=true charged_yuanbao=50 bound=true delivery_item_id=ticket_protect state=delivered duplicate=false)" = "ok" ] \
     && [ "$slot" != "0" ]; then
    ok "买一张保护券：扣 50、入包第 $slot 格、到账即绑"
  else
    bad "买保护券回包不对：$body"
  fi
  if [ -n "$before_yuanbao" ] && [ -n "$after_buy_yuanbao" ] \
     && [ "$after_buy_yuanbao" = "$((before_yuanbao - 50))" ]; then
    ok "回包余额就是扣完的事实值（$before_yuanbao → $after_buy_yuanbao）"
  else
    bad "余额不对：before=${before_yuanbao:-?} reply=${after_buy_yuanbao:-?}"
  fi

  # 券到手了：背包里多了一张绑定的券
  after_tickets="$(tickets_of "$(bag)")"
  if [ "$before_tickets" -ge 0 ] && [ "$after_tickets" = "$((before_tickets + 1))" ]; then
    ok "背包里保护券多了一张（$before_tickets → $after_tickets）"
  else
    bad "背包里券的数量不对：${before_tickets:-?} → ${after_tickets:-?}"
  fi

  if [ "$slot" != "0" ]; then
    # 这一格不能交易：can-trade 给 bound_item
    resp="$(curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$GATEWAY/inventory/can-trade" \
      -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CHARACTER,\"slot\":$slot}" 2>&1)"
    body="$(body_of "$resp")"
    if [ "$(expect_json "$body" tradable=false reason=bound_item)" = "ok" ]; then
      ok "这一格不能直接交易：tradable=false / bound_item"
    else
      bad "can-trade 没拒绑定券：$body"
    fi

    # 上架也被拒：电商那侧看的是同一个 bound
    resp="$(curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$GATEWAY/auction/list" \
      -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CHARACTER,\"slot\":$slot,\"count\":1,\"price_gold\":10}" 2>&1)"
    body="$(body_of "$resp")"
    if [ "$(expect_json "$body" ok=false reason=bound_item)" = "ok" ]; then
      ok "拿这张券上架被拒：bound_item"
    else
      bad "上架没拒绑定券：$body"
    fi
  fi

  # 余额不足：不扣、不发。带库环境时用 raw 写把元宝压到 30，事后写回。
  if [ "$HAS_DB" = "1" ] && [ -n "$before_yuanbao" ]; then
    keep=$((before_yuanbao - 50))
    "$DOTNET" "$CHARACTER_DLL" --raw-currency-write "$CHARACTER" yuanbao 30 > "$TMP/raw1.txt" 2>&1
    resp="$(buy "{\"character_id\":$CHARACTER,\"item_id\":\"shop_protect_ticket_1\"}")"
    body="$(body_of "$resp")"
    poor_tickets="$(tickets_of "$(bag)")"
    if [ "$(expect_json "$body" ok=false reason=not_enough_yuanbao charged_yuanbao=0 delivered=false)" = "ok" ] \
       && [ "$poor_tickets" = "$after_tickets" ] \
       && [ "$(yuanbao_of "$(money)")" = "30" ]; then
      ok "元宝不够：not_enough_yuanbao，钱和券都没动"
    else
      bad "余额不足这条不对：$body / tickets=${poor_tickets:-?}"
    fi
    "$DOTNET" "$CHARACTER_DLL" --raw-currency-write "$CHARACTER" yuanbao "$keep" > "$TMP/raw2.txt" 2>&1

    # 库里独立读回：券绑着、余额是写回的那个数、商城成交一笔
    "$DOTNET" "$CHARACTER_DLL" --shop-state "$CHARACTER" > "$TMP/state.txt" 2>&1
    if grep -q "balance=$keep " "$TMP/state.txt" && grep -q "tickets=$after_tickets " "$TMP/state.txt" \
       && grep -q 'ticket_bound=true' "$TMP/state.txt"; then
      ok "库里独立读回：余额 $keep、券 $after_tickets 张且都是绑定的"
    else
      bad "库里状态不对：$(head -1 "$TMP/state.txt")"
    fi

    "$DOTNET" "$CHARACTER_DLL" --shop-ledger "$CHARACTER" > "$TMP/ledger.txt" 2>&1
    if grep -qE 'shop_ledger=ok .*orders=[1-9]' "$TMP/ledger.txt"; then
      ok "商城成交记进了事务表（$(grep -o 'orders=[0-9]*' "$TMP/ledger.txt")）"
    else
      bad "成交没记进事务表：$(head -1 "$TMP/ledger.txt")"
    fi
  else
    printf '       （跳过余额不足与库内读回：没给 MMO_P1_5C2_HAS_DB=1）\n'
  fi

  final_yuanbao="$(yuanbao_of "$(money)")"
  if [ -n "$final_yuanbao" ] && [ "$final_yuanbao" = "$((before_yuanbao - 50))" ]; then
    ok "整段跑完只扣了这一张的 50（$before_yuanbao → $final_yuanbao）"
  else
    bad "元宝总额不对：before=${before_yuanbao:-?} final=${final_yuanbao:-?}"
  fi
fi

finish "P1-5c2（server）"
