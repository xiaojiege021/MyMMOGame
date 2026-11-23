#!/usr/bin/env bash
# P3-3a1 · 40 对 40 进入战斗服战场（方案 12.3 / 15.1、技术架构第 24 / 49 行、
# 开发架构 11 节 P3-3、13 节 P3-3a1、14 节第 63 行）。
#   bash tools/check/check-p3-3a1.sh
#   MMO_P3_3A1_HAS_DB=1 MMO_GAMEDB='Host=...' bash ...   加真服真库端到端
# 完成标准：技能由战斗服结算 / 世界服不结算这场技能 / 从 40 对 40 起 / 装备全额生效。
set -uo pipefail
# 这台机设了代理，.NET 会把 127.0.0.1 的明文 HTTP/2 请求也送进代理；curl 段一律 --noproxy '*'。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
WD="$ROOT/src/World/bin/Release/net10.0/World.dll"
PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
CC="--config $ROOT/config/character.json"
CG="--config $ROOT/config/gateway.json"
CW="--config $ROOT/config/world.json"
HAS_DB="${MMO_P3_3A1_HAS_DB:-0}"
TMP="$(mktemp -d)"
PIDS=""
# Windows 上 dotnet 把 stdout 重定向到管道时走 GBK；中文输出按平台转码。
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
  for _ in $(seq 1 120); do
    local u; u="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$1" 2>/dev/null | tail -1 | sed 's/^listen=//')"
    [ -n "$u" ] && { printf '%s' "$u"; return 0; }
    sleep 0.1
  done
  return 1
}
reject_line() {
  curl -sS --noproxy '*' -m 5 -i -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
    -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' \
    "$1" 2>&1 | head -1 | grep -oE '^HTTP/[0-9.]+ [0-9]{3}' | awk '{print $2}'
}

echo "== 1. 编译 =="
for proj in src/Battle/Battle.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj \
            src/World/World.csproj tools/probe/BattleProbe/BattleProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 ${n}：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done

echo "== 2. 静态断言（结构保证，不写死当时的数字）=="
python3 - "$ROOT" >"$TMP/static.txt" 2>&1 <<'PY'
import json, os, sys, re
root = sys.argv[1]
def cfg(p): return json.load(open(os.path.join(root, p), encoding="utf-8"))
def src(p):
    """读一份可能被拆分（partial）过的源码：主文件 + 分片目录一起读（14.8 纪律第 8 条）。"""
    full = os.path.join(root, p)
    _parts = {"Server.cs": "Server", "Config.cs": "ConfigTables",
              "Encounter.cs": "Encounter", "Effects.cs": "Effects",
              "CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect",
              "WorldApi.cs": "Api/World", "SectRepository.cs": "Sect", "SocialGrpcService.cs": "Grpc", "WorldGrpcService.cs": "Grpc", "Program.cs": "Cli"}
    name = os.path.basename(p)
    if name in _parts:
        d = os.path.join(os.path.dirname(full), _parts[name])
        return "".join(open(x, encoding="utf-8").read() for x in
                       [full] + sorted(__import__("glob").glob(os.path.join(d, "*.cs"))))
    return open(full, encoding="utf-8").read()
def emit(k, c, i=""):
    print(("PASS " if c else "FAIL ") + k + ("" if c else "  " + str(i)[:160]))
sg, bf, bc = cfg("config/siege.json"), cfg("config/battlefield.json"), cfg("config/battle.json")
mode, eq, pw = sg.get("mode", {}), sg.get("equipment", {}), sg.get("power", {})
emit("siege_schema", sg.get("schema") == "siege-table/1", sg.get("schema"))
emit("siege_40v40", mode.get("team_size") == 40 and mode.get("teams") == 2
     and mode["team_size"] * mode["teams"] == 80, mode)
emit("siege_no_balance", not any(k in sg for k in
     ("balance", "ratio", "templates", "raise_low_only", "adjusted_power", "arena_match")))
emit("siege_no_tech_or_tax", not re.search(r'"(tax|sect|technology|reward)"', json.dumps(sg, ensure_ascii=False)))
emit("siege_equipment", all(isinstance(eq.get(k), int) and eq[k] >= 0 for k in
     ("attack_per_level", "hp_per_level", "mana_per_level", "heal_per_level")))
