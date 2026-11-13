#!/usr/bin/env bash
# P2-3b 战场把同队战力压进 1.3 倍（方案 12.3 三层战力；开发架构 11 节 P2-3、14 节第 58 行）。
#   bash tools/check/check-p2-3b.sh                  编译+静态+只读+坏表+假服
#   MMO_P2_3B_HAS_DB=1 bash ...                      加真库段（造两个角色，验同队压缩）
# 本卡口径：战场先让装备生效（当前只有强化这一条数值线），再把同队战力差压进 1.3 倍；
# 只抬低不削高；跨服只交换报名与结果，两个服的角色库不合并。
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
CC="--config $ROOT/config/character.json"
CG="--config $ROOT/config/gateway.json"
HAS_DB="${MMO_P2_3B_HAS_DB:-0}"
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
import json, os, sys
root, out = sys.argv[1], sys.argv[2]
o = open(out, "w", encoding="utf-8")

def R(k, c, i=""):
    o.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")

def rd(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def js(p):
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}

bf = js(os.path.join(root, "config", "battlefield.json"))
mode = bf.get("mode", {})
bal = bf.get("balance", {})
eq = bf.get("equipment", {})
pw = bf.get("power", {})

R("bf_schema", bf.get("schema") == "battlefield-table/1", str(bf.get("schema")))
R("bf_10v10", mode.get("team_size") == 10 and mode.get("teams") == 2, str(mode))
R("bf_ratio", abs(float(bal.get("ratio", 0)) - 1.3) < 1e-9, str(bal.get("ratio")))
R("bf_fill_timeout", isinstance(bf.get("fill_timeout_ms"), int) and bf["fill_timeout_ms"] >= 0)
R("bf_equipment", all(isinstance(eq.get(k), int) and eq[k] >= 0
                      for k in ("attack_per_level", "hp_per_level", "mana_per_level", "heal_per_level")))
R("bf_power_weights", sum(pw.get(k, 0) for k in
                          ("attack_weight", "hp_weight", "mana_weight", "heal_weight")) > 0, str(pw))

table = rd(os.path.join(root, "src", "Battle", "BattlefieldTable.cs"))
R("bf_table_load", "static BattlefieldTable Load(string path)" in table)
R("bf_adjusted_power", "public long AdjustedPower(long power, long teamMaxPower)" in table
  and "teamMaxPower / Ratio" in table)
R("bf_power_fn", "public long Power(long attack, long hp, long mana, long heal)" in table)

enc = rd(os.path.join(root, "src", "Battle", "Encounter.cs"))
R("member_combat_first", "Combat?.Attack ?? Template?.Attack" in enc
  and "combat?.HpMax ?? arena?.HpMax" in enc)
R("combat_stats_record", "public sealed record CombatStats(" in enc)

srv = rd(os.path.join(root, "src", "Battle", "Server.cs"))
R("server_battlefield_branch", 'QueryOrNull(query, "battlefield")' in srv)
R("server_rejects_table_missing", "battlefield_table_missing" in srv)
R("server_rejects_team", "battlefield_team_invalid" in srv)
R("server_rejects_profile", "battle_profile_missing" in srv)
R("server_rejects_mixed", "battlefield_with_other_mode" in srv)
R("server_waits_for_team", "_fieldWaits" in srv and "StartBattlefieldTeam" in srv)
R("server_logs_power", "adjusted_power=" in srv and "team_max_power=" in srv)

# 战场不写库、不发经验：战斗服根本没有数据库连接，结算仍走角色服既有接口。
battle_src = "".join(rd(os.path.join(root, "src", "Battle", f))
                     for f in os.listdir(os.path.join(root, "src", "Battle")) if f.endswith(".cs"))
R("no_db_in_battle", "Npgsql" not in battle_src and "insert into" not in battle_src.lower())
settle = rd(os.path.join(root, "src", "Battle", "Settlement.cs")) + rd(os.path.join(root, "src", "Battle", "ExpSettlement.cs"))
R("battlefield_not_in_settlement", "Battlefield" not in settle and "battlefield" not in settle)

proto = rd(os.path.join(root, "proto", "mmo", "character.proto"))
gen_srv = rd(os.path.join(root, "src", "Shared", "Generated", "CharacterGrpc.cs"))
cli_gen = os.path.join(root, "client", "MMOGame", "Assets", "Proto", "Generated", "Character.cs")
gen_cli = rd(cli_gen)
R("proto_has_profile", "rpc DescribeBattleProfile(" in proto)
R("gen_server_has_profile", "DescribeBattleProfile" in gen_srv)
# 笔记本只同步服务端目录，没有 client/ 时这条跳过，不误判成本卡回退。
if os.path.exists(cli_gen):
    R("gen_client_has_profile", "DescribeBattleProfile" in gen_cli)
