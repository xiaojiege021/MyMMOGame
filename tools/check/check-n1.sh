#!/usr/bin/env bash
# N1 第 11 场打开旧天城与焦原（开发架构第 14 节第 80 行；14.3「先补方案已经写死、实现断着的口」）。
#   bash tools/check/check-n1.sh
#   MMO_N1_HAS_DB=1 MMO_GAMEDB='Host=...' bash tools/check/check-n1.sh   加真服真库端到端
# 完成标准：主线未到第 11 场，两块地表仍进不去；交完第 11 场后能进，上面已铺的矿和药能采。不改地名。
# 玩法正本：方案 2.5「旧天城区 / 焦原第 11 场才开」、2.8「立仙或立魔在第 10 场写入，两项都空着时两块都不开」；
# 进程边界：技术架构 3 节（世界服管境界区域是否可进入）、6 节（主线场次与立仙立魔只在角色 schema）。
set -uo pipefail
# 本机设了代理时 .NET 会把 127.0.0.1 的明文 gRPC 请求也送进代理；curl 一律 --noproxy '*'。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
WORLD_DLL="$ROOT/src/World/bin/Release/net10.0/World.dll"
CHARACTER_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
HAS_DB="${MMO_N1_HAS_DB:-0}"
TMP="$(mktemp -d)"; TMP="${TMP//\\//}"
PIDS=""
utf8() { case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) python3 -c 'import sys; sys.stdout.buffer.write(sys.stdin.buffer.read().decode("gbk","replace").encode("utf-8"))' ;; *) cat ;; esac; }
jget() { python3 -c 'import json,sys
try:
    v = json.load(sys.stdin).get(sys.argv[1], "")
except Exception:
    v = ""
print("true" if v is True else "false" if v is False else v)' "$1"; }
cleanup() {
  local p
  for p in $PIDS; do stop_pid "$p"; done
  if [ -n "${HAVE_DB:-}" ]; then
    "$DOTNET" "$CHARACTER_DLL" --delete-character "${CNAME_A:-}" --config config/character.json >/dev/null 2>&1 || true
    "$DOTNET" "$CHARACTER_DLL" --delete-character "${CNAME_B:-}" --config config/character.json >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "${ACCT:-}" --config config/gateway.json >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP" 2>/dev/null || true
}
trap cleanup EXIT

echo "== 1. 编译 =="
for proj in src/Shared/Shared.csproj src/Global/Global.csproj src/Gateway/Gateway.csproj \
            src/Character/Character.csproj src/Battle/Battle.csproj src/Social/Social.csproj \
            src/World/World.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 ${n}：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done

echo "== 2. 静态断言（内容正本 / 唯一判定点 / 老卡不改） =="
python3 - "$ROOT" >"$TMP/static.out" 2>&1 <<'PY'
import json, os, re, subprocess, sys
root = sys.argv[1]
def src(p):
    """读一份可能被拆分（partial）过的源码：主文件 + 分片目录一起读（14.8 纪律第 8 条）。"""
    _sub = {"CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World",
            "SectRepository.cs": "Sect", "SocialGrpcService.cs": "Grpc",
            "WorldGrpcService.cs": "Grpc", "Program.cs": "Cli"}
    full = os.path.join(root, p)
    name = os.path.basename(full)
    text = open(full, encoding="utf-8").read()
    if name in _sub:
        d = os.path.join(os.path.dirname(full), _sub[name])
        text += "".join(open(f, encoding="utf-8").read()
                        for f in sorted(__import__("glob").glob(os.path.join(d, "*.cs"))))
    return text
def cfg(p): return json.load(open(os.path.join(root, p), encoding="utf-8"))
checks = []
def ck(name, cond): checks.append((name, bool(cond)))

html = src("docs/MMO系统架构方案.html")
# 方案正文里有行内标签，比对前先去标签，按纯文本找句子。
plain = re.sub(r"<[^>]+>", "", html)
ul = cfg("config/region_unlock.json")
ml = cfg("config/mainline.json")
world = cfg("config/world.json")
regions = {f[:-5]: cfg("config/region/" + f) for f in os.listdir(os.path.join(root, "config/region")) if f.endswith(".json")}
gate = src("src/World/RegionGate.cs")
line = src("src/World/LineRegistry.cs")
flight = src("src/World/FlightGate.cs")
grpc = src("src/World/WorldGrpcService.cs")
proc = src("src/World/WorldProcess.cs")
table = src("src/World/RegionUnlockTable.cs")

# 方案原文：两处「第 11 场才开」、2.8 的「两项都空着时都不开」、立仙 / 立魔各开放一块。
ck("方案 2.5 写定两块地表第 11 场才开、2.8 写定两项都空着都不开",
   all(s in plain for s in ("第 11 场才开", "立仙或立魔在第 10 场写入",
                           "两项都空着时，旧天城区和焦原都不开", "名叫旧天城区", "名叫焦原")))
ck("解锁表 schema / version / pending 正确", ul["schema"] == "world-region-unlock/1"
   and ul["version"] == 1 and ul.get("pending") is True)
ck("解锁表只配静态不开放的地表、每块都有条目",
   {e["region_id"] for e in ul["regions"]} == {k for k, v in regions.items() if not v["open"]}
   and all(not regions[e["region_id"]]["open"] for e in ul["regions"]))
ck("解锁场次与路线照方案：两块都是第 11 场，立仙走旧天城区、立魔走焦原",
   {(e["region_id"], e["unlock_stage"], e["alignment"]) for e in ul["regions"]}
   == {("旧天城区", 11, "立仙"), ("焦原", 11, "立魔")})
ck("解锁表的路线字面量与 mainline.json 的 alignment_choices 同一套",
   {e["alignment"] for e in ul["regions"]} == set(ml["alignment_choices"]))
ck("两块地表仍在 world.json 的编号名单里、地名一字未改",
   all(k in world["regions"] and regions[k]["name"] == k for k in ("旧天城区", "焦原")))
ck("解锁表没有奖励 / 金币 / 次数一类键",
   not ({"reward", "drop", "gold", "exp", "equip", "fatigue", "price", "probability", "tax"}
        & set(ul.keys())) and all(not ({"reward", "drop", "gold"} & set(e)) for e in ul["regions"]))

# 唯一判定点：拒绝原因只由 RegionGate 给，分线注册表只认 unlocked 结论。
ck("RegionGate 是开放的唯一判定点（三种地表分开判）",
   all(s in gate for s in ("region_locked", "region_route_mismatch", "NeedsProgress", "region.Open")))
ck("分线注册表只认 unlocked 结论，不再自己读剧情",
   "bool unlocked = false" in line and "!region.Open && !unlocked" in line
   and line.count('"region_locked"') == 1)
ck("跨区把开放判定交给 RegionGate，不再有自己那条 open 判定",
   "regions.CheckAsync(account, characterId, target" in flight and "!target.Open" not in flight)
ck("进线先过 RegionGate 再进分线，日志追加进度字段",
   "await regions.CheckAsync(account, request.CharacterId, region" in grpc
   and "unlocked: access.Ok" in grpc and "unlock_stage=" in grpc)
ck("只有静态不开放的地表才问角色服（老地表不多花一次跨进程调用）",
   "if (region.Open)" in gate and "unlocks?.Find(region.Id) is null" in gate)
ck("世界服仍不连角色库（不读库、不存一份进度）",
   not any(s in src("src/World/" + p)
           for p in os.listdir(os.path.join(root, "src/World")) if p.endswith(".cs")
           for s in ("Npgsql", "Host=", "Password=")))
ck("解锁表坏表起不来、缺表降级，启动日志新行 + 老行仍在",
   "LoadIfPresent" in table and "region_unlock=missing" in proc
   and "region_unlock=ok entries=" in proc and '"locked="' in proc)
ck("老配置 / 库表 / 老核对脚本一行未改",
   # N14（v1.171）起 proto 与世界地点表回包归它改：本卡守的是解锁表、地名、库表与老脚本，
   # 不再拿「proto / client 一个字节没动」当 N1 的事实。
   # check-p1-3c.sh 里 C2 之后那条「境界回包写法」已按新口径修正，不再列入这张卡的不改范围。
   subprocess.run(["git", "diff", "--name-only", "HEAD", "--",
                   "config/region", "config/world.json", "config/flight.json",
                   "config/mainline.json", "config/gather.json", "config/gather_spread.json",
                   "config/dungeons.json", "db",
                   # D3h：check-p1-3a1 只补了一条「主文件 + 分片目录」的读法，断言正文没动。
                   # D3h 给 check-p3-5a 也只补了那条拆分读法，断言正文没动。
                   "tools/check/check-u4.sh"],
                  cwd=root, capture_output=True, text=True).stdout.strip() == "")
ck("总控已挂 n1（依赖 c1 / 进线 / 境界区域）",
   'n1) echo "c1 p1-3a1 p1-3c"' in src("tools/check/check-all.sh")
   and "c3 n1" in src("tools/check/check-all.sh"))
for name, cond in checks:
    print(("ok   " if cond else "FAIL ") + name)
PY
while IFS= read -r line_out; do
  case "$line_out" in
    "ok   "*) ok "${line_out#ok   }" ;;
    "FAIL "*) bad "${line_out#FAIL }" ;;
    *) printf '%s\n' "$line_out" | detail ;;
  esac
