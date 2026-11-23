using Npgsql;

namespace Social;

public sealed record SectState(
    bool Found, long SectId, string Name, int Level, int MemberCap, int MemberCount);

public sealed record JoinOutcome(
    bool Ok, string Reason, long SectId, int Level, int MemberCap, int MemberCount);

public sealed record CreateOutcome(
    bool Ok, string Reason, long SectId, int Level, int MemberCap, int MemberCount);

/// <summary>P2-1a2 任命 / 免职的回包事实值。业务拒绝时 seat_count=0（未生效）。</summary>
public sealed record PositionOutcome(
    bool Ok, string Reason, long SectId, string Position, int SeatCap, int SeatCount);

/// <summary>只读：某宗门各职位在名册里的已占席位（disciple 不占席位，只数人数）。</summary>
public sealed record PositionState(
    bool Found, long SectId, int Master, int ViceMaster, int Elder, int Steward, int Disciple);

public sealed record PositionRow(long CharacterId, string Position);

/// <summary>P2-1a3 传位 / 退出的回包事实值。业务拒绝时未生效数值给 0。</summary>
public sealed record LeadershipOutcome(
    bool Ok, string Reason, long SectId, long OldMaster, long NewMaster,
    int MasterCount, int ViceMasterCount, int MemberCount);

/// <summary>只读：某宗门当前掌门权威、名册席位事实值、上一次传位事务号。</summary>
public sealed record LeadershipState(
    bool Found, long SectId, long MasterCharacterId, long LastTransferOldMaster,
    int MasterCount, int ViceMasterCount, int MemberCount, string LastTransferTxn);

/// <summary>P2-1a4 升级的回包事实值。业务拒绝时未生效数值给 0。</summary>
public sealed record UpgradeOutcome(
    bool Ok, string Reason, long SectId, int OldLevel, int NewLevel, int MemberCap,
    long ConstructionBefore, long ConstructionAfter, long UpgradeCost);

/// <summary>只读：某宗门的等级 / 名册上限 / 建设 / 下一档消耗 / 幂等事务号 / 名册人数 / 升级流水条数。</summary>
public sealed record UpgradeState(
    bool Found, long SectId, int Level, int MemberCap, long Construction,
    long NextCost, string LastUpgradeTxn, int MemberCount, int UpgradeTxnCount);

// 14.8 D3e：宗门读写在 Sect/ 目录按域拆（Create / Join / Member / Tech / Position /
// Leadership / Upgrade，纯搬代码，判定与回包不变）。这个文件只留回包记录类型与类声明；
// 核对脚本仍按老路径读它，由 lib-common 的 src_agg 展开成「主文件 + 分片目录」。

/// <summary>
/// 宗门与名册的唯一写入点。只写 "社交".sect / "社交".sect_member 两张表，
/// 不 join 角色 schema，也不向战斗服下发人数。
///
/// 收人的人数判定在一笔事务里：for update 锁宗门行 → count(*) → insert。
/// 并发抢最后一个席位时，两个事务会串行在宗门行上，第二个看到已满就拒绝，
/// 库里恒定只多一个人。reader 都在回滚之前读完关掉，避免挂在连接上。
/// </summary>
public sealed partial class SectRepository(NpgsqlDataSource dataSource, SectTable sectTable)
{
}
