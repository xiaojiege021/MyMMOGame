#!/usr/bin/env bash
# P0-3a 核对：五个职业各一组技能和属性，并带上蓝条。牧师能复活。
#
# 完成标准逐条对：
#   1. 坦克提高体力后这一下伤害不变，仇恨更稳 —— 两份体力配置对比同一手
#   2. 同一精神下，法师蓝量上限高于坦克 —— 精神全配成同值的职业表
#   3. 蓝不够的技能放不出去 —— 小蓝量法师连放两个耗蓝技能
#   4. 牧师能拉起倒地队友，对方等级不变 —— 组队场景 + 角色那张表
#   5. 当前级经验是零时，这次不扣 —— 角色表 exp 归零再拉一次
#   6. 牧师蓝不够时拉不起来 —— 第二次复活被拒 NO_MANA
#
# 做这几段：
#   1. 编译战斗进程和 BattleProbe
#   2. 静态断言：仓库配置五职业三技能、蓝量基础排序、复活只有一个、
#      默认配置不开 Boss 出手和阶段（老检查的行为不变）、客户端不算伤害不写结算
#   3. 五职业开场：蓝量排序、生命来自体力、技能数
#   4. 坦克两份体力配置对比
#   5. 蓝不够
#   6~8. 复活三段：扣经验 / 经验为零 / 蓝不够
#
# 用法：tools/check-p0-3a.sh
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
PROBE_DLL="$ROOT/tools/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
PORT=60110
BOSS=9001

PASS=0
FAIL=0
TMP="$(mktemp -d)"
BATTLE_PID=""

cleanup() {
  stop_battle
  rm -rf "$TMP"
}
trap cleanup EXIT

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
detail() { printf '       %s\n' "$1"; }

stop_battle() {
  if [ -n "$BATTLE_PID" ] && kill -0 "$BATTLE_PID" 2>/dev/null; then
    kill -TERM "$BATTLE_PID" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$BATTLE_PID" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$BATTLE_PID" 2>/dev/null || true
    wait "$BATTLE_PID" 2>/dev/null || true
  fi
  BATTLE_PID=""
}

start_battle() {
  stop_battle
  : > "$TMP/battle.log"
  "$DOTNET" "$BATTLE_DLL" --config "$TMP/battle.json" >>"$TMP/battle.log" 2>&1 &
  BATTLE_PID=$!
  for _ in $(seq 1 100); do
    if grep -q '^listen=' "$TMP/battle.log" 2>/dev/null; then
      return 0
    fi
    if ! kill -0 "$BATTLE_PID" 2>/dev/null; then
      break
    fi
    sleep 0.1
  done
  bad "战斗进程没起来"
  tail -8 "$TMP/battle.log" | detail
  return 1
}

probe() {
  "$DOTNET" "$PROBE_DLL" --url "ws://127.0.0.1:$PORT/battle" "$@"
}

# 从 "key=值 …" 的输出里取一个字段。
pick() {
  printf '%s\n' "$@" | tr ' ' '\n' | sed -n "s/^$2=//p" | head -1
}

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Battle/Battle.csproj" -c Release --nologo -v q >"$TMP/build-battle.log" 2>&1 \
  && "$DOTNET" build "$ROOT/tools/BattleProbe/BattleProbe.csproj" -c Release --nologo -v q >"$TMP/build-probe.log" 2>&1; then
  ok "战斗进程与 BattleProbe 编译通过"
else
  bad "编译失败"
  grep -E "error " "$TMP/build-battle.log" "$TMP/build-probe.log" | head -5 | detail
  echo
  echo "P0-3a 核对：$PASS 项通过，$FAIL 项未通过（编译没过，后面的不跑）"
  exit 1
fi

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP" <<'PY' > "$TMP/static.env"
import json, os, sys

root, tmp = sys.argv[1], sys.argv[2]
classes = json.load(open(os.path.join(root, "config", "classes.json"), encoding="utf-8"))
skills = json.load(open(os.path.join(root, "config", "skills.json"), encoding="utf-8"))
battle = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))

by_id = {c["id"]: c for c in classes["classes"]}
assert len(by_id) == 5, "职业不是五个"
for cid, c in by_id.items():
    own = [s for s in skills["skills"] if s["class_id"] == cid]
    assert len(own) == 3, f"{cid} 不是三个技能"
    assert sorted(s["id"] for s in own) == sorted(c["skills"]), f"{cid} 的技能表对不上"

