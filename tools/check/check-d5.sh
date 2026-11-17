#!/usr/bin/env bash
# tools/check/check-d5.sh —— D5 断言只钉接口存在（开发架构 14.6 顺序 3）。
#
# 完成标准（14.6 的 D5 行）：
#   老卡断言不再写「已接 / 未接」；接一个新口只改该卡断言。
#
# 本脚本只做静态核对，不编译、不连库、不跑 Unity：
#   1. 政策扫描：assert-policy.py 在 tools/check/check-*.sh 上 0 命中
#      （已接/未接措辞、delivered_kinds 写死数字、git diff 钉整目录）；
#   2. 可变集合只写下限：逐条钉具体脚本里的下限式子，不再数等号；
#   3. 接口存在：网关路由 / Net 拆装件 / 流程出口按「在」断言（U10–U12 的改造样本）；
#   4. 护栏挂载：preflight 第 5 道闸、check-all 三处、ci.sh 的 CHECKS；
#   5. 扫描器自检：喂三个坏样本，必须三种规则各抓一条，不能是空跑。
#
#   bash tools/check/check-d5.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/tools/check/lib-common.sh"

TITLE="D5（断言口径）"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 政策扫描（0 命中） =="
if python3 tools/check/assert-policy.py >"$TMP/policy.txt" 2>&1; then
  ok "老卡断言只钉接口存在：没有 已接/未接、delivered_kinds 写死数字、整目录 diff"
else
  bad "政策扫描有命中"
  grep '^HIT ' "$TMP/policy.txt" | head -12 | detail
fi

echo "== 2. 可变集合只写下限 =="
python3 - "$ROOT" >"$TMP/floor.txt" 2>&1 <<'PY'
import os
import re
import sys

root = sys.argv[1]
# (文件, 必须出现的下限写法, 不许再出现的等号写法, 说明)
CASES = [
    ("check-d1a.sh", r">= 136 and not missing", r"== 136", "D1a flag 名单：136 个一个不少，后续卡只追加"),
    ("check-d1b.sh", r">= 135 and not missing_flags", r"== 135", "D1b probe flag：133 个一个不少，后续卡只追加"),
    ("check-k1.sh", r'\[ "\$rows" -ge 18 \]', r'\[ "\$rows" = "18" \]', "K1 商城条数：只写下限"),
    ("check-p1-5c1.sh", r"ITEMS_N", r"items=18 ", "P1-5c1 商城条数：从只读口现取，不写死"),
    ("check-p1-5c2.sh", r"-ge 18", r'= "18"', "P1-5c2 商城条数：只写下限"),
    ("check-p1-5c4.sh", r'len\(shop\["items"\]\) >= 18', r'len\(shop\["items"\]\) == 18', "P1-5c4 商城条数：只写下限"),
    ("check-p1-5c8.sh", r'len\(shop\["items"\]\) >= 18', r'len\(shop\["items"\]\) == 18', "P1-5c8 商城条数：只写下限"),
    ("check-p2-1a4.sh", r'len\(shop\["items"\]\) >= 18', r'len\(shop\["items"\]\) == 18', "P2-1a4 商城条数：只写下限"),
    ("check-k6.sh", r'len\(bosses\) >= 4', r'len\(bosses\) == 4', "K6 副本 Boss：四条在 + 至少四条"),
    ("check-c5.sh", r"len\(lines\) >= 14", r"len\(lines\) == 14", "C5 对话行数：14 条在 + 至少 14"),
    ("check-c5.sh", r"len\(files\) >= 9", r"len\(files\) == 9", "C5 开口人物：9 位在 + 至少 9"),
    ("check-c7.sh", r"d_ledger", r'-eq 9|"-eq 8"', "C7 子目录份数：从台账现取"),
    ("check-n4.sh", r'len\(base\["nodes"\]\) >= 4', r'len\(base\["nodes"\]\) == 4', "N4 基准节点：只写下限"),
    ("check-n4.sh", r'len\(spread\["nodes"\]\) >= 14', r'len\(spread\["nodes"\]\) == 14', "N4 铺开节点：只写下限"),
    ("check-u1.sh", r"len\(tokens\) >= 36", r"len\(tokens\) == 36", "U1 reason 词表：只写下限"),
    ("check-s1.sh", r"len\(skills\) >= 15", r"len\(skills\) == 15", "S1 基础技能：15 条在 + 至少 15"),
    ("check-c2.sh", r"SPREAD_N", r"nodes=14", "C2 铺开节点：从只读口现取"),
    ("check-u10.sh", r"assert_interface", r"diff --quiet -- config proto", "U10 整目录 diff 改成接口存在"),
    ("check-u11.sh", r"assert_interface", r"diff --quiet -- config proto", "U11 整目录 diff 改成接口存在"),
    ("check-u12.sh", r"assert_interface", r"diff --quiet -- config proto", "U12 整目录 diff 改成接口存在"),
]