done <"$TMP/static.out"
# 静态段整体崩掉时上面一行 ok 都不会出，别让「没跑」当成「全过」。
if ! grep -qE '^(ok|FAIL) ' "$TMP/static.out"; then
  bad "静态断言段没跑出结果（看上面的 traceback）"
fi

echo "== 3. 只读入口：解锁表与判定（不连角色服、不起进程） =="
STATE="$("$DOTNET" "$WORLD_DLL" --region-unlock-state 2>&1 | utf8)"
printf '%s' "$STATE" | grep -q 'region_unlock=ok entries=2' \
  && printf '%s' "$STATE" | grep -q 'locked=旧天城区,焦原 static_open=6' \
  && ok "解锁表两条：两块静态不开放的地表都配了条件，静态开放六块" \
  || { bad "解锁表只读口不对"; printf '%s' "$STATE" | detail; }
printf '%s' "$STATE" | grep -q 'routes=旧天城区:立仙,焦原:立魔' \
  && ok "解锁条件照方案：立仙走旧天城区、立魔走焦原" || bad "解锁路线不对：$STATE"

access_of() {  # access_of <stage> [alignment] <region> → ok / reason
  "$DOTNET" "$WORLD_DLL" --region-access --main-stage "$1" ${2:+--alignment "$2"} 2>&1 \
    | awk -v r="region=$3 " 'index($0, r) > 0 {
        for (i = 1; i <= NF; i++) {
          if ($i ~ /^ok=/) o = substr($i, 4)
          if ($i ~ /^reason=/) q = substr($i, 8)
        }
        print (o == "true" ? "ok" : q) }'
}
[ "$(access_of 0 立仙 旧天城区)" = "region_locked" ] \
  && ok "还没交任何场：旧天城区进不去（剧情没到）" || bad "第 0 场就放人进旧天城区"
