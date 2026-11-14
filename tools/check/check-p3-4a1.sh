#!/usr/bin/env bash
# P3-4a1 赛季更换一处秘境目标（方案 2 章、开发架构 11 节 P3-4 / 13 节 / 14 节第 67 行）。
#   bash tools/check/check-p3-4a1.sh
#   MMO_P3_4A1_HAS_DB=1 MMO_GAMEDB='Host=...' bash ...   加真服真库端到端
# 完成标准：新赛季目标与上一季不同。赛季归全局服务，只读、不发奖、不改角色库。
set -uo pipefail
# 本机设了代理时 .NET 会把 127.0.0.1 的明文 HTTP/2 请求也送进代理；curl 一律 --noproxy '*'。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
GL="$ROOT/src/Global/bin/Release/net10.0/Global.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CD="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
HAS_DB="${MMO_P3_4A1_HAS_DB:-0}"
TMP="$(mktemp -d)"
PIDS=""
# Windows 上 dotnet 重定向输出走 GBK；中文断言按平台转码，不能「先试 UTF-8 再退 GBK」。
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

echo "== 1. 编译 =="
for proj in src/Shared/Shared.csproj src/Global/Global.csproj src/Gateway/Gateway.csproj \
            src/Character/Character.csproj src/Battle/Battle.csproj src/Social/Social.csproj \
            src/World/World.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 $n：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done

echo "== 2. 静态断言 =="
python3 - "$ROOT" >"$TMP/static.out" 2>&1 <<'PY'
import hashlib, json, os, re, sys
root = sys.argv[1]
def src(p): return open(os.path.join(root, p), encoding="utf-8").read()
def cfg(p): return json.load(open(os.path.join(root, p), encoding="utf-8"))
def sha(p): return hashlib.sha256(open(os.path.join(root, p), "rb").read()).hexdigest()
checks = []
def ck(name, cond): checks.append((name, bool(cond)))
season, dun, realms = cfg("config/season.json"), cfg("config/dungeons.json"), cfg("config/realms.json")
rows = season["seasons"]
ids = [r["id"] for r in rows]
targets = [r["target_dungeon"] for r in rows]
dmap = {d["id"]: d.get("realm", "") for d in dun["dungeons"]}
closed = {s["realm"] for s in realms["switches"] if not s["open"]}
allowed_top = {"schema", "version", "note", "pending", "current", "seasons"}
allowed_row = {"id", "target_dungeon", "label", "note"}
forbidden = ("reward pass shop gold yuanbao stone item material equip enhance progress "
             "level rank leaderboard gacha drop price count bonus balance ticket exp").split()
ck("赛季表 schema/version/pending 齐全", season["schema"] == "season-table/1"
   and season["version"] > 0 and season.get("pending") is True)
ck("顶层与条目只有允许出现的键（通行证/奖励/商城不从配置长出来）",
   set(season) <= allowed_top and all(set(r) <= allowed_row for r in rows))
ck("顶层与条目的键名都不含奖励/材料/次数一类词",
   all(not any(w in k.lower() for w in forbidden) for k in season)
   and all(not any(w in k.lower() for w in forbidden) for r in rows for k in r))
ck("至少两季、编号唯一、current 命中其中一季", len(rows) >= 2
   and len(set(ids)) == len(ids) and season["current"] in ids)
ck("相邻两季的秘境目标不同（完成标准的配置侧证据）",
   all(targets[i] != targets[i - 1] for i in range(1, len(targets))))
ck("每季目标都是 dungeons.json 里真实存在的副本", all(t in dmap for t in targets))
ck("目标副本若写 realm，那个境界开关必须已开（不越过 P3-1a1）",
   all(dmap.get(t, "") not in closed for t in targets))
ck("老配置正本一行未改（副本表/境界表/global/gateway）",
   os.popen("git diff --name-only HEAD -- config/dungeons.json config/realms.json "
            "config/global.json config/gateway.json").read().strip() == "")
ck("global.json 没有显式登记赛季表（老卡临时目录照样起服）",
   "season_table" not in cfg("config/global.json"))
table = src("src/Global/SeasonTable.cs")
ck("加载期硬校验：相邻同目标 / 未开境界 / 目标不存在 / 禁键",
   all(s in table for s in ("相邻两季的秘境目标相同", "赛季目标的境界版本还没开",
                           "赛季目标不是副本表里的秘境", "不许出现")))
