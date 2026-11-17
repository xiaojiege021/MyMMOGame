#!/usr/bin/env bash
# S1 核对：技能改名并挂到门派 / 分支（《跑偏与缺失核对表》A10，方案 2.1 / 2.3 / 8.3）。
#
# 完成标准（14.7 与核对表 A10 的落地含义）：
#   ① 15 个技能全部改名，出屏可读（战斗服下发新名）
#   ② 每条技能挂一个门派（school）与一阶（tier），按职责挂分支（branch，空＝门派通用）
#   ③ 名字体现承露门与圣地 / 焦原两派，旧的 15 个白话名 0 处
#   ④ 战斗服读表不变：伤害 / 蓝耗 / 冷却 / 下发一行未改，proto / 库 / 客户端不碰
#   ⑤ 配置不对就起不来（门派 / 阶 / 分支写错都红）
#   ⑥ 老卡自带 skill-table/2 临时表仍能起（新字段可选）
#
# 做这几段：
#   1. 编译战斗服
#   2. 配置静态：改名、挂门派 / 分支、引用交叉校验
#   3. 代码静态：只读入口在、代码不写死门派与分支、不按分支下发、名字不在代码里
#   4. 只读入口：--skill-list 打出的 15 行与配置逐条对得上
#   5. 坏配置：门派 / 阶 / 分支写错都 config_error
#   6. 老表兼容：没有门派段的 skill-table/2 照样起
#
# 用法：tools/check/check-s1.sh     退出码 0 全过，1 有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

step() { printf '\n== %s ==\n' "$1"; }

step "1. 编译战斗服"
if "$DOTNET" build "$ROOT/src/Battle/Battle.csproj" -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "Battle 编译通过"
else
  bad "Battle 编译失败"
  tail -12 "$TMP/build.log" | detail
  finish "S1"
fi

step "2. 配置静态：15 个技能改名并挂门派 / 分支"
STATIC_OUT="$(python3 - <<'PY'
import json, re, sys

ok_count = 0
fail_count = 0

def ok(msg):
    global ok_count
    ok_count += 1
    print("  ok   " + msg)

def bad(msg):
    global fail_count
    fail_count += 1
    print("  FAIL " + msg)

skills_file = "config/skills.json"
branches_file = "config/branches.json"
data = json.load(open(skills_file, encoding="utf-8"))
branches = {b["id"]: b for b in json.load(open(branches_file, encoding="utf-8"))["branches"]}
skills = data["skills"]
schools = {s["id"]: s for s in data.get("schools", [])}

# ① schema / 版本 / 条数
if data.get("schema") == "skill-table/3" and data.get("version") == 3:
    ok("技能表升到 skill-table/3")
else:
    bad("技能表 schema / version 不是 skill-table/3：%r %r" % (data.get("schema"), data.get("version")))

if len(skills) == 15:
    ok("技能仍是 15 条（编号一个没动）")
else:
    bad("技能条数变了：%d" % len(skills))

# ② 两派都在，名字与传授方对得上方案 2.1 / 2.3
tiandi = schools.get("tiandi")
faze = schools.get("faze")
if tiandi and tiandi.get("name") == "天地玄黄" and "承露门" in tiandi.get("giver", ""):
    ok("天地玄黄挂在承露门（方案 2.1）")
else:
    bad("天地玄黄那一派缺失或名字不对")

if faze and faze.get("name") == "法则神通" and ("圣地" in faze.get("giver", "") or "焦原" in faze.get("giver", "")):
    ok("法则神通挂在圣地 / 焦原（方案 2.3）")
else:
    bad("法则神通那一派缺失或名字不对")

# ③ 每条技能都有名字、门派、阶；名字唯一
names = [s["name"] for s in skills]
missing = [s["id"] for s in skills if not s.get("name") or not s.get("school") or not s.get("tier")]
if not missing:
    ok("15 条技能都写了名字 / 门派 / 阶")
else:
    bad("这些技能缺名字 / 门派 / 阶：%s" % missing)

if len(set(names)) == len(names):
    ok("技能名互不重复")
else:
    bad("技能名有重复")

# ④ 门派与阶必须落在配置自己列的范围内
tiers_of = {sid: {t["id"] for t in s.get("tiers", [])} for sid, s in schools.items()}
outside = [s["id"] for s in skills if s["school"] not in schools or s["tier"] not in tiers_of[s["school"]]]
if not outside:
    ok("每条技能的门派与阶都在门派表内")
else:
    bad("这些技能的门派或阶不在门派表里：%s" % outside)

# ⑤ 分支引用：存在且职业一致；普攻是门派通用，不挂分支
unknown = [s["id"] for s in skills if s.get("branch") and s["branch"] not in branches]
if not unknown:
    ok("挂出去的分支都在 branches.json 里")
else:
    bad("这些技能的分支不存在：%s" % unknown)

