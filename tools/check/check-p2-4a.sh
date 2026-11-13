#!/usr/bin/env bash
# P2-4a 二转与 15 级武器锁（方案 8.3、开发架构 11 节 P2-4、14 节第 59 行）。
#   bash tools/check/check-p2-4a.sh                  编译+静态+只读+坏表
#   MMO_P2_4A_HAS_DB=1 bash ...                      加真库段（二转、武器锁、诫律不能复活）
# 本卡口径：15 级选定的分支，45 级只把这条往前推一步，改去另一条被拒；
# 不到 15 级的武器五个职业都能装备，15 级起只能用本职业兵器（职业＝分支并集）；
# 生息二转只把复活扣经验的比例减小，卷轴仍按基础比例；诫律不能复活。
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
CC="--config $ROOT/config/character.json"
CG="--config $ROOT/config/gateway.json"
HAS_DB="${MMO_P2_4A_HAS_DB:-0}"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p"; done; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Character/Character.csproj src/Battle/Battle.csproj tools/probe/BattleProbe/BattleProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1 || {
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail; }
done
[ "$BUILD_OK" = "1" ] && ok "角色服 / 战斗服 / 战斗探针编译通过（0 错误）" || true

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP/static.txt" <<'PY'
import json, os, sys
root, out = sys.argv[1], sys.argv[2]
o = open(out, "w", encoding="utf-8")

def R(k, c, i=""):
    o.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")

def rd(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def js(p):
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}

branches = js(os.path.join(root, "config", "branches.json"))
items = js(os.path.join(root, "config", "items.json"))
classes = js(os.path.join(root, "config", "classes.json"))

bs = branches.get("branches", [])
R("branches_have_weapon_kinds", bs and all(b.get("weapon_kinds") for b in bs), str(len(bs)))
R("weapon_lock_level", branches.get("weapon_lock_level") == 15, str(branches.get("weapon_lock_level")))

by = {i["id"]: i for i in items.get("items", [])}
trial = by.get("sword_trial", {})
trade = by.get("sword_trade", {})
staff = by.get("staff_trade", {})
R("low_weapon_below_lock", trial.get("weapon_kind") == "sword" and trial.get("require_level") == 10,
  str(trial))
R("high_weapon_at_lock", trade.get("weapon_kind") == "sword" and trade.get("require_level") == 15,
  str(trade))
R("staff_needs_15", staff.get("weapon_kind") == "staff" and staff.get("require_level") == 15,
  str(staff))

res = classes.get("resurrect", {})
R("resurrect_base", res.get("exp_cost_pct") == 10, str(res.get("exp_cost_pct")))
R("resurrect_second_smaller",
  isinstance(res.get("second_turn_exp_cost_pct"), int)
  and res["second_turn_exp_cost_pct"] < res.get("exp_cost_pct", 0),
  str(res.get("second_turn_exp_cost_pct")))

rules = rd(os.path.join(root, "src", "Character", "EquipRules.cs"))
R("equip_rules_single_exit", "public static string Check(" in rules and "class_mismatch" in rules)
R("equip_rules_union", "ClassCanUseWeapon" in rules)

repo = rd(os.path.join(root, "src", "Character", "CharacterRepository.cs"))
R("advance_tx_for_update", "for update" in repo and "branch_locked" in repo)
R("advance_level_45", "BranchAdvanceLevel = 45" in repo)
R("advance_idempotent", "step >= 2" in repo and "branch_step = 2" in repo)

grpc = rd(os.path.join(root, "src", "Character", "CharacterGrpcService.cs"))
R("grpc_advance", "AdvanceBranch(" in grpc and "branch_not_found" in grpc)
R("grpc_can_equip", "CanEquip(" in grpc and "EquipRules.Check" in grpc)

enc = rd(os.path.join(root, "src", "Battle", "Encounter.cs"))
R("jielv_cannot_resurrect", 'CannotResurrectBranch = "jielv"' in enc
  and "SkillRejectCannotResurrect" in enc)
R("shengxi_second_turn", 'ShengxiBranch = "shengxi"' in enc
  and "SecondTurnExpCostPct" in enc)
R("profile_cached", "ProfileOf(Member member)" in enc and "member.Profile ??=" in enc)

proto = rd(os.path.join(root, "proto", "mmo", "character.proto"))
bproto = rd(os.path.join(root, "proto", "mmo", "battle.proto"))
gen_srv = rd(os.path.join(root, "src", "Shared", "Generated", "CharacterGrpc.cs"))
R("proto_advance", "rpc AdvanceBranch(" in proto)
R("proto_can_equip", "rpc CanEquip(" in proto)
R("proto_cannot_resurrect", "SKILL_REJECT_CANNOT_RESURRECT = 11" in bproto)
R("gen_has_advance", "AdvanceBranch" in gen_srv and "CanEquip" in gen_srv)

