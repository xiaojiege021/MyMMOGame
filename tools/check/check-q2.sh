#!/usr/bin/env bash
# tools/check/check-q2.sh —— Q2 F2 效果层实现（阶段 2 第二张，未覆盖排期第 21 行）。
#
# 完成标准：StatusManager + DamagePipeline 按 Q1 的表落地；技能 / 丹药 / 道具走同一套 effect；
# DoT / HoT 由服务端时钟驱动；proto 加效果快照；客户端只画 buff 条与战斗日志；
# 死亡 / 结算清空；竞技场模板、战场 1.3、城战科技不被破坏。
#
# 本脚本无库、不跑 Unity：
#   1. 编译战斗服 / 角色服 / 战斗探针；2. 静态断言（proto / 服务端 / 客户端 / 文档 / 挂载）；
#   3. 只读入口 --effect-list / --damage-preview；4. 13.5 的坏配置起不来；
#   5. 真服（临时配置）跑效果层场景：buff+DoT、护盾、眩晕/走位、净化、CC 递减、打断。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
TITLE="Q2（效果层实现）"
TMP="$(mktemp -d)"
PIDS=""
cleanup() { for p in $PIDS; do stop_pid "$p"; done; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Battle/Battle.csproj src/Character/Character.csproj tools/probe/BattleProbe/BattleProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1 || {
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail; }
done
[ "$BUILD_OK" = "1" ] && ok "战斗服 / 角色服 / 战斗探针编译通过（0 错误）" || true

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP/static.txt" <<'PY'
import json, os, re, sys
root, out = sys.argv[1], sys.argv[2]
o = open(out, "w", encoding="utf-8")
def R(k, c, i=" "):
    o.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + str(i)) + "\n")
def rd(p):
    return open(os.path.join(root, p), encoding="utf-8").read() if os.path.exists(os.path.join(root, p)) else ""
def js(p):
    return json.load(open(os.path.join(root, p), encoding="utf-8")) if os.path.exists(os.path.join(root, p)) else {}

proto = rd("proto/mmo/battle.proto")
R("proto_effect_snapshot", "message EffectSnapshot" in proto and "message EffectState" in proto
  and "message BossState" in proto)
R("proto_effect_fields", all(x in proto for x in
  ("string effect_id = 1", "string name = 2", "string kind = 3", "uint64 source_id = 4",
   "uint32 remaining_ms = 5", "uint32 stacks = 6", "int64 shield_left = 7")))
R("proto_effect_fields_wired", "EffectState effects = 15" in proto and "BossState boss_state = 16" in proto)
R("proto_open_effects", "repeated EffectState effects = 20" in proto)
R("proto_control_rejects", "SKILL_REJECT_STUNNED = 12" in proto and "SKILL_REJECT_SILENCED = 13" in proto)
R("proto_move_blocked", "bool blocked = 5" in proto)
charproto = rd("proto/mmo/character.proto")
R("proto_passive_bps", "int32 passive_pct_bps = 8" in charproto)
R("gen_both_sides", all("EffectSnapshot" in rd(p) and "BossState" in rd(p) for p in
  ("src/Shared/Generated/Battle.cs", "client/MMOGame/Assets/Proto/Generated/Battle.cs")))
contract = rd("proto/manifest.json")
R("manifest_regenerated", "battle.proto" in contract and "character.proto" in contract)

cfg = js("config/effects.json")
R("effect_table_green", cfg.get("schema") == "effect-table/2" and len(cfg.get("effects") or []) >= 9)

eff = rd("src/Battle/Effects.cs")
dmg = rd("src/Battle/DamagePipeline.cs")
enc = rd("src/Battle/Encounter.cs")
R("runtime_files", bool(eff) and bool(dmg))
R("status_single_entry", "public EffectApply Apply(" in eff and "public StatusTick Tick(" in eff)
R("status_stacking", all(x in eff for x in ('"refresh"', '"stack"', '"independent"',
                                            "NextDiminishingPct", "RemoveByTags", "ImmunityTags.Any")))
R("damage_four_stages", all(x in dmg for x in ("baseAttack", "enhanced", "skillDamage", "Mitigation",
                                               "Final", "RoundHalfUp", "CrystalFlatAttack")))
R("damage_guard", "baseAttack <= 0" in dmg)
R("damage_uses_table", "EffectTable table" in dmg and "table.Damage" in dmg)
R("encounter_status", all(x in enc for x in ("StatusSet", "TickEffects", "ApplySkillEffects",
                                             "DamagePipeline.Compute", "member.Status.Rooted",
                                             "Status.Absorb", "Status.Clear")))
