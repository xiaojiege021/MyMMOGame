#!/usr/bin/env bash
# C3 洞府摆放（方案 12.1 / 开发架构 4.4、13 节第 629 行 / 12 节第 410 行 / 14 节第 729 行）。
# 用法：bash tools/check/check-c3.sh；真服真库另给 MMO_C3_HAS_DB=1 与 MMO_GAMEDB。
# 完成标准：P3-4a2 发到背包的洞府摆件能摆到配置格位、访客只读可见、摆件不消耗、
# 摆不进没拥有 / 不是摆件 / 不在格位表的东西、同件只占一格、事务号幂等、
# 老配置与老规则一条不改、数值全在配置。
set -uo pipefail
# 本机设了代理时 .NET 会把 127.0.0.1 的明文 HTTP/2 请求也送进代理；curl 一律 --noproxy '*'。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
json_field_stdin() { python3 -c 'import json,sys
try:
    v = json.load(sys.stdin).get(sys.argv[1], "")
    if isinstance(v, bool): v = str(v).lower()
    print(v)
except Exception:
    print("")' "$1"; }
DOTNET="${DOTNET:-}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
SOCIAL_DLL="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
CHARACTER_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GLOBAL_DLL="$ROOT/src/Global/bin/Release/net10.0/Global.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
ACCOUNT="c3_$$"; CNAME="c3a_$$"; VISITOR="c3v_$$"
TEST_PASSWORD="c3-Pw-7c19"; VISITOR_PASSWORD="c3v-Pw-7c19"
HAS_DB="${MMO_C3_HAS_DB:-0}"
TMP="$(mktemp -d)"; TMP="${TMP//\\//}"
PIDS=""; HAVE_DB=""
# Windows 上 dotnet 重定向输出是 GBK；中文断言按平台转码，禁止「先试 UTF-8 再退 GBK」。
utf8() { case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) python3 -c 'import sys; sys.stdout.buffer.write(sys.stdin.buffer.read().decode("gbk","replace").encode("utf-8"))' ;; *) cat ;; esac; }
cleanup() {
  local p
  for p in $PIDS; do stop_pid "$p"; done
  if [ -n "$HAVE_DB" ]; then
    "$DOTNET" "$CHARACTER_DLL" --delete-character "$CNAME" --config config/character.json >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" --config config/gateway.json >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$VISITOR" --config config/gateway.json >/dev/null 2>&1 || true
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

echo "== 2. 静态断言（内容正本 / 结构保证 / 老卡不改） =="
python3 - "$ROOT" "$TMP" >"$TMP/static.out" 2>&1 <<'PY'
import hashlib, json, os, re, subprocess, sys
root, tmp = sys.argv[1:3]
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
def sha(p): return hashlib.sha256(open(os.path.join(root, p), "rb").read()).hexdigest()
checks = []
def ck(name, cond): checks.append((name, bool(cond)))

html = src("docs/MMO系统架构方案.html")
h = cfg("config/homestead.json")
items = cfg("config/items.json")["items"]
pass_cfg = cfg("config/season_pass.json")
by_id = {x["id"]: x for x in items}
decor = {x["id"] for x in items if x["kind"] == "home_decor" and x["bind"] == "always"}
pass_decor = {r["item_id"] for s in pass_cfg["seasons"] for st in s["paid"] for r in st["rewards"] if r["kind"] == "home_decor"}
ban = {"reward", "drop", "loot", "gold", "exp", "equip", "fatigue", "price", "probability",
       "tax", "spirit_stone", "vein", "home_coin", "currency", "attribute", "damage", "owner", "count"}

ck("方案 12.1 写定洞府摆件只做展示和生活、家园币只换外观和便捷",
   all(s in html for s in ("洞府与家园", "洞府外观、摆件、家园装饰", "只做展示和生活", "家园币仍只换外观和便捷")))
ck("开发架构 4.4 / 12 节 / 13 节 / 14 节的 C3 口径都在",
   all(s in src("docs/开发架构.md") for s in ("摆放和访客可见", "只加内容和配置，不改前面任务卡已经完成的规则",
       "C3 洞府摆放：等 P3-4a2 的洞府摆件能发货之后再派", "C3 排在第 68 行之后")))
ck("内容表 schema / version / pending 正确，顶层与条目白名单",
   h["schema"] == "homestead-table/1" and h["version"] == 1 and h.get("pending") is True
   and set(h) <= {"schema", "version", "pending", "note", "items_config", "slots"}
   and all(set(s) <= {"id"} for s in h["slots"]) and not (ban & set(h))
   and all(not (ban & set(s)) for s in h["slots"]))
ck("格位编号不重复、只小写字母数字下划线",
   len({s["id"] for s in h["slots"]}) == len(h["slots"])
   and all(re.fullmatch(r"[a-z0-9_]+", s["id"]) for s in h["slots"]))
ck("允许摆件只来自 items.json 的 home_decor + bind=always，且与 P3-4a2 发货的完全一致",
   len(decor) > 0 and decor == pass_decor and all(by_id[i]["kind"] == "home_decor" for i in decor))
# K1 起商城清单（config/shop.json）、角色服与 character.proto 都在后续卡里正常改动，
# 这条只钉「C3 当时没收的那几条链路没被顺手改」：社交 / 扇区 / 网关 / 客户端业务代码。
ck("「只加内容和配置」：C3 没碰的老配置 / 社交链路 / 客户端业务代码一行未改",
   subprocess.run(["git", "diff", "--name-only", "HEAD", "--",
     "config/season_pass.json", "config/social.json", "config/sect.json",
     "config/gateway.json", "config/mainline.json", "config/gather_spread.json",
     # 14.8 D3h：世界服 Program / WorldGrpcService 按域拆分，这两个文件与它们的分片
     # 目录不在 C3 的「没碰」口径里；其余 src/World 文件仍要求一行未改。
     "src/World", ":!src/World/Program.cs", ":!src/World/WorldGrpcService.cs",
     "src/Battle", "client/MMOGame/Assets/Net",
     "client/MMOGame/Assets/HotUpdate", "client/MMOGame/Assets/Editor",
     # D3h：check-p2-1a1 的整目录读改成下钻一层（社交服按域分片），断言正文没动，同域放行。
     "tools/check/check-u7.sh"],
     cwd=root, capture_output=True, text=True).stdout.strip() == "")
ck("social.proto 只新增三个 RPC 与新消息，旧消息一字未改",
   all(s in src("proto/mmo/social.proto") for s in ("rpc PlaceHomeDecor(", "rpc RemoveHomeDecor(", "rpc DescribeHomestead("))
   and "rpc CreateSect(" in src("proto/mmo/social.proto"))
ck("两端 Social.cs 逐字相同，服务端 SocialGrpc.cs 带新 RPC",
   sha("src/Shared/Generated/Social.cs") == sha("client/MMOGame/Assets/Proto/Generated/Social.cs")
   and "PlaceHomeDecor" in src("src/Shared/Generated/SocialGrpc.cs"))
ck("网关注册 HomesteadApi，请求体只有角色号 / 格位 / 物品编号，事务号服务端生成",
   "HomesteadApi.Map(app" in src("src/Gateway/GatewayProcess.cs")
   and 'record PlaceBody(ulong? CharacterId, string? SlotId, string? ItemId)' in src("src/Gateway/HomesteadApi.cs")
   and '"homestead_place:" + Guid.NewGuid()' in src("src/Gateway/HomesteadApi.cs")
   and 'MapGet("/homestead"' in src("src/Gateway/HomesteadApi.cs")
   and "DescribeHomesteadRequest" in src("src/Gateway/HomesteadApi.cs"))
repo = src("src/Social/HomesteadRepository.cs")
# C# 源码里 SQL 的表名带转义反斜杠（P3-4b 踩过），按原始字节取表名，别拿无斜杠的串去 in。
ops = re.findall(r'(?:insert into|delete from)\s+\\"社交\\"\.(\w+)', repo)
ck("社交服只写社交 schema、摆放不消耗背包：写操作只落 homestead 两表，不算数值不发奖",
   bool(ops) and set(ops) <= {"homestead_placement", "homestead_txn"}
   and "inventory (" not in repo
   and not any(s in repo for s in ('"角色"', "damage", "exp_settle", "gold", "reward"))
   and "ListInventory" in src("src/Social/HomesteadInventory.cs"))
ck("加载期硬校验与缺表降级：白名单 / 禁键 / 摆件发货前置 / LoadIfPresent",
   all(s in src("src/Social/HomesteadTable.cs") for s in ("RequireKeys", "BanKeys", "LoadIfPresent", "没有 home_decor"))
   and "homestead=missing" in src("src/Social/SocialProcess.cs")
   and "homestead_config=ok slots=" in src("src/Social/SocialProcess.cs"))
ck("代码里没有第二套格数 / 格位编号 / 摆件编号",
   not any(s in src("src/Social/HomesteadTable.cs") + src("src/Social/HomesteadRepository.cs") for s in
           ("court_1", "decor_s2", "slots = 6", "slots=6")))
ck("新增连库入口都登记进「一次性命令」长条件",
   "homesteadStateOf != 0 || homesteadPlace is not null || homesteadRemove is not null" in src("src/Social/Program.cs"))
ck("社交 schema 两张表 + 同件只占一格的唯一索引，主迁移追加同一份幂等 DDL",
   all(s in src("db/social/homestead.sql") for s in ("homestead_placement", "homestead_txn", "homestead_placement_item"))
   and 'CREATE TABLE IF NOT EXISTS "社交".homestead_txn' in src("db/social/social.sql"))
ck("check-all.sh 已挂 c3（依赖 P3-4a2 / P1-1b3 / P2-1a1）",
   "c3)" in src("tools/check/check-all.sh") and "c1 c2 c3" in src("tools/check/check-all.sh"))
for name, cond in checks:
    print(("OK " if cond else "BAD ") + name)
PY
while IFS= read -r line; do
  case "$line" in "OK "*) ok "${line#OK }" ;; "BAD "*) bad "${line#BAD }" ;; *) bad "静态检查输出异常：$line" ;; esac
