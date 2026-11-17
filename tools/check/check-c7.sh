#!/usr/bin/env bash
# tools/check/check-c7.sh —— C7 数值台账与配置化审计
#
# 完成标准（开发架构 14.6 第 8 行）：
#   所有可调数值都在 config/ 且有文件、键、现值、负责人；代码与 proto 里没有第二套可调数；
#   SQL 的 CHECK 只作最后一道闸；改配置重启生效。
#
# 这个脚本守四件事，任何一件破了就红：
#   1. 台账表 A 与 config/ 一一对应（少一份、多一份都红）
#   2. 台账表 B 与配置里 pending=true 的集合双向相等（占位数值不许漏登记，也不许登记了配置里没有）
#   3. 代码里的数值常量都登记在表 C；不许用 `?? 数字` 给配置字段兜默认值
#   4. proto 里没有可调数值；db/ 里不钉方案现值
# 最后一段起角色服的只读入口，证明「改配置重启生效」：改一个值就跟着变，缺字段 / 越界就起不来。
#
# 用法：tools/check/check-c7.sh     退出码 0 全过，1 有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/tools/check/lib-common.sh"

LEDGER="docs/数值台账.md"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"

step() { printf '\n== %s ==\n' "$1"; }

# ---------- 1. 台账表 A 覆盖 config/ ----------
step "1. 台账表 A 与 config/ 一一对应"

if [ ! -f "$LEDGER" ]; then
  bad "缺 $LEDGER"
  finish "C7（台账 + 配置化审计）"
fi

