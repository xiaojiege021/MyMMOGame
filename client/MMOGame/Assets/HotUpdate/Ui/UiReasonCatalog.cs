namespace HotUpdate.Ui
{
    /// <summary>一组 reason。分组只是展示用，判定仍在服务端。</summary>
    public sealed class UiReasonGroup
    {
        /// <summary>组名，屏上直接显示。</summary>
        public readonly string Name;

        /// <summary>这一组的 reason 原文，服务端返回什么就显示什么。</summary>
        public readonly string[] Reasons;

        public UiReasonGroup(string name, string[] reasons)
        {
            Name = name;
            Reasons = reasons;
        }
    }

    /// <summary>
    /// 业务 reason 的分组表，供通用状态屏展示（原型 G01「reason 分组（服务端正本）」）。
    ///
    /// 这里只做「原文 → 分组」的展示归类，**不判定、不翻译成结论、不决定要不要重试**；
    /// 拒绝与否、能不能重试都看回包里的 <c>ok</c> 与 <c>reason</c>。
    /// 每个词都在服务端代码里真实存在，核对脚本逐个回查，别在这里造词。
    /// </summary>
    public static class UiReasonCatalog
    {
        /// <summary>分组顺序与原型一致，末尾补上 P1-5a / P1-5b 的拍卖与寄售。</summary>
        public static readonly UiReasonGroup[] Groups =
        {
            new UiReasonGroup("归属 / 会话", new[]
            {
                "invalid_token", "token_expired", "character_not_found", "account_missing",
            }),
            new UiReasonGroup("世界", new[]
            {
                "not_in_line", "unknown_region", "not_adjacent", "region_locked", "region_realm_locked",
                "flight_forbidden", "wrong_region", "unknown_node", "spirit_stone_forbidden", "node_busy",
            }),
            new UiReasonGroup("强化 / 转移", new[]
            {
                "wrong_stone", "stone_insufficient", "gold_insufficient", "coupon_insufficient",
                "source_not_higher", "not_equipment", "slot_empty",
            }),
            new UiReasonGroup("结晶", new[]
            {
                "wrong_part", "invalid_socket", "crystal_slot_empty", "unknown_crystal",
            }),
            new UiReasonGroup("拍卖 / 寄售", new[]
            {
                "listing_not_found", "listing_sold", "same_character", "bound_item", "ingot_insufficient",
            }),
            new UiReasonGroup("幂等 / 配置", new[]
            {
                "duplicate", "txn_missing", "txn_id_invalid", "unknown_item", "unknown_dungeon",
                "party_size_mismatch",
            }),
        };

        /// <summary>一共多少个 reason。屏上与核对都用它，别再数一遍。</summary>
        public static int Count
        {
            get
            {
                var total = 0;
                for (var i = 0; i < Groups.Length; i++)
                {
                    total += Groups[i].Reasons.Length;
                }

                return total;
            }
        }

        /// <summary>这个词在不在表里。用于核对，不参与业务判定。</summary>
        public static bool Contains(string reason)
        {
            for (var i = 0; i < Groups.Length; i++)
            {
                var reasons = Groups[i].Reasons;
                for (var j = 0; j < reasons.Length; j++)
                {
                    if (reasons[j] == reason)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