mana_base = {cid: c["mana_base"] for cid, c in by_id.items()}
assert mana_base["mage"] == mana_base["priest"], "法牧基础蓝量应当并列最高"
assert mana_base["mage"] > mana_base["archer"] > mana_base["warrior"] > mana_base["tank"], "基础蓝量排序不对"

resurrects = [s for s in skills["skills"] if s["kind"] == "resurrect"]
assert len(resurrects) == 1 and resurrects[0]["class_id"] == "priest", "复活技能应当只有一个且属于牧师"
assert resurrects[0]["mana_cost"] > 0, "复活应当耗蓝"

# 默认配置不开 Boss 出手、不开阶段：老检查（p0-1b/1c/1d/2b）跑的还是老行为。
assert "attack" not in battle["boss"], "默认配置不该开 Boss 出手"
assert "phases" not in battle["boss"], "默认配置不该配阶段"
assert battle["default_class"] == "warrior", "默认职业应当还是战士"

print("HEAL_SKILL=%d" % next(s["id"] for s in skills["skills"] if s["kind"] == "heal"))
print("RESURRECT=%d" % resurrects[0]["id"])
PY
if [ $? -eq 0 ]; then
  ok "仓库配置：五职业各三技能、蓝量基础法牧>射手>战士>坦克、复活只有牧师一个"
else
  bad "仓库配置静态断言失败"
fi

# 客户端不算伤害、不写结算（与 E7、p0-1c 同口径：写才失败，读不算）。
if grep -qE 'File\.(Write|Append|Create)|Directory\.Create|Npgsql|StreamWriter' \
     -r "$ROOT/client/MMOGame/Assets/Net" "$ROOT/client/MMOGame/Assets/HotUpdate" 2>/dev/null; then
  bad "客户端在写东西，这些只能来自服务端"
else
  ok "客户端只读回包，不落盘"
fi

if grep -qE 'Hp(Max)?\s*[-+]=|\b\w*[Dd]amage\s*[-+]=' \
     -r "$ROOT/client/MMOGame/Assets/Net" "$ROOT/client/MMOGame/Assets/HotUpdate" 2>/dev/null; then
  bad "客户端在算血量和伤害"
else
  ok "客户端没有算血量和伤害"
fi

echo "== 3. 五职业与蓝条 =="
# 一份职业表把精神全配成 40：同一精神下法师的蓝量上限必须高于坦克。
# 技能表副本里把坦克的普攻射程放宽到 30000：站位 6000mm，坦克手短，
# 这里验的是体力 → 伤害/仇恨，不是射程。
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
classes = json.load(open(os.path.join(root, "config", "classes.json"), encoding="utf-8"))
for c in classes["classes"]:
    c["attributes"]["spirit"] = 40