missing=0
for f in config/*.json; do
  b="$(basename "$f")"
  grep -q "\`$b\`" "$LEDGER" || { bad "台账表 A 漏了 $b"; missing=$((missing + 1)); }
done
[ "$missing" -eq 0 ] && ok "config 下 $(ls config/*.json | wc -l | tr -d ' ') 份 JSON 全部在表 A"

# 台账里写了、config 里却没有的（改名 / 删表之后必须同步改台账）
stale=0
while read -r b; do
  [ -n "$b" ] || continue
  [ -f "config/$b" ] || { bad "台账表 A 写了 $b，config 下没有这份文件"; stale=$((stale + 1)); }
done < <(grep -oE '^\| `[a-z_0-9]+\.json`' "$LEDGER" | sed 's/^| `//; s/`$//' | sort -u)
[ "$stale" -eq 0 ] && ok "台账里的文件名在 config 下都存在"

# 两个子目录按份数核：目录本身不能凭空少文件。份数从台账现取，
# 不把「9 份 / 8 份」再写第二遍——加一份配置只改台账，不用改这张脚本（D5 口径）。
d_n=$(ls config/dialogue/*.json 2>/dev/null | wc -l | tr -d ' ')
r_n=$(ls config/region/*.json 2>/dev/null | wc -l | tr -d ' ')
d_ledger=$(grep -oE 'dialogue/` [0-9]+ 份' "$LEDGER" | grep -oE '[0-9]+' | head -1)
r_ledger=$(grep -oE 'region/` [0-9]+ 份' "$LEDGER" | grep -oE '[0-9]+' | head -1)
if [ -n "${d_ledger:-}" ] && [ "$d_n" -eq "$d_ledger" ]; then
  ok "config/dialogue ${d_n} 份，与台账一致"
else
  bad "config/dialogue 现 ${d_n} 份，台账写 ${d_ledger:-缺}"
fi
if [ -n "${r_ledger:-}" ] && [ "$r_n" -eq "$r_ledger" ]; then
  ok "config/region ${r_n} 份，与台账一致"
else
  bad "config/region 现 ${r_n} 份，台账写 ${r_ledger:-缺}"
fi

# ---------- 2. 表 B 与配置里的 pending 双向相等 ----------
step "2. 表 B 与配置里的 pending=true 双向相等"

python3 - "$LEDGER" >"$TMP/pending.txt" <<'PY'
import json, os, re, sys

ledger = open(sys.argv[1], encoding='utf-8').read()


def section(start, end):
    a = ledger.find(start)
    if a < 0:
        raise SystemExit('LEDGER_MISSING ' + start)
    b = ledger.find(end, a)
    return ledger[a:b if b > 0 else len(ledger)]


top, items = set(), set()


def walk(node, path, fname):
    if isinstance(node, dict):
        if node.get('pending') is True:
            (top if path == '' else items).add(fname if path == '' else fname + '|' + path)
        for k, v in node.items():
            walk(v, (path + '.' + k) if path else k, fname)
    elif isinstance(node, list):
        for i, v in enumerate(node):
            walk(v, '%s[%d]' % (path, i), fname)


for f in sorted(os.listdir('config')):
    if not f.endswith('.json'):
        continue
    d = json.load(open(os.path.join('config', f), encoding='utf-8'))
    walk(d, '', f)

s21 = section('### 2.1', '### 2.2')
s22 = section('### 2.2', '### 2.3')

led_top = set(re.findall(r'^\| `([a-z_0-9]+\.json)`', s21, re.M))
led_item = set()
for m in re.finditer(r'^\| `([a-z_0-9]+\.json)` \| `([^`]+)`', s22, re.M):
    led_item.add(m.group(1) + '|' + m.group(2))

print('CFG_TOP=%d LED_TOP=%d CFG_ITEM=%d LED_ITEM=%d'
      % (len(top), len(led_top), len(items), len(led_item)))
for name, cfg, led in (('顶层', top, led_top), ('条目', items, led_item)):
    for x in sorted(cfg - led):
        print('DIFF 配置有、台账缺（%s）：%s' % (name, x))
    for x in sorted(led - cfg):
        print('DIFF 台账有、配置缺（%s）：%s' % (name, x))
PY

if grep -q '^LEDGER_MISSING' "$TMP/pending.txt"; then
  bad "台账缺小节：$(grep '^LEDGER_MISSING' "$TMP/pending.txt" | head -1)"
else
  head -1 "$TMP/pending.txt" | detail
  if grep -q '^DIFF ' "$TMP/pending.txt"; then
    while read -r l; do bad "$l"; done < <(grep '^DIFF ' "$TMP/pending.txt" | sed 's/^DIFF //')
  else
    ok "表 B 与配置的 pending 集合完全一致（差集为空）"
  fi
  # 一行统计都没出来说明 python 崩了：不能让「0 通过 0 失败」混过去
  grep -q '^CFG_TOP=' "$TMP/pending.txt" || bad "pending 采集没跑出统计行"
fi

# ---------- 3. 代码里的数值常量与 `?? 数字` ----------
step "3. 代码里的数值常量都登记在表 C，且不给配置兜默认值"

python3 - "$LEDGER" >"$TMP/consts.txt" <<'PY'
import os, re, sys

ledger = open(sys.argv[1], encoding='utf-8').read()
skip = {'obj', 'bin', 'Generated'}
names = {}
for base, dirs, fs in os.walk('src'):
    dirs[:] = [d for d in dirs if d not in skip]
    if 'Tests' in base:
        continue
    for f in fs:
        if not f.endswith('.cs'):
            continue
        p = os.path.join(base, f)
        for line in open(p, encoding='utf-8', errors='ignore'):
            m = re.search(r'const\s+(?:int|long|double|float|decimal)\s+(\w+)\s*=\s*(\d[\d_]*)', line)
            if m:
                names.setdefault(m.group(1), (m.group(2), p))
print('CONST_TOTAL=%d' % len(names))
for n, (v, p) in sorted(names.items()):
    if n not in ledger:
        print('MISS %s = %s (%s)' % (n, v, p))
PY

if grep -q '^CONST_TOTAL=' "$TMP/consts.txt"; then
  if grep -q '^MISS ' "$TMP/consts.txt"; then
    while read -r l; do bad "常量未登记进表 C：$l"; done < <(grep '^MISS ' "$TMP/consts.txt" | sed 's/^MISS //')
  else
    ok "代码里的 $(sed -n 's/^CONST_TOTAL=//p' "$TMP/consts.txt") 个数值常量都已在表 C 登记"
  fi
else
  bad "常量采集没跑出结果"
fi

# `?? 数字` 读配置：只允许「零值兜底」（?? 0，语义就是没配＝0，不是可调数值）
# 和两处结构兜底（版本号、堆叠上限）；?? 非零数字 = 代码里藏了第二份可调值，一律红。
# 就是这条抓出了 v1.173 之前的 bag_slots ?? 40 / fatigue_daily_cap ?? 6000 / db_pool_max ?? 8。
ZERO_FALLBACK="$(grep -rn '?? *0\b' src --include=*.cs 2>/dev/null \
  | grep -v -e '/obj/' -e '/bin/' -e 'Generated' -e 'Tests' | wc -l | tr -d ' ')"
FALLBACK="$(grep -rn '?? *[1-9]' src --include=*.cs 2>/dev/null \
  | grep -v -e '/obj/' -e '/bin/' -e 'Generated' -e 'Tests' \
  | grep -v -e 'Version ?? 1' -e 'StackMax ?? 1' || true)"
if [ -z "$FALLBACK" ]; then
  ok "没有用 ?? 非零数字给配置字段兜默认值（零值兜底 ${ZERO_FALLBACK} 处不算，只代表「没配＝0」）"
else
  printf '%s\n' "$FALLBACK" | head -5 | detail
  bad "有配置字段靠代码默认值兜底（$(printf '%s\n' "$FALLBACK" | wc -l | tr -d ' ') 处）"
fi

# ---------- 4. proto 与 db ----------
step "4. proto 里没有可调数值，db 里不钉方案现值"

# proto 里只该有字段编号与枚举值。查小数（可调数值的典型形态）时要先去注释，
# 否则「方案 12.1」「P1-5c6」这种文档编号会被当成数值。
proto_num="$(awk '{ sub(/\/\/.*/, "") } /[0-9]+\.[0-9]+/ { print FILENAME ":" FNR ": " $0 }' \
  proto/mmo/*.proto 2>/dev/null || true)"
