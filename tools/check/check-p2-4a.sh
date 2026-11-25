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
AGG_CHARGRPC="$(src_agg src/Character/CharacterGrpcService.cs)"; export AGG_CHARGRPC
# D3c 之后 Encounter 已拆进 Encounter/ 目录：这里同样走 src_agg，断言文本一个字不改。
AGG_BATTLE_ENCOUNTER="$(src_agg src/Battle/Encounter.cs)"; export AGG_BATTLE_ENCOUNTER
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
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
for proj in src/Character/Character.csproj src/Battle/Battle.csproj \
            src/Gateway/Gateway.csproj tools/probe/BattleProbe/BattleProbe.csproj; do
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

# W1（兵器按职业）：本命兵器只有方案写定的 8 个 kind，一个职业一组，同一职业的两条分支共用。
# 期望值现取：并集与每个职业的集合都从 branches.json 读出来比，不在脚本里另立一套「当时的事实」。
W1_KINDS = {"mace", "axe", "saber", "spear", "bow", "crossbow", "staff", "spirit_sword"}
W1_BY_CLASS = {
    "tank": {"mace", "axe"},
    "warrior": {"saber", "spear"},
    "archer": {"bow", "crossbow"},
    "mage": {"staff", "spirit_sword"},
    "priest": {"staff", "spirit_sword"},
}
union = {k for b in bs for k in (b.get("weapon_kinds") or [])}
per = {}
for b in bs:
    per.setdefault(b.get("class_id"), set()).update(b.get("weapon_kinds") or [])
R("w1_kinds_are_the_eight", union == W1_KINDS, str(sorted(union)))
R("w1_class_sets", per == W1_BY_CLASS, str({k: sorted(v) for k, v in per.items()}))
R("w1_branches_share_class_group",
  all(set(b.get("weapon_kinds") or []) == W1_BY_CLASS.get(b.get("class_id"), set()) for b in bs),
  str([b.get("id") for b in bs if set(b.get("weapon_kinds") or []) != W1_BY_CLASS.get(b.get("class_id"), set())]))

by = {i["id"]: i for i in items.get("items", [])}
trial = by.get("sword_trial", {})
trade = by.get("sword_trade", {})
staff = by.get("staff_trade", {})
R("low_weapon_below_lock", trial.get("weapon_kind") == "sword" and trial.get("require_level") == 10,
  str(trial))
# K5 把 sword_trade 从 15 级剑改成 15 级长刀（weapon_kind=saber，编号不变）；
# W1 把战士兵器收到 saber / spear，武器锁语义没变，这里只跟着改兵器种类。
R("high_weapon_at_lock", trade.get("weapon_kind") == "saber" and trade.get("require_level") == 15,
  str(trade))
R("staff_needs_15", staff.get("weapon_kind") == "staff" and staff.get("require_level") == 15,
  str(staff))

# W1 两把旧剑的去向：sword_trade 已是 saber（15 级长刀），sword_trial 仍是 10 级 sword；
# 15 级以后没有任何物品再用 sword 种类，那 8 个本命兵器里也没有它。
R("w1_trial_all_classes",
  trial.get("require_level", 0) < branches.get("weapon_lock_level", 0) and "sword" not in union,
  "require=%s lock=%s" % (trial.get("require_level"), branches.get("weapon_lock_level")))
_hi_sword = [i.get("id") for i in items.get("items", [])
             if i.get("weapon_kind") == "sword" and (i.get("require_level") or 0) >= 15]
R("w1_no_high_level_sword", not _hi_sword, str(_hi_sword))

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

grpc = rd(os.environ["AGG_CHARGRPC"])
R("grpc_advance", "AdvanceBranch(" in grpc and "branch_not_found" in grpc)
R("grpc_can_equip", "CanEquip(" in grpc and "EquipRules.Check" in grpc)

enc = rd(os.environ["AGG_BATTLE_ENCOUNTER"])
R("jielv_cannot_resurrect", 'CannotResurrectBranch = "jielv"' in enc
  and "SkillRejectCannotResurrect" in enc)
R("shengxi_second_turn", 'ShengxiBranch = "shengxi"' in enc
  and "SecondTurnExpCostPct" in enc)
# 缓存实现已从 `member.Profile ??=` 改成 ProfileChecked 标记 + 一次性赋值（W1 顺手对齐，语义没变：一条命只取一次）。
R("profile_cached", "ProfileOf(Member member)" in enc
  and "member.ProfileChecked" in enc and "member.Profile = " in enc)

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

