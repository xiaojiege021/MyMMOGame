#!/usr/bin/env bash
# P3-1a2 突破（方案 8.1、开发架构 11 节 P3-1、13 节 P3-1a2、14 节第 61 行）。
#   bash tools/check/check-p3-1a2.sh
#   MMO_P3_1A2_HAS_DB=1 bash ...                加真库段（突破、失败扣历练与灵石、炼心、历练结算）
# 本卡口径：修为就是当前境界；历练来自已经能进入的副本；炼心是做完这一段
# 已经打开的主线和必要支线；成功后修为进下一段，失败后修为不变、历练与灵石照扣。
set -uo pipefail
# 服间 gRPC 走明文 HTTP/2（h2c）直连 127.0.0.1。机器上若设了 HTTP(S)_PROXY，
# .NET 的 HttpClient 会把本地请求也送进代理，报「unable to establish HTTP/2
# connection」，真库段的建角与战斗服结算会整段假失败。本脚本的 HTTP 全在
# 本机，gamedb 是 TCP 直连，都不需要代理；curl 段本来就用了 --noproxy '*'。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
AGG_CHARGRPC="$(src_agg src/Character/CharacterGrpcService.cs)"; export AGG_CHARGRPC
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CC="--config $ROOT/config/character.json"
CG="--config $ROOT/config/gateway.json"
HAS_DB="${MMO_P3_1A2_HAS_DB:-0}"
TMP="$(mktemp -d)"
PIDS=""
# Windows 上 dotnet 把 stdout 重定向到管道时走系统 ANSI 代码页（GBK），
# 而本脚本的中文断言是 UTF-8，第 3 段会整段假失败。按平台选编码：
# 不能「先试 UTF-8、失败再退 GBK」——GBK 的双字节汉字偶尔恰好是合法
# UTF-8 序列，会解成一组西里尔字母而不报错（实测「元婴」→「ԪӤ」）。
# macOS / Linux 本来就是 UTF-8，原样透传。只转编码，不改任何一条断言。
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
import json, os, re, sys
root, out = sys.argv[1], sys.argv[2]
o = open(out, "w", encoding="utf-8")

def R(k, c, evidence=""):
    o.write(("PASS " if c else "FAIL ") + k + ("" if c else "  " + str(evidence)) + "\n")

def rd(p):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server", "CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World", "SectRepository.cs": "Sect", "SocialGrpcService.cs": "Grpc", "WorldGrpcService.cs": "Grpc", "Program.cs": "Cli"}
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

order = ["炼体", "筑基", "结丹", "元婴", "化神", "返虚", "合体", "渡劫", "大乘", "金仙", "极境"]
realms = js(os.path.join(root, "config", "realms.json"))
adv = {x.get("from"): x for x in realms.get("advance", [])}
R("advance_version", realms.get("version", 0) >= 2)
R("advance_scope", list(adv) == order[:-1], list(adv))
R("advance_fields", all(
    isinstance(x.get("drill"), int) and x["drill"] >= 0
    and str(x.get("stone", "")).startswith("stone_")
    and isinstance(x.get("stone_cost"), int) and x["stone_cost"] > 0
    and isinstance(x.get("success_pct"), int) and 0 <= x["success_pct"] <= 100
    and isinstance(x.get("main_stage"), int) and x["main_stage"] >= 0
    and isinstance(x.get("side_quests"), list)
    for x in adv.values()))
R("main_stage_91_10_11_12",
  [adv[r]["main_stage"] for r in ("炼体", "筑基", "结丹", "元婴")] == [9, 10, 11, 12],
  [adv[r]["main_stage"] for r in ("炼体", "筑基", "结丹", "元婴")])
R("launch_side_empty", all(adv[r]["side_quests"] == [] for r in ("炼体", "筑基", "结丹")))

d = js(os.path.join(root, "config", "dungeons.json"))
by = {x.get("id"): x for x in d.get("dungeons", [])}
R("dungeon_drill", d.get("version", 0) >= 8 and all(
    isinstance(x.get("drill"), int) and x["drill"] >= 0 for x in by.values()),
  {k: v.get("drill") for k, v in by.items()})
