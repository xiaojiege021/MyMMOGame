#!/usr/bin/env bash
# P2-1b 宗门秘境仍是 1–4 人（方案 10 章表/10.3/11.1；技术架构 49、66；开发架构 4.4、11、14 节 54 行）。
#   bash tools/check/check-p2-1b.sh                编译+静态+只读+坏表（不连库不连服）
#   MMO_P2_1B_HAS_DB=1 [MMO_P2_1B_GATEWAY=… _TOKEN=… _CHARACTER=…] bash tools/check/check-p2-1b.sh  加真服真库
# 本卡只改 config/dungeons.json 与本脚本；不改判定/proto/生成物/客户端/社交服。
# 坏表证据来自脚本内配置契约校验器（本卡语义 + 两端 DungeonTable.Load 等价规则），不新增只读入口。
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
SD="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
PD="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
HAS_DB="${MMO_P2_1B_HAS_DB:-0}"
GATEWAY="${MMO_P2_1B_GATEWAY:-http://127.0.0.1:61000}"
TOKEN="${MMO_P2_1B_TOKEN:-}"; CHARACTER="${MMO_P2_1B_CHARACTER:-0}"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_P2_1B_SKIP_BUILD:-0}" = "1" ]; then printf '  ..   跳过编译\n'; else
  BUILD_OK=1
  for proj in src/Character/Character.csproj src/Battle/Battle.csproj src/Gateway/Gateway.csproj src/Social/Social.csproj tools/probe/BattleProbe/BattleProbe.csproj; do
    n="$(basename "$proj" .csproj)"
    "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1 || {
      BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail; }
  done
  [ "$BUILD_OK" = "1" ] && ok "角色服/战斗服/网关/社交服/探针编译通过" || true
fi

echo "== 2. 静态断言 =="
python3 - "$TMP/static.txt" <<'PY'
import glob, json, os, re, sys
o = open(sys.argv[1], "w", encoding="utf-8")
def R(k, c, i=""): o.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")
def rd(p): return open(p, encoding="utf-8").read() if os.path.exists(p) else ""
cfg = json.loads(rd("config/dungeons.json")); sect = json.loads(rd("config/sect.json"))
rows = cfg.get("dungeons", []); byid = {r.get("id"): r for r in rows}
# 只钉「本卡确实升过版本、确实在三条后追加了 sect」，不写死 version=5 / 正好四条：
# 后面的卡会继续往后追加条目并递增版本（P2-2a 加了龙巢），写死就假红。
VER = cfg.get("version")
R("schema_version", cfg.get("schema") == "dungeon-table/1" and isinstance(VER, int) and VER >= 5,
  "version 已按约定升过（>=5）")
