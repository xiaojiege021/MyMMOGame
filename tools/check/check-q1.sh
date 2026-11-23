#!/usr/bin/env bash
# tools/check/check-q1.sh —— Q1 效果层设计（阶段 2 第一张卡，只写设计与配置）。
#
# 完成标准（docs/未覆盖排期.md 第 2 节 Q1 行）：
#   7 类效果每类至少一条样例；交互规则各有配置键；伤害按方案 7.2 四段写死；
#   穿透 / 抗性已打开（默认 0）；PVE / PVP 边界写进配置；只写设计与配置，不改代码。
#
# 本脚本只读、无库、不编译：
#   1. config/effects.json 的 schema / 七类 / 样例 / 交互 / 四段 / 穿透 / 抗性 / 边界；
#   2. docs/效果层设计.md、docs/配置设计.md 第 13 节、docs/数值台账.md 与排期的回写；
#   3. 护栏：ci.sh / check-all.sh 挂 q1；src/ 里没有 effects.json 读取方（Q1 不改代码）。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/tools/check/lib-common.sh"

TITLE="Q1（效果层设计）"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

python3 - >"$TMP/static.txt" 2>&1 <<'PY'
import json
import os
import re
import sys


def read(path):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server", "CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World"}
    _name = __import__("os").path.basename(path)
    if _name in _sub:
        _os = __import__("os"); _g = __import__("glob")
        _b = path if _os.path.isabs(path) else _os.path.join(_os.getcwd(), path)
        if not _os.path.exists(_b):
            _d = _os.path.dirname(_os.getcwd())
            while _d and not _os.path.exists(_os.path.join(_d, path)):
                _nd = _os.path.dirname(_d)
                if _nd == _d: break
                _d = _nd
            _b = _os.path.join(_d, path)
        return "".join(open(f, encoding="utf-8").read() for f in
                       [_b] + sorted(_g.glob(_os.path.join(_os.path.dirname(_b), _sub[_name], "*.cs"))))
    try:
        return open(path, encoding="utf-8").read()
    except Exception:
        return ""


def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + str(info)))


try:
    cfg = json.loads(read("config/effects.json"))
except Exception as ex:
    print("FAIL json_parse  " + str(ex))
    sys.exit(0)

report("schema", cfg.get("schema") == "effect-table/2", cfg.get("schema"))
report("version", cfg.get("version") == 3, cfg.get("version"))
report("top_pending", cfg.get("pending") is True)

controls = [c.get("id") for c in cfg.get("control_types") or []]
report("control_types", controls == ["root", "slow", "stun", "silence"], controls)
stun = next((c for c in cfg.get("control_types") or [] if c.get("id") == "stun"), {})
silence = next((c for c in cfg.get("control_types") or [] if c.get("id") == "silence"), {})
report("stun_sample", stun.get("sample") == "cc_stun_1", stun.get("sample"))
report("silence_key_only", not silence.get("sample"), silence.get("sample"))

effects = cfg.get("effects") or []
ids = [e.get("id") for e in effects]
kinds = [e.get("kind") for e in effects]
report("effects_count", len(effects) >= 9, len(effects))
report("ids_unique", len(ids) == len(set(ids)), "%d/%d" % (len(ids), len(set(ids))))
report("seven_kinds",
       set(kinds) == {"buff", "debuff", "control", "hot", "dot", "shield", "cleanse"},
       sorted(set(kinds)))
report("stun_effect_present", "cc_stun_1" in ids)
report("all_samples_pending", all(e.get("pending") is True for e in effects))

report("base_ids_kept",
       ids[:8] == ["buff_attack_up_1", "debuff_defense_down_1", "cc_root_1", "cc_slow_1",
                   "hot_regen_1", "shield_1", "cleanse_1", "dot_poison_1"],
       ids[:8])
