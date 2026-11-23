#!/usr/bin/env bash
# P3-1a1 境界开关（方案 8.1 / 8.5、开发架构 11 节 P3-1、13 节 P3-1a1、14 节第 60 行）。
#   bash tools/check/check-p3-1a1.sh
#   MMO_P3_1A1_HAS_DB=1 bash ...                加真库段（进本门槛与开关打开）
# 本卡口径：十一段按顺序开放，首发到结丹；元婴起每段一个开关。开关关着时
# 不能突破到这一段，也不能进该段新副本；旧副本不写 realm，门槛不抬高。
set -uo pipefail
# 与 check-p3-1a2.sh 同一处补正：本机若设了 HTTP(S)_PROXY，.NET 会把
# 127.0.0.1 的明文 HTTP/2 请求送进代理；dotnet 重定向输出在 Windows 上走 GBK，
# 而中文断言是 UTF-8。两处都会让只读入口/真库段整段假失败。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
utf8() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      python3 -c 'import sys; sys.stdout.buffer.write(
          sys.stdin.buffer.read().decode("gbk", "replace").encode("utf-8"))' ;;
    *) cat ;;
  esac
}
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
AGG_CHARGRPC="$(src_agg src/Character/CharacterGrpcService.cs)"; export AGG_CHARGRPC
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CC="--config $ROOT/config/character.json"
CG="--config $ROOT/config/gateway.json"
HAS_DB="${MMO_P3_1A1_HAS_DB:-0}"
TMP="$(mktemp -d)"
PIDS=""
CHAR_PID=""
cleanup() { for p in $PIDS; do stop_pid "$p"; done; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
BUILD_OK=1
for proj in src/Character/Character.csproj src/Gateway/Gateway.csproj; do
  n="$(basename "$proj" .csproj)"
  "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1 || {
    BUILD_OK=0; bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail; }
done
[ "$BUILD_OK" = "1" ] && ok "角色服 / 网关编译通过（0 错误）" || true

echo "== 2. 静态断言 =="
python3 - "$ROOT" "$TMP/static.txt" <<'PY'
import json, os, sys
root, out = sys.argv[1], sys.argv[2]
o = open(out, "w", encoding="utf-8")

def R(k, c, i=""):
    o.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + i) + "\n")

def rd(p):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup"}
    _name = __import__("os").path.basename(p)
    if _name in _sub:
        # 14.8 拆分卡之后：主文件 + 分片目录一起读（断言文本不变，只改读法）。
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
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def js(p):
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}

realms = js(os.path.join(root, "config", "realms.json"))
order = ["炼体", "筑基", "结丹", "元婴", "化神", "返虚", "合体", "渡劫", "大乘", "金仙", "极境"]
R("eleven_realms", realms.get("realms") == order, str(realms.get("realms")))
R("launch_until", realms.get("launch_until") == "结丹", str(realms.get("launch_until")))
sw = {s.get("realm"): s.get("open") for s in realms.get("switches", [])}
R("switch_scope", set(sw) == set(order[3:]), str(sorted(sw)))
R("launch_only_open", all(v is False for v in sw.values()), str(sw))
R("scatter_alias", (realms.get("aliases") or {}).get("散仙") == "金仙", str(realms.get("aliases")))

d = js(os.path.join(root, "config", "dungeons.json"))
by = {x.get("id"): x for x in d.get("dungeons", [])}
old = ("main", "abyss", "lair", "sect", "dragon")
R("old_no_realm", all("realm" not in by.get(k, {}) for k in old), str({k: by.get(k, {}).get("realm") for k in old}))
R("yuanying_dungeon", by.get("abyss_yuanying", {}).get("realm") == "元婴", str(by.get("abyss_yuanying")))

rt = rd(os.path.join(root, "src", "Character", "RealmTable.cs"))
R("gate_reasons", "CheckDungeon" in rt and "realm_locked" in rt and "realm_not_enough" in rt)
R("advance_gate", "CheckAdvance" in rt and "realm_max" in rt)
R("ordered_switches", "十一段要按顺序开放" in rt)
R("old_realm_rejected", "旧副本不提高境界门槛" in rt)

grpc = rd(os.environ["AGG_CHARGRPC"])
R("enter_gate", "realms.CheckDungeon(dungeon.Realm" in grpc and "RealmAsync" in grpc)
cfg = rd(os.path.join(root, "src", "Character", "CharacterConfig.cs"))
R("realm_config", "config/realms.json" in cfg and "RealmTable" in cfg)
proc = rd(os.path.join(root, "src", "Character", "CharacterProcess.cs"))
R("startup_load", "RealmTable.Load(config.RealmTable, dungeons" in proc)
prog = rd(os.path.join(root, "tools", "probe", "CliProbe", "CliParser.cs"))
R("readonly_entries", "--realm-table" in prog and "--realm-state" in prog)
repo = rd(os.path.join(root, "src", "Character", "CharacterRepository.cs"))
R("realm_reader", "RealmOfAsync" in repo)
battle = rd(os.path.join(root, "src", "Battle", "Server.cs")) + rd(os.path.join(root, "src", "Battle", "Encounter.cs"))
R("gate_in_character", "realm_locked" not in battle)
R("enter_gate_readonly", "RawWriteRealm" not in grpc and "RealmAsync" in grpc)
o.close()
PY
while IFS= read -r line; do
  case "$line" in
    "PASS "*) ok "${line#PASS }" ;;
    "FAIL "*) bad "${line#FAIL }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 只读入口 --realm-table =="
