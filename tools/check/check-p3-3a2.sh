#!/usr/bin/env bash
# P3-3a2 · 城战同屏超限（方案 15.1、技术架构第 24 行、开发架构 3 / 11 / 13 节、14 节第 64 行）。
#   bash tools/check/check-p3-3a2.sh
#   MMO_P3_3A2_CLIENT=1 UNITY=... bash tools/check/check-p3-3a2.sh       加 Unity 编辑器自检
#   MMO_P3_3A2_HAS_DB=1 MMO_GAMEDB='Host=...' bash ...                   加真服真库端到端
# 完成标准：同屏超限的单位没有完整模型；超限单位只留队伍颜色和血条。
set -uo pipefail
# 这台机设了代理，.NET 会把 127.0.0.1 的明文 HTTP/2 请求也送进代理。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
PROBE="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
CC="--config $ROOT/config/character.json"
HAS_DB="${MMO_P3_3A2_HAS_DB:-0}"
TMP="$(mktemp -d)"
PIDS=""
LIMIT="$(python3 -c 'import json;print(json.load(open("config/siege.json",encoding="utf-8"))["view"]["full_model_limit"])')"
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
    ok "编译 $n：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done

echo "== 2. 静态断言（结构保证，不写死当时的上限值）=="
python3 - "$ROOT" "$LIMIT" >"$TMP/static.txt" 2>&1 <<'PY'
import json, os, re, sys
root, limit = sys.argv[1], int(sys.argv[2])
def cfg(p): return json.load(open(os.path.join(root, p), encoding="utf-8"))
def src(p): return open(os.path.join(root, p), encoding="utf-8").read()
def emit(k, c, i=""): print(("PASS " if c else "FAIL ") + k + ("" if c else "  " + str(i)[:160]))
sg, bf = cfg("config/siege.json"), cfg("config/battlefield.json")
view = sg.get("view", {})
emit("view_only_limit", isinstance(view.get("full_model_limit"), int)
     and 1 <= view["full_model_limit"] <= 100
     and set(view.keys()) <= {"full_model_limit", "note"}, view)
emit("view_note_pending", "数值待定" in view.get("note", ""))
emit("view_one_number_only", len(re.findall(r'"full_model_limit"',
     open(os.path.join(root, "config/siege.json"), encoding="utf-8").read())) == 1)
emit("siege_scale_unchanged", sg["mode"]["team_size"] == 40 and sg["mode"]["teams"] == 2
     and sg["mode"]["team_size"] * sg["mode"]["teams"] == 80)
emit("siege_no_balance", not any(k in sg for k in
     ("balance", "ratio", "templates", "raise_low_only", "adjusted_power", "arena_match")))
emit("battlefield_unchanged", bf["mode"]["team_size"] == 10
     and abs(float(bf["balance"]["ratio"]) - 1.3) < 1e-9)
table = src("src/Battle/SiegeTable.cs")
emit("table_reads_view", "ViewFullModelLimit" in table and "没有 view 段" in table
     and "1..100" in table and "view 段不许出现" in table)
emit("table_no_model_keys", all(k in table for k in
     ("prefab", "appearance_tier", "team_color", "hp_bar")))
builder = src("src/Battle/SiegeViewTable.cs")
srv = src("src/Battle/Server.cs")
emit("build_single_point", "static SiegeView Build(" in builder
     and "OrderBy(" in builder and "SiegeViewTable.Build(" in srv
     and not any("static SiegeView Build(" in src(os.path.join("src", "Battle", f))
                 for f in os.listdir(os.path.join(root, "src", "Battle"))
                 if f.endswith(".cs") and f != "SiegeViewTable.cs"))
emit("build_uses_config", "config.Siege.ViewFullModelLimit" in srv
     and "RefreshSiegeViewsAsync" in srv and "conn.ViewReady = true" in srv)
emit("build_not_in_settlement", "siege" not in (src("src/Battle/Settlement.cs")
     + src("src/Battle/ExpSettlement.cs")).lower())
