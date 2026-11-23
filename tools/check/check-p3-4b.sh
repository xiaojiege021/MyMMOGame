#!/usr/bin/env bash
# P3-4b 月叠与第二角色栏（方案 12.1 第 809 / 810 行、开发架构 11 节 P3-4 / 13 节 / 14 节第 69 行）。
#   bash tools/check/check-p3-4b.sh
#   MMO_P3_4B_HAS_DB=1 MMO_GAMEDB='Host=...' bash ...   加真服真库端到端
# 完成标准：月叠到账后背包里只有传送符和红符（没有蓝符、复活卷轴和经验道具）；
#           第二个角色的强化等级是 0；第二角色栏不共享装备 / 等级 / 境界，外观收藏可共享。
set -uo pipefail
# 本机设了代理时 .NET 会把 127.0.0.1 的明文 HTTP/2 请求也送进代理；curl 一律 --noproxy '*'。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
AGG_CHARGRPC="$(src_agg src/Character/CharacterGrpcService.cs)"; export AGG_CHARGRPC
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHARACTER_DLL="$CD"
HAS_DB="${MMO_P3_4B_HAS_DB:-0}"
TMP="$(mktemp -d)"
PIDS=""
# Windows 上 dotnet 重定向输出走 GBK；中文断言按平台转码。
utf8() {
  case "$(uname -s)" in
    MINGW*|MSYS*|WIN32*|CYGWIN*)
      python3 -c 'import sys; sys.stdout.buffer.write(
          sys.stdin.buffer.read().decode("gbk", "replace").encode("utf-8"))' ;;
    *) cat ;;
  esac
}
cleanup() { for p in $PIDS; do stop_pid "$p"; done; rm -rf "$TMP"; }
trap cleanup EXIT
jget() { python3 -c 'import json,sys
try:
    o=json.load(sys.stdin); v=o.get(sys.argv[1])
    print("" if v is None else ("true" if v is True else "false" if v is False else v))
except Exception:
    print("")' "$1"; }
# 月叠要发的两种符号与件数都从配置现取，脚本不写死编号与数字。
read -r TP_ITEM TP_COUNT RED_ITEM RED_COUNT DAYS < <(python3 -c 'import json
d=json.load(open("config/monthly_bundle.json",encoding="utf-8"))
g={i["kind"]:i for i in d["items"]}
print(g["teleport_talisman"]["item_id"], g["teleport_talisman"]["per_day"]*d["days"],
      g["red_talisman"]["item_id"], g["red_talisman"]["per_day"]*d["days"], d["days"])')
TP_ITEM="${TP_ITEM%$'\r'}"; TP_COUNT="${TP_COUNT%$'\r'}"
RED_ITEM="${RED_ITEM%$'\r'}"; RED_COUNT="${RED_COUNT%$'\r'}"; DAYS="${DAYS%$'\r'}"
MB_ID="$(python3 -c 'import json;print(json.load(open("config/monthly_bundle.json",encoding="utf-8"))["product_id"])')"
SLOT_ID="$(python3 -c 'import json;print(json.load(open("config/character_slot.json",encoding="utf-8"))["product_id"])')"

echo "== 1. 编译 =="
for proj in src/Shared/Shared.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj \
            src/Global/Global.csproj src/Battle/Battle.csproj src/Social/Social.csproj \
            src/World/World.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 ${n}：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done

echo "== 2. 静态断言 =="
python3 - "$ROOT" >"$TMP/static.out" 2>&1 <<'PY'
import hashlib, json, os, re, sys
root = sys.argv[1]
def src(p): return open(os.path.join(root, p), encoding="utf-8").read()
def cfg(p): return json.load(open(os.path.join(root, p), encoding="utf-8"))
def sha(p): return hashlib.sha256(open(os.path.join(root, p), "rb").read()).hexdigest()
checks = []
def ck(name, cond): checks.append((name, bool(cond)))
mb, slot = cfg("config/monthly_bundle.json"), cfg("config/character_slot.json")
shop, items_cfg, tal = cfg("config/shop.json"), cfg("config/items.json"), cfg("config/talisman.json")
items = {i["id"]: i for i in items_cfg["items"]}
shop_by_cat = {i["category"]: i for i in shop["items"]}
entries = {e["kind"]: e for e in mb["items"]}
ck("月叠配置 schema / version / days / product 指向月叠商品",
   mb["schema"] == "monthly-bundle-table/1" and mb["version"] > 0 and mb.get("pending") is True
   and mb["days"] == 30 and mb["product_id"] == shop_by_cat["monthly_bundle"]["id"])
ck("月叠只有传送符与红符两种，且两条都在",
   set(entries) == {"teleport_talisman", "red_talisman"} and len(mb["items"]) == 2)
ck("红符就是 talisman.json 那一个编号（不许另立第二种）",
   entries["red_talisman"]["item_id"] == tal["red"]["item_id"])
ck("传送符就是商城那条传送符商品发的同一件",
   entries["teleport_talisman"]["item_id"] == shop_by_cat["teleport_talisman"]["delivery"]["item_id"])
ck("月叠两件都是消耗品且到账即绑",
   all(items[e["item_id"]]["kind"] == "consumable" and items[e["item_id"]]["bind"] == "always"
       for e in mb["items"]))
ck("月叠每天几张不超过堆叠上限",
   all(1 <= e["per_day"] <= items[e["item_id"]]["stack_max"] for e in mb["items"]))
def keys(o):
    if isinstance(o, dict):
        for k, v in o.items():
            yield k
            yield from keys(v)
    elif isinstance(o, list):
        for v in o:
            yield from keys(v)
bad_words = ("gold", "stone", "spirit", "crystal", "blue", "revive", "scroll", "booster",
             "unbind", "equip", "fatigue", "count", "reward")
ck("月叠配置里长不出金币 / 灵石 / 材料 / 装备 / 次数 / 卷轴 / 经验道具这些键",
   not [k for k in keys(mb)
        if k not in ("item_id", "items", "per_day") and any(w in k.lower() for w in bad_words)])
ck("月叠是「多物品发货」：清单里写成 monthly_bundle 状态，不许带假发货物",
   shop_by_cat["monthly_bundle"]["delivery"].get("mode") == "monthly_bundle"
   and shop_by_cat["monthly_bundle"]["delivery"].get("bundle_id") == "current"
   and not shop_by_cat["monthly_bundle"]["delivery"].get("item_id"))
ck("角色栏配置 base 1 / max 2 / product 指向角色栏商品且留空发货物",
   slot["schema"] == "character-slot-table/1" and slot["version"] > 0 and slot.get("pending") is True
   and slot["base_slots"] == 1 and slot["max_slots"] == 2
   and slot["product_id"] == shop_by_cat["second_slot"]["id"]
   and shop_by_cat["second_slot"]["delivery"].get("mode") == "second_slot"
   and not shop_by_cat["second_slot"]["delivery"].get("item_id"))
table_cs = src("src/Character/MonthlyBundleTable.cs")
ck("月叠表白名单就是两种符，并点名挡住蓝符 / 卷轴 / 经验道具",
   '"teleport_talisman", "red_talisman"' in table_cs
   and all(w in table_cs for w in ('"blue_talisman"', '"revive_scroll"', '"exp_booster"'))
   and "方案 12.1" in table_cs)
repo_cs = src("src/Character/MonthlyBundleRepository.cs")
svc_cs = src(os.environ["AGG_CHARGRPC"])
ck("月叠发货只在一个仓储里：先抢事务号 → 扣款 → 逐格写入（同一个事务）",
   "grant_txn (txn_id, character_id)" in repo_cs and "amount >= $2" in repo_cs
   and repo_cs.index("ClaimTxnAsync") < repo_cs.index("DebitYuanbaoAsync")
   and "inventory (character_id, slot, item_id, count, bound, bound_at)" in repo_cs)
ck("产品代码里没有写死天数、件数或这两种符的编号",
   all(lit not in repo_cs + svc_cs for lit in ('"30"', '"teleport_talisman"', '"talisman_red"')))
slot_table = src("src/Character/CharacterSlotTable.cs")
ck("角色栏表要求 base + 1 的上限，且不许出现装备 / 等级 / 境界一类键",
   "baseSlots + 1" in slot_table and '"equip"' in slot_table and '"realm"' in slot_table)
char_repo = src("src/Character/CharacterRepository.cs")
ck("建角名额与插入在同一笔事务：锁账号行 → 数角色 → 满了 slot_full",
   "for update" in char_repo and "slot_full" in char_repo
   and char_repo.index("lockAccount") < char_repo.index("insert into \"角色\".character"))
slot_sql, main_sql = src("db/character/character_slot.sql"), src("db/character/character.sql")
ck("名额表只有名额一列（装备 / 等级 / 境界不在这张表里）",
   "slots         integer" in slot_sql
   and not re.search(r"^\s*(level|realm|equip|enhance)\w*\s", slot_sql, re.M))
ck("分表迁移与主迁移是同一份 DDL（只跑主迁移的老卡也自包含）",
   "CREATE TABLE IF NOT EXISTS \"角色\".character_slot" in main_sql
   and main_sql.count("character_slot (") >= 1)
ward = src("src/Character/WardrobeRepository.cs")
ck("外观收藏只取六种外观类物品，装备 / 强化 / 等级不在其中",
   all(k in ward for k in ('"outfit"', '"weapon_skin"', '"mount_skin"', '"appearance_fragment"'))
   and '"equipment"' not in ward and "enhance" not in ward)
proto = src("proto/mmo/character.proto")
slot_req = proto.split("message DescribeCharacterSlotsRequest {")[1].split("}")[0]
ward_entry = proto.split("message WardrobeEntry {")[1].split("}")[0]
ck("proto：角色位请求只有账号一个字段，收藏条目没有等级 / 强化 / 装备字段",
   len(re.findall(r"^\s*\w+\s+\w+\s*=\s*\d+;", slot_req, re.M)) == 1
   and not re.search(r"\b(level|realm|enhance|slot|price)\b", ward_entry))
ck("proto：两个 RPC 与回包的 character_slots 都在",
   "rpc DescribeCharacterSlots(" in proto and "rpc DescribeWardrobe(" in proto
   and "int32 character_slots = 19;" in proto)
ck("两端 Character.cs 生成物一致，CharacterGrpc.cs 只在服务端",
   sha("src/Shared/Generated/Character.cs") == sha("client/MMOGame/Assets/Proto/Generated/Character.cs")
   and os.path.exists(os.path.join(root, "src/Shared/Generated/CharacterGrpc.cs"))
   and not os.path.exists(os.path.join(root, "client/MMOGame/Assets/Proto/Generated/CharacterGrpc.cs")))
api = src("src/Gateway/SlotApi.cs")
ck("网关两个只读口都注册了，除 token 之外不读别的 query",
   "SlotApi.Map(" in src("src/Gateway/GatewayProcess.cs")
   and 'MapGet("/character/slots"' in api and 'MapGet("/wardrobe"' in api
   and set(re.findall(r'Request\.Query\["([^"]+)"\]', api)) == {"token"})
ck("第二角色栏买的是账号级名额：扣款带余额条件，名额只增到配置上限",
   "amount >= $2" in src("src/Character/CharacterSlotRepository.cs")
   and "current >= table.MaxSlots" in src("src/Character/CharacterSlotRepository.cs"))
for name, cond in checks: print(("OK " if cond else "BAD ") + name)
PY
while IFS= read -r line; do
  case "$line" in "OK "*) ok "${line#OK }" ;; "BAD "*) bad "${line#BAD }" ;; *) bad "静态检查输出异常：$line" ;; esac
