#!/usr/bin/env bash
# P1-5c7 核对：限时经验道具（方案 10 章 / 12.1、技术架构 6 节、开发架构第 14 节第 46 行）。
#
# 完成标准：到点后经验恢复原倍率。再用一张不叠倍率。不能交易。
#
# 三种模式：
#   bash tools/check/check-p1-5c7.sh
#       编译 + 静态 + 只读入口 + 坏表，不连库不连服（Mac 上就能全跑）
#   MMO_P1_5C7_GATEWAY=http://127.0.0.1:61000 MMO_P1_5C7_TOKEN=<令牌> \
#     MMO_P1_5C7_CHARACTER=<角色号> MMO_P1_5C7_HAS_DB=1 bash tools/check/check-p1-5c7.sh
#       再加真网关段：买 / 用 / 结算 / 刷新 / 到期 / 拒绝 / 不能交易 / 库读回
#   MMO_P1_5C7_SKIP_BUILD=1 bash tools/check/check-p1-5c7.sh
#       跳过编译（已经 build 过时用）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY="${MMO_P1_5C7_GATEWAY:-}"
TOKEN="${MMO_P1_5C7_TOKEN:-}"
CHARACTER="${MMO_P1_5C7_CHARACTER:-0}"
HAS_DB="${MMO_P1_5C7_HAS_DB:-0}"

BOOSTER="$(python3 -c 'import json; print(json.load(open("config/exp_booster.json", encoding="utf-8"))["booster"]["item_id"])')"
DURATION="$(python3 -c 'import json; print(json.load(open("config/exp_booster.json", encoding="utf-8"))["booster"]["duration_ms"])')"
GAIN="$(python3 -c 'import json; print(json.load(open("config/exp_booster.json", encoding="utf-8"))["booster"]["gain_pct"])')"
PRICE="$(python3 -c 'import json; d=json.load(open("config/shop.json", encoding="utf-8")); print(next(i["price_yuanbao"] for i in d["items"] if i["id"]=="shop_exp_booster_1"))')"
BASE="$(python3 -c 'import json; print(json.load(open("config/exp.json", encoding="utf-8"))["base_exp"])')"
ACTIVE=$((BASE * GAIN / 100))
DELTA=$((ACTIVE - BASE))

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

# ---------------------------------------------------------------- 1. 编译