emit("build_no_db", "Npgsql" not in builder and "insert into" not in builder.lower())
proto = src("proto/mmo/battle.proto")
client = re.search(r"message ClientMessage \{(.*?)\}", proto, re.S)
servermsg = re.search(r"message ServerMessage \{(.*?)\}", proto, re.S)
unitmsg = re.search(r"message SiegeUnitVisual \{(.*?)\}", proto, re.S)
fields = set(re.findall(r"^\s*\w+\s+(\w+)\s*=\s*\d+;", unitmsg.group(1), re.M)) if unitmsg else set()
emit("proto_marker_tier", "SIEGE_VISUAL_MARKER = 2" in proto
     and "SIEGE_VISUAL_FULL = 1" in proto and "SiegeView siege_view = 14" in servermsg.group(1))
emit("proto_request_has_no_tier", client is not None
     and "siege_view" not in client.group(1) and "tier" not in client.group(1))
emit("proto_unit_only_facts", unitmsg is not None
     and fields == {"member_id", "team", "hp", "hp_max", "tier"}, fields)
net = src("client/MMOGame/Assets/Net/SiegeViewTiers.cs")
emit("client_only_translates", all(k in net for k in
     ("SiegeVisualFull", "SiegeVisualMarker", "TeamColor", "HpBar"))
     and not any(k in net for k in ("Math.Sqrt", "OrderBy", "FullModelLimit", ".X", ".Y")))
emit("client_editor_check", "p33a2_tiers=" in src("client/MMOGame/Assets/Editor/P33a2Check.cs")
     and "p33a2_boundary=" in src("client/MMOGame/Assets/Editor/P33a2Check.cs"))
field_body = srv.split("void StartBattlefieldTeam(")[-1].split("/// <summary>")[0]
emit("only_siege_path_sends_view", "RefreshSiegeViewsAsync" not in field_body
     and 'party.Key.StartsWith("siege-"' in srv)
world_all = "".join(src(os.path.join("src", "World", f))
                    for f in os.listdir(os.path.join(root, "src", "World")) if f.endswith(".cs"))
emit("world_not_involved", "SiegeView" not in world_all and "SiegeVisual" not in world_all)
PY
while IFS= read -r line; do
  case "$line" in "PASS "*) ok "${line#PASS }" ;; "FAIL "*) bad "${line#FAIL }" ;; esac
done < "$TMP/static.txt"
sha1="$(sha256sum "$ROOT/src/Shared/Generated/Battle.cs" | awk '{print $1}')"
sha2="$(sha256sum "$ROOT/client/MMOGame/Assets/Proto/Generated/Battle.cs" | awk '{print $1}')"
[ "$sha1" = "$sha2" ] && ok "两端 Battle.cs 生成物逐字相同" || bad "两端生成物不一致"

echo "== 3. 只读入口、坏配置与纯计算 =="
SO="$("$DOTNET" "$BD" --siege-table 2>&1)"; SRC=$?
[ "$SRC" = 0 ] && printf '%s' "$SO" | grep -q "view_full_model_limit=$LIMIT" \
  && ok "城战表只读入口带同屏上限（从配置现取 $LIMIT）" || bad "只读入口没有上限（rc=$SRC）"
printf '%s' "$SO" | grep -q 'team_size=40 teams=2 players=80 balance=none' \
  && ok "40 对 40 与全额生效口径未变" || bad "老规模口径被改了"
mk_bad() {
  python3 - "$ROOT/config/siege.json" "$TMP/bad-$1.json" "$1" <<'PY'
import json, sys
src, dst, mode = sys.argv[1:4]
d = json.load(open(src, encoding="utf-8"))
if mode == "no_view": d.pop("view", None)
elif mode == "zero": d["view"]["full_model_limit"] = 0
elif mode == "negative": d["view"]["full_model_limit"] = -1
elif mode == "big": d["view"]["full_model_limit"] = 101
elif mode == "model": d["view"]["model"] = "full"
elif mode == "tier": d["view"]["tier"] = 1
json.dump(d, open(dst, "w", encoding="utf-8"), ensure_ascii=False)
PY
}
for c in no_view zero negative big model tier; do
  mk_bad "$c"
  out="$("$DOTNET" "$BD" --siege-table-path "$TMP/bad-$c.json" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error='; then
    ok "坏配置 ${c}：config_error= 且非 0 退出"
  else
    bad "坏配置 ${c} 没挡住（rc=$rc）：$(printf '%s' "$out" | utf8 | head -2)"
  fi
