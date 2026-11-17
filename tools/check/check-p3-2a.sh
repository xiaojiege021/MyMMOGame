#!/usr/bin/env bash
# P3-2a 灵石五档和城池矿脉（方案 9.5 / 12 章、开发架构 11 节 P3-2、13 节 P3-2a、14 节第 62 行）。
#   bash tools/check/check-p3-2a.sh
#   MMO_P3_2A_HAS_DB=1 MMO_GAMEDB='Host=...' bash ...   加真服真库端到端
# 三条完成标准：灵石矿脉个人采集被拒 / 任务发的灵石可进拍卖 /
#               10000 下品换 1 中品且不经过拍卖。
set -uo pipefail
# 本机设了代理时，.NET 会把 127.0.0.1 的明文 HTTP/2 请求也送进代理，
# 报 unable to establish HTTP/2 connection；curl 段一律 --noproxy '*'。
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"; export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"; [ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CD="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GD="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
GL="$ROOT/src/Global/bin/Release/net10.0/Global.dll"
WD="$ROOT/src/World/bin/Release/net10.0/World.dll"
CC="--config $ROOT/config/character.json"
CG="--config $ROOT/config/gateway.json"
CGL="--config $ROOT/config/global.json"
CW="--config $ROOT/config/world.json"
HAS_DB="${MMO_P3_2A_HAS_DB:-0}"
TMP="$(mktemp -d)"
PIDS=""
# Windows 上 dotnet 把 stdout 重定向到管道时走 GBK；中文断言按平台转码。
# 不能「先试 UTF-8 再退 GBK」：GBK 双字节偶尔恰好是合法 UTF-8 序列。
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

echo "== 1. 编译与静态 =="
for proj in src/Shared/Shared.csproj src/Character/Character.csproj src/Global/Global.csproj \
            src/Gateway/Gateway.csproj src/World/World.csproj src/Battle/Battle.csproj \
            src/Social/Social.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$n.log" 2>&1; then
    ok "编译 ${n}：0 错误 0 警告"
  else
    bad "编译失败：$proj"; grep -E 'error ' "$TMP/build-$n.log" | head -4 | detail
  fi
done
python3 - "$ROOT" >"$TMP/static.out" 2>&1 <<'PY'
import json, os, sys, hashlib
root = sys.argv[1]
def cfg(p): return json.load(open(os.path.join(root, p), encoding="utf-8"))
def src(p): return open(os.path.join(root, p), encoding="utf-8").read()
checks = []
def ck(name, cond): checks.append((name, bool(cond)))
se, cu, ci, ga, au = cfg("config/stone_exchange.json"), cfg("config/currency.json"), cfg("config/city.json"), cfg("config/gather.json"), cfg("config/auction.json")
ids = [x["id"] for x in cu["currencies"]]
ck("币种表仍是七个 id（不新增币种）", set(ids) == {"gold", "stone_1", "stone_2", "stone_3", "stone_4", "stone_5", "yuanbao"})
ck("兑换表五档顺序与配置一致", se["tiers"] == ["stone_1", "stone_2", "stone_3", "stone_4", "stone_5"])
ck("相邻比例 10000:1 写在配置里", se["ratio"] == 10000 and se["gain"] == 1)
ck("仙晶是最高档且不是元宝", se["highest_kind"] == "stone_5" and se["highest_name"] == "仙晶"
   and se["ingot_kind"] == "yuanbao" and next(x["name"] for x in cu["currencies"] if x["id"] == "stone_5") == "仙晶")
def code_only(p): return "\n".join(l for l in src(p).splitlines() if not l.strip().startswith("//"))
ck("产品代码里没有 10000 字面量（注释里提到不算）", all("10000" not in code_only(p) for p in
   ["src/Character/StoneExchangeTable.cs", "src/Character/StoneExchangeRepository.cs",
    "src/Character/StoneAuctionRepository.cs", "src/Global/StoneTierTable.cs"]))
veins = [x["id"] for x in ga["nodes"] if x["kind"] == "spirit_stone"]
ck("城池矿脉归属指向 gather 里的 spirit_stone 节点", len(veins) == 1 and ci["cities"][0]["vein_id"] == veins[0])
ck("城池登记里没有个人采集点/商品字段", all(not any(k in c for k in
   ("item_id", "count", "bag_slot", "drop", "price", "slot")) for c in ci["cities"]))
ck("城池抽成比例标待定、不写死", "待定" in ci["note"] and all(0 <= c["tax_pct"] <= 100 for c in ci["cities"]))
ck("gather.json 仍是四节点、灵石矿脉一个（不撞 p1-3b）", len(ga["nodes"]) == 4 and len(veins) == 1)
ck("auction.json 税率与版本没动（不撞 p1-5a）", 5 <= au["tax_rate_pct"] <= 8 and au["gold_kind"] == "gold" and au["version"] == 1)
mig = src("db/character/character.sql")
ck("主迁移自包含：兑换流水/城池账/灵石挂单都在", all(t in mig for t in
   ('"角色".stone_exchange_txn', '"社交".city_tax_ledger', '"交易".stone_listing', '"交易".stone_settlement')))
ck("城池税收与拍卖销毁税是两张表", '"社交".city_tax_ledger' in src("db/social/city.sql")
   and '"交易".auction_settlement' in src("db/trade/auction.sql") and "tax_gold" in src("db/trade/auction.sql"))
cp, tp = src("proto/mmo/character.proto"), src("proto/mmo/trade.proto")
ck("character.proto 有兑换与灵石成交两个 RPC", "rpc ExchangeStone(" in cp and "rpc SettleStoneAuction(" in cp)
ck("trade.proto 有灵石挂单与买入两个 RPC", "rpc ListStoneAuction(" in tp and "rpc BuyStoneAuction(" in tp)
req = cp.split("message ExchangeStoneRequest {")[1].split("}")[0]
ck("兑换请求带不进数值（只有角色/档位/事务号）", "ratio" not in req and "cost" not in req and "gain" not in req)
gateway = src("src/Gateway/CharacterApi.cs")
body = gateway.split("record ExchangeStoneBody(")[1].split(")")[0]
ck("/currency/exchange 请求体没有数值字段", "FromKind" in body and "Ratio" not in body and "Cost" not in body)
def sha(p): return hashlib.sha256(open(os.path.join(root, p), "rb").read()).hexdigest()
ck("两端 Character.cs 生成物一致", sha("src/Shared/Generated/Character.cs") == sha("client/MMOGame/Assets/Proto/Generated/Character.cs"))
ck("两端 Trade.cs 生成物一致", sha("src/Shared/Generated/Trade.cs") == sha("client/MMOGame/Assets/Proto/Generated/Trade.cs"))
for name, cond in checks: print(("OK " if cond else "BAD ") + name)
PY
while IFS= read -r line; do
  case "$line" in "OK "*) ok "${line#OK }" ;; "BAD "*) bad "${line#BAD }" ;; *) bad "静态检查输出异常：$line" ;; esac
