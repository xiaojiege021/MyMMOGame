using System.Text.Json;
using System.Text.Json.Serialization;

namespace Global;

/// <summary>一季：编号 + 这一季指向的秘境目标（config/dungeons.json 里的副本编号）。</summary>
public sealed record SeasonRow(string Id, string TargetDungeon);

/// <summary>
/// 赛季表（方案 2 章、开发架构 11 节 P3-4 / 13 节 P3-4a1 / 14 节第 67 行）。
/// 赛季归全局服务，只回答「当前是哪一季、目标是什么、上一季目标是什么」。
/// 完成标准「新赛季目标与上一季不同」在加载期就是硬约束：相邻两季目标相同直接起不来。
/// 目标必须在副本表里真实存在；目标副本若写了 realm，那个境界开关必须已开（P3-1a1）。
/// 表里不许出现奖励 / 通行证 / 商城 / 段位一类字段，也不写库、不发奖、不改角色数据。
/// 季数、编号、目标名字全从配置读，代码里不写第二套数字或编号。
/// </summary>
public sealed class SeasonTable
{
    public const string WrittenSchema = "season-table/1";

    // 白名单：这份表只许长「哪一季 + 哪一处秘境」。P3-4a2 通行证、P3-4b 月叠、
    // 赛季奖励与排行都不在这张表里，多一个键就在加载期拒绝，不从配置里悄悄长出来。
    static readonly string[] AllowedTopKeys =
        ["schema", "version", "note", "pending", "current", "seasons"];
    static readonly string[] AllowedRowKeys = ["id", "target_dungeon", "label", "note"];
    static readonly string[] ForbiddenWords =
        ["reward", "pass", "shop", "gold", "yuanbao", "stone", "item", "material", "equip",
         "enhance", "progress", "level", "rank", "leaderboard", "gacha", "drop", "price",
         "count", "bonus", "balance", "ticket", "exp"];

    readonly List<SeasonRow> _rows;

    public bool Found { get; }
    public int Version { get; }
    public int Count => _rows.Count;
    public IReadOnlyList<SeasonRow> Rows => _rows;
    public string CurrentSeason { get; }
    public string CurrentTarget { get; }
    public string PreviousSeason { get; }
    public string PreviousTarget { get; }

    SeasonTable(bool found, int version, List<SeasonRow> rows, string currentId, int currentIndex)
    {
        Found = found;
        Version = version;
        _rows = rows;
        var hasCurrent = found && currentIndex >= 0 && currentIndex < rows.Count;
        CurrentSeason = hasCurrent ? currentId : "";
        CurrentTarget = hasCurrent ? rows[currentIndex].TargetDungeon : "";
        var previousIndex = currentIndex - 1;
        PreviousSeason = hasCurrent && previousIndex >= 0 ? rows[previousIndex].Id : "";
        PreviousTarget = hasCurrent && previousIndex >= 0 ? rows[previousIndex].TargetDungeon : "";
    }

    /// <summary>老卡的临时配置目录没有 season.json 时用这个：只读回包 found=false，服务照常起。</summary>
    public static SeasonTable Missing() => new(false, 0, [], "", -1);

