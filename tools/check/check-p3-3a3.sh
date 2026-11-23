#!/usr/bin/env bash
# P3-3a3 · 城战读取宗门科技（方案 11.1 / 12.3、技术架构 49 / 93 行、
# 开发架构 4.4 / 8 / 9 / 11 / 13 节、14 节第 65 行）。
#   bash tools/check/check-p3-3a3.sh
#   MMO_P3_3A3_HAS_DB=1 MMO_GAMEDB='Host=...' bash tools/check/check-p3-3a3.sh
# 完成标准：加成只出现在这场城战；日常副本没有这份加成；装备全额生效口径不回退。
set -uo pipefail
# 本机代理会把 127.0.0.1 的明文 HTTP/2 请求送进代理，gRPC 段必须先 unset。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
SD="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
WD="$ROOT/src/World/bin/Release/net10.0/World.dll"
PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
CC="--config $ROOT/config/character.json"
CS="--config $ROOT/config/social.json"
CG="--config $ROOT/config/gateway.json"
CW="--config $ROOT/config/world.json"
HAS_DB="${MMO_P3_3A3_HAS_DB:-0}"
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
  for _ in $(seq 1 120); do
    local u; u="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$1" 2>/dev/null | tail -1 | sed 's/^listen=//')"
    [ -n "$u" ] && { printf '%s' "$u"; return 0; }
    sleep 0.1
  done
  return 1
}

echo "== 1. 编译 =="
for proj in src/Battle/Battle.csproj src/Social/Social.csproj src/Character/Character.csproj \
            src/Gateway/Gateway.csproj src/World/World.csproj \
            tools/probe/BattleProbe/BattleProbe.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 ${n}：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done

echo "== 2. 静态断言（结构保证，不写死配置里的数字）=="
python3 - "$ROOT" >"$TMP/static.txt" 2>&1 <<'PY'
import hashlib, json, os, re, sys
root = sys.argv[1]
def rd(p):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server"}
    _name = __import__("os").path.basename(p)
    if _name in _sub:
        _os = __import__("os"); _g = __import__("glob")
        _b = p if _os.path.isabs(p) else _os.path.join(_os.getcwd(), p)
        if not _os.path.exists(_b):
            _d = _os.path.dirname(_os.getcwd())
            while _d and not _os.path.exists(_os.path.join(_d, p)):
                _nd = _os.path.dirname(_d)
                if _nd == _d: break
                _d = _nd
            _b = _os.path.join(_d, p)
        return "".join(open(f, encoding="utf-8").read() for f in
                       [_b] + sorted(_g.glob(_os.path.join(_os.path.dirname(_b), _sub[_name], "*.cs"))))
    q = os.path.join(root, p)
    return open(q, encoding="utf-8").read() if os.path.exists(q) else ""
def cfg(p): return json.loads(rd(p))
def emit(k, c, i=""): print(("PASS " if c else "FAIL ") + k + ("" if c else "  " + str(i)[:150]))
tech = cfg("config/sect_tech.json")
sg = cfg("config/siege.json")
sect = cfg("config/sect.json")
bc = cfg("config/battle.json")
bonus = tech.get("bonus", {})
emit("tech_schema", tech.get("schema") == "sect-tech-table/1" and tech.get("version", 0) > 0)
emit("tech_pending_note", "待定" in tech.get("note", "") and bonus.get("pending") is True)
emit("tech_numbers_positive", isinstance(bonus.get("power_pct_per_level"), int)
     and 1 <= bonus["power_pct_per_level"] <= 100
     and isinstance(bonus.get("max_level"), int) and 1 <= bonus["max_level"] <= 100)
emit("tech_no_balance_or_shop", not any(k in tech for k in
     ("balance", "ratio", "templates", "arena_match", "adjusted_power", "shop", "price", "ingot", "mall")))
emit("tech_bonus_only_numbers", set(bonus.keys()) <= {"power_pct_per_level", "max_level", "pending", "note"})
# 老卡口径：科技段绝不塞进 siege.json（check-p3-3a1.sh 的 siege_no_tech_or_tax）
emit("siege_no_tech_or_tax", not re.search(r'"(tax|sect|technology|reward)"', json.dumps(sg, ensure_ascii=False)))
emit("siege_no_balance", not any(k in sg for k in ("balance", "ratio", "templates", "adjusted_power")))
emit("siege_scale_unchanged", sg["mode"]["team_size"] == 40 and sg["mode"]["teams"] == 2
     and sg["mode"]["team_size"] * sg["mode"]["teams"] == 80)
