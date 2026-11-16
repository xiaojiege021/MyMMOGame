using Npgsql;

namespace Social;

/// <summary>N11 种植可选装配：表缺失时三个 RPC 回 plant_unavailable。</summary>
public sealed class HomesteadPlantRuntime : IDisposable
{
    readonly HomesteadHarvestGrant? harvestGrant;

    public HomesteadPlantTable? Table { get; }
    public HomesteadPlantRepository? Repository { get; }
    public bool Available => Table is not null && Repository is not null;

    public HomesteadPlantRuntime(
        HomesteadPlantTable? table, NpgsqlDataSource? dataSource, string characterUpstream)
    {
        Table = table;
        if (table is null || dataSource is null)
        {
            return;
        }

        harvestGrant = new HomesteadHarvestGrant(characterUpstream);
        Repository = new HomesteadPlantRepository(dataSource, table, harvestGrant);
    }

    public void Dispose() => harvestGrant?.Dispose();
}