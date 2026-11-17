#!/usr/bin/env python3
"""D16 契约闸：proto 生成物清单 + 队列风险列 + 卡片契约模板。

用法：
    python3 tools/check/check-contract.py              # 无库段核对（CI 跑这一条）
    python3 tools/check/check-contract.py --self-test  # 另外验四类坏法都会被抓住
    python3 tools/check/check-contract.py --root DIR    # 对指定根目录核对（自检用）

核对（开发架构 14.11.3 的 D16）：
1. proto 清单覆盖 proto/ 全部、清单没有多余条目；
2. 改了 proto 没重出生成物 → 红（输入 sha256 与清单对不上）；
3. 手改生成物 → 红（输出 sha256 与清单对不上）；
4. 两端该同则同：服务端与客户端同名 Xxx.cs 逐字节一致，客户端没有 XxxGrpc.cs；
5. 队列每一行都有风险标记，图例在开发架构 14.11.1 与队列表下各一份；
6. 卡片契约模板六栏与两个真实示例还在。

本脚本只读；写清单是 tools/gen/gen-proto-manifest.py 的事。
"""
import argparse
import importlib.util
import json
import os
import re
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_ROOT = os.path.abspath(os.path.join(HERE, "../.."))

RISK_RE = re.compile(r"^(—|[1-4](/[1-4])*)$")
HEADING_RE = re.compile(r"^## 主队列（(\d+) 张）")

# 清单问题的类别 → 核对项标题（顺序即输出顺序）。
MANIFEST_LABELS = (
    ("coverage", "清单覆盖 proto/ 与两端生成物"),
    ("input", "proto 输入与清单一致（改了 proto 必须重出）"),
    ("output", "生成物与清单一致（手改生成物会红）"),
    ("pair", "两端该同则同（客户端没有 gRPC 生成物）"),
    ("version", "protoc / 生成器版本与清单一致"),
)