[ "$(access_of 10 立仙 旧天城区)" = "region_locked" ] \
  && ok "只交到第 10 场：旧天城区仍进不去（要交完第 11 场）" || bad "第 10 场就放人进旧天城区"
[ "$(access_of 11 立仙 旧天城区)" = "ok" ] \
  && ok "立仙交完第 11 场：旧天城区能进" || bad "立仙交完第 11 场还进不去"
[ "$(access_of 11 立仙 焦原)" = "region_route_mismatch" ] \
  && ok "立仙的人进不了焦原（方案 2.5 两条路各走一段）" || bad "立仙的人被放进焦原"
[ "$(access_of 11 立魔 焦原)" = "ok" ] \
  && ok "立魔交完第 11 场：焦原能进" || bad "立魔交完第 11 场还进不去"
[ "$(access_of 11 立魔 旧天城区)" = "region_route_mismatch" ] \
  && ok "立魔的人进不了旧天城区" || bad "立魔的人被放进旧天城区"
[ "$(access_of 11 "" 旧天城区)" = "region_locked" ] \
  && ok "立仙立魔都空着时两块都不开（方案 2.8）" || bad "路线空着居然放行"
[ "$(access_of 12 立仙 旧天城区)" = "ok" ] \
  && ok "走到第 12 场后旧天城区仍留着（不因为推进剧情又关上）" || bad "第 12 场把地表关上了"