done <"$TMP/static.out"

echo "== 3. 只读入口与坏配置 =="
MB_OUT="$("$DOTNET" "$CD" --monthly-bundle-list 2>&1)"; MBRC=$?
printf '%s' "$MB_OUT" | grep -q "^monthly_bundle_list_state=ok .*product=${MB_ID} .*days=${DAYS} kinds=2" \
  && ok "月叠只读入口从配置现取：商品、标价、天数、两种符" \
  || bad "月叠只读入口不对（rc=${MBRC}）：$(printf '%s' "$MB_OUT" | utf8 | head -2)"
printf '%s' "$MB_OUT" | grep -q "monthly_bundle_item=teleport_talisman item=${TP_ITEM} .*count=${TP_COUNT}" \
  && printf '%s' "$MB_OUT" | grep -q "monthly_bundle_item=red_talisman item=${RED_ITEM} .*count=${RED_COUNT}" \
  && ok "月叠只读入口逐条打出两种符与总张数（${TP_COUNT} / ${RED_COUNT}）" \
  || bad "月叠只读入口没打出两种符：$(printf '%s' "$MB_OUT" | utf8 | head -4)"
SL_OUT="$("$DOTNET" "$CD" --character-slot-list 2>&1)"
printf '%s' "$SL_OUT" | grep -q "^character_slot_list_state=ok .*product=${SLOT_ID} .*base_slots=1 max_slots=2" \
  && ok "角色栏只读入口从配置现取：商品、标价、基础位 1、上限 2" \
  || bad "角色栏只读入口不对：$(printf '%s' "$SL_OUT" | utf8 | head -2)"
