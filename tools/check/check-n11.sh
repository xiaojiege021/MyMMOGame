#!/usr/bin/env bash
# N11 家园种植与家园币（开发架构第 14 节第 90 行，依赖 C3）。
#   bash tools/check/check-n11.sh
set -uo pipefail
unset HTTP_PROXY http_proxy HTTPS_PROXY https_proxy ALL_PROXY all_proxy
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
SOCIAL_DLL="$ROOT/src/Social/bin/Release/net10.0/Social.dll"
CHAR_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
SC="--config $ROOT/config/social.json"
CC="--config $ROOT/config/character.json"
TMP="$(mktemp -d)"
TITLE="N11"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
for proj in src/Shared/Shared.csproj src/Social/Social.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj; do
  n="$(basename "$proj" .csproj)"
  if "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/b-$n.log" 2>&1; then
    ok "编译 $n"
  else
    bad "编译失败 $n"
    grep -E 'error ' "$TMP/b-$n.log" | head -3 | detail
  fi
done

echo "== 2. 静态断言 =="
python3 - "$ROOT" >"$TMP/static.out" <<'PY'
import json, os, sys
root = sys.argv[1]
def R(k,c): print(("PASS" if c else "FAIL")+":"+k)

html = open(os.path.join(root,"docs/MMO系统架构方案.html"),encoding="utf-8").read()
arch = open(os.path.join(root,"docs/开发架构.md"),encoding="utf-8").read()
cur = json.load(open(os.path.join(root,"config/currency.json"),encoding="utf-8"))
hc = json.load(open(os.path.join(root,"config/home_coin_shop.json"),encoding="utf-8"))
plant = json.load(open(os.path.join(root,"config/homestead_plant.json"),encoding="utf-8"))

R("方案口径", "家园种植产出家园币" in html or "家园币" in html)
R("14节N11", "| 90 | N11 |" in arch and "种植产出家园币" in arch)
R("currency", any(x.get("id")=="home_coin" for x in cur["currencies"]))
R("plant_slots", plant["slots"]==["garden_1","garden_2"])
R("forbid_protect", not any(i.get("kind")=="protect_ticket" for i in hc["items"]))
R("shop_currency", hc.get("currency")=="home_coin")

soc = open(os.path.join(root,"src/Social/HomesteadPlantRepository.cs"),encoding="utf-8").read()
grant = open(os.path.join(root,"src/Social/HomesteadHarvestGrant.cs"),encoding="utf-8").read()
R("grant_harvest", "homestead_harvest:" in soc and "home_coin" in grant)
R("slot_mutex", "slot_has_plant" in open(os.path.join(root,"src/Social/HomesteadRepository.cs"),encoding="utf-8").read())
R("proto_plant", "rpc PlantHomesteadCrop" in open(os.path.join(root,"proto/mmo/social.proto"),encoding="utf-8").read())
R("proto_buy", "BuyHomeCoinShopItem" in open(os.path.join(root,"proto/mmo/character.proto"),encoding="utf-8").read())
R("gateway", "/homestead/plant" in open(os.path.join(root,"src/Gateway/HomesteadPlantApi.cs"),encoding="utf-8").read())
R("all_cards", "n11)" in open(os.path.join(root,"tools/check/check-all.sh"),encoding="utf-8").read())
PY
while IFS=: read -r st k; do
  case "$st" in PASS) ok "$k" ;; FAIL) bad "$k" ;; esac
done < "$TMP/static.out"

echo "== 3. 坏配置（保护券不得进家园币商城）=="
python3 - "$ROOT" <<'PY'
import json, os, sys
root = sys.argv[1]
hc = json.load(open(os.path.join(root,"config/home_coin_shop.json"),encoding="utf-8"))
hc["items"].append({
    "id":"x","kind":"protect_ticket","name":"x",
    "price_home_coin":1,"delivery_item_id":"ticket_protect","delivery_count":1})
bad = os.path.join(root,"config","home_coin_shop.json")
bak = bad + ".n11bak"
open(bak,"w",encoding="utf-8").write(open(bad,encoding="utf-8").read())
json.dump(hc, open(bad,"w"), ensure_ascii=False)
PY
OUT2="$("$DOTNET" "$CHAR_DLL" $CC 2>&1)" || true
mv "$ROOT/config/home_coin_shop.json.n11bak" "$ROOT/config/home_coin_shop.json"
if printf '%s' "$OUT2" | grep -q 'config_error='; then
  ok "保护券种类加载期拒绝"
else
  bad "坏 home_coin_shop 没拦住"
fi

finish "$TITLE"