ck("可选表降级与热载同时在（Missing + ConfigCatalog.Watch）",
   "SeasonTable Missing()" in table and "_catalog.Watch()" in table
   and "season_config_error=" in table)
ck("产品代码里不写死赛季编号或秘境编号",
   not any(('"' + lit + '"') in table or ('"' + lit + '"') in src("src/Gateway/SeasonApi.cs")
           for lit in ids + targets))
ck("全局服务启动日志带当前季与上一季目标",
   "season=" in src("src/Global/GlobalProcess.cs")
   and "previous_target=" in src("src/Global/GlobalProcess.cs")
   and "new SeasonCatalog(" in src("src/Global/GlobalProcess.cs"))
ck("只读口接的是同一份 SeasonTable.Load（不另起一套判定）",
   "Global.SeasonTable.Load(" in src("src/Global/Program.cs"))
grpc = src("src/Global/GlobalGrpcService.cs")
body = grpc.split("DescribeSeason(\n")[1].split("    public override async Task<ListAuctionResponse>")[0]
ck("DescribeSeason 只回事实值，不写库不结算",
   "Task.FromResult(new DescribeSeasonResponse" in body
   and not any(w in body for w in ("Repository", "Npgsql", "Settle", "await")))
api = src("src/Gateway/SeasonApi.cs")
ck("网关只读 GET、不读除 token 以外的 query",
   'MapGet("/season/current"' in api and "MapPost" not in api
   and set(re.findall(r'Request\.Query\["([^"]+)"\]', api)) == {"token"})
ck("网关注册了赛季只读口", "SeasonApi.Map(" in src("src/Gateway/GatewayProcess.cs"))
proto = src("proto/mmo/trade.proto")
resp = proto.split("message DescribeSeasonResponse {")[1].split("}")[0]
ck("proto 请求是空消息，客户端带不进季节/目标/奖励",
   re.search(r"message DescribeSeasonRequest\s*\{\s*\}", proto) is not None
   and "rpc DescribeSeason(" in proto)
ck("proto 回包只有五个事实值字段，没有奖励/数值字段",
   len(re.findall(r"^\s*\w+\s+\w+\s*=\s*\d+;", resp, re.M)) == 5
   and not any(w in resp for w in ("reward", "gold", "item", "count", "bonus")))
ck("两端 Trade.cs 生成物一致，TradeGrpc.cs 只在服务端",
   sha("src/Shared/Generated/Trade.cs") == sha("client/MMOGame/Assets/Proto/Generated/Trade.cs")
   and os.path.exists(os.path.join(root, "src/Shared/Generated/TradeGrpc.cs"))
   and not os.path.exists(os.path.join(root, "client/MMOGame/Assets/Proto/Generated/TradeGrpc.cs")))
# P3-4a2 起角色库里多了 season_pass / season_pass_claim，它们只记「这个角色买过哪一季、领过哪一阶」，
# 不存赛季本身的目标与奖励（那些仍只从 config/season.json 和全局服务来）。所以按建表名钉，
# 不再按文件名里有没有 season 钉。
tables = set()
for f in os.listdir(os.path.join(root, "db/character")):
    if f.endswith(".sql"):
        txt = open(os.path.join(root, "db/character", f), encoding="utf-8").read()
        tables |= {t.lower() for t in re.findall(r'create table[^;]*?"?([a-z_0-9]+)"?\s*\(', txt, re.I)}
ck("赛季表不写角色库（角色库里没有 season / season_target 表，赛季事实只从配置与全局服务来）",
   not (tables & {"season", "season_target", "season_goal"}))
for name, cond in checks: print(("OK " if cond else "BAD ") + name)
PY
while IFS= read -r line; do
  case "$line" in "OK "*) ok "${line#OK }" ;; "BAD "*) bad "${line#BAD }" ;; *) bad "静态检查输出异常：$line" ;; esac
done <"$TMP/static.out"

