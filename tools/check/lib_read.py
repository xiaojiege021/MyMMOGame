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
