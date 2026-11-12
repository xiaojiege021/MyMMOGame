using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>一个币种的余额，来自 `/currency` 回包。</summary>
    public struct CurrencyRow
    {
        public string Kind;
        public long Amount;
    }

    /// <summary>
    /// 货币余额的拆装。界面只读这一份；扣款与入账都发生在服务端那一笔事务里，
    /// 这里不拿余额做任何判定。
    /// </summary>
    public static class CharacterCurrency
    {
        public static string RequestPath(ulong characterId) =>
            "/currency?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        public static List<CurrencyRow> Parse(string json)
        {
            var rows = new List<CurrencyRow>();
            if (string.IsNullOrEmpty(json))
            {
                return rows;
            }

            var at = json.IndexOf("\"balances\"", StringComparison.Ordinal);
            if (at < 0)
            {
                return rows;
            }

            var end = json.Length;
            while (true)
            {
                var open = json.IndexOf('{', at + 1);
                if (open < 0 || open > end)
                {
                    break;
                }

                var close = json.IndexOf('}', open);
                if (close < 0)
                {
                    break;
                }

                var one = json.Substring(open, close - open + 1);
                rows.Add(new CurrencyRow
                {
                    Kind = JsonField.Of(one, "kind"),
                    Amount = ParseLong(JsonField.Of(one, "amount")),
                });
                at = close;
            }

            return rows;
        }

        static long ParseLong(string raw)
        {
            long value;
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }
    }
}
