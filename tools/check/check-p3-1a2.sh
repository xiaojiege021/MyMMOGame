#!/usr/bin/env bash
# P3-1a2 突破（方案 8.1、开发架构 11 节 P3-1、13 节 P3-1a2、14 节第 61 行）。
#   bash tools/check/check-p3-1a2.sh
#   MMO_P3_1A2_HAS_DB=1 bash ...                加真库段（突破、失败扣历练与灵石、炼心、历练结算）
# 本卡口径：修为就是当前境界；历练来自已经能进入的副本；炼心是做完这一段
# 已经打开的主线和必要支线；成功后修为进下一段，失败后修为不变、历练与灵石照扣。
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CC="--config $ROOT/config/character.json"
CG="--config $ROOT/config/gateway.json"
HAS_DB="${MMO_P3_1A2_HAS_DB:-0}"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p"; done; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Character/Character.csproj src/Gateway/Gateway.csproj; do
  n="$(basename "$proj" .csproj)"
  "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1 || {
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail; }
done
[ "$BUILD_OK" = "1" ] && ok "角色服 / 网关编译通过（0 错误）" || true

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP/static.txt" <<'PY'
import json, os, re, sys
root, out = sys.argv[1], sys.argv[2]
o = open(out, "w", encoding="utf-8")

def R(k, c, evidence=""):
    o.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + str(evidence)) + "\n")

def rd(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def js(p):
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}

order = ["炼体", "筑基", "结丹", "元婴", "化神", "返虚", "合体", "渡劫", "大乘", "金仙", "极境"]
realms = js(os.path.join(root, "config", "realms.json"))
adv = {x.get("from"): x for x in realms.get("advance", [])}
R("advance_version", realms.get("version", 0) >= 2)
R("advance_scope", list(adv) == order[:-1], list(adv))
R("advance_fields", all(
    isinstance(x.get("drill"), int) and x["drill"] >= 0
    and str(x.get("stone", "")).startswith("stone_")
    and isinstance(x.get("stone_cost"), int) and x["stone_cost"] > 0
    and isinstance(x.get("success_pct"), int) and 0 <= x["success_pct"] <= 100
    and isinstance(x.get("main_stage"), int) and x["main_stage"] >= 0
    and isinstance(x.get("side_quests"), list)
    for x in adv.values()))
R("main_stage_91_10_11_12",
  [adv[r]["main_stage"] for r in ("炼体", "筑基", "结丹", "元婴")] == [9, 10, 11, 12],
  [adv[r]["main_stage"] for r in ("炼体", "筑基", "结丹", "元婴")])
R("launch_side_empty", all(adv[r]["side_quests"] == [] for r in ("炼体", "筑基", "结丹")))

d = js(os.path.join(root, "config", "dungeons.json"))
by = {x.get("id"): x for x in d.get("dungeons", [])}
R("dungeon_drill", d.get("version", 0) >= 8 and all(
    isinstance(x.get("drill"), int) and x["drill"] >= 0 for x in by.values()),
  {k: v.get("drill") for k, v in by.items()})
R("old_dungeons_no_realm", all("realm" not in by.get(k, {}) for k in ("main", "abyss", "lair", "sect", "dragon")))

rt = rd(os.path.join(root, "src", "Character", "RealmTable.cs"))
R("requirement_model", "RealmAdvanceRequirement" in rt and "Requirement(" in rt)
R("stone_five_tiers", "突破灵石必须是灵石五档" in rt)
R("success_bound", "success_pct 必须在 0–100" in rt)
R("missing_requirement", "的突破要求" in rt)
R("reuse_switch", "CheckAdvance(realm)" in rt and "realm_locked" in rt)

repo = rd(os.path.join(root, "src", "Character", "RealmAdvanceRepository.cs"))
R("lock_row", "for update" in repo)
R("heart_tables", "character_quest" in repo and "main_stage" in repo)
R("ledgers", "realm_breakthrough" in repo and "realm_drill" in repo)
R("conditional_deduct", "drill >= $2" in repo and "amount >= $3" in repo)
R("one_roll", "Random.Shared.Next(100) < requirement.SuccessPct" in repo)
R("failure_keeps_realm", "if (success)" in repo and "realm = $2" in repo)
R("gate_dungeon", "CheckDungeon(dungeonRealm, realm)" in repo)

