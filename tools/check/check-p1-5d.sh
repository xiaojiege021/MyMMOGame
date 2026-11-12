#!/usr/bin/env bash
# tools/check/check-p1-5d.sh —— P1-5d 充值订单只发一次（方案 12 章 / 技术架构 6 节 /
# 开发架构第 14 节第 49 行）。
#
# 完成标准：同一笔内部订单回调两次，元宝只加一次。
#
#   bash tools/check/check-p1-5d.sh             编译 + 静态 + 只读入口 + 坏配置（不连库）
#   MMO_P1_5D_GATEWAY=... MMO_P1_5D_TOKEN=... MMO_P1_5D_CHARACTER=<号> \
#     bash tools/check/check-p1-5d.sh           再加真网关段（建单 → 回调 → 重放）
#   MMO_P1_5D_SKIP_BUILD=1 ...                  跳过编译
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY="${MMO_P1_5D_GATEWAY:-}"
TOKEN="${MMO_P1_5D_TOKEN:-}"
CHARACTER="${MMO_P1_5D_CHARACTER:-0}"
PRODUCT="$(python3 -c 'import json; print(json.load(open("config/recharge.json", encoding="utf-8"))["products"][0]["id"])')"
AMOUNT="$(python3 -c 'import json; print(json.load(open("config/recharge.json", encoding="utf-8"))["products"][0]["yuanbao"])')"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

# 从「key=value key=value」日志行里按空格切词取一个字段（别用贪婪 sed）。
field_of() {
  printf '%s\n' "$1" | python3 -c 'import sys
want = sys.argv[1] + "="
for line in sys.stdin:
    for word in line.split():
        if word.startswith(want):
            print(word[len(want):]); raise SystemExit
print("")' "$2"
}

echo "== 1. 编译 =="
if [ "${MMO_P1_5D_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_P1_5D_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/Character/Character.csproj -c Release -v quiet > "$TMP/build.log" 2>&1 \
     && "$DOTNET" build src/Gateway/Gateway.csproj -c Release -v quiet >> "$TMP/build.log" 2>&1; then
  ok "编译 Character + Gateway"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言 =="
python3 - "$TMP/static.txt" <<'PY'
import json, os, re, sys

out = open(sys.argv[1], "w", encoding="utf-8")

def report(key, cond, info=""):
    out.write(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info) + "\n")

def read(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

cfg = json.loads(read("config/recharge.json"))
products = cfg.get("products") or []
ids = [p.get("id") for p in products]

report("config", cfg.get("schema") == "recharge-table/1" and cfg.get("version", 0) >= 1
       and len(products) >= 1, "schema/version/products")
report("products", len(ids) == len(set(ids))
       and all(p.get("yuanbao", 0) > 0 and p.get("rmb_cents", 0) > 0
               and str(p.get("name") or "").strip() for p in products),
       "编号重复 / 元宝或金额非正 / 缺 name")

sql = read("db/character/recharge.sql")
report("sql", "create table if not exists \"角色\".recharge_order" in sql.lower()
       and "primary key" in sql.lower() and "status in ('pending', 'paid')" in sql.lower()
       and "recharge_order_paid_at_matches" in sql, "建表 / 主键 / 状态 / paid_at 约束")
report("main_migration", "recharge_order" in read("db/character/character.sql"),
       "主迁移里没有同一份 DDL")

table_cs = read("src/Character/RechargeTable.cs")
repo = read("src/Character/RechargeRepository.cs")
svc = read("src/Character/CharacterGrpcService.cs")
proto = read("proto/mmo/character.proto")
program = read("src/Character/Program.cs")
gateway = read("src/Gateway/GatewayProcess.cs")
api = read("src/Gateway/RechargeApi.cs")

report("proto", "rpc CreateRechargeOrder" in proto and "rpc SettleRechargeOrder" in proto
       and "paid_at_ms" in proto and "duplicate" in proto, "rpc / 回包字段")
report("service", "CreateRechargeOrder" in svc and "SettleRechargeOrder" in svc
       and "rechargeTable.Find" in svc and "product_not_found" in svc, "建单 / 结算 / 认商品")
report("repo_txn", "BeginTransactionAsync" in repo and "grant_txn" in repo
       and "TxnPrefix" in repo, "事务 / 幂等号")
report("repo_settle", "for update of o" in repo and "status = 'paid', paid_at = now()" in repo
       and "on conflict (character_id, kind) do update" in repo, "锁单 / 改 paid / 加元宝")
report("repo_duplicate", "duplicate" in repo and "paid" in repo, "已 paid 要回 duplicate")
report("readonly", all(k in program for k in
       ("--recharge-list", "--recharge-config", "--recharge-state")), "只读 / 核对入口")
report("config_wired", "recharge_table" in read("src/Character/CharacterConfig.cs"), "没接配置")
report("gateway", '"/recharge/order"' in api and '"/recharge/callback"' in api
       and "RechargeApi.Map" in gateway, "路由 / 注册")

code = table_cs + repo + svc + api
report("no_hardcoded", not re.search(r"\b(60|300|600|3000)\b", code), "代码里写死了元宝数 / 金额")
report("no_price_in_request", "RmbCents" in table_cs and "RmbCents" not in svc,
       "金额不该出现在请求处理里")
out.close()
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static.txt"
grep -qE '^(PASS|FAIL) ' "$TMP/static.txt" || { bad "静态断言没跑起来"; tail -12 "$TMP/static.txt" | detail; }

echo "== 3. 只读入口与坏配置 =="
LIST="$("$DOTNET" "$CHARACTER_DLL" --recharge-list 2>&1)"
if printf '%s' "$LIST" | grep -q '^recharge_list_state=ok' \
   && printf '%s' "$LIST" | grep -q "product=${PRODUCT}:"; then
  ok "充值商品表读得出来：$(printf '%s' "$LIST" | head -1 | cut -c1-88)"
else
  bad "充值商品表读不出来：$LIST"
fi

python3 - "$TMP" <<'PY'
import json, os, sys

tmp = sys.argv[1]
base = json.load(open("config/recharge.json", encoding="utf-8"))

def case(name, mutate):
    d = json.loads(json.dumps(base))
    mutate(d)
    with open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False)

