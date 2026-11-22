#!/usr/bin/env bash
# tools/check/mutate.sh —— D15 变异验证：把实现改坏一处，看对应卡的断言会不会红。
#
# 要回答的问题只有一个：**那条断言是活的，还是空转的？**
# 断言只钉「某字段 / 某入口在」，实现里把它删掉、把判断反过来、把数字改错，
# 断言照样全绿 —— 这就是空转。空转的断言比没有断言更危险：它给假的信心。
#
# 做法（每条变异）：
#   1. 备份目标文件（md5 记下来）；
#   2. 按登记把一处实现改坏（json-set 改字段，text-replace 替换唯一子串）；
#   3. 跑该卡的 check 脚本；退出码非 0 记「抓住」，为 0 记「空转」；
#   4. 还原文件，再算一次 md5，必须与备份一致（不一致直接红，别把仓库弄脏）。
#
# 登记在 tools/check/mutations/<卡号>.json：
#   {
#     "card": "d8",
#     "check": "check-d8.sh",              // 可省，默认 check-<卡号>.sh
#     "mutations": [
#       { "id": "版本号跳号", "file": "db/migrations.json",
#         "kind": "text-replace", "find": "...", "replace": "...",
#         "expect": "check-d8.sh 应报版本不连续" }
#     ]
#   }
#
# 只登记「改坏了必须有人发现」的地方，优先挑 docs/不可逆点清单.md 的四类。
# 不进 ci.sh：跑一次要编译 + 起进程，几分钟。按阶段闸门 / 每 5 张卡手工跑。
#
# 用法：
#   bash tools/check/mutate.sh --card d8            # 跑一张卡登记的变异
#   bash tools/check/mutate.sh --all                # 所有已登记的卡
#   bash tools/check/mutate.sh --all --dry-run      # 只验登记格式与目标文件，不动盘
#   bash tools/check/mutate.sh --card d8 --no-baseline   # 跳过基线（卡本来就红时用）
#   bash tools/check/mutate.sh --list               # 列出已登记的卡
#
# 退出码：0=每条变异都被抓住且仓库干净；1=有空转 / 还原失败 / 登记格式错。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
source "$ROOT/tools/check/lib-common.sh"

MUT_DIR="$ROOT/tools/check/mutations"
TMP="$(mktemp -d)"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

