#!/usr/bin/env bash
# P3-5a · 世界 Boss 进战斗服（方案 10.4、技术架构 5 / 6 节、
# 开发架构 11 节 P3-5、13 节 P3-5a、14 节第 70 行）。
#   bash tools/check/check-p3-5a.sh
#   MMO_P3_5A_HAS_DB=1 MMO_GAMEDB='Host=...' bash tools/check/check-p3-5a.sh
# 完成标准：一个人能进；第 21 人被拒绝；世界服不结算这场技能；归属不加属性；
#           倒地后生息和复活卷轴都能拉，等级不变。
set -uo pipefail
# 本机代理会把 127.0.0.1 的明文 HTTP/2 请求送进代理，gRPC 段必须先 unset。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
SD="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
WD="$ROOT/src/World/bin/Release/net10.0/World.dll"
PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
CC="--config $ROOT/config/character.json"
CS="--config $ROOT/config/social.json"
CG="--config $ROOT/config/gateway.json"
CW="--config $ROOT/config/world.json"
WB="$ROOT/config/world_boss.json"
HAS_DB="${MMO_P3_5A_HAS_DB:-0}"
TMP="$(mktemp -d)"; PIDS=""
utf8() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      python3 -c 'import sys; sys.stdout.buffer.write(
          sys.stdin.buffer.read().decode("gbk", "replace").encode("utf-8"))' ;;
    *) cat ;;
  esac
}
cleanup() { for p in $PIDS; do stop_pid "$p"; done; rm -rf "$TMP"; }
trap cleanup EXIT
jget() { python3 -c 'import json,sys
o=json.load(sys.stdin); v=o.get(sys.argv[1])
print("" if v is None else ("true" if v is True else "false" if v is False else v))' "$1"; }
listen_of() {
  for _ in $(seq 1 160); do
    local u; u="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$1" 2>/dev/null | tail -1 | sed 's/^listen=//')"
    [ -n "$u" ] && { printf '%s' "$u"; return 0; }
    sleep 0.1
  done
  return 1
}
# 战斗服的临时配置：表路径一律给绝对路径——临时配置所在目录里没有那几张表，
# 加载器是按配置文件的目录去找相对路径的。
mk_battle() { # <名字> [世界 Boss 表路径]
  python3 - "$ROOT" "$TMP" "$1" "${2:-}" <<'PY'
import json, os, sys
root, tmp, name, table = sys.argv[1:5]
d = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
d.update(listen="127.0.0.1:0",
         settlement_dir=os.path.join(tmp, "set-" + name),
         character_dir=os.path.join(tmp, "chr-" + name),
         exp_upstream="http://127.0.0.1:62000",
         character_upstream="http://127.0.0.1:62000",
         social_upstream="http://127.0.0.1:65000",
         world_upstream="http://127.0.0.1:63000")
if table:
    d["world_boss_table"] = table
else:
    d.pop("world_boss_table", None)
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json")):
    d[k] = os.path.join(root, "config", f)
json.dump(d, open(os.path.join(tmp, "battle-" + name + ".json"), "w", encoding="utf-8"),
          ensure_ascii=False)
PY
}
# 配置里现取，不在脚本里写死：上限、首领编号、入口编号。
CAP="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["cap"])' "$WB")"
BOSS="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["boss"]["id"])' "$WB")"
ENT="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["entrance"]["id"])' "$WB")"

echo "== 1. 编译 =="
for proj in src/Battle/Battle.csproj src/World/World.csproj src/Gateway/Gateway.csproj \
            src/Character/Character.csproj src/Social/Social.csproj src/Global/Global.csproj \
            tools/probe/BattleProbe/BattleProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 $n：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done

echo "== 2. 静态断言（结构保证，不写死配置里的数字）=="
python3 - "$ROOT" >"$TMP/static.txt" 2>&1 <<'PY'
import hashlib, json, os, re, sys
root = sys.argv[1]
def rd(p):
    q = os.path.join(root, p)
    return open(q, encoding="utf-8").read() if os.path.exists(q) else ""
