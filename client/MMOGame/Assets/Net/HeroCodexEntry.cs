using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mmo.Net
{
    /// <summary>
    /// 图鉴里的一位英灵。名字、解锁场次、收没收到、被动万分比都是服务端给的；
    /// 客户端不按主线场次自己推「该不该收到」，也不补一条目录里没有的人。
    /// </summary>
    public struct HeroCodexEntryView
    {
        public string Id;
        public string Name;
        public int UnlockMainStage;
        public bool Collected;
        public int PassivePctBps;
    }

    /// <summary>
    /// 英灵图鉴回包（N12 的 `GET /hero-codex`）。
    ///
    /// 九位的名字、收没收到、每位与合计的被动万分比，全部来自角色服按 `main_stage` 推出来的
    /// 结果；配置里的被动百分比、封顶值都在服务端，客户端不读、不算、不乘。
    /// </summary>
    public struct HeroCodexOutcome
    {
        public bool Ok;
        public string Reason;
        public ulong CharacterId;
        public int MainStage;
        public int CollectedCount;
        public int EntryCount;
        public int CollectionPctBps;
        public int TotalPassivePctBps;
        public string PassiveKind;
        public List<HeroCodexEntryView> Entries;
        public string DisplayText;

        public static HeroCodexOutcome Failure(string reason) => new HeroCodexOutcome
        {
            Ok = false,
            Reason = reason ?? string.Empty,
            PassiveKind = string.Empty,
            Entries = new List<HeroCodexEntryView>(),
            DisplayText = "没读到：" + (reason ?? string.Empty),
        };
    }

    /// <summary>`/hero-codex` 的路径与回包拆装。只读口：请求体为空，不带任何数值。</summary>
    public static class HeroCodexEntry
    {
        public const string Path = "/hero-codex";

        /// <summary>只带角色号；令牌在请求头上，事务号这条口不需要。</summary>
        public static string RequestPath(ulong characterId) =>
            Path + "?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        public static HeroCodexOutcome Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || JsonField.Of(json, "ok").Length == 0)
            {
                return HeroCodexOutcome.Failure("bad_reply");
            }

            var ok = string.Equals(JsonField.Of(json, "ok"), "true", StringComparison.OrdinalIgnoreCase);
            var reason = JsonField.Of(json, "reason");
            var entries = ParseEntries(json);
            var collected = ReadInt(json, "collected_count");
            var entryCount = ReadInt(json, "entry_count");

            return new HeroCodexOutcome
            {
                Ok = ok,
                Reason = reason,
                CharacterId = ReadUlong(json, "character_id"),
                MainStage = ReadInt(json, "main_stage"),
                CollectedCount = collected,
                EntryCount = entryCount,
                CollectionPctBps = ReadInt(json, "collection_pct_bps"),
                TotalPassivePctBps = ReadInt(json, "total_passive_pct_bps"),
                PassiveKind = JsonField.Of(json, "passive_kind"),
                Entries = entries,
                DisplayText = ok
                    ? "收集 " + collected + " / " + entryCount + " · 万分比 " + ReadInt(json, "collection_pct_bps")
                    : ("没成：" + (reason.Length == 0 ? "未知原因" : reason)),
            };
        }

        /// <summary>第几格是回包里的第几条；越界回 <c>null</c>，不补默认条目。</summary>
        public static HeroCodexEntryView? EntryAt(HeroCodexOutcome outcome, int index)
        {
            if (outcome.Entries == null || index < 0 || index >= outcome.Entries.Count)
            {
                return null;
            }

            return outcome.Entries[index];
        }

        /// <summary>
        /// 一格下面的那行小字。收没收到看回包的 <c>collected</c>，被动照抄回包的万分比：
        /// 没收到的位服务端发 0，这里不拿配置里的数补上。
        /// </summary>
        public static string EntryLine(HeroCodexEntryView entry)
        {
            return entry.Collected
                ? "已收集 · 被动 " + entry.PassivePctBps + " bps"
                : "未收集 · 被动 " + entry.PassivePctBps + " bps";
        }

        /// <summary>右侧那一行的合计。合计也不再自己加一遍，直接抄回包。</summary>
        public static string SummaryLine(HeroCodexOutcome outcome)
        {
            if (!outcome.Ok)
            {
                return "服务端：" + (outcome.Reason.Length == 0 ? "没成功" : outcome.Reason);
            }

            return "回包 收集 " + outcome.CollectedCount + " / " + outcome.EntryCount
                + " · 万分比 " + outcome.CollectionPctBps
                + " · 被动合计 " + outcome.TotalPassivePctBps + " bps"
                + (outcome.PassiveKind.Length == 0 ? string.Empty : " · " + outcome.PassiveKind);
        }

        static List<HeroCodexEntryView> ParseEntries(string json)
        {
            var rows = new List<HeroCodexEntryView>();
            var start = json.IndexOf("\"entries\"", StringComparison.Ordinal);
            if (start < 0)
            {
                return rows;
            }

            start = json.IndexOf('[', start);
            var end = start >= 0 ? json.IndexOf(']', start) : -1;
            if (start < 0 || end < 0)
            {
                return rows;
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
                rows.Add(new HeroCodexEntryView
                {
                    Id = Unescape(JsonField.Of(one, "id")),
                    Name = Unescape(JsonField.Of(one, "name")),
                    UnlockMainStage = ReadInt(one, "unlock_main_stage"),
                    Collected = string.Equals(JsonField.Of(one, "collected"), "true",
                        StringComparison.OrdinalIgnoreCase),
                    PassivePctBps = ReadInt(one, "passive_pct_bps"),
                });
                at = close + 1;
            }

            return rows;
        }

        static int ReadInt(string json, string key)
        {
            int value;
            return int.TryParse(JsonField.Of(json, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : 0;
        }

        static ulong ReadUlong(string json, string key)
        {
            ulong value;
            return ulong.TryParse(JsonField.Of(json, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : 0UL;
        }

        /// <summary>
        /// 解 JSON 的转义。网关把中文写成 `\u4E54\u5C0F\u6EE1` 这种形态发出来，
        /// 不解开的话九格上显示的是反斜杠加十六进制，看着像名字没进来。
        /// </summary>
        static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
            {
                return value ?? string.Empty;
            }

            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length)
                {
                    sb.Append(value[i]);
                    continue;
                }

                var next = value[i + 1];
                if (next == 'u' && i + 5 < value.Length)
                {
                    var hex = value.Substring(i + 2, 4);
                    int code;
                    if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    {
                        sb.Append((char)code);
                        i += 5;
                        continue;
                    }
                }

                switch (next)
                {
                    case 'n':
                        sb.Append('\n');
                        i++;
                        continue;
                    case 't':
                        sb.Append('\t');
                        i++;
                        continue;
                    case 'r':
                        sb.Append('\r');
                        i++;
                        continue;
                    case '"':
                        sb.Append('"');
                        i++;
                        continue;
                    case '\\':
                        sb.Append('\\');
                        i++;
                        continue;
                    case '/':
                        sb.Append('/');
                        i++;
                        continue;
                }

                sb.Append(next);
                i++;
            }

            return sb.ToString();
        }
    }
}
