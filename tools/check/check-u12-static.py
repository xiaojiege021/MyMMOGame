#!/usr/bin/env python3
"""U12 静态断言：F03 接 /versus/*，进场路径与模式事实只读回包，缺口文案已去掉，
不本地算战力、不做匹配、不读配置、不动 proto。"""
import os
import re
import sys

root = os.environ.get("ROOT") or os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
assets = os.path.join(root, "client", "MMOGame", "Assets")
ui = os.path.join(assets, "HotUpdate", "Ui")
net = os.path.join(assets, "Net")

for n in ("UiArenaScreen.cs", "UiArenaSelfCheck.cs", "UiSocialFlow.cs", "UiSocialCodex.cs"):
    assert os.path.exists(os.path.join(ui, n)), "missing " + n
for n in ("VersusEntry.cs",):
    assert os.path.exists(os.path.join(net, n)), "missing " + n
assert os.path.exists(os.path.join(assets, "Editor", "U12Check.cs")), "missing U12Check.cs"

screen = open(os.path.join(ui, "UiArenaScreen.cs"), encoding="utf-8").read()
flow = open(os.path.join(ui, "UiSocialFlow.cs"), encoding="utf-8").read()
codex = open(os.path.join(ui, "UiSocialCodex.cs"), encoding="utf-8").read()
entry = open(os.path.join(net, "VersusEntry.cs"), encoding="utf-8").read()
selfcheck = open(os.path.join(ui, "UiArenaSelfCheck.cs"), encoding="utf-8").read()

# 屏：两条进场口都接上，进场事实只认回包，缺口文案不再出现
assert "u12_versus_wired=ready" in screen
assert "VersusEntry.SummaryLine" in screen
assert "EnterArena" in screen and "EnterBattlefield" in screen
assert "进竞技场" in screen and "B 队" in screen
assert "VersusGap" not in screen and "无匹配入口" not in screen, "缺口文案还留着"
assert "无入口" not in screen, "「无入口」芯片还留着"

# Flow：两条口各一个唯一出口，回包解析只认 Net 层
for k in ("EnterArena", "EnterBattlefield", "VersusEntry.ArenaPath",
          "VersusEntry.BattlefieldPath", "VersusEntry.Parse"):
    assert k in flow, "Flow 缺 " + k

# Codex：缺口常量已删，进场口径与两个请求参数在
assert "VersusEnterNote" in codex
assert "ArenaModeIds" in codex and "BattlefieldMatchIds" in codex
assert "VersusGap" not in codex, "VersusGap 还留着"

# Net：路径、请求体、拆装、两处排版、解转义
for k in ("ArenaPath", "BattlefieldPath", "ArenaBody", "BattlefieldBody",
          "Parse", "SummaryLine", "ModeLine", "Unescape"):
    assert k in entry, "VersusEntry 缺 " + k

# 不读配置：客户端一个配置文件都不打开
for text in (screen, flow, codex, entry, selfcheck):
    assert "config/" not in text
    assert "File.ReadAllText" not in text and "File.Exists" not in text
    assert "Directory.GetFiles" not in text

# 不本地算战力 / 不造匹配：屏上没有系数乘法
assert "1.3" not in screen and "0.7" not in screen, "屏上乘了平衡系数"
assert "1.3" not in selfcheck and "0.7" not in selfcheck
assert "Matchmaking" not in screen and "Matchmaking" not in entry

# 不动 proto
for proto in ("character.proto", "battle.proto", "world.proto"):
    body = open(os.path.join(root, "proto", "mmo", proto), encoding="utf-8").read().lower()
    assert "u12" not in body, "不动 proto：" + proto

# 自检覆盖这几件事
for k in ("pending", "arena", "body_only", "no_local_multiply", "reason", "reject", "battlefield"):
    assert '"' + k + '"' in selfcheck, "自检缺 " + k

allcards = open(os.path.join(root, "tools", "check", "check-all.sh"), encoding="utf-8").read()
assert "u12) echo" in allcards and re.search(r"\bu12\b", allcards)
assert "u12)" in allcards.split("client_card_needs_unity", 1)[1], "总控没把 u12 标成客户端卡"

arch = open(os.path.join(root, "docs", "开发架构.md"), encoding="utf-8").read()
assert "| 95 | U12 |" in arch and "竞技场与战场进场屏" in arch

print("u12_static=ok")
sys.exit(0)