else:
    o.write("SKIP gen_client_has_profile（这台机器没有客户端生成物）\n")

o.close()
PY
while IFS= read -r line; do
  case "$line" in
    "PASS "*) ok "${line#PASS }" ;;
    "FAIL "*) bad "${line#FAIL }" ;;
    "SKIP "*) printf '  ..   %s\n' "${line#SKIP }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 只读入口 --battlefield-table =="
bt="$("$DOTNET" "$BD" --battlefield-table 2>&1)"; brc=$?
[ "$brc" = "0" ] && printf '%s' "$bt" | grep -q 'team_size=10 teams=2 players=20' \
  && ok "只读入口：战场 10 对 10（players=20）" || bad "只读入口不对（rc=$brc）：$(printf '%s' "$bt" | head -2)"
printf '%s' "$bt" | grep -q 'ratio=1.3' && ok "只读入口：同队 1.3 倍" || bad "比例没打出来"
printf '%s' "$bt" | grep -q 'battlefield_equipment=attack_per_level=' && ok "只读入口：每级强化折算" || bad "折算没打出来"
printf '%s' "$bt" | grep -q 'battlefield_power=attack_weight=' && ok "只读入口：战力权重" || bad "权重没打出来"

echo "== 4. 坏配置起不来 =="
mk_bad() {
  python3 - "$ROOT/config/battlefield.json" "$TMP/$1.json" "$1" <<'PY'
import json, sys
src, dst, mode = sys.argv[1:4]
d = json.load(open(src, encoding="utf-8"))
if mode == "version":
    d["version"] = 0
elif mode == "team_size":
    d["mode"]["team_size"] = 0
elif mode == "teams":
    d["mode"]["teams"] = 1
elif mode == "ratio":
    d["balance"]["ratio"] = 1.0
elif mode == "weight":
    d["power"] = {"attack_weight": 0, "hp_weight": 0, "mana_weight": 0, "heal_weight": 0}
elif mode == "equipment":
    d["equipment"]["attack_per_level"] = -1
elif mode == "no_mode":
    d.pop("mode", None)
json.dump(d, open(dst, "w", encoding="utf-8"), ensure_ascii=False)
PY
}
for c in version team_size teams ratio weight equipment no_mode; do
  mk_bad "$c"
  out="$("$DOTNET" "$BD" --battlefield-table-path "$TMP/$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error='; then
    ok "坏配置 ${c}：起不来（config_error=）"
  else
    bad "坏配置 ${c} 没挡住（rc=$rc）：$(printf '%s' "$out" | head -2)"
  fi
done

