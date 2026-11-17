#!/usr/bin/env python3
"""proto 生成物清单：把「输入 proto」与「两端生成物」钉成可核对的哈希。

用法：
    python3 tools/gen/gen-proto-manifest.py                    # 重写 proto/manifest.json
    python3 tools/gen/gen-proto-manifest.py --protoc-version "libprotoc 35.1"
    python3 tools/gen/gen-proto-manifest.py --check            # 只核对，不写文件
    python3 tools/gen/gen-proto-manifest.py --check --quiet    # 只给退出码

它回答一件事：仓库里提交的生成物，还是不是这份 proto 生成出来的？
- 改了 proto 没重出：清单里的输入 sha256 与当前文件对不上 → check 报 input。
- 改了生成物没改 proto：清单里的输出 sha256 与当前文件对不上 → check 报 output。
- 两端该同则同：同一份 proto 在服务端与客户端的 Xxx.cs 必须逐字节一致 → check 报 pair。
- 生成器换版本：清单记的 Grpc.Tools 版本与 tools/gen/gen-proto.sh 里的常量对不上 → check 报 version。

清单只由 tools/gen/gen-proto.sh 在生成之后调用本脚本写入；不重跑 protoc、不改生成逻辑。
本脚本只读 proto/ 与两端生成物、写 proto/manifest.json。
"""
import argparse
import hashlib
import json
import os
import re
import sys

SCHEMA = "proto-manifest/1"
MANIFEST = "proto/manifest.json"
GEN_SH = "tools/gen/gen-proto.sh"
TARGETS = (
    ("server", "src/Shared/Generated"),
    ("client", "client/MMOGame/Assets/Proto/Generated"),
)
PROTO_DIR = "proto"

SERVICE_RE = re.compile(r"^\s*service\s+[A-Za-z_]", re.M)
VERSION_RE = re.compile(r'^GRPC_TOOLS_VERSION="([^"]+)"', re.M)


def repo_root():
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))


def rel(root, path):
    return os.path.relpath(path, root).replace(os.sep, "/")


