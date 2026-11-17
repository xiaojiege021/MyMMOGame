using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Mmo.Net;

namespace WorldEntryProbe
{
    /// <summary>
    /// U4 的拆装探针。它编进的是客户端那份 `Assets/Net/WorldEntry.cs`，
    /// 所以验的就是随包发出去的那份拆装，不另抄一份解析器。
    ///
    /// 用法（都是离线，不发网络请求；真网关的回包由 check-u4.sh 用 curl 取）：
    ///   --enter-body &lt;character_id&gt; &lt;region_id&gt;
    ///   --enter &lt;status&gt; &lt;json&gt;
    ///   --regions &lt;status&gt; &lt;json&gt;
    ///
    /// 拆装本身一律返回 0：判定交给调用方按 kind 做，探针不替它下结论。
    /// </summary>
    internal static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("usage=--enter-body <id> <region> | --enter <status> <json> | --regions <status> <json>");
                return 2;
            }

            switch (args[0])
            {
                case "--enter-body":
                {
                    if (args.Length < 3)
                    {
                        Console.WriteLine("usage=--enter-body <character_id> <region_id>");
                        return 2;
                    }

                    ulong.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id);
                    Console.WriteLine("body=" + WorldEntry.EnterBody(id, args[2]));
                    return 0;
                }

                case "--enter":
                {
                    if (args.Length < 3)
                    {
                        Console.WriteLine("usage=--enter <status> <json>");
                        return 2;
                    }

                    int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var status);
                    var outcome = WorldEntry.ParseEnter(status, ReadJson(args[2]));
                    Console.WriteLine("kind=" + outcome.Kind);
                    Console.WriteLine("entered=" + (outcome.Entered ? "true" : "false"));
                    Console.WriteLine("region=" + outcome.RegionId);
                    Console.WriteLine("line=" + outcome.LineNo);
                    Console.WriteLine("size=" + outcome.LineSize);
                    Console.WriteLine("cap=" + outcome.LineCapacity);
                    Console.WriteLine("reason=" + outcome.Reason);
                    Console.WriteLine("neighbors=" + outcome.Neighbors.Length);
                    Console.WriteLine("dialogues=" + outcome.DialogueIds.Length);
                    Console.WriteLine("error=" + outcome.Error);
                    return 0;
                }

                case "--regions":
                {
                    if (args.Length < 3)
                    {
                        Console.WriteLine("usage=--regions <status> <json>");
                        return 2;
                    }

                    int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var status);
                    var outcome = WorldEntry.ParseRegions(status, ReadJson(args[2]));
                    Console.WriteLine("kind=" + outcome.Kind);
                    Console.WriteLine("version=" + outcome.Version);
                    Console.WriteLine("regions=" + (outcome.Regions == null ? 0 : outcome.Regions.Length));
                    // N14：开放状态与原因都由回包带进来，探针只念不改。
                    Console.WriteLine("regions_open=" + CountOpen(outcome));
                    Console.WriteLine("locked=" + LockedText(outcome));
                    Console.WriteLine("speakers=" + (outcome.Speakers == null ? 0 : outcome.Speakers.Length));
                    Console.WriteLine("factions=" + (outcome.Factions == null ? 0 : outcome.Factions.Length));
                    Console.WriteLine("error=" + outcome.Error);
                    return 0;
                }

                default:
                    Console.WriteLine("usage=unknown " + args[0]);
                    return 2;
            }
        }

        /// <summary>开着的地表有几块。回包没给就 0，不拿配置补。</summary>
        static int CountOpen(WorldRegionsOutcome outcome)
        {
            if (outcome.Regions == null)
            {
                return 0;
            }

            var open = 0;
            for (var i = 0; i < outcome.Regions.Length; i++)
            {
                if (outcome.Regions[i].Open)
                {
                    open++;
                }
            }

            return open;
        }

        /// <summary>关着的地表与原因，一条一行拼成串（`旧天城区:region_route_mismatch`）。</summary>
        static string LockedText(WorldRegionsOutcome outcome)
        {
            if (outcome.Regions == null)
            {
                return "none";
            }

            var parts = new List<string>();
            for (var i = 0; i < outcome.Regions.Length; i++)
            {
                var row = outcome.Regions[i];
                if (!row.Open)
                {
                    parts.Add(row.Id + ":" + (row.Reason.Length == 0 ? "none" : row.Reason));
                }
            }

            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }

        /// <summary>回包正文可以直接给，也可以写 `@文件名` 从文件读——回包里带中文与引号，
        /// 走命令行参数容易被 shell 吃掉一层。</summary>
        static string ReadJson(string arg)
        {
            if (arg != null && arg.StartsWith("@", StringComparison.Ordinal))
            {
                var path = arg.Substring(1);
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }

            return arg ?? string.Empty;
        }
    }
}