stack_rules = {"refresh", "stack", "independent"}
bad = []
for e in effects:
    eid, kind = e.get("id"), e.get("kind")
    if not e.get("name") or not eid:
        bad.append(eid or "?")
    if kind != "cleanse" and e.get("stack_rule") not in stack_rules:
        bad.append(eid + ":stack_rule")
    if not isinstance(e.get("max_stacks", 1), int) or e.get("max_stacks", 1) < 1:
        bad.append(eid + ":max_stacks")
    if kind != "cleanse" and not (isinstance(e.get("duration_ms"), int) and e.get("duration_ms") > 0):
        bad.append(eid + ":duration")
    if kind in ("hot", "dot") and not (isinstance(e.get("tick_ms"), int) and e.get("tick_ms") > 0):
        bad.append(eid + ":tick")
    if kind in ("hot", "shield") and not (0 < e.get("amount_pct_max_hp", 0) <= 100):
        bad.append(eid + ":amount_pct")
    if kind in ("buff", "debuff", "dot") and not e.get("modifiers"):
        bad.append(eid + ":modifiers")
    if kind == "control":
        if e.get("control") not in controls:
            bad.append(eid + ":control")
        if not e.get("diminishing"):
            bad.append(eid + ":diminishing")
        if not e.get("dispel_tags"):
            bad.append(eid + ":dispel_tags")
report("effect_fields", not bad, bad[:6])

inter = cfg.get("interaction") or {}
report("interaction_keys",
       all(k in inter for k in ("interrupt", "hitstun", "cc_diminishing", "stacking",
                                "dispel", "immunity", "cleanse")))
report("cc_diminishing_shape",
       len(inter.get("cc_diminishing", {}).get("steps_pct") or []) == 4
       and inter.get("cc_diminishing", {}).get("window_ms", 0) > 0)
report("stacking_rules",
       set(inter.get("stacking", {}).get("rules") or []) == stack_rules)
report("interaction_pending", inter.get("pending") is True)

damage = cfg.get("damage") or {}
stages = damage.get("stages") or []
report("damage_pending", damage.get("pending") is True)
report("four_stages", [s.get("id") for s in stages] == ["base", "enhance", "skill", "final"],
       [s.get("id") for s in stages])
formulas = {s.get("id"): s.get("formula", "") for s in stages}
report("stage_base_formula",
       "primary" in formulas.get("base", "") and "realm_bonus_pct" in formulas.get("base", ""))
report("stage_enhance_formula",
       "enhance_coef" in formulas.get("enhance", "") and "crystal_flat_attack" in formulas.get("enhance", ""))
report("stage_skill_formula",
       "skill_multiplier" in formulas.get("skill", "") and "counter_coef" in formulas.get("skill", ""))
report("stage_final_formula",
       all(x in formulas.get("final", "") for x in
           ("defense_mitigation", "crit_bonus", "random_factor", "resistance_pct")))
report("realm_bonus_source",
       damage.get("realm_bonus", {}).get("source") == "battle_profile.passive_pct_bps")
report("defense_rule",
       "effective_def" in damage.get("defense", {}).get("formula", "")
       and damage.get("defense", {}).get("k") == 100
       and damage.get("defense", {}).get("min_pct") == 10)
report("crit_rule",
       damage.get("crit", {}).get("rate_pct") == 0
       and damage.get("crit", {}).get("damage_pct") == 50)
report("random_rule",
       damage.get("random", {}).get("min_pct") == 95
       and damage.get("random", {}).get("max_pct") == 105)
report("penetration_open",
       damage.get("penetration", {}).get("enabled") is True
       and damage.get("penetration", {}).get("default_pct") == 0
       and damage.get("penetration", {}).get("cap_pct") == 100)
res = damage.get("resistance", {})
report("resistance_open",
       res.get("enabled") is True and res.get("default_pct") == 0
       and res.get("cap_pct") == 90)
report("resistance_types",
       {"physical", "metal", "wood", "water", "fire", "earth"} <= set(res.get("types") or []))
report("rounding", damage.get("rounding") == "final_round_half_up")
bounds = damage.get("boundaries") or {}
report("boundaries_keys",
       all(k in bounds for k in ("rule", "pve", "arena", "battlefield", "siege")))
report("boundary_rule", "不许改模板属性" in bounds.get("rule", ""))
def has_ratio(node):
    if isinstance(node, dict):
        return any(k == "ratio" or has_ratio(v) for k, v in node.items())
    if isinstance(node, list):
        return any(has_ratio(x) for x in node)
    if isinstance(node, (int, float)) and not isinstance(node, bool):
        return abs(float(node) - 1.3) < 1e-9
    return False