R("four_ids", [r.get("id") for r in rows][:4] == ["main", "abyss", "lair", "sect"], "前三条后追加第四条 sect")
R("ids_unique", len(byid) == len(rows) and len(rows) >= 4, "编号不重复、至少四条")
R("main_unchanged", byid.get("main") == {"id":"main","name":"主线","max_players":1,"charges_fatigue":False,"takes_weekly_share":False}, "main 一字未改")
R("abyss_unchanged", byid.get("abyss") == {"id":"abyss","name":"深渊","max_players":4,"charges_fatigue":True,"takes_weekly_share":False,"fatigue_per_player":1500}, "abyss 一字未改")
R("lair_unchanged", byid.get("lair") == {"id":"lair","name":"巢穴","max_players":4,"charges_fatigue":False,"takes_weekly_share":True}, "lair 一字未改")
R("sect_entry", byid.get("sect") == {"id":"sect","name":"宗门秘境","max_players":4,"charges_fatigue":False,"takes_weekly_share":False}, "sect 宗门秘境 1–4 人、不扣疲劳不占份额")
R("global_caps", all(cfg.get(k) == v for k, v in {"fatigue_daily_cap":6000,"hot_spring_recovery":1500,"weekly_share_cap":12,"monster_hp_scale_per_player":1.0,"monster_damage_scale_per_player":1.0}.items()), "6000/1500/12 与缩放系数一字未改")
note = cfg.get("note", "")
R("note_playbook", all(s in note for s in ["宗门秘境","高级本","1–4 人","个人活跃","宗门贡献","科技材料","稀有外观","按人数倍增","组织荣誉"]), "note 写明方案 10 章表格玩法")
R("note_pending", all(s in note for s in ["个人活跃计数","不在本卡","待定","不写死"]), "个人活跃计数与产出标待定不写死")
R("note_decoupled", all(s in note for s in ["member_cap","不参与进本人数判定","名册","不是进本人数"]), "note 写明名册上限不参与进本判定")
R("note_no_link", not any(s in note for s in ["名册上限联动进本人数","名册上限即进本人数","member_cap 决定进本人数","200 人副本"]), "note 无反口径")
R("note_no_member_gate", "不新增宗门成员资格校验" in note, "方案 10.3 准入只看等级：不新增成员资格校验")
caps = [l["member_cap"] for l in sect["levels"]]
R("sect_levels_caps", caps == [10, 30, 60, 100, 200], "sect.json 五档名册上限一字未改")
R("two_calibers", caps[-1] == 200 and byid["sect"]["max_players"] == 4 and caps[-1] != 4, "member_cap=200 与 sect.max_players=4 两份口径")
R("no_200_as_limit", all(r.get("max_players") != 200 for r in rows), "dungeons.json 无 200 作为进本上限")
svc = rd("src/Character/CharacterGrpcService.cs"); bsrv = rd("src/Battle/Server.cs")
ctbl = rd("src/Character/DungeonTable.cs"); btbl = rd("src/Battle/DungeonTable.cs")
judge = svc + bsrv + ctbl + btbl
R("no_200_in_code", not re.search(r"(?:MaxPlayers|max_players|limit|partySize|party_size)[^\n]{0,24}\b200\b|\b200\b[^\n]{0,24}(?:MaxPlayers|max_players|limit|partySize|party_size)", judge), "判定/副本表源码里不出现 200 作为进本上限")
social = "\n".join(rd(p) for p in sorted(glob.glob("src/Social/*.cs")))
R("social_no_dungeons", not re.search(r"dungeons\.json|max_players|party_size|DungeonTable", social, re.I), "src/Social/* 不读副本表、不下发人数")
R("tables_no_sect_json", all(x not in ctbl + btbl for x in ["sect.json", "member_cap"]), "两端 DungeonTable.cs 不读 sect.json/member_cap")
R("same_load", "DungeonTable.Load(config.DungeonTable)" in rd("src/Character/CharacterProcess.cs") and "DungeonTable.Load(dungeonPath)" in rd("src/Battle/Config.cs") and "dungeons=" in rd("src/Character/CharacterProcess.cs"), "两服读同一份 config/dungeons.json")
R("no_new_readonly", "--sect-dungeon" not in rd("src/Character/Program.cs") and "--dungeon-sect" not in rd("src/Character/Program.cs"), "未为宗门秘境新增只读入口")
R("char_reject", "partySize > dungeon.MaxPlayers" in svc and '"party_too_large"' in svc, "角色服 party_too_large 复用")
R("battle_reject", '"party_full"' in bsrv and "party.Connections.Count >= dungeon.MaxPlayers" in bsrv, "战斗服 party_full 复用")
api = rd("src/Gateway/CharacterApi.cs")
R("gateway_route", 'MapPost("/dungeon/enter"' in api and "DungeonId = body.DungeonId" in api and "TryAuthorize" in api, "网关 /dungeon/enter 已能传 dungeon=sect")
R("no_proto_change", not re.search(r"\bsect\b|宗门秘境", rd("proto/mmo/character.proto")), "不改 proto/生成物/客户端 Net 层")
u3 = rd("tools/check/check-u3.sh")
R("u3_narrowed", "shown = [r for r in rows" in u3 and "len(rows) == 3" not in u3 and "for absent in" in u3, "check-u3.sh 写死 len(rows)==3 已按语义收窄")
R("no_bareword", not any(w in judge + api for w in ["Player" + "Prefs", "Resources" + "." + "Load"]), "新增源码/注释不含 U1 裸词")
o.close()
PY
while read -r tag key rest; do
  case "$tag" in PASS) ok "$key" ;; FAIL) bad "$key${rest:+ $rest}" ;; esac