done <"$TMP/static.out"

echo "== 2. 只读入口与坏配置（与启动共用同一 Load）=="
out="$("$DOTNET" "$CD" --stone-exchange-table $CC 2>&1 | utf8)"
printf '%s' "$out" | grep -q 'stone_exchange_state=ok .*ratio=10000 .*highest=stone_5/仙晶 .*ingot=yuanbao' \
  && ok "只读入口读到五档、10000:1 与仙晶/元宝隔离" || bad "兑换表只读入口不对：$out"
out="$("$DOTNET" "$CD" --city-table $CC 2>&1 | utf8)"
printf '%s' "$out" | grep -q 'city_table_state=ok' \
  && printf '%s' "$out" | grep -q 'city=qiyun .*vein=spirit_qiyun_1 tax_pct=0' \
  && ok "城池表只读入口读到归属与待定税率" || bad "城池表只读入口不对：$out"
python3 - "$ROOT" "$TMP" <<'PY'
import copy, json, os, sys
root, tmp = sys.argv[1], sys.argv[2]
se = json.load(open(os.path.join(root, "config/stone_exchange.json"), encoding="utf-8"))
cu = json.load(open(os.path.join(root, "config/currency.json"), encoding="utf-8"))
ci = json.load(open(os.path.join(root, "config/city.json"), encoding="utf-8"))
def save(name, obj): json.dump(obj, open(os.path.join(tmp, name), "w", encoding="utf-8"), ensure_ascii=False)
def variant(fn):
    x = copy.deepcopy(se); fn(x); return x
