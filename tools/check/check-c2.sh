#!/usr/bin/env bash
# C2 采集节点铺开（方案 9.5，开发架构 13 节第 628 行 / 12 节第 410 行 / 14 节第 729 行）。
#   bash tools/check/check-c2.sh
#   MMO_C2_HAS_DB=1 MMO_GAMEDB='Host=...' bash tools/check/check-c2.sh   加真服真库端到端
# 完成标准：按地表境界区域铺满矿与药、元婴及以后随版本开放、采一次进背包且疲劳不变、
# 灵石矿脉仍拒、请求体带不进产物、老配置与老规则一条不改。
set -uo pipefail
# 本机设了代理时 .NET 会把 127.0.0.1 的明文 HTTP/2 请求也送进代理；curl 一律 --noproxy '*'。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
# 从 stdin 读一个 JSON 字段；bool 保持 Python 的 True/False，和老卡断言同一口径。
json_field_stdin() { python3 -c 'import json,sys
try:
    v = json.load(sys.stdin).get(sys.argv[1], "")
    print(v)
except Exception:
    print("")' "$1"; }
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
WORLD_DLL="$ROOT/src/World/bin/Release/net10.0/World.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
WORLD_PORT=${MMO_C2_WORLD_PORT:-63095}
CHAR_PORT=${MMO_C2_CHAR_PORT:-62110}
GATEWAY_PORT=${MMO_C2_GATEWAY_PORT:-61111}
ACCOUNT="c2_$$"; CNAME="c2a_$$"; TEST_PASSWORD="c2-Pw-7c19"
HAS_DB="${MMO_C2_HAS_DB:-0}"
TMP="$(mktemp -d)"; TMP="${TMP//\\//}"
PIDS=""; HAVE_DB=""
# Windows 上 dotnet 重定向输出走 GBK；中文断言按平台转码，不能「先试 UTF-8 再退 GBK」。
utf8() { case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) python3 -c 'import sys; sys.stdout.buffer.write(sys.stdin.buffer.read().decode("gbk","replace").encode("utf-8"))' ;; *) cat ;; esac; }
cleanup() {
  local p
  for p in $PIDS; do stop_pid "$p"; done
  if [ -n "$HAVE_DB" ]; then
    "$DOTNET" "$CHARACTER_DLL" --delete-character "$CNAME" --config "$TMP/character.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
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
    ok "编译 $n：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done

echo "== 2. 静态断言（内容正本 / 结构保证 / 老卡不改） =="
python3 - "$ROOT" "$TMP" >"$TMP/static.out" 2>&1 <<'PY'
import json, os, re, subprocess, sys
root, tmp = sys.argv[1:3]
def src(p): return open(os.path.join(root, p), encoding="utf-8").read()
def cfg(p): return json.load(open(os.path.join(root, p), encoding="utf-8"))
checks = []
def ck(name, cond): checks.append((name, bool(cond)))

html = src("docs/MMO系统架构方案.html")
sp = cfg("config/gather_spread.json")
base = cfg("config/gather.json")
flight = cfg("config/flight.json")
realms = cfg("config/realms.json")
world = cfg("config/world.json")
items = cfg("config/items.json")["items"]
by_item = {x["id"]: x for x in items}
gates = {x["region_id"]: x["realm"] for x in flight["region_realms"]}
nodes = sp["nodes"]

ck("方案 9.5 写定按地表境界区域刷新、元婴随版本开放、灵石矿脉不个人挖",
   all(s in html for s in ("按地表的境界区域刷新", "元婴之后", "不开放给玩家个人去挖", "采集不耗疲劳")))
ck("铺开表 schema / version / pending 正确",
   sp["schema"] == "gather-spread/1" and sp["version"] == 1 and sp.get("pending") is True)
ban = {"reward", "drop", "loot", "gold", "exp", "equip", "fatigue", "price", "probability", "tax", "spirit_stone", "vein"}
top_ok = set(sp) <= {"schema", "version", "pending", "note", "items_config", "nodes"}
node_ok = all(set(n) <= {"id", "kind", "region_id", "realm", "item_id", "count", "bag_slot", "respawn_s"} for n in nodes)
ck("顶层与条目白名单，奖励 / 概率 / 灵石一类键长不出来",
   top_ok and node_ok and not (ban & set(sp)) and all(not (ban & set(n)) for n in nodes))
ck("铺开表只许 ore / herb，灵石矿脉仍只在 gather.json 一处",
   {n["kind"] for n in nodes} <= {"ore", "herb"}
   and [n["id"] for n in base["nodes"] if n["kind"] == "spirit_stone"] == ["spirit_qiyun_1"])
ck("节点编号不重复、不与 P1-3b 基准表撞",
   len({n["id"] for n in nodes}) == len(nodes)
   and not ({n["id"] for n in nodes} & {n["id"] for n in base["nodes"]}))
ck("每个节点长在 world.json 的已配置地表上", all(n["region_id"] in world["regions"] for n in nodes))
ck("每个节点的 realm 等于所在地表在 flight.json 的境界区域",
   all(n["realm"] == gates.get(n["region_id"]) for n in nodes))
ck("realm 全部落在已开放段内（元婴及以后不铺）",
   all(n["realm"] in flight["realms"] for n in nodes)
   and all(n["realm"] not in ("元婴", "化神", "返虚", "合体", "渡劫", "大乘", "金仙", "极境") for n in nodes))
ck("flight.json 的 realms 与 realms.json 的 launch_until 对得上，不另起一套境界",
   flight["realms"] == realms["realms"][:realms["realms"].index(realms["launch_until"]) + 1])
ck("产物只引用 items.json 里可交易的 material，count 不超堆叠",
   all(by_item.get(n["item_id"], {}).get("kind") == "material"
       and by_item[n["item_id"]]["bind"] == "none"
       and 1 <= n["count"] <= by_item[n["item_id"]]["stack_max"]
       and n["bag_slot"] >= 1 and n["respawn_s"] >= 1 for n in nodes))
all_nodes = list(base["nodes"]) + list(nodes)
ck("合并后 8 块地表每块都有 ore 与 herb（铺开是结构保证）",
   all(any(n["region_id"] == r and n["kind"] == k for n in all_nodes)
       for r in world["regions"] for k in ("ore", "herb")))

# 「只加内容和配置」：老配置正本、proto、客户端、采集判定与老脚本一行不动。
old = subprocess.run(["git", "diff", "--name-only", "HEAD", "--",
    "config/gather.json", "config/items.json", "config/flight.json", "config/realms.json",
    "config/world.json", "config/region", "proto", "client", "db",
    "src/World/WorldGrpcService.cs", "src/World/GatherGrant.cs", "src/World/GatherNodeRegistry.cs",
    "tools/check/check-p1-3b.sh", "tools/check/check-p1-3c.sh", "tools/check/check-p3-2a.sh",
    "tools/check/check-u4.sh"], cwd=root, capture_output=True, text=True).stdout.strip()
ck("老配置 / proto / 客户端 / 采集判定 / 老核对脚本一行未改", old == "")
ck("gather.json 仍是 P1-3b 的四个节点、灵石矿脉一个",
   len(base["nodes"]) == 4 and sum(n["kind"] == "spirit_stone" for n in base["nodes"]) == 1)
ck("老卡写死的 nodes=4 断言仍在（本卡没有去改老脚本）",
   "gather_state=ok nodes=4" in src("tools/check/check-p1-3b.sh")
   and "gather_config=ok nodes=4" in src("tools/check/check-p1-3c.sh")
   and "len(ga[\"nodes\"]) == 4" in src("tools/check/check-p3-2a.sh"))

code = src("src/World/GatherSpreadTable.cs")
ck("加载期硬校验地表 / 境界 / 覆盖 / 可交易材料",
   all(s in code for s in ("RequiredFor(region)", "flights.Realms.Contains(realm", "缺 ore 或 herb 节点",
                           "不是可交易的 material", "RequireKeys")))
ck("代码里没有第二套数字 / 物品编号 / 境界名",
   not any(s in code for s in ("ore_iron", "herb_ling", "炼体", "结丹", "respawn_s = 2", "count = 2")))
ck("proto 与生成物没有 C2 的新字段",
   "spread" not in src("proto/mmo/world.proto").lower()
   and "gather_spread" not in src("src/Shared/Generated/World.cs"))
ck("世界服缺表降级、老日志行仍按基准表报 nodes",
   "LoadIfPresent" in src("src/World/WorldProcess.cs")
   and "gather_spread=missing" in src("src/World/WorldProcess.cs")
   and 'Console.WriteLine("gather_config=ok nodes=" + gather.Nodes.Count' in src("src/World/WorldProcess.cs"))
ck("check-all.sh 已挂 c2", "c2)" in src("tools/check/check-all.sh"))

# 逐块地表的期望事实值，供只读入口段比对。
with open(os.path.join(tmp, "regions.expect"), "w", encoding="utf-8") as f:
    for r in world["regions"]:
        ore = sum(n["region_id"] == r and n["kind"] == "ore" for n in all_nodes)
        herb = sum(n["region_id"] == r and n["kind"] == "herb" for n in all_nodes)
        f.write("region=%s realm=%s ore=%d herb=%d\n" % (r, gates[r], ore, herb))
for name, cond in checks:
    print(("OK " if cond else "BAD ") + name)
PY
while IFS= read -r line; do
  case "$line" in "OK "*) ok "${line#OK }" ;; "BAD "*) bad "${line#BAD }" ;; *) bad "静态检查输出异常：$line" ;; esac