"$DOTNET" "$CD" --realm-table $CC >"$TMP/rt.raw" 2>&1; rc=$?
RT="$(utf8 < "$TMP/rt.raw")"
if [ "$rc" = "0" ] \
   && printf '%s' "$RT" | grep -q '^realm_table_state=ok version=[0-9]\+ realms=11 launch_until=结丹 open=炼体,筑基,结丹 closed=元婴' \
   && printf '%s' "$RT" | grep -q '^realm=4:元婴:closed' \
   && printf '%s' "$RT" | grep -q '^alias=散仙=金仙'; then
  ok "十一段顺序、首发到结丹、元婴起全部关着（不连库）"
else
  bad "境界表只读口径不对（rc=${rc}）"
fi
printf '%s' "$RT" | grep -q '^advance=炼体->筑基:ok' \
  && printf '%s' "$RT" | grep -q '^advance=结丹->元婴:realm_locked' \
  && printf '%s' "$RT" | grep -q '^advance=极境->none:realm_max' \
  && ok "突破开关：首发内放行，结丹→元婴被锁，极境已是尽头" || bad "突破开关判定不对"
printf '%s' "$RT" | grep -q '^dungeon_gate=abyss_yuanying realm=元婴 open=false from_launch=realm_locked' \
  && printf '%s' "$RT" | grep -q '^dungeon_gate=abyss realm=none open=true from_launch=ok' \
  && ok "元婴副本的门槛跟着开关走；旧深渊不写 realm，门槛不抬高" || bad "副本境界门槛不对"

echo "== 4. 坏配置起不来 =="
python3 - "$ROOT" "$TMP" <<'PY'
import copy, json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
base = json.load(open(os.path.join(root, "config", "realms.json"), encoding="utf-8"))
dung = json.load(open(os.path.join(root, "config", "dungeons.json"), encoding="utf-8"))

def dump(name, obj):
    json.dump(obj, open(os.path.join(tmp, name), "w", encoding="utf-8"), ensure_ascii=False)

def sw(d, realm):
    return next(x for x in d["switches"] if x["realm"] == realm)

a = copy.deepcopy(base); a["switches"] = [x for x in a["switches"] if x["realm"] != "极境"]
dump("r-noswitch.json", a)
b = copy.deepcopy(base); b["switches"].append({"realm": "炼体", "open": True})
dump("r-launchswitch.json", b)
c = copy.deepcopy(base); c["aliases"]["散仙"] = "太乙"
dump("r-badalias.json", c)
e = copy.deepcopy(base); e["launch_until"] = "太初"
dump("r-badlaunch.json", e)
f = copy.deepcopy(base); sw(f, "化神")["open"] = True
dump("r-order.json", f)
g = copy.deepcopy(dung)
next(x for x in g["dungeons"] if x["id"] == "abyss")["realm"] = "结丹"
dump("d-oldrealm.json", g)
PY
for c in noswitch launchswitch badalias badlaunch order; do
  out="$("$DOTNET" "$CD" --realm-table-path "$TMP/r-$c.json" "$ROOT/config/dungeons.json" 2>&1)"
  assert_bad_config "$out" "坏境界表 ${c}：起不来（config_error=）"
done
out="$("$DOTNET" "$CD" --realm-table-path "$ROOT/config/realms.json" "$TMP/d-oldrealm.json" 2>&1)"
assert_bad_config "$out" "旧副本写 realm：起不来（旧副本不提高境界门槛）"

echo "== 5. 真库：元婴关着进不去，旧深渊仍能进 =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真库段（MMO_P3_1A1_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  # 幂等 DDL：账号与角色表先在，核对库可以是新建的。
  "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/account.sql" $CG >"$TMP/mig-account.log" 2>&1 || true
  "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/character.sql" $CC >"$TMP/mig-char.log" 2>&1 || true
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 &
  CHAR_PID=$!; PIDS="$PIDS $CHAR_PID"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$GD" $CG >"$TMP/gw.log" 2>&1 &
  PIDS="$PIDS $!"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
      -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p31a1_$$"; PW="P31a1-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 $CG >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
    | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
  [ -n "$TOK" ] && ok "登录拿到令牌（账号/口令只在内存）" || bad "登录没拿到令牌"
  mkchar() {
    local out id
    for _ in $(seq 1 10); do
      out="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
        -H 'Content-Type: application/json' \
        -d "{\"name\":\"p31a1_$RANDOM$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>&1)"
      id="$(printf '%s' "$out" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")' 2>/dev/null)"
      [ -n "$id" ] && { printf '%s' "$id"; return 0; }
      sleep 0.5
    done
    return 1
  }
  raw() { "$DOTNET" "$CD" --raw-realm-write "$1" "$2" $CC >/dev/null 2>&1; }
  rstate() { "$DOTNET" "$CD" --realm-state "$1" $CC 2>&1 | grep -m1 '^realm_state='; }
  entries() { "$DOTNET" "$CD" --entry-count "$1" $CC 2>&1 | grep -m1 '^entry_count='; }
  enter() {
    curl -s -m 10 -w '\n%{http_code}' -X POST "http://127.0.0.1:61000/dungeon/enter?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"character_id\":$1,\"dungeon_id\":\"$2\",\"party_size\":$3}"
  }
  body() { printf '%s' "${1%$'\n'*}"; }
  field() { printf '%s' "$1" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get(sys.argv[1],""))