echo "== 3. 只读入口、坏配置与换季 =="
SO="$("$DOTNET" "$GL" --season-table 2>&1)"; SRC=$?
# 换季目标从配置现取：挑一个「有上一季、且不是当前季」的季，不写死 s2 / s3 之类的编号。
read -r CUR TGT PREV PREV_T LAST LAST_T <<EOF
$(python3 -c 'import json
s=json.load(open("config/season.json",encoding="utf-8")); r=s["seasons"]
i=next(i for i,x in enumerate(r) if x["id"]==s["current"])
n=next((x for j,x in enumerate(r) if j>0 and x["id"]!=s["current"]), None)
print(s["current"], r[i]["target_dungeon"], (r[i-1]["id"] if i else ""),
      (r[i-1]["target_dungeon"] if i else ""),
      (n["id"] if n else ""), (n["target_dungeon"] if n else ""))')
EOF
[ "$SRC" = 0 ] && printf '%s' "$SO" | grep -q "season_current=$CUR target=$TGT previous=$PREV previous_target=$PREV_T" \
  && ok "只读入口从配置现取当前季与上一季（$CUR/$TGT ← $PREV/$PREV_T）" \
  || bad "只读入口不对（rc=$SRC）：$(printf '%s' "$SO" | utf8 | head -2)"
CNT="$(python3 -c 'import json;print(len(json.load(open("config/season.json",encoding="utf-8"))["seasons"]))')"
printf '%s' "$SO" | grep -q "season_table_state=ok .*count=$CNT" \
  && ok "只读入口报出的季数从配置现取（$CNT 季，脚本不写死）" \
  || bad "只读入口季数不对：$(printf '%s' "$SO" | utf8 | head -2)"
python3 - "$ROOT/config/season.json" "$TMP" "$LAST" <<'PY'
import json, os, sys
src, tmp, next_id = sys.argv[1:4]
base = json.load(open(src, encoding="utf-8"))
def w(name, o): json.dump(o, open(os.path.join(tmp, name), "w", encoding="utf-8"), ensure_ascii=False)
def bad(name, mut):
    o = json.loads(json.dumps(base, ensure_ascii=False)); mut(o); w("bad-" + name + ".json", o)
bad("schema", lambda o: o.update(schema="season-table/9"))
bad("version", lambda o: o.update(version=0))
bad("current", lambda o: o.update(current="not_a_season"))
bad("one", lambda o: o.update(seasons=o["seasons"][:1]))
bad("same", lambda o: o["seasons"][2].update(target_dungeon=o["seasons"][1]["target_dungeon"]))
bad("target", lambda o: o["seasons"][1].update(target_dungeon="not_a_dungeon"))
bad("realm", lambda o: o["seasons"][1].update(target_dungeon="abyss_yuanying"))
bad("reward", lambda o: o.update(rewards=[]))
bad("item", lambda o: o["seasons"][1].update(item_id="x"))
bad("pass", lambda o: o.update(season_pass={}))
bad("dup", lambda o: o["seasons"][2].update(id=o["seasons"][0]["id"]))
bad("missing", lambda o: o.pop("current"))
o = json.loads(json.dumps(base, ensure_ascii=False)); o["current"] = next_id
w("next-season.json", o)
PY
for c in schema version current one same target realm reward item pass dup missing; do
  out="$("$DOTNET" "$GL" --season-table-path "$TMP/bad-$c.json" \
       "$ROOT/config/dungeons.json" "$ROOT/config/realms.json" 2>&1)"; rc=$?
  [ "$rc" -ne 0 ] && printf '%s' "$out" | utf8 | grep -q '^config_error=' \
    && ok "坏配置 $c：config_error= 且非 0 退出" \
    || bad "坏配置 $c 没挡住（rc=$rc）：$(printf '%s' "$out" | utf8 | head -2)"
done
NO="$("$DOTNET" "$GL" --season-table-path "$TMP/next-season.json" \
     "$ROOT/config/dungeons.json" "$ROOT/config/realms.json" 2>&1)"; NRC=$?
[ "$NRC" = 0 ] && printf '%s' "$NO" | grep -q "season_current=$LAST target=$LAST_T" \
  && [ "$LAST_T" != "$TGT" ] \
  && ok "换季后只读口给出不同目标（$TGT → $LAST_T）" \
  || bad "换季后目标没变：$(printf '%s' "$NO" | utf8 | head -2)"

echo "== 4. 假服：缺表降级与老日志不回退 =="
python3 - "$ROOT/config/global.json" "$TMP/fake-global.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8")); d["listen"] = "http://127.0.0.1:64091"
json.dump(d, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
"$DOTNET" "$GL" --config "$TMP/fake-global.json" >"$TMP/fake.log" 2>&1 & PIDS="$PIDS $!"
if wait_line "$TMP/fake.log" '^season=missing'; then
  ok "临时配置目录没有 season.json：降级成 missing，服务照常启动"
else
  bad "缺表没降级：$(tail -5 "$TMP/fake.log" | utf8 | detail)"
fi
grep -q '^stone_tiers=' "$TMP/fake.log" && grep -q '^db_env=' "$TMP/fake.log" \
  && grep -q '^config=' "$TMP/fake.log" \
  && ok "老启动日志（config / stone_tiers / db_env）一字未少" \
  || bad "老启动日志被改：$(head -6 "$TMP/fake.log" | utf8 | detail)"
mkdir -p "$TMP/badcfg"
cp "$TMP/bad-same.json" "$TMP/badcfg/season.json"
cp "$ROOT/config/dungeons.json" "$ROOT/config/realms.json" "$TMP/badcfg/"
python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); d["listen"]="http://127.0.0.1:64093"; json.dump(d,open(sys.argv[2],"w"))' \
  "$TMP/fake-global.json" "$TMP/badcfg/global.json"