cases = {
 "s-miss.json": {k: v for k, v in se.items() if k != "tiers"},
 "s-dup.json": variant(lambda x: x["tiers"].append("stone_1")),
 "s-ratio.json": variant(lambda x: x.update(ratio=1)),
 "s-gain.json": variant(lambda x: x.update(gain=0)),
 "s-ingot.json": variant(lambda x: x["tiers"].append("yuanbao")),
 "s-top.json": variant(lambda x: x.update(highest_kind="stone_4")),
 "s-name.json": variant(lambda x: x.update(highest_name="仙品灵石")),
 "s-unknown.json": variant(lambda x: x["tiers"].__setitem__(0, "stone_x")),
}
for n, v in cases.items(): save(n, v)
badcu = copy.deepcopy(cu)
for c in badcu["currencies"]:
    if c["id"] == "stone_5": c["name"] = "元宝"
save("currency-bad.json", badcu)
def city(fn):
    x = copy.deepcopy(ci); fn(x); return x
ccases = {
 "c-vein.json": city(lambda x: x["cities"][0].update(vein_id="not_a_node")),
 "c-item.json": city(lambda x: x["cities"][0].update(item_id="stone_1")),
 "c-tax.json": city(lambda x: x["cities"][0].update(tax_pct=101)),
 "c-miss.json": {k: v for k, v in ci.items() if k != "gather_table"},
}
for n, v in ccases.items(): save(n, v)
PY
badcfg() {
  local desc="$1"; shift
  "$@" >"$TMP/bad.out" 2>&1; local rc=$?
  local out; out="$(utf8 <"$TMP/bad.out")"
  [ "$rc" -ne 0 ] && printf '%s' "$out" | grep -q 'config_error=' \
    && ok "${desc}：config_error= 且非 0 退出" || bad "$desc 没挡住（rc=${rc}）：$out"
}
for f in s-miss s-dup s-ratio s-gain s-ingot s-top s-unknown; do
  badcfg "坏兑换表 $f" "$DOTNET" "$CD" --stone-exchange-table-path "$TMP/$f.json" "$ROOT/config/currency.json" $CC
done
badcfg "最高档中文名与币种表不符" "$DOTNET" "$CD" --stone-exchange-table-path "$ROOT/config/stone_exchange.json" "$TMP/currency-bad.json" $CC
for f in c-vein c-item c-tax c-miss; do
  badcfg "坏城池表 $f" "$DOTNET" "$CD" --city-table-path "$TMP/$f.json" $CC
done

echo "== 3. 真服真库：任务发放 → 兑换 / 拍卖 / 采集 =="
if [ "$HAS_DB" != "1" ] || [ -z "${MMO_GAMEDB:-}" ]; then
  printf '  ..   跳过真服真库段（MMO_P3_2A_HAS_DB 未开或缺 MMO_GAMEDB）\n'
