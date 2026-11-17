#!/usr/bin/env python3
"""U13 静态断言：U5 境界屏接 N13 的两条口，阶名与被动只认回包，门槛不本地判，不动 proto / 配置。"""
import os
import re
import sys

root = os.environ.get("ROOT") or os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
assets = os.path.join(root, "client", "MMOGame", "Assets")
ui = os.path.join(assets, "HotUpdate", "Ui")
net = os.path.join(assets, "Net")


def read(*parts):
    with open(os.path.join(*parts), encoding="utf-8") as handle:
        return handle.read()


for name in ("UiRealmScreen.cs", "UiUpgradeFlow.cs", "UiUpgradeSelfCheck.cs", "UiUpgradeCodex.cs"):
    assert os.path.exists(os.path.join(ui, name)), "missing " + name
assert os.path.exists(os.path.join(net, "CharacterRealmPhase.cs")), "missing CharacterRealmPhase.cs"
assert os.path.exists(os.path.join(assets, "Editor", "U13Check.cs")), "missing U13Check.cs"

entry = read(net, "CharacterRealmPhase.cs")
screen = read(ui, "UiRealmScreen.cs")
flow = read(ui, "UiUpgradeFlow.cs")
codex = read(ui, "UiUpgradeCodex.cs")
selfcheck = read(ui, "UiUpgradeSelfCheck.cs")
editor = read(assets, "Editor", "U13Check.cs")
gateway = read(root, "src", "Gateway", "RealmPhaseApi.cs")

# 1) Net：两条口的路径、请求体只有角色号、拆装与排版件齐全
for key in ("ReadQuery", "PromoteBody", "Parse", "PhaseLine", "PromoteLine", "RequiredLine", "Unescape"):
    assert key in entry, "CharacterRealmPhase 缺 " + key
assert '"/realm/phase"' in entry and '"/realm/phase/promote"' in entry, "路径不是 N13 那两条"
promote_body = re.search(r"PromoteBody\(ulong characterId\) =>(.*?);", entry, re.S)
assert promote_body and "character_id" in promote_body.group(1), "升阶请求体没带角色号"
assert "txn" not in entry.lower(), "客户端请求体里出现了事务号"

# 2) 网关口径未改：两条口仍是 N13 的 MapGet / MapPost
assert "MapGet(\"/realm/phase\"" in gateway, "网关只读口不见了"
assert "MapPost(\"/realm/phase/promote\"" in gateway, "网关升阶口不见了"

# 3) Flow：只走 Net 拆装，两条动作都在
for key in ("LoadRealmPhase", "PromoteRealmPhase", "CharacterRealmPhase.Parse",
            "CharacterRealmPhase.ReadQuery", "CharacterRealmPhase.PromoteBody"):
    assert key in flow, "UiUpgradeFlow 缺 " + key

# 4) 屏：只读回包，按钮接两条动作，静态境界阶梯表已撤
for key in ("CharacterRealmPhase.PhaseLine", "CharacterRealmPhase.PromoteLine",
            "CharacterRealmPhase.RequiredLine", "LoadRealmPhase", "PromoteRealmPhase"):
    assert key in screen, "UiRealmScreen 缺 " + key
assert "UiUpgradeCodex.Realms" not in screen, "屏上还在读静态境界阶梯表"
assert "Realms" not in codex, "UiUpgradeCodex 的静态境界表还没撤"
assert "尝试突破" not in screen and "AdvanceRealm" not in screen, "本卡不该做下一大境界突破"

# 5) 客户端不算：不读配置、没有第二份阶名表（炼体九阶 / 四阶名一个都不写）
hot = screen + flow + codex + selfcheck + entry
for word in ("大圆满", "初期", "九阶"):
    assert word not in hot, "客户端出现了阶名 " + word
for key in ("sub_phases", "promote_min_level", "realms.json"):
    assert key not in hot, "客户端读了境界配置：" + key
# 「不读配置」只看境界这条链上本卡碰的四个文件（UiUpgradeCodex 里还留着 U5 强化档位的旧注释）。
for name, body in (("UiRealmScreen.cs", screen), ("UiUpgradeFlow.cs", flow),
                   ("UiUpgradeSelfCheck.cs", selfcheck), ("CharacterRealmPhase.cs", entry)):
    assert "config/" not in body, "读了配置：" + name
assert "File.ReadAllText" not in entry and "File.Exists" not in entry
# 伤害公式不接被动：战斗服源码里一处都不许有
battle_dir = os.path.join(root, "src", "Battle")
for name in sorted(os.listdir(battle_dir)):
    if not name.endswith(".cs"):
        continue
    body = read(battle_dir, name)
    assert "passive_pct_bps" not in body and "RealmPhase" not in body, "战斗服接了境界被动：" + name

# 6) 自检覆盖：尚未请求 / 只读 / 升成 / 被拒 / 请求体
for key in ("realm_pending", "realm_phase", "promote_ok", "promote_reject", "promote_body"):
    assert '"' + key + '"' in selfcheck, "屏自检缺 " + key

# 7) 编辑器核对脚本自己也在
for key in ("u13_files", "u13_net_parse", "u13_flow_actions", "u13_screen_wired", "u13_self"):
    assert key.replace("u13_", "") in editor, "U13Check 缺 " + key

# 8) 不动 proto：两条 rpc 仍在，proto 里没有本卡痕迹
proto = read(root, "proto", "mmo", "character.proto")
assert "PromoteRealmPhase" in proto and "DescribeRealmPassive" in proto
assert "u13" not in proto.lower(), "这张卡不该动 proto"

# 9) 总控三处都挂上 u13
allcards = read(root, "tools", "check", "check-all.sh")
assert "u13) echo" in allcards, "check-all 的 deps_of 没挂 u13"
assert re.search(r"\bu13\b", allcards), "check-all 的 ALL_CARDS 没挂 u13"
assert "u13" in allcards.split("client_card_needs_unity()")[1].split("esac")[0], \
    "check-all 的 client_card_needs_unity 没挂 u13"

# 10) 排期表里还是这一行的口径
arch = read(root, "docs", "开发架构.md")
assert "| 97 | U13 | 段内四阶屏 |" in arch, "开发架构第 97 行口径变了"

print("u13_static=ok")
sys.exit(0)
