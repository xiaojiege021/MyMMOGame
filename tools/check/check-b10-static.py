#!/usr/bin/env python3
"""B10 强化与结晶进副本伤害的静态断言。

只钉「乘区接上了、数值没写死、三条老口径没被改」，不钉条数与现值（D5）：
  1. proto：DescribeBattleProfileResponse 追加 enhance_coef_bps=10 / crystal_flat_attack=11（int32）；
  2. 配置：enhance.json 有 coef_bps_per_level 且是数字；crystal.json 每颗攻击结晶有 fixed_value；
     代码里不出现「0.24 / 2400 / 2%」这类写死的系数；
  3. 角色服：强化系数在角色服折算（EnhanceTable.CoefBpsFor），只算「已穿装备」——
     仓储按 character_equip join，强化按 inventory_id 读，结晶只认 category=attack；
  4. 战斗服：第二段两个乘区只在副本那条路给（无模板、无战场 Combat）；
     DamagePipeline 的公式仍是 base * (1 + enhance_coef + crystal_flat / base)；
  5. 冻结：竞技场模板、战场压缩、城战全额三条不读新字段；
  6. 客户端不本地算：背包屏不出现强化系数 / 结晶的乘积，只展示服务端回包。
"""
import json
import os
import re
import sys

root = os.environ["ROOT"]
fails = []


def read(path):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server", "CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World", "SectRepository.cs": "Sect", "SocialGrpcService.cs": "Grpc", "WorldGrpcService.cs": "Grpc", "Program.cs": "Cli"}
    _name = __import__("os").path.basename(path)
    if _name in _sub:
        # 14.8 拆分卡之后：主文件 + 分片目录一起读（断言文本不变，只改读法）。
        _os = __import__("os"); _g = __import__("glob")
        _b = path if _os.path.isabs(path) else _os.path.join(_os.getcwd(), path)
        if not _os.path.exists(_b):
            _d = _os.path.dirname(_os.getcwd())
            while _d and not _os.path.exists(_os.path.join(_d, path)):
                _nd = _os.path.dirname(_d)
                if _nd == _d: break
                _d = _nd
            _b = _os.path.join(_d, path)
        return "".join(open(f, encoding="utf-8").read() for f in
                       [_b] + sorted(_g.glob(_os.path.join(_os.path.dirname(_b), _sub[_name], "*.cs"))))
    with open(os.path.join(root, path), encoding="utf-8") as handle:
        return handle.read()


def want(condition, message):
    if not condition:
        fails.append(message)


def load(path):
    with open(os.path.join(root, path), encoding="utf-8") as handle:
        return json.load(handle)


proto = read("proto/mmo/character.proto")

# 1. 协议：两个字段的编号与类型。编号固定，老字段一个不动。
profile = re.search(r"message DescribeBattleProfileResponse \{(.*?)\n\}", proto, re.S)
want(profile is not None, "proto 里 DescribeBattleProfileResponse 读不出来")
if profile is not None:
    body = profile.group(1)
    want(re.search(r"int32 enhance_coef_bps = 10;", body) is not None,
         "DescribeBattleProfileResponse 缺 int32 enhance_coef_bps = 10")
    want(re.search(r"int32 crystal_flat_attack = 11;", body) is not None,
         "DescribeBattleProfileResponse 缺 int32 crystal_flat_attack = 11")
    # 老字段原样：B9 的 equip_attack=9、Q2 的 passive_pct_bps=8 不许被挤号。
    want(re.search(r"int32 equip_attack = 9;", body) is not None, "equip_attack 的字段号被改了")
    want(re.search(r"int32 passive_pct_bps = 8;", body) is not None, "passive_pct_bps 的字段号被改了")

generated = read("src/Shared/Generated/Character.cs")
for field in ("EnhanceCoefBps", "CrystalFlatAttack"):
    want(field in generated, "两端生成物里没有 " + field + "（proto 没重生成）")

# 2. 配置：系数只在配置里，代码里不写死。
enhance_cfg = load("config/enhance.json")
coef = enhance_cfg.get("coef_bps_per_level")
want(isinstance(coef, (int, float)), "enhance.json 缺 coef_bps_per_level 或不是数字")
want(enhance_cfg.get("max_level") == 12, "enhance.json 的 max_level 不是方案写定的 12")

crystal_cfg = load("config/crystal.json")
attack_crystals = [c for c in crystal_cfg["crystals"] if c.get("category") == "attack"]
want(len(attack_crystals) > 0, "crystal.json 没有攻击类结晶")
want(all(isinstance(c.get("fixed_value"), (int, float)) and c["fixed_value"] > 0
         for c in attack_crystals), "攻击类结晶缺 fixed_value")

# 角色服 / 战斗服的源码里不许出现「每级 2%」这类写死系数。
for path in ("src/Character/EnhanceTable.cs", "src/Character/CharacterGrpcService.cs",
             "src/Battle/Encounter.cs", "src/Battle/DamagePipeline.cs"):
    text = read(path)
    want("0.24" not in text and "2400" not in text,
         path + " 里写死了强化系数（应只从 enhance.json 读）")