echo "== 5. 假服：没有角色库时战场一律拒 =="
mk_cfg() {
  python3 - "$ROOT" "$TMP" "$1" "$2" <<'PY'
import json, os, sys
root, tmp, name, with_table = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
d = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
d["listen"] = "127.0.0.1:0"
d["settlement_dir"] = os.path.join(tmp, name + "-set")
d["character_dir"] = os.path.join(tmp, name + "-chr")
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json")):
    d[k] = os.path.join(root, "config", f)
if with_table == "yes":
    d["battlefield_table"] = os.path.join(root, "config", "battlefield.json")
else:
    d.pop("battlefield_table", None)
json.dump(d, open(os.path.join(tmp, name + ".json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
}
listen_of() {
  for _ in $(seq 1 120); do
    u="$(grep -oE 'listen=http://127\.0\.0\.1:[0-9]+' "$1" 2>/dev/null | tail -1 | sed 's/^listen=//')"
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

mk_cfg noprof yes
"$DOTNET" "$BD" --config "$TMP/noprof.json" >"$TMP/noprof.log" 2>&1 &
PIDS="$PIDS $!"
U="$(listen_of "$TMP/noprof.log")"
[ -n "$U" ] && ok "假服就绪（有战场表、无角色服上游）" || bad "假服没起来"
C="$(reject_line "$U/battle?battlefield=m1&team=a&char=1&class=warrior")"
[ "$C" = "400" ] && grep -q 'join_reject=battle_profile_missing' "$TMP/noprof.log" \
  && ok "没有角色库：400 + battle_profile_missing（不编一份属性出来）" || bad "无上游没被拒（$C）"
C="$(reject_line "$U/battle?battlefield=m1&team=z&char=1&class=warrior")"
[ "$C" = "400" ] && grep -q 'join_reject=battlefield_team_invalid' "$TMP/noprof.log" \
  && ok "队伍只认 a / b：400 + battlefield_team_invalid" || bad "非法队伍没被拒（$C）"
C="$(reject_line "$U/battle?battlefield=m1&team=a&char=1&class=warrior&dungeon=abyss")"
[ "$C" = "400" ] && grep -q 'join_reject=battlefield_with_other_mode' "$TMP/noprof.log" \
  && ok "战场与副本混用：400 + battlefield_with_other_mode" || bad "混用没被拒（$C）"

mk_cfg notable no
"$DOTNET" "$BD" --config "$TMP/notable.json" >"$TMP/notable.log" 2>&1 &
PIDS="$PIDS $!"
U2="$(listen_of "$TMP/notable.log")"
[ -n "$U2" ] && ok "没配战场表也能起（副本与练习场不受影响）" || bad "缺表把进程挡住了"
printf '%s' "$(cat "$TMP/notable.log")" | grep -q 'battlefield=missing' && ok "启动日志标 battlefield=missing" || bad "缺表没标 missing"
C="$(reject_line "$U2/battle?battlefield=m1&team=a&char=1&class=warrior")"
[ "$C" = "400" ] && grep -q 'join_reject=battlefield_table_missing' "$TMP/notable.log" \
  && ok "缺表时战场一律拒：400 + battlefield_table_missing" || bad "缺表还放进去了（$C）"

echo "== 6. 真库：造两个角色，验同队 1.3 倍 =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真库段（MMO_P2_3B_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  # 临时表把开场时限调短：两人不会凑满 10 人，走时限那一支。
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
d = json.load(open(os.path.join(root, "config", "battlefield.json"), encoding="utf-8"))
d["fill_timeout_ms"] = 300
json.dump(d, open(os.path.join(tmp, "bf-fast.json"), "w", encoding="utf-8"), ensure_ascii=False)
b = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
b["listen"] = "127.0.0.1:0"
b["settlement_dir"] = os.path.join(tmp, "real-set")
b["character_dir"] = os.path.join(tmp, "real-chr")
b["exp_upstream"] = "http://127.0.0.1:62000"
b["character_upstream"] = "http://127.0.0.1:62000"
for k, f in (("skill_table", "skills.json"), ("class_table", "classes.json"),
             ("dungeon_table", "dungeons.json"), ("arena_match_table", "arena_match.json")):
    b[k] = os.path.join(root, "config", f)
b["battlefield_table"] = os.path.join(tmp, "bf-fast.json")
json.dump(b, open(os.path.join(tmp, "battle-real.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 &
  CPID=$!; PIDS="$PIDS $CPID"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$BD" --config "$TMP/battle-real.json" >"$TMP/battle.log" 2>&1 &
  PIDS="$PIDS $!"
  BU="$(listen_of "$TMP/battle.log")"
  [ -n "$BU" ] && ok "真角色服 62000 + 战斗服就绪" || bad "战斗服没起来"
  WU="ws${BU#http}"

  "$DOTNET" "$GD" $CG >"$TMP/gw.log" 2>&1 &
  PIDS="$PIDS $!"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
      -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p3b_$$"; PW="P3b-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" $CG >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
    | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
  [ -n "$TOK" ] && ok "登录拿到令牌（账号/口令只在内存）" || bad "登录没拿到令牌"
  mkchar() {
    # 网关到角色服的 gRPC 子通道要轮询到真能建号才稳：刚 ready 时第一次常常失败。
    local cls="$1" out id
    for _ in $(seq 1 10); do
      out="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
        -H 'Content-Type: application/json' \
        -d "{\"name\":\"p3b_$cls$RANDOM\",\"race\":\"mai\",\"class_id\":\"$cls\"}" 2>&1)"
      id="$(printf '%s' "$out" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")' 2>/dev/null)"
      [ -n "$id" ] && { printf '%s' "$id"; return 0; }
      sleep 0.5
    done
    return 1
  }
  A="$(mkchar warrior)"; B="$(mkchar priest)"
  [ -n "$A" ] && [ -n "$B" ] && ok "造两个角色：warrior + priest（${A} / ${B}）" \
    || bad "角色没建出来（A=「${A}」B=「${B}」）"
  curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/inventory/add?token=$TOK" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$A,\"slot\":1,\"item_id\":\"sword_trial\",\"count\":1}" >/dev/null
  curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/inventory/add?token=$TOK" \
    -H 'Content-Type: application/json' -d "{\"character_id\":$B,\"slot\":1,\"item_id\":\"robe_trial\",\"count\":1}" >/dev/null
  stop_pid "$(pgrep -f "Gateway.dll --config $CG" | head -1)" 2>/dev/null || true

  ew() {
    "$DOTNET" "$CD" --raw-enhance-write "$1" 1 "$2" $CC >>"$TMP/ew.log" 2>&1 || true
    if ! grep -q "^raw_enhance=" "$TMP/ew.log" 2>/dev/null; then
      printf '  ..   强化写入没有输出：%s\n' "$(grep -E 'Exception|Unhandled' "$TMP/ew.log" 2>/dev/null | head -2 | tr '\n' ' ')"
    fi
  }
  open_field() {
    "$DOTNET" "$PROBE" --url "$WU/battle?battlefield=$1&team=a&char=$A&class=warrior" \
      --case battlefield_open >"$TMP/pa.log" 2>&1 &
    local p1=$!
    "$DOTNET" "$PROBE" --url "$WU/battle?battlefield=$1&team=a&char=$B&class=priest" \
      --case battlefield_open >"$TMP/pb.log" 2>&1 &
    local p2=$!
    wait "$p1" "$p2" 2>/dev/null || true
  }

  ew "$A" 0; ew "$B" 0
  open_field m1
  if grep -q 'verdict=ok' "$TMP/pa.log" && grep -q 'verdict=ok' "$TMP/pb.log"; then
    ok "两人同队都进了战场（等时限开场）"
  else
    bad "有人没进场"; head -3 "$TMP/pa.log" "$TMP/pb.log" | detail
    grep -E 'join_reject=|battle_profile_error' "$TMP/battle.log" | tail -4 | detail
  fi
  R1="$(python3 - "$TMP/battle.log" m1 <<'PY'
import re, sys
log, match = sys.argv[1], sys.argv[2]
rows = [l for l in open(log, encoding="utf-8") if f"battlefield_open=match={match} " in l]
vals = [(int(re.search(r" power=(\d+)", l).group(1)),
         int(re.search(r"adjusted_power=(\d+)", l).group(1))) for l in rows]
if len(vals) < 2:
    print("few")
else:
    powers = [v[0] for v in vals]; adj = [v[1] for v in vals]
    print("%d %d %.3f" % (max(powers), min(powers), max(adj) / min(adj)))
PY
)"
  read -r P_MAX P_MIN RATIO <<< "$R1"
  [ "$P_MAX" != "few" ] && awk -v r="$RATIO" 'BEGIN{exit !(r <= 1.3005)}' \
    && ok "同队两人有效战力之比 ${RATIO} ≤ 1.3（真实 ${P_MIN}..${P_MAX}）" \
    || bad "同队比值不对：$R1"

  # 一个人强化到 12 级：真实战力拉开，场上仍要被压进 1.3 倍。
  ew "$A" 12
  open_field m2
  R2="$(python3 - "$TMP/battle.log" m2 <<'PY'
import re, sys
log, match = sys.argv[1], sys.argv[2]
rows = [l for l in open(log, encoding="utf-8") if f"battlefield_open=match={match} " in l]
vals = [(int(re.search(r" power=(\d+)", l).group(1)),
         int(re.search(r"adjusted_power=(\d+)", l).group(1)),
         float(re.search(r" k=([0-9.]+)", l).group(1))) for l in rows]
if len(vals) < 2:
    print("few")
else:
    print("%d %d %.3f %.3f" % (max(v[0] for v in vals), min(v[0] for v in vals),
                              max(v[1] for v in vals) / min(v[1] for v in vals),
                              max(v[2] for v in vals)))
PY
)"
  read -r Q_MAX Q_MIN Q_RATIO Q_K <<< "$R2"
  [ "$Q_MAX" != "few" ] && awk -v r="$Q_RATIO" 'BEGIN{exit !(r <= 1.3005)}' \
    && ok "强化到 12 级后真实战力 ${Q_MIN}..${Q_MAX}，场上比仍 ${Q_RATIO} ≤ 1.3" \
    || bad "强化后比值不对：$R2"
  awk -v k="$Q_K" 'BEGIN{exit !(k > 1.001)}' \
    && ok "低战力那一侧被抬上场（k=${Q_K} > 1）" || bad "没有抬升：k=$Q_K"
  ew "$A" 0
  [ "$("$DOTNET" "$CD" --enhance-level "$A" 1 $CC 2>&1 | grep -oE 'level=[0-9]+' | head -1)" = "level=0" ] \
    && ok "强化等级已还原" || bad "强化等级没还原"
fi

finish "P2-3b（战斗服）"