echo "== 1. 编译 =="
if [ "${MMO_P1_5C7_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_P1_5C7_SKIP_BUILD=1）\n'
else
  if "$DOTNET" build src/MMOGame.sln -c Release --nologo -v q > "$TMP/build.log" 2>&1; then
    ok "服务端编译通过"
  else
    bad "编译失败"; tail -20 "$TMP/build.log" | detail
  fi
fi

# ---------------------------------------------------------------- 2. 静态断言

echo "== 2. 静态断言 =="
ROOT="$ROOT" python3 - <<'PY' > "$TMP/static.txt" 2>&1
import glob, json, os, re

root = os.environ["ROOT"]
def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

paths = [
    "config/exp_booster.json", "config/items.json", "config/shop.json", "config/exp.json",
    "db/character/exp_booster.sql", "db/character/character.sql",
    "src/Character/ExpBoosterTable.cs", "src/Character/ExpBoosterRepository.cs",
    "src/Character/ExpSettlementRepository.cs", "src/Character/ShopTable.cs",
    "src/Character/CharacterGrpcService.cs", "src/Character/CharacterProcess.cs",
    "src/Character/CharacterConfig.cs", "src/Character/Program.cs",
    "src/Gateway/ExpBoosterApi.cs", "src/Gateway/GatewayProcess.cs",
    "src/Gateway/CharacterApi.cs", "proto/mmo/character.proto",
]
files = {p: read(p) for p in paths}
report("files", all(files.values()),
       "missing " + ",".join(k for k, v in files.items() if not v))

cfg = json.loads(files["config/exp_booster.json"])
items = json.loads(files["config/items.json"])
shop = json.loads(files["config/shop.json"])
exp = json.loads(files["config/exp.json"])
b = cfg.get("booster") or {}
ids = [i["id"] for i in items["items"]]
goods = next((i for i in items["items"] if i["id"] == b.get("item_id")), None)
entry = next((i for i in shop["items"] if i["id"] == "shop_exp_booster_1"), None)

report("schema", cfg.get("schema") == "exp-booster-table/1"
       and int(cfg.get("version", 0)) >= 1)
report("item_ids_unique", len(ids) == len(set(ids)),
       "重复编号：" + ",".join(sorted({k for k in ids if ids.count(k) > 1})))
report("goods_bound_consumable",
       goods is not None and goods.get("kind") == "consumable"
       and goods.get("bind") == "always",
       json.dumps(goods, ensure_ascii=False) if goods else "缺物品")
report("numbers_positive", int(b.get("duration_ms", 0)) > 0
       and int(b.get("gain_pct", 0)) > 100)
report("notes_mark_pending", "待定" in cfg.get("note", ""))

# 时长与倍率只在配置：源码里不能出现这两个数值（用单词边界，别撞 1500 这类注释）。
sources = [files[p] for p in paths if p.startswith("src/")]
report("numbers_only_in_config",
       not any(re.search(r"\b" + str(b.get(k)) + r"\b", s)
               for k in ("duration_ms", "gain_pct") for s in sources),
       "源码里写死了配置数值")

# 基础经验与组队加成仍是 exp.json 的正本，本卡不抄第二套。
report("exp_json_untouched",
       int(exp.get("version", 0)) == 1 and int(exp.get("base_exp", 0)) > 0
       and int(exp.get("party_bonus_pct", 0)) >= 0
       and "gain_pct" not in exp and "duration_ms" not in exp,
       "exp.json 是 P1-2d 的正本：基础经验与组队加成的数值没动，道具倍率也没抄进去")

# 落库：角色 schema 一张表、一个角色一行、只存到期时刻、不存倍率。
sql = files["db/character/exp_booster.sql"]
report("table_in_character_schema",
       'CREATE TABLE IF NOT EXISTS "角色".exp_booster' in sql
       and "character_id bigint      PRIMARY KEY" in sql
       and "expires_at   timestamptz NOT NULL" in sql
       and "gain_pct" not in sql and "倍率列" not in sql.replace("没有倍率列", ""),
       "表里不该有倍率列")
report("main_migration_has_table",
       'CREATE TABLE IF NOT EXISTS "角色".exp_booster'
       in files["db/character/character.sql"],
       "老卡只跑主迁移，结算时也要能读到这张表")

repo = files["src/Character/ExpBoosterRepository.cs"]
report("single_row_upsert",
       "on conflict (character_id) do update set" in repo
       and "expires_at = excluded.expires_at" in repo
       and repo.count(r'insert into \"角色\".exp_booster') == 1,
       "再用一张只刷新同一行")
report("refresh_not_stack",
       "now() + ($2::double precision * interval '1 millisecond')" in repo
       and "expires_at +" not in repo and "remaining" not in repo,
       "到期时刻不按旧的剩余时间累加")
report("use_is_one_transaction",
       "BeginTransactionAsync" in repo and "grant_txn" in repo
       and "CommitAsync" in repo and repo.count("RollbackAsync") >= 3,
       "扣一张与写到期时刻在同一笔事务，拒绝整笔回滚")

# 结算：只在同一笔事务里读生效状态、只乘这一笔、只写 level/exp。
settle = files["src/Character/ExpSettlementRepository.cs"]
report("settle_reads_db_clock",
       "expires_at > now()" in settle and "boosterTable.ExpBooster.GainPct" in settle,
       "生效判定用数据库当前时刻，倍率只从配置读一个")
report("settle_only_multiplies_one_gain",
       "rawGain * boosterPct / OriginalPct" in settle
       and "boosterExp = gain - rawGain" in settle,
       "只乘「基础 + 组队」这一笔")
report("settle_writes_only_level_exp",
       r'update \"角色\".character set level = $2, exp = $3 where id = $1' in settle
       and r'insert into \"角色\".exp_booster' not in settle
       and r'update \"角色\".exp_booster' not in settle
       and r'delete from \"角色\".exp_booster' not in settle,
       "结算不写道具表、不碰别的账")
report("level_cap_one_place",
       "while (next < LevelCap" in read("src/Character/ExpTable.cs")
       and "LevelCap" not in settle,
       "封顶仍只有 ExpTable.Apply 一处")

# 协议：请求带不进经验数值 / 时长 / 倍率；回包只给事实值。
proto = files["proto/mmo/character.proto"]
def message(name):
    m = re.search(r"message " + name + r" \{(.*?)\n\}", proto, re.S)
    return m.group(1) if m else ""

settle_req = message("SettleExpRequest")
use_req = message("UseExpBoosterRequest")
settle_resp = message("SettleExpResponse")
report("settle_request_no_numbers",
       all(x in settle_req for x in ("account", "character_id", "txn_id", "source", "party_size"))
       and not any(x in settle_req for x in ("booster", "gain_pct", "exp_gain", "base_exp =")),
       "结算请求不带经验数值与倍率")
report("use_request_no_numbers",
       all(x in use_req for x in ("account", "character_id", "item_id", "txn_id"))
       and not any(x in use_req for x in ("duration_ms =", "gain_pct =", "expires_at_ms ="))
       and not re.search(r"^\s*\w+ count =", use_req, re.M),
       "用道具请求带不进时长、倍率、到期时刻、张数")
report("settle_response_facts",
       "booster_pct" in settle_resp and "booster_exp" in settle_resp)

# 网关：一条路由、令牌 + 归属 + 事务号；结算回包把倍率事实值带出去。
gw = files["src/Gateway/ExpBoosterApi.cs"]
report("gateway_one_route",
       gw.count('app.MapPost("/exp-booster/use"') == 1
       and files["src/Gateway/GatewayProcess.cs"].count("ExpBoosterApi.Map(") == 1)
report("gateway_auth_and_txn",
       "TryResolve" in gw and "ListInventoryAsync" in gw
       and 'txnId = "exp_booster_use:" + Guid.NewGuid()' in gw
       and "UseBody(ulong? CharacterId, string? ItemId)" in gw,
       "网关认令牌、复核归属、生成事务号，请求体只有两个字段")
gate_req = re.search(r"new UseExpBoosterRequest\s*\{(.*?)\}", gw, re.S)
report("gateway_request_fields",
       gate_req is not None
       and all(x in gate_req.group(1) for x in ("Account", "CharacterId", "ItemId", "TxnId"))
       and not any(x in gate_req.group(1) for x in ("DurationMs", "GainPct", "ExpiresAtMs")),
       "网关转给角色服的也只有标识")
report("settle_route_forwards_facts",
       "booster_pct = result.BoosterPct" in files["src/Gateway/CharacterApi.cs"]
       and "booster_exp = result.BoosterExp" in files["src/Gateway/CharacterApi.cs"])

# 商城：只从商城发，发货物必须就是配置那一个编号。
report("shop_delivery",
       int(shop.get("version", 0)) >= 6 and entry is not None
       and entry.get("delivery_item_id") == b.get("item_id")
       and entry.get("delivery_count") == 1
       and entry.get("kind") == "exp_booster")
report("shop_kind_whitelist",
       '"exp_booster"' in files["src/Character/ShopTable.cs"]
       and "boosters.ExpBooster" in files["src/Character/ShopTable.cs"]
       and "booster.ItemId" in files["src/Character/ShopTable.cs"])
report("shop_others_unchanged",
       all(any(i.get("id") == k and i.get("delivery_item_id") == v
               for i in shop["items"])
           for k, v in (("shop_protect_ticket_1", "ticket_protect"),
                        ("shop_red_talisman_1", "talisman_red"),
                        ("shop_blue_talisman_1", "talisman_blue"),
                        ("shop_revive_scroll_1", "scroll_revive"))),
       "接新发货没把前四种带坏")

# 副本 / 任务 / 拍卖 / 月叠 / 通行证不产出，也不留接口：
# 除 items / shop / exp_booster 三份配置，别的配置里不许出现这个编号。
others = [p for p in glob.glob(os.path.join(root, "config", "*.json"))
          if os.path.basename(p) not in ("items.json", "shop.json", "exp_booster.json")]
report("no_other_sources",
       all(b.get("item_id") not in open(p, encoding="utf-8").read() for p in others),
       "别的配置里出现了道具编号")

# 配置链与只读入口：改配置重启生效、坏表起不来、到期模拟只给核对用。
report("config_chain",
       "DefaultExpBoosterTable" in files["src/Character/CharacterConfig.cs"]
       and "exp_booster_table_version=" in files["src/Character/CharacterProcess.cs"]
       and "AddSingleton(boosterTable)" in files["src/Character/CharacterProcess.cs"])
program = files["src/Character/Program.cs"]
report("readonly_entries",
       all(x in program for x in ("--exp-booster-list", "--exp-booster-config",
                                  "--exp-booster-state", "--exp-booster-reset",
                                  "--exp-booster-expire", "exp_booster_state=ok")))
# C# 源码里 SQL 是转义文本，按片段逐行找，别按裸 "角色" 查。
check_only = [l for l in program.splitlines()
              if ("delete from" in l or "set expires_at = now() -" in l)
              and "exp_booster" in l]
report("check_only_writes", len(check_only) == 2,
       "清行 / 到期模拟只该在只读核对入口里：" + str(len(check_only)))
PY
if grep -q '^FAIL' "$TMP/static.txt"; then
  bad "静态断言有未通过项"; grep '^FAIL' "$TMP/static.txt" | detail
else
  ok "静态断言全过（$(grep -c '^PASS' "$TMP/static.txt") 条）"
fi

# ---------------------------------------------------------------- 3. 只读入口与坏表

echo "== 3. 只读入口与坏表 =="
LIST="$("$DOTNET" "$CHARACTER_DLL" --exp-booster-list 2>&1)"
if printf '%s' "$LIST" | grep -q "exp_booster_list_state=ok version=1 item=$BOOSTER" \
   && printf '%s' "$LIST" | grep -q "duration_ms=$DURATION gain_pct=$GAIN"; then
  ok "道具表读得出来：version=1 item=$BOOSTER duration_ms=$DURATION gain_pct=$GAIN"
else
  bad "道具表只读入口不对：$LIST"
fi

if [ "$DURATION" -gt 0 ] && [ "$GAIN" -gt 100 ] \
   && [ "$ACTIVE" -gt "$BASE" ]; then
  ok "生效期内这一笔应得 ${ACTIVE}（基础 $BASE × $GAIN%），到期后回到 $BASE"
else
  bad "配置数值算出来的倍率不对"
fi

ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import copy, json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
cfg = json.load(open(os.path.join(root, "config/exp_booster.json"), encoding="utf-8"))

def case(fn):
    d = copy.deepcopy(cfg)
    fn(d)
    return d

def set_field(section, key, value):
    def f(d):
        d[section][key] = value
    return f

cases = {
    "bad_schema": case(lambda d: d.update(schema="wrong-table/1")),
    "zero_version": case(lambda d: d.update(version=0)),
    "no_booster": case(lambda d: d.pop("booster", None)),
    "empty_item": case(set_field("booster", "item_id", "")),
    "unknown_item": case(set_field("booster", "item_id", "no_such_item")),
    "material_item": case(set_field("booster", "item_id", "ore_iron")),
    "unbound_item": case(set_field("booster", "item_id", "sword_trade")),
    "same_as_red": case(set_field("booster", "item_id", "talisman_red")),
    "same_as_scroll": case(set_field("booster", "item_id", "scroll_revive")),
    "zero_duration": case(set_field("booster", "duration_ms", 0)),
    "neg_duration": case(set_field("booster", "duration_ms", -1)),
    "gain_pct_100": case(set_field("booster", "gain_pct", 100)),
    "gain_pct_zero": case(set_field("booster", "gain_pct", 0)),
}
for name, data in cases.items():
    with open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)