ddl = rd(os.path.join(root, "db", "character", "branch_step.sql"))
main = rd(os.path.join(root, "db", "character", "character.sql"))
R("ddl_branch_step", "branch_step" in ddl and "BETWEEN 0 AND 2" in ddl)
R("ddl_in_main", "branch_step" in main)
o.close()
PY
while IFS= read -r line; do
  case "$line" in
    "PASS "*) ok "${line#PASS }" ;;
    "FAIL "*) bad "${line#FAIL }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 只读入口 --weapon-rules =="
wr="$("$DOTNET" "$CD" --weapon-rules 2>&1)"; wrc=$?
[ "$wrc" = "0" ] && printf '%s' "$wr" | grep -q '^weapon_lock_level=15' \
  && ok "武器锁等级 15（不连库也能读）" || bad "只读入口不对（rc=$wrc）"
printf '%s' "$wr" | grep -q 'weapon=sword_trial kind=sword require_level=10' \
  && ok "sword_trial 是 10 级剑（不到 15 级，全职业可装备）" || bad "sword_trial 不对"
printf '%s' "$wr" | grep -q 'class=warrior kinds=.*sword' \
  && ok "战士的可用兵器含剑（该职业分支并集）" || bad "战士兵器不对"
printf '%s' "$wr" | grep -qE 'class=tank kinds=.*mace' \
  && ok "坦克的可用兵器含锤（另一个职业的并集）" || bad "坦克兵器不对"