MISS="$("$DOTNET" "$WORLD_DLL" --region-access --main-stage 11 --alignment 立仙 \
  --region-unlock-config "$TMP/no-such.json" 2>&1 | utf8)"
printf '%s' "$MISS" | grep -q 'table=missing' \
  && [ "$(printf '%s' "$MISS" | grep -c 'reason=region_locked')" = "2" ] \
  && ok "缺解锁表降级：两块照 P1-3a1 的老口径 region_locked（老卡不受影响）" \
  || { bad "缺表降级不对"; printf '%s' "$MISS" | detail; }

echo "== 4. 坏配置（都得起不来） =="
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1:3]
base = json.load(open(os.path.join(root, "config/region_unlock.json"), encoding="utf-8"))
def bad(name, mut):
    d = json.loads(json.dumps(base)); mut(d)
    json.dump(d, open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8"), ensure_ascii=False)
def only_open(d): d["regions"][1] = {"region_id": "承露门", "unlock_stage": 11, "alignment": "立魔"}
def drop_last(d): d["regions"].pop()
def dup_route(d): d["regions"][1]["alignment"] = "立仙"
bad("schema", lambda d: d.__setitem__("schema", "x/1"))
bad("version", lambda d: d.__setitem__("version", 2))
bad("no-regions", lambda d: d.pop("regions"))
bad("empty-regions", lambda d: d.__setitem__("regions", []))
bad("unknown-region", lambda d: d["regions"].append({"region_id": "不存在的地表", "unlock_stage": 11, "alignment": "立魔"}))
bad("open-region", only_open)
bad("stage-zero", lambda d: d["regions"][0].__setitem__("unlock_stage", 0))
bad("stage-over", lambda d: d["regions"][0].__setitem__("unlock_stage", 13))
bad("bad-alignment", lambda d: d["regions"][0].__setitem__("alignment", "立妖"))
bad("dup-route", dup_route)
bad("missing-region", drop_last)
bad("entry-no-stage", lambda d: d["regions"][0].pop("unlock_stage"))
bad("top-gold", lambda d: d.__setitem__("gold", 1))
bad("entry-reward", lambda d: d["regions"][0].__setitem__("reward", 1))
PY
for f in "$TMP"/bad-*.json; do
  n="$(basename "$f" .json)"; n="${n#bad-}"
  out="$("$DOTNET" "$WORLD_DLL" --region-unlock-state --region-unlock-config "$f" 2>&1 | utf8)"; rc=$?
  [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q '^config_error=' \
    && ok "坏配置 ${n}：config_error= 且非 0 退出" \
    || bad "坏配置 ${n} 没挡住（rc=${rc}）：$(printf '%s' "$out" | head -1)"
done

echo "== 5. 真服真库（MMO_N1_HAS_DB=1 才跑） =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  echo "  ..   跳过（没有 MMO_N1_HAS_DB=1 或 MMO_GAMEDB）"
else
  HAVE_DB=1
  "$DOTNET" "$CHARACTER_DLL" --config config/character.json >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready name=Character' "角色服" || finish "N1"
  "$DOTNET" "$WORLD_DLL" --config config/world.json --gather-spread-config config/gather_spread.json \
    --region-unlock-config config/region_unlock.json >"$TMP/world.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/world.log" '^ready name=World' "世界服" || finish "N1"
  "$DOTNET" "$GATEWAY_DLL" --config config/gateway.json >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST \
      http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
      -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  grep -q '^region_unlock=ok entries=2' "$TMP/world.log" && grep -q '^locked=旧天城区,焦原$' "$TMP/world.log" \
    && ok "世界服启动：解锁表两条与老行 locked= 同时在" \
    || { bad "启动日志不对"; grep -E '^region_unlock|^locked=' "$TMP/world.log" | detail; }

  ACCT="n1_$$"; PW="N1-$$-$(date +%s)"; CNAME_A="n1a_$$"; CNAME_B="n1b_$$"
  printf '%s\n' "$PW" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$ACCT" --seed-slots 8 \
    --config config/gateway.json >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login \
    -H 'Content-Type: application/json' -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" | jget token)"
  [ -n "$TOK" ] && ok "真服：登录拿到令牌（口令只在内存）" || { bad "真服：登录没拿到令牌"; finish "N1"; }
  new_char() {
    local name="$1" id=""
    for _ in $(seq 1 10); do
      id="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
        -H 'Content-Type: application/json' -d "{\"name\":\"$name\",\"race\":\"mai\",\"class_id\":\"warrior\"}" | jget character_id)"
      [ -n "$id" ] && break; sleep 0.5
    done
    printf '%s' "$id"
  }
  A="$(new_char "$CNAME_A")"; B="$(new_char "$CNAME_B")"
  [ -n "$A" ] && [ -n "$B" ] && ok "真库：两名角色就绪（立仙线 $A / 立魔线 ${B}）" \
    || { bad "真库：角色没建出来"; finish "N1"; }
  get()  { curl -s --noproxy '*' "http://127.0.0.1:61000$1?token=$TOK&character_id=$2"; }
  post() { curl -s --noproxy '*' -X POST "http://127.0.0.1:61000$1?token=$TOK" \
    -H 'Content-Type: application/json' -d "$2"; }
  cli()  { "$DOTNET" "$CHARACTER_DLL" --config config/character.json "$@" 2>&1 | utf8; }
  enter() { post /world/enter "{\"character_id\":$1,\"region_id\":\"$2\"}"; }
  cross() { post /world/region "{\"character_id\":$1,\"target_region\":\"$2\"}"; }
  # 把一个人推到「交完第 11 场 + 指定路线」：场次直写 9 省掉前九场，突破事实按 C1 的口径注入。
  advance() {
    local id="$1" route="$2"
    cli --raw-level-write "$id" 45 >/dev/null
    cli --raw-main-stage-write "$id" 9 >/dev/null
    for seg in 炼体 筑基 结丹; do cli --mainline-breakthrough "$id" "$seg" >/dev/null; done
    post /mainline/turn-in "{\"character_id\":$id,\"stage\":10,\"alignment\":\"$route\"}" >/dev/null
    post /mainline/turn-in "{\"character_id\":$id,\"stage\":11}" >/dev/null
  }

  R="$(enter "$A" 旧天城区)"
  [ "$(printf '%s' "$R" | jget reason)" = "region_locked" ] \
    && ok "真库：主线没到时旧天城区进不去（region_locked）" || bad "主线没到就放人进旧天城区：$R"
  advance "$A" 立仙
  [ "$(printf '%s' "$(get /mainline "$A")" | jget main_stage)" = "11" ] \
    && ok "真库：立仙线交完第 11 场（main_stage=11、alignment=立仙）" || bad "立仙线没走到第 11 场"
  R="$(enter "$A" 旧天城区)"
  [ "$(printf '%s' "$R" | jget entered)" = "true" ] \
    && ok "真库：交完第 11 场后能进旧天城区" || bad "交完第 11 场还进不去：$R"
  R="$(enter "$A" 焦原)"
  [ "$(printf '%s' "$R" | jget reason)" = "region_route_mismatch" ] \
    && ok "真库：立仙的人进焦原被拒（路线不匹配，不是「还没开」）" || bad "立仙的人进了焦原：$R"
  fatigue_of() { cli --fatigue-state "$1" 2>/dev/null | grep -o 'fatigue=[0-9]*' | head -1; }
  F1="$(fatigue_of "$A")"
  R="$(post /world/gather "{\"character_id\":$A,\"node_id\":\"ore_jiutian_1\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] \
    && ok "真库：在旧天城区采到 C2 铺的矿（节点本来就有，只是进不去）" || bad "旧天城区的矿采不了：$R"
  F2="$(fatigue_of "$A")"
  [ -n "$F1" ] && [ "$F1" = "$F2" ] \
    && ok "真库读回：采集前后疲劳都是 ${F1}（表世界采集不耗疲劳）" || bad "采集改了疲劳：$F1 → $F2"
  cross "$A" 栖云城 >/dev/null
  R="$(cross "$A" 旧天城区)"
  [ "$(printf '%s' "$R" | jget reason)" = "region_realm_locked" ] \
    && ok "真库：剧情解锁不绕开境界门槛（炼体跨不进结丹区域）" || bad "境界门槛被绕开了：$R"
  cli --raw-realm-write "$A" 结丹 >/dev/null
  R="$(cross "$A" 旧天城区)"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] \
    && ok "真库：境界够了之后跨区进旧天城区" || bad "境界够了还跨不进去：$R"
  # 声明的相连关系仍是 P1-3a1 那条：焦原只连着栖云城，所以先回栖云城再往焦原走。
  cross "$A" 栖云城 >/dev/null
  R="$(cross "$A" 焦原)"
  [ "$(printf '%s' "$R" | jget reason)" = "region_route_mismatch" ] \
    && ok "真库：立仙的人跨区去焦原仍按路线拒" || bad "跨区绕过了路线：$R"
  advance "$B" 立魔
  R="$(enter "$B" 焦原)"
  [ "$(printf '%s' "$R" | jget entered)" = "true" ] \
    && ok "真库：立魔线交完第 11 场后能进焦原" || bad "立魔进不了焦原：$R"
  R="$(enter "$B" 旧天城区)"
  [ "$(printf '%s' "$R" | jget reason)" = "region_route_mismatch" ] \
    && ok "真库：立魔的人进不了旧天城区" || bad "立魔的人进了旧天城区：$R"
  grep -q 'world_enter.*region=旧天城区.*entered=True' "$TMP/world.log" \
    && grep -qE 'unlock_stage=11 alignment=立仙' "$TMP/world.log" \
    && ok "世界服日志：进线带上进度与路线事实值，服务端一处判定" \
    || { bad "进线日志没带进度字段"; grep 'world_enter' "$TMP/world.log" | tail -3 | detail; }
fi

echo "== 6. 回归闸门 =="
if bash tools/check/preflight.sh >"$TMP/preflight.log" 2>&1; then
  ok "preflight.sh 四道闸门绿"
else
  bad "preflight.sh 有硬性失败"; grep '^  FAIL' "$TMP/preflight.log" | detail
fi
# rsync 副本目录不是 git 仓库（第 16 节的笔记本就是这种），这条在那边只能跳过。
if git rev-parse --git-dir >/dev/null 2>&1; then
  git diff --check >/dev/null 2>&1 && ok "git diff --check 通过" || bad "git diff --check 有空白问题"
else
  echo "  ..   跳过 git diff --check（当前目录不是 git 仓库）"
fi

finish "N1"