proto_default="$(grep -rn '\[default' proto/mmo/*.proto 2>/dev/null || true)"
if [ -z "$proto_num" ] && [ -z "$proto_default" ]; then
  ok "proto 里只有字段编号与枚举值，没有小数、没有默认值"
else
  [ -n "$proto_num" ] && bad "proto 里出现小数：$(printf '%s' "$proto_num" | head -2 | tr '\n' ' ')"
  [ -n "$proto_default" ] && bad "proto 里出现字段默认值：$(printf '%s' "$proto_default" | head -2 | tr '\n' ' ')"
fi

# 配置现值不许进库：日疲劳上限只在 config/dungeons.json
if grep -q 'fatigue <= 6000' db/character/character.sql 2>/dev/null; then
  bad "character.sql 把日疲劳上限钉成了 6000（上界应写荒谬值 1000000，不写方案现值）"
else
  ok "character.sql 的疲劳 CHECK 没钉方案现值"
fi
if grep -q 'character_fatigue_daily_cap CHECK (fatigue <= 1000000)' db/character/character.sql 2>/dev/null; then
  ok "疲劳 CHECK 上界是 1000000，只作最后一道闸"
else
  bad "找不到 fatigue <= 1000000 这条兜底约束"
fi
if grep -rqE '\b(6000|1500)\b' db/ 2>/dev/null; then
  bad "db/ 里出现了方案现值（6000 / 1500）：$(grep -rlE '\b(6000|1500)\b' db/ | head -3 | tr '\n' ' ')"
else
  ok "db/ 里没有方案现值（6000 / 1500 一处不见）"
fi
printf '       db CHECK 共 %s 条（分四类，全部只作最后一道闸，见台账第 4 节）\n' \
  "$(grep -rho 'CHECK *(' db/*/*.sql | wc -l | tr -d ' ')"

# ---------- 5. 改配置重启生效 ----------
step "5. 改配置重启生效（角色服只读入口）"

if [ ! -f "$PROBE" ]; then
  echo "       跳过：没有 $PROBE（先 dotnet build src/Character/Character.csproj）"
else
  # 现值：仓库那一份读出来是 40
  "$DOTNET" "$PROBE" --config-show --config config/character.json >"$TMP/now.out" 2>&1
  if grep -q '^bag_slots=40$' "$TMP/now.out"; then
    ok "config/character.json 的 bag_slots 现值 40 能从只读入口读到"
  else
    bad "只读入口没读到 bag_slots=40：$(head -3 "$TMP/now.out" | tr '\n' ' ')"
  fi

  # 改一个值再读：跟着变，证明值只来自配置
  python3 - config/character.json "$TMP/fifty.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding='utf-8'))