    /// <summary>
    /// 与全局服务真实启动共用同一个校验链。dungeons / realms 是同一份配置目录下的文本；
    /// 只读入口 --season-table 默认读产品正本，--season-table-path 可以把三份都换成临时表。
    /// </summary>
    public static SeasonTable Load(string seasonText, string dungeonsText, string realmsText)
    {
        JsonDocument season;
        try
        {
            season = JsonDocument.Parse(seasonText);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("赛季表不是合法 JSON：" + ex.Message);
        }

        using (season)
        {
            var root = season.RootElement;
            foreach (var key in RequiredTopKeys)
            {
                if (!root.TryGetProperty(key, out _))
                {
                    throw new ConfigException("season.json 缺字段 " + key);
                }
            }

            RejectUnknownKeys(root, AllowedTopKeys, "顶层");
            ScanForbiddenWords(root, "顶层");

            Wire wire;
            try
            {
                wire = JsonSerializer.Deserialize<Wire>(seasonText, JsonOptions)
                    ?? throw new ConfigException("赛季表是空的");
            }
            catch (JsonException ex)
            {
                throw new ConfigException("赛季表字段类型不对：" + ex.Message);
            }

            var schema = (wire.Schema ?? "").Trim();
            if (!string.Equals(schema, WrittenSchema, StringComparison.Ordinal))
            {
                throw new ConfigException("season.json 的 schema 必须是 " + WrittenSchema);
            }

            var version = wire.Version ?? 0;
            if (version <= 0)
            {
                throw new ConfigException("season.json 的 version 必须大于 0");
            }

            if (wire.Seasons is null || wire.Seasons.Count < 2)
            {
                throw new ConfigException("赛季表至少要两季才能换一处秘境目标");
            }

            var current = (wire.Current ?? "").Trim();
            if (current.Length == 0)
            {
                throw new ConfigException("season.json 的 current 不能为空");
            }

            var dungeons = ReadDungeons(dungeonsText);
            var openRealms = ReadOpenRealms(realmsText);

            var rowsElement = root.GetProperty("seasons");
            if (rowsElement.ValueKind != JsonValueKind.Array
                || rowsElement.GetArrayLength() != wire.Seasons.Count)
            {
                throw new ConfigException("season.json 的 seasons 必须是条目齐全的数组");
            }

            var rows = new List<SeasonRow>(wire.Seasons.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var currentIndex = -1;
            for (var i = 0; i < wire.Seasons.Count; i++)
            {
                var wireRow = wire.Seasons[i];
                var element = rowsElement[i];
                RejectUnknownKeys(element, AllowedRowKeys, "第 " + (i + 1) + " 季");
                ScanForbiddenWords(element, "第 " + (i + 1) + " 季");

                var id = (wireRow.Id ?? "").Trim();
                var target = (wireRow.TargetDungeon ?? "").Trim();
                if (id.Length == 0 || target.Length == 0)
                {
                    throw new ConfigException("赛季表第 " + (i + 1) + " 季的 id / target_dungeon 不能为空");
                }

                if (!seen.Add(id))
                {
                    throw new ConfigException("赛季编号重复：" + id);
                }

                // 目标必须是副本表里真实存在的一处秘境；带 realm 的还要看版本开关。
                if (!dungeons.TryGetValue(target, out var requiredRealm))
                {
                    throw new ConfigException("赛季目标不是副本表里的秘境：" + target);
                }

                if (requiredRealm.Length > 0 && !openRealms.Contains(requiredRealm))
                {
                    throw new ConfigException("赛季目标的境界版本还没开：" + target + " realm=" + requiredRealm);
                }

                // 完成标准「新赛季目标与上一季不同」：加载期就挡住，不靠运行期碰运气。
                if (i > 0 && string.Equals(rows[i - 1].TargetDungeon, target, StringComparison.Ordinal))
                {
                    throw new ConfigException("相邻两季的秘境目标相同：" + target);
                }

                if (string.Equals(id, current, StringComparison.Ordinal))
                {
                    currentIndex = i;
                }

                rows.Add(new SeasonRow(id, target));
            }

            if (currentIndex < 0)
            {
                throw new ConfigException("season.json 的 current 不在 seasons 里：" + current);
            }

            return new SeasonTable(true, version, rows, current, currentIndex);
        }
    }

    static readonly string[] RequiredTopKeys = ["schema", "version", "current", "seasons"];

    /// <summary>副本表里 id → 需要的境界（没写 realm 的旧副本是空串）。</summary>
    static Dictionary<string, string> ReadDungeons(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("dungeons.json 不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!document.RootElement.TryGetProperty("dungeons", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("dungeons.json 缺 dungeons 数组");
            }

            foreach (var row in list.EnumerateArray())
            {
                var id = row.TryGetProperty("id", out var idValue) ? (idValue.GetString() ?? "").Trim() : "";
                if (id.Length == 0)
                {
                    throw new ConfigException("dungeons.json 里有副本没写 id");
                }

                var realm = row.TryGetProperty("realm", out var realmValue)
                    ? (realmValue.GetString() ?? "").Trim() : "";
                result[id] = realm;
            }

            if (result.Count == 0)
            {
                throw new ConfigException("dungeons.json 的 dungeons 不能为空");
            }

            return result;
        }
    }