done
UNITS="7:0:0:0:1200:1200 8:0:6000:0:1200:1200 9:1:100000:0:900:1200"
V2="$("$DOTNET" "$BD" --siege-view 2 7 $UNITS 2>&1)"
printf '%s' "$V2" | grep -q 'full=2 units=3 markers=1' \
  && printf '%s' "$V2" | grep -q 'unit=id=7 team=0 tier=full' \
  && printf '%s' "$V2" | grep -q 'unit=id=9 team=1 tier=marker hp=900 hp_max=1200' \
  && ok "纯计算：上限 2 时前两人完整、第三人只有队伍颜色和血条" || bad "上限 2 的档位不对"
V1="$("$DOTNET" "$BD" --siege-view 1 7 $UNITS 2>&1)"
printf '%s' "$V1" | grep -q 'full=1 units=3 markers=2' \
  && printf '%s' "$V1" | grep -q 'unit=id=7 team=0 tier=full' \
  && ok "纯计算：上限改成 1 后只剩自己完整" || bad "上限 1 的档位不对"
V9="$("$DOTNET" "$BD" --siege-view 9 7 $UNITS 2>&1)"
printf '%s' "$V9" | grep -q 'full=3 units=3 markers=0' \
  && ok "纯计算：上限不小于人数时一个都不超限" || bad "人少时还有超限单位"
VS="$("$DOTNET" "$BD" --siege-view 1 9 $UNITS 2>&1)"
printf '%s' "$VS" | grep -q 'unit=id=9 team=1 tier=full' \
  && printf '%s' "$VS" | grep -q 'unit=id=7 team=0 tier=marker' \
  && ok "纯计算：按看的人算，自己恒完整、档位随名单重排" || bad "换观察者后档位没跟着变"