# ---- B6 觉醒（并进本卡：同一条「分支 + 等级」成长线，不新开脚本）----
awaken = js(os.path.join(root, "config", "awaken.json"))
skillsj = js(os.path.join(root, "config", "skills.json"))
expj = js(os.path.join(root, "config", "exp.json"))
bclass = {b.get("id"): b.get("class_id") for b in bs}
rows = awaken.get("branch_skills") or []
skillmap = {s.get("id"): s for s in skillsj.get("skills") or []}
asked = [s for s in skillsj.get("skills") or [] if s.get("awaken")]
R("awaken_schema", awaken.get("schema") == "awaken-table/1")
R("awaken_min_level_is_cap", awaken.get("min_level") == expj.get("level_cap") == 100)
R("awaken_free", awaken.get("gold_cost") == 0 and awaken.get("pending") is True)
R("awaken_covers_branches", sorted(r.get("branch_id") for r in rows) == sorted(bclass))
R("awaken_skills_one_per_branch", len(asked) == len(rows) == len(bclass) == 10)
R("awaken_skill_links", all(
    (skillmap.get(r.get("skill_id")) or {}).get("awaken") is True
    and (skillmap.get(r.get("skill_id")) or {}).get("branch") == r.get("branch_id")
    and (skillmap.get(r.get("skill_id")) or {}).get("school") == "faze"
    and (skillmap.get(r.get("skill_id")) or {}).get("tier") == "tian"
    and (skillmap.get(r.get("skill_id")) or {}).get("class_id") == bclass.get(r.get("branch_id"))
    for r in rows))
R("level_cap_untouched", expj.get("level_cap") == 100)

skc = rd(os.path.join(root, "src", "Battle", "ConfigTables", "Skill.cs"))
R("forclass_skips_awaken", "classId && !s.Awaken" in skc)
R("one_awaken_per_branch_guard", "有两条觉醒技" in skc)
party = rd(os.path.join(root, "src", "Battle", "Encounter", "Party.cs"))
R("awaken_only_own_branch", "awaken.Awaken" in party
  and "awaken.Branch, profile.Branch" in party)
repo2 = rd(os.path.join(root, "src", "Character", "CharacterRepository.cs"))
R("awaken_locks_character_row", "where id = $1 for update" in repo2
  and "AwakenAsync" in repo2)
R("awaken_writes_two_cols_only",
  "set awaken_skill_id = $2, awaken_at = now()" in repo2)
R("proto_awaken_rpc", "rpc Awaken(" in proto and "rpc DescribeAwaken(" in proto)
R("proto_awaken_field", "awaken_skill_id = 12" in proto)
R("gen_has_awaken", "AwakenRequest" in gen_srv and "DescribeAwaken" in gen_srv)
awddl = rd(os.path.join(root, "db", "character", "awaken.sql"))
R("ddl_awaken", "awaken_skill_id" in awddl and "character_awaken_pair" in awddl)
R("ddl_awaken_in_main", "awaken_skill_id" in main)
R("gateway_awaken", '"/character/awaken"' in
  rd(os.path.join(root, "src", "Gateway", "Api", "Character", "Awaken.cs")))
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
  && ok "武器锁等级 15（不连库也能读）" || bad "只读入口不对（rc=${wrc}）"
printf '%s' "$wr" | grep -q 'weapon=sword_trial kind=sword require_level=10' \
  && ok "sword_trial 是 10 级剑（不到 15 级，全职业可装备）" || bad "sword_trial 不对"
# W1 起战士兵器是长刀 / 长枪（saber / spear），不再含 sword——旧断言钉的是 P2-4a 当时的事实。
printf '%s' "$wr" | grep -q 'class=warrior kinds=.*saber' \
  && ok "战士兵器含长刀（该职业分支并集）" || bad "战士兵器不对"
printf '%s' "$wr" | grep -qE 'class=tank kinds=.*mace' \
  && ok "坦克的可用兵器含锤（另一个职业的并集）" || bad "坦克兵器不对"

echo "== 4. 只读入口 --awaken-config =="
awc="$("$DOTNET" "$CD" --awaken-config 2>&1)"; awrc=$?
[ "$awrc" = "0" ] && printf '%s' "$awc" | grep -q '^awaken_config=ok version=1 pending=true min_level=100 gold_cost=0 branches=10' \
  && ok "觉醒表现值：满级 100 / 免费 / 10 条分支（不连库也能读）" || bad "只读入口不对（rc=${awrc}）：$(printf '%s' "$awc" | head -1)"