# 坦克体力 30：稳定值 300，血 1200。
classes["classes"] = [c for c in classes["classes"] if c["id"] in ("warrior", "mage", "priest", "archer", "tank")]
json.dump(classes, open(os.path.join(tmp, "classes.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)

skills = json.load(open(os.path.join(root, "config", "skills.json"), encoding="utf-8"))
for s in skills["skills"]:
    if s["id"] == 1401:
        s["range_mm"] = 30000
json.dump(skills, open(os.path.join(tmp, "skills.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
python3 - "$ROOT" "$TMP" "$PORT" <<'PY'
import json, os, sys
root, tmp, port = sys.argv[1], sys.argv[2], sys.argv[3]
battle = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
battle["listen"] = "127.0.0.1:" + port
battle["settlement_dir"] = os.path.join(tmp, "settlements")
battle["character_dir"] = os.path.join(tmp, "characters")
battle["skill_table"] = "skills.json"
battle["class_table"] = "classes.json"
battle["dungeon_table"] = os.path.join(root, "config", "dungeons.json")
json.dump(battle, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
mkdir -p "$TMP/characters"

if start_battle; then
  ok "战斗进程起来了（精神全 40 的职业表）"
else
  echo
  echo "P0-3a 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

CLASSES_OUT="$(probe --case p03a_classes)"
echo "$CLASSES_OUT" | grep '^p03a_' | sed 's/^/       /'
if echo "$CLASSES_OUT" | grep -q 'verdict=ok'; then
  ok "五个职业都进得去：技能各三个、生命来自体力、职业目录随开场下发"
else
  bad "五职业开场有问题"
fi

MAGE_MANA="$(echo "$CLASSES_OUT" | grep '^p03a_class=mage' | sed -n 's/.*mana_max=\([0-9]*\).*/\1/p')"
TANK_MANA="$(echo "$CLASSES_OUT" | grep '^p03a_class=tank' | sed -n 's/.*mana_max=\([0-9]*\).*/\1/p')"
if [ -n "$MAGE_MANA" ] && [ -n "$TANK_MANA" ] && [ "$MAGE_MANA" -gt "$TANK_MANA" ]; then
  ok "同一精神（40）下，法师蓝量上限 $MAGE_MANA 高于坦克 $TANK_MANA"
else
  bad "同一精神下法师蓝量没有高于坦克：$MAGE_MANA vs $TANK_MANA"
fi

TANK_A="$(probe --case p03a_tank --boss $BOSS --hit 1401)"
echo "$TANK_A" | grep '^p03a_' | sed 's/^/       /'
if echo "$TANK_A" | grep -q 'verdict=ok'; then
  ok "坦克打一下：伤害来自装备固定攻击 × 倍率，仇恨含稳定值"
else
  bad "坦克这一下有问题"
fi

echo "== 4. 坦克体力翻倍：伤害不变，仇恨更稳 =="
python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
path = os.path.join(tmp, "classes.json")
classes = json.load(open(path, encoding="utf-8"))
tank = next(c for c in classes["classes"] if c["id"] == "tank")
tank["attributes"]["vitality"] = 60
json.dump(classes, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY

if start_battle; then
  TANK_B="$(probe --case p03a_tank --boss $BOSS --hit 1401)"
  echo "$TANK_B" | grep '^p03a_' | sed 's/^/       /'

  DAMAGE_A="$(echo "$TANK_A" | sed -n 's/.*damage=\([0-9]*\).*/\1/p')"
  DAMAGE_B="$(echo "$TANK_B" | sed -n 's/.*damage=\([0-9]*\).*/\1/p')"
  THREAT_A="$(echo "$TANK_A" | sed -n 's/.*threat=\([0-9]*\).*/\1/p')"
  THREAT_B="$(echo "$TANK_B" | sed -n 's/.*threat=\([0-9]*\).*/\1/p')"

  if [ -z "$DAMAGE_A" ] || [ -z "$THREAT_A" ] || [ -z "$THREAT_B" ]; then
    bad "坦克那一手没打出数字，没法比"
  elif [ "$DAMAGE_A" = "$DAMAGE_B" ]; then
    ok "体力 30 → 60，这一下伤害都是 ${DAMAGE_A}，体力不进入坦克攻击"
  else
    bad "体力变了伤害也变了：$DAMAGE_A → $DAMAGE_B"
  fi

  EXPECT_GAP=$((THREAT_A + 300))
  if [ -n "$THREAT_A" ] && [ "$THREAT_B" = "$EXPECT_GAP" ]; then
    ok "仇恨稳定值涨了 30 × 10 = 300：$THREAT_A → ${THREAT_B}，更不容易被拉开"
  else
    bad "仇恨涨得不对：$THREAT_A → ${THREAT_B}（期望 ${EXPECT_GAP}）"
  fi
else
  bad "体力翻倍后战斗进程起不来"
fi

echo "== 5. 蓝不够的技能放不出去 =="
python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
path = os.path.join(tmp, "classes.json")
classes = json.load(open(path, encoding="utf-8"))
mage = next(c for c in classes["classes"] if c["id"] == "mage")
mage["mana_base"] = 80
mage["mana_per_spirit"] = 0
json.dump(classes, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)

# 顺手把火球的冷却缩到 600ms：这一手等的 700ms 要同时过后摇和冷却，
# 拒绝的原因才能落在「蓝不够」上，不是冷却。
skills = json.load(open(os.path.join(tmp, "skills.json"), encoding="utf-8"))
for s in skills["skills"]:
    if s["id"] == 1202:
        s["cooldown_ms"] = 600
json.dump(skills, open(os.path.join(tmp, "skills.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY

if start_battle; then
  MANA_OUT="$(probe --case p03a_mana --boss $BOSS --hit 1202)"
  echo "$MANA_OUT" | grep '^p03a_' | sed 's/^/       /'
  if echo "$MANA_OUT" | grep -q 'verdict=ok'; then
    ok "法师蓝 80：第一个 60 蓝的火球放得出去，第二个被拒（NO_MANA）"
  else
    bad "蓝不够的拒绝没对上"
  fi
else
  bad "小蓝量配置起不来"
fi

echo "== 6. 牧师复活：拉起倒地队友，扣一部分经验，不掉级 =="
# 换一份场景配置：战士血薄（体力 10 → 400），Boss 一击 600 会把他打倒；
# 牧师血厚（体力 24 → 960），复活冷却 1.5 秒、蓝耗 150、蓝 400 够拉两次。
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]

classes = {
  "schema": "class-table/1", "version": 1, "hp_per_vitality": 40,
  "resurrect": {"hp_restore_pct": 50, "exp_cost_pct": 10},
  "classes": [
    {"id": "warrior", "name": "战士", "primary": "strength",
     "attributes": {"strength": 100, "agility": 40, "spirit": 40, "vitality": 10},
     "attack_per_primary": 2.0, "equipment_attack": 0,
     "mana_base": 400, "mana_per_spirit": 3, "threat_per_vitality": 1, "skills": [1001, 1002, 1003]},
    {"id": "priest", "name": "牧师", "primary": "spirit",
     "attributes": {"strength": 30, "agility": 40, "spirit": 40, "vitality": 24},
     "attack_per_primary": 2.0, "equipment_attack": 0, "heal_per_spirit": 1.5,
     "mana_base": 400, "mana_per_spirit": 6, "threat_per_vitality": 0, "skills": [1301, 1302, 1303]},
  ],
}
json.dump(classes, open(os.path.join(tmp, "classes.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)

skills = {
  "schema": "skill-table/2", "version": 2,
  "skills": [
    {"id": 1001, "name": "劈砍", "class_id": "warrior", "kind": "basic", "damage_multiplier": 0.6, "mana_cost": 0,
     "range_mm": 3000, "foreswing_ms": 400, "recovery_ms": 300, "cooldown_ms": 2000, "stagger_ms": 300},
    {"id": 1002, "name": "重斩", "class_id": "warrior", "kind": "active", "damage_multiplier": 1.3, "mana_cost": 40,
     "range_mm": 30000, "foreswing_ms": 600, "recovery_ms": 400, "cooldown_ms": 2000, "stagger_ms": 200},
    {"id": 1003, "name": "横扫", "class_id": "warrior", "kind": "active", "damage_multiplier": 1.7, "mana_cost": 60,
     "range_mm": 5000, "foreswing_ms": 900, "recovery_ms": 600, "cooldown_ms": 6000, "stagger_ms": 800},
    {"id": 1301, "name": "圣光", "class_id": "priest", "kind": "basic", "damage_multiplier": 0.5, "mana_cost": 0,
     "range_mm": 20000, "foreswing_ms": 450, "recovery_ms": 300, "cooldown_ms": 2000, "stagger_ms": 300},
    {"id": 1302, "name": "治疗", "class_id": "priest", "kind": "heal", "damage_multiplier": 1.0, "mana_cost": 40,
     "range_mm": 20000, "foreswing_ms": 600, "recovery_ms": 400, "cooldown_ms": 2000, "stagger_ms": 0},
    {"id": 1303, "name": "回生", "class_id": "priest", "kind": "resurrect", "damage_multiplier": 0, "mana_cost": 150,
     "range_mm": 20000, "foreswing_ms": 800, "recovery_ms": 300, "cooldown_ms": 1500, "stagger_ms": 0},
  ],
}
json.dump(skills, open(os.path.join(tmp, "skills.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)

battle = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
battle["listen"] = "127.0.0.1:" + "60110"
battle["settlement_dir"] = os.path.join(tmp, "settlements")
battle["character_dir"] = os.path.join(tmp, "characters")
battle["skill_table"] = "skills.json"
battle["class_table"] = "classes.json"
battle["dungeon_table"] = os.path.join(root, "config", "dungeons.json")
battle["boss"]["hp_max"] = 3000
battle["boss"]["attack"] = {"damage": 600, "interval_ms": 1500, "foreswing_ms": 1000}
json.dump(battle, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY

mkdir -p "$TMP/characters"
python3 - "$TMP" <<'PY'
import json, os, sys
json.dump({"schema": "character/1", "level": 5, "exp": 500},
          open(os.path.join(sys.argv[1], "characters", "2.json"), "w", encoding="utf-8"))
PY

if start_battle; then
  RES_OUT="$(probe --case p03a_resurrect --boss $BOSS --party r1 --warrior-char 2 --priest-char 3 --hit 1002 --resurrect 1303 --second ok)"
  echo "$RES_OUT" | grep '^p03a_' | sed 's/^/       /'

  if echo "$RES_OUT" | grep -q 'verdict=ok'; then
    ok "战士被 Boss 打倒后牧师拉起来：回 200 血，对没倒地的和自己发复活都被拒"
  else
    bad "复活这一轮有问题"
  fi

  RESTORE="$(echo "$RES_OUT" | grep '^p03a_resurrect ' | sed -n 's/.*restore=\([0-9]*\).*/\1/p')"
  if [ "$RESTORE" = "200" ]; then
    ok "拉起来回到 50% 血（400 × 50% = 200）"
  else
    bad "回血量不对：$RESTORE"
  fi

  LEVEL_NOW="$(python3 -c "import json,sys; print(json.load(open('$TMP/characters/2.json'))['level'])")"
  EXP_NOW="$(python3 -c "import json,sys; print(json.load(open('$TMP/characters/2.json'))['exp'])")"
  # 两次复活各扣当次的 10%：500 → 450 → 405。
  if [ "$LEVEL_NOW" = "5" ] && [ "$EXP_NOW" = "405" ]; then
    ok "被拉的人等级不变（5），经验 500 → 450 → 405，每次只扣当时已有的一部分"
  else
    bad "等级或经验不对：level=$LEVEL_NOW exp=${EXP_NOW}（期望 5 / 405）"
  fi
else
  bad "复活场景的战斗进程起不来"
fi

echo "== 7. 当前级经验是零时，这次不扣 =="
python3 - "$TMP" <<'PY'
import json, os, sys
json.dump({"schema": "character/1", "level": 5, "exp": 0},
          open(os.path.join(sys.argv[1], "characters", "2.json"), "w", encoding="utf-8"))
PY

RES_ZERO="$(probe --case p03a_resurrect --boss $BOSS --party r2 --warrior-char 2 --priest-char 3 --hit 1002 --resurrect 1303 --second ok)"
echo "$RES_ZERO" | grep '^p03a_' | sed 's/^/       /'
if echo "$RES_ZERO" | grep -q 'verdict=ok'; then
  LEVEL_NOW="$(python3 -c "import json,sys; print(json.load(open('$TMP/characters/2.json'))['level'])")"
  EXP_NOW="$(python3 -c "import json,sys; print(json.load(open('$TMP/characters/2.json'))['exp'])")"
  if [ "$LEVEL_NOW" = "5" ] && [ "$EXP_NOW" = "0" ]; then
    ok "经验是零时拉起来不扣：level=5 exp=0，不掉级"
  else
    bad "经验为零还扣了：level=$LEVEL_NOW exp=$EXP_NOW"
  fi
else
  bad "经验为零这一轮没跑过"
fi

echo "== 8. 牧师蓝不够时拉不起来 =="
python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
path = os.path.join(tmp, "classes.json")
classes = json.load(open(path, encoding="utf-8"))
priest = next(c for c in classes["classes"] if c["id"] == "priest")
# 蓝量 = mana_base + 精神 × mana_per_spirit。两个都定死：
# 200 蓝够拉第一次（150），剩 50 不够第二次。
priest["mana_base"] = 200
priest["mana_per_spirit"] = 0
json.dump(classes, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
json.dump({"schema": "character/1", "level": 5, "exp": 500},
          open(os.path.join(tmp, "characters", "2.json"), "w", encoding="utf-8"))
PY

if start_battle; then
  NOMANA_OUT="$(probe --case p03a_resurrect --boss $BOSS --party r3 --warrior-char 2 --priest-char 3 --hit 1002 --resurrect 1303 --second no_mana)"
  echo "$NOMANA_OUT" | grep '^p03a_' | sed 's/^/       /'

  if echo "$NOMANA_OUT" | grep -q 'p03a_resurrect_second expect=no_mana reason=SkillRejectNoMana'; then
    ok "牧师蓝不够（200 − 150 = 50 < 150），第二次复活被拒：NO_MANA"
  else
    bad "蓝不够的复活没被拒"
  fi

  EXP_NOW="$(python3 -c "import json,sys; print(json.load(open('$TMP/characters/2.json'))['exp'])")"
  if [ "$EXP_NOW" = "450" ]; then
    ok "被拒的那次没有扣经验：只有第一次拉起扣了，500 → 450"
  else
    bad "被拒的复活也动了经验：exp=${EXP_NOW}（期望 450）"
  fi
else
  bad "小蓝量牧师的战斗进程起不来"
fi

echo
echo "P0-3a 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
