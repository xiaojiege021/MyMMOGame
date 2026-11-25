using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>
    /// B6 觉醒状态（`GET /character/awaken`）。`skill_id = 0` 表示还没觉醒，
    /// 不是错误；只有角色不存在时网关才回 404。
    /// </summary>
    public struct AwakenState
    {
        public bool Ok;
        public string Reason;
        public string Branch;
        public int SkillId;
        public int Level;
        public long AwakenAtMs;

        /// <summary>回包给了技能编号就算已觉醒——这是字段的有无，不是本地判的。</summary>
        public bool Awakened => SkillId > 0;

        public static AwakenState Failure(string reason) => new AwakenState
        {
            Ok = false,
            Reason = reason,
        };
    }

    /// <summary>B6 觉醒这一笔的回包。`duplicate` 是同一分支重放。</summary>
    public struct AwakenOutcome
    {
        public bool Ok;
        public string Reason;
        public string Branch;
        public int SkillId;
        public int Level;
        public bool Duplicate;

        public static AwakenOutcome Failure(string reason) => new AwakenOutcome
        {
            Ok = false,
            Reason = reason,
        };
    }

    /// <summary>背包里的一只坐骑（`owned` 的一行）。`mounted` 现在骑的是不是这一只。</summary>
    public struct MountFact
    {
        public string ItemId;
        public string Name;
        public string Tier;
        public int SpeedBps;
        public int BagSlot;
        public bool Mounted;
    }

    /// <summary>B4 坐骑栏只读结果（`GET /character/mounts`）。没骑时前两项是空串与 0，不是错误。</summary>
    public struct MountBoardOutcome
    {
        public bool Ok;
        public string Reason;
        public string EquippedItemId;
        public int EquippedSpeedBps;
        public List<MountFact> Owned;

        public static MountBoardOutcome Failure(string reason) => new MountBoardOutcome
        {
            Ok = false,
            Reason = reason,
            Owned = new List<MountFact>(),
        };
    }

    /// <summary>骑上 / 下坐骑的回包（两条口同一个形状）。</summary>
    public struct MountActionOutcome
    {
        public bool Ok;
        public string Reason;
        public string ItemId;
        public string Tier;
        public int SpeedBps;
        public int BagSlot;

        public static MountActionOutcome Failure(string reason) => new MountActionOutcome
        {
            Ok = false,
            Reason = reason,
        };
    }

    /// <summary>图鉴里的一只灵兽。`collected` 由角色服按背包现在持有算。</summary>
    public struct SpiritBeastFact
    {
        public string ItemId;
        public string Name;
        public string Tier;
        public bool Collected;
        public int LifeAssistPctBps;
    }

    /// <summary>
    /// B4 灵兽图鉴只读结果（`GET /character/spirit-beasts`）。
    /// 收集度、图鉴条数、万分比与生活协助合计全是角色服算好的事实值。
    /// </summary>
    public struct SpiritBeastOutcome
    {
        public bool Ok;
        public string Reason;
        public int CollectedCount;
        public int EntryCount;
        public int CollectionPctBps;
        public int TotalLifeAssistPctBps;
        public string PassiveKind;
        public List<SpiritBeastFact> Entries;

        public static SpiritBeastOutcome Failure(string reason) => new SpiritBeastOutcome
        {
            Ok = false,
            Reason = reason,
            Entries = new List<SpiritBeastFact>(),
        };
    }

    /// <summary>
    /// U21：B6 觉醒与 B4 坐骑 / 灵兽的 HTTP 口拆装。
    ///
    /// 三条域各一条只读、觉醒与骑乘各一条写：
    /// 觉醒 `GET/POST /character/awaken`、坐骑 `GET /character/mounts` +
    /// `POST /character/mount` / `POST /character/dismount`、
    /// 灵兽 `GET /character/spirit-beasts`。
    ///
    /// 客户端只发角色号、分支号与背包格号：等级够不够、分支对不对、能不能骑、
    /// 档位与速度、收集度与生活协助全在角色服按配置表与库里判；事务号由网关生成。
    /// 这里不读配置表、不算速度加成、不把万分比折成百分比。
    /// Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，不引引擎里的 JSON 组件。
    /// </summary>
    public static class Companion
    {
        /// <summary>觉醒：POST 这一笔、GET 只读状态，同一路径。</summary>
        public const string AwakenPath = "/character/awaken";

        /// <summary>骑上某一格背包里的坐骑。</summary>
        public const string MountUpPath = "/character/mount";

        /// <summary>下坐骑。</summary>
        public const string DismountPath = "/character/dismount";

        /// <summary>当前骑的是哪一只、背包里一共有哪几只。只读。</summary>
        public const string MountsPath = "/character/mounts";

        /// <summary>灵兽图鉴与生活协助合计。只读。</summary>
        public const string SpiritPath = "/character/spirit-beasts";

        public static string Query(string path, ulong characterId) =>
            path + "?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        /// <summary>觉醒请求体：只有角色号与分支号。技能编号由角色服按配置表现取。</summary>
        public static string AwakenBody(ulong characterId, string branchId)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"branch_id\":\"" + Escape(branchId ?? string.Empty) + "\"}";
        }

        /// <summary>骑上请求体：只有角色号与背包格号。</summary>
        public static string MountUpBody(ulong characterId, int slot)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture) + "}";
        }

        /// <summary>下坐骑请求体：只有角色号。</summary>
        public static string DismountBody(ulong characterId) =>
            "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture) + "}";

        /// <summary>
        /// `GET /character/awaken`。回包里没有 `ok`，只有 `found`：
        /// 角色存在就是 200，找不找得到人看这一格。
        /// </summary>
        public static AwakenState ParseState(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "found").Length == 0)
            {
                return AwakenState.Failure("bad_reply");
            }

            return new AwakenState
            {
                Ok = JsonField.Bool(json, "found"),
                Branch = JsonField.Text(json, "branch"),
                SkillId = JsonField.Int(json, "skill_id"),
                Level = JsonField.Int(json, "level"),
                AwakenAtMs = JsonField.Long(json, "awaken_at_ms"),
            };
        }

        public static AwakenOutcome ParseAwaken(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return AwakenOutcome.Failure("bad_reply");
            }

            return new AwakenOutcome
            {
                Ok = JsonField.Bool(json, "ok"),
                Reason = JsonField.Text(json, "reason"),
                Branch = JsonField.Text(json, "branch"),
                SkillId = JsonField.Int(json, "skill_id"),
                Level = JsonField.Int(json, "level"),
                Duplicate = JsonField.Bool(json, "duplicate"),
            };
        }

        /// <summary>坐骑栏：清单顺序就是回包顺序，不按速度或档位重排。</summary>
        public static MountBoardOutcome ParseMounts(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return MountBoardOutcome.Failure("bad_reply");
            }

            var outcome = new MountBoardOutcome
            {
                Ok = JsonField.Bool(json, "ok"),
                Reason = JsonField.Text(json, "reason"),
                EquippedItemId = JsonField.Text(json, "equipped_item_id"),
                EquippedSpeedBps = JsonField.Int(json, "equipped_speed_bps"),
                Owned = new List<MountFact>(),
            };

            var items = JsonField.Objects(json, "owned");
            for (var i = 0; i < items.Count; i++)
            {
                var one = items[i];
                outcome.Owned.Add(new MountFact
                {
                    ItemId = JsonField.Text(one, "item_id"),
                    Name = JsonField.Text(one, "name"),
                    Tier = JsonField.Text(one, "tier"),
                    SpeedBps = JsonField.Int(one, "speed_bps"),
                    BagSlot = JsonField.Int(one, "bag_slot"),
                    Mounted = JsonField.Bool(one, "mounted"),
                });
            }

            return outcome;
        }

        public static MountActionOutcome ParseMountAction(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return MountActionOutcome.Failure("bad_reply");
            }

            return new MountActionOutcome
            {
                Ok = JsonField.Bool(json, "ok"),
                Reason = JsonField.Text(json, "reason"),
                ItemId = JsonField.Text(json, "item_id"),
                Tier = JsonField.Text(json, "tier"),
                SpeedBps = JsonField.Int(json, "speed_bps"),
                BagSlot = JsonField.Int(json, "bag_slot"),
            };
        }

        /// <summary>灵兽图鉴：九格名字、档位、收没收集全照回包，不补条目、不删条目。</summary>
        public static SpiritBeastOutcome ParseSpiritBeasts(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return SpiritBeastOutcome.Failure("bad_reply");
            }

            var outcome = new SpiritBeastOutcome
            {
                Ok = JsonField.Bool(json, "ok"),
                Reason = JsonField.Text(json, "reason"),
                CollectedCount = JsonField.Int(json, "collected_count"),
                EntryCount = JsonField.Int(json, "entry_count"),
                CollectionPctBps = JsonField.Int(json, "collection_pct_bps"),
                TotalLifeAssistPctBps = JsonField.Int(json, "total_life_assist_pct_bps"),
                PassiveKind = JsonField.Text(json, "passive_kind"),
                Entries = new List<SpiritBeastFact>(),
            };

            var items = JsonField.Objects(json, "entries");
            for (var i = 0; i < items.Count; i++)
            {
                var one = items[i];
                outcome.Entries.Add(new SpiritBeastFact
                {
                    ItemId = JsonField.Text(one, "item_id"),
                    Name = JsonField.Text(one, "name"),
                    Tier = JsonField.Text(one, "tier"),
                    Collected = JsonField.Bool(one, "collected"),
                    LifeAssistPctBps = JsonField.Int(one, "life_assist_pct_bps"),
                });
            }

            return outcome;
        }

        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