python3 - "$ROOT" "$TMP" <<'PY'
import copy, json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
mb = json.load(open(os.path.join(root, "config/monthly_bundle.json"), encoding="utf-8"))
sl = json.load(open(os.path.join(root, "config/character_slot.json"), encoding="utf-8"))
def w(name, o):
    json.dump(o, open(os.path.join(tmp, "bad-mb-" + name + ".json"), "w", encoding="utf-8"),
              ensure_ascii=False)
def mut(name, f):
    o = copy.deepcopy(mb); f(o); w(name, o)
mut("schema", lambda o: o.update(schema="monthly-bundle-table/9"))
mut("version", lambda o: o.update(version=0))
mut("days_zero", lambda o: o.update(days=0))
mut("days_over", lambda o: o.update(days=999))
mut("product", lambda o: o.update(product_id="shop_outfit_1"))
mut("blue", lambda o: o["items"][0].update(kind="blue_talisman", item_id="talisman_blue"))
mut("wrong_red", lambda o: o["items"][1].update(item_id="talisman_blue"))
mut("revive", lambda o: o["items"][1].update(kind="revive_scroll", item_id="scroll_revive"))
mut("exp", lambda o: o["items"][0].update(kind="exp_booster", item_id="exp_booster"))
mut("per_day_zero", lambda o: o["items"][0].update(per_day=0))
mut("per_day_over", lambda o: o["items"][0].update(per_day=21))
mut("missing_red", lambda o: o["items"].pop())
mut("top_gold", lambda o: o.update(gold=100))
for name, f in (("schema", lambda o: o.update(schema="x")), ("version", lambda o: o.update(version=0)),
                ("base_zero", lambda o: o.update(base_slots=0)),
                ("base_two", lambda o: o.update(base_slots=2)),
                ("max_three", lambda o: o.update(max_slots=3)),
                ("product", lambda o: o.update(product_id="shop_monthly_bundle_1")),
                ("top_gold", lambda o: o.update(gold=100)),
                ("top_level", lambda o: o.update(level_shared=True))):
    o = copy.deepcopy(sl); f(o)
    json.dump(o, open(os.path.join(tmp, "bad-sl-" + name + ".json"), "w", encoding="utf-8"),
              ensure_ascii=False)