emit("siege_power_weights", sum(pw.get(k, 0) for k in
     ("attack_weight", "hp_weight", "mana_weight", "heal_weight")) > 0)
emit("siege_pending_note", "待定" in sg.get("note", "") and "40" in sg.get("note", ""))
emit("battlefield_unchanged", bf["mode"]["team_size"] == 10
     and abs(float(bf["balance"]["ratio"]) - 1.3) < 1e-9)
emit("battle_json_no_siege_key", "siege_table" not in bc,
     "老卡临时配置目录没有 siege.json，显式登记会让它们全起不来")
emit("world_no_skill_code", not any(re.search(r"SkillRequest|SkillResult|DamageOf|skill_settle|结算伤害", t)
     for f in os.listdir(os.path.join(root, "src", "World")) if f.endswith(".cs")
     for t in [src(os.path.join("src", "World", f))]))
emit("world_gather_still_forbidden", "spirit_stone_forbidden" in src("src/World/WorldGrpcService.cs"))
emit("gateway_forwards_query_not_skills", "BuildUpstreamUrl" in src("src/Gateway/BattleProxy.cs")
     and "SkillRequest" not in src("src/Gateway/WorldApi.cs")
     and "SkillRequest" not in src("src/Gateway/BattleProxy.cs"))
battle_all = "".join(src(os.path.join("src", "Battle", f))
                     for f in os.listdir(os.path.join(root, "src", "Battle")) if f.endswith(".cs"))
emit("battle_no_db", "Npgsql" not in battle_all and "insert into" not in battle_all.lower())
settle = src("src/Battle/Settlement.cs") + src("src/Battle/ExpSettlement.cs")
emit("siege_not_in_settlement", "siege" not in settle.lower())
table = src("src/Battle/SiegeTable.cs")
emit("siege_table_load", "static SiegeTable Load(string path)" in table
     and "Missing()" in table and "IsMissing" in table)
emit("siege_table_rejects_balance_keys", "不许出现" in table and "EnumerateObject" in table)
emit("siege_table_no_compression", "AdjustedPower" not in table and "Ratio" not in table
     and "Templates" not in table)
srv = src("src/Battle/Server.cs")
for key in ("QueryOrNull(query, \"siege\")", "siege_with_other_mode", "siege_table_missing",
            "siege_team_invalid", "siege_character_missing", "siege_full", "siege_not_started",
            "StartSiegeTeam", "_siegeWaits", "siege_open=match=", "balance=none"):
    emit("srv_" + key.replace('"', "").replace(" ", "_")[:28], key in srv)
emit("srv_old_battlefield_intact", "StartBattlefieldTeam" in srv
     and "battlefield_with_other_mode" in srv and "_fieldWaits" in srv)
# 只取城战开场方法体：不许出现战场的同队压缩调用，也不许出现最高战力。
body = srv.split("void StartSiegeTeam(")[-1].split("/// <summary>")[0]
emit("siege_start_no_compression", "AdjustedPower(" not in body and "teamMaxPower" not in body
     and "entry.K = 1.0" in body)
emit("siege_two_teams_one_encounter", 'var partyKey = "siege-" + matchId' in body
     and "table.Players" in body and "session={party.Encounter.SessionId}" in srv)
conf = src("src/Battle/Config.cs")
emit("config_wires_siege", "SiegeTablePath = siegePath" in conf and "Siege = siege" in conf
     and 'Resolve(dir, raw.SiegeTable, "siege.json")' in conf)
emit("config_siege_optional", "SiegeTable.Missing()" in conf and "siege_table 指的" in conf)
prog = src("src/Battle/Program.cs")
emit("program_readonly_entry", "--siege-table" in prog and "--siege-table-path" in prog
     and "players={table.Players} balance=none" in prog)
proc = src("src/Battle/BattleProcess.cs")
emit("process_startup_log", "siege_table=" in proc and "siege_players=" in proc
     and "siege=missing" in proc)
PY
while IFS= read -r line; do
  case "$line" in
    "PASS "*) ok "${line#PASS }" ;;
    "FAIL "*) bad "${line#FAIL }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 只读入口与坏配置 =="
