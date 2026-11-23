using Grpc.Core;
using Mmo.Proto.Character;
using Mmo.Proto.Social;
using Npgsql;

namespace Character;

/// <summary>
/// 建角色的 gRPC 入口。校验收在服务端：种族要在表里、职业要在表里、
/// 分支必须属于这个职业。种族与外观原样落库，不参与任何数值计算。
/// </summary>
public sealed partial class CharacterGrpcService(
    CharacterRepository characters,
    InventoryRepository inventory,
    BagRepository bags,
    EquipRepository equips,
    CurrencyRepository currency,
    GrantRepository grants,
    RaceTable races,
    BranchTable branches,
    ClassIds classes,
    ItemTable items,
    CurrencyTable currencies,
    DungeonTable dungeons,
    RealmTable realms,
    DungeonEntryRepository dungeonEntries,
    HardEntryRepository hardEntries,
    WeeklyShareRepository weeklyShares,
    HotSpringRepository hotSpring,
    ExpSettlementRepository expSettlements,
    ExpTable expTable,
    EnhanceRepository enhances,
    EnhanceTable enhanceTable,
    TransferRepository transfers,
    TransferTable transferTable,
    CrystalRepository crystals,
    CrystalTable crystalTable,
    SetTable sets,
    AuctionRepository auctions,
    AuctionTable auctionTable,
    IngotRepository ingots,
    ShopTable shopTable,
    TalismanTable talismanTable,
    TalismanRepository talismans,
    PillTable pillTable,
    PillRepository pills,
    ReviveScrollTable scrollTable,
    ReviveScrollRepository scrolls,
    ExpBoosterTable boosterTable,
    ExpBoosterRepository boosters,
    UnbindTable unbindTable,
    UnbindRepository unbinds,
    RechargeTable rechargeTable,
    RechargeRepository recharges,
    RealmAdvanceRepository realmAdvances,
    RealmPhaseRepository realmPhases,
    SocialService.SocialServiceClient social,
    CharacterConfig config,
    ILogger<CharacterGrpcService> logger,
    StoneExchangeTable stoneExchange,
    StoneExchangeRepository stoneExchanges,
    StoneAuctionRepository stoneAuctions,
    CityTable cities,
    SiegeTerritoryRepository siegeTerritories,
    SeasonPassTable seasonPassTable,
    SeasonPassProgressTable seasonPassProgressTable,
    SeasonPassProgressRepository seasonPassPoints,
    SeasonPassRepository seasonPasses,
    MonthlyBundleTable monthlyBundleTable,
    MonthlyBundleRepository monthlyBundles,
    CharacterSlotTable characterSlotTable,
    CharacterSlotRepository characterSlots,
    BagExpandRepository bagExpands,
    WardrobeRepository wardrobe,
    ISeasonSource seasonSource,
    MainlineTable mainlineTable,
    MainlineRepository mainlines,
    DungeonDropTable dungeonDropTable,
    HomeCoinShopTable homeCoinShopTable,
    NpcShopTable npcShopTable,
    NpcShopRepository npcShops,
    HeroCodexRepository heroCodex,
    RecipeTable recipeTable,
    CraftRepository crafts,
    CityTaskTable? cityTaskTable,
    CityTaskRepository? cityTasks,
    DeserterRepository deserters,
    NameRulesTable nameRules) : CharacterService.CharacterServiceBase
{

    // 两张表随服务一起注入：启动加载的正本就是判定用的正本，不另起一套。
    public StoneExchangeTable StoneExchange { get; } = stoneExchange;

    public CityTable Cities { get; } = cities;


    public const int MinLevel = 1;


    public const int MaxLevel = 100;
}