PY
for c in schema version days_zero days_over product blue wrong_red revive exp per_day_zero \
         per_day_over missing_red top_gold; do
  out="$("$DOTNET" "$CD" --monthly-bundle-list --monthly-bundle-path "$TMP/bad-mb-$c.json" 2>&1)"; rc=$?
  [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error=' \
    && ok "坏月叠配置 ${c}：config_error= 且非 0 退出" \
    || bad "坏月叠配置 $c 没挡住（rc=${rc}）：$(printf '%s' "$out" | utf8 | head -2)"
done
for c in schema version base_zero base_two max_three product top_gold top_level; do
  out="$("$DOTNET" "$CD" --character-slot-list --character-slot-path "$TMP/bad-sl-$c.json" 2>&1)"; rc=$?
  [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error=' \
    && ok "坏角色栏配置 ${c}：config_error= 且非 0 退出" \
    || bad "坏角色栏配置 $c 没挡住（rc=${rc}）：$(printf '%s' "$out" | utf8 | head -2)"
done

echo "== 4. 启动日志（不连库也先打出两表事实值）=="
python3 - "$ROOT" "$TMP" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1] + "/config/character.json", encoding="utf-8"))
cfg["listen"] = "http://127.0.0.1:62093"
json.dump(cfg, open(sys.argv[2] + "/char-log.json", "w", encoding="utf-8"), ensure_ascii=False)
PY
"$DOTNET" "$CD" --config "$TMP/char-log.json" >"$TMP/char-log.log" 2>&1 & PIDS="$PIDS $!"
if wait_line "$TMP/char-log.log" '^monthly_bundle=version='; then
  grep -q "^monthly_bundle=.*days=${DAYS} kinds=2" "$TMP/char-log.log" \
    && grep -q "^monthly_bundle_item=red_talisman item=${RED_ITEM}" "$TMP/char-log.log" \
    && ok "启动日志打出月叠的天数、两种符与总张数" || bad "启动日志的月叠事实值不对"
  grep -q "^character_slot=.*base_slots=1 max_slots=2" "$TMP/char-log.log" \
    && ok "启动日志打出角色位的基础位与上限" || bad "启动日志的角色位事实值不对"
  grep -q "^season_pass=version=" "$TMP/char-log.log" \
    && ok "老启动日志（通行证）仍在，顺序没被打乱" || bad "老启动日志被改"