def cfg(p): return json.loads(rd(p))
def emit(k, c, i=""): print(("PASS " if c else "FAIL ") + k + ("" if c else "  " + str(i)[:150]))
t = cfg("config/world_boss.json")
bc = cfg("config/battle.json")
region = cfg("config/region/" + t["entrance"]["region_id"] + ".json")
emit("world_boss_schema", t.get("schema") == "world-boss-table/1" and t.get("version", 0) > 0)
emit("world_boss_pending_note", t.get("pending") is True and "待定" in t.get("note", ""))
emit("world_boss_cap_is_config", isinstance(t.get("cap"), int) and 0 < t["cap"] <= 100)
emit("world_boss_entrance_on_open_region", region.get("open") is True)
emit("world_boss_entrance_keys", set(t["entrance"]) <= {"id", "region_id", "name", "note"})
emit("world_boss_boss_keys",
     set(t["boss"]) <= {"id", "name", "hp_max", "spawn_mm", "exp_on_clear", "attack", "note"})
keys = json.dumps(t, ensure_ascii=False).lower()
emit("world_boss_no_reward_or_attribute_keys", not any(k in keys for k in (
     "reward", "drop", "loot", "gold", "spirit_stone", "vein", "tax", "owner_sect",
     "shop", "balance", "attribute", "power")))
emit("world_boss_windows_in_config", len(t["windows"]) >= 1 and all(
     re.fullmatch(r"\d\d:\d\d", w["start_utc"]) and w["duration_s"] >= 60 for w in t["windows"]))
emit("battle_json_no_new_table", "world_boss_table" not in bc and "world_upstream" not in bc)
# 经验只有一份正本（config/exp.json）：世界 Boss 表里的那个数字只给结算回包用，
# 两边不一致就会出现「屏幕说一份、包里另一份」。
emit("world_boss_exp_matches_policy",
     t["boss"]["exp_on_clear"] == cfg("config/exp.json")["base_exp"])
proto = rd("proto/mmo/world.proto")
emit("proto_has_rpcs", "rpc DescribeWorldBoss(" in proto and "rpc RecordWorldBossOwner(" in proto)
req = proto.split("message RecordWorldBossOwnerRequest {")[1].split("}")[0]
emit("record_request_no_reward", not any(k in req for k in (
     "reward", "drop", "item", "gold", "attribute", "tax", "vein", "shop", "power")))
emit("record_request_only_name", "winner_sect_id" in req and "winner_sect_name" in req)
s1 = rd("src/Shared/Generated/World.cs"); s2 = rd("client/MMOGame/Assets/Proto/Generated/World.cs")
emit("two_ends_world_same", bool(s1) and
     hashlib.sha256(s1.encode()).hexdigest() == hashlib.sha256(s2.encode()).hexdigest())
wc = rd("src/World/WorldBossTable.cs"); bwc = rd("src/Battle/WorldBossTable.cs")
banned = ("reward", "drop", "loot", "gold", "spirit_stone", "vein", "tax", "owner_sect", "balance")
emit("banned_keys_both_ends", all(k in wc for k in banned) and all(k in bwc for k in banned))
world_src = "".join(rd(os.path.join("src/World", f)) for f in os.listdir(os.path.join(root, "src/World"))
                    if f.endswith(".cs"))
emit("world_never_settles_skills", not re.search(r"Encounter|SkillResult|skill_result", world_src))
ent = rd("src/World/WorldBossEntrance.cs")
emit("owner_is_memory_only", not re.search(r"insert into|update \"|delete from", ent, re.I))
emit("entrance_key_by_server_clock", "table.WindowOf(now)" in ent and "StartSeconds" in wc)
srv = rd("src/Battle/Server.cs"); enc = rd("src/Battle/Encounter.cs")
emit("worldboss_branch_before_siege",
     srv.index('QueryOrNull(query, "worldboss")') < srv.index('QueryOrNull(query, "siege")'))
emit("worldboss_mutually_exclusive", "worldboss_with_other_mode" in srv)
emit("worldboss_cap_from_config", "table.Cap" in srv and not re.search(r"cap\s*==\s*\d{2}\b", srv))
emit("settlement_source_world_boss", 'SettlementSource = "world_boss"' in srv)
emit("settlement_party_size_gate", "_settlementPartySize > 0" in enc and "SetSettlementPartySize" in enc)
emit("worldboss_damage_not_in_encounter", "WorldBossDamage" not in enc and "WorldBossDamage" in srv)
emit("sect_name_is_social_fact",
     "sectFacts.NameOf" in srv and "NameOf" in rd("src/Battle/SiegeResultClient.cs"))
emit("battle_no_db", "Npgsql" not in "".join(
     rd(os.path.join("src/Battle", f)) for f in os.listdir(os.path.join(root, "src/Battle"))
     if f.endswith(".cs")))