SO="$("$DOTNET" "$BD" --siege-table 2>&1)"; SRC=$?
[ "$SRC" = 0 ] && printf '%s' "$SO" | grep -q 'team_size=40 teams=2 players=80 balance=none' \
  && ok "只读入口：40 对 40、players=80、balance=none" || bad "只读入口不对（rc=${SRC}）"
printf '%s' "$SO" | grep -q '^siege_equipment=attack_per_level=' && ok "只读入口：每级强化全额折算" || bad "折算没打出来"
printf '%s' "$SO" | grep -q '^siege_power=attack_weight=' && ok "只读入口：战力权重" || bad "权重没打出来"
mk_bad() {
  python3 - "$ROOT/config/siege.json" "$TMP/bad-$1.json" "$1" <<'PY'
import json, sys
src, dst, mode = sys.argv[1:4]
d = json.load(open(src, encoding="utf-8"))
if mode == "version": d["version"] = 0
elif mode == "schema": d["schema"] = "battlefield-table/1"
elif mode == "team_size": d["mode"]["team_size"] = 0
elif mode == "teams": d["mode"]["teams"] = 1
elif mode == "too_many": d["mode"]["team_size"] = 60
elif mode == "timeout": d["fill_timeout_ms"] = 60001
elif mode == "equipment": d["equipment"]["attack_per_level"] = -1
elif mode == "weight": d["power"] = dict.fromkeys(
    ("attack_weight", "hp_weight", "mana_weight", "heal_weight"), 0)
elif mode == "no_mode": d.pop("mode", None)
elif mode == "balance": d["balance"] = {"ratio": 1.3}
json.dump(d, open(dst, "w", encoding="utf-8"), ensure_ascii=False)
PY
}
for c in version schema team_size teams too_many timeout equipment weight no_mode balance; do
  mk_bad "$c"
  out="$("$DOTNET" "$BD" --siege-table-path "$TMP/bad-$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error='; then
    ok "坏配置 ${c}：config_error= 且非 0 退出"
  else
    bad "坏配置 ${c} 没挡住（rc=${rc}）：$(printf '%s' "$out" | utf8 | head -2)"
  fi
done