[ "$(printf '%s' "$awc" | grep -c '^awaken_row=branch=')" = "10" ] \
  && ok "10 条分支各有一条觉醒技" || bad "觉醒技行数不对"
printf '%s' "$awc" | grep -q 'awaken_row=branch=pojun class=warrior skill=1004' \
  && ok "破军解锁 1004（技能编号来自 awaken.json，不是代码）" || bad "破军那一条不对"

echo "== 5. 坏配置起不来（觉醒表）=="
mk_awaken() {
  python3 - "$ROOT" "$TMP" "$1" <<'PY'
import json, os, sys
root, tmp, mode = sys.argv[1], sys.argv[2], sys.argv[3]
d = json.load(open(os.path.join(root, "config", "awaken.json"), encoding="utf-8"))
rows = d["branch_skills"]
if mode == "min99":
    d["min_level"] = 99
elif mode == "gold":
    d["gold_cost"] = 1
elif mode == "miss":
    rows.pop()
elif mode == "dup_branch":
    rows[1] = dict(rows[0])
elif mode == "dup_skill":
    rows[1] = {"branch_id": rows[1]["branch_id"], "skill_id": rows[0]["skill_id"]}
elif mode == "schema":
    d["schema"] = "awaken-table/9"
elif mode == "no_pending_note":
    d["note"] = "觉醒"
elif mode == "unknown_branch":
    rows[0] = {"branch_id": "nosuch", "skill_id": 1404}
json.dump(d, open(os.path.join(tmp, "aw-" + mode + ".json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)
PY
}
for c in min99 gold miss dup_branch dup_skill schema no_pending_note unknown_branch; do
  mk_awaken "$c"
  out="$("$DOTNET" "$CD" --awaken-config --awaken-table-path "$TMP/aw-$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error='; then
    ok "觉醒坏配置 ${c}：起不来（config_error=）"
  else
    bad "觉醒坏配置 ${c} 没挡住（rc=${rc}）：$(printf '%s' "$out" | head -2)"
  fi
done

echo "== 6. 坏配置起不来 =="
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
    bad "坏配置 ${c} 没挡住（rc=${rc}）：$(printf '%s' "$out" | head -2)"
  fi
done

echo "== 7. 真库：二转 / 武器锁 / 诫律不能复活 / 觉醒 =="
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
             ("battlefield_table", "battlefield.json"),
             # B15：副本 boss_id 要与怪物表对账；临时配置不指过去就一个都认不到。
             ("monster_table", "monsters.json")):
    b[k] = os.path.join(root, "config", f)
json.dump(b, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  # 这一列是幂等 DDL：老库没有 branch_step，先补上再起角色服。
  "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/branch_step.sql" $CC >"$TMP/mig.log" 2>&1 || true
  grep -q '^migrate=ok' "$TMP/mig.log" && ok "真库补 branch_step 列（幂等 DDL）" \
    || bad "branch_step 迁移失败：$(head -2 "$TMP/mig.log")"
  # B6：觉醒两列同样是幂等 DDL，老库先补上再起角色服。
  "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/awaken.sql" $CC >"$TMP/mig-aw.log" 2>&1 || true
  grep -q '^migrate=ok' "$TMP/mig-aw.log" && ok "真库补 awaken_skill_id / awaken_at 两列（幂等 DDL）" \
    || bad "觉醒迁移失败：$(head -2 "$TMP/mig-aw.log")"
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
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 $CG >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
    | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
  [ -n "$TOK" ] && ok "登录拿到令牌（账号/口令只在内存）" || bad "登录没拿到令牌"
  mkchar() {
    local cls="$1" branch="$2" out id body
    # 名字长度按 config/name_rules.json 的 max_length=12 起：Q12 之后 p4a_warrior12345 会被 name_too_long 拒。
    for _ in $(seq 1 10); do
      if [ -n "$branch" ]; then
        body="{\"name\":\"p4a${cls:0:1}$RANDOM\",\"race\":\"mai\",\"class_id\":\"$cls\",\"branch\":\"$branch\"}"
      else
        body="{\"name\":\"p4a${cls:0:1}$RANDOM\",\"race\":\"mai\",\"class_id\":\"$cls\"}"
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
  # 夹具没建起来时这几个调用会拿到空 id：空放过，让「角色没建齐」自己红，不要连带一串中止。
  lv() { [ -n "${1:-}" ] || return 0; "$DOTNET" "$CD" --raw-level-write "$1" "$2" $CC >/dev/null 2>&1; }
  bstate() { [ -n "${1:-}" ] || return 0; "$DOTNET" "$CD" --branch-state "$1" $CC 2>&1 | grep -m1 '^branch_state='; }
  adv() { [ -n "${1:-}" ] || return 0; "$DOTNET" "$CD" --advance-branch "$1" "$2" $CC 2>&1 | grep -m1 '^advance_branch='; }
  equip() { [ -n "${1:-}" ] || return 0; "$DOTNET" "$CD" --can-equip "$1" "$2" $CC 2>&1 | grep -m1 '^can_equip='; }
  awaken() { [ -n "${1:-}" ] || return 0; "$DOTNET" "$CD" --awaken "$1" "$2" $CC 2>&1 | grep -m1 '^awaken='; }
  astate() { [ -n "${1:-}" ] || return 0; "$DOTNET" "$CD" --awaken-state "$1" $CC 2>&1 | grep -m1 '^awaken_state='; }

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
  equip "$W45" sword_trade | grep -q 'ok=true' && ok "45 级战士能装 15 级长刀" || bad "45 级战士装不了长刀"
  equip "$W45" staff_trade | grep -q 'reason=class_mismatch' \
    && ok "战士装 15 级法杖：class_mismatch（15 级起只能用本职业兵器）" || bad "战士装上了法杖"
  equip "$M45" staff_trade | grep -q 'ok=true' && ok "法师能装同一根法杖" || bad "法师装不了法杖"
  equip "$W45" ring_trial | grep -q 'ok=true' && ok "戒指不是武器，不受武器锁约束" || bad "戒指被武器锁挡住"
  # W1：一个职业一组本命兵器。坦克只有锤斧、法师两件都行、战士不再能摸法剑。
  lv "$T12" 45
  equip "$T12" sword_trade | grep -q 'reason=class_mismatch' \
    && ok "坦克装 15 级长刀：class_mismatch（坦克只有锤 / 斧）" || bad "坦克装上了长刀"
  equip "$M45" spirit_sword_trade | grep -q 'ok=true' \
    && ok "法师装 15 级法剑：ok（法杖 / 法剑都算本命）" || bad "法师装不了法剑"
  equip "$W45" spirit_sword_trade | grep -q 'reason=class_mismatch' \
    && ok "战士装 15 级法剑：class_mismatch" || bad "战士装上了法剑"
  # 不到 15 级全职业：五个角色都拿剑试，只靠 require_level 放行（牧师先拉到 45 级，免得撞 level_not_enough）。
  lv "$P45" 45
  equip "$M45" sword_trial | grep -q 'ok=true' && ok "法师也能装 10 级剑（不到 15 级全职业）" || bad "法师装不了 10 级剑"
  equip "$P45" sword_trial | grep -q 'ok=true' && ok "牧师也能装 10 级剑（不到 15 级全职业）" || bad "牧师装不了 10 级剑"

  echo "-- ③ 诫律不能复活 --"
  # party / class / char 由探针自己拼，URL 只给到 /battle——重复的 query 会让两个人进不同的场。
  "$DOTNET" "$PROBE" --url "$WU/battle" \
    --case p24a_jielv --boss 9001 --hit 1002 --resurrect 1303 --priest-char "$P45" \
    --warrior-char "$W45" >"$TMP/jielv.log" 2>&1
  grep -q 'reason=SkillRejectCannotResurrect' "$TMP/jielv.log" \
    && ok "诫律牧师拉人：SkillRejectCannotResurrect，人还倒着" \
    || bad "诫律拉起来了：$(head -3 "$TMP/jielv.log")"
  echo "-- ④ 觉醒：满级才开，一次之后只回 duplicate --"
  A100="$(mkchar warrior pojun)"
  [ -n "$A100" ] && ok "造觉醒夹具角色（warrior / pojun）" || bad "觉醒夹具没建起来"
  lv "$A100" 99
  r="$(awaken "$A100" pojun)"
  printf '%s' "$r" | grep -q 'reason=not_enough_level' \
    && ok "99 级觉醒：not_enough_level（满级才开）" || bad "99 级能觉醒：$r"
  printf '%s' "$(astate "$A100")" | grep -q 'skill=0' \
    && ok "被拒之后没留任何觉醒状态（skill=0）" || bad "被拒却写进了状态"
  lv "$A100" 100
  r="$(awaken "$A100" pojun)"
  printf '%s' "$r" | grep -q 'skill=1004' && printf '%s' "$r" | grep -q 'duplicate=false' \
    && ok "100 级觉醒：破军解锁 1004" || bad "满级觉醒不对：$r"
  st="$(astate "$A100")"
  printf '%s' "$st" | grep -q 'branch=pojun skill=1004 level=100' \
    && ok "只读口读回觉醒状态（分支 / 技能 / 等级没变）" || bad "读回不对：$st"
  printf '%s' "$st" | grep -qE 'awaken_at_ms=[1-9][0-9]*' \
    && ok "觉醒时刻已落库（非 0）" || bad "觉醒时刻没写：$st"
  r="$(awaken "$A100" pojun)"
  printf '%s' "$r" | grep -q 'duplicate=true' \
    && ok "重复觉醒只回 duplicate，不二次写库" || bad "重复觉醒不对：$r"
  r="$(awaken "$A100" xianzhen)"
  printf '%s' "$r" | grep -q 'reason=branch_locked' \
    && ok "换一条分支觉醒：branch_locked" || bad "换分支没被拒：$r"
  printf '%s' "$(bstate "$A100")" | grep -q 'level=100' \
    && ok "觉醒不改等级（仍 100，不抬上限）" || bad "觉醒改了等级"

  echo "-- ⑤ 开场包：觉醒技只发给已觉醒的本分支 --"
  "$DOTNET" "$PROBE" --url "$WU/battle" --case b6_awaken \
    --b6-char "$A100" --b6-class warrior --b6-skill 1004 --expect-units 4 \
    >"$TMP/aw-live.log" 2>&1
  grep -q 'verdict=ok' "$TMP/aw-live.log" \
    && ok "觉醒后开场包 4 条、含本分支觉醒技 1004" \
    || bad "觉醒后技能不对：$(tail -2 "$TMP/aw-live.log")"
  "$DOTNET" "$PROBE" --url "$WU/battle" --case b6_awaken \
    --b6-char "$W12" --b6-class warrior --b6-skill 0 --expect-units 3 \
    >"$TMP/aw-off.log" 2>&1
  grep -q 'verdict=ok' "$TMP/aw-off.log" \
    && ok "未觉醒仍是 3 条（觉醒技不在 ForClass 里）" \
    || bad "未觉醒拿到了觉醒技：$(tail -2 "$TMP/aw-off.log")"

  echo "-- ⑥ 网关 POST /character/awaken --"
  A2="$(mkchar mage chixiao)"
  lv "$A2" 100
  printf '%s' "$(astate "$A2")" | grep -q 'skill=0' \
    && ok "没觉醒的法师：skill=0（觉醒不是创号就送）" || bad "没觉醒却有技能"
  code="$(curl -s --noproxy '*' -o "$TMP/aw.json" -w '%{http_code}' \
    -X POST "http://127.0.0.1:61000/character/awaken?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$A2,\"branch_id\":\"chixiao\"}" 2>&1)"
  [ "$code" = "200" ] && grep -qE '"skill_id": *1204' "$TMP/aw.json" \
    && ok "网关觉醒回 200 且 skill_id=1204" || bad "网关觉醒不对（http=$code）：$(head -c 200 "$TMP/aw.json")"
  code="$(curl -s --noproxy '*' -o "$TMP/aw2.json" -w '%{http_code}' \
    -X POST "http://127.0.0.1:61000/character/awaken?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$A2,\"branch_id\":\"chixiao\"}" 2>&1)"
  [ "$code" = "200" ] && grep -qE '"duplicate": *true' "$TMP/aw2.json" \
    && ok "网关重复觉醒回 duplicate=true（200 + ok）" || bad "网关重复觉醒不对（http=$code）"
  code="$(curl -s --noproxy '*' -o "$TMP/aw3.json" -w '%{http_code}' \
    "http://127.0.0.1:61000/character/awaken?character_id=$A2&token=$TOK" 2>&1)"
  [ "$code" = "200" ] && grep -qE '"skill_id": *1204' "$TMP/aw3.json" \
    && ok "网关只读觉醒状态（GET）" || bad "网关 GET 不对（http=$code）"

  stop_pid "$(pgrep -f "Gateway.dll $CG" | head -1)" 2>/dev/null || true
fi

finish "P2-4a（角色服 + 战斗服）"