emit("exp_settle_has_source", "source=" in rd("src/Battle/ExpSettlement.cs"))
emit("no_world_boss_in_exp_repo", "world_boss" not in rd("src/Character/ExpSettlementRepository.cs"))
emit("gateway_worldboss_route", 'MapGet("/world/worldboss"' in rd("src/Gateway/WorldApi.cs"))
emit("city_untouched", cfg("config/city.json")["cities"][0].get("vein_id") == "spirit_qiyun_1")
PY
while IFS= read -r line; do
  case "$line" in "PASS "*) ok "${line#PASS }" ;; "FAIL "*) bad "${line#FAIL }" ;; esac
done < "$TMP/static.txt"

echo "== 3. 两个只读入口按同一时刻算出同一把钥匙 =="
python3 - "$WB" >"$TMP/points.txt" <<'PY'
import datetime, json, sys
t = json.load(open(sys.argv[1], encoding="utf-8"))
eid = t["entrance"]["id"]
w1, w2 = t["windows"][0], t["windows"][1] if len(t["windows"]) > 1 else (t["windows"][0], None)
day = datetime.date(2026, 11, 14)
def at(w, secs):
    h, m = (int(x) for x in w["start_utc"].split(":"))
    return datetime.datetime(day.year, day.month, day.day, h, m,
                             tzinfo=datetime.timezone.utc) + datetime.timedelta(seconds=secs)
def key(w): return "%s-%s-%s" % (eid, day.strftime("%Y%m%d"), w["id"])
def emit(w, secs, want_open):
    ms = int(at(w, secs).timestamp() * 1000)
    print(ms, "true" if want_open else "false", key(w) if want_open else "none")
emit(w1, 5, True)
emit(w1, w1["duration_s"] - 1, True)
emit(w1, w1["duration_s"], False)
if w2 is not None:
    emit(w2, -1, False)
    emit(w2, 0, True)
PY
while read -r ms want_open want_key; do
  # python3 在 Windows 上按文本模式打 \r\n，末位字段会拖一个 \r，取值后必须削掉。
  want_open="${want_open%$'\r'}"; want_key="${want_key%$'\r'}"
  WO="$("$DOTNET" "$WD" $CW --world-boss-state --world-boss-config "$WB" --at "$ms" 2>&1 | utf8 | grep -m1 '^world_boss_window=')"
  BO="$("$DOTNET" "$BD" --world-boss-table-path "$WB" --at "$ms" 2>&1 | utf8 | grep -m1 '^world_boss_now=')"
  WN="${WO#world_boss_window=}"; BN="${BO#world_boss_now=}"
  WN="${WN%$'\r'}"; BN="${BN%$'\r'}"
  if [ -n "$WN" ] && [ "$WN" = "$BN" ] && printf '%s' "$WN" | grep -q "open=$want_open key=$want_key " \
     && printf '%s' "$WN" | grep -q "^at=$ms "; then
    ok "同一时刻（$ms）两端钥匙一致：open=$want_open key=$want_key"
  else
    bad "两端跑偏（$ms 期望 open=$want_open key=$want_key）：世界服[$WN] 战斗服[$BN]"
  fi
done < "$TMP/points.txt"

echo "== 4. 入口状态机（只记名字、打掉只认第一次、散场可升级成打掉）=="
SO="$("$DOTNET" "$WD" $CW --world-boss-state --world-boss-config "$WB" 2>&1 | utf8)"
printf '%s' "$SO" | grep -q 'step=disband .*duplicate=false .*closed=false .*winner=0' \
  && printf '%s' "$SO" | grep -q 'step=disband_again .*duplicate=true' \
  && ok "散场没打掉：第一次记下来，第二次重复报不改" \
  || bad "散场那一步不对：$(printf '%s' "$SO" | grep 'world_boss_record=' | head -2 | detail)"
printf '%s' "$SO" | grep -q 'step=kill .*duplicate=false .*closed=true' \
  && printf '%s' "$SO" | grep -q 'step=kill_again .*duplicate=true .*closed=true' \
  && ok "打掉就关入口，第二条报一律 duplicate（谁先报谁算）" \
  || bad "打掉那一步不对：$(printf '%s' "$SO" | grep 'world_boss_record=' | sed -n 3,4p | detail)"