R("old_dungeons_no_realm", all("realm" not in by.get(k, {}) for k in ("main", "abyss", "lair", "sect", "dragon")))

rt = rd(os.path.join(root, "src", "Character", "RealmTable.cs"))
R("requirement_model", "RealmAdvanceRequirement" in rt and "Requirement(" in rt)
R("stone_five_tiers", "突破灵石必须是灵石五档" in rt)
R("success_bound", "success_pct 必须在 0–100" in rt)
R("missing_requirement", "的突破要求" in rt)
R("reuse_switch", "CheckAdvance(" in rt and "realm_locked" in rt)

repo = rd(os.path.join(root, "src", "Character", "RealmAdvanceRepository.cs"))
R("lock_row", "for update" in repo)
R("heart_tables", "character_quest" in repo and "main_stage" in repo)
R("ledgers", "realm_breakthrough" in repo and "realm_drill" in repo)
R("conditional_deduct", "drill >= $2" in repo and "amount >= $3" in repo)
R("one_roll", "Random.Shared.Next(100) < requirement.SuccessPct" in repo)
R("failure_keeps_realm", "if (success)" in repo and "realm = $2" in repo)
R("realm_phase_gate", "realm_phase_not_enough" in repo and "BreakthroughMinIndex" in rt)
R("gate_dungeon", "CheckDungeon(dungeonRealm, realm)" in repo)

grpc = rd(os.environ["AGG_CHARGRPC"])
R("grpc_entries", "AdvanceRealm(" in grpc and "SettleDrill(" in grpc and "realmAdvances" in grpc)
proto = rd(os.path.join(root, "proto", "mmo", "character.proto"))
R("proto_rpcs", "rpc AdvanceRealm(" in proto and "rpc SettleDrill(" in proto)
R("proto_no_numbers", "int64 drill_cost = 9" in proto and "int32 success_pct = 17" in proto)
gw = rd(os.path.join(root, "src", "Gateway", "Program.cs"))
# P3-1a2 的边界是「网关不本地算境界规则」；客户端入口是后续卡的活，
# 不写「网关还没有 advance-realm 路由」这种会过期的话（D5 口径）。
R("gateway_no_local_rules", "SuccessPct" not in gw and "BreakthroughMinIndex" not in gw,
  "网关不本地算突破成功率 / 阶位门槛")
battle = rd(os.path.join(root, "src", "Battle", "Server.cs")) + rd(os.path.join(root, "src", "Battle", "Encounter.cs"))
R("drill_only_in_character", "realm_breakthrough" not in battle and "realm_drill" not in battle)

ddl = rd(os.path.join(root, "db", "character", "realm_advance.sql"))
main = rd(os.path.join(root, "db", "character", "character.sql"))
for name in ("character_quest", "realm_breakthrough", "realm_drill"):
    R("ddl_" + name, name in ddl and name in main)
R("ddl_realm_phase_promote", "realm_phase_promote" in main)
R("ddl_realm_phase", "realm_phase" in main and "character_realm_phase_range" in main)
R("ddl_drill_column", "ADD COLUMN IF NOT EXISTS drill bigint" in ddl and "ADD COLUMN IF NOT EXISTS drill bigint" in main)

# ---- v1.110 补正：历练与等级经验走同一笔结算，生产链路有写入者 ----
req = re.search(r"message SettleExpRequest \{(.*?)\n\}", proto, re.S)
resp = re.search(r"message SettleExpResponse \{(.*?)\n\}", proto, re.S)
R("link_proto_request", bool(req) and "string dungeon_id = 7" in req.group(1)
  and "uint32 qualified_kills = 8" in req.group(1))
R("link_proto_response", bool(resp) and "int64 drill_gain = 15" in resp.group(1)
  and "int64 drill = 16" in resp.group(1) and "string drill_reason = 17" in resp.group(1))
R("link_request_no_value", bool(req) and "int64 drill" not in req.group(1)
  and "drill_gain" not in req.group(1))
R("link_generated_same",
  rd(os.path.join(root, "src", "Shared", "Generated", "Character.cs"))
  == rd(os.path.join(root, "client", "MMOGame", "Assets", "Proto", "Generated", "Character.cs")))
