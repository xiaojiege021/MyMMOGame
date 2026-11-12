#!/usr/bin/env bash
# P1-5c6 核对：复活卷轴只拉起自己（方案 8.3 / 12.1、开发架构第 14 节第 45 行）。
#
# 完成标准：不能拉队友。换一场后冷却还在。当前级经验是零时不扣，不掉级。
#
# 四种模式：
#   bash tools/check/check-p1-5c6.sh
#       编译 + 静态 + 只读入口 + 坏表，不连库不连服（Mac 上就能全跑）
#   MMO_P1_5C6_GATEWAY=http://127.0.0.1:61000 MMO_P1_5C6_TOKEN=<令牌> \
#     MMO_P1_5C6_CHARACTER=<角色号> bash tools/check/check-p1-5c6.sh
#       再加真网关段：买一张卷轴（扣款、入包、绑定）
#   MMO_P1_5C6_BATTLE / _BATTLE_DEADLY / _BATTLE_SECOND / _BATTLE_OFF / _PROBE
#       再加战斗段：只拉起自己、冷却跨场、经验为零、不占符组、五类拒绝
#   MMO_P1_5C6_SKIP_BUILD=1 bash tools/check/check-p1-5c6.sh
#       跳过编译（已经 build 过时用）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY="${MMO_P1_5C6_GATEWAY:-}"
TOKEN="${MMO_P1_5C6_TOKEN:-}"
CHARACTER="${MMO_P1_5C6_CHARACTER:-0}"
BATTLE="${MMO_P1_5C6_BATTLE:-}"
BATTLE_DEADLY="${MMO_P1_5C6_BATTLE_DEADLY:-}"
BATTLE_SECOND="${MMO_P1_5C6_BATTLE_SECOND:-}"
BATTLE_OFF="${MMO_P1_5C6_BATTLE_OFF:-}"
PROBE="${MMO_P1_5C6_PROBE:-$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll}"
SCROLL="${MMO_P1_5C6_SCROLL:-scroll_revive}"
PRICE="${MMO_P1_5C6_PRICE:-80}"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

# ---------------------------------------------------------------- 1. 编译