else
  bad "启动没有打出 monthly_bundle=：$(tail -5 "$TMP/char-log.log" | utf8 | detail)"
fi
stop_pid "$(printf '%s' "$PIDS" | awk '{print $NF}')"
PIDS=""

echo "== 5. 真服真库（月叠两种符 / 名额闸门 / 第二角色 / 外观收藏）=="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真服真库段（MMO_P3_4B_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
c = json.load(open(os.path.join(sys.argv[1], "config", "character.json"), encoding="utf-8"))
c["listen"] = "http://127.0.0.1:62000"
json.dump(c, open(os.path.join(sys.argv[2], "char-real.json"), "w", encoding="utf-8"),
          ensure_ascii=False)
PY
  # 名额表要在这台库上真存在；主迁移自包含，老库只跑这一份就够。
  "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/character.sql" \
      --config "$ROOT/config/character.json" >"$TMP/mig.log" 2>&1 || true
  grep -q '^migrate=ok' "$TMP/mig.log" \
    && ok "真服：主迁移跑过（名额表 character_slot 就位）" \
    || bad "真服：主迁移没跑成：$(tail -3 "$TMP/mig.log" | utf8 | detail)"
  "$DOTNET" "$GD" --config "$ROOT/config/gateway.json" >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  "$DOTNET" "$CD" --config "$TMP/char-real.json" >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  wait_ready "$TMP/gw.log" '^ready' "网关" || true
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST \
      http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
      -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p34b_$$"; PW="P34B-$$-$(date +%s)"
  # 种子不给 --seed-slots：这个账号就是产品里的基础位（1 个角色）。
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" \
      --config "$ROOT/config/gateway.json" >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login \
    -H 'Content-Type: application/json' -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" | jget token)"
  [ -n "$TOK" ] && ok "真服：登录拿到令牌（账号/口令只在内存）" || bad "真服：登录没拿到令牌"
  mkchar() {
    curl -s --noproxy '*' -w '\n%{http_code}' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"name\":\"$1\",\"race\":\"mai\",\"class_id\":\"warrior\"}"
  }
  R="$(mkchar "p34b_a_$RANDOM")"; B="$(body_of "$R")"; C1="$(printf '%s' "$B" | jget character_id)"
  [ -n "$C1" ] && ok "真服：基础位建第一个角色（#${C1}）" || { bad "真服：第一个角色没建成：$B"; finish "P3-4b"; }
  R="$(mkchar "p34b_b_$RANDOM")"; B="$(body_of "$R")"
  [ "$(code_of "$R")" = "409" ] && [ "$(printf '%s' "$B" | jget error)" = "slot_full" ] \
    && ok "真服：没买第二角色栏时第二个角色被拒（409 slot_full）" \
    || bad "真服：第二个角色没被挡住：$B"
  SL="$(curl -s --noproxy '*' "http://127.0.0.1:61000/character/slots?token=$TOK")"
  [ "$(printf '%s' "$SL" | jget base_slots)" = "1" ] && [ "$(printf '%s' "$SL" | jget max_slots)" = "2" ] \
    && [ "$(printf '%s' "$SL" | jget slots)" = "1" ] && [ "$(printf '%s' "$SL" | jget used)" = "1" ] \
    && ok "真服：只读口给出基础位 1 / 上限 2 / 现在 1 位且已用 1" \
    || bad "真服：角色位只读口不对：$SL"
  SPOOF="$(curl -s --noproxy '*' "http://127.0.0.1:61000/character/slots?token=$TOK&slots=99&base_slots=9")"
  [ "$SPOOF" = "$SL" ] && ok "真服：query 里塞名额，回包一字不变（客户端带不进来）" \
    || bad "真服：请求参数影响了名额回包：$SPOOF"
  curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/currency/add?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$C1,\"kind\":\"yuanbao\",\"delta\":3000}" >/dev/null
  R="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/shop/buy?token=$TOK" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$C1,\"item_id\":\"$MB_ID\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] && [ "$(printf '%s' "$R" | jget state)" = "bundle_delivered" ] \
    && [ "$(printf '%s' "$R" | jget delivered)" = "true" ] \
    && [ "$(printf '%s' "$R" | jget charged_yuanbao)" = "300" ] \
    && ok "真服：买月叠扣 300 元宝、两种符一次到账（state=bundle_delivered）" \
    || bad "真服：买月叠不对：$R"
  INV="$(curl -s --noproxy '*' "http://127.0.0.1:61000/inventory?token=$TOK&character_id=$C1")"
  printf '%s' "$INV" | TP_ITEM="$TP_ITEM" TP_COUNT="$TP_COUNT" RED_ITEM="$RED_ITEM" \
      RED_COUNT="$RED_COUNT" python3 -c '
