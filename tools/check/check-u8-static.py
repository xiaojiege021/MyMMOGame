#!/usr/bin/env python3
# U8 静态断言（由 check-u8.sh 调用）。单独成文件，避免 heredoc 在 Windows 壳层里丢输出。
import os
import re
import sys

root = os.environ.get("ROOT") or os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
assets = os.path.join(root, "client", "MMOGame", "Assets")
ui = os.path.join(assets, "HotUpdate", "Ui")

for n in (
    "UiEntryHub.cs",
    "UiEntryHubSelfCheck.cs",
    "UiWorldHudScreen.cs",
    "UiEntryFlow.cs",
    "UiSettleScreen.cs",
    "UiWorldFlow.cs",
):
    path = os.path.join(ui, n)
    assert os.path.exists(path), "missing " + n
assert os.path.exists(os.path.join(assets, "Editor", "U8Check.cs")), "missing U8Check.cs"

hub = open(os.path.join(ui, "UiEntryHub.cs"), encoding="utf-8").read()
hud = open(os.path.join(ui, "UiWorldHudScreen.cs"), encoding="utf-8").read()
entry = open(os.path.join(ui, "UiEntryFlow.cs"), encoding="utf-8").read()
settle = open(os.path.join(ui, "UiSettleScreen.cs"), encoding="utf-8").read()
world = open(os.path.join(ui, "UiWorldFlow.cs"), encoding="utf-8").read()
selfcheck = open(os.path.join(ui, "UiEntryHubSelfCheck.cs"), encoding="utf-8").read()

assert 'ReadyLog = "u8_entry_hub=ready"' in hub, "hub ready log"
assert "OpenHudFunction" in hub and "OpenUpgrade" in hub and "OpenCommerce" in hub
assert "OpenSocial" in hub and "OpenDungeon" in hub and "BackToWorld" in hub
assert "GateFromLoading" in hub and "EnterWorld" in hub
assert "config/" not in hub and "JsonField.Of" not in hub, "hub reads config"
assert "这一格留给后续卡" not in hub and "这一格留给后续卡" not in hud
assert "UiEntryHub.OpenHudFunction" in hud, "hud not wired"
assert "切目标还没有独立屏" in hud, "切目标仍应诚实标未接"
assert "UiEntryHub.GateFromLoading" in entry and "UiEntryHub.EnterWorld" in entry
assert "Application.isPlaying" in entry, "gate must skip editor self-check"
assert len(re.findall(r"Entered = true;", entry)) == 1
assert "UiEntryHub.BackToWorld" in settle
assert "表世界入口等 U4 接上" not in settle
assert "SubmitEnter(string regionId, Action done = null)" in world
assert "u8_self_" in selfcheck and "config/" not in selfcheck

for proto in ("social.proto", "character.proto", "world.proto", "battle.proto"):
    text = open(os.path.join(root, "proto", "mmo", proto), encoding="utf-8").read().lower()
    assert "u8" not in text, "这张卡不该动 proto：" + proto

allcards = open(os.path.join(root, "tools", "check", "check-all.sh"), encoding="utf-8").read()
assert "u8) echo" in allcards, "check-all 没有 u8"
assert re.search(r"\bu8\b", allcards), "ALL_CARDS 缺 u8"

arch = open(os.path.join(root, "docs", "开发架构.md"), encoding="utf-8").read()
assert "U8" in arch and "界面入口" in arch, "开发架构未登记 U8"

print("u8_static=ok")
sys.exit(0)
