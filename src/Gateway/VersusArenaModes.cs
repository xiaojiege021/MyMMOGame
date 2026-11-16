using System.Text.Json;

namespace Gateway;

/// <summary>
/// 网关只读竞技场模式表：用来把 mode id 映成开打人数，不把模板数值带进来。
/// 正本是 config/arena_match.json，与战斗服 P2-3a 同一张表。
/// </summary>
public sealed class VersusArenaModes
{
    readonly Dictionary<string, int> _playersByMode = new(StringComparer.OrdinalIgnoreCase);

    public bool Missing { get; private set; }

    public static VersusArenaModes MissingTable() => new() { Missing = true };

    public static VersusArenaModes Load(string path)
    {
        if (!File.Exists(path))
        {
            return MissingTable();
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (!root.TryGetProperty("modes", out var modes) || modes.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigException("arena_match.json 缺少 modes 数组");
        }

        var table = new VersusArenaModes();
        foreach (var mode in modes.EnumerateArray())
        {
            if (!mode.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idEl.GetString()?.Trim() ?? "";
            if (id.Length == 0)
            {
                continue;
            }

            var teamSize = mode.TryGetProperty("team_size", out var ts) && ts.TryGetInt32(out var tsv)
                ? tsv
                : 0;
            var teams = mode.TryGetProperty("teams", out var tm) && tm.TryGetInt32(out var tmv)
                ? tmv
                : 0;
            if (teamSize < 1 || teams < 2)
            {
                continue;
            }

            var players = teamSize * teams;
            if (players < 2 || players > 100)
            {
                continue;
            }

            table._playersByMode[id] = players;
        }

        if (table._playersByMode.Count == 0)
        {
            throw new ConfigException("arena_match.json 没有可用模式");
        }

        return table;
    }

    public bool TryPlayers(string modeId, out int players)
    {
        players = 0;
        return _playersByMode.TryGetValue(modeId.Trim(), out players);
    }
}