done <"$TMP/static.out"

echo "== 3. 只读入口（与启动共用同一份 Load） =="
OUT="$("$DOTNET" "$WORLD_DLL" --gather-spread-state 2>&1 | utf8)"; rc=$?
if [ "$rc" = "0" ] && printf '%s' "$OUT" | grep -qF 'gather_spread_state=ok nodes=14 regions=8 realms=炼体,结丹 base=4 total=18'; then
  ok "--gather-spread-state：14 个铺开节点 / 8 块地表 / 基准 4 / 合计 18"
else
  bad "--gather-spread-state 输出不对（rc=$rc）：${OUT:0:180}"
fi
MISSING=""
while IFS= read -r want; do
  # python3 在 Windows 写文件是 \r\n，末位字段要剥掉 \r，否则逐块比对全假红。
  want="${want%$'\r'}"
  printf '%s' "$OUT" | grep -qF "$want" || MISSING="$MISSING [$want]"
done <"$TMP/regions.expect"
[ -z "$MISSING" ] && ok "只读入口逐块地表与配置 / flight.json 一致" || bad "只读入口逐块对不上：$MISSING"
OLD="$("$DOTNET" "$WORLD_DLL" --gather-state 2>&1)"
printf '%s' "$OLD" | grep -qF 'gather_state=ok nodes=4 ore=ore_iron:2 herb=herb_ling:3 respawn_s=2 forbidden=1' \
  && ok "老只读口仍是 nodes=4 的 P1-3b 口径" || bad "老只读口被 C2 改了：$OLD"

