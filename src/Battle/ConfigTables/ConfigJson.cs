using System.Text.Json;
using System.Text.Json.Serialization;

namespace Battle;

static class ConfigJson
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static T Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
                   ?? throw new ConfigException($"{path}：内容为空");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{path}：JSON 读不了，{ex.Message}");
        }
        catch (IOException ex)
        {
            throw new ConfigException($"{path}：文件读不了，{ex.Message}");
        }
    }
}

