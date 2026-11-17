#!/usr/bin/env python3
"""从 config/ 生成客户端的内容表 UiContentTable.cs（K6 / C5）。

输入（唯一正本）：
  config/world.json          地表顺序、势力、开口人的顺序
  config/region/<地名>.json  地名、章节、邻接、open、position、map_x / map_y
  config/flight.json         每块地表的境界门槛与三段大境界
  config/npc.json            NPC 实体（名字、地表、站位、职能、对话 / 商店引用）
  config/dialogue/<人名>.json 开口人的 origin / 场次 / 每一条定词与边界

输出：
  client/MMOGame/Assets/HotUpdate/Ui/UiContentTable.cs

客户端不再手写人名 / 地名 / 台词；改配置后重跑本脚本，check-k6.sh / check-c5.sh
会逐字比对生成物。
  python3 tools/gen/gen-ui-content.py            # 写正本
  python3 tools/gen/gen-ui-content.py --out X    # 写到别处（核对用）
  python3 tools/gen/gen-ui-content.py --root R --out X   # 用 R 下的配置生成（改配置演练）
"""

import argparse
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_OUT = os.path.join(ROOT, "client/MMOGame/Assets/HotUpdate/Ui/UiContentTable.cs")
BASE = ROOT


def load(rel):
    path = os.path.join(BASE, rel)
    if not os.path.exists(path):
        raise SystemExit("缺少配置：" + rel)
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def cs(value):
    """JSON 字符串 → C# 字符串字面量。"""
    return json.dumps(value, ensure_ascii=False)


def fnum(value):
    text = ("%.2f" % float(value)).rstrip("0").rstrip(".")
    return text + "f"


def arr(values, fmt=cs):
    return "new[] { " + ", ".join(fmt(v) for v in values) + " }"


