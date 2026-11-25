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
# D3c 之后 Encounter / Server 已拆进子目录：走 src_agg 读全文，断言文本一个字不改。
AGG_BATTLE_ENCOUNTER="$(src_agg src/Battle/Encounter.cs)"; export AGG_BATTLE_ENCOUNTER
AGG_BATTLE_SERVER="$(src_agg src/Battle/Server.cs)"; export AGG_BATTLE_SERVER
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
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

enc = rd(os.environ["AGG_BATTLE_ENCOUNTER"])
R("member_uses_template", "Template?.Attack" in enc and "arena?.HpMax" in enc
  and "arena?.ManaMax" in enc)
R("damage_reads_attack_power", "member.AttackPower()" in enc and "member.Class.Attack()" not in enc)
R("heal_reads_heal_power", "member.HealPower()" in enc
  and "member.Class.Attributes.Spirit * member.Class.HealPerSpirit" not in enc)

srv = rd(os.environ["AGG_BATTLE_SERVER"])
R("server_parses_arena_mode", 'QueryOrNull(query, "arena_mode")' in srv)
R("server_rejects_unknown_mode", "arena_mode_unknown" in srv)
R("server_rejects_party_size", "arena_party_size_mismatch" in srv)
R("server_rejects_with_dungeon", "arena_with_dungeon" in srv)
R("server_passes_template", "Join(charId, cls, arenaTemplate)" in srv)
R("server_logs_template", "arena_template=class=" in srv)

# 竞技场不发奖：模板与结算两条线不交叉。
settle = rd(os.path.join(root, "src", "Battle", "Settlement.cs")) + rd(os.path.join(root, "src", "Battle", "ExpSettlement.cs"))
R("arena_not_in_settlement", "Arena" not in settle)

# 竞技场路径不碰装备：模板表与竞技场表里不出现背包 / 装备字样。
# D3c 拆分后 Encounter 聚合里含副本掉落（inventory），「整份没有这几个字」已经不是
# 竞技场的口径——这里钉的是竞技场自己的两份表，断言文本不变、判据更准。
# 表里那个 banned 数组是「拒绝装备键」的守门代码，不是读背包；
# 这里钉的是竞技场表不引用任何背包 / 装备仓储。
R("arena_no_inventory_read",
  not re.search(r"inventory|EquipRepository|BagRepository", table, re.I))

# 竞技场参数走 query，一个字没进协议：这条钉的是本卡语义，
# 而不是「这个提交里 proto 有没有动」——后面的卡改 proto 不该让这里假红。
bproto = rd(os.path.join(root, "proto", "mmo", "battle.proto"))
R("arena_not_in_proto", "arena_mode" not in bproto and "arena_template" not in bproto)

# ---- B11：竞技场赛季段位（并进同域脚本，不新开 check-*.sh）----
rank = js(os.path.join(root, "config", "arena_rank.json"))
items = js(os.path.join(root, "config", "items.json"))
appearance_kinds = {"outfit", "weapon_skin", "realm_skin", "flight_trail",
                    "mount_skin", "appearance_fragment"}
tiers = rank.get("tiers", [])
by_id = {i.get("id"): i for i in items.get("items", [])}
R("arena_rank_schema", rank.get("schema") == "arena-rank-table/1", str(rank.get("schema")))
R("arena_rank_pending", rank.get("pending") is True, str(rank.get("pending")))
R("arena_rank_win_points",
  isinstance(rank.get("win_points"), int) and rank["win_points"] > 0, str(rank.get("win_points")))
R("arena_rank_top_n",
  isinstance(rank.get("top_n"), int) and 1 <= rank["top_n"] <= 100, str(rank.get("top_n")))
R("arena_rank_two_tiers", len(tiers) >= 2, str(len(tiers)))
R("arena_rank_first_zero", bool(tiers) and tiers[0].get("min_score") == 0, str(tiers[:1]))
scores = [t.get("min_score") for t in tiers]
R("arena_rank_strict_increase",
  all(isinstance(x, int) for x in scores) and scores == sorted(set(scores)), str(scores))
R("arena_rank_appearance_kind", all(
    by_id.get(t.get("appearance_item_id"), {}).get("kind") in appearance_kinds for t in tiers),
  str([t.get("appearance_item_id") for t in tiers]))
R("arena_rank_appearance_unique",
  len({t.get("appearance_item_id") for t in tiers}) == len(tiers))

rank_table = rd(os.path.join(root, "src", "Character", "ArenaRankTable.cs"))
R("arena_rank_table_load", "static ArenaRankTable Load(string path, ItemTable items)" in rank_table)
R("arena_rank_table_pending", "pending 必须是 true" in rank_table)
battle_rank = rd(os.path.join(root, "src", "Battle", "ArenaRank.cs"))
R("arena_rank_report_win", "void ReportWin(" in battle_rank and "ReportArenaResult(" in battle_rank)
# 只在竞技场模式下、打掉 Boss（通关）才上报；散场不记分也不扣分。
R("arena_rank_only_on_clear",
  "ArenaModeId.Length > 0 && _bossHp <= 0" in enc and "_arenaRank.ReportWin(" in enc)