R("link_keep_cli_entry", "rpc SettleDrill(" in proto)
exp_repo = rd(os.path.join(root, "src", "Character", "ExpSettlementRepository.cs"))
R("link_one_transaction", "exp_settlement" in exp_repo and "realm_drill" in exp_repo
  and "BeginTransactionAsync" in exp_repo)
R("link_value_from_config", "dungeon.Drill * qualifiedKills" in exp_repo)
R("link_gate_in_txn", "realms.CheckDungeon(dungeon.Realm, realm)" in exp_repo)
R("link_duplicate_rolls_back", '0, drillBefore, "duplicate"' in exp_repo)
exp_settle = rd(os.path.join(root, "src", "Battle", "ExpSettlement.cs"))
R("link_battle_sends", "DungeonId = dungeonId" in exp_settle
  and "QualifiedKills = (uint)Math.Max(0, qualifiedKills)" in exp_settle)
R("link_battle_kills", "_qualifiedKills++" in battle and "_dungeon?.Id" in battle)
R("link_no_second_writer", "SettleDrill" not in exp_settle and "SettleDrill" not in battle)
R("link_service_passes", "qualifiedKills, realms, grants" in grpc)
R("link_side_quests_all_empty", all(adv[r]["side_quests"] == [] for r in order[:-1]))
o.close()
PY
while IFS= read -r line; do
  case "$line" in
    "PASS "*) ok "${line#PASS }" ;;
    "FAIL "*) bad "${line#FAIL }" ;;
  esac
done < "$TMP/static.txt"

echo "== 3. 只读入口 --realm-table / --dungeon-table =="
"$DOTNET" "$CD" --realm-table $CC >"$TMP/rt.raw" 2>&1; rc=$?
RT="$(utf8 < "$TMP/rt.raw")"
if [ "$rc" = "0" ] \
   && printf '%s' "$RT" | grep -q '^realm_table_state=ok version=5 realms=11 launch_until=结丹' \
   && printf '%s' "$RT" | grep -q '^sub_phase=\*:2 name=后期 passive_pct_bps=160' \
   && printf '%s' "$RT" | grep -q '^advance_req=炼体->筑基 drill=100 stone=stone_1 stone_cost=10 success_pct=70 main_stage=9 side_quests=$' \
   && [ "$(printf '%s' "$RT" | grep -c '^advance_req=')" = "10" ]; then
  ok "十段突破要求都在配置里，数值不写死在代码（不连库）"
else
  bad "境界表只读口径不对（rc=${rc}）"
fi
printf '%s' "$RT" | grep -q '^advance=结丹->元婴:realm_locked' \
  && ok "版本开关仍是 P3-1a1 那一处判定：结丹→元婴关着" || bad "版本开关判定不对"

"$DOTNET" "$CD" --dungeon-table $CC >"$TMP/dt.raw" 2>&1
DT="$(utf8 < "$TMP/dt.raw")"
printf '%s' "$DT" | grep -q '^dungeon=abyss .*drill=50' \
  && printf '%s' "$DT" | grep -q '^dungeon=abyss_yuanying .*realm=元婴 drill=200' \
  && ok "副本表带历练值；元婴副本仍带境界门槛" || bad "副本表历练字段不对"

echo "== 4. 坏配置起不来 =="
python3 - "$ROOT" "$TMP" <<'PY'
import copy, json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
base = json.load(open(os.path.join(root, "config", "realms.json"), encoding="utf-8"))
dung = json.load(open(os.path.join(root, "config", "dungeons.json"), encoding="utf-8"))

def dump(name, obj):
    json.dump(obj, open(os.path.join(tmp, name), "w", encoding="utf-8"), ensure_ascii=False)

def item(d, realm):
    return next(x for x in d["advance"] if x["from"] == realm)

a = copy.deepcopy(base); a["advance"] = [x for x in a["advance"] if x["from"] != "元婴"]
dump("r-missing.json", a)
b = copy.deepcopy(base); b["advance"].append(dict(item(base, "炼体")))
dump("r-dup.json", b)
c = copy.deepcopy(base); item(c, "炼体")["stone"] = "gold"
dump("r-stone.json", c)
e = copy.deepcopy(base); item(e, "炼体")["success_pct"] = 101
dump("r-pct.json", e)
f = copy.deepcopy(base); item(f, "炼体")["drill"] = -1
dump("r-drill.json", f)
g = copy.deepcopy(base); item(g, "炼体")["side_quests"] = [""]
dump("r-side.json", g)
h = copy.deepcopy(base)
h["advance"].append({"from": "极境", "drill": 1, "stone": "stone_1",
                     "stone_cost": 1, "success_pct": 50, "main_stage": 0, "side_quests": []})