echo "== 4. 坏配置起不来 =="
mk_bad() {
  python3 - "$ROOT" "$TMP" "$1" <<'PY'
import json, os, sys
root, tmp, mode = sys.argv[1], sys.argv[2], sys.argv[3]
b = json.load(open(os.path.join(root, "config", "branches.json"), encoding="utf-8"))
i = json.load(open(os.path.join(root, "config", "items.json"), encoding="utf-8"))
if mode == "lock_level":
    b["weapon_lock_level"] = 0
elif mode == "lock_missing":
    b.pop("weapon_lock_level", None)
elif mode == "weapon_no_level":
    for it in i["items"]:
        if it.get("id") == "staff_trade":
            it.pop("require_level", None)
json.dump(b, open(os.path.join(tmp, "b-" + mode + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
json.dump(i, open(os.path.join(tmp, "i-" + mode + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
}
for c in lock_level lock_missing weapon_no_level; do
  mk_bad "$c"
  out="$("$DOTNET" "$CD" --weapon-rules-path "$TMP/b-$c.json" "$TMP/i-$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error='; then
    ok "坏配置 ${c}：起不来（config_error=）"
  else
    bad "坏配置 ${c} 没挡住（rc=$rc）：$(printf '%s' "$out" | head -2)"
  fi
done

echo "== 5. 真库：二转 / 武器锁 / 诫律不能复活 =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真库段（MMO_P2_4A_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
b = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
b["listen"] = "127.0.0.1:0"
b["settlement_dir"] = os.path.join(tmp, "set")
b["character_dir"] = os.path.join(tmp, "chr")
b["exp_upstream"] = "http://127.0.0.1:62000"
b["character_upstream"] = "http://127.0.0.1:62000"
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json"),
             ("battlefield_table", "battlefield.json")):
    b[k] = os.path.join(root, "config", f)
json.dump(b, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  # 这一列是幂等 DDL：老库没有 branch_step，先补上再起角色服。
  "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/branch_step.sql" $CC >"$TMP/mig.log" 2>&1 || true
  grep -q '^migrate=ok' "$TMP/mig.log" && ok "真库补 branch_step 列（幂等 DDL）" \
    || bad "branch_step 迁移失败：$(head -2 "$TMP/mig.log")"
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 &
  PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$BD" --config "$TMP/battle.json" >"$TMP/battle.log" 2>&1 &
  PIDS="$PIDS $!"
  BU=""
  for _ in $(seq 1 120); do
    BU="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/battle.log" 2>/dev/null | tail -1 | sed 's/^listen=//')"
    [ -n "$BU" ] && break; sleep 0.1
  done
  WU="ws${BU#http}"
  "$DOTNET" "$GD" $CG >"$TMP/gw.log" 2>&1 &
  PIDS="$PIDS $!"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
      -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p4a_$$"; PW="P4a-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" $CG >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
    | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
  [ -n "$TOK" ] && ok "登录拿到令牌（账号/口令只在内存）" || bad "登录没拿到令牌"
  mkchar() {
    local cls="$1" branch="$2" out id body
    for _ in $(seq 1 10); do
      if [ -n "$branch" ]; then
        body="{\"name\":\"p4a_$cls$RANDOM\",\"race\":\"mai\",\"class_id\":\"$cls\",\"branch\":\"$branch\"}"
      else
        body="{\"name\":\"p4a_$cls$RANDOM\",\"race\":\"mai\",\"class_id\":\"$cls\"}"
      fi
      out="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
        -H 'Content-Type: application/json' -d "$body" 2>&1)"
      id="$(printf '%s' "$out" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")' 2>/dev/null)"
      [ -n "$id" ] && { printf '%s' "$id"; return 0; }
      sleep 0.5
    done
    return 1
  }
  lv() { "$DOTNET" "$CD" --raw-level-write "$1" "$2" $CC >/dev/null 2>&1; }
  bstate() { "$DOTNET" "$CD" --branch-state "$1" $CC 2>&1 | grep -m1 '^branch_state='; }
  adv() { "$DOTNET" "$CD" --advance-branch "$1" "$2" $CC 2>&1 | grep -m1 '^advance_branch='; }
  equip() { "$DOTNET" "$CD" --can-equip "$1" "$2" $CC 2>&1 | grep -m1 '^can_equip='; }

  W45="$(mkchar warrior pojun)"; W12="$(mkchar warrior "")"; M45="$(mkchar mage "")"
  T12="$(mkchar tank "")"; P45="$(mkchar priest jielv)"
  if [ -n "$W45" ] && [ -n "$W12" ] && [ -n "$M45" ] && [ -n "$T12" ] && [ -n "$P45" ]; then
    ok "造 5 个角色（warrior×2 / mage / tank / priest-jielv）"
  else
    bad "角色没建齐"
  fi

  echo "-- ① 45 级才能二转，只能推进已选的那条 --"
  lv "$W45" 44
  r="$(adv "$W45" pojun)"
  printf '%s' "$r" | grep -q 'reason=not_enough_level' && ok "44 级二转：not_enough_level" || bad "44 级二转没被拒：$r"
  lv "$W45" 45
  r="$(adv "$W45" pojun)"
  printf '%s' "$r" | grep -q 'reason=none branch=pojun branch_step=2' \
    && ok "45 级推进 pojun：branch_step 1 → 2" || bad "45 级二转不成：$r"
  r="$(adv "$W45" pojun)"
  printf '%s' "$r" | grep -q 'duplicate=true' && ok "同一笔再点一次：duplicate，不再改" || bad "重放没挡住：$r"
  r="$(adv "$W45" xianzhen)"
  printf '%s' "$r" | grep -q 'reason=branch_locked' && ok "45 级改去另一条分支：branch_locked" || bad "能改分支：$r"
  [ "$(bstate "$W45" | grep -oE 'branch=pojun branch_step=2')" = "branch=pojun branch_step=2" ] \
    && ok "库里仍是 pojun / step 2（改分支那一笔一字没写）" || bad "库被改动了：$(bstate "$W45")"
  lv "$W12" 45
  r="$(adv "$W12" pojun)"
  printf '%s' "$r" | grep -q 'reason=branch_missing' && ok "没一转就二转：branch_missing" || bad "没一转能二转：$r"

  echo "-- ② 武器锁：15 级是分界线 --"
  lv "$W12" 12; lv "$T12" 12
  equip "$W12" sword_trial | grep -q 'ok=true' && ok "12 级战士能装 10 级剑" || bad "12 级战士装不了 10 级剑"
  equip "$T12" sword_trial | grep -q 'ok=true' && ok "12 级坦克也能装同一把 10 级剑（五职业通用）" || bad "坦克装不了 10 级剑"
  equip "$W12" sword_trade | grep -q 'reason=level_not_enough' \
    && ok "12 级战士装 15 级剑：level_not_enough" || bad "12 级能装 15 级剑"
  lv "$W45" 45; lv "$M45" 45
  equip "$W45" sword_trade | grep -q 'ok=true' && ok "45 级战士能装 15 级剑" || bad "45 级战士装不了剑"
  equip "$W45" staff_trade | grep -q 'reason=class_mismatch' \
    && ok "战士装 15 级法杖：class_mismatch（15 级起只能用本职业兵器）" || bad "战士装上了法杖"
  equip "$M45" staff_trade | grep -q 'ok=true' && ok "法师能装同一根法杖" || bad "法师装不了法杖"
  equip "$W45" ring_trial | grep -q 'ok=true' && ok "戒指不是武器，不受武器锁约束" || bad "戒指被武器锁挡住"

  echo "-- ③ 诫律不能复活 --"
  # party / class / char 由探针自己拼，URL 只给到 /battle——重复的 query 会让两个人进不同的场。
  "$DOTNET" "$PROBE" --url "$WU/battle" \
    --case p24a_jielv --boss 9001 --hit 1002 --resurrect 1303 --priest-char "$P45" \
    --warrior-char "$W45" >"$TMP/jielv.log" 2>&1
  grep -q 'reason=SkillRejectCannotResurrect' "$TMP/jielv.log" \
    && ok "诫律牧师拉人：SkillRejectCannotResurrect，人还倒着" \
    || bad "诫律拉起来了：$(head -3 "$TMP/jielv.log")"
  stop_pid "$(pgrep -f "Gateway.dll $CG" | head -1)" 2>/dev/null || true
fi

finish "P2-4a（角色服 + 战斗服）"