# 卡片契约模板必须有的栏目。
CONTRACT_LABELS = (
    "卡号：",
    "风险标记：",
    "本卡只做：",
    "不做：",
    "允许改：",
    "冻结（一项都不许动）：",
    "状态与拒绝码：",
    "不变量：",
    "证据：",
    "回滚点：",
)


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def load_manifest_module(root):
    path = os.path.join(root, "tools/gen/gen-proto-manifest.py")
    spec = importlib.util.spec_from_file_location("gen_proto_manifest", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("导入不了 {}".format(path))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def row(ok, label, detail="", kind=""):
    return {"ok": ok, "label": label, "detail": detail, "kind": kind}


# ---------------------------------------------------------------------------
# 1. proto 清单与生成物
# ---------------------------------------------------------------------------
def manifest_rows(root):
    gm = load_manifest_module(root)
    data, errs = gm.load(root)
    if data is None:
        # 清单本身读不了：五项都别报绿，直接说清缺什么。
        return [row(False, "proto/manifest.json 可读（六份 proto 的输入 / 输出都靠它）", "\n".join(errs), "coverage")]
    problems = gm.check(root, data)
    by_kind = {}
    for kind, msg in problems:
        by_kind.setdefault(kind, []).append(msg)
    rows = []
    for kind, label in MANIFEST_LABELS:
        msgs = by_kind.get(kind) or []
        detail = "\n".join(msgs[:6])
        if len(msgs) > 6:
            detail += "\n… 另有 {} 条".format(len(msgs) - 6)
        rows.append(row(not msgs, label, detail, kind))
    # 清单条数（给标题补一个可读数字）
    n_proto = len(gm.proto_files(root))
    n_out = 0
    for item in data.get("protos") or []:
        outs = item.get("outputs") or {}
        n_out += len(outs.get("server") or []) + len(outs.get("client") or [])
    rows[0]["label"] = "清单覆盖 proto/ 与两端生成物（{} 份 proto / {} 个文件）".format(n_proto, n_out)
    return rows


# ---------------------------------------------------------------------------
# 2. 队列风险列与图例
# ---------------------------------------------------------------------------
def split_queue(root):
    """返回 (声明张数, 数据行列表, 表后文字)。行是去掉 `|` 后的字段列表。"""
    text = read(os.path.join(root, "docs/任务队列.md"))
    lines = text.splitlines()
    start = None
    declared = None
    for i, line in enumerate(lines):
        m = HEADING_RE.match(line)
        if m:
            start = i + 1
            declared = int(m.group(1))
            break
    if start is None:
        return None, [], ""
    rows = []
    last_row = start - 1
    table_end = len(lines)
    for i in range(start, len(lines)):
        line = lines[i]
        if line.startswith("## "):
            table_end = i
            break
        if not line.startswith("|"):
            continue
        if "序号" in line or "---" in line:
            continue
        rows.append([cell.strip() for cell in line.split("|")])
        last_row = i
    # 图例在表格最后一行之后、下一个二级标题之前。
    return declared, rows, "\n".join(lines[last_row + 1:table_end])


def queue_rows(root):
    declared, rows, after = split_queue(root)
    rows_out = []
    if declared is None:
        return [row(False, "队列表可读（找得到「## 主队列（N 张）」）", "docs/任务队列.md 里没有主队列表头", "queue")]

    bad_cells = []
    bad_risk = []
    for idx, cells in enumerate(rows, 1):
        if len(cells) != 10:
            bad_cells.append("第 {} 行有 {} 个单元格（应 10 个：序号 / 阶段 / 卡 / 标题 / 依赖 / 状态 / 风险 / 详情）".format(idx, len(cells)))
            continue
        card = cells[3]
        risk = cells[7]
        if not RISK_RE.match(risk):
            bad_risk.append("{}（第 {} 行）风险标记是「{}」".format(card or "?", idx, risk or "空"))
    detail = "\n".join((bad_cells + bad_risk)[:6])
    rows_out.append(
        row(
            not bad_cells and not bad_risk and len(rows) == declared,
            "队列每行都有风险标记（{} 行，声明 {} 行）".format(len(rows), declared),
            detail if (bad_cells or bad_risk) or len(rows) != declared else "",
            "queue",
        )
    )
    if len(rows) != declared:
        rows_out[-1]["detail"] = (rows_out[-1]["detail"] + "\n" if rows_out[-1]["detail"] else "") + \
            "实际 {} 行，表头声明 {} 行".format(len(rows), declared)

    legend_ok = all(token in after for token in ("风险列", "不可逆点清单", "卡片契约", "`—`", "`4`"))
    rows_out.append(
        row(legend_ok, "风险图例在队列表下（含 — 与 1–4 的解释）", "" if legend_ok else "队列表后找不到「风险列」图例段", "queue")
    )

    arch = read(os.path.join(root, "docs/开发架构.md"))
    m = re.search(r"^#### 14\.11\.1 风险标记$", arch, re.M)
    legend_arch = False
    if m:
        tail = arch[m.end():]
        nxt = re.search(r"^(####|###|##) ", tail, re.M)
        section = tail[: nxt.start()] if nxt else tail
        marks = ("| `—` |", "| `1` |", "| `2` |", "| `3` |", "| `4` |", "| 组合 |")
        legend_arch = all(mark in section for mark in marks)
    rows_out.append(
        row(legend_arch, "风险图例在开发架构 14.11.1", "" if legend_arch else "14.11.1 里缺「—」或 1–4 或「组合」行", "queue")
    )
    return rows_out


# ---------------------------------------------------------------------------
# 3. 卡片契约模板
# ---------------------------------------------------------------------------
def contract_rows(root):
    path = os.path.join(root, "docs/卡片契约.md")
    if not os.path.isfile(path):
        return [row(False, "卡片契约模板齐（六栏 + 两个真实示例）", "docs/卡片契约.md 不存在", "queue")]
    text = read(path)
    missing = [label for label in CONTRACT_LABELS if label not in text]
    examples = [name for name in ("卡号：D8", "卡号：P1-5a") if name not in text]
    usage = "## 使用口径" in text
    detail = ""
    if missing:
        detail += "模板缺栏：" + " / ".join(missing)
    if examples:
        detail += ("\n" if detail else "") + "示例缺：" + " / ".join(examples)
    if not usage:
        detail += ("\n" if detail else "") + "缺「使用口径」段"
    return [row(not missing and not examples and usage, "卡片契约模板齐（六栏 + 两个真实示例）", detail, "queue")]


def collect(root):
    rows = manifest_rows(root)
    rows += queue_rows(root)
    rows += contract_rows(root)
    return rows


def report(rows):
    for r in rows:
        print(("  ok   " if r["ok"] else "  FAIL ") + r["label"])
        if r["detail"]:
            for line in r["detail"].splitlines():
                print("       " + line)
    passed = sum(1 for r in rows if r["ok"])
    failed = len(rows) - passed
    print("核对：{} 项通过，{} 项未通过".format(passed, failed))
    return 0 if failed == 0 else 1


# ---------------------------------------------------------------------------
# 4. 自检：四类坏法必须被抓住（基线先全绿，改坏一处再跑，跑完临时目录丢掉）
# ---------------------------------------------------------------------------
FIXTURES = (
    "proto",
    "src/Shared/Generated",
    "client/MMOGame/Assets/Proto/Generated",
    "tools/gen/gen-proto.sh",
    "tools/gen/gen-proto-manifest.py",
    "docs/任务队列.md",
    "docs/开发架构.md",
    "docs/卡片契约.md",
)


def copy_fixture(root, dst):
    for rel in FIXTURES:
        src = os.path.join(root, rel)
        out = os.path.join(dst, rel)
        if os.path.isdir(src):
            shutil.copytree(src, out)
        else:
            os.makedirs(os.path.dirname(out), exist_ok=True)
            shutil.copy2(src, out)


def mutate_proto(root):
    path = os.path.join(root, "proto/mmo/common.proto")
    with open(path, "a", encoding="utf-8") as f:
        f.write("\n// D16 自检：改输入不重出\n")


def mutate_output(root):
    path = os.path.join(root, "src/Shared/Generated/Common.cs")
    with open(path, "a", encoding="utf-8") as f:
        f.write("\n// D16 自检：手改生成物\n")


def mutate_drop_manifest(root):
    path = os.path.join(root, "proto/manifest.json")
    data = json.load(open(path, encoding="utf-8"))
    data["protos"] = data["protos"][:-1]
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write("\n")


def mutate_client(root):
    path = os.path.join(root, "client/MMOGame/Assets/Proto/Generated/Common.cs")
    with open(path, "a", encoding="utf-8") as f:
        f.write("\n// D16 自检：客户端与服务器分叉\n")


def mutate_queue(root):
    path = os.path.join(root, "docs/任务队列.md")
    lines = read(path).splitlines()
    for i, line in enumerate(lines):
        if HEADING_RE.match(line):
            start = i
            break
    else:
        return
    for i in range(start, len(lines)):
        line = lines[i]
        if line.startswith("## ") and i > start:
            break
        if line.startswith("|") and "序号" not in line and "---" not in line:
            cells = line.split("|")
            cells[7] = "  "
            lines[i] = "|".join(cells)
            break
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def self_test(root):
    cases = (
        ("基线：清单与队列全绿", None, "ok"),
        ("改 proto 不重出 → input 红", mutate_proto, "input"),
        ("手改生成物 → output 红", mutate_output, "output"),
        ("清单漏一条 → coverage 红", mutate_drop_manifest, "coverage"),
        ("两端分叉 → pair 红", mutate_client, "pair"),
        ("风险列缺一格 → queue 红", mutate_queue, "queue"),
    )
    rows = []
    for label, mutate, expect in cases:
        tmp = tempfile.mkdtemp(prefix="d16-selftest-")
        try:
            copy_fixture(root, tmp)
            if mutate:
                mutate(tmp)
            got = collect(tmp)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        fails = [r for r in got if not r["ok"]]
        if expect == "ok":
            ok = not fails
            detail = "" if ok else "基线就红：" + " / ".join(r["label"] for r in fails)
        else:
            hit = [r for r in fails if r["kind"] == expect]
            ok = bool(hit)
            detail = "" if ok else "没抓住，红的是：" + (" / ".join("{}[{}]".format(r["label"], r["kind"]) for r in fails) or "无")
        rows.append(row(ok, label, detail, "selftest"))
    return rows


def main(argv):
    ap = argparse.ArgumentParser(description="D16 契约闸")
    ap.add_argument("--root", default="")
    ap.add_argument("--self-test", action="store_true", dest="self_test")
    args = ap.parse_args(argv)
    root = os.path.abspath(args.root or DEFAULT_ROOT)

    print("== 1. proto 清单与生成物 ==")
    rows = manifest_rows(root)
    print("== 2. 队列风险列与图例 ==")
    rows += queue_rows(root) + contract_rows(root)
    if args.self_test:
        print("== 3. 自检（坏法必须被抓住）==")
        rows += self_test(root)
    return report(rows)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