import json, os, sys
e = os.environ
d = json.load(sys.stdin)
got = {}
for s in d.get("slots", []):
    got[s["item_id"]] = got.get(s["item_id"], 0) + s["count"]
want = {e["TP_ITEM"]: int(e["TP_COUNT"]), e["RED_ITEM"]: int(e["RED_COUNT"])}
print("ok" if got == want and d.get("bag_slots") is not None else "bad %r" % (got,))' \
    | grep -qx ok \
    && ok "真服：月叠到账后背包里只有传送符 ${TP_COUNT} 张与红符 ${RED_COUNT} 张（没有蓝符 / 卷轴 / 经验道具）" \
    || bad "真服：月叠到账的背包内容不对：$INV"
  printf '%s' "$INV" | grep -q '"bound": *true' \
    && ok "真服：月叠发到的两种符都是绑定态（不能上架换金币）" || bad "真服：月叠的符没落成绑定物"
  R="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/shop/buy?token=$TOK" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$C1,\"item_id\":\"$SLOT_ID\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] && [ "$(printf '%s' "$R" | jget state)" = "slot_added" ] \
    && [ "$(printf '%s' "$R" | jget character_slots)" = "2" ] \
    && [ "$(printf '%s' "$R" | jget charged_yuanbao)" = "800" ] \
    && [ "$(printf '%s' "$R" | jget delivered)" = "false" ] \
    && ok "真服：买第二角色栏扣 800 元宝、名额变 2、不往背包放东西" \
    || bad "真服：买第二角色栏不对：$R"
  BAL="$(printf '%s' "$R" | jget balance)"
  R="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/shop/buy?token=$TOK" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$C1,\"item_id\":\"$SLOT_ID\"}")"
  [ "$(printf '%s' "$R" | jget reason)" = "already_owned" ] \
    && [ "$(printf '%s' "$R" | jget balance)" = "$BAL" ] \
    && ok "真服：已经有第二位时再买被拒（already_owned），元宝不再扣" \
    || bad "真服：重复买角色栏没挡住：$R"
  R="$(mkchar "p34b_c_$RANDOM")"; B="$(body_of "$R")"; C2="$(printf '%s' "$B" | jget character_id)"
  [ -n "$C2" ] && ok "真服：买到角色栏后第二个角色建成（#${C2}）" \
    || bad "真服：第二个角色还是没建成：$B"
  # 第一个角色：装备 + 强化 5 级；第二个角色同一格必须还是 0（装备 / 强化不共享）。
  curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/inventory/add?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$C1,\"slot\":1,\"item_id\":\"sword_trial\",\"count\":1}" >/dev/null
  db_cli --raw-enhance-write "$C1" 1 5 --config "$TMP/char-real.json" >/dev/null 2>&1
  L1="$(db_cli --enhance-level "$C1" 1 --config "$TMP/char-real.json" | tr -d '\r' \
    | sed -n 's/^enhance_state=ok .* level=\([0-9]*\).*/\1/p')"
  L2="$(db_cli --enhance-level "$C2" 1 --config "$TMP/char-real.json" | tr -d '\r' \
    | sed -n 's/^enhance_state=ok .* level=\([0-9]*\).*/\1/p')"
  [ "$L1" = "5" ] && [ "$L2" = "0" ] \
    && ok "真服：第一个角色第 1 格强化 ${L1} 级，第二个角色同一格是 ${L2}（强化不共享）" \
    || bad "真服：第二个角色强化等级不是 0：$L1 / $L2"
  C2R="$(db_cli --character-report "$(printf '%s' "$B" | jget name)" --config "$TMP/char-real.json" \
    | tr -d '\r' | utf8)"
  printf '%s' "$C2R" | grep -q 'level=1 ' && printf '%s' "$C2R" | grep -q 'realm=炼体' \
    && ok "真服：第二个角色等级 1、境界炼体（等级与境界不共享）" \
    || bad "真服：第二个角色的等级 / 境界不对：$(printf '%s' "$C2R" | utf8)"
  INV2="$(curl -s --noproxy '*' "http://127.0.0.1:61000/inventory?token=$TOK&character_id=$C2")"
  [ "$(printf '%s' "$INV2" | python3 -c 'import json,sys;print(len(json.load(sys.stdin).get("slots",[])))')" = "0" ] \
    && ok "真服：第二个角色的背包是空的（装备不共享）" \
    || bad "真服：第二个角色的背包不是空的：$INV2"
  # 外观收藏：给第一个角色一件外观，两个角色名下都该看到它（共享的是收藏，不是装备）。
  curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/inventory/add?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$C1,\"slot\":5,\"item_id\":\"outfit_s1\",\"count\":1}" >/dev/null
  W="$(curl -s --noproxy '*' "http://127.0.0.1:61000/wardrobe?token=$TOK")"
  printf '%s' "$W" | C1="$C1" python3 -c '
