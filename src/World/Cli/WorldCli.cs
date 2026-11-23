namespace World;

/// <summary>
/// 世界服启动参数面（14.8 D3h）。字段名与拆分前 Program.cs 的局部变量同名，
/// 搬进来的解析与只读入口因此一个字都不用改。
/// </summary>
public sealed partial class WorldCli
{

    // 配置路径可以指到别处，核对脚本用临时配置跑，不碰仓库里那份。
    public string configPath = "config/world.json";
    public string gatherConfigPath = "config/gather.json";
    // C2 铺开表是可选表：老卡的临时配置目录里没有它，世界服按 P1-3b 的四个基准点照常启动。
    public string gatherSpreadConfigPath = "config/gather_spread.json";
    public bool gatherSpreadExplicit = false;
    public string flightConfigPath = "config/flight.json";
    public string worldBossConfigPath = "config/world_boss.json";
    // N1 地表解锁表：可选表，默认跟 --config 所在目录走（与 gather_spread 同一条经验）。
    public string regionUnlockPath = "config/region_unlock.json";
    public bool regionUnlockExplicit = false;
    public bool regionUnlockState = false;
    public bool regionAccessState = false;
    // N14 的列表口：按给定进度与路线打一遍「ListRegions 会回什么」，纯计算。
    public bool regionListState = false;
    // 只给 --region-access 用：按给定进度与路线验判定，不连角色服、不起进程。
    public int accessStage = -1;
    public string accessAlignment = "";
    public bool regionState = false;
    public bool positionState = false;
    public bool tierState = false;
    public bool gatherState = false;
    public bool gatherSpreadState = false;
    public bool flightState = false;
    public bool worldBossState = false;
    public long worldBossAtMs = 0L;
    // Q11：世界 Boss 的窗口时刻按日历时区解释。核对脚本可以指一份临时日历表，
    // 空着就用 --config 那份 world.json 里写的 calendar_table。
    public string calendarPath = "";

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
            // 采集表单独一份，核对脚本用临时配置跑，不碰仓库里那份。
            case "--gather-config" when i + 1 < args.Length:
                gatherConfigPath = args[++i];
                break;
            // 只给核对用：把读表后的节点与产物打出来，不启动进程。
            case "--gather-state":
                gatherState = true;
                break;
            // C2 铺开表单独一份，核对脚本用临时配置跑，不碰仓库里那份。
            case "--gather-spread-config" when i + 1 < args.Length:
                gatherSpreadConfigPath = args[++i];
                gatherSpreadExplicit = true;
                break;
            // 只给核对用：把铺开后的节点、境界区域与每块地表的覆盖打出来，不启动进程。
            case "--gather-spread-state":
                gatherSpreadState = true;
                break;
            // 飞行与跨区门槛单独一份，核对脚本用临时配置跑，不碰仓库里那份。
            case "--flight-config" when i + 1 < args.Length:
                flightConfigPath = args[++i];
                break;
            // 只给核对用：把读表后的境界顺序与门槛打出来，不启动进程。
            case "--flight-state":
                flightState = true;
                break;
            // 只给核对用：把读表后的地点与人物编号打出来，不启动进程。
            case "--region-state":
                regionState = true;
                break;
            // 只给核对用：把位置同步的配置读出来，不启动进程。
            case "--position-state":
                positionState = true;
                break;
            // 只给核对用：把同屏三档的阈值与分档结果读出来，不启动进程。
            case "--tier-state":
                tierState = true;
                break;
            // 世界 Boss 表单独一份，核对脚本用临时配置跑，不碰仓库里那份。
            case "--world-boss-config" when i + 1 < args.Length:
                worldBossConfigPath = args[++i];
                break;
            // 只给核对用：把读表后的入口、上限、窗口与「这一刻是哪一场」打出来，不起进程。
            case "--world-boss-state":
                worldBossState = true;
                break;
            // 搭配 --world-boss-state：换一份日历表跑（验改时区后出现时刻整体平移）。
            case "--calendar" when i + 1 < args.Length:
                calendarPath = args[++i];
                break;
            // 搭配 --world-boss-state：把「这一刻」定成给定的毫秒时间戳，用来验未到点 / 到点。
            case "--at" when i + 1 < args.Length:
                if (!long.TryParse(args[++i], out worldBossAtMs))
                {
                    Console.WriteLine("bad_arg=--at");
                    return 64;
                }
                break;
            // N1 地表解锁表单独一份，核对脚本用临时配置跑，不碰仓库里那份。
            case "--region-unlock-config" when i + 1 < args.Length:
                regionUnlockPath = args[++i];
                regionUnlockExplicit = true;
                break;
            // 只给核对用：把解锁条目与每块静态不开放的地表打出来，不启动进程、不连角色服。
            case "--region-unlock-state":
                regionUnlockState = true;
                break;
            // 只给核对用：按 --main-stage / --alignment 给的进度验判定，纯计算。
            case "--region-access":
                regionAccessState = true;
                break;
            // 只给核对用：N14 的列表口会回什么（八块地表逐块 open / reason / 门槛）。
            case "--region-list":
                regionListState = true;
                break;
            case "--main-stage" when i + 1 < args.Length:
                if (!int.TryParse(args[++i], out accessStage) || accessStage < 0 || accessStage > 12)
                {
                    Console.WriteLine("bad_arg=--main-stage");
                    return 64;
                }
                break;
            case "--alignment" when i + 1 < args.Length:
                accessAlignment = args[++i];
                break;
            default:
                Console.WriteLine("unknown_arg=" + args[i]);
                return 64;
        }
    }

    // C2 的可选表跟 --config 所在的目录走。老卡用自己的临时配置目录改 flight.json 门槛时，
    // 那份目录里没有 gather_spread.json，就按 P1-3b 四个基准点降级；否则仓库里的产品表
    // 会和临时改过的境界门槛交叉校验失败，把老卡的「改配置重启」整段打成假红。
    // 显式给 --gather-spread-config 时不推导（C2 自己的核对脚本走这条路）。
    if (!gatherSpreadExplicit)
    {
        var configDir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(configDir))
        {
            gatherSpreadConfigPath = Path.Combine(configDir, "gather_spread.json");
        }
    }

    // N1 的解锁表同样跟着 --config 所在的目录走：老卡的临时配置目录里没有它，
    // 世界服按 P1-3a1 的静态 open 跑（两块第 11 场才开的地表照旧 region_locked）。
    if (!regionUnlockExplicit)
    {
        var unlockDir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(unlockDir))
        {
            regionUnlockPath = Path.Combine(unlockDir, "region_unlock.json");
        }
    }

        return 0;
    }

    // 只读入口：命中任一个就不起服（避免 bind 65000 打架），顺序与拆分前一致。
    public bool HasStateEntry =>
        gatherState || gatherSpreadState || flightState || tierState || positionState
        || regionState || regionUnlockState || regionAccessState || regionListState
        || worldBossState;
}
