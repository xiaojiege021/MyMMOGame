#!/usr/bin/env python3
"""U10 静态断言：F04 接种植与家园币，缺口文案已去掉，不读配置表、不动 proto。"""
import os
import re
import sys

root = os.environ.get("ROOT") or os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
assets = os.path.join(root, "client", "MMOGame", "Assets")
ui = os.path.join(assets, "HotUpdate", "Ui")
net = os.path.join(assets, "Net")

for n in ("UiHomesteadScreen.cs", "UiHomesteadPlantSelfCheck.cs", "UiSocialFlow.cs", "UiSocialCodex.cs"):
    assert os.path.exists(os.path.join(ui, n)), "missing " + n
for n in ("HomesteadPlantEntry.cs", "HomeCoinShopEntry.cs", "HomesteadEntry.cs"):
    assert os.path.exists(os.path.join(net, n)), "missing " + n
assert os.path.exists(os.path.join(assets, "Editor", "U10Check.cs")), "missing U10Check.cs"

home = open(os.path.join(ui, "UiHomesteadScreen.cs"), encoding="utf-8").read()
flow = open(os.path.join(ui, "UiSocialFlow.cs"), encoding="utf-8").read()
codex = open(os.path.join(ui, "UiSocialCodex.cs"), encoding="utf-8").read()
plant = open(os.path.join(net, "HomesteadPlantEntry.cs"), encoding="utf-8").read()
coin = open(os.path.join(net, "HomeCoinShopEntry.cs"), encoding="utf-8").read()
selfcheck = open(os.path.join(ui, "UiHomesteadPlantSelfCheck.cs"), encoding="utf-8").read()

# 屏：接了三块，成熟只认回包的 mature，缺口文案不再出现
assert "u10_homestead_plant=ready" in home
assert "HomesteadPlantEntry.PlantInSlot" in home and ".Mature" in home
assert "HomeCoinShopItemIds" in home and "PlantingNote" in home
assert "尚未接服务端" not in home, "种植缺口文案还留着"

# Flow：四个出口都在，回包解析只认 Net 层
for k in ("LoadPlants", "PlantCrop", "HarvestCrop", "BuyHomeCoinItem",
          "HomesteadPlantEntry.Parse", "HomeCoinShopEntry.Parse"):
    assert k in flow, "Flow 缺 " + k

# Codex：编号只作请求参数，旧缺口常量已删
for k in ("PlantSlots", "CropIds", "HomeCoinShopItemIds", "PlantingNote"):
    assert k in codex, "Codex 缺 " + k
assert "PlantingGap" not in codex

# Net：四条路径与两种处理在拆装层
for k in ("DescribePath", "PlantBody", "HarvestBody", "PlantInSlot"):
    assert k in plant, "HomesteadPlantEntry 缺 " + k
assert "BuyPath" in coin and "BuyBody" in coin

# 不读配置：客户端一个配置文件都不打开，也没有把配置里的数值搬进来
for text in (home, flow, codex, plant, coin, selfcheck):
    assert "config/" not in text
    assert "grow_seconds" not in text and "yield_home_coin" not in text
    assert "price_home_coin" not in text and "delivery_count" not in text
    assert "File.ReadAllText" not in text and "File.Exists" not in text

# 不动 proto
for proto in ("social.proto", "character.proto", "world.proto"):
    body = open(os.path.join(root, "proto", "mmo", proto), encoding="utf-8").read().lower()
    assert "u10" not in body, "不动 proto：" + proto

# 自检覆盖六件事
for k in ("plant", "mature", "harvest", "buy", "buy_pending", "unavailable"):
    assert '"' + k + '"' in selfcheck, "自检缺 " + k

allcards = open(os.path.join(root, "tools", "check", "check-all.sh"), encoding="utf-8").read()
assert "u10) echo" in allcards and re.search(r"\bu10\b", allcards)

arch = open(os.path.join(root, "docs", "开发架构.md"), encoding="utf-8").read()
assert "| 93 | U10 |" in arch and "洞府种植与家园币屏" in arch

print("u10_static=ok")
sys.exit(0)