grpc = rd(os.path.join(root, "src", "Character", "CharacterGrpcService.cs"))
R("grpc_entries", "AdvanceRealm(" in grpc and "SettleDrill(" in grpc and "realmAdvances" in grpc)
proto = rd(os.path.join(root, "proto", "mmo", "character.proto"))
R("proto_rpcs", "rpc AdvanceRealm(" in proto and "rpc SettleDrill(" in proto)
R("proto_no_numbers", "int64 drill_cost = 9" in proto and "int32 success_pct = 17" in proto)
gw = rd(os.path.join(root, "src", "Gateway", "Program.cs"))
R("no_gateway_route", "AdvanceRealm" not in gw and "advance-realm" not in gw)
battle = rd(os.path.join(root, "src", "Battle", "Server.cs")) + rd(os.path.join(root, "src", "Battle", "Encounter.cs"))
R("drill_only_in_character", "realm_breakthrough" not in battle and "realm_drill" not in battle)

ddl = rd(os.path.join(root, "db", "character", "realm_advance.sql"))
main = rd(os.path.join(root, "db", "character", "character.sql"))
for name in ("character_quest", "realm_breakthrough", "realm_drill"):
    R("ddl_" + name, name in ddl and name in main)
R("ddl_drill_column", "ADD COLUMN IF NOT EXISTS drill bigint" in ddl and "ADD COLUMN IF NOT EXISTS drill bigint" in main)
o.close()
PY
while IFS= read -r line; do
  case "$line" in
    "PASS "*) ok "${line#PASS }" ;;
    "FAIL "*) bad "${line#FAIL }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 只读入口 --realm-table / --dungeon-table =="
RT="$("$DOTNET" "$CD" --realm-table $CC 2>&1)"; rc=$?
if [ "$rc" = "0" ] \
   && printf '%s' "$RT" | grep -q '^realm_table_state=ok version=2 realms=11 launch_until=结丹' \
   && printf '%s' "$RT" | grep -q '^advance_req=炼体->筑基 drill=100 stone=stone_1 stone_cost=10 success_pct=70 main_stage=9 side_quests=$' \
   && [ "$(printf '%s' "$RT" | grep -c '^advance_req=')" = "10" ]; then
  ok "十段突破要求都在配置里，数值不写死在代码（不连库）"
else
  bad "境界表只读口径不对（rc=$rc）"
fi
printf '%s' "$RT" | grep -q '^advance=结丹->元婴:realm_locked' \
  && ok "版本开关仍是 P3-1a1 那一处判定：结丹→元婴关着" || bad "版本开关判定不对"

DT="$("$DOTNET" "$CD" --dungeon-table $CC 2>&1)"
printf '%s' "$DT" | grep -q '^dungeon=abyss .*drill=50' \
  && printf '%s' "$DT" | grep -q '^dungeon=abyss_yuanying .*realm=元婴 drill=200' \
  && ok "副本表带历练值；元婴副本仍带境界门槛" || bad "副本表历练字段不对"

echo "== 4. 坏配置起不来 =="
python3 - "$ROOT" "$TMP" <<'PY'
import copy, json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
base = json.load(open(os.path.join(root, "config", "realms.json"), encoding="utf-8"))
dung = json.load(open(os.path.join(root, "config", "dungeons.json"), encoding="utf-8"))

def dump(name, obj):
    json.dump(obj, open(os.path.join(tmp, name), "w", encoding="utf-8"), ensure_ascii=False)

def item(d, realm):
    return next(x for x in d["advance"] if x["from"] == realm)

a = copy.deepcopy(base); a["advance"] = [x for x in a["advance"] if x["from"] != "元婴"]
dump("r-missing.json", a)
b = copy.deepcopy(base); b["advance"].append(dict(item(base, "炼体")))
dump("r-dup.json", b)
c = copy.deepcopy(base); item(c, "炼体")["stone"] = "gold"
dump("r-stone.json", c)
e = copy.deepcopy(base); item(e, "炼体")["success_pct"] = 101
dump("r-pct.json", e)
f = copy.deepcopy(base); item(f, "炼体")["drill"] = -1
dump("r-drill.json", f)
g = copy.deepcopy(base); item(g, "炼体")["side_quests"] = [""]
dump("r-side.json", g)
h = copy.deepcopy(base)
h["advance"].append({"from": "极境", "drill": 1, "stone": "stone_1",
                     "stone_cost": 1, "success_pct": 50, "main_stage": 0, "side_quests": []})
dump("r-max.json", h)
k = copy.deepcopy(dung); next(x for x in k["dungeons"] if x["id"] == "abyss")["drill"] = -1
dump("d-drill.json", k)
PY
for c in missing dup stone pct drill side max; do
  out="$("$DOTNET" "$CD" --realm-table-path "$TMP/r-$c.json" "$ROOT/config/dungeons.json" 2>&1)"
  assert_bad_config "$out" "坏突破要求 ${c}：起不来（config_error=）"