import json, os, sys
d = json.load(sys.stdin)
items = {e["item_id"]: e for e in d.get("entries", [])}
e = items.get("outfit_s1")
print("ok" if e and str(os.environ["C1"]) in [str(x) for x in e["character_ids"]] else "bad %r" % (sorted(items),))' \
    | grep -qx ok \
    && ok "真服：外观收藏按账号给出第一角色那件服装（共享的是收藏）" \
    || bad "真服：外观收藏不对：$W"
  printf '%s' "$W" | grep -q '"kind": *"equipment"\|sword_trial' \
    && bad "真服：外观收藏里混进了装备" || ok "真服：外观收藏里没有装备 / 强化一类东西"
  STATE="$(db_cli --character-slots "$ACCT" --config "$TMP/char-real.json" | tr -d '\r' \
    | grep -v '^db_env=')"
  printf '%s\n' "$STATE" | grep -q '^character_slots=ok .*slots=2 used=2' \
    && ok "真服：库里独立读回名额 2、已用 2（两个角色都在）" \
    || bad "真服：库里读回不对：$(printf '%s' "$STATE" | utf8)"
  WARD="$(db_cli --wardrobe "$ACCT" --config "$TMP/char-real.json" | tr -d '\r' | grep -v '^db_env=')"
  printf '%s\n' "$WARD" | grep -q '^wardrobe=ok .*outfit_s1' \
    && ok "真服：独立读回的外观收藏里有那件服装（只读，不写库）" \
    || bad "真服：独立读回的外观收藏不对：$(printf '%s' "$WARD" | utf8)"
fi

echo "== 6. 回归闸门 =="
if bash tools/check/preflight.sh >"$TMP/preflight.log" 2>&1; then
  ok "preflight.sh 第 4 道闸门绿"
else
  bad "preflight.sh 没过：$(tail -4 "$TMP/preflight.log" | detail)"
fi
if git diff --check >"$TMP/diffcheck.log" 2>&1; then
  ok "git diff --check 通过"
else
  bad "git diff --check 有空白错误：$(head -3 "$TMP/diffcheck.log" | detail)"
fi

finish "P3-4b（月叠与第二角色栏）"