fails = 0
for name, must, forbid, why in CASES:
    path = os.path.join(root, "tools/check", name)
    text = open(path, encoding="utf-8").read() if os.path.exists(path) else ""
    if not text:
        print("FAIL %s 不存在" % name)
        fails += 1
        continue
    if not re.search(must, text):
        print("FAIL %s 缺下限写法：%s" % (name, must))
        fails += 1
    elif re.search(forbid, text):
        print("FAIL %s 还在写等号：%s" % (name, forbid))
        fails += 1
    else:
        print("PASS %s：%s" % (name, why))
PY
while IFS= read -r line; do
  case "$line" in
    PASS\ *) ok "${line#PASS }" ;;
    FAIL\ *) bad "${line#FAIL }" ;;
    *) printf '%s\n' "$line" | detail ;;
  esac
done < "$TMP/floor.txt"

echo "== 3. 接口存在（网关路由 / Net 拆装件 / 流程出口） =="
assert_interface src/Gateway/HomesteadPlantApi.cs 'MapGet\("/homestead/plants"' "种植只读口在"
assert_interface src/Gateway/HomeCoinShopApi.cs 'MapPost\("/home-coin/shop/buy"' "家园币购买口在"
assert_interface src/Gateway/HeroCodexApi.cs 'MapGet\("/hero-codex"' "图鉴只读口在"
assert_interface src/Gateway/VersusApi.cs 'MapPost\("/versus/arena/enter"' "竞技场进场口在"
assert_interface src/Gateway/VersusApi.cs 'MapPost\("/versus/battlefield/enter"' "战场进场口在"
assert_interface src/Gateway/RealmPhaseApi.cs 'MapPost\("/realm/phase/promote"' "境界升阶口在"
assert_interface client/MMOGame/Assets/HotUpdate/Ui/UiHomesteadScreen.cs \
  'HomesteadPlantEntry\.PlantInSlot' "U10 屏仍按 Net 拆装件摆种植块"
assert_interface client/MMOGame/Assets/HotUpdate/Ui/UiHeroCodexScreen.cs \
  'HeroCodexEntry\.EntryAt' "U11 屏仍按 Net 拆装件摆九格"
assert_interface client/MMOGame/Assets/HotUpdate/Ui/UiArenaScreen.cs \
  'VersusEntry\.SummaryLine' "U12 屏仍按 Net 拆装件摆进场卡"
assert_interface client/MMOGame/Assets/HotUpdate/Ui/UiSocialFlow.cs 'LoadHeroCodex' "Flow 仍有图鉴读入口"

echo "== 4. 护栏挂载 =="
assert_interface tools/check/preflight.sh 'assert-policy\.py' "preflight 接上政策扫描"
assert_interface tools/check/preflight.sh '5\. 断言只钉接口存在' "preflight 有第 5 道闸"
assert_interface tools/check/check-all.sh 'd5\) echo "d4"' "check-all 的依赖表挂了 d5"
assert_interface tools/check/check-all.sh ' d5[ "]' "check-all 的 ALL_CARDS 挂了 d5"
assert_interface tools/check/ci.sh 'd5' "ci.sh 的 CHECKS 挂了 d5"
assert_interface tools/check/lib-common.sh 'assert_interface\(\)' "lib-common 提供 assert_interface"
assert_interface tools/check/lib-common.sh 'assert_floor\(\)' "lib-common 提供 assert_floor"

echo "== 5. 扫描器自检（坏样本必须被抓住） =="
mkdir -p "$TMP/policy"
printf '%s\n' 'ok "还没接发货"' > "$TMP/policy/bad-wording.sh"
printf '%s\n' 'grep -q "delivered_kinds=5" x' > "$TMP/policy/bad-delivered.sh"
printf '%s\n' 'git diff --quiet -- config proto' > "$TMP/policy/bad-diff.sh"
printf '%s\n' 'ok "接口在"' > "$TMP/policy/good.sh"
if python3 tools/check/assert-policy.py \
     --files "$TMP/policy/bad-wording.sh" "$TMP/policy/bad-delivered.sh" "$TMP/policy/bad-diff.sh" \
     >"$TMP/self.txt" 2>&1; then
  bad "自检：三个坏样本没被抓住"
  detail < "$TMP/self.txt"
else
  n=$(grep -c '^HIT ' "$TMP/self.txt" || true)
  if [ "$n" = "3" ] && grep -q '^HIT .*:wording:' "$TMP/self.txt" \
     && grep -q '^HIT .*:delivered:' "$TMP/self.txt" \
     && grep -q '^HIT .*:dir_diff:' "$TMP/self.txt"; then
    ok "自检：三种规则各抓一条（${n} 处）"
  else
    bad "自检：规则没全抓到（${n} 处）"
    detail < "$TMP/self.txt"
  fi
fi
if python3 tools/check/assert-policy.py --files "$TMP/policy/good.sh" >/dev/null 2>&1; then
  ok "自检：干净样本不误报"
else
  bad "自检：干净样本被误报"
fi

finish "$TITLE"
