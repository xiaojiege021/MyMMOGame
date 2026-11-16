#!/usr/bin/env python3
import os
import re
import sys

root = os.environ.get("ROOT") or os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
assets = os.path.join(root, "client", "MMOGame", "Assets")
ui = os.path.join(assets, "HotUpdate", "Ui")
net = os.path.join(assets, "Net")

for n in (
    "UiHomesteadScreen.cs",
    "UiHomesteadSelfCheck.cs",
    "UiSocialFlow.cs",
    "UiSocialCodex.cs",
):
    assert os.path.exists(os.path.join(ui, n)), "missing " + n
assert os.path.exists(os.path.join(net, "HomesteadEntry.cs")), "missing HomesteadEntry.cs"
assert os.path.exists(os.path.join(assets, "Editor", "U9Check.cs")), "missing U9Check.cs"

home = open(os.path.join(ui, "UiHomesteadScreen.cs"), encoding="utf-8").read()
flow = open(os.path.join(ui, "UiSocialFlow.cs"), encoding="utf-8").read()
codex = open(os.path.join(ui, "UiSocialCodex.cs"), encoding="utf-8").read()
entry = open(os.path.join(net, "HomesteadEntry.cs"), encoding="utf-8").read()

assert "u9_homestead_wired=ready" in home
assert "HomesteadEntry.ItemInSlot" in home
assert "无接口" not in home
assert "HomesteadEntry.Parse" in flow and "LoadHomestead" in flow
assert "PlaceDecor" in flow and "RemoveDecor" in flow
assert "HomesteadSlots" in codex and "DecorItemIds" in codex
assert "config/" not in home and "config/" not in flow and "config/" not in entry and "config/" not in codex
assert "DescribePath" in entry and "PlaceBody" in entry

for proto in ("social.proto", "character.proto", "world.proto"):
    text = open(os.path.join(root, "proto", "mmo", proto), encoding="utf-8").read().lower()
    assert "u9" not in text, "不动 proto：" + proto

allcards = open(os.path.join(root, "tools", "check", "check-all.sh"), encoding="utf-8").read()
assert "u9) echo" in allcards and re.search(r"\bu9\b", allcards)

arch = open(os.path.join(root, "docs", "开发架构.md"), encoding="utf-8").read()
assert "U9" in arch and "洞府" in arch

print("u9_static=ok")
sys.exit(0)