dump("r-max.json", h)
k = copy.deepcopy(dung); next(x for x in k["dungeons"] if x["id"] == "abyss")["drill"] = -1
dump("d-drill.json", k)
PY
for c in missing dup stone pct drill side max; do
  out="$("$DOTNET" "$CD" --realm-table-path "$TMP/r-$c.json" "$ROOT/config/dungeons.json" 2>&1)"
  assert_bad_config "$out" "坏突破要求 ${c}：起不来（config_error=）"
done
out="$("$DOTNET" "$CD" --dungeon-table-path "$TMP/d-drill.json" 2>&1)"
assert_bad_config "$out" "副本历练值是负数：起不来"

echo "== 5. 真库：突破、炼心、失败扣历练与灵石、历练结算 =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真库段（MMO_P3_1A2_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/account.sql" $CG >"$TMP/mig-account.log" 2>&1 || true
  "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/character.sql" $CC >"$TMP/mig-char.log" 2>&1 || true
  "$DOTNET" "$CD" --migrate --sql "$ROOT/db/character/currency.sql" $CC >"$TMP/mig-currency.log" 2>&1 || true
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 &
  PIDS="$PIDS $!"
  wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$GD" $CG >"$TMP/gw.log" 2>&1 &
  PIDS="$PIDS $!"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
      -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p31a2_$$"; PW="P31a2-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 $CG >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" \
    | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
  [ -n "$TOK" ] && ok "登录拿到令牌（账号/口令只在内存）" || bad "登录没拿到令牌"
  ID=""
  for _ in $(seq 1 10); do
    out="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
      -H 'Content-Type: application/json' \
      -d "{\"name\":\"p31a2_$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}" 2>&1)"
    ID="$(printf '%s' "$out" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")' 2>/dev/null)"
    [ -n "$ID" ] && break; sleep 0.5
  done
  [ -n "$ID" ] && ok "造一个角色（${ID}）" || bad "角色没建成"

  # 临时配置：成功率 100 / 0 / 带一条必要支线。只用配置指路，不改仓库里的那份。
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
base = json.load(open(os.path.join(root, "config", "realms.json"), encoding="utf-8"))
ch = json.load(open(os.path.join(root, "config", "character.json"), encoding="utf-8"))

def save(name, obj):
    json.dump(obj, open(os.path.join(tmp, name), "w", encoding="utf-8"), ensure_ascii=False)

for name, pct in (("r-100.json", 100), ("r-0.json", 0), ("r-side.json", 100)):
    r = json.loads(json.dumps(base))
    for x in r["advance"]:
        if x["from"] == "炼体":
            x["success_pct"] = pct
            if name == "r-side.json":
                x["side_quests"] = ["side_lianxin_p31a2"]
    save(name, r)
    c = dict(ch); c["realm_table"] = os.path.join(tmp, name)
    save("c-" + name, c)