else
  for f in account character currency inventory; do
    "$DOTNET" "$GD" --migrate --sql "$ROOT/db/character/$f.sql" $CG >"$TMP/mig-$f.log" 2>&1 || true
  done
  "$DOTNET" "$GD" --migrate --sql "$ROOT/db/trade/auction.sql" $CG >"$TMP/mig-auction.log" 2>&1 || true
  "$DOTNET" "$CD" $CC >"$TMP/char.log" 2>&1 & PIDS="$PIDS $!"; wait_ready "$TMP/char.log" '^ready' "角色服" || true
  "$DOTNET" "$GL" $CGL >"$TMP/global.log" 2>&1 & PIDS="$PIDS $!"; wait_ready "$TMP/global.log" '^ready' "全局服" || true
  "$DOTNET" "$WD" $CW --gather-config "$ROOT/config/gather.json" >"$TMP/world.log" 2>&1 & PIDS="$PIDS $!"; wait_ready "$TMP/world.log" '^ready' "世界服" || true
  "$DOTNET" "$GD" $CG >"$TMP/gw.log" 2>&1 & PIDS="$PIDS $!"
  for _ in $(seq 1 120); do
    code=$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:61000/login \
      -H 'Content-Type: application/json' -d '{"account":"x","password":"y"}' 2>/dev/null)
    [ -n "$code" ] && [ "$code" != "000" ] && break; sleep 0.2
  done
  ACCT="p32a_$$"; PW="P32a-$$-$(date +%s)"
  printf '%s\n' "$PW" | "$DOTNET" "$GD" --seed-account "$ACCT" --seed-slots 8 $CG >/dev/null 2>&1
  TOK="$(curl -s --noproxy '*' -X POST http://127.0.0.1:61000/login -H 'Content-Type: application/json' \
    -d "{\"account\":\"$ACCT\",\"password\":\"$PW\"}" | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null)"
  [ -n "$TOK" ] && ok "登录拿到令牌（账号/口令只在内存）" || bad "登录没拿到令牌"
  mkchar() {
    for _ in $(seq 1 10); do
      local o; o="$(curl -s --noproxy '*' -X POST "http://127.0.0.1:61000/character?token=$TOK" \
        -H 'Content-Type: application/json' -d "{\"name\":\"p32a_$RANDOM$RANDOM\",\"race\":\"mai\",\"class_id\":\"warrior\"}")"
      local id; id="$(printf '%s' "$o" | jget character_id)"
      [ -n "$id" ] && { printf '%s' "$id"; return; }; sleep 0.5
    done
  }
  A="$(mkchar)"; B="$(mkchar)"
  [ -n "$A" ] && [ -n "$B" ] && [ "$A" != "$B" ] && ok "造两个角色（$A / ${B}）" || bad "角色没建成：A=$A B=$B"
  TX="p32a-$$-$(date +%s)"
  post() { curl -s --noproxy '*' -X POST "http://127.0.0.1:61000$1?token=$TOK" -H 'Content-Type: application/json' -d "$2"; }
  grant() { post /grant "{\"character_id\":$1,\"txn_id\":\"$TX-$2\",\"entries\":[{\"currency\":{\"kind\":\"$3\",\"delta\":$4}}]}" >/dev/null; }
  bal() { curl -s --noproxy '*' "http://127.0.0.1:61000/currency?token=$TOK&character_id=$1" \
    | python3 -c 'import json,sys; o=json.load(sys.stdin); print(next((x["amount"] for x in o.get("balances",[]) if x["kind"]==sys.argv[1]),0))' "$2"; }
  exch() { "$DOTNET" "$CD" --exchange-stone "$1" "$2" "$3" $CC 2>&1 | utf8 | grep -m1 '^stone_exchange='; }
  state() { "$DOTNET" "$CD" --stone-exchange-state "$1" $CC 2>&1 | utf8 | grep -m1 '^stone_exchange_state='; }
  listings() { "$DOTNET" "$CD" --stone-listing-state $CC 2>&1 | utf8 | grep -m1 '^stone_listing='; }

  LIST_BEFORE="$(listings)"
  grant "$A" task-1 stone_1 10000
  [ "$(bal "$A" stone_1)" = "10000" ] && ok "任务发放的 10000 下品灵石到账（ApplyGrant 同一笔事务）" || bad "任务发放没到账"
  R="$(exch "$A" stone_1 "$TX-ex-1")"
  printf '%s' "$R" | grep -q 'stone_exchange=ok .*from=stone_1 to=stone_2 from_cost=10000 to_gain=1 from_amount=0 to_amount=1 ratio=10000' \
    && ok "10000 下品 → 1 中品，比例与产出只从配置读" || bad "兑换结果不对：$R"
  [ "$(bal "$A" stone_1)" = "0" ] && [ "$(bal "$A" stone_2)" = "1" ] \
    && ok "兑换后库里的事实值：下品 0 / 中品 1" || bad "兑换没落库"
  R="$(exch "$A" stone_1 "$TX-ex-1")"
  printf '%s' "$R" | grep -q 'stone_exchange=duplicate' && ok "同一事务号重放 duplicate，不多扣不多发" || bad "重放没挡住：$R"
  printf '%s' "$(state "$A")" | grep -q 'exchange_txns=1' && ok "兑换流水只有一笔" || bad "兑换流水笔数不对：$(state "$A")"
  [ "$(listings)" = "$LIST_BEFORE" ] && ok "兑换不经过拍卖：前后挂单数不变" || bad "兑换动了挂单表"
  BEFORE="$(state "$A")"
  for spec in "stone_5:top_tier" "yuanbao:unknown" "stone_3:not_enough_stone"; do
    kind="${spec%%:*}"; why="${spec##*:}"
    R="$(exch "$A" "$kind" "$TX-rej-$kind")"
    if [ "$why" = "unknown" ]; then
      printf '%s' "$R" | grep -qE 'stone_exchange=(unknown_tier|unknown_currency)' \
        && ok "把元宝/$kind 当灵石档被拒" || bad "元宝没挡住：$R"
    else
      printf '%s' "$R" | grep -q "stone_exchange=$why" && ok "$kind 被拒：$why" || bad "$kind 没挡住：$R"
    fi
  done
  [ "$(state "$A")" = "$BEFORE" ] && ok "四种拒绝一分都没扣" || bad "拒绝路径动了余额"
  grant "$A" task-2 stone_1 10000
  R="$(post /currency/exchange "{\"character_id\":$A,\"from_kind\":\"stone_1\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] \
    && [ "$(printf '%s' "$R" | jget ratio)" = "10000" ] \
    && [ "$(printf '%s' "$R" | jget highest_name)" = "仙晶" ] \
    && ok "网关 /currency/exchange 通：回包只带事实值" || bad "网关兑换不对：$R"

  grant "$A" task-3 stone_2 3
  grant "$B" buyer-gold gold 1000
  S0="$(bal "$A" stone_2)"
  R="$(post /auction/stone/list "{\"character_id\":$A,\"stone_kind\":\"stone_2\",\"stone_amount\":2,\"price_gold\":1000}")"
  LID="$(printf '%s' "$R" | jget listing_id)"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] && [ -n "$LID" ] && [ "$LID" != "0" ] \
    && ok "任务发的灵石挂上拍卖（listing=${LID}）" || bad "挂单失败：$R"
  R="$(post /auction/stone/list "{\"character_id\":$A,\"stone_kind\":\"yuanbao\",\"stone_amount\":1,\"price_gold\":10}")"
  [ "$(printf '%s' "$R" | jget reason)" = "unknown_stone" ] && ok "元宝不是灵石档，挂单被拒" || bad "坏档位没挡住：$R"
  R="$(post /auction/stone/buy "{\"character_id\":$B,\"listing_id\":$LID}")"
  [ "$(printf '%s' "$R" | jget ok)" = "true" ] \
    && [ "$(printf '%s' "$R" | jget tax_gold)" = "60" ] \
    && [ "$(printf '%s' "$R" | jget net_gold)" = "940" ] \
    && [ "$(printf '%s' "$R" | jget state)" = "sold" ] \
    && ok "买入成交：1000 标价、销毁税 60、卖家净额 940" || bad "成交回包不对：$R"
  [ "$(bal "$B" stone_2)" = "2" ] && [ "$(bal "$A" stone_2)" = "$((S0-2))" ] \
    && ok "灵石等量转移：买家 +2、卖家 -2" || bad "灵石转移不对"
  [ "$(bal "$B" gold)" = "0" ] && [ "$(bal "$A" gold)" = "940" ] \
    && ok "税不进任何角色：双方金币合计 940（净减少 60）" || bad "税进了角色钱包"
  CS="$("$DOTNET" "$CD" --city-state $CC 2>&1 | utf8 | grep -m1 '^city_state=')"
  printf '%s' "$CS" | grep -q 'city_state=ok .*city_tax_rows=.*auction_tax_rows=' \
    && ok "城池税收账本与拍卖销毁税各自计数（两张表）" || bad "城池账本读不到：$CS"
  R="$(post /world/gather "{\"character_id\":$A,\"node_id\":\"spirit_qiyun_1\"}")"
  [ "$(printf '%s' "$R" | jget ok)" = "false" ] \
    && [ "$(printf '%s' "$R" | jget reason)" = "spirit_stone_forbidden" ] \
    && [ "$(printf '%s' "$R" | jget count)" = "0" ] \
    && ok "个人采集灵石矿脉被拒：spirit_stone_forbidden" || bad "矿脉没被拒：$R"
  [ "$(bal "$A" stone_1)" = "0" ] && ok "被拒后灵石余额不变" || bad "采集拒绝仍改了灵石"
fi

echo "== 4. 回归入口 =="
printf '  ..   依赖闭包：bash tools/check/check-all.sh --card p3-2a（p1-3b / p1-5a 及其上游）\n'
printf '  ..   老卡：check-p1-3b.sh / check-p1-5a.sh / check-p1-1b4.sh / check-e8.sh\n'
finish
