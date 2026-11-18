using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Character;

/// <summary>
/// 一次名字判定的结果。<see cref="Normalized"/> 是写库用的 name_key，
/// <see cref="Display"/> 是展示用原名（去掉首尾空白后的原样）。
/// 拒绝时两者都为空串、<see cref="Reason"/> 是拒绝码。
/// </summary>
public sealed record NameVerdict(bool Ok, string Reason, string Normalized, string Display)
{
    public static NameVerdict Reject(string reason) => new(false, reason, "", "");
}

/// <summary>
/// Q12/K3：角色名规则的唯一正本（`config/name_rules.json` + 敏感词表文件）。
///
/// 判重口径是代码能力、不是内容：NFKC 归一 → 去首尾空白 → 转大写。
/// 只此一种实现，配置里没有「用哪种归一」的开关；库里的 name_key 也由这里算好再写，
/// SQL 只有 lower/btrim、做不出 NFKC，写第二套就会漂。
///
/// 敏感词表单独一个文件（每行一词、# 注释、空行忽略），表里每条也先归一，
/// 判定是「归一后的名字包含归一后的词」。表为空或文件缺失时加载期就失败：
/// 不静默退化成「谁都能起名」。
/// </summary>
public sealed class NameRulesTable
{
    /// <summary>名字没填（去空白后为空）。</summary>
    public const string ReasonMissing = "name_missing";

    /// <summary>归一后短于 min_length。</summary>
    public const string ReasonTooShort = "name_too_short";

    /// <summary>归一后长于 max_length。</summary>
    public const string ReasonTooLong = "name_too_long";

    /// <summary>命中敏感词表。</summary>
    public const string ReasonBanned = "name_banned";

    /// <summary>含空白或控制字符：名字是单段可见文本。</summary>
    public const string ReasonInvalid = "name_invalid";

    /// <summary>规则里关了改名。</summary>
    public const string ReasonRenameDisabled = "rename_disabled";

    readonly HashSet<string> _banned;

    NameRulesTable(
        int version, int minLength, int maxLength, bool globalUnique, bool allowRename,
        string renameItemId, string bannedWordsFile, HashSet<string> banned)
    {
        Version = version;
        MinLength = minLength;
        MaxLength = maxLength;
        GlobalUnique = globalUnique;
        AllowRename = allowRename;
        RenameItemId = renameItemId;
        BannedWordsFile = bannedWordsFile;
        _banned = banned;
    }

    public int Version { get; }
    public int MinLength { get; }
    public int MaxLength { get; }
    public bool GlobalUnique { get; }
    public bool AllowRename { get; }
    public string RenameItemId { get; }
    public string BannedWordsFile { get; }

    /// <summary>加载进来的敏感词条数（只给启动日志与只读入口看）。</summary>
    public int BannedCount => _banned.Count;

    public static NameRulesTable Load(string path, ItemTable items)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException("找不到名字规则表：" + path);
        }

        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(File.ReadAllText(path), JsonOptions)
                ?? throw new ConfigException("名字规则表是空的：" + path);
        }
        catch (JsonException ex)
        {
            throw new ConfigException("名字规则表不是合法 JSON：" + ex.Message);
        }

        var schema = (wire.Schema ?? "").Trim();
        if (schema != "name-rules/2")
        {
            throw new ConfigException("名字规则表的 schema 只认 name-rules/2，现在是 " + schema);
        }

        var min = wire.MinLength ?? 0;
        var max = wire.MaxLength ?? 0;
        // 上限 24 是「防手滑写成 240」的闸，不是玩法数值；方案没写长度，长度本身在配置里。
        if (min < 1 || max > 24 || min >= max)
        {
            throw new ConfigException("名字长度要在 1 <= min < max <= 24，现在是 " + min + " / " + max);
        }

        // 这一版只做全局唯一；写 false 会与本卡的口径矛盾，宁可起不来。
        if (wire.GlobalUnique != true)
        {
            throw new ConfigException("name_rules.json 的 global_unique 必须是 true（Q12 只实现跨账号唯一）");
        }

        var renameItemId = (wire.RenameItemId ?? "").Trim();
        if (renameItemId.Length == 0)
        {
            throw new ConfigException("name_rules.json 缺 rename_item_id");
        }

        if (items.Find(renameItemId) is null)
        {
            throw new ConfigException("改名卡 " + renameItemId + " 不在 items.json 里");
        }

        var bannedFile = (wire.BannedWordsFile ?? "").Trim();
        if (bannedFile.Length == 0)
        {
            throw new ConfigException("name_rules.json 缺 banned_words_file");
        }

        if (!File.Exists(bannedFile))
        {
            throw new ConfigException("找不到敏感词表：" + bannedFile);
        }

        var banned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(bannedFile))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
            {
                continue;
            }

            var key = Normalize(text);
            if (key.Length > 0)
            {
                banned.Add(key);
            }
        }

        if (banned.Count == 0)
        {
            // 空表等于「没有敏感词」，但更可能是路径写错或文件被清空——那是配置错误。
            throw new ConfigException("敏感词表里一条词都没有：" + bannedFile);
        }

        return new NameRulesTable(
            wire.Version ?? 0, min, max, true, wire.AllowRename ?? true,
            renameItemId, bannedFile, banned);
    }

    /// <summary>
    /// 归一：NFKC → 去首尾空白 → 转大写。写库的 name_key 与敏感词比对都用它。
    /// 全角「ＡＢＣ」与半角「abc」因此落到同一个 key 上，中文不受影响。
    /// </summary>
    public static string Normalize(string raw)
    {
        var text = (raw ?? "").Normalize(NormalizationForm.FormKC).Trim();
        return text.ToUpperInvariant();
    }

    /// <summary>判一个名字：长度、可见字符、敏感词。不做唯一性（那要问库）。</summary>
    public NameVerdict Check(string raw)
    {
        var display = (raw ?? "").Normalize(NormalizationForm.FormKC).Trim();
        if (display.Length == 0)
        {
            return NameVerdict.Reject(ReasonMissing);
        }

        foreach (var c in display)
        {
            // 名字是单段可见文本：中间的空格、制表符、控制字符一律不收。
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return NameVerdict.Reject(ReasonInvalid);
            }
        }

        // 长度按「人看到的字」算：中文一字、字母一字，组合字符算一个。
        var length = new StringInfo(display).LengthInTextElements;
        if (length < MinLength)
        {
            return NameVerdict.Reject(ReasonTooShort);
        }

        if (length > MaxLength)
        {
            return NameVerdict.Reject(ReasonTooLong);
        }

        var key = Normalize(display);
        foreach (var word in _banned)
        {
            if (key.Contains(word, StringComparison.Ordinal))
            {
                return NameVerdict.Reject(ReasonBanned);
            }
        }

        return new NameVerdict(true, "", key, display);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed record Wire
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; init; }

        [JsonPropertyName("version")]
        public int? Version { get; init; }

        [JsonPropertyName("min_length")]
        public int? MinLength { get; init; }

        [JsonPropertyName("max_length")]
        public int? MaxLength { get; init; }

        [JsonPropertyName("global_unique")]
        public bool? GlobalUnique { get; init; }

        [JsonPropertyName("allow_rename")]
        public bool? AllowRename { get; init; }

        [JsonPropertyName("rename_item_id")]
        public string? RenameItemId { get; init; }

        [JsonPropertyName("banned_words_file")]
        public string? BannedWordsFile { get; init; }
    }
}