PY
  C100="--config $TMP/c-r-100.json"; C0="--config $TMP/c-r-0.json"; CSIDE="--config $TMP/c-r-side.json"
  # 真库是长期存在的，事务号不能写死：第二遍跑会撞上上一遍留下的号，
  # 所有突破都回 duplicate=true，整段假红。用本次进程号 + 时间戳做前缀，
  # 同一条断言里重复提交同一个短号仍然会撞（那正是要验的幂等）。
  TX="p31a2-$$-$(date +%s)"
  rawd() { "$DOTNET" "$CD" --raw-drill-write "$1" "$2" $CC 2>&1 | utf8 | grep -m1 '^raw_drill='; }
  rawm() { "$DOTNET" "$CD" --raw-main-stage-write "$1" "$2" $CC >/dev/null 2>&1; }
  rawr() { "$DOTNET" "$CD" --raw-realm-write "$1" "$2" $CC >/dev/null 2>&1; }
  rawp() { "$DOTNET" "$CD" --raw-realm-phase-write "$1" "$2" $CC >/dev/null 2>&1; }
  qdone() { "$DOTNET" "$CD" --complete-quest "$1" "$2" $CC >/dev/null 2>&1; }
  bstate() { "$DOTNET" "$CD" --realm-break-state "$1" $CC 2>&1 | utf8 | grep -m1 '^realm_break_state='; }
  # B1 起突破要多扣一枚境界丹；这一段验的是 P3-1a2 的门槛与流水，所以每条突破前先把丹补满。
  adv() { local id="$1" txn="$2"; shift 2; rawi "$id" 9 pill_zhuji 5; rawi "$id" 10 pill_pojing 5; "$DOTNET" "$CD" --advance-realm "$id" "$TX-$txn" "$@" 2>&1 | utf8 | grep -m1 '^advance_realm='; }
  drill() { "$DOTNET" "$CD" --settle-drill "$1" "$2" "$TX-$3" $CC 2>&1 | utf8 | grep -m1 '^settle_drill='; }
  stone() {
    curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/currency/add?token=$TOK" \
      -H 'Content-Type: application/json' -d "{\"character_id\":$1,\"kind\":\"stone_1\",\"delta\":$2}" >/dev/null
  }
  setstone() { "$DOTNET" "$CD" --raw-currency-write "$1" stone_1 "$2" $CC >/dev/null 2>&1; }
  # B1 起突破要多扣一枚境界丹（筑基丹 / 破镜丹）：这一段验的是 P3-1a2 的门槛与流水，
  # 所以先在背包里备好丹，让每一条突破都走得到自己要验的那道门。
  # 一格只放得下一种物品：筑基丹与破镜丹必须占两格，否则后写的会把先写的顶掉。
  rawi() { "$DOTNET" "$CD" --raw-item-write "$1" "$2" "$3" "$4" $CC >/dev/null 2>&1; }
  stonebalance() {
    curl -s --noproxy '*' "http://127.0.0.1:61000/currency?token=$TOK&character_id=$1" \
      | python3 -c 'import json,sys
try:
    print([x.get("amount") for x in json.load(sys.stdin).get("balances",[]) if x.get("kind")=="stone_1"][0])
except Exception:
    print("")'
  }

  # B1：每一条突破都先备好境界丹（炼体→筑基扣筑基丹，后面各段扣破镜丹）。
  rawi "$ID" 9 pill_zhuji 5
  rawi "$ID" 10 pill_pojing 5

  rawd "$ID" -1 | grep -q 'raw_drill=rejected .*sqlstate=23514' \
    && ok "历练直写负数被库的 CHECK 拒（23514）" || bad "历练负数没被库挡住"

  # 炼心用例要在「段内阶已够」的前提下测：R1 起炼体要满第 9 阶（index 8），
  # 否则 phase 门槛会先于 heart 触发，测不到炼心。
  rawp "$ID" 8
  R="$(adv "$ID" t-heart-1 $CC)"
  printf '%s' "$R" | grep -q 'executed=false duplicate=false success=false from=炼体 next=筑基 realm=炼体 reason=heart_not_done' \
    && ok "主线没走到第 9 场：heart_not_done" || bad "炼心没挡住：$R"

  rawm "$ID" 9; rawp "$ID" 0
  R="$(adv "$ID" t-phase-1 $CC)"
  printf '%s' "$R" | grep -q 'reason=realm_phase_not_enough' \
    && ok "炼体未到九阶不能突破筑基" || bad "段内阶没挡住：$R"
  rawp "$ID" 8

  rawm "$ID" 9
  rawp "$ID" 8
  rawd "$ID" 0 >/dev/null
  R="$(adv "$ID" t-drill-1 $CC)"
  printf '%s' "$R" | grep -q 'reason=drill_not_enough' \
    && ok "历练不够：drill_not_enough，修为不动" || bad "历练没挡住：$R"

  rawd "$ID" 100 >/dev/null
  rawp "$ID" 8
  R="$(adv "$ID" t-stone-1 $CC)"
  printf '%s' "$R" | grep -q 'reason=stone_not_enough' \
    && ok "灵石不够：stone_not_enough，修为不动" || bad "灵石没挡住：$R"

  stone "$ID" 10
  rawp "$ID" 8
  R="$(adv "$ID" t-ok-1 $C100)"
  printf '%s' "$R" | grep -q 'executed=true duplicate=false success=true .*realm=筑基 .*drill=100 drill_required=100 drill_cost=100 drill_after=0 .*stone_left=0' \
    && ok "成功：修为进筑基，历练 100→0、灵石 10→0" || bad "成功路径不对：$R"
  rawd "$ID" 100 >/dev/null; setstone "$ID" 10
  R="$(adv "$ID" t-ok-1 $C100)"
  printf '%s' "$R" | grep -q 'executed=false duplicate=true' \
    && ok "同一事务号再提交不掷第二次" || bad "重复事务号没挡住：$R"
  [ "$(stonebalance "$ID")" = "10" ] && ok "重复提交没有多扣灵石" || bad "重复提交扣了灵石"

  rawr "$ID" 炼体; rawd "$ID" 100 >/dev/null; setstone "$ID" 10; rawm "$ID" 9; rawp "$ID" 8
  R="$(adv "$ID" t-fail-1 $C0)"
  printf '%s' "$R" | grep -q 'executed=true duplicate=false success=false from=炼体 next=筑基 realm=炼体 .*drill_after=0 .*stone_left=0' \
    && ok "失败：修为停在炼体，历练与灵石照扣" || bad "失败路径不对：$R"

  rawr "$ID" 炼体; rawd "$ID" 100 >/dev/null; setstone "$ID" 10; rawm "$ID" 9; rawp "$ID" 8
  R="$(adv "$ID" t-side-1 $CSIDE)"
  printf '%s' "$R" | grep -q 'reason=heart_not_done' \
    && ok "必要支线没做完：历练与灵石都够也不能突破" || bad "必要支线没卡住：$R"
  # N10 起 --realm-break-state 多了 realm_phase 字段，R1 后炼体是 8；期望串跟上实际输出。
  [ "$(stonebalance "$ID")" = "10" ] && [ "$(bstate "$ID")" = "realm_break_state=ok id=$ID realm=炼体 realm_phase=8 drill=100 main_stage=9 quests=" ] \
    && ok "被炼心挡下的突破一分都没扣" || bad "被挡下仍扣了东西：$(bstate "$ID")"
  qdone "$ID" side_lianxin_p31a2
  rawp "$ID" 8
  R="$(adv "$ID" t-side-2 $CSIDE)"
  printf '%s' "$R" | grep -q 'success=true .*realm=筑基' \
    && ok "必要支线做完后可以突破" || bad "补完支线仍不能突破：$R"

  rawr "$ID" 结丹; rawd "$ID" 800 >/dev/null; rawm "$ID" 11; setstone "$ID" 80; rawp "$ID" 2
  BAL="$(stonebalance "$ID")"
  R="$(adv "$ID" t-lock-1 $CC)"
  printf '%s' "$R" | grep -q 'reason=realm_locked' && [ "$(stonebalance "$ID")" = "$BAL" ] \
    && ok "元婴开关关着：结丹不能突破，灵石与历练不动" || bad "版本开关没挡住：$R"

  rawr "$ID" 炼体; rawd "$ID" 0 >/dev/null
  R="$(drill "$ID" abyss t-drill-a)"
  printf '%s' "$R" | grep -q "settle_drill=done id=$ID dungeon=abyss gain=50 drill=50 duplicate=false reason=$" \
    && ok "历练结算：深渊加 50" || bad "历练结算不对：$R"
  R="$(drill "$ID" abyss t-drill-a)"
  printf '%s' "$R" | grep -q "gain=0 drill=50 duplicate=true" \
    && ok "同一场重复结算不加第二次" || bad "历练重复结算：$R"
  R="$(drill "$ID" abyss_yuanying t-trill-b)"
  printf '%s' "$R" | grep -q 'reason=realm_locked' \
    && printf '%s' "$R" | grep -q 'gain=0' \
    && ok "元婴副本关着时结算不给历练" || bad "关着的副本仍给了历练：$R"
  bstate "$ID" | grep -q 'drill=50' \
    && ok "历练值落在角色身上（事实值 50）" || bad "历练值没落库"

  # ---- v1.110 补正：生产链路端到端 ----
  # 原来只有 --settle-drill 这个 CLI 入口在写 character.drill，战斗服从不调用。
  # 补正后战斗服通关时把副本编号与击杀数随 SettleExp 一起交给角色服，
  # 角色服在同一事务里写经验与历练。这一段用真服真库把这条链跑通。
  echo "== 6. 生产链路：战斗服通关 → 角色服同一笔写历练 =="
  BD="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
  BP="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"
  "$DOTNET" build "$ROOT/tools/probe/BattleProbe/BattleProbe.csproj" -c Release --nologo -v q \
    >"$TMP/build-probe.log" 2>&1 \
    && ok "通关探针编译通过" || bad "通关探针编译失败"
  python3 - "$ROOT" "$TMP" <<'PY'
import json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
b = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
b["listen"] = "127.0.0.1:60011"
b["exp_upstream"] = "http://127.0.0.1:62000"
b["character_upstream"] = "http://127.0.0.1:62000"
b["settlement_dir"] = os.path.join(tmp, "settlements")
b["character_dir"] = os.path.join(tmp, "characters")
for key, name in (("skill_table", "skills.json"), ("class_table", "classes.json"),
                  ("dungeon_table", "dungeons.json")):
    b[key] = os.path.join(root, "config", name)
json.dump(b, open(os.path.join(tmp, "battle-link.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)
PY
  "$DOTNET" "$BD" --config "$TMP/battle-link.json" >"$TMP/battle-link.log" 2>&1 &
  PIDS="$PIDS $!"
  wait_ready "$TMP/battle-link.log" '^listen=' "战斗服" || true
  rawd "$ID" 0 >/dev/null
  run_clear() {
    "$DOTNET" "$BP" --case clear --url "ws://127.0.0.1:60011/battle?$1" \
      --boss 9001 --ranged 1002 2>&1 | grep -m1 '^verdict=' || true
  }
  wait_drill() {
    for _ in $(seq 1 40); do bstate "$ID" | grep -q "drill=$1 " && return 0; sleep 0.2; done
    return 1
  }
  V="$(run_clear "dungeon=abyss&char=$ID&party=link-a")"
  printf '%s' "$V" | grep -q '^verdict=ok' \
    && ok "带副本通关：探针 pass" || bad "带副本通关失败：$V"
  wait_drill 50 \
    && ok "战斗服通关后角色服写下历练 50（生产链路通了）" \
    || bad "历练没从战斗服到角色服：$(bstate "$ID")"
  # v1.121 起结算日志末尾多一个 source= 字段（世界 Boss 那张卡加的），不再把
  # drill_reason 钉在行尾；仍然钉住「同一笔结算回包里带回历练 50」这条不变式。
  grep -qE 'exp_settle=ok .* dungeon=abyss kills=1 drill=50 drill_after=50 drill_reason=' \
    "$TMP/battle-link.log" \
    && ok "同一笔结算回包里带回历练 50" \
    || bad "战斗服结算日志不对：$(grep -m1 '^exp_settle=' "$TMP/battle-link.log")"
  V="$(run_clear "dungeon=abyss&char=$ID&party=link-b")"
  wait_drill 100 \
    && ok "第二场累计到 100（按击杀累计，不是一次通关固定发一次）" \
    || bad "第二场历练没累计：$(bstate "$ID")"
  V="$(run_clear "char=$ID&party=link-solo")"
  sleep 1
  bstate "$ID" | grep -q 'drill=100 ' \
    && ok "不带副本的练习场不计历练" || bad "练习场也给了历练"
  V="$(run_clear "dungeon=abyss_yuanying&char=$ID&party=link-yy")"
  sleep 1
  bstate "$ID" | grep -q 'drill=100 ' \
    && ok "元婴副本开关关着：结算不给历练" || bad "关着的副本仍给了历练"
  grep -q 'drill_reason=realm_locked' "$TMP/battle-link.log" \
    && ok "境界门槛在同一笔事务里判，回 realm_locked" \
    || bad "没回 realm_locked"
fi

finish "P3-1a2（角色服 + 网关）"
