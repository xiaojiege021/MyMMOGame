namespace Social;

/// <summary>
/// 社交服启动参数面（14.8 D3h）。字段名与拆分前 Program.cs 的局部变量同名，
/// 搬进来的解析与只读入口因此一个字都不用改：命中哪一类入口、退出码多少，
/// 仍按拆分前的顺序判。只给核对用的口子也不新增语义。
/// </summary>
public sealed partial class SocialCli
{

    // 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
    public string configPath = "config/social.json";
    public string? sectTablePath = null;
    public string sqlPath = "db/social/social.sql";
    public bool migrate = false;
    public bool sectConfig = false;
    public bool sectList = false;
    public long sectStateOf = 0;
    public long sectMembersOf = 0;
    public long sectRosterOf = 0;
    public long sectSetLevelOf = 0;
    public int sectSetLevelTo = 0;
    public bool sectPositionConfig = false;
    public bool sectPositionList = false;
    public long sectPositionStateOf = 0;
    public long sectPositionMembersOf = 0;
    // P2-1a3：四个只读入口，与真实启动共用 SocialLoader.LoadAll 同一组参数。
    public bool sectLeadershipConfig = false;
    public long sectLeadershipStateOf = 0;
    public long sectLeadershipMembersOf = 0;
    public long sectLeaveStateOf = 0;
    // P2-1a4：升级只读入口 + 仅核对用的建设赋值，同样与启动共用 LoadAll 同一组参数。
    public bool sectUpgradeConfig = false;
    // Q4：宗门任务只读入口 + 日历 / 任务表路径覆盖（坏表核对用）。
    public string? calendarTablePath = null;
    public string? sectTaskTablePath = null;
    public bool sectTaskConfig = false;
    public long sectTaskStateOf = 0;
    public long sectTaskStateCharacter = 0;
    public long sectTaskLedgerOf = 0;
    public DateTimeOffset? sectTaskAt = null;
    public string[]? sectTaskComplete = null;
    public long sectUpgradeStateOf = 0;
    public long sectUpgradeMembersOf = 0;
    public long sectConstructionSetOf = 0;
    public long sectConstructionSetTo = -1;
    // P3-3a3：宗门科技等级只读入口 + 仅核对用的写入口（方案没写定科技产出规则）。
    public long sectTechStateOf = 0;
    public long sectTechSetOf = 0;
    public int sectTechSetTo = -1;
    // C3 洞府摆放：只读配置口、连库只读口、两个只给核对用的固定事务号写入口。
    // 可选表默认跟 --config 所在目录走（老卡的临时配置目录没有它 → 缺表降级）；
    // --homestead-table 指向坏表时，与真实启动走同一份硬校验。
    public string? homesteadTablePath = null;
    public bool homesteadConfig = false;
    public long homesteadStateOf = 0;
    public string[]? homesteadPlace = null;
    public string[]? homesteadRemove = null;
    public string? homesteadPlantTablePath = null;
    // Q7：队伍只读入口。--party-config 打正本，--party-state 跑内存状态机自检（都不连库），
    // --party-table 指向坏表时与真实启动走同一份硬校验。
    public string? partyTablePath = null;
    public bool partyConfig = false;
    public bool partyState = false;
    // C3：可选内容表的默认路径跟 --config 所在目录走。老卡的临时配置目录里
    // 没有 homestead.json，社交服照常起来（homestead=missing）；产品正本在
    // config/ 目录里，与 config/social.json 同目录。显式 --homestead-table 优先。
    public string homesteadPath => homesteadTablePath
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "homestead.json");
    public string homesteadPlantPath => homesteadPlantTablePath
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "homestead_plant.json");

    /// <summary>解析命令行；0 = 继续往下走，64 = 未知参数（与拆分前一致）。</summary>
    public int Parse(string[] args)
    {
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--config" when i + 1 < args.Length:
                configPath = args[++i];
                break;
            case "--sect-table" when i + 1 < args.Length:
                sectTablePath = args[++i];
                break;
            // Q4：日历 / 任务表的临时路径，与真实启动共用同一份 Load。
            case "--calendar-table" when i + 1 < args.Length:
                calendarTablePath = args[++i];
                break;
            case "--sect-task-table" when i + 1 < args.Length:
                sectTaskTablePath = args[++i];
                break;
            case "--sql" when i + 1 < args.Length:
                sqlPath = args[++i];
                break;
            case "--migrate":
                migrate = true;
                break;
            case "--sect-config":
                sectConfig = true;
                break;
            case "--sect-list":
                sectList = true;
                break;
            case "--sect-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectStateOf);
                break;
            case "--sect-members" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectMembersOf);
                break;
            // N2：名册逐条只读，不写库。
            case "--sect-roster" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectRosterOf);
                break;
            // 只给核对用：把某个宗门的 level 改到指定档，验上限切换，不做升级逻辑。
            case "--sect-set-level" when i + 2 < args.Length:
                long.TryParse(args[++i], out sectSetLevelOf);
                int.TryParse(args[++i], out sectSetLevelTo);
                break;
            // P2-1a2：四个只读入口，与真实启动共用 SocialLoader.LoadAll 同一组参数。
            case "--sect-position-config":
                sectPositionConfig = true;
                break;
            case "--sect-position-list":
                sectPositionList = true;
                break;
            case "--sect-position-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectPositionStateOf);
                break;
            case "--sect-position-members" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectPositionMembersOf);
                break;
            // P2-1a3：四个只读入口。
            case "--sect-leadership-config":
                sectLeadershipConfig = true;
                break;
            case "--sect-leadership-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectLeadershipStateOf);
                break;
            case "--sect-leadership-members" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectLeadershipMembersOf);
                break;
            case "--sect-leave-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectLeaveStateOf);
                break;
            // P2-1a4：升级只读入口 + 仅核对用的建设赋值（不对外、不作为玩法来源）。
            case "--sect-upgrade-config":
                sectUpgradeConfig = true;
                break;
            // Q7：队伍正本 / 状态机自检（--at 让超时用例不真的等一分钟）。
            case "--party-table" when i + 1 < args.Length:
                partyTablePath = args[++i];
                break;
            case "--party-config":
                partyConfig = true;
                break;
            case "--party-state":
                partyState = true;
                break;
            // Q4：任务表只读口 + 连库只读口（面板 / 流水）。
            case "--sect-task-config":
                sectTaskConfig = true;
                break;
            case "--sect-task-state" when i + 2 < args.Length:
                long.TryParse(args[++i], out sectTaskStateOf);
                long.TryParse(args[++i], out sectTaskStateCharacter);
                break;
            case "--sect-task-ledger" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectTaskLedgerOf);
                break;
            // 只给核对用：固定事务号完成一笔（参数 = 宗门 角色 等级 任务号 事务号），
            // 验「同一号重放不二次计次 / 不二次加建设」。不对外，网关那条才是玩法入口。
            case "--sect-task-complete" when i + 5 < args.Length:
                sectTaskComplete = args.Skip(i + 1).Take(5).ToArray();
                i += 5;
                break;
            // 只读口的时间点：ISO 8601 或毫秒时间戳，验业务日 / 周界随日历变。
            case "--at" when i + 1 < args.Length:
                var atRaw = args[++i];
                if (long.TryParse(atRaw, out var atMs))
                {
                    sectTaskAt = DateTimeOffset.FromUnixTimeMilliseconds(atMs);
                }
                else if (DateTimeOffset.TryParse(atRaw, System.Globalization.CultureInfo.InvariantCulture,
                             System.Globalization.DateTimeStyles.AssumeUniversal, out var atParsed))
                {
                    sectTaskAt = atParsed;
                }
                else
                {
                    Console.WriteLine("bad_at=" + atRaw);
                    return 64;
                }
                break;
            case "--sect-upgrade-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectUpgradeStateOf);
                break;
            case "--sect-upgrade-members" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectUpgradeMembersOf);
                break;
            case "--sect-construction-set" when i + 2 < args.Length:
                long.TryParse(args[++i], out sectConstructionSetOf);
                long.TryParse(args[++i], out sectConstructionSetTo);
                break;
            // P3-3a3：只读查角色所属宗门的科技等级。
            case "--sect-tech-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out sectTechStateOf);
                break;
            // 只给核对用：写某宗门的科技等级，不做升级逻辑（方案没写定产出）。
            case "--raw-sect-tech-write" when i + 2 < args.Length:
                long.TryParse(args[++i], out sectTechSetOf);
                int.TryParse(args[++i], out sectTechSetTo);
                break;
            // C3：可选内容表显式路径（坏配置核对用），不显式登记进 config/social.json。
            case "--homestead-table" when i + 1 < args.Length:
                homesteadTablePath = args[++i];
                break;
            case "--homestead-config":
                homesteadConfig = true;
                break;
            case "--homestead-state" when i + 1 < args.Length:
                long.TryParse(args[++i], out homesteadStateOf);
                break;
            // 只给核对用：固定事务号连发两次验幂等。参数 = 账号 角色号 格位 物品 事务号。
            case "--homestead-place" when i + 5 < args.Length:
                homesteadPlace = args.Skip(i + 1).Take(5).ToArray();
                i += 5;
                break;
            // 只给核对用：固定事务号撤下。参数 = 角色号 格位 事务号。
            case "--homestead-remove" when i + 3 < args.Length:
                homesteadRemove = args.Skip(i + 1).Take(3).ToArray();
                i += 3;
                break;
            case "--homestead-plant-table" when i + 1 < args.Length:
                homesteadPlantTablePath = args[++i];
                break;
            default:
                Console.WriteLine("unknown_arg=" + args[i]);
                return 64;
        }
    }

        return 0;
    }
    // 连库入口：--migrate / --sect-list / --sect-state / --sect-members /
    // --sect-set-level / --sect-position-* / --sect-leadership-state /
    // --sect-leadership-members / --sect-leave-state。都先过与真实启动同一份
    // Load + 同一组参数。
    // --sect-set-level / --sect-position-list / --sect-position-state /
    // --sect-position-members。都先过与真实启动同一份 Load + 同一组参数。
    public bool HasReadOnlyEntry =>
            // C3：新增的连库只读 / 核对写入口也必须登记进这条「一次性命令」长条件，
            // 否则社交服会走正常启动去 bind 65000，核对时表现为 address already in use。
        migrate || sectList || sectStateOf != 0 || sectMembersOf != 0 || sectSetLevelOf != 0
        || sectPositionList || sectPositionStateOf != 0 || sectPositionMembersOf != 0
        || sectLeadershipStateOf != 0 || sectLeadershipMembersOf != 0 || sectLeaveStateOf != 0
        || sectUpgradeStateOf != 0 || sectUpgradeMembersOf != 0 || sectConstructionSetOf != 0
        || sectTaskStateOf != 0 || sectTaskLedgerOf != 0 || sectTaskComplete is not null
        || sectTechStateOf != 0 || sectTechSetOf != 0 || sectRosterOf != 0
        || homesteadStateOf != 0 || homesteadPlace is not null || homesteadRemove is not null;
}

