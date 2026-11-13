#!/usr/bin/env bash
# P2-3a 竞技场统一属性模板（方案 12 章三层战力；开发架构 11 节 P2-3、14 节第 57 行）。
#   bash tools/check/check-p2-3a.sh                 编译+静态+只读+坏表+假服
#   MMO_P2_3A_HAS_DB=1 MMO_P2_3A_CHAR=<角色> bash ...  加真库段（改强化等级后模板不变）
# 本卡口径：1v1 / 3v3 只按职业与分支取模板，装备、强化、结晶、等级一概不进模板；
# 换装不改变模板属性；副本与练习场仍按职业表算，老路径一字不回退。
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
CC="--config $ROOT/config/character.json"
HAS_DB="${MMO_P2_3A_HAS_DB:-0}"
TMP="$(mktemp -d)"
BPID=""
cleanup() { [ -n "$BPID" ] && stop_pid "$BPID"; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Battle/Battle.csproj tools/probe/BattleProbe/BattleProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1 || {
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail; }
done
[ "$BUILD_OK" = "1" ] && ok "战斗服与战斗探针编译通过（0 错误）" || true

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP/static.txt" <<'PY'
import json, os, re, subprocess, sys
root, out = sys.argv[1], sys.argv[2]
o = open(out, "w", encoding="utf-8")

def R(k, c, i=""):
    o.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")

def rd(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def js(p):
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}

arena = js(os.path.join(root, "config", "arena_match.json"))
classes = js(os.path.join(root, "config", "classes.json"))
branches = js(os.path.join(root, "config", "branches.json"))
skills = js(os.path.join(root, "config", "skills.json"))

R("arena_match_schema", arena.get("schema") == "arena-match-table/1", str(arena.get("schema")))
R("arena_match_version", isinstance(arena.get("version"), int) and arena["version"] >= 1)

modes = arena.get("modes", [])
shapes = sorted((m.get("team_size", 0), m.get("teams", 0)) for m in modes)
R("arena_two_modes", len(modes) == 2 and shapes == [(1, 2), (3, 2)], str(shapes))
R("arena_mode_players", sorted(m.get("players", m.get("team_size", 0) * m.get("teams", 0)) for m in modes) == [2, 6])

templates = arena.get("templates", [])
defaults = [t for t in templates if not t.get("branch_id")]
branched = [t for t in templates if t.get("branch_id")]
class_ids = [c.get("id") for c in classes.get("classes", [])]
branch_ids = [b.get("id") for b in branches.get("branches", [])]
R("arena_default_per_class", sorted(t["class_id"] for t in defaults) == sorted(class_ids), str(len(defaults)))
R("arena_branch_templates", sorted(t["branch_id"] for t in branched) == sorted(branch_ids), str(len(branched)))
R("arena_branch_belongs", all(
    next((b["class_id"] for b in branches.get("branches", []) if b["id"] == t["branch_id"]), "") == t["class_id"]
    for t in branched))

banned = ("level", "equip", "enhance", "crystal")
R("arena_no_equipment_keys", not any(
    any(w in k.lower() for w in banned) for t in templates for k in t))

# 模板值不能照抄职业表：照抄就分不出「走模板」和「走职业表」。
hp_per = classes.get("hp_per_vitality", 0)
w = next((c for c in classes.get("classes", []) if c["id"] == "warrior"), None)
wdef = next((t for t in defaults if t["class_id"] == "warrior"), None)
if w and wdef:
    class_atk = w["attributes"].get(w["primary"], 0) * w["attack_per_primary"] + w.get("equipment_attack", 0)
    class_hp = w["attributes"]["vitality"] * hp_per
    R("arena_template_differs", wdef["attack"] != class_atk and wdef["hp_max"] != class_hp,
      "attack=%s/%s hp=%s/%s" % (wdef["attack"], class_atk, wdef["hp_max"], class_hp))
else:
    R("arena_template_differs", False, "缺 warrior 默认模板")

table = rd(os.path.join(root, "src", "Battle", "ArenaMatchTable.cs"))
R("arena_table_load", "static ArenaMatchTable Load(string path)" in table)
R("arena_reject_equipment_keys", "RejectForbiddenKeys" in table and "模板里不能出现" in table)
R("arena_validate_against_classes", "ValidateAgainst(ClassTable" in table)

enc = rd(os.path.join(root, "src", "Battle", "Encounter.cs"))
R("member_uses_template", "Template?.Attack" in enc and "arena?.HpMax" in enc
  and "arena?.ManaMax" in enc)
R("damage_reads_attack_power", "member.AttackPower()" in enc and "member.Class.Attack()" not in enc)
R("heal_reads_heal_power", "member.HealPower()" in enc
  and "member.Class.Attributes.Spirit * member.Class.HealPerSpirit" not in enc)

srv = rd(os.path.join(root, "src", "Battle", "Server.cs"))
R("server_parses_arena_mode", 'QueryOrNull(query, "arena_mode")' in srv)
R("server_rejects_unknown_mode", "arena_mode_unknown" in srv)
R("server_rejects_party_size", "arena_party_size_mismatch" in srv)
R("server_rejects_with_dungeon", "arena_with_dungeon" in srv)
R("server_passes_template", "Join(charId, cls, arenaTemplate)" in srv)
R("server_logs_template", "arena_template=class=" in srv)

# 竞技场不发奖：模板与结算两条线不交叉。
settle = rd(os.path.join(root, "src", "Battle", "Settlement.cs")) + rd(os.path.join(root, "src", "Battle", "ExpSettlement.cs"))
R("arena_not_in_settlement", "Arena" not in settle)

# 竞技场路径不碰装备：这两个文件里不应出现背包/装备字样（模板表里的禁用键除外）。
R("arena_no_inventory_read", not re.search(r"inventory|equipment", enc + srv, re.I))

# 竞技场参数走 query，一个字没进协议：这条钉的是本卡语义，
# 而不是「这个提交里 proto 有没有动」——后面的卡改 proto 不该让这里假红。
bproto = rd(os.path.join(root, "proto", "mmo", "battle.proto"))
R("arena_not_in_proto", "arena_mode" not in bproto and "arena_template" not in bproto)

o.close()
PY
while IFS= read -r line; do
  case "$line" in
    "PASS "*) ok "${line#PASS }" ;;
    "FAIL "*) bad "${line#FAIL }" ;;
    "SKIP "*) printf '  ..   %s\n' "${line#SKIP }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 只读入口 --arena-table =="