except Exception: print("")' "$2"; }

  ID="$(mkchar)"; [ -n "$ID" ] && ok "造一个角色（${ID}）" || bad "角色没建成"
  raw "$ID" 结丹
  r="$(rstate "$ID")"
  printf '%s' "$r" | grep -q 'realm=结丹 rank=3 next=元婴 next_open=false advance=realm_locked' \
    && ok "结丹的下一段元婴关着：advance=realm_locked" || bad "结丹→元婴没被锁：$r"

  E0="$(entries "$ID")"
  R="$(enter "$ID" abyss_yuanying 1)"; B="$(body "$R")"; C="${R##*$'\n'}"
  if [ "$C" = "200" ] && [ "$(field "$B" allowed)" = "False" ] \
     && [ "$(field "$B" reason)" = "realm_locked" ]; then
    ok "元婴副本在开关关着时拒绝：allowed=false reason=realm_locked"
  else
    bad "元婴副本没被拒：HTTP $C $B"
  fi
  [ "$(entries "$ID")" = "$E0" ] && ok "被拒的进本没有留下记录（${E0}）" || bad "被拒也写了进本记录"

  R="$(enter "$ID" abyss 1)"; B="$(body "$R")"; C="${R##*$'\n'}"
  if [ "$C" = "200" ] && [ "$(field "$B" allowed)" = "True" ]; then
    ok "旧深渊没有境界门槛，结丹仍能进"
  else
    bad "旧深渊被挡住了：HTTP $C $B"
  fi

  raw "$ID" 元婴
  R="$(enter "$ID" abyss_yuanying 1)"; B="$(body "$R")"
  [ "$(field "$B" reason)" = "realm_locked" ] \
    && ok "已经到元婴，开关没开也进不去" || bad "元婴无视关闭的开关进去了：$B"

  echo "-- 打开元婴开关（临时配置，重启角色服）--"
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
c = json.load(open(os.path.join(root, "config", "character.json"), encoding="utf-8"))
c["realm_table"] = os.path.join(tmp, "realms-open.json")
json.dump(c, open(os.path.join(tmp, "character.json"), "w", encoding="utf-8"), ensure_ascii=False)
r = json.load(open(os.path.join(root, "config", "realms.json"), encoding="utf-8"))
for s in r["switches"]:
    if s["realm"] == "元婴":
        s["open"] = True
json.dump(r, open(os.path.join(tmp, "realms-open.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
  stop_pid "$CHAR_PID" 2>/dev/null || true
  sleep 0.5
  "$DOTNET" "$CD" --config "$TMP/character.json" >"$TMP/char2.log" 2>&1 &
  CHAR_PID=$!; PIDS="$PIDS $CHAR_PID"
  wait_ready "$TMP/char2.log" '^ready' "角色服（元婴已开）" || true
  CCT="--config $TMP/character.json"
  raw2() { "$DOTNET" "$CD" --raw-realm-write "$1" "$2" $CCT >/dev/null 2>&1; }
  rstate2() { "$DOTNET" "$CD" --realm-state "$1" $CCT 2>&1 | grep -m1 '^realm_state='; }

  S2="$(mkchar)"; Y2="$(mkchar)"
  raw2 "$S2" 结丹; raw2 "$Y2" 元婴
  r="$(rstate2 "$S2")"
  printf '%s' "$r" | grep -q 'next=元婴 next_open=true advance=ok' \
    && ok "元婴打开后：结丹的下一段放行（advance=ok）" || bad "开关开了仍不放行：$r"
  r="$(rstate2 "$Y2")"
  printf '%s' "$r" | grep -q 'realm=元婴 rank=4 next=化神 next_open=false advance=realm_locked' \
    && ok "元婴之后仍被化神开关挡住：按顺序开放" || bad "化神开关没挡住：$r"

  R="$(enter "$S2" abyss_yuanying 1)"; B="$(body "$R")"
  [ "$(field "$B" reason)" = "realm_not_enough" ] \
    && ok "开关开了但境界不够：realm_not_enough" || bad "结丹进了元婴副本：$B"
  R="$(enter "$Y2" abyss_yuanying 1)"; B="$(body "$R")"
  [ "$(field "$B" allowed)" = "True" ] \
    && ok "元婴本人能进元婴副本" || bad "元婴进不了元婴副本：$B"
fi

finish "P3-1a1（角色服 + 网关）"