done
out="$("$DOTNET" "$CD" --dungeon-table-path "$TMP/d-drill.json" 2>&1)"
assert_bad_config "$out" "副本历练值是负数：起不来"

echo "== 5. 真库：突破、炼心、失败扣历练与灵石、历练结算 =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真库段（MMO_P3_1A2_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/account.sql" $CG >"$TMP/mig-account.log" 2>&1 || true
  "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/character.sql" $CC >"$TMP/mig-char.log" 2>&1 || true
  "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/currency.sql" $CC >"$TMP/mig-currency.log" 2>&1 || true
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
  ACCT="p31a2_$$"; PW="P31a2-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" $CG >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
    | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
  [ -n "$TOK" ] && ok "登录拿到令牌（账号/口令只在内存）" || bad "登录没拿到令牌"
  ID=""
  for _ in $(seq 1 10); do
    out="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"name\":\"p31a2_$RANDOM$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>&1)"
    ID="$(printf '%s' "$out" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")' 2>/dev/null)"
    [ -n "$ID" ] && break; sleep 0.5
  done
  [ -n "$ID" ] && ok "造一个角色（$ID）" || bad "角色没建成"

  # 临时配置：成功率 100 / 0 / 带一条必要支线。只用配置指路，不改仓库里的那份。
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
base = json.load(open(os.path.join(root, "config", "realms.json"), encoding="utf-8"))
ch = json.load(open(os.path.join(root, "config", "character.json"), encoding="utf-8"))

def save(name, obj):
    json.dump(obj, open(os.path.join(tmp, name), "w", encoding="utf-8"), ensure_ascii=False)

for name, pct in (("r-100.json", 100), ("r-0.json", 0), ("r-side.json", 100)):
    r = json.loads(json.dumps(base))
    for x in r["advance"]:
        if x["from"] == "炼体":
            x["success_pct"] = pct
            if name == "r-side.json":
                x["side_quests"] = ["side_lianxin_p31a2"]
    save(name, r)
    c = dict(ch); c["realm_table"] = os.path.join(tmp, name)
    save("c-" + name, c)