done < "$TMP/static.txt"
grep -qE '^(PASS|FAIL) ' "$TMP/static.txt" || bad "静态断言没跑起来"

echo "== 3. 只读入口（与启动同形）=="
CONF="$("$DOTNET" "$SD" --sect-config 2>&1)"
printf '%s' "$CONF" | grep -q '^sect_config_state=ok' && printf '%s' "$CONF" | grep -q 'levels=1:10,2:30,3:60,4:100,5:200' \
  && ok "--sect-config 打印名册五档上限（5 级 200 人）" || bad "--sect-config 不对：$CONF"
DUMP="$(python3 -c 'import json;d=json.load(open("config/dungeons.json",encoding="utf-8"));print("version=%d ids=%s"%(d["version"],",".join("%s:%d"%(r["id"],r["max_players"]) for r in d["dungeons"])))')"
# 只钉本卡追加的 sect 与前三条的进本人数；版本号与总条数从配置现取，不写死。
printf '%s' "$DUMP" | grep -q 'ids=main:1,abyss:4,lair:4,sect:4' \
  && ok "只读打印四条进本人数：main:1 abyss:4 lair:4 sect:4（$DUMP）" || bad "副本表四条不对：$DUMP"
CAP5="$(python3 -c 'import json;print(json.load(open("config/sect.json",encoding="utf-8"))["levels"][-1]["member_cap"])')"
SMAX="$(python3 -c 'import json;print([x["max_players"] for x in json.load(open("config/dungeons.json",encoding="utf-8"))["dungeons"] if x["id"]=="sect"][0])')"
[ "$CAP5" = "200" ] && [ "$SMAX" = "4" ] && [ "$CAP5" != "$SMAX" ] \
  && ok "名册 200 与进本 4 是两份不同口径，进本判定不读名册" || bad "解耦口径不对：cap5=$CAP5 sect=$SMAX"

echo "== 4. 坏表：本卡配置契约校验器起不来 =="
cat > "$TMP/validate.py" <<'PY'
import json, sys
def fail(w): print("config_error=" + w); raise SystemExit(2)
try: d = json.load(open(sys.argv[1], encoding="utf-8"))
except Exception as ex: fail("不是合法 JSON：" + str(ex))
if d.get("schema") != "dungeon-table/1": fail("schema 不对")
if not isinstance(d.get("version"), int) or d["version"] < 5: fail("version 必须按约定升过 5")
for k, v in {"fatigue_daily_cap":6000,"hot_spring_recovery":1500,"weekly_share_cap":12,"monster_hp_scale_per_player":1.0,"monster_damage_scale_per_player":1.0}.items():
    if d.get(k) != v: fail(k + " 一字未改")
rows = d.get("dungeons")
if not isinstance(rows, list): fail("缺 dungeons 段")
ids = [r.get("id") for r in rows]
if len(set(ids)) != len(ids): fail("副本 id 重复")
if ids[:4] != ["main", "abyss", "lair", "sect"]: fail("副本条目必须以 main/abyss/lair/sect 四条开头（后面的卡可在其后追加）")
byid = {r["id"]: r for r in rows}
base = {"main":{"id":"main","name":"主线","max_players":1,"charges_fatigue":False,"takes_weekly_share":False},
        "abyss":{"id":"abyss","name":"深渊","max_players":4,"charges_fatigue":True,"takes_weekly_share":False,"fatigue_per_player":1500},
        "lair":{"id":"lair","name":"巢穴","max_players":4,"charges_fatigue":False,"takes_weekly_share":True}}
for k, w in base.items():
    if byid[k] != w: fail("旧副本 " + k + " 口径被改")
for k, v in {"id":"sect","name":"宗门秘境","max_players":4,"charges_fatigue":False,"takes_weekly_share":False}.items():
    if byid["sect"].get(k) != v: fail("sect." + k + " 不对或缺失")
