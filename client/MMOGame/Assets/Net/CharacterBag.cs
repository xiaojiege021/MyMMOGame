using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>背包里的一格。数量与绑定态都是回包里的值，界面不改。</summary>
    public struct BagSlotView
    {
        public uint Slot;
        public string ItemId;
        public int Count;
        public bool Bound;

        /// <summary>B9：这一件正穿在哪个部位（weapon / armor / accessory）；空串是没穿。</summary>
        public string EquippedSlot;

        /// <summary>屏上要显示「正穿着」时用它，不本地记一份穿戴状态。</summary>
        public bool IsWorn => !string.IsNullOrEmpty(EquippedSlot);
    }

    /// <summary>移动回包：source / target 是移完之后那两格的现状，界面照抄。</summary>
    public struct MoveOutcome
    {
        public bool Ok;
        public string Reason;
        public BagSlotView Source;
        public BagSlotView Target;
        public bool Merged;
        public bool Swapped;
        public string DisplayText;
    }

    /// <summary>丢弃回包：count_left 是这一格还剩几个，duplicate 表示同号重放没二次丢。</summary>
    public struct DiscardOutcome
    {
        public bool Ok;
        public string Reason;
        public string ItemId;
        public int CountLeft;
        public bool Duplicate;
        public string DisplayText;
    }

    /// <summary>排序回包：moved 是真正换了格的行数，slots 是排完之后的整份背包。</summary>
    public struct SortOutcome
    {
        public bool Ok;
        public string Reason;
        public int Moved;
        public List<BagSlotView> Slots;
        public string DisplayText;
    }

    /// <summary>批量使用回包：used 是这一笔真扣掉几个，被拒时是 0。</summary>
    public struct UseBatchOutcome
    {
        public bool Ok;
        public string Reason;
        public string ItemId;
        public int Used;
        public int CountLeft;
        public bool Duplicate;
        public string DisplayText;
    }

    /// <summary>
    /// B9 穿上回包：部位、装备固定攻击之和、等级与职业都是服务端给的事实值；
    /// 被拒时也给当时的事实（例如 level_not_enough 时的 level）。
    /// </summary>
    public struct EquipOutcome
    {
        public bool Ok;
        public string Reason;
        public string ItemId;
        public string EquipSlot;
        public int EquipAttack;
        public int Level;
        public string ClassId;
        public uint BagSlot;
        public string DisplayText;
    }

    /// <summary>B9 脱下回包：脱下不换格，bag_slot 就是它原来那一格。</summary>
    public struct UnequipOutcome
    {
        public bool Ok;
        public string Reason;
        public string ItemId;
        public string EquipSlot;
        public uint BagSlot;
        public int EquipAttack;
        public string DisplayText;
    }

    /// <summary>
    /// 背包只读回包（`GET /inventory`）。U5 的角色与装备、背包、强化、转移、结晶五屏
    /// 都从这里拿格号与物品编号——客户端不读 `config/items.json`，也不本地判种类。
    /// </summary>
    public struct BagOutcome
    {
        public bool Ok;
        public string Reason;
        public List<BagSlotView> Slots;
        public int BagSlots;
        public string CharacterName;
        public string Realm;
        public string RealmDisplay;

        /// <summary>屏上显示的境界名：优先用服务端给的展示名，老回包没有这一格时退回规范名。</summary>
        public string DisplayRealm =>
            !string.IsNullOrEmpty(RealmDisplay) ? RealmDisplay : Realm;

        public static BagOutcome Failure(string reason) => new BagOutcome
        {
            Ok = false,
            Reason = reason,
            Slots = new List<BagSlotView>(),
        };
    }

    /// <summary>
    /// 背包回包的拆装。Mmo.Net 是 noEngineReferences 的纯 .NET 程序集，
    /// 只拆这一条固定形状的回包，不引引擎里的 JSON 组件。
    /// </summary>
    public static class CharacterBag
    {
        /// <summary>背包请求。只带角色号，分页与筛选都在界面侧按已有回包做。</summary>
        public static string RequestPath(ulong characterId) =>
            "/inventory?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        public static BagOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || json.IndexOf("\"slots\"", StringComparison.Ordinal) < 0)
            {
                return BagOutcome.Failure("bad_reply");
            }

            var slots = new List<BagSlotView>();
            var start = json.IndexOf("\"slots\"", StringComparison.Ordinal);
            start = json.IndexOf('[', start);
            var end = start >= 0 ? json.IndexOf(']', start) : -1;
            if (start < 0 || end < 0)
            {
                return BagOutcome.Failure("bad_reply");
            }

            var at = start;
            while (true)
            {
                var open = json.IndexOf('{', at);
                if (open < 0 || open > end)
                {
                    break;
                }

                var close = json.IndexOf('}', open);
                if (close < 0 || close > end)
                {
                    break;
                }

                var one = json.Substring(open, close - open + 1);
                slots.Add(new BagSlotView
                {
                    Slot = (uint)ReadLong(one, "slot"),
                    ItemId = ReadRaw(one, "item_id"),
                    Count = (int)ReadLong(one, "count"),
                    Bound = ReadBool(one, "bound"),
                    EquippedSlot = ReadRaw(one, "equipped_slot"),
                });
                at = close + 1;
            }

            slots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            return new BagOutcome
            {
                Ok = true,
                Reason = string.Empty,
                Slots = slots,
                BagSlots = (int)ReadLong(json, "bag_slots"),
                CharacterName = ReadRaw(json, "character_name"),
                Realm = ReadRaw(json, "realm"),
                RealmDisplay = ReadRaw(json, "realm_display"),
            };
        }

        static bool ReadBool(string json, string key)
        {
            var raw = ReadRaw(json, key);
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ Q10 背包操作

        /// <summary>移动一格。只发两格格号；合不合并、交不交换、能不能合，全看回包。</summary>
        public static string MoveBody(ulong characterId, uint fromSlot, uint toSlot)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"from_slot\":" + fromSlot.ToString(CultureInfo.InvariantCulture)
                + ",\"to_slot\":" + toSlot.ToString(CultureInfo.InvariantCulture) + "}";
        }

        /// <summary>
        /// 丢弃。confirm 只有界面点过第二次确认才传 true——服务端不看这个字段的诚意，
        /// 但 false 一律拒；事务号留空时由网关现生成。
        /// </summary>
        public static string DiscardBody(ulong characterId, uint slot, int count, bool confirm)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture)
                + ",\"count\":" + count.ToString(CultureInfo.InvariantCulture)
                + ",\"confirm\":" + (confirm ? "true" : "false") + "}";
        }

        public static string SortBody(ulong characterId)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture) + "}";
        }

        /// <summary>批量使用。只发格号与个数；这一格能不能用、能连用几个全看回包。</summary>
        public static string UseBatchBody(ulong characterId, uint slot, int count)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture)
                + ",\"count\":" + count.ToString(CultureInfo.InvariantCulture) + "}";
        }

        // ------------------------------------------------------------------ B9 穿戴

        /// <summary>穿上一件：只发背包格号，部位与门槛都由角色服判。</summary>
        public static string EquipBody(ulong characterId, uint slot)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture) + "}";
        }

        /// <summary>脱下：按部位脱，同一个部位只有一件。</summary>
        public static string UnequipBody(ulong characterId, string equipSlot)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"equip_slot\":\"" + (equipSlot ?? "") + "\"}";
        }

        public static EquipOutcome ParseEquip(string json)
        {
            var ok = ReadBool(json, "ok");
            var reason = ReadRaw(json, "reason");
            var itemId = ReadRaw(json, "item_id");
            var worn = ReadRaw(json, "equip_slot");
            var attack = (int)ReadLong(json, "equip_attack");
            return new EquipOutcome
            {
                Ok = ok,
                Reason = reason,
                ItemId = itemId,
                EquipSlot = worn,
                EquipAttack = attack,
                Level = (int)ReadLong(json, "level"),
                ClassId = ReadRaw(json, "class_id"),
                BagSlot = (uint)ReadLong(json, "bag_slot"),
                DisplayText = ok
                    ? "已穿上 " + itemId + "（" + SlotName(worn) + "），装备攻击 " + attack
                    : "穿戴被拒：" + ReasonText(reason),
            };
        }

        public static UnequipOutcome ParseUnequip(string json)
        {
            var ok = ReadBool(json, "ok");
            var reason = ReadRaw(json, "reason");
            var worn = ReadRaw(json, "equip_slot");
            var attack = (int)ReadLong(json, "equip_attack");
            var bagSlot = (uint)ReadLong(json, "bag_slot");
            return new UnequipOutcome
            {
                Ok = ok,
                Reason = reason,
                ItemId = ReadRaw(json, "item_id"),
                EquipSlot = worn,
                BagSlot = bagSlot,
                EquipAttack = attack,
                DisplayText = ok
                    ? "已脱下 " + SlotName(worn) + "（第 " + bagSlot + " 格），装备攻击 " + attack
                    : "脱下被拒：" + ReasonText(reason),
            };
        }

        /// <summary>部位的屏上叫法。只做显示，不参与判定。</summary>
        public static string SlotName(string slot)
        {
            switch (slot)
            {
                case "weapon": return "武器";
                case "armor": return "护甲";
                case "accessory": return "饰品";
                default: return slot ?? string.Empty;
            }
        }

        /// <summary>拒绝码的屏上叫法：码本身照回包原样传进日志，这里只翻译给人看。</summary>
        static string ReasonText(string reason)
        {
            switch (reason)
            {
                case "slot_empty": return "这一格是空的";
                case "slot_occupied": return "这个部位已经穿着一件，先脱下";
                case "already_equipped": return "已经穿在身上了";
                case "level_not_enough": return "等级不够";
                case "class_mismatch": return "职业不符";
                case "not_equipment": return "这不是装备";
                case "item_not_found": return "这件装备没有定义";
                case "character_not_found": return "角色不存在";
                default: return reason ?? string.Empty;
            }
        }

        public static MoveOutcome ParseMove(string json)
        {
            var source = ReadSlotBlock(json, "source");
            var target = ReadSlotBlock(json, "target");
            var ok = ReadBool(json, "ok");
            var reason = ReadRaw(json, "reason");
            var merged = ReadBool(json, "merged");
            var swapped = ReadBool(json, "swapped");
            return new MoveOutcome
            {
                Ok = ok,
                Reason = reason,
                Source = source,
                Target = target,
                Merged = merged,
                Swapped = swapped,
                DisplayText = ok
                    ? (merged
                        ? "已合并到第 " + target.Slot + " 格"
                        : (swapped ? "两格已交换" : "已移到第 " + target.Slot + " 格"))
                    : "移动被拒：" + reason,
            };
        }

        public static DiscardOutcome ParseDiscard(string json)
        {
            var ok = ReadBool(json, "ok");
            var reason = ReadRaw(json, "reason");
            var duplicate = ReadBool(json, "duplicate");
            var left = (int)ReadLong(json, "count_left");
            return new DiscardOutcome
            {
                Ok = ok,
                Reason = reason,
                ItemId = ReadRaw(json, "item_id"),
                CountLeft = left,
                Duplicate = duplicate,
                DisplayText = ok
                    ? "已丢弃，这一格还剩 " + left
                    : (duplicate ? "重复提交，未二次丢弃" : "丢弃被拒：" + reason),
            };
        }

        public static SortOutcome ParseSort(string json)
        {
            var slots = ReadSlots(json);
            var ok = ReadBool(json, "ok");
            var reason = ReadRaw(json, "reason");
            var moved = (int)ReadLong(json, "moved");
            return new SortOutcome
            {
                Ok = ok,
                Reason = reason,
                Moved = moved,
                Slots = slots,
                DisplayText = ok ? "已排序，换了 " + moved + " 行" : "排序被拒：" + reason,
            };
        }

        public static UseBatchOutcome ParseUseBatch(string json)
        {
            var ok = ReadBool(json, "ok");
            var reason = ReadRaw(json, "reason");
            var duplicate = ReadBool(json, "duplicate");
            var used = (int)ReadLong(json, "used");
            var left = (int)ReadLong(json, "count_left");
            return new UseBatchOutcome
            {
                Ok = ok,
                Reason = reason,
                ItemId = ReadRaw(json, "item_id"),
                Used = used,
                CountLeft = left,
                Duplicate = duplicate,
                DisplayText = ok
                    ? "用掉 " + used + " 个，还剩 " + left
                    : (duplicate ? "重复提交，未二次使用" : "使用被拒：" + reason),
            };
        }

        /// <summary>读一格 `{slot, item_id, count, bound}`。回包里没有这一格就当空格。</summary>
        static BagSlotView ReadSlotBlock(string json, string key)
        {
            var at = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (at < 0)
            {
                return new BagSlotView();
            }

            var open = json.IndexOf('{', at);
            var close = open >= 0 ? json.IndexOf('}', open) : -1;
            if (open < 0 || close < 0)
            {
                return new BagSlotView();
            }

            var one = json.Substring(open, close - open + 1);
            return new BagSlotView
            {
                Slot = (uint)ReadLong(one, "slot"),
                ItemId = ReadRaw(one, "item_id"),
                Count = (int)ReadLong(one, "count"),
                Bound = ReadBool(one, "bound"),
                EquippedSlot = ReadRaw(one, "equipped_slot"),
            };
        }

        /// <summary>读整份 `slots` 数组；没有这一项就回空表。</summary>
        static List<BagSlotView> ReadSlots(string json)
        {
            var slots = new List<BagSlotView>();
            var at = json.IndexOf("\"slots\"", StringComparison.Ordinal);
            if (at < 0)
            {
                return slots;
            }

            var start = json.IndexOf('[', at);
            var end = start >= 0 ? json.IndexOf(']', start) : -1;
            if (start < 0 || end < 0)
            {
                return slots;
            }

            var cursor = start;
            while (true)
            {
                var open = json.IndexOf('{', cursor);
                if (open < 0 || open > end)
                {
                    break;
                }

                var close = json.IndexOf('}', open);
                if (close < 0 || close > end)
                {
                    break;
                }

                var one = json.Substring(open, close - open + 1);
                slots.Add(new BagSlotView
                {
                    Slot = (uint)ReadLong(one, "slot"),
                    ItemId = ReadRaw(one, "item_id"),
                    Count = (int)ReadLong(one, "count"),
                    Bound = ReadBool(one, "bound"),
                    EquippedSlot = ReadRaw(one, "equipped_slot"),
                });
                cursor = close + 1;
            }

            slots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            return slots;
        }

        static long ReadLong(string json, string key)
        {
            var raw = ReadRaw(json, key);
            long value;
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }

        static string ReadRaw(string json, string key)
        {
            // 键必须紧跟冒号才算命中：值里出现同一个词时（拒绝码 reason="duplicate"
            // 与键 duplicate 撞名）不能把它当成键读走。老实现只找带引号的键名，
            // 于是 duplicate 会被读成 reason 的值，重放那一笔在屏上显示不出来。
            var needle = "\"" + key + "\"";
            var colon = -1;
            var from = 0;
            while (true)
            {
                var found = json.IndexOf(needle, from, StringComparison.Ordinal);
                if (found < 0)
                {
                    return string.Empty;
                }

                var after = found + needle.Length;
                while (after < json.Length && char.IsWhiteSpace(json[after]))
                {
                    after++;
                }

                if (after < json.Length && json[after] == ':')
                {
                    colon = after;
                    break;
                }

                from = found + needle.Length;
            }

            var index = colon + 1;
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }

            if (index >= json.Length)
            {
                return string.Empty;
            }

            if (json[index] == '"')
            {
                var close = json.IndexOf('"', index + 1);
                if (close < 0)
                {
                    return string.Empty;
                }

                return json.Substring(index + 1, close - index - 1);
            }

            var stop = index;
            while (stop < json.Length && json[stop] != ',' && json[stop] != '}' && json[stop] != ']')
            {
                stop++;
            }

            return json.Substring(index, stop - index).Trim();
        }
    }
}