PY
  C100="--config $TMP/c-r-100.json"; C0="--config $TMP/c-r-0.json"; CSIDE="--config $TMP/c-r-side.json"
  rawd() { "$DOTNET" "$CD" --raw-drill-write "$1" "$2" $CC 2>&1 | grep -m1 '^raw_drill='; }
  rawm() { "$DOTNET" "$CD" --raw-main-stage-write "$1" "$2" $CC >/dev/null 2>&1; }
  rawr() { "$DOTNET" "$CD" --raw-realm-write "$1" "$2" $CC >/dev/null 2>&1; }
  qdone() { "$DOTNET" "$CD" --complete-quest "$1" "$2" $CC >/dev/null 2>&1; }
  bstate() { "$DOTNET" "$CD" --realm-break-state "$1" $CC 2>&1 | grep -m1 '^realm_break_state='; }
  adv() { local id="$1" txn="$2"; shift 2; "$DOTNET" "$CD" --advance-realm "$id" "$txn" "$@" 2>&1 | grep -m1 '^advance_realm='; }
  drill() { "$DOTNET" "$CD" --settle-drill "$1" "$2" "$3" $CC 2>&1 | grep -m1 '^settle_drill='; }
  stone() {
    curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/currency/add?token=$TOK" \
      -H 'Content-Type: application/json' -d "{\"character_id\":$1,\"kind\":\"stone_1\",\"delta\":$2}" >/dev/null
  }
  setstone() { "$DOTNET" "$CD" --raw-currency-write "$1" stone_1 "$2" $CC >/dev/null 2>&1; }
  stonebalance() {
    curl -s --noproxy '*' "http://127.0.0.1:61000/currency?token=$TOK&character_id=$1" \
      | python3 -c 'import json,sys
try:
    print([x.get("amount") for x in json.load(sys.stdin).get("balances",[]) if x.get("kind")=="stone_1"][0])
except Exception:
    print("")'
  }

  rawd "$ID" -1 | grep -q 'raw_drill=rejected .*sqlstate=23514' \
    && ok "历练直写负数被库的 CHECK 拒（23514）" || bad "历练负数没被库挡住"

  R="$(adv "$ID" t-heart-1 $CC)"
  printf '%s' "$R" | grep -q 'executed=false duplicate=false success=false from=炼体 next=筑基 realm=炼体 reason=heart_not_done' \
    && ok "主线没走到第 9 场：heart_not_done" || bad "炼心没挡住：$R"

  rawm "$ID" 9
  rawd "$ID" 0 >/dev/null
  R="$(adv "$ID" t-drill-1 $CC)"
  printf '%s' "$R" | grep -q 'reason=drill_not_enough' \
    && ok "历练不够：drill_not_enough，修为不动" || bad "历练没挡住：$R"

  rawd "$ID" 100 >/dev/null
  R="$(adv "$ID" t-stone-1 $CC)"
  printf '%s' "$R" | grep -q 'reason=stone_not_enough' \
    && ok "灵石不够：stone_not_enough，修为不动" || bad "灵石没挡住：$R"

  stone "$ID" 10
  R="$(adv "$ID" t-ok-1 $C100)"
  printf '%s' "$R" | grep -q 'executed=true duplicate=false success=true .*realm=筑基 .*drill=100 drill_required=100 drill_cost=100 drill_after=0 .*stone_left=0' \
    && ok "成功：修为进筑基，历练 100→0、灵石 10→0" || bad "成功路径不对：$R"
  rawd "$ID" 100 >/dev/null; setstone "$ID" 10
  R="$(adv "$ID" t-ok-1 $C100)"
  printf '%s' "$R" | grep -q 'executed=false duplicate=true' \
    && ok "同一事务号再提交不掷第二次" || bad "重复事务号没挡住：$R"
  [ "$(stonebalance "$ID")" = "10" ] && ok "重复提交没有多扣灵石" || bad "重复提交扣了灵石"

  rawr "$ID" 炼体; rawd "$ID" 100 >/dev/null; setstone "$ID" 10; rawm "$ID" 9
  R="$(adv "$ID" t-fail-1 $C0)"
  printf '%s' "$R" | grep -q 'executed=true duplicate=false success=false from=炼体 next=筑基 realm=炼体 .*drill_after=0 .*stone_left=0' \
    && ok "失败：修为停在炼体，历练与灵石照扣" || bad "失败路径不对：$R"

  rawr "$ID" 炼体; rawd "$ID" 100 >/dev/null; setstone "$ID" 10; rawm "$ID" 9
  R="$(adv "$ID" t-side-1 $CSIDE)"
  printf '%s' "$R" | grep -q 'reason=heart_not_done' \
    && ok "必要支线没做完：历练与灵石都够也不能突破" || bad "必要支线没卡住：$R"
  [ "$(stonebalance "$ID")" = "10" ] && [ "$(bstate "$ID")" = "realm_break_state=ok id=$ID realm=炼体 drill=100 main_stage=9 quests=" ] \
    && ok "被炼心挡下的突破一分都没扣" || bad "被挡下仍扣了东西：$(bstate "$ID")"
  qdone "$ID" side_lianxin_p31a2
  R="$(adv "$ID" t-side-2 $CSIDE)"
  printf '%s' "$R" | grep -q 'success=true .*realm=筑基' \
    && ok "必要支线做完后可以突破" || bad "补完支线仍不能突破：$R"

  rawr "$ID" 结丹; rawd "$ID" 800 >/dev/null; rawm "$ID" 11; setstone "$ID" 80
  BAL="$(stonebalance "$ID")"
  R="$(adv "$ID" t-lock-1 $CC)"
  printf '%s' "$R" | grep -q 'reason=realm_locked' && [ "$(stonebalance "$ID")" = "$BAL" ] \
    && ok "元婴开关关着：结丹不能突破，灵石与历练不动" || bad "版本开关没挡住：$R"

  rawr "$ID" 炼体; rawd "$ID" 0 >/dev/null
  R="$(drill "$ID" abyss t-drill-a)"
  printf '%s' "$R" | grep -q "settle_drill=done id=$ID dungeon=abyss gain=50 drill=50 duplicate=false reason=$" \
    && ok "历练结算：深渊加 50" || bad "历练结算不对：$R"
  R="$(drill "$ID" abyss t-drill-a)"
  printf '%s' "$R" | grep -q "gain=0 drill=50 duplicate=true" \
    && ok "同一场重复结算不加第二次" || bad "历练重复结算：$R"
  R="$(drill "$ID" abyss_yuanying t-trill-b)"
  printf '%s' "$R" | grep -q 'reason=realm_locked gain=0' \
    && ok "元婴副本关着时结算不给历练" || bad "关着的副本仍给了历练：$R"
  bstate "$ID" | grep -q 'drill=50' \
    && ok "历练值落在角色身上（事实值 50）" || bad "历练值没落库"
fi

finish "P3-1a2（角色服 + 网关）"