at="$("$DOTNET" "$BD" --arena-table 2>&1)"; arc=$?
[ "$arc" = "0" ] && printf '%s' "$at" | grep -q '^arena_table_version=1 modes=2 templates=15' \
  && ok "只读入口打印 arena_table_version=1 modes=2 templates=15" || bad "只读入口不对（rc=${arc}）：$(printf '%s' "$at" | head -3)"
printf '%s' "$at" | grep -q 'arena_mode=duel .* players=2' && ok "1v1 是两队各 1 人（players=2）" || bad "1v1 人数不对"
printf '%s' "$at" | grep -q 'arena_mode=trio .* players=6' && ok "3v3 是两队各 3 人（players=6）" || bad "3v3 人数不对"
printf '%s' "$at" | grep -c '^arena_template=' | grep -qx '15' && ok "模板 15 条（5 职业默认 + 10 分支）" || bad "模板条数不对"
# 只读入口打出来的值必须就是配置里的值，不能是代码里另写的一套。
printf '%s' "$at" > "$TMP/arena-entry.txt"
if python3 - "$ROOT/config/arena_match.json" "$TMP/arena-entry.txt" <<'PY'
import json, sys
arena = json.load(open(sys.argv[1], encoding="utf-8"))
lines = [l.strip() for l in open(sys.argv[2], encoding="utf-8") if l.startswith("arena_template=")]
got = {}
for line in lines:
    kv = dict(p.split("=", 1) for p in line[len("arena_template="):].split(" "))
    got[(kv["class"], kv["branch"])] = (int(kv["attack"]), int(kv["hp_max"]),
                                        int(kv["mana_max"]), int(kv["heal_power"]), int(kv["threat"]))
want = {(t["class_id"], t["branch_id"]): (t["attack"], t["hp_max"], t["mana_max"],
                                          t["heal_power"], t["threat_stability"])
        for t in arena["templates"]}
sys.exit(0 if got == want else 1)
PY
then ok "只读入口逐条等于 arena_match.json（代码里没有第二套数值）"; else bad "只读入口与配置不一致"; fi

echo "== 4. 坏配置起不来 =="
mk_bad() {
  python3 - "$ROOT/config/arena_match.json" "$TMP/$1.json" "$1" <<'PY'
import json, sys
src, dst, mode = sys.argv[1:4]
d = json.load(open(src, encoding="utf-8"))
if mode == "version":
    d["version"] = 0
elif mode == "one_mode":
    d["modes"] = d["modes"][:1]
elif mode == "bad_shape":
    d["modes"] = [{"id": "solo", "name": "1v1", "team_size": 1, "teams": 1},
                  {"id": "trio", "name": "3v3", "team_size": 3, "teams": 2}]
elif mode == "team_size":
    d["modes"][0]["team_size"] = 0
elif mode == "hp":
    d["templates"][0]["hp_max"] = 0
elif mode == "equip":
    d["templates"][0]["equipment_attack"] = 120
elif mode == "no_default":
    d["templates"] = [t for t in d["templates"]
                      if not (t["class_id"] == "warrior" and not t["branch_id"])]
json.dump(d, open(dst, "w", encoding="utf-8"), ensure_ascii=False)
PY
}
for c in version one_mode bad_shape team_size hp equip no_default; do
  mk_bad "$c"
  out="$("$DOTNET" "$BD" --arena-table-path "$TMP/$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error='; then
    ok "坏配置 ${c}：起不来（config_error=）"
  else
    bad "坏配置 $c 没挡住（rc=${rc}）：$(printf '%s' "$out" | head -2)"
  fi
