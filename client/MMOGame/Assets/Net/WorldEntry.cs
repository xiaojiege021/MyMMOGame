using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mmo.Net
{
    /// <summary>进分线的结果。分到哪条线、这条线满没满、周围连着哪几块地表都由世界服给。</summary>
    public enum WorldEnterKind
    {
        Entered,
        Refused,
        BadRequest,
        InvalidToken,
        CharacterNotFound,
        Unavailable,
        Network,
        Rejected,
    }

    /// <summary>`/world/enter` 拆出来的展示结果。没进成就看 <see cref="Reason"/>，不在本地补一个成功。</summary>
    public struct WorldEnterOutcome
    {
        public WorldEnterKind Kind;
        public bool Entered;
        public string Reason;
        public string LineId;
        public string RegionId;
        public uint LineNo;
        public uint LineSize;
        public uint LineCapacity;
        public bool Duplicate;
        public string[] Neighbors;
        public string[] DialogueIds;
        public string Error;

        /// <summary>被拒时给人看的一句。原因原样带出来，不翻译成别的话。</summary>
        public string DisplayText
        {
            get
            {
                switch (Kind)
                {
                    case WorldEnterKind.Entered:
                        return RegionId + " · 第 " + LineNo.ToString(CultureInfo.InvariantCulture) + " 线";
                    case WorldEnterKind.Refused:
                        return "进不去：" + Reason;
                    case WorldEnterKind.Network:
                        return "连不上网关";
                    case WorldEnterKind.InvalidToken:
                        return "令牌失效";
                    default:
                        return "进分线没成：" + (Error.Length > 0 ? Error : Reason);
                }
            }
        }
    }

    /// <summary>一块地表。邻接与对白编号都来自服务端，客户端不自己连边。</summary>
    public struct WorldRegionRow
    {
        public string Id;
        public string Name;
        public uint[] Chapters;
        public string[] Neighbors;
        public string[] DialogueIds;
        public bool Open;

        /// <summary>N14：不开放时的原因（`region_locked` / `region_route_mismatch`），开放时是空串。</summary>
        public string Reason;

        /// <summary>N14：剧情解锁要求交完第几场；静态开放与只锁着的地表由服务端给 0。</summary>
        public int UnlockStage;

        /// <summary>N14：判定用的主线场次（服务端在这一刻读到的事实值）。</summary>
        public int MainStage;
    }

    /// <summary>`/world/regions` 只读回包：地点、开口的人、势力三张编号表。</summary>
    public struct WorldRegionsOutcome
    {
        public WorldEnterKind Kind;
        public uint Version;
        public WorldRegionRow[] Regions;
        public string[] Speakers;
        public string[] Factions;
        public string Error;
    }

    /// <summary>
    /// 表世界两条 REST 的请求与回包拆装：`/world/enter` 进分线、`/world/regions` 读地点表。
    /// 按开发架构第 3 节，分线分配、容量、邻接、开放与对白归属都在世界服判；
    /// 这里只拆固定形状的回包，不算距离、不选线、不按配置补一个「应该分到第几线」。
    /// Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，所以不用引擎的 JsonUtility，
    /// 也不引第二套 JSON 框架——这两条回包字段固定，够用即可。
    /// </summary>
    public static partial class WorldEntry
    {
        /// <summary>
        /// 进分线的请求体。只有角色号与地表编号：身份走令牌，名字牌与分线都由服务端定，
        /// 客户端带不进 display_name、line_id、neighbors。
        /// </summary>
        public static string EnterBody(ulong characterId, string regionId)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"region_id\":" + Quote(regionId ?? string.Empty) + "}";
        }

        /// <summary>
        /// 地点表的路由。带角色号时世界服按这个角色的主线进度与立仙 / 立魔逐块判「这一刻开没开」；
        /// 角色号为 0 时只读静态表（老口径，屏上照配置的 open 标）。
        /// 进度与路线都不在客户端手里，这里也带不进去。
        /// </summary>
        public static string RegionsPath(ulong characterId) =>
            characterId == 0
                ? "/world/regions"
                : "/world/regions?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        public static WorldEnterOutcome ParseEnter(int status, string json)
        {
            var kind = KindOf(status);
            if (status == 200)
            {
                if (string.IsNullOrEmpty(json) || KeyAt(json, "entered") < 0)
                {
                    return new WorldEnterOutcome
                    {
                        Kind = WorldEnterKind.Rejected,
                        Error = "bad_reply",
                    };
                }

                var entered = ReadBool(json, "entered");
                return new WorldEnterOutcome
                {
                    Kind = entered ? WorldEnterKind.Entered : WorldEnterKind.Refused,
                    Entered = entered,
                    Reason = Raw(json, "reason"),
                    LineId = Raw(json, "line_id"),
                    RegionId = Raw(json, "region_id"),
                    LineNo = ReadUInt(json, "line_no"),
                    LineSize = ReadUInt(json, "line_size"),
                    LineCapacity = ReadUInt(json, "line_capacity"),
                    Duplicate = ReadBool(json, "duplicate"),
                    Neighbors = ReadStringArray(json, "neighbors"),
                    DialogueIds = ReadStringArray(json, "dialogue_ids"),
                    Error = string.Empty,
                };
            }

            // 非 200 的回包里没有业务字段，数组也要显式给空，
            // 免得调用方数邻居个数时撞空引用。
            return new WorldEnterOutcome
            {
                Kind = kind,
                Error = ErrorOf(json, kind),
                Neighbors = Array.Empty<string>(),
                DialogueIds = Array.Empty<string>(),
            };
        }

        public static WorldRegionsOutcome ParseRegions(int status, string json)
        {
            var kind = KindOf(status);
            if (status != 200)
            {
                return new WorldRegionsOutcome
                {
                    Kind = kind,
                    Error = ErrorOf(json, kind),
                    Regions = Array.Empty<WorldRegionRow>(),
                    Speakers = Array.Empty<string>(),
                    Factions = Array.Empty<string>(),
                };
            }

            if (string.IsNullOrEmpty(json) || KeyAt(json, "regions") < 0)
            {
                return new WorldRegionsOutcome
                {
                    Kind = WorldEnterKind.Rejected,
                    Error = "bad_reply",
                    Regions = Array.Empty<WorldRegionRow>(),
                    Speakers = Array.Empty<string>(),
                    Factions = Array.Empty<string>(),
                };
            }

            // regions 是对象数组：先切出每个对象的文本，再逐个取字段，
            // 免得内层的 neighbors / dialogue_ids 跟顶层的同名键串了。
            var blocks = ObjectBlocks(json, "regions");
            var rows = new List<WorldRegionRow>(blocks.Count);
            for (var i = 0; i < blocks.Count; i++)
            {
                var one = blocks[i];
                rows.Add(new WorldRegionRow
                {
                    Id = Raw(one, "id"),
                    Name = Raw(one, "name"),
                    Chapters = ReadUIntArray(one, "chapters"),
                    Neighbors = ReadStringArray(one, "neighbors"),
                    DialogueIds = ReadStringArray(one, "dialogue_ids"),
                    Open = ReadBool(one, "open"),
                    Reason = Raw(one, "reason"),
                    UnlockStage = ReadInt(one, "unlock_stage"),
                    MainStage = ReadInt(one, "main_stage"),
                });
            }

            return new WorldRegionsOutcome
            {
                Kind = WorldEnterKind.Entered,
                Version = ReadUInt(json, "version"),
                Regions = rows.ToArray(),
                Speakers = ReadStringArray(json, "speakers"),
                Factions = ReadStringArray(json, "factions"),
                Error = string.Empty,
            };
        }

        /// <summary>按编号取一条回包。没有这一条就 false，不补一条默认行。</summary>
        public static bool TryRegion(WorldRegionsOutcome outcome, string regionId, out WorldRegionRow row)
        {
            row = default;
            if (outcome.Regions == null || string.IsNullOrEmpty(regionId))
            {
                return false;
            }

            for (var i = 0; i < outcome.Regions.Length; i++)
            {
                if (string.Equals(outcome.Regions[i].Id, regionId, StringComparison.Ordinal))
                {
                    row = outcome.Regions[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 回包说的开放状态，一句话。原因与门槛都照抄服务端的 reason / unlock_stage：
        /// 「这条路不开」和「第几场才开」是两件事，屏上不能写成一句。
        /// </summary>
        public static string RegionStateText(WorldRegionRow row)
        {
            if (row.Open)
            {
                return "已开";
            }

            if (string.Equals(row.Reason, "region_route_mismatch", StringComparison.Ordinal))
            {
                return "这条路不开";
            }

            if (string.Equals(row.Reason, "region_locked", StringComparison.Ordinal) && row.UnlockStage > 0)
            {
                return "第 " + row.UnlockStage + " 场才开";
            }

            return "未开";
        }
    }
}