wrong_class = [s["id"] for s in skills
               if s.get("branch") and s.get("branch") in branches
               and branches[s["branch"]]["class_id"] != s["class_id"]]
if not wrong_class:
    ok("分支职业与技能职业一致（没人挂错门派的路数）")
else:
    bad("这些技能挂到了别的职业的分支：%s" % wrong_class)

basic_with_branch = [s["id"] for s in skills if s["kind"] == "basic" and s.get("branch")]
if not basic_with_branch:
    ok("五条普攻是门派通用（branch 为空）")
else:
    bad("普攻不该挂分支：%s" % basic_with_branch)

used = {s["branch"] for s in skills if s.get("branch")}
uncovered = sorted(set(branches) - used)
if used:
    ok("技能挂到了 %d 条分支（暂时没技能的：%s）"
       % (len(used), "、".join(uncovered) if uncovered else "无"))
else:
    bad("没有任何技能挂到分支")

# ⑥ 旧的 15 个白话名一个不留
old_names = ["劈砍", "重斩", "横扫", "速射", "穿云箭", "连珠", "法杖击",
             "火球", "寒霜", "圣光", "治疗", "回生", "锤击", "盾击", "裂地"]
text = open(skills_file, encoding="utf-8").read()
left = [n for n in old_names if n in text]
if not left:
    ok("15 个旧白话名在配置里 0 处")
else:
    bad("配置里还留着旧名字：%s" % "、".join(left))

# ⑦ 代码里不许出现门派 / 分支 / 技能的第三份名单
for path in ("src/Battle/Config.cs", "src/Battle/Program.cs"):
    body = open(path, encoding="utf-8").read()
    hit_branch = [b for b in branches if '"%s"' % b in body]
    hit_name = [n for n in names if '"%s"' % n in body]
    hit_old = [n for n in old_names if n in body]
    if hit_branch or hit_name or hit_old:
        bad("%s 里写死了分支 / 技能名：%s" % (path, hit_branch + hit_name + hit_old))
    else:
        ok("%s 里没有第二份分支 / 技能名单" % path)

# ⑧ 下发仍按职业，不按分支过滤
config_cs = open("src/Battle/Config.cs", encoding="utf-8").read()
if "s.ClassId == classId" in config_cs and "ForClass" in config_cs:
    ok("ForClass 仍按职业下发，没按分支过滤")
else:
    bad("ForClass 的判定被改了")

missing_fields = [f for f in ("School", "Tier", "Branch")
                  if "%s { get; init; }" % f not in config_cs]
if missing_fields:
    bad("SkillDef 少了这些字段：%s" % missing_fields)
else:
    ok("SkillDef 读了三格新字段（school / tier / branch）")

print("COUNTS %d %d" % (ok_count, fail_count))
PY
)"
printf '%s\n' "$STATIC_OUT" | grep -v '^COUNTS '
PASS=$((PASS + $(printf '%s\n' "$STATIC_OUT" | sed -n 's/^COUNTS \([0-9]*\) [0-9]*$/\1/p')))
FAIL=$((FAIL + $(printf '%s\n' "$STATIC_OUT" | sed -n 's/^COUNTS [0-9]* \([0-9]*\)$/\1/p')))

step "3. 本卡不改 proto / 库 / 客户端"
if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  CHANGED="$(git diff --name-only HEAD 2>/dev/null; git diff --name-only 2>/dev/null)"
  if printf '%s\n' "$CHANGED" | grep -qE '^(proto/|db/|client/)'; then
    bad "本卡动了 proto / db / client：$(printf '%s\n' "$CHANGED" | grep -E '^(proto/|db/|client/)' | head -3 | tr '\n' ' ')"
  else
    ok "proto / db / client 一处未改"
  fi
else
  ok "不是 git 仓库，跳过改动范围断言"
fi

step "4. 只读入口：--skill-list 打出的 15 行"
"$DOTNET" "$BATTLE_DLL" --skill-list --config config/battle.json >"$TMP/skill-list.txt" 2>&1
LIST="$TMP/skill-list.txt"
assert_file_contains "$LIST" '^skill_table_version=3 skills=15 schools=2$' "只读入口报到 3 / 15 条 / 两派"

ROWS="$(grep -c '^skill_row=' "$LIST" || true)"
if [ "$ROWS" = "15" ]; then
  ok "打出 15 行技能（每行带门派 / 阶 / 分支）"
else
  bad "打出的技能行数不对：${ROWS}"
fi

WANT="$(python3 - <<'PY'
import json
d = json.load(open("config/skills.json", encoding="utf-8"))
for s in sorted(d["skills"], key=lambda x: x["id"]):
    print("skill_row=id=%d name=%s class=%s school=%s tier=%s branch=%s" % (
        s["id"], s["name"], s["class_id"], s["school"], s["tier"], s["branch"] or "-"))