printf '%s' "$SO" | grep -q 'winner=7001 name=[^ ]* killed=true' \
  && printf '%s' "$SO" | grep -q 'step=wrong_entrance .*reason=wrong_entrance' \
  && printf '%s' "$SO" | grep -q 'step=no_sect_name .*reason=sect_name_missing' \
  && ok "只认自己那个入口、只认有名字的宗门" \
  || bad "入口校验不对：$(printf '%s' "$SO" | grep 'world_boss_record=' | tail -3 | detail)"
printf '%s' "$SO" | grep -q 'world_boss_record=summary recorded=2 kills=1' \
  && ok "只记名字：一场只记一次、只关一次，没有任何数值被改写" \
  || bad "汇总不对：$(printf '%s' "$SO" | grep 'summary' | detail)"

echo "== 5. 坏配置与缺表降级 =="
mk_bad() {
  python3 - "$WB" "$TMP/bad-$1.json" "$1" <<'PY'
import json, sys
src, dst, mode = sys.argv[1:4]
d = json.load(open(src, encoding="utf-8"))
w = d["windows"][0]
if mode == "schema": d["schema"] = "world-boss-table/2"
elif mode == "version": d["version"] = 0
elif mode == "timezone": d["timezone"] = "local"
elif mode == "cap0": d["cap"] = 0
elif mode == "cap101": d["cap"] = 101
elif mode == "region_closed": d["entrance"]["region_id"] = "焦原"
elif mode == "region_unknown": d["entrance"]["region_id"] = "无此地"
elif mode == "no_windows": d["windows"] = []
elif mode == "overlap": d["windows"] = [dict(w), dict(w, id="w2")]
elif mode == "midnight": w["start_utc"] = "23:50"
elif mode == "short": w["duration_s"] = 30
elif mode == "bad_clock": w["start_utc"] = "25:00"
elif mode == "hp0": d["boss"]["hp_max"] = 0
elif mode == "no_boss": d.pop("boss")
elif mode == "reward": d["reward"] = {"item_id": "x"}
elif mode == "owner": d["owner_sect"] = "sect-1"
elif mode == "vein": d["entrance"]["vein_id"] = "spirit_qiyun_1"
json.dump(d, open(dst, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
}
for c in schema version timezone cap0 cap101 no_windows overlap midnight short bad_clock \
         hp0 no_boss reward owner vein; do
  mk_bad "$c"
  wout="$("$DOTNET" "$WD" $CW --world-boss-state --world-boss-config "$TMP/bad-$c.json" 2>&1 | utf8)"
  wrc=$?
  bout="$("$DOTNET" "$BD" --world-boss-table-path "$TMP/bad-$c.json" 2>&1 | utf8)"
  brc=$?
  if [ "$wrc" -ne 0 ] && [ "$brc" -ne 0 ] \
     && printf '%s' "$wout" | grep -q '^config_error=' && printf '%s' "$bout" | grep -q '^config_error='; then
    ok "坏配置 ${c}：两端都打 config_error= 且非 0 退出"
  else
    bad "坏配置 ${c} 没挡住（世界服 rc=$wrc / 战斗服 rc=$brc）：$(printf '%s' "$wout" | head -1)"
  fi
done
# 入口必须长在已开放的地表上：这条只有世界服知道（战斗服不读地表开放表），
# 所以只要求世界服挡住，战斗服照样能把这一张表读进来算窗口。
for c in region_closed region_unknown; do
  mk_bad "$c"
  wout="$("$DOTNET" "$WD" $CW --world-boss-state --world-boss-config "$TMP/bad-$c.json" 2>&1 | utf8)"
  wrc=$?
  bout="$("$DOTNET" "$BD" --world-boss-table-path "$TMP/bad-$c.json" 2>&1 | utf8)"
  if [ "$wrc" -ne 0 ] && printf '%s' "$wout" | grep -q '^config_error=' \
     && printf '%s' "$bout" | grep -q '^world_boss_table_version='; then
    ok "入口不在已开放地表（${c}）：世界服打 config_error=，战斗服只算窗口不挡进场"
  else
    bad "入口地段校验不对（${c}）：世界服 rc=$wrc，战斗服[$(printf '%s' "$bout" | head -1)]"
  fi
done
printf '%s' "$("$DOTNET" "$WD" $CW --world-boss-state --world-boss-config "$TMP/none.json" 2>&1 | utf8)" \
  | grep -q '^world_boss=missing' \
  && ok "缺表：世界服打 world_boss=missing，不挡启动" || bad "缺表没降级"
mk_battle novb
printf '%s' "$("$DOTNET" "$BD" --config "$TMP/battle-novb.json" --world-boss-table 2>&1 | utf8)" \
  | grep -q '^world_boss=missing' \
  && ok "缺表：战斗服打 world_boss=missing，老临时配置目录照样起服务" || bad "战斗服缺表没降级"

echo "== 6. 真服真库（本节全部需要真服；跳过时只算静态证据）=="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真服真库段（MMO_P3_5A_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  python3 - "$ROOT" "$TMP" <<'PY'
import datetime, json, os, sys
root, tmp = sys.argv[1:3]
now = datetime.datetime.now(datetime.timezone.utc)
now_s = now.hour * 3600 + now.minute * 60 + now.second
base = json.load(open(os.path.join(root, "config", "world_boss.json"), encoding="utf-8"))
def hhmm(sec): return "%02d:%02d" % (sec // 3600, (sec % 3600) // 60)
def variant(name, windows, damage=None, interval=None, hp=None):
    d = json.loads(json.dumps(base))
    d["windows"] = windows
    if damage: d["boss"]["attack"]["damage"] = damage
    if interval: d["boss"]["attack"]["interval_ms"] = interval
    if hp: d["boss"]["hp_max"] = hp
    json.dump(d, open(os.path.join(tmp, name), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
# 到点：窗口从今天 00:00 起、覆盖「现在」，绝不跨午夜；场次编号各家不同，
# 这样三个核对进程各自打的是不同的一场，互不干扰。
# 打掉那两场把首领调成「血少、几乎不还手」——核对要的是「一个人也能打完」这条，
# 不是站桩耐受力；复活那场才让它下死手，否则没有倒地可拉。
open_dur = min(86400, max(60, now_s + 600))
future = now + datetime.timedelta(minutes=30)
closed_start = future.hour * 3600 + future.minute * 60 if future.date() == now.date() else 0
variant("wb-open.json", [{"id": "w1", "start_utc": "00:00", "duration_s": open_dur}],
        damage=1, hp=900)
variant("wb-open2.json", [{"id": "w9", "start_utc": "00:00", "duration_s": open_dur}],
        damage=1, hp=900)
variant("wb-open3.json", [{"id": "w7", "start_utc": "00:00", "duration_s": open_dur}],
        damage=900, interval=1200)
variant("wb-closed.json", [{"id": "w5", "start_utc": hhmm(closed_start), "duration_s": 60}])
PY
  for spec in "closed:wb-closed.json" "open:wb-open.json" "open2:wb-open2.json" "open3:wb-open3.json"; do
    mk_battle "${spec%%:*}" "$TMP/${spec##*:}"
  done
  "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/character.sql" $CG >"$TMP/mig-character.log" 2>&1 || true
  "$DOTNET" "$SD" --migrate --sql "$ROOT/db/social/social.sql" $CS >"$TMP/mig-social.log" 2>&1 || true
  grep -q '^migrate=ok' "$TMP/mig-character.log" \
    && ok "真库：主迁移自包含" || bad "主迁移失败：$(head -3 "$TMP/mig-character.log" | detail)"
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$SD" $CS >"$TMP/social.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/social.log" '^ready' "社交服" || true
  "$DOTNET" "$WD" $CW --gather-config "$ROOT/config/gather.json" \
    --world-boss-config "$TMP/wb-open.json" >"$TMP/world.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/world.log" '^ready' "世界服" || true
  for n in closed open open2 open3; do
    "$DOTNET" "$BD" --config "$TMP/battle-$n.json" >"$TMP/battle-$n.log" 2>&1 & PIDS="$PIDS $!"
  done
  U1="$(listen_of "$TMP/battle-closed.log")"; U2="$(listen_of "$TMP/battle-open.log")"
  U3="$(listen_of "$TMP/battle-open2.log")"; U4="$(listen_of "$TMP/battle-open3.log")"
  # 客户端那条是 WebSocket：listen 回的是 http://，探针只认 ws://。
  U1="ws${U1#http}"; U2="ws${U2#http}"; U3="ws${U3#http}"; U4="ws${U4#http}"
  [ -n "$U1" ] && [ -n "$U2" ] && [ -n "$U3" ] && [ -n "$U4" ] \
    && ok "真服：四场战斗进程各就绪（未到点 / 单人 / 同门 / 复活）" || bad "战斗进程没起来"
  python3 - "$ROOT" "$TMP" "$U2" <<'PY'
import json, os, sys
root, tmp, url = sys.argv[1:4]
d = json.load(open(os.path.join(root, "config", "gateway.json"), encoding="utf-8"))
d["battle_upstream"] = "ws" + url[url.index("://"):] + "/battle"
json.dump(d, open(os.path.join(tmp, "gateway-real.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
  GR="--config $TMP/gateway-real.json"
  "$DOTNET" "$GD" $GR >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
      -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p335_$$"; PW="P335-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 $GR >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" | jget token 2>/dev/null)"
  [ -n "$TOK" ] && ok "真服：登录拿到令牌（账号/口令只在内存）" || bad "真服：登录没拿到令牌"
  post() { curl -s --noproxy '*' -X POST "http://127.0.0.1:61000$1?token=$TOK" \
    -H 'Content-Type: application/json' -d "$2"; }
  mkchar() {
    local cls="$1" o id
    for _ in $(seq 1 10); do
      o="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
        -H 'Content-Type: application/json' -d "{\"name\":\"p335_$cls$RANDOM\",\"race\":\"mai\",\"class_id\":\"$cls\"}" 2>&1)"
      id="$(printf '%s' "$o" | jget character_id 2>/dev/null)"
      [ -n "$id" ] && { printf '%s' "$id"; return 0; }; sleep 0.5
    done
  }
  A="$(mkchar warrior)"; B="$(mkchar priest)"
  [ -n "$A" ] && [ -n "$B" ] && ok "真服：同一账号造两个角色（$A / $B）" || bad "真服：角色没建出来"
  SECT="$(post /sect/create "{\"character_id\":$A,\"name\":\"p335_sect_$$\"}" | jget sect_id 2>/dev/null)"
  post /sect/join "{\"character_id\":$B,\"sect_id\":$SECT}" >/dev/null 2>&1
  [ -n "$SECT" ] && ok "真库：A 建宗门、B 入同一个宗门（sect=$SECT）" || bad "真库：宗门没建出来"
  # 复活卷轴是商店货，这里按正规路径买一张：核对不自己往库里塞道具。
  # 新角色身上没有元宝，先走「加余额」那条路把够买的元宝记上（只给核对用）。
  SCROLL_ITEM="$(python3 -c 'import json,sys
d = json.load(open("config/shop.json", encoding="utf-8"))
print(next(i["id"] for i in d["items"] if i["kind"] == "revive_scroll"))')"
  PRICE="$(python3 -c 'import json,sys
d = json.load(open("config/shop.json", encoding="utf-8"))
print(next(i["price_yuanbao"] for i in d["items"] if i["kind"] == "revive_scroll"))')"
  ADD="$(post /currency/add "{\"character_id\":$A,\"kind\":\"yuanbao\",\"delta\":$((PRICE * 2))}")"
  BUY="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/shop/buy?token=$TOK" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$A,\"item_id\":\"$SCROLL_ITEM\"}")"
  [ "$(printf '%s' "$BUY" | jget ok)" = "true" ] \
    && ok "真库：A 从商店买到一张复活卷轴（$SCROLL_ITEM，$PRICE 元宝）" \
    || bad "卷轴没买到（加余额：$ADD）：$BUY"
  EXP0="$("$DOTNET" "$CD" $CC --revive-scroll-state "$A" 2>&1 | utf8 \
    | sed -n 's/.*\bexp=\([0-9]*\).*/\1/p' | head -1)"

  # 1) 未到点进不去：理由由服务端时钟给，客户端带不进时间。
  R="$("$DOTNET" "$PROBE" --url "$U1/battle?worldboss=$ENT&char=$A&class=warrior" \
    --case worldboss_join --expect closed 2>&1)"; echo "$R" | sed 's/^/       /'
  printf '%s' "$R" | grep -q 'verdict=ok' && printf '%s' "$R" | grep -q 'reject=worldboss_closed' \
    && ok "未到点：一个人也进不去，回 worldboss_closed" \
    || bad "未到点没挡住：$(printf '%s' "$R" | head -2 | detail)"
  grep -q "join_reject=worldboss reason=worldboss_closed" "$TMP/battle-closed.log" \
    && ok "拒绝只由服务端事实值给出（服务端日志有 worldboss_closed）" || bad "服务端没记拒绝原因"

  # 2) 首发上限 20：满 20 人，第 21 人被拒。
  R="$("$DOTNET" "$PROBE" --url "$U2/battle?worldboss=$ENT" --case worldboss_fill --cap "$CAP" \
    --a-char "$A" --boss "$BOSS" 2>&1)"; echo "$R" | sed 's/^/       /'
  printf '%s' "$R" | grep -q 'verdict=ok' \
    && ok "一个人能进；满 $CAP 人后第 $((CAP + 1)) 人回 worldboss_full（上限只从配置读）" \
    || bad "人数上限不对：$(printf '%s' "$R" | tail -2 | detail)"
  grep -q "reason=worldboss_full current=$CAP limit=$CAP" "$TMP/battle-open.log" \
    && ok "服务端日志：worldboss_full current=$CAP limit=$CAP" || bad "没记满员拒绝"

  # 3) 一个人打掉这一场：结算来源 world_boss、等级经验真进库。
  R="$("$DOTNET" "$PROBE" --url "$U2/battle?worldboss=$ENT" --case worldboss_clear --boss "$BOSS" \
    --a-char "$A" 2>&1)"; echo "$R" | sed 's/^/       /'
  printf '%s' "$R" | grep -q 'verdict=ok' && ok "一个人把这一场的 Boss 打掉，回结算号" \
    || bad "单人清场不对：$(printf '%s' "$R" | tail -2 | detail)"
  SETTLE="$(grep "exp_settle=ok" "$TMP/battle-open.log" | grep " character=$A " | head -1)"
  printf '%s' "$SETTLE" | grep -q 'party=1' && printf '%s' "$SETTLE" | grep -q 'source=world_boss' \
    && ok "结算来源是 world_boss（世界服不结算这场技能）：$(printf '%s' "$SETTLE" | grep -oE 'gain=[0-9]+')" \
    || bad "结算来源不对：$SETTLE"
  printf '%s' "$SETTLE" | grep -qi 'duplicate=false' && printf '%s' "$SETTLE" | grep -q 'dungeon=none' \
    && ok "这场不是副本：dungeon=none，事务不重复" || bad "副本字段不对：$SETTLE"
  EXP1="$("$DOTNET" "$CD" $CC --revive-scroll-state "$A" 2>&1 | utf8 \
    | sed -n 's/.*\bexp=\([0-9]*\).*/\1/p' | head -1)"
  if [ -n "$EXP0" ] && [ -n "$EXP1" ] && [ "$EXP1" -gt "$EXP0" ]; then
    ok "真库：等级经验从 $EXP0 涨到 $EXP1（结算真落库）"
  else
    bad "经验没进库：exp0=$EXP0 exp1=$EXP1"
  fi
  RES="$(grep 'worldboss_result=' "$TMP/battle-open.log" | grep -i 'killed=true' | head -1)"
  printf '%s' "$RES" | grep -q "winner_sect=$SECT " && printf '%s' "$RES" | grep -qi 'ok=true' \
    && printf '%s' "$RES" | grep -qi 'entrance_closed=true' \
    && ok "归属只记名字：伤害最高的宗门是 $SECT，入口随即关闭" \
    || bad "归属记录不对：$RES"
  WBJSON="$(curl -s --noproxy '*' "http://127.0.0.1:61000/world/worldboss?token=$TOK")"
  [ "$(printf '%s' "$WBJSON" | jget killed)" = "true" ] \
    && [ "$(printf '%s' "$WBJSON" | jget owner_sect_id)" = "$SECT" ] \
    && [ "$(printf '%s' "$WBJSON" | jget owner_sect_name)" != "" ] \
    && ok "网关只读入口回：这一场已打掉，归属记的是 $SECT 的名字" \
    || bad "网关回的世界 Boss 状态不对：$WBJSON"

  # 4) 两个人同门：组队加成与同门加成在这场生效，结算人数是 2。
  R="$("$DOTNET" "$PROBE" --url "$U3/battle?worldboss=$ENT" --case worldboss_clear --boss "$BOSS" \
    --a-char "$A" --second-char "$B" 2>&1)"; echo "$R" | sed 's/^/       /'
  printf '%s' "$R" | grep -q 'verdict=ok' && ok "两个人同场把这一场打掉" \
    || bad "两人清场不对：$(printf '%s' "$R" | tail -2 | detail)"
  TWO="$(grep "exp_settle=ok" "$TMP/battle-open2.log" | head -1)"
  printf '%s' "$TWO" | grep -q 'party=2' \
    && [ "$(printf '%s' "$TWO" | sed -n 's/.* bonus=\([0-9]*\).*/\1/p')" -gt 0 ] \
    && [ "$(printf '%s' "$TWO" | sed -n 's/.* same_sect=\([0-9]*\).*/\1/p')" -gt 0 ] \
    && ok "组队加成与同门加成在这里生效：$(printf '%s' "$TWO" | grep -oE 'party=[0-9]+ gain=[0-9]+ bonus=[0-9]+ same_sect=[0-9]+')" \
    || bad "加成不对：$TWO"

  # 5) 倒地后生息与复活卷轴都能拉，等级不变。
  # 这里只看「倒地 → 被拉起来」这一条事实：牧师技能的冷却与错目标拒绝属于
  # P0-3a / P1-3a 那几张卡的口径，在那边按短冷却的临时技能表验过，这里不重跑。
  R="$("$DOTNET" "$PROBE" --url "$U4/battle?worldboss=$ENT" --case p03a_resurrect --boss "$BOSS" \
    --party "wb$$" --warrior-char "$A" --priest-char "$B" --hit 1002 --resurrect 1303 2>&1)"
  echo "$R" | sed 's/^/       /'
  RAISE="$(printf '%s' "$R" | grep -m1 '^p03a_resurrect ')"
  printf '%s' "$RAISE" | grep -q 'down=True' && printf '%s' "$RAISE" | grep -q 'restore=[1-9]' \
    && printf '%s' "$RAISE" | grep -q 'hp_after=[1-9]' \
    && ok "世界 Boss 里被打倒后，牧师的生息把人拉起来（$(printf '%s' "$RAISE" | grep -oE 'restore=[0-9]+ hp_after=[0-9]+')）" \
    || bad "生息在世界上不对：$RAISE"
  "$DOTNET" "$CD" $CC --scroll-cooldown-reset "$A" >/dev/null 2>&1
  "$DOTNET" "$CD" $CC --raw-exp-write "$A" 1000 >/dev/null 2>&1
  R="$("$DOTNET" "$PROBE" --url "$U4/battle?worldboss=$ENT&char=$A&class=warrior" --case p15c6_scroll \
    --scroll scroll_revive --exp-before 1000 --exp-cost 100 --expect-level 1 2>&1)"
  echo "$R" | sed 's/^/       /'
  printf '%s' "$R" | grep -q 'verdict=ok' && ok "倒地后复活卷轴能拉起来，只扣当前级经验、等级不变" \
    || bad "卷轴在世界上不对：$(printf '%s' "$R" | tail -2 | detail)"
  SC="$("$DOTNET" "$CD" $CC --revive-scroll-state "$A" 2>&1 | utf8 | grep -m1 '^revive_scroll_state=')"
  printf '%s' "$SC" | grep -q 'level=1' && printf '%s' "$SC" | grep -q 'exp=900' \
    && ok "真库：卷轴扣 100 经验，等级仍是 1（跨场保留的冷却记在库里）" \
    || bad "卷轴落库不对：$SC"

  # 6) 世界服全程不结算技能；归属不加属性、不改税收、不转走矿脉。
  grep -qE 'skill_result|damage=|exp_settle' "$TMP/world.log" \
    && bad "世界服日志出现技能结算行" \
    || ok "世界服全程不结算这场技能（技术架构 5 节不回退）"
  grep -q 'world_boss_record=' "$TMP/world.log" \
    && ok "世界服收到事实值并写在入口上（只记名字）" || bad "世界服没收到归属事实值"
  R="$(post /world/gather "{\"character_id\":$A,\"node_id\":\"spirit_qiyun_1\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "false" ] \
    && [ "$(printf '%s' "$R" | jget reason)" = "spirit_stone_forbidden" ] \
    && ok "归属不加属性：个人采集灵石矿脉仍被拒（spirit_stone_forbidden）" \
    || bad "矿脉被归属打开了：$R"
fi

echo "== 7. 回归闸门 =="
if bash tools/check/preflight.sh >"$TMP/preflight.log" 2>&1; then
  ok "preflight.sh 第 4 道闸门绿"
else
  bad "preflight.sh 没过：$(tail -4 "$TMP/preflight.log" | detail)"
fi
if git diff --check >"$TMP/diffcheck.log" 2>&1; then
  ok "git diff --check 通过"
else
  bad "git diff --check 有空白错误：$(head -3 "$TMP/diffcheck.log" | detail)"
fi

finish "P3-5a（世界 Boss 进战斗服）"