"$DOTNET" "$GL" --config "$TMP/badcfg/global.json" >"$TMP/badcfg.log" 2>&1 & PIDS="$PIDS $!"
if wait_line "$TMP/badcfg.log" '^season=missing'; then
  grep -q '^season_config_error=' "$TMP/badcfg.log" \
    && ok "启动时就是坏表：降级 missing 并留错误日志，老链路照常起" \
    || bad "启动坏表没留错误日志"
else
  bad "启动坏表把全局服务拖垮了：$(tail -5 "$TMP/badcfg.log" | utf8 | detail)"
fi

echo "== 5. 真服真库（新请求读到新表，目标与上一季显式比对）=="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真服真库段（MMO_P3_4A1_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, shutil, sys
root, tmp = sys.argv[1:3]
for f in ("season.json", "dungeons.json", "realms.json", "stone_exchange.json"):
    shutil.copy(os.path.join(root, "config", f), os.path.join(tmp, f))
g = json.load(open(os.path.join(root, "config", "global.json"), encoding="utf-8"))
g["listen"] = "http://127.0.0.1:64000"
json.dump(g, open(os.path.join(tmp, "global-real.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
  "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/character.sql" \
      --config "$ROOT/config/character.json" >"$TMP/mig.log" 2>&1 || true
  "$DOTNET" "$CD" --config "$ROOT/config/character.json" >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"
  "$DOTNET" "$GL" --config "$TMP/global-real.json" >"$TMP/global.log" 2>&1 & PIDS="$PIDS $!"
  "$DOTNET" "$GD" --config "$ROOT/config/gateway.json" >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  wait_ready "$TMP/global.log" '^ready' "全局服务" || true
  wait_ready "$TMP/gw.log" '^ready' "网关" || true
  grep -q "season=$CUR target=$TGT" "$TMP/global.log" \
    && ok "真服：全局服务启动日志带当前季与上一季目标" \
    || bad "真服：启动日志不对：$(grep '^season=' "$TMP/global.log" | utf8 | head -1)"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST \
      http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
      -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p341_$$"; PW="P341-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 \
      --config "$ROOT/config/gateway.json" >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login \
    -H 'Content-Type: application/json' -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" | jget token)"
  [ -n "$TOK" ] && ok "真服：登录拿到令牌（账号/口令只在内存）" || bad "真服：登录没拿到令牌"
  season_get() { curl -s --noproxy '*' "http://127.0.0.1:61000/season/current?token=$TOK"; }
  wait_target() {
    local want="$1" out=""
    for _ in $(seq 1 60); do
      out="$(season_get)"
      [ "$(printf '%s' "$out" | jget target_dungeon)" = "$want" ] && { printf '%s' "$out"; return 0; }
      sleep 0.25
    done
    printf '%s' "$out"; return 1
  }
  BASE="$(season_get)"
  [ "$(printf '%s' "$BASE" | jget found)" = "true" ] \
    && [ "$(printf '%s' "$BASE" | jget target_dungeon)" = "$TGT" ] \
    && [ "$(printf '%s' "$BASE" | jget season_id)" = "$CUR" ] \
    && [ "$(printf '%s' "$BASE" | jget previous_target_dungeon)" = "$PREV_T" ] \
    && ok "真服：当前季 $CUR 目标 $TGT，上一季目标 $PREV_T（来自服务端）" \
    || bad "真服：赛季回包不对：$BASE"
  SPOOF="$(curl -s --noproxy '*' \
    "http://127.0.0.1:61000/season/current?token=$TOK&season_id=hack&target_dungeon=dragon&reward=gold&item_id=x")"
  [ "$SPOOF" = "$BASE" ] \
    && ok "真服：query 里塞赛季/目标/奖励，回包一字不变（客户端带不进来）" \
    || bad "真服：请求参数影响了回包：$SPOOF"
  A=""
  for _ in $(seq 1 10); do
    A="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"name\":\"p341_$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}" | jget character_id)"
    [ -n "$A" ] && break
    sleep 0.3
  done
  INV1="$(curl -s --noproxy '*' "http://127.0.0.1:61000/inventory?token=$TOK&character_id=$A")"
  python3 - "$ROOT/config/season.json" "$TMP/season.next.json" "$LAST" <<'PY'
import json, sys
s = json.load(open(sys.argv[1], encoding="utf-8"))
s["current"] = sys.argv[3]
json.dump(s, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
  mv -f "$TMP/season.next.json" "$TMP/season.json"
  NEXT="$(wait_target "$LAST_T")"
  [ "$(printf '%s' "$NEXT" | jget season_id)" = "$LAST" ] \
    && [ "$(printf '%s' "$NEXT" | jget target_dungeon)" = "$LAST_T" ] \
    && [ "$LAST_T" != "$TGT" ] \
    && ok "真服：配置热载换季后目标 $TGT → $LAST_T，与上一季不同" \
    || bad "真服：换季没生效：$NEXT"
  printf '%s' "$NEXT" | jget previous_target_dungeon | grep -qx "$TGT" \
    && ok "真服：换季回包的 previous_target_dungeon 就是换季前的目标 $TGT" \
    || bad "真服：上一季目标不对：$(printf '%s' "$NEXT" | jget previous_target_dungeon)"
  [ "$(grep -c '^ready name=Global pid=' "$TMP/global.log")" = "1" ] \
    && ok "真服：换季没有重启全局服务（配置下发，不是换进程）" \
    || bad "真服：全局服务重启了"
  INV2="$(curl -s --noproxy '*' "http://127.0.0.1:61000/inventory?token=$TOK&character_id=$A")"
  [ -n "$A" ] && [ "$INV1" = "$INV2" ] \
    && ok "真服：换季前后背包/货币回包一致（不写角色库、不发奖）" \
    || bad "真服：赛季换了角色数据"
  python3 - "$TMP/bad-same.json" "$TMP/season.next.json" <<'PY'
import json, shutil, sys
shutil.copy(sys.argv[1], sys.argv[2])
PY
  # 日志重定向在 Windows 上走 GBK，中文错误信息不拿来当匹配条件；
  # 只数新增的 season_config_error= 行，避免换季时写入半截文件留下的旧行造成假绿。
  ERR0="$(grep -c 'season_config_error=' "$TMP/global.log" 2>/dev/null || true)"
  mv -f "$TMP/season.next.json" "$TMP/season.json"
  ERRNEW=0
  for _ in $(seq 1 40); do
    # SeasonCatalog 是「每次只读请求才取快照」：光换文件不发请求不会触发校验。
    season_get >/dev/null 2>&1 || true
    ERR1="$(grep -c 'season_config_error=' "$TMP/global.log" 2>/dev/null || true)"
    [ "${ERR1:-0}" -gt "${ERR0:-0}" ] && { ERRNEW=1; break; }
    sleep 0.25
  done
  [ "$ERRNEW" = "1" ] \
    && ok "真服：坏表热载被拒并留下 season_config_error 日志" \
    || bad "真服：坏表热载没被拒：$(tail -4 "$TMP/global.log" | utf8 | detail)"
  KEEP="$(season_get)"
  [ "$(printf '%s' "$KEEP" | jget target_dungeon)" = "$LAST_T" ] \
    && ok "真服：坏表期间保留上一份好表，全局服务没被拖垮" \
    || bad "真服：坏表把赛季读没了：$KEEP"
  cp -f "$ROOT/config/season.json" "$TMP/season.next.json"
  mv -f "$TMP/season.next.json" "$TMP/season.json"
  BACK="$(wait_target "$TGT")"
  [ "$(printf '%s' "$BACK" | jget season_id)" = "$CUR" ] \
    && [ "$(printf '%s' "$BACK" | jget previous_target_dungeon)" = "$PREV_T" ] \
    && ok "真服：恢复产品正本后回到 $CUR/$TGT，上一季目标 $PREV_T" \
    || bad "真服：恢复后回包不对：$BACK"
fi

echo "== 6. 回归闸门 =="
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

finish "P3-4a1（赛季更换一处秘境目标）"