for r in rows:  # 与两端 DungeonTable.Load 的逐条硬校验同形
    mp = r.get("max_players")
    if not isinstance(mp, int) or mp < 1 or mp > 100: fail("max_players 越界")
    per = r.get("fatigue_per_player", 0) or 0
    if r.get("charges_fatigue") and (not isinstance(per, int) or per < 1): fail("扣疲劳但 fatigue_per_player 非正")
    if per * mp > d["fatigue_daily_cap"]: fail("满员一次疲劳超过日上限")
    if r.get("takes_weekly_share") and mp > d["weekly_share_cap"]: fail("满员一次份额超过周上限")
note = d.get("note", "")
for s in ["宗门秘境","高级本","1–4 人","个人活跃","按人数倍增","个人活跃计数","不在本卡","待定","不写死","member_cap","不参与进本人数判定","名册","不是进本人数","不新增宗门成员资格校验"]:
    if s not in note: fail("note 缺「" + s + "」")
for s in ["名册上限联动进本人数","名册上限即进本人数","member_cap 决定进本人数","200 人副本"]:
    if s in note: fail("note 出现反口径「" + s + "」")
print("dungeon_table_state=ok version=5 ids=" + ",".join(ids) + " sect.max_players=4")
PY
GOOD="$(python3 "$TMP/validate.py" config/dungeons.json 2>&1)"; GRC=$?
[ "$GRC" = "0" ] && printf '%s' "$GOOD" | grep -q '^dungeon_table_state=ok' \
  && ok "正本通过契约校验：$GOOD" || bad "正本校验没过：$GOOD"
