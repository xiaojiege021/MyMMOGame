using Npgsql;

namespace Social;

/// <summary>
/// C3 的装配边界：可选内容表 + 社交库写入点 + 角色服只读客户端，三样一起注册进
/// 社交服 DI，也一起给 Program 的连库核对入口用。
///
/// 表缺失（老卡的临时配置目录没有 homestead.json）时 Table=null、Repository=null，
/// 三个新 RPC 一律回 homestead_unavailable，宗门那些老 RPC 一个字不受影响。
/// 角色服上游没起来时摆放回 inventory_unavailable，不回假成功、不写社交库。
/// </summary>
public sealed class HomesteadRuntime : IDisposable
{
    readonly HomesteadInventory? inventory;

    public HomesteadTable? Table { get; }

    public HomesteadRepository? Repository { get; }

    public bool Available => Table is not null && Repository is not null;

    public HomesteadRuntime(
        HomesteadTable? table, NpgsqlDataSource? dataSource, string characterUpstream)
    {
        Table = table;
        if (table is null || dataSource is null)
        {
            return;
        }

        inventory = new HomesteadInventory(characterUpstream);
        Repository = new HomesteadRepository(dataSource, table, inventory);
    }

    public void Dispose() => inventory?.Dispose();
}