# 3. 角色服：系数折算 + 只算已穿装备。
table = read("src/Character/EnhanceTable.cs")
want("CoefBpsPerLevel" in table, "EnhanceTable 没有读 coef_bps_per_level")
want("CoefBpsFor" in table, "EnhanceTable 没有 CoefBpsFor（系数折算没有唯一出口）")
want("coef_bps_per_level" in table, "EnhanceTable 的 Wire 没有 coef_bps_per_level")

repo = read("src/Character/EquipRepository.cs")
want("WornTotalsAsync" in repo, "EquipRepository 没有 WornTotalsAsync（已穿装备汇总口）")
want("character_equip" in repo, "WornTotalsAsync 不是从穿戴表读的")
# 强化挂在背包行上（C4），不是挂在格子上。
worn = re.search(r"WornTotalsAsync\(.*?\n    \}", repo, re.S)
want(worn is not None, "WornTotalsAsync 的实现读不出来")
if worn is not None:
    body = worn.group(0)
    want("on e.inventory_id = i.id" in body,
         "WornTotalsAsync 的强化不是按背包行 id 读的（退了 C4 的口径）")
    want("\"attack\"" in body, "WornTotalsAsync 没有按攻击类过滤结晶")

service = read("src/Character/CharacterGrpcService.cs")
want("WornTotalsAsync" in service, "角色服战斗快照没用 WornTotalsAsync")
want("EnhanceCoefBps = enhanceTable.CoefBpsFor(" in service,
     "角色服没有把强化等级之和折成强化系数")
want("CrystalFlatAttack = (int)worn.CrystalFlatAttack" in service,
     "角色服没有填 crystal_flat_attack")
# 一次连库读三样：不许再单独查一次装备攻击（三次连接会读到三个时刻的库）。
want("EquipAttackAsync(" not in service.split("DescribeBattleProfile")[1].split("AdvanceBranch")[0],
     "战斗快照里还留着单独的 EquipAttackAsync 查询（应走 WornTotalsAsync 一次读完）")

# 4. 战斗服：只在副本那条路给乘区。
client = read("src/Battle/BattleProfileClient.cs")
want("EnhanceCoefBps" in client and "CrystalFlatAttack" in client,
     "战斗服快照没带两个新字段")
want("reply.EnhanceCoefBps, reply.CrystalFlatAttack" in client,
     "战斗服快照没有从回包读两个新字段")

encounter = read("src/Battle/Encounter.cs")
want("wearsEquipment" in encounter, "Encounter 没有区分「副本 / 非副本」的口子")
want(re.search(r"member\.Template is null && member\.Combat is null", encounter) is not None,
     "门控条件不是「无模板且无战场属性」（竞技场 / 战场会双算）")
want("EnhanceCoef: wearsEquipment" in encounter, "强化系数没有按副本门控给")
want("CrystalFlatAttack: wearsEquipment" in encounter, "结晶固定值没有按副本门控给")

pipeline = read("src/Battle/DamagePipeline.cs")
want("input.CrystalFlatAttack / baseAttack" in pipeline,
     "第二段公式被改了：结晶固定值应除以基础攻击")
want("1 + input.EnhanceCoef" in pipeline, "第二段公式里没有强化系数")

# 5. 冻结：三条老口径不读新字段。
battle_server = read("src/Battle/Server.cs")
want("EnhanceCoef" not in battle_server and "CrystalFlat" not in battle_server,
     "战场 / 竞技场的属性折算读了新字段（1.3 倍压缩或模板会被二次放大）")
arena = read("src/Battle/Config.cs")
want("EnhanceCoef" not in arena and "CrystalFlat" not in arena,
     "竞技场模板 / 战场配置里出现了新字段")

# 6. 客户端不本地算。
bag_screen = read("client/MMOGame/Assets/HotUpdate/Ui/UiBagScreen.cs")
want("CrystalFlat" not in bag_screen and "EnhanceCoef" not in bag_screen,
     "背包屏在本地算强化 / 结晶（界面只展示服务端确认的结果）")

# 7. B2：套装加成只由角色服算，并进 equip_attack；战斗服不读套装表。
equip_repo = read("src/Character/EquipRepository.cs")
want("wornBySet" in equip_repo and "sets.BonusFor" in equip_repo,
     "已穿汇总没按套数加成（套装效果没接进 EquipAttack）")
want("record WornTotals(long Attack, long SetBonus" in equip_repo,
     "WornTotals 没有单独一列套装加成（分不出「装备和」与「套装加成」）")
service_cs = read("src/Character/CharacterGrpcService.cs")
want("EquipAttack = (int)worn.TotalAttack" in service_cs,
     "DescribeBattleProfile 的 equip_attack 没把套装加成并进去")
battle_all = "".join(read(p) for p in (
    "src/Battle/Config.cs", "src/Battle/Encounter.cs", "src/Battle/BattleProfileClient.cs"))
want("sets.json" not in battle_all and "SetTable" not in battle_all,
     "战斗服自己读套装表了（第 4 类：跨进程数值口径只许角色服一处写）")

if fails:
    for one in fails:
        print("b10_static=fail " + one)
    sys.exit(1)

print("b10_static=ok proto=2fields config=enhance+crystal "
      "character=worn-only battle=dungeon-only frozen=arena+battlefield+siege client=reads-reply")