def sha256_bytes(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def read_text(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def proto_files(root):
    """proto/ 下所有 .proto，按路径排序。"""
    found = []
    base = os.path.join(root, PROTO_DIR)
    if not os.path.isdir(base):
        return found
    for dirpath, _dirs, files in os.walk(base):
        for name in files:
            if name.endswith(".proto"):
                found.append(rel(root, os.path.join(dirpath, name)))
    return sorted(found)


def type_name(proto_rel):
    """battle.proto → Battle：与 protoc 的 C# 文件名规则一致（下划线分段首字母大写）。"""
    stem = os.path.splitext(os.path.basename(proto_rel))[0]
    return "".join(part[:1].upper() + part[1:] for part in re.split(r"[_\-\s]+", stem) if part)


def service_count(text):
    return len(SERVICE_RE.findall(text))


def expected_outputs(proto_rel, services):
    """两端应有的生成物：服务端多一份 XxxGrpc.cs（proto 里声明了 service 才有）。"""
    name = type_name(proto_rel)
    out = {}
    for end, _dir in TARGETS:
        files = [name + ".cs"]
        if end == "server" and services > 0:
            files.append(name + "Grpc.cs")
        out[end] = files
    return out


def grpc_tools_version(root):
    """从 gen-proto.sh 读 GRPC_TOOLS_VERSION；读不到返回空串。"""
    path = os.path.join(root, GEN_SH)
    if not os.path.isfile(path):
        return ""
    m = VERSION_RE.search(read_text(path))
    return m.group(1) if m else ""


def target_dir(root, end):
    return dict(TARGETS)[end]


def build(root, protoc_version="", previous=None):
    """按磁盘现状生成清单对象。缺生成物时直接退出：清单不登记不存在的输出。"""
    if not protoc_version and previous:
        protoc_version = previous.get("protoc_version", "")
    protos = []
    for proto in proto_files(root):
        path = os.path.join(root, proto)
        services = service_count(read_text(path))
        outputs = {}
        for end, _dir in TARGETS:
            items = []
            for name in expected_outputs(proto, services)[end]:
                out_path = os.path.join(root, target_dir(root, end), name)
                if not os.path.isfile(out_path):
                    raise SystemExit(
                        "缺生成物：{}（先跑 bash tools/gen/gen-proto.sh）".format(rel(root, out_path))
                    )
                items.append({"path": rel(root, out_path), "sha256": sha256_bytes(out_path)})
            outputs[end] = items
        protos.append(
            {
                "proto": proto,
                "sha256": sha256_bytes(path),
                "services": services,
                "outputs": outputs,
            }
        )
    return {
        "schema": SCHEMA,
        "generator": "tools/gen/gen-proto-manifest.py",
        "generated_by": GEN_SH,
        "grpc_tools_version": grpc_tools_version(root),
        "protoc_version": protoc_version,
        "targets": {end: target_dir(root, end) for end, _dir in TARGETS},
        "protos": protos,
    }


def load(root):
    path = os.path.join(root, MANIFEST)
    if not os.path.isfile(path):
        return None, ["缺少 {}：跑 bash tools/gen/gen-proto.sh 生成".format(MANIFEST)]
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f), []
    except (OSError, ValueError) as exc:
        return None, ["{} 读不了：{}".format(MANIFEST, exc)]


def check(root, data=None):
    """返回 [(kind, message)]：kind ∈ coverage / input / output / pair / version。

    不清空、不改文件；只比对清单与磁盘现状。
    """
    if data is None:
        data, problems = load(root)
        if data is None:
            return [("coverage", p) for p in problems]
    problems = []
    if data.get("schema") != SCHEMA:
        problems.append(("version", "清单 schema 是 {}，本脚本认 {}".format(data.get("schema"), SCHEMA)))
    want_tools = grpc_tools_version(root)
    got_tools = data.get("grpc_tools_version", "")
    if want_tools and got_tools != want_tools:
        problems.append(
            ("version", "生成器版本漂移：清单记 grpc.tools={}，{} 里是 {}".format(got_tools, GEN_SH, want_tools))
        )
    if not data.get("protoc_version"):
        problems.append(("version", "清单没记 protoc 版本（重跑 gen-proto.sh 写入）"))

    listed = {}
    for item in data.get("protos") or []:
        proto = item.get("proto", "")
        if proto in listed:
            problems.append(("coverage", "清单里 {} 重复登记".format(proto)))
        listed[proto] = item
    actual = proto_files(root)
    for proto in actual:
        if proto not in listed:
            problems.append(("coverage", "清单漏了 {}（改 / 删 proto 后要重出清单）".format(proto)))
    for proto in sorted(listed):
        if proto not in actual:
            problems.append(("coverage", "清单登记了不存在的 {}".format(proto)))

    for proto in actual:
        item = listed.get(proto)
        if item is None:
            continue
        services = service_count(read_text(os.path.join(root, proto)))
        if item.get("sha256") != sha256_bytes(os.path.join(root, proto)):
            problems.append(
                ("input", "{} 的输入与清单对不上：改了 proto 没重出生成物（跑 tools/gen/gen-proto.sh）".format(proto))
            )
        if item.get("services") != services:
            problems.append(
                ("coverage", "{} 的 service 数与清单对不上（{} ≠ {}）：改了 proto 没重出".format(proto, services, item.get("services")))
            )
        outputs = item.get("outputs") or {}
        for end, _dir in TARGETS:
            got = outputs.get(end) or []
            want = expected_outputs(proto, services)[end]
            got_paths = sorted(x.get("path", "") for x in got)
            want_paths = sorted(target_dir(root, end) + "/" + name for name in want)
            if got_paths != want_paths:
                problems.append(
                    ("coverage", "{} 的{}生成物清单不对：应 {}，清单是 {}".format(proto, end, want_paths, got_paths))
                )
            for entry in got:
                path = os.path.join(root, entry.get("path", ""))
                if not os.path.isfile(path):
                    problems.append(("output", "清单登记的生成物不存在：{}".format(entry.get("path"))))
                elif entry.get("sha256") != sha256_bytes(path):
                    problems.append(
                        ("output", "生成物与清单不一致：{}（手改生成物，或改了 proto 没重出）".format(entry.get("path")))
                    )

    # 两个输出目录里不许有清单外的 .cs（重命名 proto 后留下的旧文件也在这里抓）。
    for end, _dir in TARGETS:
        out_dir = os.path.join(root, target_dir(root, end))
        if not os.path.isdir(out_dir):
            continue
        allowed = set()
        for item in listed.values():
            for entry in (item.get("outputs") or {}).get(end) or []:
                allowed.add(os.path.basename(entry.get("path", "")))
        for name in sorted(os.listdir(out_dir)):
            if name.endswith(".cs") and name not in allowed:
                problems.append(("output", "{} 里有未登记的生成物：{}".format(target_dir(root, end), name)))

    # 成对比较（用磁盘现状，不信清单）：服务端 Xxx.cs 与客户端 Xxx.cs 必须一致。
    sg = os.path.join(root, target_dir(root, "server"))
    cg = os.path.join(root, target_dir(root, "client"))
    if os.path.isdir(sg) and os.path.isdir(cg):
        for proto in actual:
            name = type_name(proto)
            s = os.path.join(sg, name + ".cs")
            c = os.path.join(cg, name + ".cs")
            if os.path.isfile(s) and os.path.isfile(c):
                if sha256_bytes(s) != sha256_bytes(c):
                    problems.append(("pair", "两端 {} 不一致：{} ≠ {}".format(name + ".cs", rel(root, s), rel(root, c))))
        grpc_leaks = sorted(n for n in os.listdir(cg) if n.endswith("Grpc.cs"))
        for name in grpc_leaks:
            problems.append(("pair", "客户端不该有 gRPC 生成物：{}/{}".format(target_dir(root, "client"), name)))
    return problems


def write(root, manifest):
    path = os.path.join(root, MANIFEST)
    text = json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    return path


def main(argv):
    ap = argparse.ArgumentParser(description="proto 生成物清单 / 核对")
    ap.add_argument("--root", default="")
    ap.add_argument("--protoc-version", default="")
    ap.add_argument("--check", action="store_true", help="只核对，不写文件")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args(argv)

    root = os.path.abspath(args.root or repo_root())
    if args.check:
        data, errs = load(root)
        problems = [("coverage", e) for e in errs] if data is None else check(root, data)
        if not args.quiet:
            for kind, msg in problems:
                print("FAIL [{}] {}".format(kind, msg))
            if not problems:
                print("OK proto 清单与生成物一致：{} 份 proto".format(len(proto_files(root))))
        return 1 if problems else 0

    previous, _errs = load(root)
    manifest = build(root, args.protoc_version, previous if isinstance(previous, dict) else None)
    path = write(root, manifest)
    if not args.quiet:
        outs = sum(len(p["outputs"]["server"]) + len(p["outputs"]["client"]) for p in manifest["protos"])
        print("写入 {}：{} 份 proto / {} 个生成物 / protoc={}".format(
            rel(root, path), len(manifest["protos"]), outs, manifest["protoc_version"] or "未记"))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