    /// <summary>
    /// 已经开放的大境界集合：首发段（launch_until 以前，含）恒开放，
    /// 元婴起只看 realms.json 的 switches[].open。赛季目标不能越过 P3-1a1 的开关。
    /// </summary>
    static HashSet<string> ReadOpenRealms(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("realms.json 不是合法 JSON：" + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (!root.TryGetProperty("realms", out var order) || order.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("realms.json 缺 realms 顺序表");
            }

            var launchUntil = root.TryGetProperty("launch_until", out var launch)
                ? (launch.GetString() ?? "").Trim() : "";
            var rank = 0;
            foreach (var name in order.EnumerateArray())
            {
                rank++;
                var value = (name.GetString() ?? "").Trim();
                if (value.Length == 0)
                {
                    throw new ConfigException("realms.json 的顺序表里有空名字");
                }

                result.Add(value);
                if (string.Equals(value, launchUntil, StringComparison.Ordinal))
                {
                    // 首发段及以前全开放；rank 之后的段要从这个集合里拿掉，交给 switches 决定。
                    for (var i = rank; i < order.GetArrayLength(); i++)
                    {
                        var later = (order[i].GetString() ?? "").Trim();
                        result.Remove(later);
                    }

                    break;
                }
            }

            if (root.TryGetProperty("switches", out var switches)
                && switches.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in switches.EnumerateArray())
                {
                    var realm = row.TryGetProperty("realm", out var r) ? (r.GetString() ?? "").Trim() : "";
                    var open = row.TryGetProperty("open", out var o)
                        && o.ValueKind == JsonValueKind.True;
                    if (realm.Length == 0)
                    {
                        throw new ConfigException("realms.json 的 switches 里有空境界");
                    }

                    if (open)
                    {
                        result.Add(realm);
                    }
                    else
                    {
                        result.Remove(realm);
                    }
                }
            }

            return result;
        }
    }

    static void RejectUnknownKeys(JsonElement row, string[] allowed, string where)
    {
        foreach (var property in row.EnumerateObject())
        {
            var key = property.Name;
            var ok = false;
            foreach (var candidate in allowed)
            {
                if (string.Equals(key, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    ok = true;
                    break;
                }
            }

            if (!ok)
            {
                throw new ConfigException("season.json " + where + " 不许出现 " + key);
            }
        }
    }

    static void ScanForbiddenWords(JsonElement row, string where)
    {
        foreach (var property in row.EnumerateObject())
        {
            foreach (var word in ForbiddenWords)
            {
                if (property.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConfigException("season.json " + where + " 不许出现 " + property.Name);
                }
            }
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed class Wire
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        [JsonPropertyName("version")]
        public int? Version { get; init; }

        [JsonPropertyName("current")]
        public string? Current { get; init; }

        [JsonPropertyName("seasons")]
        public List<RowWire>? Seasons { get; init; }
    }

    sealed class RowWire
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("target_dungeon")]
        public string? TargetDungeon { get; init; }
    }
}

/// <summary>
/// 全局服务持有的赛季表快照。E4 / 技术架构 7 节：新请求读到新表，换季不用重启进程，
/// 也不用改角色库。目录里没有 season.json 就降级成 Missing，老卡的临时配置照常起服；
/// 表写坏了保留上一份好的并打一行 season_config_error=，不让一份坏配置把全局服务拖垮。
/// </summary>
public sealed class SeasonCatalog : IDisposable
{
    readonly ConfigCatalog _catalog;
    readonly string _file;
    readonly object _gate = new();
    SeasonTable _good = SeasonTable.Missing();
    bool _reported;

    public SeasonCatalog(string configDirectory, string seasonFile)
    {
        _file = seasonFile;
        _catalog = new ConfigCatalog(configDirectory);
        try
        {
            _catalog.Watch();
        }
        catch (IOException)
        {
            // 目录不可监视时仍能按快照读，重启或手工 Reload 后照样换季。
        }

        // 启动时表就是坏的，也没有「上一份好表」可留：降级成 missing 起服，
        // 不让一份可选配置把拍卖 / 寄售这些老链路一起拖垮。
        try
        {
            _good = Read();
        }
        catch (ConfigException ex)
        {
            Console.WriteLine("season_config_error=" + ex.Message);
            _good = SeasonTable.Missing();
            _reported = true;
        }
    }

    public SeasonTable Current()
    {
        try
        {
            var table = Read();
            if (table.Found)
            {
                lock (_gate)
                {
                    _good = table;
                    _reported = false;
                }
            }

            return table;
        }
        catch (ConfigException ex)
        {
            Report("season_config_error=" + ex.Message);
            lock (_gate)
            {
                return _good;
            }
        }
    }

    SeasonTable Read()
    {
        var snapshot = _catalog.Enter();
        if (!snapshot.Documents.TryGetValue(_file, out var seasonText))
        {
            return SeasonTable.Missing();
        }

        if (!snapshot.Documents.TryGetValue("dungeons.json", out var dungeonsText)
            || !snapshot.Documents.TryGetValue("realms.json", out var realmsText))
        {
            throw new ConfigException("赛季表所在目录缺 dungeons.json / realms.json，无法核对目标与境界开关");
        }

        return SeasonTable.Load(seasonText, dungeonsText, realmsText);
    }

    void Report(string line)
    {
        lock (_gate)
        {
            if (_reported)
            {
                return;
            }

            _reported = true;
            Console.WriteLine(line);
        }
    }

    public void Dispose() => _catalog.Dispose();
}
