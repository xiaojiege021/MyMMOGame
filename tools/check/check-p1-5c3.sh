#!/usr/bin/env bash
# P1-5c3 核对：红符只回复自己的生命（方案 12.1、开发架构第 14 节第 43 行）。
#
# 完成标准：红符只回复自己的生命——不能治疗队友，不能复活；回复不超过同级红药。
#
# 四种模式：
#   bash tools/check/check-p1-5c3.sh
#       编译 + 静态 + 只读入口 + 坏表，不连库不连服（Mac 上就能全跑）
#   MMO_P1_5C3_GATEWAY=http://127.0.0.1:61000 MMO_P1_5C3_TOKEN=<令牌> \
#     MMO_P1_5C3_CHARACTER=<角色号> bash tools/check/check-p1-5c3.sh
#       再加真网关段：买一张红符（扣款、入包、绑定）、元宝与背包对得上
#   MMO_P1_5C3_BATTLE=ws://127.0.0.1:60194/battle \
#     MMO_P1_5C3_BATTLE_DEADLY=ws://127.0.0.1:60195/battle \
#     MMO_P1_5C3_PROBE=<BattleProbe.dll> bash tools/check/check-p1-5c3.sh
#       再加战斗段：用符回血、共用冷却、别的编号、背包没有、倒地
#   MMO_P1_5C3_SKIP_BUILD=1 bash tools/check/check-p1-5c3.sh
#       跳过编译（已经 build 过时用）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY="${MMO_P1_5C3_GATEWAY:-}"
TOKEN="${MMO_P1_5C3_TOKEN:-}"
CHARACTER="${MMO_P1_5C3_CHARACTER:-0}"
BATTLE="${MMO_P1_5C3_BATTLE:-}"
BATTLE_DEADLY="${MMO_P1_5C3_BATTLE_DEADLY:-}"
PROBE="${MMO_P1_5C3_PROBE:-$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll}"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

# ---------------------------------------------------------------- 1. 编译