case("bad_schema", lambda d: d.update(schema="wrong-table/1"))
case("zero_version", lambda d: d.update(version=0))
case("no_products", lambda d: d.pop("products", None))
case("empty_products", lambda d: d.update(products=[]))
case("empty_id", lambda d: d["products"][0].update(id=""))
case("dup_id", lambda d: d["products"].append(dict(d["products"][0])))
case("zero_yuanbao", lambda d: d["products"][0].update(yuanbao=0))
case("neg_yuanbao", lambda d: d["products"][0].update(yuanbao=-1))
case("zero_cents", lambda d: d["products"][0].update(rmb_cents=0))
case("empty_name", lambda d: d["products"][0].update(name=""))
PY

for bad_case in bad_schema zero_version no_products empty_products empty_id \
                dup_id zero_yuanbao neg_yuanbao zero_cents empty_name; do
  out="$("$DOTNET" "$CHARACTER_DLL" --recharge-list \
        --recharge-config "$TMP/bad-$bad_case.json" 2>&1)"
  assert_bad_config "$out" "坏配置 ${bad_case}：起不来（$(printf '%s' "$out" | head -1 | cut -c1-44)…）"
done

echo "== 4. 真网关段（建单 → 回调 → 同一笔再来一次）=="
if [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （跳过：没给 MMO_P1_5D_GATEWAY / _TOKEN / _CHARACTER）\n'
else
  post() {
    curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" \
      -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$2" 2>&1
  }
  get() { curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" "$1" 2>&1; }
  create() { post "$GATEWAY/recharge/order" "{\"character_id\":$CHARACTER,\"product_id\":\"$1\"}"; }
  callback() { post "$GATEWAY/recharge/callback" "{\"order_id\":\"$1\"}"; }
  money() { get "$GATEWAY/currency?character_id=$CHARACTER"; }
  state() { "$DOTNET" "$CHARACTER_DLL" --recharge-state "$1" 2>&1; }
  check() {
    if [ "$(expect_json "$(body_of "$1")" $2)" = "ok" ]; then ok "$3"; else bad "$3：$1"; fi
  }
  yuanbao_of() {
    printf '%s' "$1" | python3 -c 'import json, sys
try: d = json.load(sys.stdin)
except Exception: print(""); raise SystemExit
print(next((b.get("amount") for b in d.get("balances", []) if b.get("kind") == "yuanbao"), ""))'
  }

  before="$(yuanbao_of "$(money)")"

  # ① 建单：60 元宝那档，订单 pending，余额一分不动。
  r="$(create "$PRODUCT")"; body="$(body_of "$r")"; order="$(json_field "$body" order_id)"
  check "$r" "ok=true status=pending yuanbao=$AMOUNT product_id=$PRODUCT" "建单：pending、$AMOUNT 元宝"
  [ "$(yuanbao_of "$(money)")" = "$before" ] \
    && ok "建单不动余额（$before）" || bad "建单就把钱加了"

  # ② 回调：元宝 +60，订单变 paid，paid_at 有值。
  r="$(callback "$order")"
  check "$r" "ok=true status=paid yuanbao=$AMOUNT duplicate=false" "回调：发 $AMOUNT 元宝、订单 paid"
  [ "$(yuanbao_of "$(money)")" = "$((before + AMOUNT))" ] \
    && ok "余额 +$AMOUNT（$before→$((before + AMOUNT))）" || bad "余额不对：$(yuanbao_of "$(money)")"
  st="$(state "$order")"
  [ "$(field_of "$st" status)" = "paid" ] && [ "$(field_of "$st" paid)" = "true" ] \
    && ok "订单状态从库里读回：paid / paid=true" || bad "订单状态不对：$st"
  [ "$(field_of "$st" paid_at_ms)" -gt 0 ] \
    && ok "paid_at 落库（$(field_of "$st" paid_at_ms)）" || bad "paid_at 没落库"

  # ③ 同一笔再来一次：整笔不做，一分不加。
  r="$(callback "$order")"
  check "$r" "ok=false reason=duplicate duplicate=true balance=$((before + AMOUNT))" \
        "同一笔回调第二次：duplicate，不再发"
  [ "$(yuanbao_of "$(money)")" = "$((before + AMOUNT))" ] \
    && ok "重放后余额不变（仍 $((before + AMOUNT))）" || bad "重放把钱又加了"
  st="$(state "$order")"
  [ "$(field_of "$st" balance)" = "$((before + AMOUNT))" ] \
    && ok "库里读回余额仍是 $((before + AMOUNT))" || bad "库里余额不对：$st"

  # ④ 不存在的订单 / 商品编号不对。
  check "$(callback "rc_no_such_order")" "ok=false reason=order_not_found" "不存在的订单：order_not_found"
  r="$(create no_such_product)"
  check "$r" "ok=false reason=product_not_found" "商品编号不对：product_not_found"
  [ "$(yuanbao_of "$(money)")" = "$((before + AMOUNT))" ] \
    && ok "拒绝路径没有动余额" || bad "拒绝路径动了余额"
fi

finish "P1-5d（server）"