echo "== 4. 坏配置（加载期就起不来）与缺表降级 =="
python3 - "$ROOT" "$TMP" <<'PY'
import copy, json, os, sys
root, tmp = sys.argv[1:3]
base = json.load(open(os.path.join(root, "config/gather_spread.json"), encoding="utf-8"))
base["items_config"] = os.path.join(root, "config/items.json").replace("\\", "/")
def bad(name, mut, expect):
    d = copy.deepcopy(base); mut(d)
    json.dump(d, open(os.path.join(tmp, "bad-%s.json" % name), "w", encoding="utf-8"), ensure_ascii=False)
    open(os.path.join(tmp, "bad-%s.expect" % name), "w", encoding="utf-8").write(expect)
bad("schema", lambda d: d.update(schema="gather-spread/2"), "schema 必须是 gather-spread/1")
bad("version", lambda d: d.update(version=2), "version 必须是 1")
bad("top-reward", lambda d: d.update(reward=[]), "出现不认识的键 reward")
bad("node-probability", lambda d: d["nodes"][0].update(probability=50), "出现不认识的键 probability")
bad("no-realm", lambda d: d["nodes"][0].pop("realm"), "缺 realm")
bad("future-realm", lambda d: d["nodes"][0].update(realm="元婴"), "不在已开放境界里：元婴")
bad("realm-mismatch", lambda d: d["nodes"][0].update(realm="结丹"), "的境界区域不符")
bad("spirit-kind", lambda d: d["nodes"][0].update(kind="spirit_stone"), "不许配 spirit_stone")
bad("dup-id", lambda d: d["nodes"][1].update(id=d["nodes"][0]["id"]), "有重复节点编号")
bad("base-id", lambda d: d["nodes"][0].update(id="ore_kulan_1"), "与基准表重复")
bad("no-item", lambda d: d["nodes"][0].update(item_id="ore_not_there"), "不在 items.json 里")
bad("bound-item", lambda d: d["nodes"][0].update(item_id="ticket_protect"), "不是可交易的 material")
bad("over-stack", lambda d: d["nodes"][0].update(count=10000), "超过 herb_ling 的 stack_max")
bad("zero-count", lambda d: d["nodes"][0].update(count=0), "count 必须大于 0")
bad("unknown-region", lambda d: d["nodes"][0].update(region_id="不在地名表里"), "地点不在 world.json 里")
bad("uncovered", lambda d: d.update(nodes=[n for n in d["nodes"] if n["region_id"] != "龙巢门口"]), "铺开后地表 龙巢门口 缺 ore 或 herb 节点")
PY
for f in "$TMP"/bad-*.json; do
  n="${f##*/}"; n="${n%.json}"
  want="$(cat "$TMP/$n.expect")"
  out="$("$DOTNET" "$WORLD_DLL" --gather-spread-state --gather-spread-config "$f" 2>&1 | utf8)"; rc=$?
  if [ "$rc" != "0" ] && printf '%s' "$out" | grep -qF 'config_error=' && printf '%s' "$out" | grep -qF "$want"; then
    ok "坏配置 ${n#bad-} 被加载期挡住（rc=$rc）"
  else
    bad "坏配置 ${n#bad-} 没挡住（rc=$rc）：$(printf '%s' "$out" | head -2 | tr '\n' ' ')"
  fi
