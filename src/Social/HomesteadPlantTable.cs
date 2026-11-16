using System.Text.Json;
using System.Text.RegularExpressions;

namespace Social;

public sealed record HomesteadCropDef(
    string Id, string Name, int GrowSeconds, long YieldHomeCoin, bool Pending);

/// <summary>N11 家园种植内容正本（config/homestead_plant.json）。产量与时长只在这里。</summary>
public sealed class HomesteadPlantTable
{
    public const string Schema = "homestead-plant/1";

    static readonly HashSet<string> TopKeys = new(StringComparer.Ordinal)
    {
        "schema", "version", "pending", "note", "slots", "crops",
    };

    static readonly HashSet<string> CropKeys = new(StringComparer.Ordinal)
    {
        "id", "name", "grow_seconds", "yield_home_coin", "pending", "note",
    };

    static readonly Regex SlotIdPattern = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    HomesteadPlantTable(
        IReadOnlyList<string> slots, IReadOnlyDictionary<string, HomesteadCropDef> crops, bool pending)
    {
        Slots = slots;
        Crops = crops;
        Pending = pending;
    }

    public bool Pending { get; }
    public IReadOnlyList<string> Slots { get; }
    public IReadOnlyDictionary<string, HomesteadCropDef> Crops { get; }

    public bool HasSlot(string id) => Slots.Contains(id, StringComparer.Ordinal);

    public HomesteadCropDef? FindCrop(string id) =>
        Crops.TryGetValue(id, out var c) ? c : null;

    public static HomesteadPlantTable? LoadIfPresent(string path)
        => File.Exists(path) ? Load(path) : null;

    public static HomesteadPlantTable Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.GetProperty("schema").GetString() != Schema)
        {
            throw new ConfigException("homestead_plant.json schema 必须是 " + Schema);
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (!TopKeys.Contains(prop.Name))
            {
                throw new ConfigException("homestead_plant.json 不允许键：" + prop.Name);
            }
        }

        var slots = new List<string>();
        foreach (var slot in root.GetProperty("slots").EnumerateArray())
        {
            var id = (slot.ValueKind == JsonValueKind.String ? slot.GetString() : null) ?? "";
            id = id.Trim();
            if (!SlotIdPattern.IsMatch(id))
            {
                throw new ConfigException("种植格位编号不合法：" + id);
            }

            if (slots.Contains(id, StringComparer.Ordinal))
            {
                throw new ConfigException("种植格位重复：" + id);
            }

            slots.Add(id);
        }

        if (slots.Count == 0)
        {
            throw new ConfigException("homestead_plant.json 至少要有一个种植格");
        }

        var crops = new Dictionary<string, HomesteadCropDef>(StringComparer.Ordinal);
        foreach (var crop in root.GetProperty("crops").EnumerateArray())
        {
            foreach (var prop in crop.EnumerateObject())
            {
                if (!CropKeys.Contains(prop.Name))
                {
                    throw new ConfigException("作物条目不允许键：" + prop.Name);
                }
            }

            var id = (crop.GetProperty("id").GetString() ?? "").Trim();
            if (id.Length == 0 || crops.ContainsKey(id))
            {
                throw new ConfigException("作物 id 重复或为空：" + id);
            }

            var grow = crop.GetProperty("grow_seconds").GetInt32();
            if (grow <= 0 || grow > 86400 * 30)
            {
                throw new ConfigException("grow_seconds 超出允许范围：" + id);
            }

            var yield = crop.GetProperty("yield_home_coin").GetInt64();
            if (yield <= 0 || yield > 1_000_000)
            {
                throw new ConfigException("yield_home_coin 超出允许范围：" + id);
            }

            crops[id] = new HomesteadCropDef(
                id,
                (crop.GetProperty("name").GetString() ?? id).Trim(),
                grow,
                yield,
                crop.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.True);
        }

        if (crops.Count == 0)
        {
            throw new ConfigException("homestead_plant.json 至少要有一种作物");
        }

        var pending = root.TryGetProperty("pending", out var pend) && pend.ValueKind == JsonValueKind.True;
        return new HomesteadPlantTable(slots, crops, pending);
    }
}