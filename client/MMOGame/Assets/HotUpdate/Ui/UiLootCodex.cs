using System.Collections.Generic;
using Mmo.Proto.Battle;

namespace HotUpdate.Ui
{
    /// <summary>掉落归属的一行：哪件、几件、归谁。三个数都是战斗服给的。</summary>
    public struct UiLootRow
    {
        public string ItemId;
        public int Count;
        public ulong WinnerCharacterId;
    }

    /// <summary>
    /// U15 掷点屏的文案与口径。
    ///
    /// 归属只认战斗服下发的 `LootAssigned`（`mode` + `lines` 的 item_id / count /
    /// winner_character_id）；`winner_character_id = 0` 就写「没人要」，不发放也不猜。
    /// **点数不在客户端出现**：界面不掷、不算、不改，意向只有 need / greed / pass 三个词。
    /// </summary>
    public static class UiLootCodex
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u15_loot=ready";

        public const string Notice = "掷点只在服务端 · 界面不掷、不算、不改 · 意图只有需求 / 贪婪 / 放弃三个词";

        /// <summary>归属为 0 时的文案。</summary>
        public const string Nobody = "没人要";

        /// <summary>没收到归属时的空态。</summary>
        public const string EmptyList = "没收到掉落归属 · 全队一份的掉落由战斗服下发 LootAssigned，personal 模式不发这条";

        public const string Need = "need";

        public const string Greed = "greed";

        public const string Pass = "pass";

        /// <summary>三个词的中文名。认不出的照原文写，不编第四个词。</summary>
        public static string Word(string decision)
        {
            switch (decision)
            {
                case Need:
                    return "需求";
                case Greed:
                    return "贪婪";
                case Pass:
                    return "放弃";
                default:
                    return decision ?? string.Empty;
            }
        }

        /// <summary>`LootAssigned` 拆成一行一行。形状固定，按 lines 的顺序原样列。</summary>
        public static List<UiLootRow> Rows(LootAssigned assigned)
        {
            var rows = new List<UiLootRow>();
            if (assigned == null)
            {
                return rows;
            }

            for (var i = 0; i < assigned.Lines.Count; i++)
            {
                var line = assigned.Lines[i];
                rows.Add(new UiLootRow
                {
                    ItemId = line.ItemId ?? string.Empty,
                    Count = line.Count,
                    WinnerCharacterId = line.WinnerCharacterId,
                });
            }

            return rows;
        }

        /// <summary>归属那一句：谁拿到了。0 就是没人要，不写成「待定」。</summary>
        public static string OwnerLine(UiLootRow row)
        {
            if (row.WinnerCharacterId == 0)
            {
                return Nobody;
            }

            return "归角色 " + row.WinnerCharacterId;
        }

        /// <summary>这一行的标题：物品编号 × 件数。两件以上才写「× N」。</summary>
        public static string ItemLine(UiLootRow row)
        {
            var item = string.IsNullOrEmpty(row.ItemId) ? "未知物品" : row.ItemId;
            return row.Count > 1 ? item + " × " + row.Count : item;
        }
    }
}
