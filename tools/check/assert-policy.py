#!/usr/bin/env python3
"""D5 断言政策扫描：把「写死当前事实」的三类写法在提交前拦下来。

用法：
    python3 tools/check/assert-policy.py                  # 扫 tools/check/check-*.sh
    python3 tools/check/assert-policy.py --files a.sh b.sh
    python3 tools/check/assert-policy.py --quiet          # 只给退出码

规则（命中即退出码 1；preflight.sh / check-d5.sh / 本文件自身不扫）：
  1. wording      —— 非注释行出现「已接 / 未接 / 没接 / 尚未接 / 还没接」。
                     断言要写「入口在不在」，不写「接到哪一步」；发货状态写
                     delivery.mode=none，不写「没接发货」。
  2. delivered    —— delivered_kinds 直接和数字比较（delivered_kinds=5 / == 5）。
                     种类数会随卡增长，只写下限或从配置现取。
  3. dir_diff     —— git diff 的路径参数是顶层目录（config / proto / src / client /
                     db / tools / docs）。后一张卡改配置或 proto 是常态，老卡只钉
                     自己承诺不动的那几个具体文件，不钉整目录。

这个文件只读，不改任何东西。check-d5.sh 与 preflight.sh 第 5 道闸共用它。
"""
import argparse
import glob
import os
import re
import sys

RULES = ("wording", "delivered", "dir_diff")
SKIP_NAMES = {"preflight.sh", "check-d5.sh"}

WORDING = re.compile(r"已接|未接|没接|尚未接|还没接")
DELIVERED = re.compile(r"delivered_kinds\s*(?:=|==|-eq)\s*[\"']?[0-9]")
TOP_DIRS = {"config", "proto", "src", "client", "db", "tools", "docs"}
GIT_DIFF = re.compile(r"\bgit\b[^\n]*\bdiff\b[^\n]*\s--(?:\s|$)")


def code_part(line):
    """去掉行尾 `# 注释`，但保留字符串里的 #（如 '订单 #3'）。"""
    out = []
    quote = ""
    for i, ch in enumerate(line):
        if quote:
            out.append(ch)
            if ch == quote:
                quote = ""
            continue
        if ch in "\"'":
            quote = ch
            out.append(ch)
        elif ch == "#" and (i == 0 or line[i - 1].isspace()):
            break
        else:
            out.append(ch)
    return "".join(out)


def scan_lines(path):
    """返回 [(行号, 规则, 说明)]。"""
    hits = []
    try:
        lines = open(path, encoding="utf-8").read().splitlines()
    except OSError as exc:
        return [(0, "read", str(exc))]
    for no, raw in enumerate(lines, 1):
        line = code_part(raw)
        text = line.strip()
        if not text:
            continue
        if WORDING.search(line):
            hits.append((no, "wording", WORDING.search(line).group(0)))
        m = DELIVERED.search(line)
        if m:
            hits.append((no, "delivered", m.group(0).strip()))
        m = GIT_DIFF.search(line)
        if m:
            tail = line[m.end():]
            args = [a.strip("\"'") for a in tail.split() if a.strip("\"'")]
            bad = [a for a in args if a in TOP_DIRS]
            if bad:
                hits.append((no, "dir_diff", " ".join(bad)))
    return hits


def default_files(root):
    files = sorted(glob.glob(os.path.join(root, "tools/check/check-*.sh")))
    return [p for p in files if os.path.basename(p) not in SKIP_NAMES]


def main():
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--root", default="")
    ap.add_argument("--files", nargs="*", default=None)
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args()

    root = args.root or os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
    if args.files:
        files = [f if os.path.isabs(f) else os.path.join(root, f) for f in args.files]
    else:
        files = default_files(root)

    total = 0
    for path in files:
        # Windows 上核对脚本的临时目录常在 C: 而仓库在 E:，跨盘符算不出相对路径
        # （自检段正是拿临时目录里的坏样本扫），退化成绝对路径，只影响输出里的文件名。
        try:
            rel = os.path.relpath(path, root)
        except ValueError:
            rel = path
        for no, rule, info in scan_lines(path):
            total += 1
            if not args.quiet:
                print("HIT %s:%d:%s:%s" % (rel, no, rule, info))
    if total:
        if not args.quiet:
            print("政策扫描：%d 处命中" % total)
        return 1
    if not args.quiet:
        print("OK 政策扫描：%d 份脚本，0 处命中" % len(files))
    return 0


if __name__ == "__main__":
    sys.exit(main())