done <"$TMP/static.out"
echo "== 3. 只读入口（与启动共用同一份 Load） =="
OUT="$("$DOTNET" "$SOCIAL_DLL" --homestead-config --config config/social.json 2>&1 | utf8)"; rc=$?
EXPECT="homestead_config_state=ok version=1 pending=true slots=6"
if [ "$rc" = "0" ] && printf '%s' "$OUT" | grep -qF "$EXPECT" \
   && printf '%s' "$OUT" | grep -qF 'items=3 item_ids=decor_s1,decor_s2,decor_s3'; then
  ok "--homestead-config：6 格、3 件摆件全从配置现取，pending=true"
else
  bad "--homestead-config 输出不对（rc=${rc}）：${OUT:0:180}"
fi
cp config/social.json "$TMP/social.json"
MISS="$("$DOTNET" "$SOCIAL_DLL" --homestead-config --config "$TMP/social.json" 2>&1 | utf8)"; rc=$?
[ "$rc" = "0" ] && printf '%s' "$MISS" | grep -qF 'homestead_config_state=missing' \
  && ok "缺表降级：临时目录没有 homestead.json，社交服只读口照常工作" \
  || bad "缺表没有降级（rc=${rc}）：${MISS:0:120}"
# 改配置后只读口跟着变，证明格位数只在配置里、代码没有第二套。
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1:3]
d = json.load(open(os.path.join(root, "config/homestead.json"), encoding="utf-8"))
d["slots"].pop()
json.dump(d, open(os.path.join(tmp, "hot.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
HOT="$("$DOTNET" "$SOCIAL_DLL" --homestead-config --homestead-table "$TMP/hot.json" --config config/social.json 2>&1 | utf8)"
printf '%s' "$HOT" | grep -qF 'slots=5' \
  && ok "改配置：只读口从 6 格变 5 格，格位数没有写死在代码里" \
  || bad "改配置没被读到：${HOT:0:150}"
echo "== 4. 坏配置（加载期就起不来） =="
python3 - "$ROOT" "$TMP" <<'PY'
import copy, json, os, sys
root, tmp = sys.argv[1:3]
base = json.load(open(os.path.join(root, "config/homestead.json"), encoding="utf-8"))
def bad(name, mut, want):
    d = copy.deepcopy(base); mut(d)
    json.dump(d, open(os.path.join(tmp, "bad-%s.json" % name), "w", encoding="utf-8"), ensure_ascii=False)
    open(os.path.join(tmp, "bad-%s.expect" % name), "w", encoding="utf-8").write(want)
bad("schema", lambda d: d.update(schema="homestead-table/2"), "homestead-table/1")
bad("version", lambda d: d.update(version=2), "version")
bad("top-reward", lambda d: d.update(reward=[]), "reward")
bad("top-coin", lambda d: d.update(home_coin=10), "home_coin")
bad("top-items", lambda d: d.update(items=[]), "items")
bad("slot-probability", lambda d: d["slots"][0].update(probability=50), "probability")
bad("slot-count", lambda d: d["slots"][0].update(count=2), "count")
bad("slot-owner", lambda d: d["slots"][0].update(owner=7), "owner")
bad("no-slots", lambda d: d.pop("slots"), "slots")
bad("empty-id", lambda d: d["slots"][0].update(id=""), "homestead.json")
bad("bad-id", lambda d: d["slots"][0].update(id="Court-1"), "homestead.json")
bad("dup-id", lambda d: d["slots"][1].update(id=d["slots"][0]["id"]), "homestead.json")
bad("too-many", lambda d: d.update(slots=[{"id": "s%d" % i} for i in range(65)]), "slots")
bad("not-object", lambda d: d["slots"].append("garden_3"), "homestead.json")
bad("items-missing", lambda d: d.update(items_config="config/no-such-items.json"), "items.json")
bad("items-no-decor", lambda d: d.update(items_config=os.path.join(tmp, "no-decor.json").replace("\\", "/")), "home_decor")
json.dump({"items": [{"id": "ore_iron", "kind": "material", "bind": "none"}]},
          open(os.path.join(tmp, "no-decor.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
for f in "$TMP"/bad-*.json; do
  n="${f##*/}"; n="${n%.json}"
  want="$(cat "$TMP/$n.expect")"
  out="$("$DOTNET" "$SOCIAL_DLL" --homestead-config --homestead-table "$f" --config config/social.json 2>&1 | utf8)"; rc=$?
  if [ "$rc" != "0" ] && printf '%s' "$out" | grep -qF 'config_error=' && printf '%s' "$out" | grep -qF "$want"; then
    ok "坏配置 ${n#bad-} 被加载期挡住（rc=${rc}）"
  else
    bad "坏配置 ${n#bad-} 没挡住（rc=${rc}）：$(printf '%s' "$out" | head -2 | tr '\n' ' ')"
  fi
done
echo "== 5. 真服真库（MMO_C3_HAS_DB=1 才跑） =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  echo "  ..   跳过（没有 MMO_C3_HAS_DB=1 或 MMO_GAMEDB）"
else
  if "$DOTNET" "$GATEWAY_DLL" --migrate --sql db/character/account.sql --config config/gateway.json >"$TMP/m1.log" 2>&1 \
     && "$DOTNET" "$CHARACTER_DLL" --migrate --config config/character.json >"$TMP/m2.log" 2>&1 \
     && "$DOTNET" "$SOCIAL_DLL" --migrate --sql db/social/social.sql --config config/social.json >"$TMP/m3.log" 2>&1 \
     && printf '%s\n' "$TEST_PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$ACCOUNT" --seed-slots 8 --config config/gateway.json >"$TMP/seed.log" 2>&1 \
     && printf '%s\n' "$VISITOR_PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$VISITOR" --seed-slots 8 --config config/gateway.json >"$TMP/seed2.log" 2>&1; then
    HAVE_DB=1
    ok "账号 / 角色 / 社交三张主迁移就绪，主人与访客两个账号已播种（唯一，不撞真库老数据）"
  else
    bad "建表或播种失败"; tail -6 "$TMP"/m*.log "$TMP"/seed*.log | detail
  fi
  if [ -n "$HAVE_DB" ]; then
    "$DOTNET" "$CHARACTER_DLL" --config config/character.json >"$TMP/character.log" 2>&1 & PIDS="$PIDS $!"
    "$DOTNET" "$GLOBAL_DLL" --config config/global.json >"$TMP/global.log" 2>&1 & PIDS="$PIDS $!"
    "$DOTNET" "$SOCIAL_DLL" --config config/social.json >"$TMP/social.log" 2>&1 & PIDS="$PIDS $!"
    "$DOTNET" "$GATEWAY_DLL" --config config/gateway.json >"$TMP/gateway.log" 2>&1 & PIDS="$PIDS $!"
    if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
       && wait_ready "$TMP/global.log" '^ready name=Global' "全局服务" \
       && wait_ready "$TMP/social.log" '^ready name=Social' "社交服" \
       && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关"; then
      ok "角色服 + 全局服 + 社交服 + 网关四个进程都起来（真库 gamedb）"
    else
      bad "真服没起来"; finish "C3（洞府摆放）"
    fi
    if grep -qaF 'homestead_config=ok slots=6 items=3' "$TMP/social.log" \
       && grep -qaF 'sect_table_version=' "$TMP/social.log"; then
      ok "启动日志：新行 homestead_config=ok 与 P2-1 老行同时在"
    else
      bad "启动日志的洞府配置不对"; grep -aE '^(homestead|sect_table_version|config_error)' "$TMP/social.log" | detail
    fi
    post() { curl -s -m 10 --noproxy '*' -w '\n%{http_code}' -X POST "$@"; }
    get()  { curl -s -m 10 --noproxy '*' "$@"; }
    for _ in $(seq 1 120); do
      code="$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' http://127.0.0.1:61000/health 2>/dev/null)"
      [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
    done
    TOKEN="$(get -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
      -d "{\"account\":\"$ACCOUNT\",\"password\":\"$TEST_PASSWORD\"}" | json_field_stdin token)"
    [ -n "$TOKEN" ] || { bad "登录拿不到令牌"; finish "C3（洞府摆放）"; }
    CR="$(post "http://127.0.0.1:61000/character?token=$TOKEN" -H 'Content-Type: application/json' \
      -d "{\"name\":\"$CNAME\",\"race\":\"mai\",\"class_id\":\"warrior\"}")"
    CID="$(printf '%s' "$(body_of "$CR")" | json_field_stdin character_id)"
    [ -n "$CID" ] || { bad "建角色失败：$(body_of "$CR")"; finish "C3（洞府摆放）"; }
    ok "登录与建角成功（角色号 ${CID}，每次唯一）"
    # 从配置现取当季与付费轨那件洞府摆件，不写死编号。
    read -r CUR DECOR STAGE < <(python3 -c 'import json
s=json.load(open("config/season.json",encoding="utf-8"))["current"]
p=json.load(open("config/season_pass.json",encoding="utf-8"))
g=next(x for x in p["seasons"] if x["season"]==s)
r=[(st["stage"],rw["item_id"]) for st in g["paid"] for rw in st["rewards"] if rw["kind"]=="home_decor"][0]
print(s,r[1],r[0])')
    CUR="${CUR%$'\r'}"; DECOR="${DECOR%$'\r'}"; STAGE="${STAGE%$'\r'}"
    curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/currency/add?token=$TOKEN" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"kind\":\"yuanbao\",\"delta\":1000}" >/dev/null
    BUY="$(post "http://127.0.0.1:61000/shop/buy?token=$TOKEN" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"item_id\":\"shop_season_pass_1\"}")"
    CLAIM="$(post "http://127.0.0.1:61000/season/pass/claim?token=$TOKEN" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"track\":\"paid\",\"stage\":$STAGE}")"
    [ "$(printf '%s' "$(body_of "$BUY")" | json_field_stdin state)" = "pass_activated" ] \
      && [ "$(printf '%s' "$(body_of "$CLAIM")" | json_field_stdin item_id)" = "$DECOR" ] \
      && ok "P3-4a2 发货链路：买 $CUR 付费轨、领第 $STAGE 阶拿到 ${DECOR}（摆放卡的前置已满足）" \
      || bad "摆件没发到背包：buy=$(body_of "$BUY") claim=$(body_of "$CLAIM")"
    bag() { get "http://127.0.0.1:61000/inventory?token=$TOKEN&character_id=$CID" | python3 -c 'import json,os,sys
try: print(sum(s["count"] for s in json.load(sys.stdin)["slots"] if s["item_id"] == os.environ["ITEM"]))
except Exception: print("-1")'; }
    [ "$(DECOR="$DECOR" ITEM="$DECOR" bag)" = "1" ] \
      && ok "背包里 $DECOR ×1（绑定态，角色服是唯一写入）" || bad "背包摆件数量不对"
    home() { get "http://127.0.0.1:61000/homestead?token=$1&owner=$CID"; }
    place() { post "http://127.0.0.1:61000/homestead/place?token=$1" -H 'Content-Type: application/json' -d "$2"; }
    R="$(home "$TOKEN")"
    [ "$(expect_json "$R" ok=true slot_count=6)" = "ok" ] \
      && [ "$(printf '%s' "$R" | python3 -c 'import json,sys;print(len(json.load(sys.stdin)["placements"]))')" = "0" ] \
      && ok "摆放前只读口：6 格全空（访客可见的是事实值，不是配置顶格数）" || bad "初始摆放不对：$R"
    for case in "not-owned:decor_s1:court_1:decor_not_owned" "not-decor:ore_iron:court_1:item_not_decor" \
                "unknown-slot:$DECOR:no_such_slot:slot_unknown"; do
      IFS=: read -r label item slot want <<<"$case"
      B="$(body_of "$(place "$TOKEN" "{\"character_id\":$CID,\"slot_id\":\"$slot\",\"item_id\":\"$item\"}")")"
      [ "$(printf '%s' "$B" | json_field_stdin reason)" = "$want" ] \
        && ok "拒绝 ${label}：${want}，且不写任何行" || bad "$label 没被挡：$B"
    done
    SPOOF="$(body_of "$(place "$TOKEN" "{\"character_id\":$CID,\"slot_id\":\"court_1\",\"item_id\":\"$DECOR\",\"owner\":999,\"txn_id\":\"forged\",\"count\":99,\"home_coin\":999}")")"
    [ "$(expect_json "$SPOOF" ok=true slot_id=court_1 item_id="$DECOR")" = "ok" ] \
      && [ "$(printf '%s' "$SPOOF" | json_field_stdin txn_id)" != "forged" ] \
      && ok "请求体塞 owner / 事务号 / 数量 / 家园币都无效，事务号仍由网关生成" \
      || bad "改包影响了摆放：$SPOOF"
    [ "$(DECOR="$DECOR" ITEM="$DECOR" bag)" = "1" ] \
      && ok "摆放不消耗背包：摆件仍在背包里 ×1（社交服只读问角色服）" || bad "摆件被消耗了"
    B="$(body_of "$(place "$TOKEN" "{\"character_id\":$CID,\"slot_id\":\"hall_1\",\"item_id\":\"$DECOR\"}")")"
    [ "$(printf '%s' "$B" | json_field_stdin reason)" = "item_already_placed" ] \
      && ok "同一件摆件在同一个洞府只占一格：第二格被拒" || bad "同件摆件摆了两格：$B"
    B="$(body_of "$(place "$TOKEN" "{\"character_id\":$CID,\"slot_id\":\"court_1\",\"item_id\":\"$DECOR\"}")")"
    [ "$(expect_json "$B" ok=true duplicate=false)" = "ok" ] \
      && ok "同格重摆仍成功（展示事实 upsert，不发第二份奖励）" || bad "同格重摆失败：$B"
    R="$(home "$TOKEN")"
    [ "$(printf '%s' "$R" | python3 -c 'import json,sys
p=json.load(sys.stdin)["placements"]
print("ok" if len(p)==1 and p[0]["slot_id"]=="court_1" and p[0]["item_id"]=="'"$DECOR"'" else "bad")')" = "ok" ] \
      && ok "主人只读口逐格回事实值：court_1 摆着 $DECOR" || bad "摆放回包不对：$R"
    VTOK="$(get -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
      -d "{\"account\":\"$VISITOR\",\"password\":\"$VISITOR_PASSWORD\"}" | json_field_stdin token)"
    VR="$(home "$VTOK")"
    [ "$(printf '%s' "$VR" | python3 -c 'import json,sys
p=json.load(sys.stdin)["placements"]
print(len(p))')" = "1" ] && ok "访客可见：另一个账号的令牌能只读看到这处洞府的摆放" || bad "访客看不到：$VR"
    VR="$(post "http://127.0.0.1:61000/homestead/place?token=$VTOK" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"slot_id\":\"garden_1\",\"item_id\":\"$DECOR\"}")"
    [ "$(code_of "$VR")" = "404" ] \
      && ok "访客改不了别人的洞府：拿别人的角色号去摆被归属复核挡住（404）" \
      || bad "访客改了别人的洞府：$(body_of "$VR")"
    B="$(body_of "$(post "http://127.0.0.1:61000/homestead/remove?token=$TOKEN" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"slot_id\":\"court_1\"}")")"
    [ "$(expect_json "$B" ok=true)" = "ok" ] \
      && [ "$(printf '%s' "$(home "$TOKEN")" | python3 -c 'import json,sys;print(len(json.load(sys.stdin)["placements"]))')" = "0" ] \
      && ok "撤下后格位空，访客只读口同步看不到" || bad "撤下没生效：$B"
    B="$(body_of "$(post "http://127.0.0.1:61000/homestead/remove?token=$TOKEN" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"slot_id\":\"court_1\"}")")"
    [ "$(printf '%s' "$B" | json_field_stdin reason)" = "slot_empty" ] \
      && ok "空格再撤回 slot_empty，不占事务号" || bad "空格撤下没被挡：$B"
    # 固定事务号连发两次：网关生成的号脚本指定不了，走社交服只给核对用的一次性入口。
    TXN="c3_$$_$(date +%s)_replay"
    P1="$("$DOTNET" "$SOCIAL_DLL" --homestead-place "$ACCOUNT" "$CID" hall_1 "$DECOR" "$TXN" --config config/social.json 2>&1 | utf8)"
    P2="$("$DOTNET" "$SOCIAL_DLL" --homestead-place "$ACCOUNT" "$CID" hall_1 "$DECOR" "$TXN" --config config/social.json 2>&1 | utf8)"
    printf '%s' "$P1" | grep -qF 'ok=true reason= duplicate=false' \
      && printf '%s' "$P2" | grep -qF 'reason=duplicate duplicate=true' \
      && ok "同一事务号重放：第一笔生效、第二笔 duplicate=true 不二次写入" \
      || bad "幂等重放不对：p1=${P1:0:120} p2=${P2:0:120}"
    STATE="$("$DOTNET" "$SOCIAL_DLL" --homestead-state "$CID" --config config/social.json 2>&1 | utf8)"
    printf '%s' "$STATE" | grep -qF "rows=1 slots=6 placements=hall_1:$DECOR" \
      && ok "--homestead-state 独立读回社交库：rows=1，摆放与网关回包一致" \
      || bad "独立读回不对：${STATE:0:160}"
  fi
fi
echo "== 6. 回归闸门 =="
if bash tools/check/preflight.sh >"$TMP/preflight.log" 2>&1; then
  ok "preflight.sh 第 4 道闸门绿"
else
  bad "preflight.sh 没过：$(tail -4 "$TMP/preflight.log" | tr '\n' ' ')"
fi
if git diff --check >"$TMP/diffcheck.log" 2>&1; then
  ok "git diff --check 通过"
else
  bad "git diff --check 有空白错误"
fi
finish "C3（洞府摆放）"