echo "== 4. 假服：启动日志与老链路 =="
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
json.dump(d, open(os.path.join(tmp, "fake.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
"$DOTNET" "$BD" --config "$TMP/fake.json" >"$TMP/fake.log" 2>&1 & PIDS="$PIDS $!"
U="$(listen_of "$TMP/fake.log")"
[ -n "$U" ] && grep -q "siege_view_limit=$LIMIT" "$TMP/fake.log" \
  && ok "假服启动日志带同屏上限（siege_view_limit=$LIMIT）" || bad "假服没起来或没打上限"
WU="ws${U#http}"
"$DOTNET" "$PROBE" --url "$WU/battle?class=warrior" --case no_siege_view >"$TMP/plain.log" 2>&1
grep -q 'verdict=ok' "$TMP/plain.log" && ok "练习场没有城战档位包（老链路不回退）" \
  || bad "练习场收到档位包：$(head -2 "$TMP/plain.log" | detail)"
C="$(reject_line "$U/battle?siege=m1&team=a&char=1&class=warrior")"
[ "$C" = "400" ] && grep -q 'join_reject=battle_profile_missing' "$TMP/fake.log" \
  && ok "没有角色库仍按 P3-3a1 拒绝，不编一份档位出来" || bad "城战进场拒绝口径回退（$C）"

echo "== 5. 真服真库（本节全部需要真服；跳过时只算静态证据）=="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真服真库段（MMO_P3_3A2_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  python3 - "$ROOT" "$TMP" <<'PY'
import copy, json, os, sys
root, tmp = sys.argv[1:3]
sg = json.load(open(os.path.join(root, "config", "siege.json"), encoding="utf-8"))
for name, limit in (("siege-fast", 2), ("siege-limit1", 1), ("siege-limit9", 9)):
    d = copy.deepcopy(sg)
    d["fill_timeout_ms"] = 300
    d["mode"]["team_size"] = 3
    d["view"]["full_model_limit"] = limit
    json.dump(d, open(os.path.join(tmp, name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
bf = json.load(open(os.path.join(root, "config", "battlefield.json"), encoding="utf-8"))
bf["fill_timeout_ms"] = 300
json.dump(bf, open(os.path.join(tmp, "bf-fast.json"), "w", encoding="utf-8"), ensure_ascii=False)
for name, siege in (("battle-real", "siege-fast"), ("battle-l1", "siege-limit1"),
                    ("battle-l9", "siege-limit9")):
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
PY
  for f in account character currency inventory; do
    "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/$f.sql" --config "$ROOT/config/gateway.json" >"$TMP/mig-$f.log" 2>&1 || true
  done
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  WUS=()
  for spec in "battle-real real" "battle-l1 l1" "battle-l9 l9"; do
    set -- $spec
    "$DOTNET" "$BD" --config "$TMP/$1.json" >"$TMP/$2.log" 2>&1 & PIDS="$PIDS $!"
    u="$(listen_of "$TMP/$2.log")"
    [ -n "$u" ] && WUS+=("ws${u#http}") || WUS+=("")
  done
  WU="${WUS[0]}"; WU1="${WUS[1]}"; WU9="${WUS[2]}"
  BPORT="${WU##*:}"; BPORT="${BPORT%%/*}"
  python3 - "$ROOT" "$TMP" "$BPORT" <<'PY'
import json, os, sys
root, tmp, port = sys.argv[1:4]
d = json.load(open(os.path.join(root, "config", "gateway.json"), encoding="utf-8"))
d["battle_upstream"] = "ws://127.0.0.1:" + port + "/battle"
json.dump(d, open(os.path.join(tmp, "gateway-real.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
  GR="--config $TMP/gateway-real.json"
  "$DOTNET" "$GD" $GR >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
      -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p332_$$"; PW="P332-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 $GR >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" | jget token 2>/dev/null)"
  [ -n "$TOK" ] && ok "真服：登录拿到令牌（账号/口令只在内存）" || bad "真服：登录没拿到令牌"
  mkchar() {
    for _ in $(seq 1 10); do
      o="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
        -H 'Content-Type: application/json' \
        -d "{\"name\":\"p332_$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>&1)"
      id="$(printf '%s' "$o" | jget character_id 2>/dev/null)"
      [ -n "$id" ] && { printf '%s' "$id"; return 0; }; sleep 0.5
    done
  }
  CHAR=(); for i in 1 2 3 4 5 6; do CHAR+=("$(mkchar)"); done
  [ "${#CHAR[@]}" = 6 ] && [ -n "${CHAR[0]}" ] && [ -n "${CHAR[5]}" ] \
    && ok "真服：造六个角色（三对三，同一场城战）" || bad "真服：角色没建齐 ${CHAR[*]}"
  run_match() {
    local tag="$1" wu="$2" lim="$3" full="$4" marks="$5" m i team pids=()
    m="p332a-$$-$(date +%s)-$tag"
    for i in 0 1 2 3 4 5; do
      [ "$i" -lt 3 ] && team=a || team=b
      "$DOTNET" "$PROBE" --url "$wu/battle?siege=$m&team=$team&char=${CHAR[$i]}&class=warrior" \
        --case siege_view --expect-units 6 --hold-ms 1500 >"$TMP/$tag-$i.log" 2>&1 &
      pids+=($!)
    done
    wait "${pids[@]}" 2>/dev/null || true
    python3 - "$TMP" "$tag" "$lim" "$full" "$marks" >"$TMP/$tag-parse.txt" 2>&1 <<'PY'
import glob, os, re, sys
tmp, tag, lim, full, marks = sys.argv[1:6]
logs = sorted(glob.glob(os.path.join(tmp, tag + "-*.log")))
rows = []
for p in logs:
    t = open(p, encoding="utf-8", errors="replace").read()
    m = re.search(r"case=siege_view caster=(\d+) session=(\d+) limit=(\d+) full=(\d+) units=(\d+) markers=(\d+)", t)
    units = re.findall(r"^siege_unit=id=(\d+) team=(\d+) tier=(full|marker) hp=(-?\d+) hp_max=(-?\d+)$", t, re.M)
    rows.append((m, units, "verdict=ok" in t))
def say(k, c): print(("PASS " if c else "FAIL ") + tag + "_" + k)
say("six_opened", len(logs) == 6 and all(m and ok for m, u, ok in rows))
say("limit_and_counts", all(m and m.group(3) == lim and m.group(5) == "6"
    and m.group(4) == full and m.group(6) == marks for m, u, ok in rows))
say("self_always_full", all(m and any(u[0] == m.group(1) and u[2] == "full" for u in us)
    for m, us, ok in rows))
say("no_full_over_limit", all(m and sum(1 for u in us if u[2] == "full") == int(full)
    and sum(1 for u in us if u[2] == "marker") == int(marks)
    and len(us) == 6 for m, us, ok in rows))
say("marker_only_color_and_hp", all(m and all(u[1] in ("0", "1") and int(u[4]) > 0
    for u in us if u[2] == "marker") for m, us, ok in rows))
say("same_session", len({m.group(2) for m, u, ok in rows if m}) == 1)
PY
    while IFS= read -r line; do
      case "$line" in "PASS "*) ok "真服：${line#PASS }" ;; "FAIL "*) bad "真服：${line#FAIL }" ;; esac
    done < "$TMP/$tag-parse.txt"
  }
  run_match real "$WU" 2 2 4
  run_match l1 "$WU1" 1 1 5
  run_match l9 "$WU9" 9 6 0
  "$DOTNET" "$PROBE" --url "$WU/battle?class=warrior" --case no_siege_view >"$TMP/plain2.log" 2>&1
  grep -q 'verdict=ok' "$TMP/plain2.log" && ok "真服：练习场没有城战档位包" || bad "真服：练习场收到档位包"
  "$DOTNET" "$PROBE" --url "$WU/battle?battlefield=old&team=a&char=${CHAR[0]}&class=warrior" \
    --case no_siege_view >"$TMP/oldbf.log" 2>&1
  grep -q 'verdict=ok' "$TMP/oldbf.log" && ok "真服：战场老链路没有城战档位包" \
    || bad "真服：战场老链路收到档位包：$(head -2 "$TMP/oldbf.log" | detail)"
  "$DOTNET" "$PROBE" --url "ws://127.0.0.1:61000/battle?token=$TOK&siege=gw&team=a&char=${CHAR[0]}&class=warrior" \
    --case siege_view --expect-units 1 --hold-ms 500 >"$TMP/gwview.log" 2>&1
  grep -q 'verdict=ok' "$TMP/gwview.log" && grep -q 'case=siege_view .*units=1' "$TMP/gwview.log" \
    && ok "真服：经网关透传也能收到服务端确认的档位" || bad "真服：网关这条路没走通"
fi

echo "== 6. 客户端编辑器自检（显式开启；不进 playmode、不重打 AB）=="
if [ "${MMO_P3_3A2_CLIENT:-0}" != "1" ]; then
  printf '  ..   跳过（MMO_P3_3A2_CLIENT 未开）\n'
elif [ ! -x "${UNITY:-}" ]; then
  bad "找不到 Unity：${UNITY:-<未设置>}"
else
  "$UNITY" -batchmode -quit -projectPath "$ROOT/client/MMOGame" \
    -executeMethod P33a2Check.Run -logFile "$TMP/unity.log" >/dev/null 2>&1
  for key in config proto tiers boundary; do
    grep -q "p33a2_${key}=ok" "$TMP/unity.log" 2>/dev/null \
      && ok "Unity P33a2Check：$key=ok" \
      || { bad "Unity P33a2Check：$key 没过"; grep -E "error CS|p33a2_" "$TMP/unity.log" | tail -6 | detail; }
  done
fi

finish "P3-3a2（城战同屏超限）"