echo "== 1. 编译 =="
if [ "${MMO_P1_5C6_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_P1_5C6_SKIP_BUILD=1）\n'
else
  if "$DOTNET" build src/MMOGame.sln -c Release --nologo -v q > "$TMP/build.log" 2>&1 \
     && "$DOTNET" build tools/probe/BattleProbe/BattleProbe.csproj -c Release --nologo -v q \
          > "$TMP/build-probe.log" 2>&1; then
    ok "服务端与探针编译通过"
  else
    bad "编译失败"; tail -20 "$TMP/build.log" "$TMP/build-probe.log" | detail
  fi
fi

# ---------------------------------------------------------------- 2. 静态断言

echo "== 2. 静态断言 =="
ROOT="$ROOT" python3 - "$TMP" <<'PY' > "$TMP/static.txt" 2>&1
import json, os, re, sys

root = os.environ["ROOT"]
def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

files = {
    "config/revive_scroll.json": read("config/revive_scroll.json"),
    "config/items.json": read("config/items.json"),
    "config/shop.json": read("config/shop.json"),
    "config/classes.json": read("config/classes.json"),
    "config/skills.json": read("config/skills.json"),
    "db/character/revive_scroll.sql": read("db/character/revive_scroll.sql"),
    "src/Character/ReviveScrollTable.cs": read("src/Character/ReviveScrollTable.cs"),
    "src/Character/ReviveScrollRepository.cs": read("src/Character/ReviveScrollRepository.cs"),
    "src/Character/ExpSettlementRepository.cs": read("src/Character/ExpSettlementRepository.cs"),
    "src/Character/ShopTable.cs": read("src/Character/ShopTable.cs"),
    "src/Character/CharacterGrpcService.cs": read("src/Character/CharacterGrpcService.cs"),
    "src/Character/Program.cs": read("src/Character/Program.cs"),
    "src/Battle/ReviveScrollUse.cs": read("src/Battle/ReviveScrollUse.cs"),
    "src/Battle/Encounter.cs": read("src/Battle/Encounter.cs"),
    "src/Battle/Server.cs": read("src/Battle/Server.cs"),
    "proto/mmo/battle.proto": read("proto/mmo/battle.proto"),
    "proto/mmo/character.proto": read("proto/mmo/character.proto"),
    "src/Gateway/GatewayProcess.cs": read("src/Gateway/GatewayProcess.cs"),
    "tools/probe/BattleProbe/Program.cs": read("tools/probe/BattleProbe/Program.cs"),
}
report("files", all(files.values()),
       "missing " + ",".join(k for k, v in files.items() if not v))

scroll_cfg = json.loads(files["config/revive_scroll.json"])
items = json.loads(files["config/items.json"])
shop = json.loads(files["config/shop.json"])
classes = json.loads(files["config/classes.json"])
skills = json.loads(files["config/skills.json"])
scroll = scroll_cfg.get("scroll") or {}
item_ids = [i["id"] for i in items["items"]]
item_by_id = {i["id"]: i for i in items["items"]}
scroll_item = scroll.get("item_id", "")
goods = item_by_id.get(scroll_item)

# ItemTable.Load 用 map[id] = ... 装载，重复编号会静默覆盖；卷轴物品 P1-1b3 已有，
# 本卡不再加第二条。钉住全表唯一，免得「物品定义」悄悄变成两份。
report("item_ids_unique",
       len(item_ids) == len(set(item_ids)),
       "重复编号：" + ",".join(sorted({k for k in item_ids if item_ids.count(k) > 1})))

report("scroll_schema",
       scroll_cfg.get("schema") == "revive-scroll-table/1"
       and int(scroll_cfg.get("version", 0)) >= 1)
report("scroll_item",
       bool(goods) and goods.get("kind") == "consumable"
       and goods.get("bind") == "always",
       "item=" + scroll_item + " " + json.dumps(goods, ensure_ascii=False) if goods else "缺物品")
report("cooldown_in_config",
       int(scroll.get("cooldown_ms", 0)) > 0
       and "60000" not in files["src/Character/ReviveScrollTable.cs"]
       and "60000" not in files["src/Battle/Encounter.cs"],
       "冷却时长不在配置里，或被写死在源码里")

# 方案 8.3：卷轴冷却「时间长」「比生息技能长」。生息复活技能在 skills.json。
priest = [s for s in skills["skills"] if s.get("kind") == "resurrect"]
report("longer_than_priest",
       len(priest) >= 1
       and int(scroll.get("cooldown_ms", 0)) > max(int(s.get("cooldown_ms", 0)) for s in priest),
       "scroll=" + str(scroll.get("cooldown_ms")) + " priest=" + str([s.get("cooldown_ms") for s in priest]))

# 一转基础比例唯一正本在 classes.json，卷轴配置不抄第二套数。
report("base_from_classes",
       "hp_restore_pct" not in scroll and "exp_cost_pct" not in scroll
       and int(classes.get("resurrect", {}).get("hp_restore_pct", 0)) < 100
       and 0 < int(classes.get("resurrect", {}).get("exp_cost_pct", 0)) < 100
       and "GetProperty(\"resurrect\")" in files["src/Character/ReviveScrollTable.cs"],
       "比例被抄进卷轴配置，或没从职业表读")

shop_scroll = [i for i in shop["items"] if i["kind"] == "revive_scroll"]
report("shop_scroll_delivery",
       len(shop_scroll) == 1
       and shop_scroll[0].get("delivery_item_id") == scroll_item
       and shop_scroll[0].get("delivery_count") == 1,
       json.dumps(shop_scroll, ensure_ascii=False))
report("shop_version", int(shop.get("version", 0)) >= 5)
# 只钉本卡相关的三种，不把「已接发货全集」写死（以后每种新卡都会加）。
shop_cs = files["src/Character/ShopTable.cs"]
kinds = re.search(r"DeliveredKinds\s*=\s*(?:new\s*)?(?:string\[\]\s*)?\{([^}]*)\}", shop_cs)
kinds_text = kinds.group(1) if kinds else ""
report("delivered_kinds",
       all(('"' + k + '"') in kinds_text
           for k in ("protect_ticket", "red_talisman", "blue_talisman", "revive_scroll")),
       kinds_text)
report("shop_scroll_check",
       "revive_scroll.json 的 scroll.item_id" in shop_cs
       and "scrolls.ReviveScroll" in shop_cs)
# 方案 12.1「复活只卖复活卷轴，不另卖复活币」。
report("shop_no_revive_coin",
       not any("coin" in (i.get("kind") or "") for i in shop["items"]))

table_cs = files["src/Character/ReviveScrollTable.cs"]
report("table_rejects_bad_scroll",
       all(s in table_cs for s in (
           "缺 scroll 段", "scroll.item_id 不能是空的", "不在 items.json 里",
           "必须是 consumable", "必须到账即绑", "cooldown_ms 必须是正整数",
           "不能和符是同一个编号")))
report("table_rejects_bad_base",
       "hp_restore_pct 要在 [1,100)" in table_cs
       and "exp_cost_pct 要在 [1,100)" in table_cs)

# 冷却到期是角色库的持久数据，不是战斗进程内存。
sql = files["db/character/revive_scroll.sql"]
report("cooldown_table",
       "CREATE TABLE IF NOT EXISTS" in sql
       and "revive_scroll_cooldown" in sql
       and "ready_at" in sql
       and "character_id" in sql)
exp_repo = files["src/Character/ExpSettlementRepository.cs"]
u_start = exp_repo.find("public async Task<ReviveScrollOutcome> UseReviveScrollAsync(")
u_end = exp_repo.find("/// <summary>", u_start + 10)
use_tx = exp_repo[u_start:u_end] if u_start >= 0 and u_end > u_start else ""
report("one_atomic_tx",
       u_start >= 0 and u_end > u_start
       and "BeginTransactionAsync" in use_tx
       and "CommitAsync" in use_tx
       and use_tx.count("RollbackAsync") >= 4
       and "revive_scroll_cooldown" in use_tx
       and "grant_txn" in use_tx,
       "扣卷 / 经验 / 冷却没在同一笔事务里")
# 等级经验的写入仍集中在 ExpSettlementRepository；本方法只写 exp，不写 level。
report("exp_write_only_one",
       'set exp = exp - $2' in use_tx
       and 'set level' not in use_tx
       and 'var cost = exp * expCostPct / 100' in use_tx,
       "卷轴另开了一条写 level/exp 的路")
report("zero_exp_no_cost",
       "exp * expCostPct / 100" in use_tx
       and "exp = exp - $2" in use_tx
       and "level = $2" not in use_tx)
repo_cs = files["src/Character/ReviveScrollRepository.cs"]
report("state_reads_coolrow",
       "revive_scroll_cooldown" in repo_cs
       and "ready_at" in repo_cs
       and "Cooling =>" in repo_cs)

# 只读查询不写库；扣卷请求里没有任何服务端数值。
char_proto = files["proto/mmo/character.proto"]
d_start = char_proto.find("message DescribeReviveScrollRequest")
d_end = char_proto.find("message DescribeReviveScrollResponse", d_start)
describe_req = char_proto[d_start:d_end] if d_start >= 0 else ""
report("describe_request_readonly",
       d_start >= 0 and "character_id" in describe_req and "item_id" in describe_req
       and "target" not in describe_req and "cooldown" not in describe_req
       and "restore" not in describe_req)
svc = files["src/Character/CharacterGrpcService.cs"]
s_start = svc.find("public override async Task<DescribeReviveScrollResponse> DescribeReviveScroll(")
s_end = svc.find("    /// <summary>", s_start + 10)
describe_body = svc[s_start:s_end] if s_start >= 0 and s_end > s_start else ""
report("describe_no_write",
       "scrolls.StateAsync" in describe_body
       and "UseReviveScrollAsync" not in describe_body
       and "insert" not in describe_body.lower()
       and "delete" not in describe_body.lower())
c_start = char_proto.find("message ConsumeReviveScrollRequest")
c_end = char_proto.find("message ConsumeReviveScrollResponse", c_start)
consume_req = char_proto[c_start:c_end] if c_start >= 0 else ""
report("consume_request_body",
       all(s in consume_req for s in (
           "string account = 1", "uint64 character_id = 2",
           "string item_id = 3", "string txn_id = 4"))
       and "target" not in consume_req
       and "pct" not in consume_req
       and "cooldown" not in consume_req
       and "hp_restore" not in consume_req,
       "请求体里混进了服务端算的数")

# 客户端到战斗服的请求结构上没有目标：表达不出「拉队友」。
battle_proto = files["proto/mmo/battle.proto"]
r_start = battle_proto.find("message UseReviveScrollRequest")
r_end = battle_proto.find("}", r_start)
use_req = battle_proto[r_start:r_end] if r_start >= 0 else ""
report("no_target_in_request",
       r_start >= 0 and "target" not in use_req.lower()
       and "item_id" in use_req,
       "请求段里出现了 target：" + use_req.replace("\n", " ")[:120])

enc = files["src/Battle/Encounter.cs"]
b_start = enc.find("public ServerMessage UseReviveScroll(")
b_end = enc.find("// ---- 卷轴用毕 ----", b_start)
body = enc[b_start:b_end] if b_start >= 0 and b_end > b_start else ""
report("self_only",
       b_start >= 0 and b_end > b_start
       and "TargetId" not in body and "target." not in body
       and "_members.Values" not in body
       and "member.Hp = restore" in body,
       "卷轴拉起了别人，或者没把本人拉起来")
report("no_other_pool",
       "member.Mana +=" not in body and "member.Mana =" not in body
       and "member.Hp +=" not in body
       and "DamageDealt" not in body and "Spirit" not in body
       and "ManaMax =" not in body)
report("no_skill_cd",
       "member.ReadyAtMs" not in body
       and "ReadyAtMs[" not in body
       and "HealGroup" not in body
       and "ManaGroup" not in body,
       "卷轴开始了生息或符的冷却")
report("restore_from_reply",
       "used.HpRestorePct" in body and "50" not in body,
       "拉起血量不认角色服回包")
report("describe_before_use",
       0 <= body.find("_scrolls.Describe") < body.find("_scrolls.Use"),
       "没先查冷却 / 背包就去扣卷轴")
report("use_before_apply",
       body.find("_scrolls.Use") < body.find("member.Hp = restore"),
       "扣卷与拉起的先后不对")
report("coolrow_from_character",
       "info.NowMs < info.ReadyAtMs" in body
       and "revive_scroll_cooldown" not in body,
       "冷却不是读角色库那一行")
report("reject_paths",
       all(('"' + r + '"') in body for r in (
           "not_in_battle", "left_battle", "not_down", "cooldown", "no_scroll")))

# 冷却只有角色服能写；核对用的清冷却入口只在只读/核对程序里。
hits = []
for base, _, names in os.walk(os.path.join(root, "src")):
    for n in names:
        if n.endswith(".cs") and not n.endswith(".g.cs"):
            p = os.path.join(base, n)
            # C# 里的 SQL 是 \"角色\" 转义文本，别按裸引号去比对；逐行找同一条语句。
            for line in open(p, encoding="utf-8"):
                if "delete from" in line and "revive_scroll_cooldown" in line:
                    hits.append(os.path.relpath(p, root))
                    break
report("reset_only_for_check", hits == ["src/Character/Program.cs"], ",".join(hits))

# 卷轴编号不硬编码在源码里；网关不开用卷轴的路由。
hits = []
for base, _, names in os.walk(os.path.join(root, "src")):
    for n in names:
        if n.endswith(".cs") and not n.endswith(".g.cs"):
            p = os.path.join(base, n)
            if '"' + scroll_item + '"' in open(p, encoding="utf-8").read():
                hits.append(os.path.relpath(p, root))
report("no_hardcoded_scroll_item", not hits, ",".join(hits))
report("no_client_route",
       "scroll" not in files["src/Gateway/GatewayProcess.cs"].lower(),
       "网关不该有用卷轴路由：用卷走战斗服到角色服的服间调用")

probe = files["tools/probe/BattleProbe/Program.cs"]
report("probe_cases",
       all(('"' + c + '"') in probe for c in (
           "p15c6_scroll", "p15c6_scroll_exp_zero", "p15c6_scroll_cooldown",
           "p15c6_scroll_cross", "p15c6_scroll_groups", "p15c6_scroll_not_down",
           "p15c6_scroll_other", "p15c6_scroll_none", "p15c6_scroll_off")))
PY
sed 's/^PASS /  ok   /; s/^FAIL /  FAIL /' "$TMP/static.txt"
grep -q '^PASS ' "$TMP/static.txt" || bad "静态断言没跑起来"
if grep -q '^FAIL ' "$TMP/static.txt"; then
  bad "静态断言有未通过项"
else
  ok "静态断言全过（$(grep -c '^PASS ' "$TMP/static.txt") 条）"
fi

# ---------------------------------------------------------------- 3. 只读入口

echo "== 3. 只读入口与坏表 =="

if "$DOTNET" "$CHARACTER_DLL" --revive-scroll-list > "$TMP/scroll.txt" 2>&1; then
  head1="$(head -1 "$TMP/scroll.txt")"
  case "$head1" in
    *"revive_scroll_list_state=ok"*"item=$SCROLL"*"cooldown_ms="*"hp_restore_pct="*"exp_cost_pct="*)
      ok "卷轴表读得出来：$head1" ;;
    *) bad "卷轴表头部不对：$head1" ;;
  esac
  # 按空格切词、只认前缀完全相等的那一个（sed 的 .* 会贪婪匹配到别的字段）。
  word_of() { printf '%s\n' "$head1" | python3 -c 'import sys
key = sys.argv[1]
for t in sys.stdin.read().split():
    if t.startswith(key + "="):
        print(t.split("=", 1)[1]); break' "$1"; }
  cd_ms="$(word_of cooldown_ms)"
  hp_pct="$(word_of hp_restore_pct)"
  exp_pct="$(word_of exp_cost_pct)"
  priest_ms="$(python3 -c 'import json
d=json.load(open("config/skills.json",encoding="utf-8"))
print(max(int(s.get("cooldown_ms",0)) for s in d["skills"] if s.get("kind")=="resurrect"))')"
  if [ -n "$cd_ms" ] && [ "$cd_ms" -gt "$priest_ms" ] \
     && [ -n "$hp_pct" ] && [ "$hp_pct" -lt 100 ] \
     && [ -n "$exp_pct" ] && [ "$exp_pct" -gt 0 ] && [ "$exp_pct" -lt 100 ]; then
    ok "冷却 ${cd_ms}ms 比生息复活 ${priest_ms}ms 长；拉起 ${hp_pct}% 不满血；基础扣经验 ${exp_pct}%"
  else
    bad "配置数值不对：cd=${cd_ms:-?} priest=${priest_ms:-?} hp=${hp_pct:-?} exp=${exp_pct:-?}"
  fi
else
  bad "卷轴表读取失败"; tail -3 "$TMP/scroll.txt" | detail
fi

# 卷轴表与职业表两路坏配置都要起不来，而且要说清是哪一条。
ROOT="$ROOT" TMP="$TMP" python3 - <<'PY'
import json, os
root, tmp = os.environ["ROOT"], os.environ["TMP"]
scroll = json.load(open(os.path.join(root, "config/revive_scroll.json"), encoding="utf-8"))
classes = json.load(open(os.path.join(root, "config/classes.json"), encoding="utf-8"))
cases = {
    "bad_schema":    lambda j: j.__setitem__("schema", "revive-scroll-table/0"),
    "zero_version":  lambda j: j.__setitem__("version", 0),
    "no_scroll":     lambda j: j.pop("scroll", None),
    "empty_item":    lambda j: j["scroll"].__setitem__("item_id", ""),
    "unknown_item":  lambda j: j["scroll"].__setitem__("item_id", "no_such_item"),
    "voucher_item":  lambda j: j["scroll"].__setitem__("item_id", "ticket_protect"),
    "same_as_red":   lambda j: j["scroll"].__setitem__("item_id", "talisman_red"),
    "zero_cooldown": lambda j: j["scroll"].__setitem__("cooldown_ms", 0),
    "neg_cooldown":  lambda j: j["scroll"].__setitem__("cooldown_ms", -1),
}
for name, fn in cases.items():
    j = json.loads(json.dumps(scroll))
    fn(j)
    with open(os.path.join(tmp, name + ".json"), "w", encoding="utf-8") as f:
        json.dump(j, f, ensure_ascii=False)

classes_cases = {
    "hp_pct_full":   lambda j: j["resurrect"].__setitem__("hp_restore_pct", 100),
    "exp_pct_zero":  lambda j: j["resurrect"].__setitem__("exp_cost_pct", 0),
    "exp_pct_full":  lambda j: j["resurrect"].__setitem__("exp_cost_pct", 100),
    "no_resurrect":  lambda j: j.pop("resurrect", None),
}
for name, fn in classes_cases.items():
    j = json.loads(json.dumps(classes))
    fn(j)
    with open(os.path.join(tmp, name + "-classes.json"), "w", encoding="utf-8") as f:
        json.dump(j, f, ensure_ascii=False)
PY

for bad_case in bad_schema zero_version no_scroll empty_item unknown_item \
                voucher_item same_as_red zero_cooldown neg_cooldown; do
  out="$("$DOTNET" "$CHARACTER_DLL" --revive-scroll-list \
        --revive-scroll-config "$TMP/${bad_case}.json" 2>&1)"
  code=$?
  if [ "$code" = "2" ] && printf '%s' "$out" | grep -q 'config_error='; then
    ok "坏表 ${bad_case}：起不来（$(printf '%s' "$out" | head -1 | cut -c1-46)…）"
  else
    bad "坏表 ${bad_case} 没被挡住（exit=$code）：$out"
  fi
done

for bad_case in hp_pct_full exp_pct_zero exp_pct_full no_resurrect; do
  out="$("$DOTNET" "$CHARACTER_DLL" --revive-scroll-list \
        --revive-scroll-classes "$TMP/${bad_case}-classes.json" 2>&1)"
  code=$?
  if [ "$code" = "2" ] && printf '%s' "$out" | grep -q 'config_error='; then
    ok "坏职业表 ${bad_case}：起不来（$(printf '%s' "$out" | head -1 | cut -c1-46)…）"
  else
    bad "坏职业表 ${bad_case} 没被挡住（exit=$code）：$out"
  fi
done

if "$DOTNET" "$CHARACTER_DLL" --shop-list > "$TMP/shop.txt" 2>&1; then
  if grep -q "shop_item=shop_revive_scroll_1 .*delivery=${SCROLL}x1" "$TMP/shop.txt" \
     && grep -q 'shop_item=shop_red_talisman_1 .*delivery=talisman_redx1' "$TMP/shop.txt" \
     && grep -q 'shop_item=shop_blue_talisman_1 .*delivery=talisman_bluex1' "$TMP/shop.txt"; then
    ok "清单里卷轴发 ${SCROLL}x1，红符 / 蓝符发货未被带坏"
  else
    bad "清单里的发货物不对"; grep -E 'shop_item=shop_(revive_scroll|red_talisman|blue_talisman)' "$TMP/shop.txt" | detail
  fi
else
  bad "清单读取失败"; tail -3 "$TMP/shop.txt" | detail
fi

# ---------------------------------------------------------------- 4. 真网关段

echo "== 4. 真网关段 =="
if [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （跳过：没给 MMO_P1_5C6_GATEWAY / _TOKEN / _CHARACTER）\n'
else
  scroll_of() {
    printf '%s' "$1" | python3 -c 'import json,sys
try: d = json.load(sys.stdin)
except Exception: print("count=-1 bound=false"); raise SystemExit
slots = [s for s in d.get("slots", []) if s.get("item_id") == sys.argv[1]]
print("count=%d bound=%s" % (
    sum(int(s.get("count") or 0) for s in slots),
    "true" if slots and all(s.get("bound") for s in slots) else "false"))' "$SCROLL"
  }
  yuanbao_of() {
    printf '%s' "$1" | python3 -c 'import json,sys
try: d = json.load(sys.stdin)
except Exception: print(""); raise SystemExit
for b in d.get("balances", []):
    if b.get("kind") == "yuanbao": print(b.get("amount")); break
else: print("")'
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
  before_scroll="$(scroll_of "$(bag)")"

  resp="$(buy "{\"character_id\":$CHARACTER,\"item_id\":\"shop_revive_scroll_1\"}")"
  body="$(body_of "$resp")"
  if [ "$(expect_json "$body" ok=true delivered=true bound=true)" = "ok" ] \
     && [ "$(json_field "$body" charged_yuanbao)" = "$PRICE" ]; then
    ok "买一张卷轴：扣 ${PRICE} 元宝、入包、到账即绑"
  else
    bad "买卷轴的回包不对：$body"
  fi

  after_money="$(yuanbao_of "$(money)")"
  after_scroll="$(scroll_of "$(bag)")"
  if [ -n "$before_money" ] && [ "$after_money" = "$((before_money - PRICE))" ]; then
    ok "元宝 ${before_money} → ${after_money}"
  else
    bad "元宝不对：before=${before_money:-?} after=${after_money:-?}"
  fi
  before_count="${before_scroll#count=}"; before_count="${before_count%% *}"
  after_count="${after_scroll#count=}"; after_count="${after_count%% *}"
  if [ "$after_count" = "$((before_count + 1))" ] \
     && printf '%s' "$after_scroll" | grep -q 'bound=true'; then
    ok "背包卷轴 ${before_count} → ${after_count}，且到账即绑"
  else
    bad "背包卷轴不对：before=${before_scroll} after=${after_scroll}"
  fi

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
if [ -z "$BATTLE" ] || [ -z "$BATTLE_DEADLY" ] || [ ! -f "$PROBE" ]; then
  printf '       （跳过：没给 MMO_P1_5C6_BATTLE / _BATTLE_DEADLY 或找不到 BattleProbe）\n'
else
  run_probe() {
    local url="$1"; shift
    local out
    out="$("$DOTNET" "$PROBE" --url "$url" "$@" 2>&1)"
    printf '%s\n' "$out" | tail -3 | detail
    printf '%s' "$out" | grep -q 'verdict=ok'
  }

  # 只给核对用的两个库入口：清全局冷却、设定当前级经验。
  reset_cooldown() {
    "$DOTNET" "$CHARACTER_DLL" --scroll-cooldown-reset "$CHARACTER" \
      --config config/character.json 2>&1 | grep -q '^scroll_cooldown_reset=ok'
  }
  set_exp() {
    "$DOTNET" "$CHARACTER_DLL" --raw-exp-write "$CHARACTER" "$1" \
      --config config/character.json 2>&1 | grep -q '^raw_exp=written'
  }
  state() {
    "$DOTNET" "$CHARACTER_DLL" --revive-scroll-state "$CHARACTER" \
      --config config/character.json 2>&1 | grep '^revive_scroll_state=ok'
  }
  field_of() {
    printf '%s\n' "$1" | python3 -c 'import sys
key = sys.argv[1]
for t in sys.stdin.read().split():
    if t.startswith(key + "="):
        print(t.split("=", 1)[1]); break' "$2"
  }

  # 起手清零：这个角色上一轮可能留过冷却与经验。
  if reset_cooldown && set_exp 1000; then
    ok "核对起手：清冷却、当前级经验置 1000"
  else
    bad "核对起手失败（库连接或新表没建？）"
  fi

  if run_probe "$BATTLE_DEADLY" --case p15c6_scroll --scroll "$SCROLL" \
       --exp-before 1000 --exp-cost 100 --expect-level 1; then
    ok "倒地用卷轴：只拉起自己、回到一小段生命、不回蓝、扣当前级经验 100、等级不变"
  else
    bad "卷轴没做到只拉起自己，或经验 / 血量 / 蓝量不对"
  fi

  after_first="$(state)"
  ready_first="$(field_of "$after_first" ready_at_ms)"
  if printf '%s' "$after_first" | grep -q 'cooling=true' \
     && printf '%s' "$after_first" | grep -q 'exp=900' \
     && printf '%s' "$after_first" | grep -q 'level=1' \
     && [ -n "$ready_first" ] && [ "$ready_first" -gt 0 ]; then
    ok "从角色库独立读回：冷却到期 ${ready_first}、exp=900、level=1"
  else
    bad "用卷之后角色库状态不对：$after_first"
  fi

  # 换一套战斗进程（另起的一场）再试：冷却必须从角色库读出来，不能在新场里消失。
  if [ -n "$BATTLE_SECOND" ]; then
    if run_probe "$BATTLE_SECOND" --case p15c6_scroll_cross --scroll "$SCROLL"; then
      ok "换一场（另一套战斗进程）：cooldown，冷却跨场保留"
    else
      bad "换一场后冷却不见了"
    fi
    after_cross="$(state)"
    if [ "$(field_of "$after_cross" ready_at_ms)" = "$ready_first" ] \
       && printf '%s' "$after_cross" | grep -q 'cooling=true'; then
      ok "跨场前后同一个冷却到期时刻，没被新场重写"
    else
      bad "跨场后冷却到期被改了：before=$ready_first after=$after_cross"
    fi
  else
    printf '       （跳过换进程那一条：没给 MMO_P1_5C6_BATTLE_SECOND）\n'
  fi

  if run_probe "$BATTLE_DEADLY" --case p15c6_scroll_cooldown --scroll "$SCROLL"; then
    ok "同一场再次倒地：cooldown，卷轴不扣、人不起来"
  else
    bad "冷却期内还能再用一张"
  fi

  # 当前级经验是零：这一次不扣经验、等级不变，卷轴仍然生效。
  if reset_cooldown && set_exp 0; then
    if run_probe "$BATTLE_DEADLY" --case p15c6_scroll_exp_zero --scroll "$SCROLL" \
         --exp-before 0 --exp-cost 0 --expect-level 1; then
      ok "当前级经验是零：卷轴仍拉起，扣 0 经验、等级不变、不掉级"
    else
      bad "零经验时扣了经验或掉了级"
    fi
    zero_state="$(state)"
    if printf '%s' "$zero_state" | grep -q 'exp=0' \
       && printf '%s' "$zero_state" | grep -q 'level=1'; then
      ok "从角色库独立读回：exp=0、level=1（不掉级、不扣上一等级）"
    else
      bad "零经验用卷之后库里状态不对：$zero_state"
    fi
  else
    bad "零经验用例的起手没准备好"
  fi

  # 卷轴不占用治疗组：拉起之后红符立刻能用（牧师治疗共用这一组）。
  if reset_cooldown; then
    if run_probe "$BATTLE_DEADLY" --case p15c6_scroll_groups --scroll "$SCROLL" \
         --item talisman_red; then
      ok "卷轴之后红符立刻能用：治疗组没被占用，生息冷却也没开始"
    else
      bad "卷轴占用了符或生息的冷却"
    fi
  else
    bad "卷轴 + 红符用例的起手没准备好"
  fi

  if run_probe "$BATTLE" --case p15c6_scroll_not_down --scroll "$SCROLL"; then
    ok "还活着用卷轴：not_down，不扣不写"
  else
    bad "活着还能自救"
  fi

  if run_probe "$BATTLE" --case p15c6_scroll_other --other ticket_protect; then
    ok "拿保护券当卷轴：not_scroll，不扣不写"
  else
    bad "别的物品编号没被挡住"
  fi

  # 「背包一张不剩」靠上面三个成功场景各扣一张把卷轴用光：顺序不能调换。
  left_scroll="?"
  if [ -n "$GATEWAY" ] && [ -n "$TOKEN" ]; then
    left_scroll="$(scroll_of "$(bag)")"
    printf '%s\n' "三个成功场景之后背包卷轴 ${left_scroll}" | detail
  fi
  if reset_cooldown && run_probe "$BATTLE_DEADLY" --case p15c6_scroll_none --scroll "$SCROLL"; then
    ok "背包一张不剩：no_scroll，人不起来、经验不动"
  else
    bad "没卷轴还能用（跑到这里时背包余量 ${left_scroll}，不是 0 就说明前面的场景没各扣一张）"
  fi

  if [ -n "$BATTLE_OFF" ]; then
    if run_probe "$BATTLE_OFF" --case p15c6_scroll_off --scroll "$SCROLL"; then
      ok "这一场没配角色服：upstream_off，不在本地凭空变一张"
    else
      bad "没角色服还能用卷轴"
    fi
  else
    printf '       （跳过没角色服那一条：没给 MMO_P1_5C6_BATTLE_OFF）\n'
  fi

  final_state="$(state)"
  printf '%s\n' "跑完之后的角色库状态：$final_state" | detail
  # none / not_down / not_scroll / upstream_off 都是拒绝路径：一封回包都不该
  # 让角色库里的冷却到期或当前级经验动一下。none 之前刚清过冷却。
  if [ "$(field_of "$final_state" ready_at_ms)" = "0" ] \
     && printf '%s' "$final_state" | grep -q 'exp=0' \
     && printf '%s' "$final_state" | grep -q 'level=1' \
     && printf '%s' "$final_state" | grep -q 'count=0'; then
    ok "拒绝路径从库里读回：冷却到期 0、exp=0、level=1、卷轴 0（一个字都没写）"
  else
    bad "拒绝路径写库了：$final_state"
  fi
fi

finish "P1-5c6（server + battle）"
