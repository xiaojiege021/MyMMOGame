#!/usr/bin/env bash
# tools/check/check-d15.sh —— D15 不可逆点清单与变异验证
#
# 完成标准（开发架构 14.10）：
#   人工审查只落在四类不可逆点上；有清单可依；有一件工具能证明断言不是空转。
#
# 这个脚本守六件事，任何一件破了就红：
#   1. docs/不可逆点清单.md 在，四类齐全，每类都有判据 / 不可逆原因 / 审查问句
#   2. 清单里引用的锚点在仓库里真实存在（写文档最怕引用了已经改名的文件）
#   3. tools/check/mutate.sh 语法正确、可执行、登记目录有内容
#   4. 每份 mutations/<卡>.json 字段齐、目标文件在
#   5. 变异器自检：临时目录造一份假卡，验「抓住 / 空转 / 还原 / 基线红」四种行为
#      —— 这条是整张卡的关键：工具本身错的时候，它会告诉你一切正常
#   6. 护栏挂上：preflight 第 6 道闸、check-all 三处、ci.sh、14.10、任务队列
#
# 不需要 gamedb、不需要 Unity、不跑真实变异（那个按阶段闸门手工跑）。
#   bash tools/check/check-d15.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/tools/check/lib-common.sh"

LEDGER="docs/不可逆点清单.md"
MUTATE="tools/check/mutate.sh"
MUT_DIR="tools/check/mutations"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

step() { printf '\n== %s ==\n' "$1"; }

# ---------------------------------------------------------------------------
# 1. 清单文档与四类
# ---------------------------------------------------------------------------
step "1. 不可逆点清单与四类"

if [ ! -f "$LEDGER" ]; then
  bad "缺 $LEDGER"
  finish "D15（不可逆点清单 + 变异验证）"
fi
ok "清单文件在（${LEDGER}）"

# 四类各起一个函数调，别用 `for pair in "1:带空格的名字"`：for 会按空格分词，
# 中文标题一带空格就散架（Mac bash 3.2）。
assert_class() {
  local n="$1" want="$2"
  if grep -q "^## ${n}\. .*${want}" "$LEDGER"; then
    ok "第 ${n} 类（${want}）在"
  else
    bad "第 ${n} 类（${want}）缺"
  fi
}
assert_class 1 "schema 与迁移顺序"
assert_class 2 "钱的写入口与事务边界"
assert_class 3 "协议拒绝码"
assert_class 4 "跨进程数值口径"

assert_section() {
  local sec="$1"
  local cnt
  cnt="$(grep -c "^\*\*${sec}" "$LEDGER" || true)"
  assert_floor "$cnt" 4 "每类都有「${sec}」小节"
}
assert_section "判据"
assert_section "为什么不可逆"
assert_section "15 分钟里只问三句"
assert_section "锚点"

# 用法段与变异验证的入口写清楚，否则没人会跑
assert_file_contains "$LEDGER" 'mutate\.sh --card' "清单里写了变异验证的用法"
assert_file_contains "$LEDGER" '空转' "清单里解释了「断言空转」"

# ---------------------------------------------------------------------------
# 2. 锚点真实存在
# ---------------------------------------------------------------------------
step "2. 清单引用的锚点存在"

for f in db/migrations.json config/currency.json config/auction.json \
         db/trade/auction.sql src/Character/ExpSettlementRepository.cs; do
  if [ -e "$f" ]; then
    ok "锚点在：$f"
  else
    bad "锚点不存在：${f}（清单引用了已经改名 / 删掉的文件）"
  fi
done

# 清单里那条最容易被人「顺手补上」的约束：拍卖税没有收款方
assert_file_contains db/trade/auction.sql 'net_gold \+ tax_gold = price_gold' \
  "拍卖表仍有 net_gold + tax_gold = price_gold"
assert_file_contains "$LEDGER" 'tax 没有收款方' "清单点明了 tax 无收款方"

# ---------------------------------------------------------------------------
# 3. 变异器本体
# ---------------------------------------------------------------------------
step "3. 变异器本体"