done
out="$("$DOTNET" "$BD" --arena-table-path "$TMP/no-such.json" 2>&1)"; rc=$?
[ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error=' \
  && ok "表不存在：起不来" || bad "表不存在也起来了（rc=${rc}）"

echo "== 5. 假服：模板与职业表两条路径 =="
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
d = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
d["listen"] = "127.0.0.1:0"
d["settlement_dir"] = os.path.join(tmp, "settlements")
d["character_dir"] = os.path.join(tmp, "characters")
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json")):
    d[k] = os.path.join(root, "config", f)
json.dump(d, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
"$DOTNET" "$BD" --config "$TMP/battle.json" >"$TMP/battle.log" 2>&1 &
BPID=$!
wait_ready "$TMP/battle.log" '^ready name=Battle' "战斗服" || true
# listen 行在 ready 之后才打，得单独等它，不然刚 ready 就 grep 会拿到空端口。
for _ in $(seq 1 120); do grep -q '^listen=http://' "$TMP/battle.log" && break; sleep 0.1; done
BURL="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$TMP/battle.log" | tail -1 | sed 's/^listen=//')"
[ -n "$BURL" ] && ok "战斗服就绪（随机端口）" || bad "没等到监听地址"
WURL="ws${BURL#http}"

read -r D_CLASS D_DEF D_BR < <(python3 - "$ROOT/config" <<'PY'
import json, os, sys
c = sys.argv[1]
classes = json.load(open(os.path.join(c, "classes.json"), encoding="utf-8"))
skills = json.load(open(os.path.join(c, "skills.json"), encoding="utf-8"))
arena = json.load(open(os.path.join(c, "arena_match.json"), encoding="utf-8"))
w = next(x for x in classes["classes"] if x["id"] == "warrior")
atk = w["attributes"].get(w["primary"], 0) * w["attack_per_primary"] + w.get("equipment_attack", 0)
mult = next(s for s in skills["skills"] if s["id"] == 1002)["damage_multiplier"]

def tpl(branch):
    return next(x for x in arena["templates"] if x["class_id"] == "warrior" and x["branch_id"] == branch)["attack"]

print(round(atk * mult), round(tpl("") * mult), round(tpl("pojun") * mult))
PY
)
dmg() { "$DOTNET" "$PROBE" --url "$1" --case damage --boss 9001 --ranged 1002 2>&1 | grep -oE 'damage=[0-9]+' | head -1 | cut -d= -f2; }

D1="$(dmg "$WURL/battle?class=warrior")"
[ "$D1" = "$D_CLASS" ] && ok "副本：战士 1002 伤害 ${D1}（职业表主属性 × 倍率）" || bad "副本伤害 ${D1}，应为 $D_CLASS"
grep -q 'arena_template=' "$TMP/battle.log" && bad "没进竞技场也打了模板行" || ok "副本路径不打模板行（老路径不碰模板）"

D2="$(dmg "$WURL/battle?class=warrior&arena_mode=duel&party_size=2")"
[ "$D2" = "$D_DEF" ] && ok "竞技场 1v1 默认分支：伤害 ${D2}（模板值 × 倍率）" || bad "竞技场默认模板伤害 ${D2}，应为 $D_DEF"

D3="$(dmg "$WURL/battle?class=warrior&branch=pojun&arena_mode=duel&party_size=2")"
[ "$D3" = "$D_BR" ] && ok "竞技场 1v1 镇岳分支：伤害 ${D3}（只有分支不同，数值随模板）" || bad "竞技场分支伤害 ${D3}，应为 $D_BR"

[ "$D1" != "$D2" ] && [ "$D2" != "$D3" ] && [ "$D1" != "$D3" ] \
  && ok "副本 / 默认模板 / 分支模板三个数互不相同（确实换了属性来源）" \
  || bad "三个数有重复：$D1 $D2 $D3"
grep -q 'stats_source=arena_template' "$TMP/battle.log" && ok "进场日志带 stats_source=arena_template" || bad "日志没标属性来源"
grep -q 'arena_template=class=warrior branch=pojun' "$TMP/battle.log" && ok "日志打出所用模板（class=warrior branch=pojun）" || bad "日志没打模板值"

reject_line() {
  curl -sS --noproxy '*' -m 5 -i -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
    -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' \
    "$1" 2>&1 | head -1 | grep -oE '^HTTP/[0-9.]+ [0-9]{3}' | awk '{print $2}'
}
C="$(reject_line "$BURL/battle?arena_mode=duel9&party_size=2")"
[ "$C" = "400" ] && grep -q 'join_reject=arena_mode_unknown' "$TMP/battle.log" \
  && ok "未知模式：400 + arena_mode_unknown" || bad "未知模式码不对（${C}）"
C="$(reject_line "$BURL/battle?arena_mode=duel&party_size=3")"
[ "$C" = "400" ] && grep -q 'join_reject=arena_party_size_mismatch' "$TMP/battle.log" \
  && ok "人数与模式不符：400 + arena_party_size_mismatch" || bad "人数不符码不对（${C}）"
C="$(reject_line "$BURL/battle?arena_mode=duel&party_size=2&dungeon=abyss")"
[ "$C" = "400" ] && grep -q 'join_reject=arena_with_dungeon' "$TMP/battle.log" \
  && ok "竞技场与副本同时给：400 + arena_with_dungeon" || bad "混用码不对（${C}）"

# 同场混打：第一个人打竞技场并挂住连接，第二个人不带 arena_mode 进来。
TS="$(date +%s | tail -c 6)"
PK="p3a-$TS"
"$DOTNET" "$PROBE" --url "$WURL/battle?class=warrior&party=$PK&party_size=2&arena_mode=duel" \
  --case p12a_join --expect open --hold-ms 4000 >"$TMP/hold.log" 2>&1 &
HOLD=$!
for _ in $(seq 1 40); do grep -q 'joined=ok' "$TMP/hold.log" && break; sleep 0.1; done
r2="$("$DOTNET" "$PROBE" --url "$WURL/battle?class=warrior&party=$PK&party_size=2" --case p12a_join --expect reject 2>&1)"
printf '%s' "$r2" | grep -q 'reason=arena_mode_mismatch' \
  && ok "同场一半打竞技场、一半打副本：被拒 arena_mode_mismatch" \
  || bad "同场混打没被拒：$(printf '%s' "$r2" | head -2)"
wait "$HOLD" 2>/dev/null || true

echo "== 6. 真库：改强化等级，模板不变 =="
if [ "$HAS_DB" != "1" ]; then
  printf '  ..   跳过真库段（MMO_P2_3A_HAS_DB 未开）\n'
elif [ -z "${MMO_GAMEDB:-}" ] || [ -z "${MMO_P2_3A_CHAR:-}" ]; then
  printf '  ..   跳过真库段（缺 MMO_GAMEDB 或 MMO_P2_3A_CHAR）\n'
else
  CHAR="$MMO_P2_3A_CHAR"; SLOT="${MMO_P2_3A_SLOT:-1}"
  elvl() { "$DOTNET" "$CD" --enhance-level "$CHAR" "$SLOT" $CC 2>&1 | grep -oE 'level=[0-9]+' | head -1 | cut -d= -f2; }
  ew() { "$DOTNET" "$CD" --raw-enhance-write "$CHAR" "$SLOT" "$1" $CC >"$TMP/raw.log" 2>&1; }
  L0="$(elvl)"
  if [ -z "$L0" ]; then bad "读不到角色 $CHAR 槽 $SLOT 的强化等级"; else
    # 先把库里的强化等级写成 3，再写成 7：库这一列确实变了，场上数值不该跟着动。
    ew 3
    grep -q '^raw_enhance=written' "$TMP/raw.log" && [ "$(elvl)" = "3" ] \
      && ok "库里强化等级写成 3（读回一致，角色 $CHAR 槽 $SLOT）" \
      || bad "强化等级没写成 3：$(head -2 "$TMP/raw.log")"
    D4="$(dmg "$WURL/battle?class=warrior&branch=pojun&arena_mode=duel&party_size=2")"
    ew 7
    [ "$(elvl)" = "7" ] && ok "库里强化等级 3 → 7（读回一致）" || bad "强化等级没改成 7"
    D5="$(dmg "$WURL/battle?class=warrior&branch=pojun&arena_mode=duel&party_size=2")"
    [ "$D4" = "$D5" ] && [ "$D5" = "$D3" ] \
      && ok "换装（强化 3→7）后竞技场伤害仍是 $D5，等于模板算出来的 $D3" \
      || bad "换装后竞技场伤害变了：$D4 / $D5 / 模板 $D3"
    ew "$L0"
    [ "$(elvl)" = "$L0" ] && ok "强化等级已还原（${L0}）" || bad "强化等级没还原"
  fi
fi

finish "P2-3a（战斗服）"