# 段位不进模板：模板表里不许出现分数 / 段位字段（P2-3a 一字不变）。
R("arena_rank_not_in_template", not any(
    ("rank" in k.lower() or "score" in k.lower() or "tier" in k.lower())
    for t in templates for k in t))
gw = rd(os.path.join(root, "src", "Gateway", "ArenaRankApi.cs"))
R("arena_rank_gateway", all(p in gw for p in
  ("/arena/rank", "/arena/leaderboard", "/arena/appearance/claim")))

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
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json"),
             # B15 之后副本 boss_id 要与怪物表对账：临时配置不指过去，战斗服起不来。
             ("monster_table", "monsters.json")):
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
      && ok "库里强化等级写成 3（读回一致，角色 $CHAR 槽 ${SLOT}）" \
      || bad "强化等级没写成 3：$(head -2 "$TMP/raw.log")"
    D4="$(dmg "$WURL/battle?class=warrior&branch=pojun&arena_mode=duel&party_size=2")"
    ew 7
    [ "$(elvl)" = "7" ] && ok "库里强化等级 3 → 7（读回一致）" || bad "强化等级没改成 7"
    D5="$(dmg "$WURL/battle?class=warrior&branch=pojun&arena_mode=duel&party_size=2")"
    [ "$D4" = "$D5" ] && [ "$D5" = "$D3" ] \
      && ok "换装（强化 3→7）后竞技场伤害仍是 ${D5}，等于模板算出来的 $D3" \
      || bad "换装后竞技场伤害变了：$D4 / $D5 / 模板 $D3"
    ew "$L0"
    [ "$(elvl)" = "$L0" ] && ok "强化等级已还原（${L0}）" || bad "强化等级没还原"
  fi
fi

echo "== 7. 段位表：只读入口与坏配置（B11） =="
rr="$("$DOTNET" "$CD" --arena-rank-config 2>&1)"; rrc=$?
[ "$rrc" = "0" ] && ok "只读入口 --arena-rank-config 正常" \
  || bad "只读入口不对（rc=${rrc}）：$(printf '%s' "$rr" | head -3)"
printf '%s' "$rr" > "$TMP/rank-entry.txt"
if python3 - "$ROOT/config/arena_rank.json" "$TMP/rank-entry.txt" <<'RANKPY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
lines = [l.strip() for l in open(sys.argv[2], encoding="utf-8")]
head = next((l for l in lines if l.startswith("arena_rank_config=")), "")
kv = dict(p.split("=", 1) for p in head[len("arena_rank_config="):].split(" ")[1:])
rows = {}
for line in lines:
    if not line.startswith("arena_rank_row="):
        continue
    row = dict(p.split("=", 1) for p in line[len("arena_rank_row="):].split(" "))
    rows[row["key"]] = row
want = {t["key"]: t for t in cfg["tiers"]}
good = (kv.get("win_points") == str(cfg["win_points"])
        and kv.get("top_n") == str(cfg["top_n"])
        and kv.get("tiers") == str(len(cfg["tiers"]))
        and set(rows) == set(want)
        and all(rows[k]["min_score"] == str(want[k]["min_score"])
                and rows[k]["item"] == want[k]["appearance_item_id"] for k in want))
sys.exit(0 if good else 1)
RANKPY
then ok "只读入口逐条等于 arena_rank.json（分数 / 阈值 / 外观编号现取）" \
  || bad "段位只读入口与配置不一致"
fi

mk_bad_rank() {
  python3 - "$ROOT/config/arena_rank.json" "$TMP/rank-$1.json" "$1" <<'BADPY'
import json, sys
src, dst, mode = sys.argv[1:4]
d = json.load(open(src, encoding="utf-8"))
if mode == "win_points":
    d["win_points"] = 0
elif mode == "top_n":
    d["top_n"] = 0
elif mode == "first":
    d["tiers"][0]["min_score"] = 10
elif mode == "order":
    d["tiers"][2]["min_score"] = d["tiers"][1]["min_score"]
elif mode == "item":
    # 拿一件真装备当段位外观：不是外观类，必须起不来。
    d["tiers"][2]["appearance_item_id"] = "sword_trade"
elif mode == "pending":
    d.pop("pending", None)
elif mode == "unknown":
    d["drop_bonus"] = 1
json.dump(d, open(dst, "w", encoding="utf-8"), ensure_ascii=False)
BADPY
}
for c in win_points top_n first order item pending unknown; do
  mk_bad_rank "$c"
  out="$("$DOTNET" "$CD" --arena-rank-table-path "$TMP/rank-$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error='; then
    ok "段位坏配置 ${c}：起不来（config_error=）"
  else
    bad "段位坏配置 $c 没挡住（rc=${rc}）：$(printf '%s' "$out" | head -2)"
  fi
done

echo "== 8. 真库：段位记分、幂等与专属外观（B11） =="
if [ "$HAS_DB" != "1" ]; then
  printf '  ..   跳过真库段（MMO_P2_3A_HAS_DB 未开）\n'