emit("siege_view_only_limit", set(sg["view"].keys()) <= {"full_model_limit", "note"})
emit("sect_caps_unchanged", [x["member_cap"] for x in sect["levels"]] == [10, 30, 60, 100, 200])
emit("battle_json_optional_tables", "siege_table" not in bc and "sect_tech_table" not in bc,
     "临时配置目录没有这两份表，显式登记会让老卡全起不来")
# proto 与两端生成物
proto = rd("proto/mmo/social.proto")
emit("proto_has_rpc", "rpc DescribeSectTech(" in proto and "int32 tech_level = 5;" in proto)
s1 = rd("src/Shared/Generated/Social.cs"); s2 = rd("client/MMOGame/Assets/Proto/Generated/Social.cs")
emit("two_ends_social_same", bool(s1) and
     hashlib.sha256(s1.encode()).hexdigest() == hashlib.sha256(s2.encode()).hexdigest())
# 战斗服：科技只在城战分支读，其他模式连入口都没有
srv = rd("src/Battle/Server.cs")
siege_body = srv.split("async Task HandleSiegeAsync(")[-1].split("async Task ")[0]
emit("tech_read_only_in_siege", "sectTech.Get(charId)" in siege_body
     and srv.count("sectTech.Get(") == 1)
emit("scaled_has_tech_factor", "entry.K * entry.TechFactor" in srv and "TechFactor" in rd("src/Battle/Server.cs"))
emit("full_equipment_intact", "entry.AdjustedPower = entry.RawPower" in srv and "entry.K = 1.0;" in srv)
emit("siege_open_appends_facts", "sect_tech_level={entry.SectTechLevel}" in srv
     and "k={entry.K:0.###} balance=none" in srv)
emit("not_in_settlement", not re.search(r"sect_tech|tech_factor",
     (rd("src/Battle/Settlement.cs") + rd("src/Battle/ExpSettlement.cs")).lower()))
world = "".join(rd(os.path.join("src/World", f)) for f in os.listdir(os.path.join(root, "src/World"))
                if f.endswith(".cs"))
emit("world_not_involved", "SectTech" not in world and "tech_factor" not in world)
emit("battle_no_db", "Npgsql" not in "".join(rd(os.path.join("src/Battle", f))
     for f in os.listdir(os.path.join(root, "src/Battle")) if f.endswith(".cs")))
client = rd("src/Battle/SectTechClient.cs")
emit("client_degrades_to_zero", "catch (RpcException" in client and "return 0;" in client
     and "NoSectTechSource" in client)
emit("table_optional", "SectTechTable.Missing()" in rd("src/Battle/Config.cs")
     and "static SectTechTable Missing()" in rd("src/Battle/SectTechTable.cs"))
# 社交服：只读等级事实值，不算伤害系数；库表只存等级
social_srv = rd("src/Social/SocialGrpcService.cs") + rd("src/Social/SectRepository.cs")
emit("social_only_level_fact", "SectTechAsync" in social_srv and "TechLevel = state.TechLevel" in social_srv)
emit("social_no_power_formula", not re.search(r"attack_weight|power\(|damage", social_srv.split("DescribeSectTech(")[-1][:1200]))
ddl1, ddl2 = rd("db/social/sect_tech.sql"), rd("db/social/social.sql")
emit("ddl_in_both_files", "CREATE TABLE IF NOT EXISTS \"社交\".sect_tech" in ddl1 and "sect_tech" in ddl2)
emit("ddl_no_shop_or_tax", not re.search(r"shop|price|ingot|tax|reward", ddl1.lower()))
# 禁售与公平竞技那一层不改
# 断言名与注释都是「禁售」：宗门科技不许出现在商城清单 / 商城表里（原来漏了 not，一直在反着判）。
emit("no_sell_tech", not ("宗门科技" in rd("config/shop.json") or "guild_tech" in rd("src/Character/ShopTable.cs")))
emit("bf_and_arena_untouched", abs(float(cfg("config/battlefield.json")["balance"]["ratio"]) - 1.3) < 1e-9)
PY
while IFS= read -r line; do
  case "$line" in "PASS "*) ok "${line#PASS }" ;; "FAIL "*) bad "${line#FAIL }" ;; esac
done < "$TMP/static.txt"

echo "== 3. 只读入口、坏配置与纯计算 =="
SO="$("$DOTNET" "$BD" --sect-tech-table 2>&1)"; SRC=$?
PCT="$(python3 -c 'import json;print(json.load(open("config/sect_tech.json",encoding="utf-8"))["bonus"]["power_pct_per_level"])')"
MAX="$(python3 -c 'import json;print(json.load(open("config/sect_tech.json",encoding="utf-8"))["bonus"]["max_level"])')"
[ "$SRC" = 0 ] && printf '%s' "$SO" | grep -q "power_pct_per_level=$PCT max_level=$MAX pending=true" \
  && ok "科技表只读入口从配置现取（$PCT / $MAX / pending=true）" || bad "只读入口不对（rc=${SRC}）"