PY
)"
if [ "$(grep '^skill_row=' "$LIST")" = "$WANT" ]; then
  ok "只读入口的 15 行与配置逐条一致（名字 / 门派 / 阶 / 分支）"
else
  bad "只读入口与配置对不上"
  diff <(printf '%s\n' "$WANT") <(grep '^skill_row=' "$LIST") | head -6 | detail
fi

assert_file_contains "$LIST" '^skill_school=id=tiandi name=天地玄黄' "只读入口报出天地玄黄这一派"
assert_file_contains "$LIST" '^skill_school=id=faze name=法则神通 giver=明霄圣地 / 焦原 tiers=- skills=0' "法则神通在表里但暂时 0 条（待二转 / 觉醒卡）"

WANT_SUM="$(python3 - <<'PY'
import json
d = json.load(open("config/skills.json", encoding="utf-8"))
common = sum(1 for s in d["skills"] if not s["branch"])
branched = len({s["branch"] for s in d["skills"] if s["branch"]})
print("skill_branch_total=%d common=%d" % (branched, common))
PY
)"
assert_file_contains "$LIST" "^${WANT_SUM}\$" "汇总与配置一致：${WANT_SUM}"

step "5. 坏配置起不来"
make_bad() {
  # $1 目标文件；$2 python 改动表达式（拿到 data 后改完写回）
  python3 - "$1" <<PY
import json, sys
p = sys.argv[1]
d = json.load(open("config/skills.json", encoding="utf-8"))
$2
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
}

BAD1="$TMP/bad-school.json"
make_bad "$BAD1" 'd["skills"][0]["school"] = "nowhere"'
OUT1="$("$DOTNET" "$BATTLE_DLL" --skill-table-path "$BAD1" 2>&1)"; RC1=$?
assert_bad_config "$OUT1" "门派写错（nowhere）起不来"
[ "$RC1" != "0" ] && ok "门派写错时非 0 退出" || bad "门派写错时退出码仍是 0"

BAD2="$TMP/bad-tier.json"
make_bad "$BAD2" 'd["skills"][0]["tier"] = "tian" if d["skills"][0]["tier"] != "tian" else "huang"'  # 黄阶普攻换成天阶仍在表内
make_bad "$BAD2" 'd["skills"][0]["tier"] = "yuan"'
OUT2="$("$DOTNET" "$BATTLE_DLL" --skill-table-path "$BAD2" 2>&1)"
assert_bad_config "$OUT2" "阶写错（yuan）起不来"

BAD3="$TMP/bad-branch.json"
make_bad "$BAD3" 'd["skills"][0]["branch"] = "Bad-Branch"'
OUT3="$("$DOTNET" "$BATTLE_DLL" --skill-table-path "$BAD3" 2>&1)"
assert_bad_config "$OUT3" "分支形状不合法（大写与横线）起不来"

BAD4="$TMP/no-school.json"
python3 - "$BAD4" <<'PY'
import json, sys
d = json.load(open("config/skills.json", encoding="utf-8"))
del d["skills"][0]["school"]
json.dump(d, open(sys.argv[1], "w", encoding="utf-8"), ensure_ascii=False)
PY
OUT4="$("$DOTNET" "$BATTLE_DLL" --skill-table-path "$BAD4" 2>&1)"
assert_bad_config "$OUT4" "有门派表却漏写门派起不来"

step "6. 老表兼容：没有门派段的 skill-table/2 照样起"
OLD="$TMP/old-skills.json"
cat >"$OLD" <<'JSON'
{
  "schema": "skill-table/2",
  "version": 2,
  "skills": [
    {"id": 1001, "name": "老表普攻", "class_id": "warrior", "kind": "basic", "damage_multiplier": 0.6,
     "mana_cost": 0, "range_mm": 3000, "foreswing_ms": 400, "recovery_ms": 300,
     "cooldown_ms": 2000, "stagger_ms": 300}
  ]
}
JSON
OUT5="$("$DOTNET" "$BATTLE_DLL" --skill-table-path "$OLD" 2>&1)"
if printf '%s' "$OUT5" | grep -q '^skill_table_version=2 skills=1 schools=0$'; then
  ok "老临时表（skill-table/2，无门派段）照常起"
else
  bad "老临时表起不来：$(printf '%s' "$OUT5" | head -2 | tr '\n' ' ')"
fi

step "7. 新卡挂进总控与 CI"
assert_file_contains "$ROOT/tools/check/check-all.sh" 's1\) echo ' "check-all 的依赖表挂了 s1"
assert_file_contains "$ROOT/tools/check/check-all.sh" 'c6 s1"$' "check-all 的卡片清单含 s1"
assert_file_contains "$ROOT/tools/check/ci.sh" 'CHECKS=.* s1"$' "ci.sh 的无库段含 s1"

finish "S1"