if [ "${MMO_P1_5C3_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_P1_5C3_SKIP_BUILD=1）\n'
else
  if "$DOTNET" build src/MMOGame.sln -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
    ok "角色服、网关与战斗服编译通过"
  else
    bad "编译失败"; tail -20 "$TMP/build.log" | detail
  fi
fi

# ---------------------------------------------------------------- 2. 静态断言

echo "== 2. 静态断言 =="
ROOT="$ROOT" python3 - "$TMP" <<'PY' > "$TMP/static.txt" 2>&1
import json, os, sys

root = os.environ["ROOT"]
def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

files = {
    "config/talisman.json": read("config/talisman.json"),
    "config/items.json": read("config/items.json"),
    "config/shop.json": read("config/shop.json"),
    "src/Character/TalismanTable.cs": read("src/Character/TalismanTable.cs"),
    "src/Character/TalismanRepository.cs": read("src/Character/TalismanRepository.cs"),
    "src/Battle/TalismanSpend.cs": read("src/Battle/TalismanSpend.cs"),
    "src/Battle/Encounter.cs": read("src/Battle/Encounter.cs"),
    "proto/mmo/battle.proto": read("proto/mmo/battle.proto"),
    "proto/mmo/character.proto": read("proto/mmo/character.proto"),
    "src/Character/CharacterGrpcService.cs": read("src/Character/CharacterGrpcService.cs"),
}
report("files", all(files.values()),
       "missing " + ",".join(k for k, v in files.items() if not v))

talisman = json.loads(files["config/talisman.json"])
items = json.loads(files["config/items.json"])
shop = json.loads(files["config/shop.json"])
red = talisman.get("red") or {}
item_by_id = {i["id"]: i for i in items["items"]}

report("talisman_schema",
       talisman.get("schema") == "talisman-table/1" and int(talisman.get("version", 0)) >= 1,
       str(talisman.get("schema")) + " v" + str(talisman.get("version")))

red_item = red.get("item_id", "")
goods = item_by_id.get(red_item)
report("red_item",
       bool(goods) and goods.get("kind") == "consumable" and goods.get("bind") == "always",
       "item=" + red_item + " " + json.dumps(goods, ensure_ascii=False) if goods else "缺物品")

# 完成标准第三条：回复不超过同级红药。这一条在配置里卡死，不在运行时兜。
report("restore_under_potion",
       int(red.get("hp_restore", 0)) >= 1
       and int(red.get("potion_hp_restore", 0)) >= 1
       and int(red.get("hp_restore", 0)) <= int(red.get("potion_hp_restore", 0)),
       "hp=" + str(red.get("hp_restore")) + " potion=" + str(red.get("potion_hp_restore")))

# 方案 12.1：和红药、牧师治疗共用冷却；蓝符是另一组。
report("cooldown_group_heal",
       red.get("cooldown_group") == "heal" and int(red.get("cooldown_ms", 0)) >= 1,
       "group=" + str(red.get("cooldown_group")) + " ms=" + str(red.get("cooldown_ms")))

red_shop = [i for i in shop["items"] if i["kind"] == "red_talisman"]
report("shop_red_delivery",
       len(red_shop) == 1
       and red_shop[0].get("delivery_item_id") == red_item
       and red_shop[0].get("delivery_count") == 1,
       json.dumps(red_shop, ensure_ascii=False))

table_cs = files["src/Character/TalismanTable.cs"]
report("table_rejects_over_potion",
       "HpRestore > raw.PotionHpRestore" in table_cs
       and "Build" not in table_cs,
       "上限校验不在配置加载里")

shop_cs = read("src/Character/ShopTable.cs")
report("delivered_kinds",
       'DeliveredKinds = { "protect_ticket", "red_talisman" }' in shop_cs,
       "白名单没接红符")

# 请求里没有目标：红符只回复自己，客户端连「想治谁」都表达不了。
battle_proto = files["proto/mmo/battle.proto"]
start = battle_proto.find("message UseTalismanRequest")
end = battle_proto.find("}", start)
use_req = battle_proto[start:end] if start >= 0 else ""
report("no_target_in_request",
       start >= 0 and "target" not in use_req.lower(),
       "请求段里出现了 target：" + use_req.replace("\n", " ")[:120])

# 战斗服这一侧：加血只落在 member 身上，没有任何「选目标」的路径。
enc = files["src/Battle/Encounter.cs"]
u_start = enc.find("public ServerMessage UseTalisman(")
u_end = enc.find("// ---- 时间推进 ----", u_start)
use_body = enc[u_start:u_end] if u_start >= 0 and u_end > u_start else ""
report("only_self_heal",
       u_start >= 0 and u_end > u_start
       and "member.Hp +=" in use_body
       and "TargetId" not in use_body
       and "target." not in use_body,
       "UseTalisman 段里有选目标的痕迹")

# 共用冷却：成员上有一个治疗组字段，heal 技能与红符都读写它。
report("heal_group_shared",
       enc.count("HealGroupReadyAtMs") >= 4
       and "skill.Kind == SkillKind.Heal" in enc,
       "HealGroupReadyAtMs 出现 " + str(enc.count("HealGroupReadyAtMs")) + " 次")

# 回复值只来自角色服回包，战斗服不写第二套数。
report("restore_from_reply",
       "spend.HpRestore" in use_body and "120" not in use_body,
       "用例里出现硬编码回复值")

# 先扣符再回血：扣不掉就不回血。
report("spend_before_heal",
       use_body.find("_talismans.Spend") >= 0
       and use_body.find("_talismans.Spend") < use_body.find("member.Hp +="),
       "扣符与回血的先后不对")

# 倒地不能点符：这就是「不能复活自己」。
report("no_revive_on_self",
       'if (member.Down)' in use_body and 'return "caster_down"' in use_body,
       "没有倒地拒绝")

char_proto = files["proto/mmo/character.proto"]
c_start = char_proto.find("message ConsumeTalismanRequest")
c_end = char_proto.find("message ConsumeTalismanResponse", c_start)
consume_req = char_proto[c_start:c_end] if c_start >= 0 else ""
report("consume_request_body",
       "string account = 1" in consume_req
       and "uint64 character_id = 2" in consume_req
       and "string item_id = 3" in consume_req
       and "string txn_id = 4" in consume_req
       and "hp_restore" not in consume_req
       and "cooldown" not in consume_req,
       "请求体里混进了服务端算的数")

gw_proc = read("src/Gateway/GatewayProcess.cs")
report("no_client_route",
       "talisman" not in gw_proc.lower(),
       "网关不该有红符路由：用符走战斗服到角色服的服间调用")

# 背包的扣减只在角色服：战斗服里不许出现写库语句。
spend_cs = files["src/Battle/TalismanSpend.cs"]
report("spend_in_character_only",
       "delete from" not in spend_cs.lower()
       and "insert into" not in spend_cs.lower()
       and "delete from" in files["src/Character/TalismanRepository.cs"].lower(),
       "战斗服里出现了写库语句")

PY
cat "$TMP/static.txt" | sed 's/^PASS /  ok   /; s/^FAIL /  FAIL /'
grep -q '^PASS ' "$TMP/static.txt" || bad "静态断言没跑起来"
if grep -q '^FAIL ' "$TMP/static.txt"; then
  bad "静态断言有未通过项"
else
  ok "静态断言全部通过"
fi

# ---------------------------------------------------------------- 3. 只读入口

echo "== 3. 只读入口与坏表 =="

if "$DOTNET" "$CHARACTER_DLL" --talisman-list > "$TMP/talis.txt" 2>&1; then
  head1="$(head -1 "$TMP/talis.txt")"
  case "$head1" in
    *"talisman_list_state=ok"*"red_item=talisman_red"*"cooldown_group=heal"*)
      ok "红符表读得出来：$head1" ;;
    *) bad "红符表头部不对：$head1" ;;
  esac
  # 按空格切词再挑前缀完全相等的那一个：sed 的 .* 会贪婪地把
  # potion_hp_restore 也当成 hp_restore，取出来的是上限而不是回复值。
  hp=$(printf '%s\n' "$head1" | python3 -c 'import sys
for t in sys.stdin.read().split():
    if t.startswith("hp_restore="):
        print(t.split("=", 1)[1])')
  potion=$(printf '%s\n' "$head1" | python3 -c 'import sys
for t in sys.stdin.read().split():
    if t.startswith("potion_hp_restore="):
        print(t.split("=", 1)[1])')
  if [ -n "$hp" ] && [ -n "$potion" ] && [ "$hp" -le "$potion" ]; then
    ok "回复 ${hp} 不超过同级红药 ${potion}"
  else
    bad "回复与上限对不上：hp=${hp:-?} potion=${potion:-?}"
  fi
else
  bad "红符表读取失败"; tail -3 "$TMP/talis.txt" | detail
fi

# 六类坏表都要起不来，而且要说清是哪一条。
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
base = json.load(open(os.path.join(root, "config/talisman.json"), encoding="utf-8"))
cases = {
    "over_potion": lambda j: j["red"].__setitem__("hp_restore", j["red"]["potion_hp_restore"] + 1),
    "group_mana": lambda j: j["red"].__setitem__("cooldown_group", "mana"),
    "unknown_item": lambda j: j["red"].__setitem__("item_id", "no_such_item"),
    "not_consumable": lambda j: j["red"].__setitem__("item_id", "ticket_protect"),
    "zero_cooldown": lambda j: j["red"].__setitem__("cooldown_ms", 0),
    "no_red": lambda j: j.pop("red", None),
}
for name, fn in cases.items():
    j = json.loads(json.dumps(base))
    fn(j)
    with open(os.path.join(tmp, name + ".json"), "w", encoding="utf-8") as f:
        json.dump(j, f, ensure_ascii=False)
PY

for bad_case in over_potion group_mana unknown_item not_consumable zero_cooldown no_red; do
  out="$("$DOTNET" "$CHARACTER_DLL" --talisman-list --talisman-config "$TMP/$bad_case.json" 2>&1)"
  code=$?
  if [ "$code" = "2" ] && printf '%s' "$out" | grep -q 'config_error='; then
    ok "坏表 ${bad_case}：起不来（$(printf '%s' "$out" | head -1 | cut -c1-46)…）"
  else
    bad "坏表 ${bad_case} 没被挡住（exit=$code）：$out"
  fi
done

# 商城清单里红符这一条已经接上发货，保护券那一条没被带坏。
if "$DOTNET" "$CHARACTER_DLL" --shop-list > "$TMP/shop.txt" 2>&1; then
  if grep -q 'shop_item=shop_red_talisman_1 .*delivery=talisman_redx1' "$TMP/shop.txt" \
     && grep -q 'shop_item=shop_protect_ticket_1 .*delivery=ticket_protectx1' "$TMP/shop.txt"; then
    ok "清单里红符发 talisman_redx1、保护券仍发 ticket_protectx1"
  else
    bad "清单里的发货物不对"; grep -E 'shop_item=shop_(red|protect)' "$TMP/shop.txt" | detail
  fi
else
  bad "清单读取失败"; tail -3 "$TMP/shop.txt" | detail
fi

# ---------------------------------------------------------------- 4. 真网关段

echo "== 4. 真网关段 =="
if [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （跳过：没给 MMO_P1_5C3_GATEWAY / MMO_P1_5C3_TOKEN / MMO_P1_5C3_CHARACTER）\n'
else
  red_count_of() {
    printf '%s' "$1" | python3 -c 'import json,sys
try:
    d = json.load(sys.stdin)
except Exception:
    print(-1); raise SystemExit
n = 0
for s in d.get("slots", []):
    if s.get("item_id") == "talisman_red":
        n += int(s.get("count") or 0)
print(n)'
  }

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

  bag() {
    curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
      "$GATEWAY/inventory?character_id=$CHARACTER" 2>&1
  }

  money() {
    curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" \
      "$GATEWAY/currency?character_id=$CHARACTER" 2>&1
  }

  before_money="$(yuanbao_of "$(money)")"
  before_red="$(red_count_of "$(bag)")"

  resp="$(buy "{\"character_id\":$CHARACTER,\"item_id\":\"shop_red_talisman_1\"}")"
  body="$(body_of "$resp")"
  if [ "$(expect_json "$body" ok=true delivered=true bound=true)" = "ok" ] \
     && [ "$(json_field "$body" charged_yuanbao)" = "15" ]; then
    ok "买一张红符：扣 15 元宝、入包、到账即绑"
  else
    bad "买红符的回包不对：$body"
  fi

  after_money="$(yuanbao_of "$(money)")"
  after_red="$(red_count_of "$(bag)")"
  if [ -n "$before_money" ] && [ "$after_money" = "$((before_money - 15))" ]; then
    ok "元宝 $before_money → $after_money"
  else
    bad "元宝不对：before=${before_money:-?} after=${after_money:-?}"
  fi
  if [ "$after_red" = "$((before_red + 1))" ]; then
    ok "背包红符 $before_red → $after_red"
  else
    bad "背包红符不对：before=${before_red:-?} after=${after_red:-?}"
  fi

  # 清单外仍然是不认识，不因为接了红符就放宽。
  resp="$(buy "{\"character_id\":$CHARACTER,\"item_id\":\"shop_no_such_item\"}")"
  body="$(body_of "$resp")"
  if [ "$(expect_json "$body" ok=false reason=not_in_list)" = "ok" ]; then
    ok "清单外：仍是 not_in_list"
  else
    bad "清单外的回包不对：$body"
  fi
fi

# ---------------------------------------------------------------- 5. 战斗段

echo "== 5. 战斗段 =="
if [ -z "$BATTLE" ] || [ ! -f "$PROBE" ]; then
  printf '       （跳过：没给 MMO_P1_5C3_BATTLE 或找不到 BattleProbe）\n'
else
  run_probe() {
    local url="$1"; shift
    local out
    out="$("$DOTNET" "$PROBE" --url "$url" "$@" 2>&1)"
    printf '%s\n' "$out" | tail -3 | detail
    printf '%s' "$out" | grep -q 'verdict=ok'
  }

  if run_probe "$BATTLE" --case p15c3_talisman --item talisman_red; then
    ok "用掉一张：回自己的血、写 heal 组冷却，紧接着再用撞共用冷却"
  else
    bad "用符成功与共用冷却这一条没过"
  fi

  # 牧师那条要换职业：BATTLE 里已经带了角色号，这里只追加 class，不重拼 URL。
  if run_probe "$BATTLE&class=priest" --case p15c3_talisman_heal_share \
       --item talisman_red --heal 1302; then
    ok "用符之后牧师治疗撞冷却（共用治疗组）"
  else
    bad "红符与牧师治疗没共用冷却"
  fi

  if run_probe "$BATTLE" --case p15c3_talisman_other --other ticket_protect; then
    ok "拿保护券当符用：not_talisman，不扣不回血"
  else
    bad "别的物品编号没被挡住"
  fi

  if run_probe "$BATTLE" --case p15c3_talisman_full --item talisman_red; then
    ok "满血用符：hp_full，不去扣符"
  else
    bad "满血没被挡住（会白烧一张）"
  fi

  # 这一条要的是「背包一张不剩」，靠上面两个用符场景各扣一张把符用光；
  # 顺序不能调换。万一余量不对，先把背包数出来，免得把「还有符」读成「没符还能用」。
  left_red="?"
  if [ -n "$GATEWAY" ] && [ -n "$TOKEN" ] && [ "$CHARACTER" != "0" ]; then
    left_red="$(red_count_of "$(curl -sS --noproxy '*' -m 10 \
      -H "Authorization: Bearer $TOKEN" \
      "$GATEWAY/inventory?character_id=$CHARACTER" 2>&1)")"
    printf '%s\n' "用掉两张之后背包红符 left=$left_red" | detail
  fi

  if run_probe "$BATTLE" --case p15c3_talisman_none --item talisman_red; then
    ok "背包一张不剩：no_talisman，不回血"
  else
    bad "没符还能用（跑到这里时背包余量 left=${left_red}，不是 0 就说明前面的用符场景没各扣一张）"
  fi

  if [ -n "$BATTLE_DEADLY" ]; then
    if run_probe "$BATTLE_DEADLY" --case p15c3_talisman_down --item talisman_red; then
      ok "倒地用符：caster_down（不能复活自己）"
    else
      bad "倒地还能用符"
    fi
  else
    printf '       （跳过倒地那一条：没给 MMO_P1_5C3_BATTLE_DEADLY）\n'
  fi
fi

finish "P1-5c3（server + battle）"