if [ ! -f "$MUTATE" ]; then
  bad "缺 $MUTATE"
  finish "D15（不可逆点清单 + 变异验证）"
fi

if bash -n "$MUTATE" 2>"$TMP/syntax.log"; then
  ok "mutate.sh 语法通过"
else
  bad "mutate.sh 语法错"; tail -5 "$TMP/syntax.log" | detail
fi

if [ -x "$MUTATE" ]; then
  ok "mutate.sh 可执行"
else
  bad "mutate.sh 没有可执行位（跑不起来）"
fi

# 基线这个概念必须在：卡本来就红的时候，变异结论是假的
assert_file_contains "$MUTATE" '基线' "mutate.sh 有基线检查"
assert_file_contains "$MUTATE" '还原失败' "mutate.sh 跑完校验 md5 还原"
assert_file_contains "$MUTATE" '空转' "mutate.sh 会把空转报出来"

# ---------------------------------------------------------------------------
# 4. 登记表格式
# ---------------------------------------------------------------------------
step "4. 变异登记表"

regs="$(ls "$MUT_DIR"/*.json 2>/dev/null || true)"
if [ -z "$regs" ]; then
  bad "没有已登记的变异（$MUT_DIR 为空）"
else
  n_reg="$(printf '%s\n' "$regs" | wc -l | tr -d ' ')"
  assert_floor "$n_reg" 1 "至少登记了一张卡的变异"
fi

if [ -n "$regs" ]; then
  for reg in $regs; do
    out="$(python3 - "$reg" <<'PY'
import json, sys
p = sys.argv[1]
try:
    d = json.load(open(p, encoding='utf-8'))
except Exception as e:
    print('ERR json 坏了：%s' % e); sys.exit(1)
if not d.get('card'):
    print('ERR 缺 card'); sys.exit(1)
ms = d.get('mutations') or []
if not ms:
    print('ERR mutations 为空'); sys.exit(1)
for i, m in enumerate(ms):
    for k in ('id', 'file', 'kind', 'expect'):
        if not m.get(k):
            print('ERR 第 %d 条缺 %s' % (i, k)); sys.exit(1)
    if m['kind'] == 'text-replace' and ('find' not in m or 'replace' not in m):
        print('ERR 第 %d 条 text-replace 缺 find/replace' % i); sys.exit(1)
    if m['kind'] == 'json-set' and ('path' not in m or 'value' not in m):
        print('ERR 第 %d 条 json-set 缺 path/value' % i); sys.exit(1)
print('ok %d' % len(ms))
PY
)"
    if printf '%s' "$out" | grep -q '^ok'; then
      ok "$(basename "$reg") 登记合法（$(printf '%s' "$out" | sed 's/^ok //') 条）"
    else
      bad "$(basename "$reg") 格式错：$(printf '%s' "$out" | head -1 | sed 's/^ERR //')"
    fi
  done

  # 登记里的目标文件必须在；顺带跑一次干跑（不改盘）
  if bash "$MUTATE" --all --dry-run >"$TMP/dry.log" 2>&1; then
    ok "全量干跑通过（登记的目标文件都在）"
  else
    bad "全量干跑失败"; grep 'FAIL' "$TMP/dry.log" | head -5 | detail
  fi
fi

# ---------------------------------------------------------------------------
# 5. 变异器自检：假卡上验四种行为
#   造一份「严格 check」和一份「永远绿的 check」，同一个变异分别跑一遍：
#   前者必须报抓住，后者必须报空转，两次跑完目标文件都必须还原成原样。
# ---------------------------------------------------------------------------
step "5. 变异器自检（抓住 / 空转 / 还原 / 基线红）"

SD="$TMP/self"
mkdir -p "$SD"
printf 'good\n' > "$SD/target.txt"

cat > "$SD/strict.sh" <<EOS
#!/usr/bin/env bash
if grep -qx 'good' "$SD/target.txt"; then
  printf '  ok   target 仍是 good\n'
  printf '\n核对：1 项通过，0 项未通过\n'
  exit 0