python3 - "$TMP" <<'PY'
import copy, json, os, sys
tmp = sys.argv[1]; base = json.load(open("config/dungeons.json", encoding="utf-8"))
def case(name, mutate):
    d = copy.deepcopy(base); mutate(d)
    json.dump(d, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
def S(d): return [x for x in d["dungeons"] if x["id"] == "sect"][0]
def M(d): return [x for x in d["dungeons"] if x["id"] == "main"][0]
def L(d): return [x for x in d["dungeons"] if x["id"] == "lair"][0]
case("no_sect", lambda d: d["dungeons"].remove(S(d)))
case("max_players_ne4", lambda d: S(d).update(max_players=3))
case("max_players_zero", lambda d: S(d).update(max_players=0))
case("max_players_negative", lambda d: S(d).update(max_players=-1))
case("dup_id", lambda d: L(d).update(id="sect"))
case("charges_fatigue", lambda d: S(d).update(charges_fatigue=True, fatigue_per_player=1500))
case("takes_weekly_share", lambda d: S(d).update(takes_weekly_share=True))
case("bad_version", lambda d: d.update(version=4))
case("missing_field", lambda d: S(d).pop("takes_weekly_share"))
case("old_row_changed", lambda d: M(d).update(max_players=4))
case("global_cap_changed", lambda d: d.update(weekly_share_cap=13))
case("roster_linked", lambda d: d.update(note=d["note"] + " 名册上限联动进本人数"))
PY
BAD_RC=0
for c in no_sect max_players_ne4 max_players_zero max_players_negative dup_id charges_fatigue takes_weekly_share bad_version missing_field old_row_changed global_cap_changed roster_linked; do
  o="$(python3 "$TMP/validate.py" "$TMP/bad-$c.json" 2>&1)"; rc=$?
  assert_bad_config "$o" "坏配置 ${c}：$(printf '%s' "$o" | head -1 | cut -c1-60)"
  [ "$rc" -ne 0 ] || BAD_RC=1
done
[ "$BAD_RC" = "0" ] && ok "12 类坏配置都以非 0 退出" || bad "有坏配置以 0 退出"

echo "== 5. 真服 / 真库段 =="
if [ "$HAS_DB" != "1" ]; then
  printf '       （真库段未跑：MMO_P2_1B_HAS_DB 未置 1；本机段已跑完，不伪造）\n'
elif [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  printf '       （真库段未跑：缺 MMO_GAMEDB / /srv/mmo/gamedb.env；本机段已跑完）\n'
else
  source "$ROOT/tools/env/service-harness.sh"
  harness_init "$TMP/harness"
  for p in 65000 62000 61000 60001; do harness_track_port "$p"; done
  cp "$ROOT/config/dungeons.json" "$TMP/dungeons.orig.json"
  cp "$ROOT/config/sect.json" "$TMP/sect.orig.json"
  trap 'cp "$TMP/dungeons.orig.json" "$ROOT/config/dungeons.json" 2>/dev/null; cp "$TMP/sect.orig.json" "$ROOT/config/sect.json" 2>/dev/null; harness_cleanup; rm -rf "$TMP"' EXIT
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
d = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
d["listen"] = "127.0.0.1:60001"
d["settlement_dir"] = os.path.join(tmp, "settlements"); d["character_dir"] = os.path.join(tmp, "characters")
for k, f in (("skill_table","skills.json"),("class_table","classes.json"),("dungeon_table","dungeons.json")):
    d[k] = os.path.join(root, "config", f)
json.dump(d, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  CC="--config $ROOT/config/character.json"; CS="--config $ROOT/config/social.json"; CG="--config $ROOT/config/gateway.json"
  MIG=1
  "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/account.sql" $CG >"$TMP/m1" 2>&1 || MIG=0
  : > "$TMP/m2"
  for sql in character.sql dungeon_entry.sql inventory.sql currency.sql transaction.sql; do
    "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/$sql" $CC >>"$TMP/m2" 2>&1 || true; done
  "$DOTNET" "$SD" --migrate --sql "$ROOT/db/social/social.sql" $CS >"$TMP/m3" 2>&1 || MIG=0
  [ "$MIG" = "1" ] && grep -q '^migrate=ok' "$TMP/m1" "$TMP/m2" "$TMP/m3" \
    && ok "真 gamedb 三 schema 主迁移（账号/角色/进本记录/社交）" || bad "建表失败：$(head -3 "$TMP/m1" "$TMP/m3")"
  ACCOUNT="p21b_$$"; PASSWORD="P21b-$$-$(date +%s)"
  printf '%s\n' "$PASSWORD" | "$DOTNET" "$GD" --seed-account "$ACCOUNT" $CG >/dev/null 2>&1
  ok "自造临时账号（口令/令牌/角色号只在内存，不落盘不打印）"
  start_all() {
    local p f
    for p in 65000 62000 61000 60001; do harness_kill_port "$p"; done
    harness_start "$TMP/harness/social.log" "$DOTNET" "$SD" $CS >/dev/null
    harness_start "$TMP/harness/character.log" "$DOTNET" "$CD" $CC >/dev/null
    harness_start "$TMP/harness/battle.log" "$DOTNET" "$BD" --config "$TMP/battle.json" >/dev/null
    harness_start "$TMP/harness/gateway.log" "$DOTNET" "$GD" $CG >/dev/null
    for f in social character battle gateway; do harness_wait_ready "$TMP/harness/$f.log" "$f" || return 1; done
    harness_wait_http "$GATEWAY/health" 200
  }
  restart_chr() {
    harness_kill_port 62000
    harness_start "$TMP/harness/character.log" "$DOTNET" "$CD" $CC >/dev/null
    harness_wait_ready "$TMP/harness/character.log" "角色服" || return 1
    harness_wait_http "$GATEWAY/health" 200 || return 1
    for _ in $(seq 1 60); do [ "$(code_of "$(enter "$CH" sect 5)")" = "200" ] && return 0; sleep 0.5; done; return 1
  }
  restart_social() {
    harness_kill_port 65000
    harness_start "$TMP/harness/social.log" "$DOTNET" "$SD" $CS >/dev/null
    harness_wait_ready "$TMP/harness/social.log" "社交服" || return 1
    for _ in $(seq 1 60); do [ "$(code_of "$(post "$GATEWAY/sect/join" "{\"character_id\":$CH,\"sect_id\":999999999}")")" = "200" ] && return 0; sleep 0.5; done; return 1
  }
  post() { curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' -d "$2" 2>&1; }
  check() { [ "$(expect_json "$(body_of "$1")" $2)" = "ok" ] && ok "$3" || bad "$3：$1"; }
  enter() { post "$GATEWAY/dungeon/enter?token=$TOK" "{\"character_id\":${1},\"dungeon_id\":\"${2}\",\"party_size\":${3}}"; }
  reserves() { "$DOTNET" "$CD" --reserves-of "$CH" $CC 2>&1 | grep -m1 '^reserves='; }
  entrycnt() { "$DOTNET" "$CD" --entry-count "$CH" $CC 2>&1 | grep -m1 '^entry_count='; }
  money() { "$DOTNET" "$CD" --shop-state "$CH" $CC 2>&1 | grep -oE 'balances=[^ ]*' | head -1; }
  snap() { printf '%s|%s|%s' "$(reserves)" "$(entrycnt)" "$(money)"; }
  start_all && ok "真社交服 65000 + 角色服 62000 + 网关 61000 + 战斗服 60001 就绪" || bad "有服务没起来（见 harness 日志）"
  DG="$(grep -m1 '^dungeons=' "$TMP/harness/character.log")"
  # 只钉「启动日志里的条数与版本号 == 配置文件里的」，不写死 4 条 / version=5：
  # 后面的卡会往这份表里追加条目（P2-2a 加了龙巢），写死就会假红。
  EXP_COUNT="$(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1],encoding="utf-8"))["dungeons"]))' "$ROOT/config/dungeons.json")"
  EXP_VER="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["version"])' "$ROOT/config/dungeons.json")"
  printf '%s' "$DG" | grep -q "dungeons=$EXP_COUNT " && printf '%s' "$DG" | grep -q "dungeon_table_version=$EXP_VER" \
    && ok "角色服启动日志 $DG" || bad "角色服启动日志与配置不一致（配置 $EXP_COUNT 条 v$EXP_VER）：$DG"
  grep -q '^config=' "$TMP/harness/battle.log" && ok "战斗服启动日志打印它加载的配置（dungeon_table 指向同一份表）" || bad "战斗服启动日志缺 config= 行"
  TOK="$TOKEN"; [ -z "$TOK" ] && TOK="$(harness_login 61000 "$ACCOUNT" "$PASSWORD")"
  [ -n "$TOK" ] && ok "登录拿到令牌" || bad "登录没拿到令牌"
  CH="$CHARACTER"
  if [ "$CH" = "0" ]; then
    CNAME="p21b$(date +%s | tail -c 6)"
    for _ in $(seq 1 20); do
      r="$(curl -sS --noproxy '*' -m 10 -X POST "$GATEWAY/character?token=$TOK" -H 'Content-Type: application/json' -d "{\"name\":\"$CNAME\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>/dev/null)"
      CH="$(printf '%s' "$r" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")')"
      [ -n "$CH" ] && break; sleep 0.5
    done
  fi
  [ -n "$CH" ] && [ "$CH" != "0" ] && ok "测试角色就位：id=$CH" || bad "测试角色没建出来"
  r="$(post "$GATEWAY/sect/create" "{\"character_id\":$CH,\"name\":\"p21b_sect_$$\"}")"
  check "$r" "ok=true level=1 member_cap=10" "真库建测试宗门（1 级）"
  SECT="$(json_field "$(body_of "$r")" sect_id)"
  "$DOTNET" "$SD" --sect-set-level "$SECT" 5 $CS >/dev/null 2>&1
  ST="$("$DOTNET" "$SD" --sect-state "$SECT" $CS 2>&1 | grep -m1 '^sect_state=')"
  CCONF="$("$DOTNET" "$SD" --sect-config 2>&1 | grep -m1 '^sect_config_state=')"
  printf '%s' "$ST" | grep -q 'level=5 member_cap=200' && printf '%s' "$CCONF" | grep -q '5:200' \
    && ok "仅核对用 --sect-set-level 改到 5 级：$ST（--sect-config 含 5:200）" || bad "5 级 200 读不回：$ST / $CCONF"
  echo "-- ① 1/2/3/4 人可进、5 人被拒 --"
  RB="$(reserves)"
  for n in 1 2 3 4; do
    check "$(enter "$CH" sect "$n")" "allowed=true max_players=4 fatigue_cost=0 weekly_share_cost=0 reward_shares=0" "sect ${n} 人：allowed=true max_players=4、不扣疲劳不占份额"
  done
  [ "$(reserves)" = "$RB" ] && ok "1–4 人进本前后 --reserves-of 不变（sect 不扣疲劳、不占周份额）" || bad "sect 动了账：$RB → $(reserves)"
  BEFORE="$(snap)"
  r="$(enter "$CH" sect 5)"
  check "$r" "allowed=false reason=party_too_large max_players=4 fatigue_cost=0 weekly_share_cost=0 entry_id=0" "5 人进秘境：party_too_large limit=4、未生效数值 0"
  [ "$(code_of "$r")" = "200" ] && ok "业务拒绝 HTTP 200" || bad "拒绝码不对：$r"
  [ "$(snap)" = "$BEFORE" ] && ok "5 人拒绝前后显式读库：疲劳/周份额/角色库余额/进本记录没动" || bad "5 人拒绝写了库：$BEFORE → $(snap)"
  echo "-- ② 单人可进、名册上限不参与判定 --"
  check "$(enter "$CH" sect 1)" "allowed=true max_players=4" "单人 party=1 进 sect 成功（技术架构第 66 行）"
  set_cap() { python3 -c 'import json,sys
d=json.load(open(sys.argv[1],encoding="utf-8"));d["levels"][0]["member_cap"]=int(sys.argv[2]);json.dump(d,open(sys.argv[3],"w",encoding="utf-8"),ensure_ascii=False,indent=2)' "$TMP/sect.orig.json" "$1" "$ROOT/config/sect.json"; }
  set_cap 1
  restart_social && restart_chr && ok "1 级 member_cap 临时改成 1、两服重启并轮询只读回 200" || bad "改 cap=1 重启没起来"
  CC2="$("$DOTNET" "$SD" --sect-config 2>&1 | grep -m1 '^sect_config_state=')"
  printf '%s' "$CC2" | grep -q 'levels=1:1,' && ok "--sect-config 打印名册上限已变成 1" || bad "名册配置没变：$CC2"
  check "$(enter "$CH" sect 5)" "allowed=false reason=party_too_large max_players=4" "名册上限=1 时第 5 人仍被拒、sect.max_players 仍 4"
  cp "$TMP/sect.orig.json" "$ROOT/config/sect.json"
  restart_social && restart_chr && ok "改回 member_cap=10、两服重启并轮询只读回 200" || bad "改回重启没起来"
  check "$(enter "$CH" sect 5)" "allowed=false reason=party_too_large max_players=4" "改回后第 5 人仍被拒、sect.max_players 仍 4"
  echo "-- ③ 既有副本口径不回退 --"
  check "$(enter "$CH" main 2)" "allowed=false reason=party_too_large max_players=1" "主线仍 1 人：第 2 人 party_too_large limit=1"
  check "$(enter "$CH" lair 5)" "allowed=false reason=party_too_large max_players=4" "巢穴仍 1–4 人：第 5 人 party_too_large limit=4"
  echo "-- ④ 拒绝路径每条前后显式读库 --"
  BEFORE="$(snap)"
  r="$(enter "$CH" no_such_dungeon 1)"
  [ "$(code_of "$r")" = "400" ] && [ "$(json_field "$(body_of "$r")" error)" = "unknown_dungeon" ] && ok "不存在的编号：400 unknown_dungeon" || bad "dungeon_not_found 路径不对：$r"
  [ "$(snap)" = "$BEFORE" ] && ok "unknown_dungeon：显式读库没动" || bad "unknown_dungeon 写了库"
  r="$(curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$GATEWAY/dungeon/enter" -H 'Content-Type: application/json' -d "{\"character_id\":$CH,\"dungeon_id\":\"sect\",\"party_size\":1}" 2>&1)"
  [ "$(code_of "$r")" = "401" ] && ok "无令牌：HTTP 401" || bad "无令牌码不对：$r"
  [ "$(snap)" = "$BEFORE" ] && ok "无令牌：显式读库没动" || bad "无令牌写了库"
  r="$(post "$GATEWAY/dungeon/enter?token=$TOK" "{\"character_id\":$CH,\"dungeon_id\":\"sect\"}")"
  [ "$(code_of "$r")" = "400" ] && ok "缺 party_size：HTTP 400" || bad "缺字段码不对：$r"
  [ "$(snap)" = "$BEFORE" ] && ok "缺字段：显式读库没动" || bad "缺字段写了库"
  r="$(enter 999999999 sect 1)"
  [ "$(code_of "$r")" = "404" ] && ok "别人的角色：HTTP 404" || bad "别人的角色码不对：$r"
  [ "$(snap)" = "$BEFORE" ] && ok "别人的角色：显式读库没动" || bad "别人的角色写了库"
  echo "-- ⑤ 战斗服长连接：4 人连上后第 5 人 party_full limit=4 --"
  BURL="ws://127.0.0.1:60001/battle?dungeon=sect&party_size=4&party=p21b_sect_full"
  HP=()
  for n in 1 2 3 4; do
    "$DOTNET" "$PD" --url "${BURL}&char=${n}" --case p12a_join --expect open --hold-ms 12000 >"$TMP/holder-$n.txt" 2>&1 &
    HP+=("$!")
  done
  HOLD_OK=1
  for n in 1 2 3 4; do
    READY=0
    for _ in $(seq 1 80); do grep -q 'joined=ok' "$TMP/holder-$n.txt" 2>/dev/null && { READY=1; break; }; sleep 0.1; done
    [ "$READY" = "1" ] || HOLD_OK=0
  done
  [ "$HOLD_OK" = "1" ] && ok "4 人连上同一个 sect 长连接场" || bad "4 人没都连上"
  FIFTH="$("$DOTNET" "$PD" --url "${BURL}&char=5" --case p12a_join --expect reject 2>&1)"
  printf '%s' "$FIFTH" | grep -q '^verdict=ok' && printf '%s' "$FIFTH" | grep -q 'reason=party_full' \
    && ok "第 5 人收到 EnterRejected reason=party_full" || bad "第 5 人没被挡：$FIFTH"
  grep -q 'join_reject=party=p21b_sect_full dungeon=sect reason=party_full' "$TMP/harness/battle.log" \
    && grep -q 'dungeon=sect reason=party_full current=4 limit=4' "$TMP/harness/battle.log" \
    && ok "战斗服日志 join_reject ... reason=party_full limit=4（同一份 sect.max_players）" \
    || bad "战斗服日志缺 party_full limit=4：$(grep 'join_reject' "$TMP/harness/battle.log" | tail -3)"
  for pid in "${HP[@]}"; do stop_pid "$pid"; done
  echo "-- 坏表真角色服起不来（同一份 Load）--"
  harness_kill_port 62000
  python3 - "$ROOT/config/dungeons.json" "$TMP/dungeons.badstart.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
for x in d["dungeons"]:
    if x["id"] == "sect": x["max_players"] = 0
json.dump(d, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
  cp "$TMP/dungeons.badstart.json" "$ROOT/config/dungeons.json"
  "$DOTNET" "$CD" $CC >"$TMP/harness/badstart.log" 2>&1; BRC=$?
  assert_bad_config "$(cat "$TMP/harness/badstart.log")" "坏表（sect.max_players=0）真角色服起不来：$(head -1 "$TMP/harness/badstart.log" | cut -c1-56)"
  [ "$BRC" -ne 0 ] && ok "坏表真角色服以非 0 退出（rc=$BRC）" || bad "坏表真角色服以 0 退出"
  cp "$TMP/dungeons.orig.json" "$ROOT/config/dungeons.json"
  harness_cleanup
fi
finish "P2-1b（server）"
