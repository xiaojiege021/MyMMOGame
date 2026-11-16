#!/usr/bin/env python3
"""U11 静态断言：F05 接 /hero-codex，九格只读回包，缺口文案已去掉，不读配置、不动 proto。"""
import os
import re
import sys

root = os.environ.get("ROOT") or os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
assets = os.path.join(root, "client", "MMOGame", "Assets")
ui = os.path.join(assets, "HotUpdate", "Ui")
net = os.path.join(assets, "Net")

for n in ("UiHeroCodexScreen.cs", "UiHeroCodexSelfCheck.cs", "UiSocialFlow.cs", "UiSocialCodex.cs"):
    assert os.path.exists(os.path.join(ui, n)), "missing " + n
for n in ("HeroCodexEntry.cs",):
    assert os.path.exists(os.path.join(net, n)), "missing " + n
assert os.path.exists(os.path.join(assets, "Editor", "U11Check.cs")), "missing U11Check.cs"

screen = open(os.path.join(ui, "UiHeroCodexScreen.cs"), encoding="utf-8").read()
flow = open(os.path.join(ui, "UiSocialFlow.cs"), encoding="utf-8").read()
codex = open(os.path.join(ui, "UiSocialCodex.cs"), encoding="utf-8").read()
entry = open(os.path.join(net, "HeroCodexEntry.cs"), encoding="utf-8").read()
selfcheck = open(os.path.join(ui, "UiHeroCodexSelfCheck.cs"), encoding="utf-8").read()

# 屏：九格与收集度只认回包，缺口文案不再出现，也不写死九位名字
assert "u11_codex_wired=ready" in screen
assert "HeroCodexEntry.EntryAt" in screen and "HeroCodexEntry.SummaryLine" in screen
assert "CodexNote" in screen
assert "CodexGap" not in screen and "尚未接服务端" not in screen, "图鉴缺口文案还留着"
assert "乔小满" not in screen, "屏里写死了名字"

# Flow：只读出口在，回包解析只认 Net 层
for k in ("LoadHeroCodex", "HeroCodexEntry.RequestPath", "HeroCodexEntry.Parse"):
    assert k in flow, "Flow 缺 " + k

# Codex：缺口常量与本地编的九位都已删
assert "CodexNote" in codex
assert "CodexGap" not in codex, "CodexGap 还留着"
assert "CodexNames" not in codex, "本地编的九位还留着"

# Net：路径与拆装、中文解转义、每位与合计的排版
for k in ("RequestPath", "Parse", "EntryLine", "SummaryLine", "Unescape"):
    assert k in entry, "HeroCodexEntry 缺 " + k

# 不读配置：客户端一个配置文件都不打开，也没有把配置里的数值搬进来
for text in (screen, flow, codex, entry, selfcheck):
    assert "config/" not in text
    assert "hero_codex" not in text.lower()
    assert "max_total_passive_pct_bps" not in text
    assert "File.ReadAllText" not in text and "File.Exists" not in text

# 不动 proto
for proto in ("character.proto", "social.proto", "world.proto"):
    body = open(os.path.join(root, "proto", "mmo", proto), encoding="utf-8").read().lower()
    assert "u11" not in body, "不动 proto：" + proto

# 自检覆盖这几件事
for k in ("pending", "entries", "collected", "passive", "advance", "no_entries", "reject"):
    assert '"' + k + '"' in selfcheck, "自检缺 " + k

allcards = open(os.path.join(root, "tools", "check", "check-all.sh"), encoding="utf-8").read()
assert "u11) echo" in allcards and re.search(r"\bu11\b", allcards)

arch = open(os.path.join(root, "docs", "开发架构.md"), encoding="utf-8").read()
assert "| 94 | U11 |" in arch and "英灵图鉴屏" in arch

print("u11_static=ok")
sys.exit(0)