shop = json.load(open(os.path.join(root, "config/shop.json"), encoding="utf-8"))
for item in shop["items"]:
    if item["id"] == "shop_exp_booster_1":
        item["delivery_item_id"] = "talisman_red"
with open(os.path.join(tmp, "bad-shop-delivery.json"), "w", encoding="utf-8") as f:
    json.dump(shop, f, ensure_ascii=False)
PY

for bad_case in bad_schema zero_version no_booster empty_item unknown_item \
                material_item unbound_item same_as_red same_as_scroll \
                zero_duration neg_duration gain_pct_100 gain_pct_zero; do
  out="$("$DOTNET" "$CHARACTER_DLL" --exp-booster-list \
        --exp-booster-config "$TMP/bad-$bad_case.json" 2>&1)"
  if printf '%s' "$out" | grep -q '^config_error='; then
    ok "坏表 ${bad_case}：起不来（$(printf '%s' "$out" | head -1 | cut -c1-60)…）"
  else
    bad "坏表 ${bad_case} 没被挡住：$out"
  fi
done

out="$("$DOTNET" "$CHARACTER_DLL" --shop-list \
      --shop-config "$TMP/bad-shop-delivery.json" 2>&1)"
if printf '%s' "$out" | grep -q '^config_error='; then
  ok "坏清单：发货物不等于配置编号就起不来"