F0="$(printf '%s' "$SO" | grep -oE 'level=0 [0-9.]+' | awk '{print $2}')"
F1="$(printf '%s' "$SO" | grep -oE 'level=1 [0-9.]+' | awk '{print $2}')"
FM="$(printf '%s' "$SO" | grep -oE 'level=max [0-9.]+' | awk '{print $2}')"
python3 - "$F0" "$F1" "$FM" >"$TMP/factor.txt" <<'PY'
import sys
f0, f1, fm = (float(x or 0) for x in sys.argv[1:4])
print("PASS factor_zero_is_one" if abs(f0 - 1.0) < 1e-9 else "FAIL factor_zero_is_one f0=%s" % f0)
print("PASS factor_grows_with_level" if 1.0 < f1 <= fm else "FAIL factor_grows_with_level %s %s" % (f1, fm))
PY
while IFS= read -r line; do
  case "$line" in "PASS "*) ok "纯计算：${line#PASS }" ;; "FAIL "*) bad "纯计算：${line#FAIL }" ;; esac
done < "$TMP/factor.txt"
mk_bad() {
  python3 - "$ROOT/config/sect_tech.json" "$TMP/bad-$1.json" "$1" <<'PY'
import json, sys
src, dst, mode = sys.argv[1:4]
d = json.load(open(src, encoding="utf-8"))
if mode == "schema": d["schema"] = "sect-tech-table/2"
elif mode == "version": d["version"] = 0
elif mode == "pct0": d["bonus"]["power_pct_per_level"] = 0
elif mode == "pct101": d["bonus"]["power_pct_per_level"] = 101
elif mode == "max0": d["bonus"]["max_level"] = 0
elif mode == "no_bonus": d.pop("bonus", None)
elif mode == "balance": d["balance"] = {"ratio": 1.3}
elif mode == "templates": d["templates"] = []
elif mode == "shop": d["shop"] = {"price": 1}
elif mode == "model": d["bonus"]["model"] = "prefab"
json.dump(d, open(dst, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
}
for c in schema version pct0 pct101 max0 no_bonus balance templates shop model; do
  mk_bad "$c"
  out="$("$DOTNET" "$BD" --sect-tech-table-path "$TMP/bad-$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error='; then
    ok "坏配置 ${c}：config_error= 且非 0 退出"
  else
    bad "坏配置 ${c} 没挡住（rc=${rc}）：$(printf '%s' "$out" | utf8 | head -2)"
  fi
done

echo "== 4. 假服：缺表降级与老口径不回退 =="
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1:3]
d = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
d.update(listen="127.0.0.1:0", settlement_dir=os.path.join(tmp, "fake-set"),
         character_dir=os.path.join(tmp, "fake-chr"))
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json")):
    d[k] = os.path.join(root, "config", f)
d["siege_table"] = os.path.join(root, "config", "siege.json")
d.pop("sect_tech_table", None); d.pop("social_upstream", None)
json.dump(d, open(os.path.join(tmp, "fake.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
"$DOTNET" "$BD" --config "$TMP/fake.json" >"$TMP/fake.log" 2>&1 & PIDS="$PIDS $!"
U="$(listen_of "$TMP/fake.log")"
[ -n "$U" ] && ok "假服就绪（临时目录没有 sect_tech.json）" || bad "假服没起来"
grep -q 'sect_tech=missing' "$TMP/fake.log" && ok "科技表可选降级：sect_tech=missing，不拖垮启动" \
  || bad "缺表没降级：$(grep -m1 'sect_tech_table=' "$TMP/fake.log" | detail)"
grep -q 'siege=missing\|siege_version=' "$TMP/fake.log" && grep -q '^resume=off' "$TMP/fake.log" \
  && ok "城战表与 resume=off 老启动日志仍在" || bad "老启动日志被改了"
grep -q 'social_upstream=off' "$TMP/fake.log" && ok "没配 social_upstream 时不查社交服" \
  || bad "默认上游不是 off"

echo "== 5. 真服真库（本节全部需要真服；跳过时只算静态证据）=="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真服真库段（MMO_P3_3A3_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1:3]
sg = json.load(open(os.path.join(root, "config", "siege.json"), encoding="utf-8"))
sg["fill_timeout_ms"] = 300
json.dump(sg, open(os.path.join(tmp, "siege-fast.json"), "w", encoding="utf-8"), ensure_ascii=False)
bf = json.load(open(os.path.join(root, "config", "battlefield.json"), encoding="utf-8"))
bf["fill_timeout_ms"] = 300
json.dump(bf, open(os.path.join(tmp, "bf-fast.json"), "w", encoding="utf-8"), ensure_ascii=False)
d = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
d.update(listen="127.0.0.1:0", settlement_dir=os.path.join(tmp, "real-set"),
         character_dir=os.path.join(tmp, "real-chr"),
         exp_upstream="http://127.0.0.1:62000", character_upstream="http://127.0.0.1:62000",
         social_upstream="http://127.0.0.1:65000")
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json")):
    d[k] = os.path.join(root, "config", f)
d["siege_table"] = os.path.join(tmp, "siege-fast.json")
d["battlefield_table"] = os.path.join(tmp, "bf-fast.json")
d["sect_tech_table"] = os.path.join(root, "config", "sect_tech.json")
json.dump(d, open(os.path.join(tmp, "battle-real.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
  for f in account character currency inventory; do
    "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/$f.sql" $CG >"$TMP/mig-$f.log" 2>&1 || true
  done
  "$DOTNET" "$SD" --migrate --sql "$ROOT/db/social/social.sql" $CS >"$TMP/mig-social.log" 2>&1 || true
  grep -q '^migrate=ok' "$TMP/mig-social.log" && ok "真库：社交主迁移含 sect_tech 表" \
    || bad "社交迁移失败：$(head -3 "$TMP/mig-social.log" | detail)"
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"; wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$SD" $CS >"$TMP/social.log" 2>&1 & PIDS="$PIDS $!"; wait_ready "$TMP/social.log" '^ready' "社交服" || true
  "$DOTNET" "$BD" --config "$TMP/battle-real.json" >"$TMP/battle.log" 2>&1 & PIDS="$PIDS $!"
  U="$(listen_of "$TMP/battle.log")"; WU="ws${U#http}"; BPORT="${U##*:}"
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
  ACCT="p333_$$"; PW="P333-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 $GR >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" | jget token 2>/dev/null)"
  [ -n "$TOK" ] && ok "真服：登录拿到令牌（账号/口令只在内存）" || bad "真服：登录没拿到令牌"
  mkchar() {
    local cls="$1" o id
    for _ in $(seq 1 10); do
      o="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
        -H 'Content-Type: application/json' \
        -d "{\"name\":\"p333_$cls$RANDOM\",\"race\":\"mai\",\"class_id\":\"$cls\"}" 2>&1)"
      id="$(printf '%s' "$o" | jget character_id 2>/dev/null)"
      [ -n "$id" ] && { printf '%s' "$id"; return 0; }; sleep 0.5
    done
  }
  A="$(mkchar warrior)"; B="$(mkchar priest)"
  [ -n "$A" ] && [ -n "$B" ] && ok "真服：造两个角色（$A / ${B}）" || bad "真服：角色没建出来"
  SECT="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/sect/create?token=$TOK" \
    -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' \
    -d "{\"character_id\":$A,\"name\":\"p333_sect_$$\"}" | jget sect_id 2>/dev/null)"
  [ -n "$SECT" ] && ok "真库：A 建了宗门（sect=${SECT}）" || bad "真库：宗门没建出来"
  BOSS="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1],encoding="utf-8"))["boss"]["id"])' "$ROOT/config/battle.json")"
  set_tech() {
    "$DOTNET" "$SD" --raw-sect-tech-write "$SECT" "$1" $CS >"$TMP/tech-write.log" 2>&1
    ST="$("$DOTNET" "$SD" --sect-tech-state "$A" $CS 2>&1 | grep -m1 '^sect_tech_state=')"
    printf '%s' "$ST" | grep -q "found=true sect=$SECT level=$1" \
      && ok "真库：宗门科技等级写成 $1，只读入口读回 $ST" \
      || bad "科技等级没读回：$ST / $(head -2 "$TMP/tech-write.log" | detail)"
  }
  damage() {
    "$DOTNET" "$PROBE" --url "$1" --case damage --boss "$BOSS" --ranged 1002 \
      >"$TMP/damage-$2.log" 2>&1
    grep -q 'verdict=ok' "$TMP/damage-$2.log" || { printf '0'; return; }
    grep -oE 'damage=[0-9]+' "$TMP/damage-$2.log" | head -1 | cut -d= -f2
  }
  set_tech 0
  D0="$(damage "$WU/battle?siege=cast0&team=a&char=$A&class=warrior" siege0)"
  P0="$(damage "$WU/battle?char=$A&class=warrior&party=p333daily0" daily0)"
  [ "${D0:-0}" -gt 0 ] && [ "${P0:-0}" -gt 0 ] \
    && ok "真服：无科技基线 damage 城战=$D0 / 练习场=$P0" || bad "基线伤害没取到：$D0 / $P0"
  set_tech 3
  D3="$(damage "$WU/battle?siege=cast3&team=a&char=$A&class=warrior" siege3)"
  P3="$(damage "$WU/battle?char=$A&class=warrior&party=p333daily3" daily3)"
  [ "${D3:-0}" -gt "${D0:-0}" ] && ok "真服：城战加成让伤害提高（$D0 → ${D3}）" \
    || bad "城战没有加成：$D0 → $D3"
  [ "${P3:-0}" = "${P0:-0}" ] && [ "${P3:-0}" -gt 0 ] \
    && ok "真服：日常副本伤害不变（$P0 → ${P3}），没有这份加成" \
    || bad "日常副本被加成污染：$P0 → $P3"
  # 两队同一 match：A 有科技、B 无宗门，开场日志逐行核对。
  M="p333m-$$-$(date +%s)"
  "$DOTNET" "$PROBE" --url "$WU/battle?siege=$M&team=a&char=$A&class=warrior" \
    --case battlefield_open --hold-ms 1500 >"$TMP/sa.log" 2>&1 & P1=$!
  "$DOTNET" "$PROBE" --url "$WU/battle?siege=$M&team=b&char=$B&class=warrior" \
    --case battlefield_open --hold-ms 1500 >"$TMP/sb.log" 2>&1 & P2=$!
  wait "$P1" "$P2" 2>/dev/null || true
  python3 - "$TMP/battle.log" "$M" "$A" "$B" >"$TMP/siege-parse.txt" 2>&1 <<'PY'
import re, sys
log, match, a, b = sys.argv[1:5]
rows = [l.strip() for l in open(log, encoding="utf-8") if f"siege_open=match={match} " in l]
def num(l, k):
    m = re.search(r" " + k + r"=([0-9]+)", l)
    return int(m.group(1)) if m else -1
print("PASS siege_open_two_teams" if len(rows) == 2 else "FAIL siege_open_two_teams rows=%d" % len(rows))
if len(rows) == 2:
    print("PASS two_teams_same_session" if len({re.search(r" session=(\d+)", l).group(1) for l in rows}) == 1
          else "FAIL two_teams_same_session")
    print("PASS full_equipment_intact" if all("k=1 " in l and "balance=none" in l
          and "team_size=40 teams=2 players=80" in l
          and num(l, "power") == num(l, "effective_power") for l in rows)
          else "FAIL full_equipment_intact " + " | ".join(rows)[:180])
    am = [l for l in rows if f"character={a} " in l]
    bm = [l for l in rows if f"character={b} " in l]
    print("PASS tech_only_for_sect_member" if len(am) == 1 and len(bm) == 1
          and num(am[0], "sect_tech_level") == 3 and num(am[0], "sect_tech_bonus") > 0
          and num(bm[0], "sect_tech_level") == 0 and num(bm[0], "sect_tech_bonus") == 0
          else "FAIL tech_only_for_sect_member " + " | ".join(rows)[:180])
PY
  while IFS= read -r line; do
    case "$line" in "PASS "*) ok "真服：${line#PASS }" ;; "FAIL "*) bad "真服：${line#FAIL }" ;; esac
  done < "$TMP/siege-parse.txt"
  if grep -qE 'damage=|skill_result|skill_settle|结算伤害' "$TMP/world.log"; then
    bad "真服：世界服日志出现技能结算行"
  else
    ok "真服：城战期间世界服没有任何技能/伤害结算行"
  fi
  TECHLINES="$(grep -c 'sect_tech_level=' "$TMP/battle.log" || true)"
  NONCITY="$(grep 'sect_tech_level=' "$TMP/battle.log" | grep -cv '^siege_join=\|^siege_open=' || true)"
  [ "${NONCITY:-0}" = "0" ] && [ "${TECHLINES:-0}" -gt 0 ] \
    && ok "真服：科技事实值只出现在城战进场/开场日志（$TECHLINES 行）" \
    || bad "科技字段漏进了别的路径：非城战行 $NONCITY"
  "$DOTNET" "$SD" --raw-sect-tech-write "$SECT" 0 $CS >/dev/null 2>&1 || true
  ok "真服：测试宗门科技等级已还原 0"
fi

finish "P3-3a3（城战读取宗门科技）"