R("encounter_control_rejects", "SkillRejectStunned" in enc and "SkillRejectSilenced" in enc)
R("no_second_damage_formula", "AttackPower() * skill.DamageMultiplier" not in enc)
R("death_clears", enc.count("Status.Clear()") >= 3)
R("boss_effects", "_bossUnit.Status" in enc and "BossStateOf" in enc)

skill = rd("src/Battle/Config.cs")
R("skill_effect_block", "List<EffectSpec> Effects" in skill and "effect_table" in skill
  and "不在效果表里" in skill)
R("boss_defense", "public long Defense" in rd("src/Shared.Domain/BossConfig.cs"))

grpc = rd("src/Character/CharacterGrpcService.cs")
R("character_passive", "PassivePctBps = passive.Found" in grpc)
R("battle_profile_passive", "reply.PassivePctBps" in rd("src/Battle/BattleProfileClient.cs"))

client = rd("client/MMOGame/Assets/HotUpdate/BattleClient.cs")
hud = rd("client/MMOGame/Assets/HotUpdate/BattleHud.cs")
R("client_effect_state", "BodyOneofCase.Effects" in client and "BodyOneofCase.BossState" in client
  and "SelfEffects" in client)
R("client_blocked", "moved.Blocked" in client)
R("client_hud_buff", "buff" in hud.lower() and "remaining_ms" in hud)
def code_only(text):
    return "\n".join(line for line in text.splitlines() if not line.strip().startswith("//"))
R("client_no_config", "effects.json" not in code_only(client) and "effects.json" not in code_only(hud)
  and "File.ReadAllText" not in client and "File.ReadAllText" not in hud)

design = rd("docs/效果层设计.md")
config_design = rd("docs/配置设计.md")
R("docs_q2", "Q2 实现" in design or "Q2 的落点" in design)
R("docs_validation", "13.5" in config_design and "effect-list" in config_design)
queue = rd("docs/任务队列.md")
R("queue_q2_row", re.search(r"\| 21 \| 2 \| Q2 \|[^\n]*\| (进行中|完成) \|", queue) is not None)
ci = rd("tools/check/ci.sh")
R("ci_registered", re.search(r"^CHECKS=.*\bq2\b", ci, re.M) is not None)
allsh = rd("tools/check/check-all.sh")
R("checkall_deps", re.search(r"q2\)\s*echo", allsh) is not None
  and re.search(r'ALL_CARDS="[^"]*\bq2\b', allsh, re.S) is not None)
q1 = rd("tools/check/check-q1.sh")
R("q1_reader_updated", "only_q2_reader" in q1 or "allowed_readers" in q1)

PY
while IFS= read -r line; do
  case "$line" in
    "PASS "*) ok "${line#PASS }" ;;
    "FAIL "*) bad "${line#FAIL }" ;;
  esac
done < <(grep '^PASS\|^FAIL' "$TMP/static.txt")
grep -q '^PASS\|^FAIL' "$TMP/static.txt" || { bad "静态段没有输出结果"; tail -5 "$TMP/static.txt" | detail; }

echo "== 3. 只读入口 =="
EL="$("$DOTNET" "$BD" --effect-list --config config/battle.json 2>&1)"; rc=$?
[ "$rc" = "0" ] && ok "效果清单可读（rc=0）" || bad "效果清单读不了（rc=$rc）：$(printf '%s' "$EL" | head -2)"
for pat in 'effect_table=schema=effect-table/2 version=3' 'effects=9' 'kind=buff' 'kind=debuff' \
           'kind=control' 'kind=hot' 'kind=dot' 'kind=shield' 'kind=cleanse' \
           'effect_interaction=cc_window_ms=15000 steps=100,60,30,0' \
           'effect_damage=stages=base,enhance,skill,final'; do
  printf '%s' "$EL" | grep -q "$pat" && ok "清单含 ${pat%%=*}" || bad "清单缺 ${pat}"
done

preview() { "$DOTNET" "$BD" --damage-preview --config config/battle.json "$@" 2>&1 | grep -oE 'rounded=[0-9]+' | cut -d= -f2; }
[ "$(preview --attack 200 --multiplier 0.6 --random-pct 100)" = "120" ] \
  && ok "四段默认值：200 × 0.6 = 120（与方案 7.2 逐字等价）" || bad "默认四段不是 120"
[ "$(preview --attack 200 --multiplier 0.6 --defense 100 --random-pct 100)" = "60" ] \
  && ok "防御 100 / K=100：减免 50%，最终 60" || bad "防御减免不对"
[ "$(preview --attack 200 --multiplier 0.6 --realm-bps 1000 --random-pct 100)" = "132" ] \
  && ok "境界加成 +10%（bps=1000）：120 → 132" || bad "境界加成不对"
[ "$(preview --attack 200 --multiplier 0.6 --attack-pct 10 --random-pct 100)" = "132" ] \
  && ok "攻击百分比 +10%：120 → 132" || bad "攻击百分比不对"