else
  bad "坏清单没被挡住：$out"
fi

SHOP="$("$DOTNET" "$CHARACTER_DLL" --shop-list 2>&1)"
# 已接发货的种类以后还会涨（c8 起还会加），只钉「头部两个数字相等且不少于本卡的 5 种」，
# 不写死 delivered_kinds=5：写死数字的话，下一张卡接第六种发货时这张老卡就会假失败。
SHOP_HDR="$(printf '%s' "$SHOP" | python3 -c 'import re, sys
line = sys.stdin.readline()
def f(k):
    m = re.search(r"\b" + k + r"=(\d+)", line)
    return int(m.group(1)) if m else -1
d, v = f("delivered_kinds"), f("deliverable_items")
print("ok" if d >= 5 and d == v else "bad")')"
if printf '%s' "$SHOP" | grep -q "delivery=${BOOSTER}x1" && [ "$SHOP_HDR" = "ok" ]; then
  ok "清单里限时经验道具发 ${BOOSTER}x1，前四种发货未被带坏"
else
  bad "商城清单发货不对：$SHOP"
fi

# ---------------------------------------------------------------- 4. 真网关段

echo "== 4. 真网关段（买 → 用 → 结算 → 刷新 → 到期 → 拒绝）=="
if [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （跳过：没给 MMO_P1_5C7_GATEWAY / _TOKEN / _CHARACTER）\n'
else
  post() {
    curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" \
      -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
      -d "$2" 2>&1
  }
  get() {
    curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" "$1" 2>&1
  }
  buy() { post "$GATEWAY/shop/buy" "{\"character_id\":$CHARACTER,\"item_id\":\"$1\"}"; }
  use() { post "$GATEWAY/exp-booster/use" "{\"character_id\":$CHARACTER,\"item_id\":\"$1\"}"; }
  settle() {
    post "$GATEWAY/exp/settle" \
      "{\"character_id\":$CHARACTER,\"txn_id\":\"$1\",\"source\":\"dungeon\",\"party_size\":1}"
  }
  new_txn() { python3 -c 'import uuid; print("p15c7_" + uuid.uuid4().hex)'; }
  bag() { get "$GATEWAY/inventory?character_id=$CHARACTER"; }
  money() { get "$GATEWAY/currency?character_id=$CHARACTER"; }

  yuanbao_of() {
    printf '%s' "$1" | python3 -c 'import json,sys
try: d = json.load(sys.stdin)
except Exception: print(""); raise SystemExit
print(next((b.get("amount") for b in d.get("balances", []) if b.get("kind")=="yuanbao"), ""))'
  }
  count_of() {
    printf '%s' "$1" | python3 -c 'import json,sys
try: d = json.load(sys.stdin)
except Exception: print(-1); raise SystemExit
print(sum(int(s.get("count") or 0) for s in d.get("slots", []) if s.get("item_id")==sys.argv[1]))' "$BOOSTER"
  }
  slot_of() {
    printf '%s' "$1" | python3 -c 'import json,sys
try: d = json.load(sys.stdin)
except Exception: print(0); raise SystemExit
print(next((s.get("slot") for s in d.get("slots", []) if s.get("item_id")==sys.argv[1]), 0))' "$BOOSTER"
  }
  db() { "$DOTNET" "$CHARACTER_DLL" "$@" 2>&1; }
  booster_state() { db --exp-booster-state "$CHARACTER"; }
  exp_state() { db --exp-state "$CHARACTER"; }

  before_money="$(yuanbao_of "$(money)")"
  before_count="$(count_of "$(bag)")"
  before_total=""
  if [ "$HAS_DB" = "1" ]; then
    before_total="$(field_of "$(exp_state)" total_gain)"
  fi

  # 闸门：无令牌 / 缺字段 / 别人的角色 / 清单外 / 没接发货的种类
  no_token="$(curl -sS --noproxy '*' -m 10 -o /dev/null -w '%{http_code}' \
    -X POST "$GATEWAY/exp-booster/use" -H 'Content-Type: application/json' \
    -d "{\"character_id\":$CHARACTER,\"item_id\":\"$BOOSTER\"}" 2>&1)"
  if [ "$no_token" = "401" ]; then ok "无令牌用道具：401" ; else bad "无令牌没挡住：$no_token"; fi

  r="$(post "$GATEWAY/exp-booster/use" '{}')"
  if [ "$(code_of "$r")" = "400" ]; then ok "缺角色号或编号：400"; else bad "缺字段没挡住：$r"; fi

  other=$((CHARACTER + 1000000))
  r="$(post "$GATEWAY/exp-booster/use" "{\"character_id\":$other,\"item_id\":\"$BOOSTER\"}")"
  if [ "$(code_of "$r")" = "404" ]; then ok "别人的角色：404"; else bad "别人的角色没挡住：$r"; fi

  r="$(buy no_such_goods)"
  if [ "$(expect_json "$(body_of "$r")" ok=false reason=not_in_list charged_yuanbao=0)" = "ok" ]; then
    ok "清单外：not_in_list，一分元宝不扣"
  else
    bad "清单外没拒干净：$r"
  fi

  # 动态挑一条还没接发货的商品，别写死某个编号：后面的卡会陆续接上发货。
  pending_id="$(python3 -c 'import json; d=json.load(open("config/shop.json", encoding="utf-8")); g=next((i for i in d["items"] if not i.get("delivery_item_id")), None); print(g["id"] if g else "")')"
  r="$(buy "$pending_id")"
  if [ -n "$pending_id" ] \
     && [ "$(expect_json "$(body_of "$r")" ok=false reason=delivery_pending charged_yuanbao=0)" = "ok" ]; then
    ok "清单内还没接发货的种类（${pending_id}）：delivery_pending，一分不扣"
  else
    bad "未接发货的种类没拒干净：$r"
  fi

  # 买第一张：扣款、入包、到账即绑；这一格不能交易、不能上架。
  r="$(buy shop_exp_booster_1)"
  b1="$(body_of "$r")"
  if [ "$(expect_json "$b1" ok=true delivered=true bound=true charged_yuanbao="$PRICE" delivery_item_id="$BOOSTER")" = "ok" ]; then
    ok "买一张：扣 $PRICE 元宝、入包、到账即绑"
  else
    bad "买一张没成功：$r"
  fi
  after_money="$(yuanbao_of "$(money)")"
  after_count="$(count_of "$(bag)")"
  if [ -n "$before_money" ] && [ -n "$after_money" ] \
     && [ "$((before_money - after_money))" -eq "$PRICE" ] \
     && [ "$((after_count - before_count))" -eq 1 ]; then
    ok "元宝 $before_money → ${after_money}，背包道具 $before_count → $after_count"
  else
    bad "买完钱或背包不对：$before_money → $after_money / $before_count → $after_count"
  fi

  slot="$(slot_of "$(bag)")"
  if [ "$slot" != "0" ] && [ "$slot" != "" ]; then
    r="$(post "$GATEWAY/inventory/can-trade" "{\"character_id\":$CHARACTER,\"slot\":$slot}")"
    if [ "$(expect_json "$(body_of "$r")" tradable=false reason=bound_item)" = "ok" ]; then
      ok "不能交易：can-trade 回 bound_item"
    else
      bad "can-trade 没拒绑定道具：$r"
    fi
    r="$(post "$GATEWAY/auction/list" \
      "{\"character_id\":$CHARACTER,\"slot\":$slot,\"count\":1,\"price_gold\":10}")"
    if [ "$(expect_json "$(body_of "$r")" ok=false reason=bound_item)" = "ok" ]; then
      ok "不能交易：上架也回 bound_item"
    else
      bad "上架没拒绑定道具：$r"
    fi
  else
    bad "背包里找不到道具格，没法验「不能交易」"
  fi

  # 再买两张，凑够三个成功场景各用一张；最后「背包空」时刚好是 0。
  for _ in 1 2; do buy shop_exp_booster_1 > /dev/null 2>&1; done

  # 用第一张 → 结算 A：吃到配置倍率
  r="$(use "$BOOSTER")"; b="$(body_of "$r")"
  exp1="$(json_field "$b" expires_at_ms)"; now1="$(json_field "$b" now_ms)"
  if [ "$(expect_json "$b" ok=true item_id="$BOOSTER" gain_pct="$GAIN" duration_ms="$DURATION" count_left=2)" = "ok" ] \
     && [ "$exp1" -gt "$now1" ] && [ "$((exp1 - now1))" -le "$DURATION" ]; then
    ok "用第一张：到期时刻 ${exp1}、倍率 $GAIN%、余 2 张"
  else
    bad "用第一张不对：$r"
  fi

  if [ "$HAS_DB" = "1" ]; then
    st="$(booster_state)"
    if [ "$(field_of "$st" rows)" = "1" ] \
       && [ "$(field_of "$st" active)" = "true" ] \
       && [ "$(field_of "$st" bound)" = "true" ] \
       && [ "$(field_of "$st" count)" = "2" ] \
       && [ "$(field_of "$st" remaining_ms)" -gt 0 ]; then
      ok "库里读回：只有一行、生效中、绑定、余 2 张"
    else
      bad "库里状态不对：$st"
    fi
  fi

  sa="$(settle "$(new_txn)")"; ba="$(body_of "$sa")"
  if [ "$(expect_json "$ba" duplicate=false base_exp="$BASE" party_bonus=0 booster_pct="$GAIN" booster_exp="$DELTA" exp_gain="$ACTIVE")" = "ok" ]; then
    ok "生效期内结算：exp_gain=${ACTIVE}（原倍率 ${BASE}）、booster_pct=$GAIN"
  else
    bad "生效期内结算不对：$sa"
  fi

  # 用第二张 → 只刷新到期时刻、库里仍一行 → 结算 B 与 A 相同（不叠倍率）
  r="$(use "$BOOSTER")"; b="$(body_of "$r")"
  exp2="$(json_field "$b" expires_at_ms)"; now2="$(json_field "$b" now_ms)"
  if [ "$(expect_json "$b" ok=true gain_pct="$GAIN" count_left=1)" = "ok" ] \
     && [ "$exp2" -gt "$exp1" ] \
     && [ "$((exp2 - now2))" -le "$DURATION" ]; then
    ok "再用一张：只刷新到期时刻（$exp1 → ${exp2}），倍率仍是 $GAIN%"
  else
    bad "再用一张不对（可能把剩余时间累加了）：$r"
  fi

  if [ "$HAS_DB" = "1" ]; then
    st="$(booster_state)"
    if [ "$(field_of "$st" rows)" = "1" ] && [ "$(field_of "$st" count)" = "1" ]; then
      ok "库里仍只有一行、余 1 张：同时只生效一个"
    else
      bad "刷新把行数或余量改坏了：$st"
    fi
  fi

  sb="$(settle "$(new_txn)")"; bb="$(body_of "$sb")"
  if [ "$(expect_json "$bb" booster_pct="$GAIN" booster_exp="$DELTA" exp_gain="$ACTIVE")" = "ok" ]; then
    ok "刷新之后再结算：exp_gain 仍是 ${ACTIVE}，倍率没叠"
  else
    bad "刷新之后倍率叠了：$sb"
  fi

  # 到期 → 结算 C 恢复原倍率（用核对专用入口把到期时刻前移，不必真等配置时长）
  if [ "$HAS_DB" = "1" ]; then
    expired="$(db --exp-booster-expire "$CHARACTER")"
    if printf '%s' "$expired" | grep -q '^exp_booster_expire=ok'; then
      ok "到期模拟：只把库里那一行的 expires_at 挪到过去"
    else
      bad "到期模拟没生效：$expired"
    fi
    st="$(booster_state)"
    if [ "$(field_of "$st" active)" = "false" ] \
       && [ "$(field_of "$st" remaining_ms)" = "0" ]; then
      ok "库里读回：到点后 active=false、剩余时间 0"
    else
      bad "到点后库里状态不对：$st"
    fi
  fi

  sc="$(settle "$(new_txn)")"; bc="$(body_of "$sc")"
  if [ "$(expect_json "$bc" booster_pct=100 booster_exp=0 exp_gain="$BASE")" = "ok" ]; then
    ok "到点后结算：booster_pct=100、exp_gain 回到原倍率 $BASE"
  else
    bad "到点后没恢复原倍率：$sc"
  fi

  # 到期后再用第三张能重新生效；接着的两次拒绝都不写库
  r="$(use "$BOOSTER")"; b="$(body_of "$r")"
  exp3="$(json_field "$b" expires_at_ms)"
  if [ "$(expect_json "$b" ok=true gain_pct="$GAIN" count_left=0)" = "ok" ]; then
    ok "到期后再用一张：重新生效，背包用光"
  else
    bad "到期后再用一张不对：$r"
  fi

  r="$(use "$BOOSTER")"; b="$(body_of "$r")"
  if [ "$(expect_json "$b" ok=false reason=no_booster count_left=0 expires_at_ms=0)" = "ok" ]; then
    ok "背包一张不剩：no_booster，未生效数值为 0"
  else
    bad "背包空没挡住：$r"
  fi

  r="$(use ticket_protect)"
  if [ "$(expect_json "$(body_of "$r")" ok=false reason=not_booster expires_at_ms=0)" = "ok" ]; then
    ok "拿保护券当经验道具：not_booster"
  else
    bad "编号不对没挡住：$r"
  fi

  if [ "$HAS_DB" = "1" ]; then
    st="$(booster_state)"
    if [ "$(field_of "$st" expires_at_ms)" = "$exp3" ] \
       && [ "$(field_of "$st" rows)" = "1" ] \
       && [ "$(field_of "$st" count)" = "0" ]; then
      ok "拒绝路径从库里读回：到期时刻没动、仍一行、道具 0 张（一个字没写）"
    else
      bad "拒绝路径写了库：$st"
    fi

    after_total="$(field_of "$(exp_state)" total_gain)"
    expected=$((ACTIVE + ACTIVE + BASE))
    if [ -n "$before_total" ] && [ -n "$after_total" ] \
       && [ "$((after_total - before_total))" -eq "$expected" ]; then
      ok "只加经验：三笔结算入账 ${expected}，与流水累计对得上"
    else
      bad "流水累计不对：$before_total → ${after_total}，期望 +$expected"
    fi
  fi

  final_money="$(yuanbao_of "$(money)")"
  if [ -n "$before_money" ] && [ -n "$final_money" ] \
     && [ "$((before_money - final_money))" -eq "$((PRICE * 3))" ]; then
    ok "整段只买了三张：元宝一共少 $((PRICE * 3))"
  else
    bad "元宝扣得不对：$before_money → $final_money"
  fi
  final_count="$(count_of "$(bag)")"
  if [ "$final_count" = "0" ]; then
    ok "背包道具 0 张：三张各被一个成功场景扣掉"
  else
    bad "背包还剩 $final_count 张，用例顺序可能乱了"
  fi
fi

finish "P1-5c7（server）"