fi
printf '  FAIL target 被改坏了\n'
printf '\n核对：0 项通过，1 项未通过\n'
exit 1
EOS

cat > "$SD/lazy.sh" <<EOS
#!/usr/bin/env bash
printf '  ok   什么都没查\n'
printf '\n核对：1 项通过，0 项未通过\n'
exit 0
EOS

cat > "$SD/broken.sh" <<EOS
#!/usr/bin/env bash
printf '  FAIL 本来就红\n'
printf '\n核对：0 项通过，1 项未通过\n'
exit 1
EOS

mkreg() {
  cat > "$1" <<EOS
{
  "card": "自检",
  "check": "$2",
  "mutations": [
    { "id": "把 good 改成 bad", "file": "$SD/target.txt", "kind": "text-replace",
      "find": "good", "replace": "bad", "expect": "该卡 check 应变红" }
  ]
}
EOS
}

mkreg "$SD/reg_grab.json" "$SD/strict.sh"
mkreg "$SD/reg_escape.json" "$SD/lazy.sh"
mkreg "$SD/reg_broken.json" "$SD/broken.sh"

# 5.1 断言活着 → 抓住
if bash "$MUTATE" --registry "$SD/reg_grab.json" >"$TMP/self_grab.log" 2>&1; then
  if grep -q '抓住' "$TMP/self_grab.log"; then
    ok "自检：断言活着时报「抓住」"
  else
    bad "自检：退出码 0 但没说抓住"; tail -6 "$TMP/self_grab.log" | detail
  fi
else
  bad "自检：断言活着却报失败"; tail -8 "$TMP/self_grab.log" | detail
fi

# 5.2 断言空转 → 必须报空转且退出非 0
bash "$MUTATE" --registry "$SD/reg_escape.json" >"$TMP/self_escape.log" 2>&1
esc_code=$?
if [ "$esc_code" -ne 0 ] && grep -q '空转' "$TMP/self_escape.log"; then
  ok "自检：断言空转时报「空转」且非 0 退出"
else
  bad "自检：空转没被报出来（退出码 ${esc_code}）"; tail -6 "$TMP/self_escape.log" | detail
fi

# 5.3 两次跑完，目标文件必须原样（还原失败 = 把仓库弄脏）
if grep -qx 'good' "$SD/target.txt"; then
  ok "自检：变异跑完文件还原干净"
else
  bad "自检：文件没还原（现在是「$(head -1 "$SD/target.txt")」）"
fi

# 5.4 基线本来就红 → 不许拿它当证据
bash "$MUTATE" --registry "$SD/reg_broken.json" >"$TMP/self_broken.log" 2>&1
if grep -q '基线本来就红' "$TMP/self_broken.log"; then
  ok "自检：基线红时拒绝给结论"
else
  bad "自检：基线红没有被挡住"; tail -6 "$TMP/self_broken.log" | detail
fi

# ---------------------------------------------------------------------------
# 6. 护栏挂载
# ---------------------------------------------------------------------------
step "6. 护栏挂载"

assert_file_contains tools/check/preflight.sh '闸门 6' "preflight 有第 6 道闸"
assert_file_contains tools/check/preflight.sh '不可逆点清单' "第 6 道闸指向清单"

assert_file_contains tools/check/check-all.sh 'd15)' "check-all 的 deps_of 有 d15"
assert_file_contains tools/check/check-all.sh ' d15 ' "check-all 的 ALL_CARDS 有 d15"
assert_file_contains tools/check/ci.sh 'd15' "ci.sh 的 CHECKS 有 d15"

assert_file_contains docs/开发架构.md '### 14\.10 ' "开发架构有 14.10 节"
assert_file_contains docs/任务队列.md 'D15.*不可逆点' "任务队列有 D15 行"

# 一行 ok/FAIL 都没出就不算通过（整段崩掉时会「0 通过 0 失败」）
[ $((PASS + FAIL)) -gt 0 ] || bad "静态段没跑出任何结果"

finish "D15（不可逆点清单 + 变异验证）"