echo "== 4. 坏配置起不来（配置设计 13.5） =="
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
base = json.load(open(os.path.join(root, "config", "effects.json"), encoding="utf-8"))
def dump(name, mut):
    cfg = json.loads(json.dumps(base))
    mut(cfg)
    json.dump(cfg, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
def schema(c): c["schema"] = "effect-table/1"
def dup(c): c["effects"][1]["id"] = c["effects"][0]["id"]
def tick(c):
    for e in c["effects"]:
        if e["kind"] == "hot": e.pop("tick_ms", None)
def stages(c): c["damage"]["stages"] = c["damage"]["stages"][:3]
def cap(c): c["damage"]["penetration"]["cap_pct"] = 200
def ratio(c): c["damage"]["boundaries"]["battlefield"]["ratio"] = 1.3
def stat(c):
    for e in c["effects"]:
        if e["kind"] == "buff": e["modifiers"][0]["stat"] = "haste_pct"
for name, mut in (("schema", schema), ("dup", dup), ("tick", tick), ("stages", stages),
                  ("cap", cap), ("ratio", ratio), ("stat", stat)):
    dump(name, mut)
PY
for c in schema dup tick stages cap ratio stat; do
  out="$("$DOTNET" "$BD" --effect-list --effect-table-path "$TMP/bad-$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error='; then
    ok "坏配置 ${c}：起不来（config_error=）"
  else
    bad "坏配置 ${c} 没挡住（rc=${rc}）：$(printf '%s' "$out" | head -2)"
  fi
done

echo "== 5. 真服（临时配置）：效果层场景 =="
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, shutil, sys
root, tmp = sys.argv[1], sys.argv[2]
fx = os.path.join(tmp, "fx")
os.makedirs(fx)
for f in os.listdir(os.path.join(root, "config")):
    if f.endswith(".json"):
        shutil.copy(os.path.join(root, "config", f), fx)

effects = json.load(open(os.path.join(fx, "effects.json"), encoding="utf-8"))
effects["damage"]["random"]["min_pct"] = 100
effects["damage"]["random"]["max_pct"] = 100
json.dump(effects, open(os.path.join(fx, "effects.json"), "w", encoding="utf-8"), ensure_ascii=False)

skills = json.load(open(os.path.join(fx, "skills.json"), encoding="utf-8"))
by = {x["id"]: x for x in skills["skills"]}
# A：buff / DoT / 护盾
by[1001].update({"effects": [{"effect_id": "buff_attack_up_1", "target": "self"}], "range_mm": 30000})
by[1002].update({"effects": [{"effect_id": "dot_poison_1", "target": "target"}], "range_mm": 30000})
by[1003].update({"effects": [{"effect_id": "shield_1", "target": "self"}], "range_mm": 30000})
json.dump(skills, open(os.path.join(fx, "skills-a.json"), "w", encoding="utf-8"), ensure_ascii=False)

# B：控制 / 净化 / 递减
b = json.loads(json.dumps(skills))
bb = {x["id"]: x for x in b["skills"]}
bb[1001].update({"effects": [{"effect_id": "cc_stun_1", "target": "self"}], "range_mm": 30000,
                 "cooldown_ms": 0})
bb[1002].update({"effects": [{"effect_id": "dot_poison_1", "target": "self"}], "range_mm": 30000})
bb[1003].update({"effects": [{"effect_id": "cleanse_1", "target": "self"}], "range_mm": 30000})
s1011 = dict(bb[1001]); s1011.update({"id": 1011, "name": "缓行测试",
    "effects": [{"effect_id": "cc_slow_1", "target": "self"}], "cooldown_ms": 0,
    "foreswing_ms": 50, "recovery_ms": 0, "mana_cost": 0, "damage_multiplier": 0.1})
b["skills"].append(s1011)
json.dump(b, open(os.path.join(fx, "skills-b.json"), "w", encoding="utf-8"), ensure_ascii=False)

# C：打断（长前摇 + Boss 立刻出手）
c = json.loads(json.dumps(skills))
cc = {x["id"]: x for x in c["skills"]}
cc[1001].update({"foreswing_ms": 3000, "cooldown_ms": 0, "range_mm": 30000})
json.dump(c, open(os.path.join(fx, "skills-c.json"), "w", encoding="utf-8"), ensure_ascii=False)

battle = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
battle.update({"settlement_dir": os.path.join(fx, "settlements"), "character_dir": os.path.join(fx, "characters"),
               "effect_table": "effects.json", "boss": dict(battle["boss"], hp_max=900)})
battle["boss"].pop("attack", None); battle["boss"].pop("phases", None)

def write(name, skills_file, attack):
    cfg = json.loads(json.dumps(battle))
    cfg["skill_table"] = skills_file
    if attack:
        cfg["boss"]["attack"] = {"damage": 80, "interval_ms": 2000, "foreswing_ms": 500}
    json.dump(cfg, open(os.path.join(fx, name + ".json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)

write("a", "skills-a.json", False)
write("b", "skills-b.json", False)
write("c", "skills-c.json", True)
write("d", "skills-a.json", True)
PY
start_battle() {
  local name="$1" port_var="$2"
  "$DOTNET" "$BD" --config "$TMP/fx/$name.json" >"$TMP/$name.log" 2>&1 &
  PIDS="$PIDS $!"
  local url=""
  for _ in $(seq 1 120); do
    url="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/$name.log" 2>/dev/null | tail -1 | sed 's#^listen=http://#ws://#')"
    [ -n "$url" ] && break; sleep 0.1
  done
  eval "$port_var=\"\$url\""
}
start_battle a UA; start_battle b UB; start_battle c UC; start_battle d UD
[ -n "$UA" ] && [ -n "$UB" ] && [ -n "$UC" ] && [ -n "$UD" ] \
  && ok "四份临时战斗配置都起服（effects=ok）" || bad "临时战斗服没起来"
grep -q '^effects=ok version=3 effects=9' "$TMP/a.log" && ok "启动日志带 effects=ok（读的是效果表）" \
  || bad "启动日志没有 effects=ok"

probe() { "$DOTNET" "$PROBE" "$@" 2>&1; }
OUT="$(probe --url "$UA/battle" --case q2_buff_dot --boss 9001 --buff 1001 --dot 1002)"
printf '%s' "$OUT" | grep -q '^verdict=ok' && printf '%s' "$OUT" | grep -q 'first_damage=120' \
  && printf '%s' "$OUT" | grep -q 'second_damage=286' \
  && ok "buff 进伤害：第一下 120，+10% 后第二下 286（200×1.3×1.1）" \
  || { bad "buff/DoT 场景不对"; printf '%s' "$OUT" | detail; }
printf '%s' "$OUT" | grep -qE 'dot_tick_hp=467 ' && ok "DoT 按服务端时钟跳：900 → 494 → 467（每 2 秒 3%）" \
  || { bad "DoT 没跳或跳得不对"; printf '%s' "$OUT" | detail; }

OUT="$(probe --url "$UD/battle" --case q2_shield --boss 9001 --shield 1003)"
printf '%s' "$OUT" | grep -q '^verdict=ok' && printf '%s' "$OUT" | grep -q 'shield_left=240 shield_after=160' \
  && ok "护盾先吃伤害：240 → 160，挨打后血量停住（80 被盾吃掉）" \
  || { bad "护盾场景不对"; printf '%s' "$OUT" | detail; }

OUT="$(probe --url "$UB/battle" --case q2_control --boss 9001 --control 1001 --hit2 1002)"
printf '%s' "$OUT" | grep -q '^verdict=ok' && printf '%s' "$OUT" | grep -q 'reject=SkillRejectStunned' \
  && printf '%s' "$OUT" | grep -q 'moved_blocked=True' \
  && ok "眩晕：施法拒绝 Stunned、走位 blocked=true、到点自动解除" \
  || { bad "控制场景不对"; printf '%s' "$OUT" | detail; }

OUT="$(probe --url "$UB/battle" --case q2_cleanse --boss 9001 --dot 1002 --cleanse 1003)"
printf '%s' "$OUT" | grep -q '^verdict=ok' && printf '%s' "$OUT" | grep -q 'dot_seen=True' \
  && ok "净化按标签移除：先挂上 DoT，净化后快照里没有它" \
  || { bad "净化场景不对"; printf '%s' "$OUT" | detail; }

OUT="$(probe --url "$UB/battle" --case q2_diminish --boss 9001 --slow 1011)"
printf '%s' "$OUT" | grep -q '^verdict=ok' && printf '%s' "$OUT" | grep -q 'remaining_ms=4000,2400,1200' \
  && ok "CC 递减：同类控制 100% / 60% / 30%（4000 → 2400 → 1200）" \
  || { bad "CC 递减不对"; printf '%s' "$OUT" | detail; }

OUT="$(probe --url "$UC/battle" --case q2_interrupt --boss 9001 --control 1001)"
printf '%s' "$OUT" | grep -q '^verdict=ok' && printf '%s' "$OUT" | grep -q 'cancelled=True' \
  && ok "前摇里挨打会打断：客户端收 Cancelled，技能进短冷却" \
  || { bad "打断场景不对"; printf '%s' "$OUT" | detail; }

grep -qE '^effect_apply=.*effect=dot_poison_1 result=Applied' "$TMP/a.log" \
  && ok "施加日志只有一处入口（effect_apply=）" || bad "没有 effect_apply 日志"

finish "$TITLE"