elif [ -z "${MMO_P2_3A_CHAR:-}" ] || [ -z "${MMO_P2_3A_SEASON:-}" ] \
  || { [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; }; then
  printf '  ..   跳过真库段（缺 MMO_P2_3A_CHAR / MMO_P2_3A_SEASON 或库环境）\n'
else
  CHAR="$MMO_P2_3A_CHAR"; SEASON="$MMO_P2_3A_SEASON"
  rstate() { "$DOTNET" "$CD" --arena-rank-state "$CHAR" "$SEASON" $CC 2>&1; }
  rscore() { rstate | grep -oE 'score=[0-9]+' | head -1 | cut -d= -f2; }
  WIN="$(python3 -c "import json;print(json.load(open('$ROOT/config/arena_rank.json'))['win_points'])")"

  S0="$(rscore)"
  if [ -z "$S0" ]; then bad "读不到角色 $CHAR 在季 $SEASON 的段位分"; else
    ok "读到基线：角色 $CHAR 季 $SEASON 当前 $S0 分"

    T="p3a-rank-$(date +%s)-$RANDOM"
    A="$("$DOTNET" "$CD" --arena-rank-award "$CHAR" duel "$SEASON" "$T" $CC 2>&1)"
    S1="$(rscore)"
    printf '%s' "$A" | grep -q '^arena_rank_award=ok' \
      && [ "$S1" = "$((S0 + WIN))" ] \
      && ok "一场胜仗 +${WIN} 分（$S0 → $S1，分数只从配置读）" \
      || bad "记分不对：$S0 → $S1（应 +${WIN}）：$(printf '%s' "$A" | head -2)"

    A2="$("$DOTNET" "$CD" --arena-rank-award "$CHAR" duel "$SEASON" "$T" $CC 2>&1)"
    S2="$(rscore)"
    printf '%s' "$A2" | grep -q 'duplicate=true' && [ "$S2" = "$S1" ] \
      && ok "同一事务号重放：duplicate 且分数不变（${S2}）" \
      || bad "重放没挡住：$(printf '%s' "$A2" | head -2)"

    # 分数落在哪一档、最高档门槛多少都现取，不在这里写死。
    ROW="$(python3 - "$ROOT/config/arena_rank.json" "$S2" <<'RANKPY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
score = int(sys.argv[2])
tiers = sorted(cfg["tiers"], key=lambda t: t["min_score"])
cur = tiers[0]
for t in tiers:
    if t["min_score"] <= score:
        cur = t
print(tiers[-1]["key"], tiers[-1]["min_score"], cur["key"], cur["appearance_item_id"])
RANKPY
)"
    TOP_KEY="$(printf '%s' "$ROW" | awk '{print $1}')"
    TOP_MIN="$(printf '%s' "$ROW" | awk '{print $2}')"
    CUR_KEY="$(printf '%s' "$ROW" | awk '{print $3}')"
    CUR_ITEM="$(printf '%s' "$ROW" | awk '{print $4}')"

    if [ "$S2" -lt "$TOP_MIN" ]; then
      C0="$("$DOTNET" "$CD" --arena-rank-claim "$CHAR" "$SEASON" "$TOP_KEY" "p3a-claim0-$(date +%s)-$RANDOM" $CC 2>&1)"
      printf '%s' "$C0" | grep -q 'reason=tier_not_reached' \
        && ok "分数 $S2 领最高档 $TOP_KEY（门槛 $TOP_MIN）：被拒 tier_not_reached" \
        || bad "未达档也领到了：$(printf '%s' "$C0" | head -2)"
    fi

    C1="$("$DOTNET" "$CD" --arena-rank-claim "$CHAR" "$SEASON" "$CUR_KEY" "p3a-claim1-$(date +%s)-$RANDOM" $CC 2>&1)"
    printf '%s' "$C1" | grep -q '^arena_rank_claim=ok' \
      && printf '%s' "$C1" | grep -q "item=$CUR_ITEM" \
      && ok "已达档 $CUR_KEY：领到专属外观 $CUR_ITEM（到手即绑）" \
      || bad "领当前档外观不对：$(printf '%s' "$C1" | head -2)"

    C2="$("$DOTNET" "$CD" --arena-rank-claim "$CHAR" "$SEASON" "$CUR_KEY" "p3a-claim2-$(date +%s)-$RANDOM" $CC 2>&1)"
    printf '%s' "$C2" | grep -q 'reason=already_claimed' \
      && ok "同一档再领：already_claimed（一季一档只发一次）" \
      || bad "重复领没挡住：$(printf '%s' "$C2" | head -2)"

    rstate | grep -q "arena_rank_tier=key=$CUR_KEY reached=true claimed=true" \
      && ok "只读口回报该档 reached=true claimed=true" \
      || bad "只读口没回领过的事实"

    TOP="$("$DOTNET" "$CD" --arena-rank-top "$SEASON" 3 $CC 2>&1)"
    printf '%s' "$TOP" | grep -q '^arena_rank_top=ok' && printf '%s' "$TOP" | grep -q "rank=1 " \
      && ok "排行前 3 名：按本季分数从高到低（只读，不写库）" \
      || bad "排行不对：$(printf '%s' "$TOP" | head -3)"
  fi
fi

finish "P2-3a（战斗服）"