d['bag_slots'] = 50
json.dump(d, open(sys.argv[2], 'w', encoding='utf-8'), ensure_ascii=False)
PY
  "$DOTNET" "$PROBE" --config-show --config "$TMP/fifty.json" >"$TMP/fifty.out" 2>&1
  if grep -q '^bag_slots=50$' "$TMP/fifty.out"; then
    ok "把 bag_slots 改成 50，只读入口跟着变成 50（代码里没有第二份）"
  else
    bad "改了 bag_slots=50 但入口仍报别的值：$(head -3 "$TMP/fifty.out" | tr '\n' ' ')"
  fi

  # 缺字段：起不来
  python3 - config/character.json "$TMP/miss.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding='utf-8'))
d.pop('bag_slots')
json.dump(d, open(sys.argv[2], 'w', encoding='utf-8'), ensure_ascii=False)
PY
  "$DOTNET" "$PROBE" --config-show --config "$TMP/miss.json" >"$TMP/miss.out" 2>&1
  miss_code=$?
  if grep -q 'config_error' "$TMP/miss.out" && [ "$miss_code" -ne 0 ]; then
    ok "缺 bag_slots 直接 config_error 且非 0 退出（$(head -1 "$TMP/miss.out")）"
  else
    bad "缺 bag_slots 没有拒绝：码 ${miss_code}，$(head -2 "$TMP/miss.out" | tr '\n' ' ')"
  fi

  # 越界：起不来
  python3 - config/character.json "$TMP/zero.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding='utf-8'))
d['bag_slots'] = 0
json.dump(d, open(sys.argv[2], 'w', encoding='utf-8'), ensure_ascii=False)
PY
  "$DOTNET" "$PROBE" --config-show --config "$TMP/zero.json" >"$TMP/zero.out" 2>&1
  zero_code=$?
  if grep -q 'config_error' "$TMP/zero.out" && [ "$zero_code" -ne 0 ]; then
    ok "bag_slots=0 直接 config_error 且非 0 退出"
  else
    bad "bag_slots=0 没有拒绝：码 $zero_code"
  fi

  # 副本表的日上限同样缺了就起不来（DungeonTable 与启动共用同一个加载器）
  python3 - config/dungeons.json "$TMP/no_cap.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding='utf-8'))
d.pop('fatigue_daily_cap')
json.dump(d, open(sys.argv[2], 'w', encoding='utf-8'), ensure_ascii=False)
PY
  "$DOTNET" "$PROBE" --dungeon-table-path "$TMP/no_cap.json" >"$TMP/no_cap.out" 2>&1
  cap_code=$?
  if grep -q 'config_error' "$TMP/no_cap.out" && [ "$cap_code" -ne 0 ]; then
    ok "dungeons.json 缺 fatigue_daily_cap 直接 config_error 且非 0 退出"
  else
    bad "缺 fatigue_daily_cap 没有拒绝：码 ${cap_code}，$(head -2 "$TMP/no_cap.out" | tr '\n' ' ')"
  fi

  # 改一个副本表的数：入口跟着变（这张表由角色服与战斗服共读，改一处两处都变）
  python3 - config/dungeons.json "$TMP/cap.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding='utf-8'))
d['fatigue_daily_cap'] = 7000
json.dump(d, open(sys.argv[2], 'w', encoding='utf-8'), ensure_ascii=False)
PY
  "$DOTNET" "$PROBE" --dungeon-table-path "$TMP/cap.json" >"$TMP/cap.out" 2>&1
  if grep -q '^dungeon_table_state=ok' "$TMP/cap.out"; then
    ok "把 fatigue_daily_cap 改成 7000 仍能加载（库里没钉 6000，改上限不用改库）"
  else
    bad "fatigue_daily_cap=7000 加载失败：$(head -2 "$TMP/cap.out" | tr '\n' ' ')"
  fi
fi

# 一行 ok/FAIL 都没出就不算通过（脚本整段崩掉时会「0 通过 0 失败」）
[ $((PASS + FAIL)) -gt 0 ] || bad "静态段没跑出任何结果"

finish "C7（台账 + 配置化审计）"
