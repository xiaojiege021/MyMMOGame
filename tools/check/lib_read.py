#!/usr/bin/env python3
# tools/check/lib_read.py —— 拆分卡之后 python 段的读法（开发架构 14.8 纪律第 8 条）。
#
# 一个类型可能由「主文件 + 同域分片目录里的 partial 分片」组成。核对脚本仍按老路径
# 传主文件，这里把分片拼上去：断言文本一个字不改，改的只是「怎么读」。
#
# 同一张表有三份，改一处要同步改三处：
#   - bash 段：tools/check/lib-common.sh 的 split_parts（src_agg 用它）
#   - python 段：本文件的 PARTS
#   - Unity 编辑器自检：client/MMOGame/Assets/Editor/SplitRead.cs
#
# 用法（脚本一般已把 ROOT 放进环境变量）：
#   import sys; sys.path.insert(0, os.path.join(root, "tools/check"))
#   from lib_read import read_text
#   flow = read_text(os.path.join(ui, "UiSocialFlow.cs"))
#
# 顺序：主文件在前，分片按文件名排序。凡是用注释锚点「切到文件末尾」的断言都依赖
# 这个顺序（D2a–d 的坑），所以不要改成把分片排到前面。

import glob
import os

# basename → 分项目录名。没登记的 basename 原样读单个文件。
PARTS = {
    # D13：客户端 Ui 三个大文件 + 战斗 HUD / 战斗会话 + 表世界拆装层
    "UiShell.cs": "Shell",
    "UiSocialFlow.cs": "Flow",
    "UiWorldFlow.cs": "World",
    "BattleHud.cs": "Hud",
    "BattleClient.cs": "Session",
    "WorldEntry.cs": "WorldEntry",
    # D2–D3：服务端按域拆的那批（与 lib-common.sh 的 split_parts 一致）
    "CharacterGrpcService.cs": "Grpc",
    "RealmTable.cs": "Realm",
    "CharacterProcess.cs": "Startup",
    "Config.cs": "ConfigTables",
    "Encounter.cs": "Encounter",
    "Effects.cs": "Effects",
    "Server.cs": "Server",
    "CharacterApi.cs": "Api/Character",
    "SectApi.cs": "Api/Sect",
    "WorldApi.cs": "Api/World",
    "SectRepository.cs": "Sect",
    "SocialGrpcService.cs": "Grpc",
    "WorldGrpcService.cs": "Grpc",
}


def parts(path):
    """主文件 + 分片目录里的 *.cs（按文件名排序）；没登记或目录不存在时只有主文件。"""
    files = [path]
    sub = PARTS.get(os.path.basename(path))
    if sub:
        files.extend(sorted(glob.glob(os.path.join(os.path.dirname(path), sub, "*.cs"))))
    return [f for f in files if os.path.exists(f)]


def read_text(path):
    """聚合读。文件不存在就当空串（与各脚本原来的 `open(...) if exists else ''` 同口径）。"""
    if not os.path.exists(path):
        return ""
    return "".join(open(f, encoding="utf-8").read() + "\n" for f in parts(path))


# ---------------------------------------------------------------- D9 文案表
#
# 屏上的中文不再写在 .cs 里，正本搬到 config/ui_text/<组>.json，源码只留
# `UiStrings.<组>.<key>` 这样的常量引用。断言原来查「源码里有这句中文」的，
# 改成「表里有这句 + 源码引用了它的常量」——口径没放松，只是多跳一层。

def ui_text_ref(root, group, text):
    """文案表里 group 组下 text 的常量引用（如 `UiStrings.UiEntryFlow.T003`）；没有返回 None。"""
    import json
    path = os.path.join(root, "config/ui_text", group + ".json")
    if not os.path.exists(path):
        return None
    for entry in json.load(open(path, encoding="utf-8"))["entries"]:
        if entry["text"] == text:
            return "UiStrings.%s.%s" % (group, entry["key"])
    return None


def ui_text_assert(root, group, text, source, where=""):
    """断言「文案表里有这句，且 source（聚合读后的源码）引用了它」。返回引用串。"""
    ref = ui_text_ref(root, group, text)
    assert ref, "%s文案表里没有这句：%s" % (where and where + "：", text)
    assert ref in source, "%s源码没有引用 %s（原文：%s）" % (where and where + "：", ref, text)
    return ref


def ui_text_ref_like(root, group, needle):
    """子串版：文案表里 text 含 needle 的第一条。原断言用子串匹配时（一句长文案里的一小段）用它。"""
    import json
    path = os.path.join(root, "config/ui_text", group + ".json")
    if not os.path.exists(path):
        return None
    for entry in json.load(open(path, encoding="utf-8"))["entries"]:
        if needle in entry["text"]:
            return "UiStrings.%s.%s" % (group, entry["key"])
    return None


def ui_text_assert_like(root, group, needle, source, where=""):
    """断言「表里有一条含 needle 的文案，且 source 引用了它」。"""
    ref = ui_text_ref_like(root, group, needle)
    assert ref, "%s文案表里没有含「%s」的条目" % (where and where + "：", needle)
    assert ref in source, "%s源码没有引用 %s（含：%s）" % (where and where + "：", ref, needle)
    return ref