# 还原之后必须强制重编：dotnet 的增量构建按输入哈希判定，源码还原成字节相同的内容后
# 它会认定「产物已是最新」，而 bin 里躺着的仍是变异版 dll。不重编，下一张卡跑核对会
# 读到被改坏的二进制——这是 Q7 实际踩到的（状态机自检在源码已还原的情况下仍红）。
force_rebuild() {
  local f="$1" dir name
  case "$f" in
    src/*/*.cs) dir="src/$(printf '%s' "$f" | cut -d/ -f2)" ;;
    *) return 0 ;;
  esac
  name="$(basename "$dir")"
  [ -f "$ROOT/$dir/$name.csproj" ] || return 0
  [ -n "$DOTNET" ] || return 0
  "$DOTNET" build "$ROOT/$dir/$name.csproj" -c Release -t:Rebuild --nologo -v q \
    >"$TMP/rebuild-$name.log" 2>&1 || {
      printf '       （%s 还原后强制重编失败，见 %s）\n' "$dir" "$TMP/rebuild-$name.log"
    }
}

RESTORE_FILE=""
RESTORE_BAK=""
cleanup() {
  if [ -n "$RESTORE_FILE" ] && [ -f "$RESTORE_BAK" ]; then
    cp "$RESTORE_BAK" "$RESTORE_FILE" 2>/dev/null || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT INT TERM

CARD=""
REGISTRY=""
ALL=0
DRY=0
LIST=0
BASELINE=1

usage() { sed -n '2,45p' "$0" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --card) CARD="${2:-}"; shift 2 ;;
    --registry) REGISTRY="${2:-}"; shift 2 ;;
    --all) ALL=1; shift ;;
    --dry-run) DRY=1; shift ;;
    --list) LIST=1; shift ;;
    --no-baseline) BASELINE=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) printf '未知参数：%s\n' "$1"; exit 2 ;;
  esac
done

file_md5() {
  python3 -c 'import hashlib,sys
print(hashlib.md5(open(sys.argv[1],"rb").read()).hexdigest())' "$1"
}

# 登记里的路径可以写相对于仓库的（db/migrations.json），也可以写绝对路径
# （自检时在临时目录造假卡用，不碰真仓库）。
# 绝对路径原样返回；Windows 盘符路径（C:/…）也算绝对——自检段用的临时目录常在 C:，
# 只认 "/*" 的话会被拼成 "$ROOT/C:/…" 这种不存在的路径。
abs_of() {
  case "$1" in
    /*|[A-Za-z]:/*) printf '%s' "$1" ;;
    *)  printf '%s/%s' "$ROOT" "$1" ;;
  esac
}

# 登记文件里的 list / apply 两个子命令。参数：<命令> <登记文件> [序号]
reg_py() {
  python3 - "$@" <<'PY'
import json, sys

cmd = sys.argv[1]
path = sys.argv[2]
try:
    doc = json.load(open(path, encoding='utf-8'))
except Exception as e:
    print('ERR 登记文件读不了：%s' % e)
    sys.exit(3)

items = doc.get('mutations') or []
if cmd == 'meta':
    print(doc.get('card', ''))
    print(doc.get('check', ''))
    print(len(items))
    sys.exit(0)

if cmd == 'list':
    for i, m in enumerate(items):
        miss = [k for k in ('id', 'file', 'kind', 'expect') if not m.get(k)]
        if miss:
            print('ERR %s 缺字段 %s' % (i, ','.join(miss)))
            sys.exit(4)
        print('\t'.join([str(i), m['id'], m['file'], m['kind'], m['expect']]))
    sys.exit(0)

if cmd == 'apply':
    idx = int(sys.argv[3])
    m = items[idx]
    f = m['file']
    try:
        text = open(f, encoding='utf-8').read()
    except Exception as e:
        print('ERR 目标文件读不了：%s' % e)
        sys.exit(3)
    if m['kind'] == 'text-replace':
        hit = text.count(m['find'])
        if hit != 1:
            print('ERR find 命中 %d 处（要求恰好 1 处）：%s' % (hit, m['find'][:60]))
            sys.exit(5)
        open(f, 'w', encoding='utf-8').write(text.replace(m['find'], m['replace']))
    elif m['kind'] == 'json-set':
        data = json.loads(text)
        keys = m['path'].split('.')
        node = data
        for k in keys[:-1]:
            node = node[int(k)] if isinstance(node, list) else node[k]
        last = keys[-1]
        if isinstance(node, list):
            node[int(last)] = m['value']
        else:
            node[last] = m['value']
        out = json.dumps(data, ensure_ascii=False, indent=2)
        if text.endswith('\n'):
            out += '\n'
        open(f, 'w', encoding='utf-8').write(out)
    else:
        print('ERR 未知 kind：%s' % m['kind'])
        sys.exit(6)
    print('ok')
    sys.exit(0)

print('ERR 未知命令：%s' % cmd)
sys.exit(2)
PY
}

regs=()
if [ -n "$REGISTRY" ]; then
  regs=("$REGISTRY")
elif [ -n "$CARD" ]; then
  regs=("$MUT_DIR/${CARD}.json")
elif [ "$ALL" = "1" ] || [ "$LIST" = "1" ]; then
  for f in "$MUT_DIR"/*.json; do
    [ -f "$f" ] && regs+=("$f")
  done
else
  usage
  exit 2
fi

if [ ${#regs[@]} -eq 0 ]; then
  printf '  ..   没有已登记的变异（%s 为空）\n' "$MUT_DIR"
  finish "D15（变异验证）"
fi

if [ "$LIST" = "1" ]; then
  printf '已登记变异的卡：\n'
  for f in "${regs[@]}"; do
    printf '  %-10s %s\n' "$(basename "$f" .json)" "$f"
  done
  exit 0
fi

GRABBED=0
ESCAPED=0
BROKEN=0

for reg in "${regs[@]}"; do
  name="$(basename "$reg" .json)"
  printf '\n== %s ==\n' "$name"

  if [ ! -f "$reg" ]; then
    bad "登记文件不存在：$reg"
    BROKEN=$((BROKEN + 1))
    continue
  fi

  meta="$(reg_py meta "$reg" 2>&1)"
  if printf '%s' "$meta" | grep -q '^ERR'; then
    bad "登记格式错：$(printf '%s' "$meta" | head -1)"
    BROKEN=$((BROKEN + 1))
    continue
  fi

  reg_card="$(printf '%s\n' "$meta" | sed -n '1p')"
  reg_check="$(printf '%s\n' "$meta" | sed -n '2p')"
  reg_count="$(printf '%s\n' "$meta" | sed -n '3p')"
  [ -n "$reg_check" ] || reg_check="check-${reg_card}.sh"
  case "$reg_check" in
    /*|[A-Za-z]:/*) check_path="$reg_check" ;;
    *)  check_path="$ROOT/tools/check/$reg_check" ;;
  esac

  if [ ! -f "$check_path" ]; then
    bad "登记指向的核对脚本不存在：$check_path"
    BROKEN=$((BROKEN + 1))
    continue
  fi

  listing="$(reg_py list "$reg" 2>&1)"
  if printf '%s' "$listing" | grep -q '^ERR'; then
    bad "登记格式错：$(printf '%s' "$listing" | grep '^ERR' | head -1)"
    BROKEN=$((BROKEN + 1))
    continue
  fi

  if [ "$DRY" = "1" ]; then
    n=0
    while IFS="$(printf '\t')" read -r idx mid mfile mkind mexpect; do
      [ -n "$idx" ] || continue
      n=$((n + 1))
      if [ ! -f "$(abs_of "$mfile")" ]; then
        bad "变异「${mid}」的目标文件不存在：$mfile"
        BROKEN=$((BROKEN + 1))
      else
        ok "变异「${mid}」登记合法（${mkind} → ${mfile}）"
      fi
    done <<EOF
$listing
EOF
    continue
  fi

  # 基线：卡本来就红的时候，变异后照样红，结论是假的。先确认基线绿。
  if [ "$BASELINE" = "1" ]; then
    if bash "$check_path" >"$TMP/${name}.base.log" 2>&1; then
      ok "基线绿（$reg_check 先跑一遍通过）"
    else
      bad "基线本来就红，变异结论无意义 —— 先修好 $reg_check 再跑"
      tail -8 "$TMP/${name}.base.log" | detail
      BROKEN=$((BROKEN + 1))
      continue
    fi
  fi

  while IFS="$(printf '\t')" read -r idx mid mfile mkind mexpect; do
    [ -n "$idx" ] || continue
    src="$(abs_of "$mfile")"
    if [ ! -f "$src" ]; then
      bad "变异「${mid}」目标文件不存在：$mfile"
      BROKEN=$((BROKEN + 1))
      continue
    fi

    RESTORE_FILE="$src"
    RESTORE_BAK="$TMP/$(printf '%s' "$mfile" | tr '/' '_').bak"
    cp "$src" "$RESTORE_BAK" || { bad "备份失败：$mfile"; RESTORE_FILE=""; BROKEN=$((BROKEN + 1)); continue; }
    before="$(file_md5 "$src")"

    applied="$(reg_py apply "$reg" "$idx" 2>&1)"
    if ! printf '%s' "$applied" | grep -q '^ok$'; then
      bad "变异「${mid}」没应用上：$(printf '%s' "$applied" | head -1)"
      cp "$RESTORE_BAK" "$src"
      RESTORE_FILE=""
      BROKEN=$((BROKEN + 1))
      continue
    fi

    if bash "$check_path" >"$TMP/${name}.${idx}.log" 2>&1; then
      code=0
    else
      code=$?
    fi

    if [ "$code" -ne 0 ]; then
      if grep -qE '(error|错误) [A-Z]{2,4}[0-9]{3,4}' "$TMP/${name}.${idx}.log" \
         && ! grep -q '^  FAIL' "$TMP/${name}.${idx}.log"; then
        bad "变异「${mid}」只把编译弄坏了，没验到断言（${mkind}）"
        BROKEN=$((BROKEN + 1))
      else
        first_fail="$(grep '^  FAIL' "$TMP/${name}.${idx}.log" | head -1 | sed 's/^  FAIL //')"
        ok "抓住「${mid}」→ ${first_fail:-退出码 $code}"
        GRABBED=$((GRABBED + 1))
      fi
    else
      bad "空转「${mid}」：改坏了 ${mfile}，${reg_check} 仍然全绿（期望：${mexpect}）"
      ESCAPED=$((ESCAPED + 1))
    fi

    cp "$RESTORE_BAK" "$src"
    force_rebuild "$mfile"
    after="$(file_md5 "$src")"
    if [ "$before" != "$after" ]; then
      bad "还原失败：$mfile 的 md5 与变异前不一致"
      BROKEN=$((BROKEN + 1))
    fi
    RESTORE_FILE=""
  done <<EOF
$listing
EOF
done

printf '\n抓住 %d 条，空转 %d 条，异常 %d 条\n' "$GRABBED" "$ESCAPED" "$BROKEN"
[ $((PASS + FAIL)) -gt 0 ] || bad "一条变异都没跑成（登记表或路径有问题）"
finish "D15（变异验证）"