echo "== 4. 假服：模式互斥、缺表降级、老路径照跑 =="
mk_cfg() {
  python3 - "$ROOT" "$TMP" "$1" "$2" "$3" <<'PY'
import json, os, sys
root, tmp, name, with_table, upstream = sys.argv[1:6]
d = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
d["listen"] = "127.0.0.1:0"
d["settlement_dir"] = os.path.join(tmp, name + "-set")
d["character_dir"] = os.path.join(tmp, name + "-chr")
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json")):
    d[k] = os.path.join(root, "config", f)
if with_table == "yes":
    d["siege_table"] = os.path.join(root, "config", "siege.json")
else:
    d.pop("siege_table", None)
if upstream == "yes":
    d["exp_upstream"] = d["character_upstream"] = "http://127.0.0.1:62000"
json.dump(d, open(os.path.join(tmp, name + ".json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)
PY
}
mk_cfg noprof yes no
"$DOTNET" "$BD" --config "$TMP/noprof.json" >"$TMP/noprof.log" 2>&1 & PIDS="$PIDS $!"
U="$(listen_of "$TMP/noprof.log")"
[ -n "$U" ] && ok "假服就绪（有城战表、无角色服上游）" || bad "假服没起来"
WU="ws${U#http}"
check_reject() {
  local url="$1" reason="$2" label="$3" c
  c="$(reject_line "$U/battle?$url")"
  [ "$c" = "400" ] && grep -q "join_reject=$reason" "$TMP/noprof.log" \
    && ok "${label}：400 + $reason" || bad "$label 没拒（$c / ${reason}）"
}
check_reject 'siege=m1&team=a&char=1&class=warrior' battle_profile_missing \
  "没有角色库不编一份属性出来"
check_reject 'siege=m1&team=z&char=1&class=warrior' siege_team_invalid "队伍只认 a / b"
check_reject 'siege=m1&team=a&class=warrior' siege_character_missing "城战必须给角色编号"
for other in dungeon=abyss arena_mode=duel battlefield=m2; do
  check_reject "siege=m1&team=a&char=1&class=warrior&$other" siege_with_other_mode \
    "城战与 ${other%%=*} 互斥"
done
"$DOTNET" "$PROBE" --url "$WU/battle?class=warrior" --case open >"$TMP/plain.log" 2>&1
grep -q 'verdict=ok' "$TMP/plain.log" && ok "练习场不受城战表影响（--case open verdict=ok）" \
  || bad "普通练习场回退了：$(head -2 "$TMP/plain.log" | detail)"
mk_cfg notable no no
"$DOTNET" "$BD" --config "$TMP/notable.json" >"$TMP/notable.log" 2>&1 & PIDS="$PIDS $!"
U2="$(listen_of "$TMP/notable.log")"
[ -n "$U2" ] && grep -q 'siege=missing' "$TMP/notable.log" \
  && ok "缺城战表能起、日志标 siege=missing" || bad "缺表没降级"
C="$(reject_line "$U2/battle?siege=m1&team=a&char=1&class=warrior")"
[ "$C" = "400" ] && grep -q 'join_reject=siege_table_missing' "$TMP/notable.log" \
  && ok "缺表时城战一律拒：400 + siege_table_missing" || bad "缺表还放进去了（${C}）"
WU2="ws${U2#http}"
"$DOTNET" "$PROBE" --url "$WU2/battle?class=warrior" --case open >"$TMP/plain2.log" 2>&1
grep -q 'verdict=ok' "$TMP/plain2.log" && ok "缺表时老卡副本/练习场照跑" || bad "缺表把练习场挡住了"

echo "== 5. 真服真库（本节全部需要真服；跳过时只算静态证据）=="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真服真库段（MMO_P3_3A1_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1:3]
sg = json.load(open(os.path.join(root, "config", "siege.json"), encoding="utf-8"))
fast = dict(sg); fast["fill_timeout_ms"] = 300
small = dict(sg); small["fill_timeout_ms"] = 300; small["mode"] = dict(sg["mode"], team_size=1)
bf = json.load(open(os.path.join(root, "config", "battlefield.json"), encoding="utf-8"))
bf["fill_timeout_ms"] = 300
for name, d in (("siege-fast", fast), ("siege-small", small), ("bf-fast", bf)):
    json.dump(d, open(os.path.join(tmp, name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
def battle(name, siege):
    d = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
    d.update(listen="127.0.0.1:0", settlement_dir=os.path.join(tmp, name + "-set"),
             character_dir=os.path.join(tmp, name + "-chr"),
             exp_upstream="http://127.0.0.1:62000", character_upstream="http://127.0.0.1:62000")
    for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
                 ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json")):
        d[k] = os.path.join(root, "config", f)
    d["siege_table"] = os.path.join(tmp, siege + ".json")
    d["battlefield_table"] = os.path.join(tmp, "bf-fast.json")
    json.dump(d, open(os.path.join(tmp, name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
battle("battle-real", "siege-fast")
battle("battle-small", "siege-small")
PY
  for f in account character currency inventory; do
    "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/$f.sql" $CG >"$TMP/mig-$f.log" 2>&1 || true
  done
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"; wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$BD" --config "$TMP/battle-real.json" >"$TMP/battle.log" 2>&1 & PIDS="$PIDS $!"
  U="$(listen_of "$TMP/battle.log")"
  WU="ws${U#http}"
  BPORT="${U##*:}"
  python3 - "$ROOT" "$TMP" "$BPORT" <<'PY'
import json, os, sys
root, tmp, port = sys.argv[1:4]
d = json.load(open(os.path.join(root, "config", "gateway.json"), encoding="utf-8"))
d["battle_upstream"] = "ws://127.0.0.1:" + port + "/battle"
json.dump(d, open(os.path.join(tmp, "gateway-real.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
  GR="--config $TMP/gateway-real.json"
  "$DOTNET" "$GD" $GR >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  "$DOTNET" "$WD" $CW --gather-config "$ROOT/config/gather.json" >"$TMP/world.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/world.log" '^ready' "世界服" || true
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
      -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p331_$$"; PW="P331-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 $GR >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" | jget token 2>/dev/null)"
  [ -n "$TOK" ] && ok "真服：登录拿到令牌（账号/口令只在内存）" || bad "真服：登录没拿到令牌"
  mkchar() {
    local cls="$1" o id
    for _ in $(seq 1 10); do
      o="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
        -H 'Content-Type: application/json' \
        -d "{\"name\":\"p331_$cls$RANDOM\",\"race\":\"mai\",\"class_id\":\"$cls\"}" 2>&1)"
      id="$(printf '%s' "$o" | jget character_id 2>/dev/null)"
      [ -n "$id" ] && { printf '%s' "$id"; return 0; }; sleep 0.5
    done
  }
  A="$(mkchar warrior)"; B="$(mkchar priest)"; C="$(mkchar warrior)"
  [ -n "$A" ] && [ -n "$B" ] && [ -n "$C" ] && ok "真服：造三个角色（$A / $B / ${C}）" || bad "真服：角色没建出来"
  curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/inventory/add?token=$TOK" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$A,\"slot\":1,\"item_id\":\"sword_trial\",\"count\":1}" >/dev/null
  "$DOTNET" "$CD" --raw-enhance-write "$A" 1 12 $CC >"$TMP/ew.log" 2>&1 || true
  grep -q '^raw_enhance=' "$TMP/ew.log" && ok "真服：A 的第 1 格强化写成 12 级" || bad "真服：强化没写进去"
  BOSS="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["boss"]["id"])' "$ROOT/config/battle.json")"
  # 两队同一 match：正本 40v40 的时限调短到 300ms，按已到的人开场。
  # 后台进程必须在当前 shell 里取 $!；用命令替换取 pid 会落进子 shell，wait 等不到它。
  M="p331a-$$-$(date +%s)"
  "$DOTNET" "$PROBE" --url "$WU/battle?siege=$M&team=a&char=$A&class=warrior" \
    --case battlefield_open --hold-ms 1500 >"$TMP/sa.log" 2>&1 & P1=$!
  "$DOTNET" "$PROBE" --url "$WU/battle?siege=$M&team=b&char=$B&class=warrior" \
    --case battlefield_open --hold-ms 1500 >"$TMP/sb.log" 2>&1 & P2=$!
  wait "$P1" "$P2" 2>/dev/null || true
  grep -q 'verdict=ok' "$TMP/sa.log" && grep -q 'verdict=ok' "$TMP/sb.log" \
    && ok "真服：两队各一人按超时开场（同一场 40v40 实例）" \
    || { bad "真服：有人没进这场"; head -3 "$TMP/sa.log" "$TMP/sb.log" | detail; }
  python3 - "$TMP/battle.log" "$M" >"$TMP/siege-parse.txt" 2>&1 <<'PY'
import re, sys
log, match = sys.argv[1:3]
rows = [l.strip() for l in open(log, encoding="utf-8") if f"siege_open=match={match} " in l]
def num(l, k):
    m = re.search(r" " + k + r"=([0-9]+)", l)
    return int(m.group(1)) if m else -1
print("PASS siege_open_two_teams" if len(rows) == 2 else "FAIL siege_open_two_teams rows=%d" % len(rows))
if len(rows) == 2:
    teams = sorted(re.search(r" team=(\d+)", l).group(1) for l in rows)
    sessions = [re.search(r" session=(\d+)", l).group(1) for l in rows]
    print("PASS two_teams_same_session" if teams == ["0", "1"] and sessions[0] == sessions[1] else
          "FAIL two_teams_same_session teams=%s sessions=%s" % (teams, sessions))
    print("PASS full_equipment" if all("k=1 " in l or "k=1.000" in l for l in rows)
          and all("balance=none" in l for l in rows)
          and all(num(l, "power") == num(l, "effective_power") for l in rows) else
          "FAIL full_equipment " + " | ".join(rows)[:200])
    print("PASS scale_40v40" if all("team_size=40 teams=2 players=80" in l for l in rows) else
          "FAIL scale_40v40")
    powers = [num(l, "power") for l in rows]
    print("PASS equipment_changes_power" if len(set(powers)) == 2 and min(powers) > 0 else
          "FAIL equipment_changes_power powers=%s" % powers)
PY
  while IFS= read -r line; do
    case "$line" in "PASS "*) ok "真服：${line#PASS }" ;; "FAIL "*) bad "真服：${line#FAIL }" ;; esac
  done < "$TMP/siege-parse.txt"
  # 技能结算：1002 裂碑射程 30000，出生 6000 直接命中；damage 由战斗服 Encounter 算。
  "$DOTNET" "$PROBE" --url "$WU/battle?siege=cast&team=a&char=$A&class=warrior" \
    --case damage --boss "$BOSS" --ranged 1002 >"$TMP/cast.log" 2>&1
  DMG="$(grep -oE 'damage=[0-9]+' "$TMP/cast.log" | head -1)"
  grep -q 'verdict=ok' "$TMP/cast.log" && [ -n "$DMG" ] \
    && ok "真服：城战技能由战斗服结算（--case damage → ${DMG}，含目标血量与冷却）" \
    || bad "真服：城战技能没结算：$(head -2 "$TMP/cast.log" | detail)"
  # 网关 /battle 原样透传 query：经网关再进一次同一场。
  "$DOTNET" "$PROBE" --url "ws://127.0.0.1:61000/battle?token=$TOK&siege=gw&team=a&char=$A&class=warrior" \
    --case battlefield_open --hold-ms 500 >"$TMP/gwsiege.log" 2>&1
  grep -q 'verdict=ok' "$TMP/gwsiege.log" && grep -q 'proxy_open' "$TMP/gw.log" \
    && ok "真服：网关 /battle 透传 siege/team/char 后由战斗服开场" || bad "真服：网关这条路没走通"
  if grep -qE 'damage=|skill_result|skill_settle|结算伤害' "$TMP/world.log"; then
    bad "真服：世界服日志出现技能结算行"
  else
    ok "真服：城战期间世界服没有任何技能/伤害结算行"
  fi
  # 老路径不回退：战场仍走 1.3 倍压缩那条链，练习场仍可开。
  "$DOTNET" "$PROBE" --url "$WU/battle?battlefield=old&team=a&char=$A&class=warrior" \
    --case battlefield_open --hold-ms 500 >"$TMP/oldbf.log" 2>&1
  grep -q 'verdict=ok' "$TMP/oldbf.log" && grep -q 'battlefield_open=match=old ' "$TMP/battle.log" \
    && ok "真服：战场 1.3 倍那条链没被城战吃掉" || bad "真服：战场路径回退了"
  # 满员：临时表把 team_size 调成 1（players=2），第 3 人必须被 siege_full 拒。
  "$DOTNET" "$BD" --config "$TMP/battle-small.json" >"$TMP/battle-small.log" 2>&1 & PIDS="$PIDS $!"
  U3="$(listen_of "$TMP/battle-small.log")"; WU3="ws${U3#http}"
  F="p331f-$$-$(date +%s)"
  "$DOTNET" "$PROBE" --url "$WU3/battle?siege=$F&team=a&char=$A&class=warrior" \
    --case battlefield_open --hold-ms 3000 >"$TMP/f1.log" 2>&1 & Q1=$!
  sleep 0.7
  "$DOTNET" "$PROBE" --url "$WU3/battle?siege=$F&team=b&char=$B&class=warrior" \
    --case battlefield_open --hold-ms 3000 >"$TMP/f2.log" 2>&1 & Q2=$!
  sleep 0.7
  "$DOTNET" "$PROBE" --url "$WU3/battle?siege=$F&team=a&char=$C&class=warrior" \
    --case battlefield_open --hold-ms 300 >"$TMP/f3.log" 2>&1
  grep -q 'join_reject=siege_full' "$TMP/battle-small.log" \
    && ok "真服：players 满额后的下一条连接被 siege_full 拒（未生效数值 0）" \
    || bad "真服：满员没挡住：$(grep -E 'join_reject=' "$TMP/battle-small.log" | tail -3 | detail)"
  wait "$Q1" "$Q2" 2>/dev/null || true
  "$DOTNET" "$CD" --raw-enhance-write "$A" 1 0 $CC >"$TMP/ew0.log" 2>&1 || true
  [ "$("$DOTNET" "$CD" --enhance-level "$A" 1 $CC 2>&1 | grep -oE 'level=[0-9]+' | head -1)" = "level=0" ] \
    && ok "真服：强化等级已还原" || bad "真服：强化等级没还原"
fi

finish "P3-3a1（40 对 40 城战进场）"