def main():
    global BASE
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=DEFAULT_OUT)
    parser.add_argument("--root", default=ROOT, help="配置根目录，默认仓库根")
    args = parser.parse_args()
    BASE = os.path.abspath(args.root)

    world = load("config/world.json")
    flight = load("config/flight.json")
    npc_doc = load("config/npc.json")

    region_order = world.get("regions") or []
    speakers = world.get("speakers") or []
    factions = world.get("factions") or []
    if len(region_order) != 8:
        raise SystemExit("world.json 的 regions 应是 8 块地表，实际 %d" % len(region_order))
    if not speakers:
        raise SystemExit("world.json 没有 speakers")

    realms = flight.get("realms") or []
    flight_realm = flight.get("flight_realm") or ""
    realm_of = {r["region_id"]: r["realm"] for r in (flight.get("region_realms") or [])}

    layout = load("tools/gen/ui_map_layout.json")
    map_points = {row["id"]: (row["x"], row["y"]) for row in (layout.get("regions") or [])}

    npcs = npc_doc.get("npcs") or []
    npc_by_dialogue = {n["dialogue_id"]: n for n in npcs if n.get("dialogue_id")}

    region_lines = []
    for rid in region_order:
        region = load("config/region/%s.json" % rid)
        if region.get("id") != rid:
            raise SystemExit("region 文件名与 id 不一致：" + rid)
        if rid not in map_points:
            raise SystemExit("tools/gen/ui_map_layout.json 缺摆点：" + rid)
        if rid not in realm_of:
            raise SystemExit("flight.json 的 region_realms 漏了 " + rid)
        region_lines.append(
            "            new UiWorldCodex.Region\n"
            "            {\n"
            "                Id = %s, Name = %s, Chapters = %s,\n"
            "                Neighbors = %s, Open = %s, Realm = %s,\n"
            "                Position = %s, MapX = %s, MapY = %s,\n"
            "            },"
            % (
                cs(rid), cs(region["name"]), arr(region["chapters"], str),
                arr(region["neighbors"]), "true" if region["open"] else "false",
                cs(realm_of[rid]), cs(region["position"]),
                fnum(map_points[rid][0]), fnum(map_points[rid][1]),
            ))

    speaker_lines = []
    dialogue_lines = []
    total_lines = 0
    for name in speakers:
        if name not in npc_by_dialogue:
            raise SystemExit("npc.json 没有开口的人：" + name)
        dialogue = load("config/dialogue/%s.json" % name)
        lines = dialogue.get("lines") or []
        if not lines:
            raise SystemExit("dialogue/%s.json 没有台词" % name)
        first = lines[0]
        speaker_lines.append(
            "            new UiWorldCodex.SpeakerMeta\n"
            "            {\n"
            "                Name = %s, Origin = %s, LineCount = %d, FirstChapter = %d, FirstRegion = %s,\n"
            "            },"
            % (cs(name), cs(dialogue.get("origin") or ""), len(lines),
               int(first.get("chapter") or 0), cs(first.get("region") or "")))

        rows = []
        for line in lines:
            total_lines += 1
            text = line.get("text") or ""
            if not text:
                raise SystemExit("dialogue/%s.json 有空的 text" % name)
            region = line.get("region") or ""
            rows.append(
                "                new UiWorldCodex.SpeakerLine\n"
                "                {\n"
                "                    Chapter = %d, Region = %s,\n"
                "                    Text = %s, Limit = %s,\n"
                "                },"
                % (int(line.get("chapter") or 0), cs(region),
                   cs(text), cs(line.get("limit") or "")))
        dialogue_lines.append(
            "            new[]\n"
            "            {\n"
            "%s\n"
            "            },"
            % "\n".join(rows))

    npc_lines = []
    for npc in npcs:
        pos = npc.get("pos") or {}
        npc_lines.append(
            "            new UiWorldCodex.NpcMeta\n"
            "            {\n"
            "                Id = %s, Name = %s, Region = %s, Role = %s,\n"
            "                Faction = %s, DialogueId = %s, ShopId = %s,\n"
            "                X = %s, Y = %s, Z = %s, Facing = %s,\n"
            "            },"
            % (
                cs(npc["id"]), cs(npc["name"]), cs(npc["region"]), cs(npc["role"]),
                cs(npc.get("faction") or ""), cs(npc.get("dialogue_id") or ""),
                cs(npc.get("shop_id") or ""),
                fnum(pos.get("x", 0)), fnum(pos.get("y", 0)), fnum(pos.get("z", 0)),
                fnum(npc.get("facing", 0)),
            ))

    body = """// <auto-generated>
// 由 tools/gen/gen-ui-content.py 从以下配置生成，不要手改：
//   config/world.json / config/region/*.json / config/flight.json
//   config/npc.json / config/dialogue/*.json / tools/gen/ui_map_layout.json
// 重新生成：python3 tools/gen/gen-ui-content.py
// 核对：bash tools/check/check-k6.sh / bash tools/check/check-c5.sh（重跑生成器并逐字比对）
// </auto-generated>

namespace HotUpdate.Ui
{
    /// <summary>客户端唯一的内容表：人名、地名、势力、开口人的元数据与全部定词都从 config/ 生成。</summary>
    public static class UiContentTable
    {
        public const int Version = 2;

        /// <summary>三段大境界，顺序与 config/flight.json 一致。</summary>
        public static readonly string[] Realms = %s;

        /// <summary>能飞的起点境界，照 config/flight.json 的 flight_realm。</summary>
        public const string FlightRealm = %s;

        /// <summary>八块地表，顺序与 config/world.json 的 regions 一致。</summary>
        public static readonly UiWorldCodex.Region[] Regions =
        {
%s
        };

        /// <summary>九位开口的人，顺序与 config/world.json 的 speakers 一致。</summary>
        public static readonly UiWorldCodex.SpeakerMeta[] Speakers =
        {
%s
        };

        /// <summary>九位开口人的全部定词（C5），顺序与 Speakers 一致，逐条照 config/dialogue。</summary>
        public static readonly UiWorldCodex.SpeakerLine[][] SpeakerDialogue =
        {
%s
        };

        /// <summary>五个势力，照 config/world.json 的 factions。</summary>
        public static readonly string[] Factions = %s;

        /// <summary>NPC 实体（K6），站位与职能照 config/npc.json。</summary>
        public static readonly UiWorldCodex.NpcMeta[] Npcs =
        {
%s
        };
    }
}
""" % (arr(realms), cs(flight_realm), "\n".join(region_lines),
       "\n".join(speaker_lines), "\n".join(dialogue_lines),
       arr(factions), "\n".join(npc_lines))

    out = os.path.abspath(args.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        f.write(body)
    print("wrote=%s regions=%d speakers=%d npcs=%d lines=%d" %
          (os.path.relpath(out, ROOT), len(region_order), len(speakers),
           len(npcs), total_lines))


if __name__ == "__main__":
    main()
