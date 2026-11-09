using System.Text.Json;
using System.Text.Json.Serialization;

namespace Battle;

/// <summary>一个角色的等级与当前等级已有经验。P0 只有复活会动它。</summary>
public sealed record CharacterState
{
    public string Schema { get; init; } = "character/1";
    public long Level { get; init; } = 1;
    public long Exp { get; init; }
}

/// <summary>
/// 角色数据的读与写。P0 暂放在战斗进程旁的一张表（技术架构的口径），
/// 接口按角色服的样子留好：只按角色编号取和存，不暴露存在哪。
/// P1-1 的角色服起来以后，实现换成那一笔事务，调用方不改。
/// </summary>
public interface ICharacterStore
{
    CharacterState Get(ulong characterId);

    void Save(ulong characterId, CharacterState state);
}

/// <summary>
/// 文件实现：一个角色一个 JSON，写在 character_dir 下面。
/// 和结算记录同一种写法——先写临时文件再 rename，进程中途挂掉不会留半个人。
/// </summary>
public sealed class FileCharacterStore(string dir) : ICharacterStore
{
    public string Dir { get; } = Path.GetFullPath(dir);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CharacterState Get(ulong characterId)
    {
        var path = Path.Combine(Dir, characterId + ".json");
        if (!File.Exists(path))
        {
            // 没写过就当作一级、当前级经验是零：复活扣经验的那条规则里
            // 「当前级经验是零时不扣」正好落在这里。
            return new CharacterState();
        }

        try
        {
            return JsonSerializer.Deserialize<CharacterState>(File.ReadAllText(path), JsonOptions)
                   ?? new CharacterState();
        }
        catch (JsonException)
        {
            // 读不了的文件当没这个人，不让一场战斗因为一张坏表起不来。
            return new CharacterState();
        }
    }

    public void Save(ulong characterId, CharacterState state)
    {
        Directory.CreateDirectory(Dir);
        var final = Path.Combine(Dir, characterId + ".json");
        var tmp = final + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmp, final, overwrite: true);
    }
}