report("no_second_ratio", not has_ratio(cfg),
       "effects.json 里出现了第二份 1.3 / ratio 数字；只许引用 config/battlefield.json")

design = read("docs/效果层设计.md")
report("design_doc", bool(design))
for key, needle in (("design_seven_kinds", "七类效果"),
                    ("design_interaction", "交互规则"),
                    ("design_four_stages", "伤害四段"),
                    ("design_pen_res", "穿透 / 抗性"),
                    ("design_boundary", "PVE / PVP 边界"),
                    ("design_pending", "待定值清单"),
                    ("design_q2", "Q2 的落点")):
    report(key, needle in design, needle)
config_design = read("docs/配置设计.md")
report("config_design_section",
       "## 13. effects.json" in config_design and "effect-table/2" in config_design)
ledger = read("docs/数值台账.md")
report("ledger_schema", "effect-table/2 · v3" in ledger)
report("ledger_pending_paths",
       "`interaction`" in ledger and "`damage`" in ledger and "`effects[8]`" in ledger)
schedule = read("docs/未覆盖排期.md")
ql = re.search(r"\| Q1 \| F1 效果层设计 \|[^\n]*", schedule)
report("schedule_q1_row",
       ql is not None and "cc_stun_1" in ql.group(0) and "穿透 / 抗性已打开" in ql.group(0))
queue = read("docs/任务队列.md")
report("queue_q1_row",
       re.search(r"\| 20 \| 2 \| Q1 \|[^\n]*\| (进行中|完成) \|", queue) is not None)
ci = read("tools/check/ci.sh")
report("ci_registered", re.search(r"^CHECKS=.*\bq1\b", ci, re.M) is not None)
allsh = read("tools/check/check-all.sh")
report("checkall_deps", re.search(r"q1\)\s*echo", allsh) is not None)
report("checkall_cards", re.search(r'ALL_CARDS="[^"]*\bq1\b', allsh, re.S) is not None)

# Q1 当时没有任何读取方；Q2 起效果层是唯一读取方。这里只钉「读取方没有扩散」：
# 允许 Q2 的效果层与配置加载，别处出现 effects.json / effect-table 就是第二套口径。
# Program.cs 是 Q2 的两个只读入口（--effect-list / --damage-preview），也只读。
allowed_readers = {"src/Battle/Config.cs", "src/Battle/Effects.cs",
                   "src/Battle/DamagePipeline.cs", "src/Battle/Encounter.cs",
                   "src/Battle/Program.cs"}
# D3c/D3f 把上面这几个文件按域拆成了目录：拆出去的仍是「同一处配置加载 / 效果层」，
# 按目录放行，别按文件名逐个补（每拆一次就要来改一遍白名单）。
allowed_prefixes = ("src/Battle/ConfigTables/", "src/Battle/Effects/", "src/Battle/Encounter/")
src_hits = []
for base, dirs, files in os.walk("src"):
    dirs[:] = [d for d in dirs if d not in ("bin", "obj", "Generated")]
    for f in files:
        if f.endswith(".cs"):
            # Windows 上 relpath 用反斜杠，下面的白名单是正斜杠，先归一再比。
            rel = os.path.relpath(os.path.join(base, f), ".").replace(os.sep, "/")
            t = read(rel)
            if (("effects.json" in t or "effect-table" in t) and rel not in allowed_readers
                    and not rel.startswith(allowed_prefixes)):
                src_hits.append(rel)
report("only_q2_reader", not src_hits, src_hits[:3])
PY

if grep -q '^PASS\|^FAIL' "$TMP/static.txt"; then
  while IFS= read -r line; do
    case "$line" in
      PASS\ *) ok "${line#PASS }" ;;
      FAIL\ *) bad "${line#FAIL }" ;;
    esac
  done < <(grep '^PASS\|^FAIL' "$TMP/static.txt")
else
  bad "静态段没有输出结果"
  grep -i 'error\|traceback' "$TMP/static.txt" | head -5 | detail
fi

finish "$TITLE"