done
MISS_OUT="$("$DOTNET" "$WORLD_DLL" --gather-spread-state --gather-spread-config "$TMP/no-such-table.json" 2>&1 | utf8)"; rc=$?
[ "$rc" = "0" ] && printf '%s' "$MISS_OUT" | grep -qF 'gather_spread=missing' \
  && ok "缺表降级：世界服仍按 P1-3b 四个基准点启动（老卡临时目录没有这份表）" \
  || bad "缺表没有降级（rc=$rc）：${MISS_OUT:0:120}"
# 改配置后只读口跟着变，证明节点与数值只在配置里、代码没有第二套。
python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1:3]
d = json.load(open(os.path.join(root, "config/gather_spread.json"), encoding="utf-8"))
d["items_config"] = os.path.join(root, "config/items.json").replace("\\", "/")
d["nodes"].append(dict(d["nodes"][0], id="ore_chenglu_extra"))
json.dump(d, open(os.path.join(tmp, "spread-hot.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
HOT="$("$DOTNET" "$WORLD_DLL" --gather-spread-state --gather-spread-config "$TMP/spread-hot.json" 2>&1 | utf8)"
printf '%s' "$HOT" | grep -qF 'nodes=15' && printf '%s' "$HOT" | grep -qF 'total=19' \
  && ok "改配置热更：只读口从 14/18 变 15/19，代码里没有写死节点数" \
  || bad "改配置没被读到：${HOT:0:150}"

echo "== 5. 真服真库（MMO_C2_HAS_DB=1 才跑） =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  echo "  ..   跳过（没有 MMO_C2_HAS_DB=1 或 MMO_GAMEDB）"
else
  if ! "$DOTNET" "$GATEWAY_DLL" --migrate --sql db/character/account.sql --config config/gateway.json >"$TMP/m1.log" 2>&1 \
     || ! "$DOTNET" "$CHARACTER_DLL" --migrate --config config/character.json >"$TMP/m2.log" 2>&1; then
    bad "账号表 / 角色表建表失败"; tail -6 "$TMP/m1.log" "$TMP/m2.log" | detail
  else
    HAVE_DB=1
    printf '%s\n' "$TEST_PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$ACCOUNT" --seed-slots 8 --config config/gateway.json >"$TMP/seed.log" 2>&1
    ok "账号表、角色表就绪，测试账号已播种（每次唯一，不撞真库老数据）"
  fi
  if [ -n "$HAVE_DB" ]; then
    cp "$ROOT/config/dungeons.json" "$TMP/dungeons.json"; cp "$ROOT/config/exp.json" "$TMP/exp.json"
    ROOT="$ROOT" TMP="$TMP" WORLD_PORT="$WORLD_PORT" CHAR_PORT="$CHAR_PORT" GATEWAY_PORT="$GATEWAY_PORT" python3 - <<'PY'
import json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
def load(p): return json.load(open(root + '/config/' + p, encoding='utf-8'))
def dump(d, p): json.dump(d, open(tmp + '/' + p, 'w', encoding='utf-8'), ensure_ascii=False)
cp, gp, wp = [os.environ[k] for k in ('CHAR_PORT', 'GATEWAY_PORT', 'WORLD_PORT')]
w = load('world.json'); w['listen'] = 'http://127.0.0.1:' + wp; dump(w, 'world.json')
g = load('gather.json'); g['character_upstream'] = 'http://127.0.0.1:' + cp; dump(g, 'gather.json')
s = load('gather_spread.json'); s['items_config'] = (root + '/config/items.json').replace('\\', '/'); dump(s, 'gather_spread.json')
dump({'schema': 'character-config/1', 'listen': 'http://127.0.0.1:' + cp, 'db_pool_max': 6, 'bag_slots': 40, 'dungeon_table': tmp + '/dungeons.json', 'exp_table': tmp + '/exp.json'}, 'character.json')
dump({'schema': 'gateway-config/1', 'listen': 'http://127.0.0.1:' + gp, 'token_ttl_s': 7200, 'pbkdf2_iterations': 210000, 'battle_upstream': 'ws://127.0.0.1:60001/battle', 'character_upstream': 'http://127.0.0.1:' + cp, 'world_upstream': 'http://127.0.0.1:' + wp}, 'gateway.json')
PY
    "$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & PIDS="$PIDS $!"
    "$DOTNET" "$WORLD_DLL" --config "$TMP/world.json" --gather-config "$TMP/gather.json" \
      --gather-spread-config "$TMP/gather_spread.json" >"$TMP/world.log" 2>&1 & PIDS="$PIDS $!"
    "$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >"$TMP/gateway.log" 2>&1 & PIDS="$PIDS $!"
    if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
       && wait_ready "$TMP/world.log" '^ready name=World' "世界服" \
       && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关"; then
      ok "角色服 + 世界服 + 网关三个进程都起来（真库 gamedb）"
    else
      bad "真服没起来"; finish "C2（采集节点铺开）"
    fi
    if grep -qaF 'gather_config=ok nodes=4 kinds=ore,herb,spirit_stone forbidden=1' "$TMP/world.log" \
       && grep -qaF 'gather_spread=ok nodes=14 regions=8' "$TMP/world.log" \
       && grep -qaF 'total=18' "$TMP/world.log"; then
      ok "启动日志：老行仍 nodes=4，新行铺开 14 个、合计 18 个节点"
    else
      bad "启动日志的采集配置不对"; grep -aE '^(gather_|config_error)' "$TMP/world.log" | detail
    fi
    post() { curl -s -m 10 --noproxy '*' -w '\n%{http_code}' -X POST "$@"; }
    get()  { curl -s -m 10 --noproxy '*' "$@"; }
    TOKEN="$(get -X POST "http://127.0.0.1:${GATEWAY_PORT}/login" -H 'Content-Type: application/json' \
      -d "{\"account\":\"$ACCOUNT\",\"password\":\"$TEST_PASSWORD\"}" | json_field_stdin token)"
    if [ -z "$TOKEN" ]; then bad "登录拿不到令牌"; finish "C2（采集节点铺开）"; fi
    CR="$(post "http://127.0.0.1:${GATEWAY_PORT}/character?token=$TOKEN" -H 'Content-Type: application/json' \
      -d "{\"name\":\"$CNAME\",\"race\":\"mai\",\"class_id\":\"warrior\"}")"
    CID="$(printf '%s' "$(body_of "$CR")" | json_field_stdin character_id)"
    if [ -z "$CID" ]; then bad "建角色失败：$(body_of "$CR")"; finish "C2（采集节点铺开）"; fi
    ok "登录与建角成功（角色名带本次进程号，真库不撞老数据）"
    enter() { post "http://127.0.0.1:${GATEWAY_PORT}/world/enter?token=$TOKEN" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"region_id\":\"$1\"}"; }
    # 网关回包把中文转成 \uXXXX，比较放在 Python 内部做，输出只有 ASCII 的 ok / bad。
    line_is() { printf '%s' "$1" | python3 -c 'import json,sys; print("ok" if json.load(sys.stdin).get("line_id") == sys.argv[1] else "bad")' "$2"; }
    gather() { post "http://127.0.0.1:${GATEWAY_PORT}/world/gather?token=$TOKEN" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"node_id\":\"$1\"${2:-}}"; }
    bag() { get "http://127.0.0.1:${GATEWAY_PORT}/inventory?token=$TOKEN&character_id=$CID" | python3 -c 'import json,os,sys
try: print(sum(s["count"] for s in json.load(sys.stdin)["slots"] if s["item_id"] == os.environ["ITEM"]))
except Exception: print("-1")'; }
    fatigue() { "$DOTNET" "$CHARACTER_DLL" --fatigue-state "$CID" --config "$TMP/character.json" 2>/dev/null \
      | sed -n 's/^fatigue_state=ok .* fatigue=\([0-9-]*\) .*/\1/p'; }
    E="$(enter 承露门)"
    [ "$(line_is "$(body_of "$E")" '承露门#1')" = "ok" ] \
      && ok "进承露门分线（C2 在这块地表补了矿与药）" || bad "进线失败：$(body_of "$E")"
    # 先让疲劳不是 0，采完才能证明「疲劳不变」不是拿空值糊弄过去。
    F="$(post "http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter?token=$TOKEN" -H 'Content-Type: application/json' \
      -d "{\"character_id\":$CID,\"dungeon_id\":\"abyss\",\"party_size\":4}")"
    [ "$(printf '%s' "$(body_of "$F")" | json_field_stdin fatigue)" = "6000" ] \
      && ok "先进一次 4 人深渊，疲劳基线 6000" || bad "疲劳基线没建立：$(body_of "$F")"
    R="$(gather ore_chenglu_1)"; B="$(body_of "$R")"
    [ "$(expect_json "$B" ok=true item_id=ore_iron count=2 bag_slot=3 respawn_s=2)" = "ok" ] \
      && ok "采铺开的矿：回包矿石 2 个、第 3 格、刷新 2 秒，全按配置" \
      || bad "采矿回包不对：$B"
    [ "$(ITEM=ore_iron bag)" = "2" ] && ok "背包出现 ore_iron ×2（角色服 ApplyGrant 落库）" || bad "背包没进矿石：$(ITEM=ore_iron bag)"
    [ "$(fatigue)" = "6000" ] && ok "采矿后疲劳仍是 6000" || bad "采矿动了疲劳：$(fatigue)"
    R="$(gather herb_chenglu_1 ',"item_id":"stone_1","count":9999,"bag_slot":9,"realm":"元婴","txn_id":"forged","kind":"spirit_stone"')"
    B="$(body_of "$R")"
    [ "$(expect_json "$B" ok=true item_id=herb_ling count=3 bag_slot=4)" = "ok" ] \
      && [ "$(printf '%s' "$B" | json_field_stdin txn_id)" != "forged" ] \
      && ok "采铺开的药：塞产物 / 数量 / 格子 / realm / 事务号都无效，仍按配置发药材 3 个" \
      || bad "改包影响了产物：$B"
    [ "$(ITEM=herb_ling bag)" = "3" ] && ok "背包出现 herb_ling ×3" || bad "背包没进药材：$(ITEM=herb_ling bag)"
    [ "$(fatigue)" = "6000" ] && ok "采药后疲劳仍是 6000" || bad "采药动了疲劳：$(fatigue)"
    R="$(gather ore_kulan_1)"
    [ "$(printf '%s' "$(body_of "$R")" | json_field_stdin reason)" = "wrong_region" ] \
      && ok "跨地表采基准节点仍回 wrong_region（老判定没改）" || bad "跨区采集没被挡：$(body_of "$R")"
    E="$(enter 栖云城)"
    [ "$(line_is "$(body_of "$E")" '栖云城#1')" = "ok" ] \
      && ok "换到栖云城（主城也铺了矿与药）" || bad "换区失败：$(body_of "$E")"
    R="$(gather spirit_qiyun_1)"
    [ "$(printf '%s' "$(body_of "$R")" | json_field_stdin reason)" = "spirit_stone_forbidden" ] \
      && ok "灵石矿脉仍拒：spirit_stone_forbidden，个人采集采不到灵石" || bad "灵石矿脉没被拒：$(body_of "$R")"
    R="$(gather ore_qiyun_1)"
    [ "$(printf '%s' "$(body_of "$R")" | json_field_stdin ok)" = "True" ] \
      && ok "主城铺开的矿可采，产物仍进背包" || bad "主城铺开的矿采不了：$(body_of "$R")"
    E="$(enter 龙巢门口)"
    [ "$(line_is "$(body_of "$E")" '龙巢门口#1')" = "ok" ] \
      && ok "进龙巢门口（结丹境界区域）" || bad "进结丹区域失败：$(body_of "$E")"
    R="$(gather ore_longchao_1)"
    [ "$(printf '%s' "$(body_of "$R")" | json_field_stdin ok)" = "True" ] \
      && ok "结丹境界区域铺开的矿可采（节点按地表区域刷新，不改 P1-3c 的境界权限）" \
      || bad "结丹区域节点采不了：$(body_of "$R")"
    E="$(enter 焦原)"
    printf '%s' "$(body_of "$E")" | grep -qF 'region_locked' \
      && ok "焦原仍按 region.open=false 拒进线（C2 不改剧情开放，只留节点配置）" \
      || bad "主线未开的地表被放行了：$(body_of "$E")"
    [ "$(fatigue)" = "6000" ] && ok "真库读回：采集全程疲劳不变" || bad "疲劳被改了：$(fatigue)"
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
finish "C2（采集节点铺开）"
