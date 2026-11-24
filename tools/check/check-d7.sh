#!/usr/bin/env bash
# tools/check/check-d7.sh —— D7 文档单一状态源（开发架构 14.6 顺序 13）。
#
# 完成标准（14.6 的 D7 行）：
#   README / 开发架构不再重复状态；只留开发日志；收尾只改一处。
#
# 本脚本只做静态核对，不编译、不连库、不跑 Unity：
#   1. README 只讲项目是什么：没有「当前进度 / 上一版进度」这类摘要段，进度指到开发日志；
#   2. 开发架构 0 处「vX 已收口」注记，且第 10 节有成文的单一状态源规则；
#   3. 任务队列「当前」不抄摘要（只留指针与机械事实），取卡规则写明唯一写入点；
#   4. 《跑偏与缺失核对表》0 处「已收口」（它是核对快照，不是进度表）；
#   5. 开发日志仍是唯一时间线（顶部有条目、有本卡 D7 条目）；
#   6. 自检：喂一份「三处都写状态」的坏样本，四条规则必须各抓一条。
#
#   bash tools/check/check-d7.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/tools/check/lib-common.sh"

TITLE="D7（文档单一状态源）"
TMP="$(mktemp -d)"; TMP="${TMP//\\//}"
trap 'rm -rf "$TMP"' EXIT

scan_docs() {
  python3 - "$1" <<'PY'
import os
import re
import sys

base = sys.argv[1]

def read(rel):
    path = os.path.join(base, rel)
    return open(path, encoding="utf-8").read() if os.path.exists(path) else ""

readme = read("README.md")
arch = read("docs/开发架构.md")
queue = read("docs/任务队列.md")
check = read("docs/跑偏与缺失核对表.md")
log = read("docs/开发日志.md")

def emit(name, ok, msg=""):
    print(("PASS " if ok else "FAIL ") + name + (("：" + msg) if msg and not ok else ""))

# ---- 1. README 不再抄状态 ----
m = re.search(r"^\*\*(当前进度|上一版进度|更早进度|更早一版)", readme, re.M)
emit("readme_no_digest", m is None and "## 当前做到哪" not in readme,
     "README 里还有进度摘要段「%s」" % (m.group(0) if m else "## 当前做到哪"))
hit = re.search(r"^## 进度\s*\n+([\s\S]{0,600}?)\n##", readme, re.M)
emit("readme_points_to_log",
     hit is not None and "开发日志" in hit.group(1) and "任务队列" in hit.group(1),
     "README 的「进度」段没有同时指向开发日志与任务队列")

# ---- 2. 开发架构 0 处收口注记 ----
bad = re.findall(r"v1\.\d{3}\s*已收口|已收口（v1\.\d{3}|收口记录（v", arch)
emit("arch_no_landed_marks", not bad, "开发架构还有 %d 处收口注记" % len(bad))
emit("arch_single_source_rule",
     "文档单一状态源" in arch and "只写开发日志一处" in arch,
     "第 10 节没有成文的「文档单一状态源」规则")
emit("arch_d7_row", re.search(r"\| 13 \| D7 \| 文档单一状态源", arch) is not None,
     "14.6 排期表的 D7 行不见了")

# ---- 3. 任务队列：当前段不抄摘要，取卡规则只有一个写入点 ----
m = re.search(r"^## 当前\s*\n([\s\S]*?)^## ", queue, re.M)
cur = m.group(1) if m else ""
bad = re.findall(r"刚收口|上一张收口|已完成：|v1\.\d{3}", cur)
emit("queue_current_no_digest", m is not None and not bad,
     "任务队列「当前」段还有 %d 处进度摘要" % len(bad))
emit("queue_rule_single_write", "唯一写进度的地方" in queue,
     "取卡规则没写「唯一写进度的地方」")
pointers = re.findall(r"^(CURRENT|NEXT|STAGE|PLAN_GATE)=\S+$", queue, re.M)
emit("queue_pointers", len(set(pointers)) == 4,
     "四个指针不全：%s" % "/".join(sorted(set(pointers))))
cm = re.search(r"^CURRENT=(\S+)$", queue, re.M)
cur_card = cm.group(1) if cm else ""
emit("queue_current_consistent",
     cur_card == "—"
     or re.search(r"\|\s*" + re.escape(cur_card) + r"\s*\|[^\n]*\|\s*进行中\s*\|", queue) is not None,
     "CURRENT=%s 既不是 —，也不是标进行中的卡" % cur_card)

# ---- 4. 核对表不是进度表 ----
bad = re.findall(r"已收口", check)
emit("checklist_no_close_marks", not bad, "跑偏与缺失核对表还有 %d 处「已收口」" % len(bad))

# ---- 5. 开发日志是唯一时间线 ----
emit("log_is_timeline", re.search(r"^### ", log, re.M) is not None,
     "开发日志没有卡条目")
emit("log_d7_entry", re.search(r"^### D7 ", log, re.M) is not None,
     "开发日志最上面没有 D7 条目")
PY
}

echo "== 1. 文档里只有一处状态源 =="
scan_docs "$ROOT" >"$TMP/docs.txt" 2>&1
while IFS= read -r line; do
  case "$line" in
    PASS\ *) ok "${line#PASS }" ;;
    FAIL\ *) bad "${line#FAIL }" ;;
    *) printf '%s\n' "$line" | detail ;;
  esac
done < "$TMP/docs.txt"

echo "== 2. 自检（三处都写状态的坏样本必须被抓住） =="
mkdir -p "$TMP/bad/docs"
printf '%s\n' '# 坏样本' '' '**当前进度 · v9.999**：某卡已收口。' '' '## 进度' '' '只看别处。' > "$TMP/bad/README.md"
printf '%s\n' '# 开发架构' '| 1 | D1 | 某事 | 无 | 做完即止（v1.999 已收口：一堆细节） |' > "$TMP/bad/docs/开发架构.md"
printf '%s\n' '# 任务队列' 'CURRENT=—' 'NEXT=D1' 'STAGE=1' 'PLAN_GATE=no' '' \
  '## 当前' '' '- **刚刚收口：D1（v1.999）**' '' '## Agent 取卡规则' > "$TMP/bad/docs/任务队列.md"
printf '%s\n' '| D8 | 没有迁移版本表 | 已收口 |' > "$TMP/bad/docs/跑偏与缺失核对表.md"
printf '%s\n' '# 开发日志' '' '### D7 文档单一状态源 · 2026-01-01' > "$TMP/bad/docs/开发日志.md"
scan_docs "$TMP/bad" >"$TMP/self.txt" 2>&1
caught=$(grep -c '^FAIL ' "$TMP/self.txt" || true)
if grep -q '^FAIL readme_no_digest' "$TMP/self.txt" \
   && grep -q '^FAIL arch_no_landed_marks' "$TMP/self.txt" \
   && grep -q '^FAIL queue_current_no_digest' "$TMP/self.txt" \
   && grep -q '^FAIL checklist_no_close_marks' "$TMP/self.txt"; then
  ok "自检：四条规则各抓一条（${caught} 处）"
else
  bad "自检：规则没全抓到（${caught} 处）"
  detail < "$TMP/self.txt"
fi

echo "== 3. 闸门挂载 =="
assert_interface tools/check/ci.sh 'd7' "ci.sh 的 CHECKS 挂了 d7"
assert_interface tools/check/check-all.sh ' d7[ "]' "check-all 的 ALL_CARDS 挂了 d7"
assert_interface docs/任务队列.md 'check-d7\.sh' "任务队列的取卡规则指向本脚本"

finish "$TITLE"
