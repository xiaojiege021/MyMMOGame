namespace Character.Cli;

/// <summary>只读入口与真实启动共用的表加载链（通行证 / 主线 / 月叠 / 角色栏）。</summary>
internal static class CliLoaders
{
    // Q3/G1：只读入口与真实启动共用同一份日历表加载链。
    public static CalendarTable LoadCalendar(CharacterConfig cfg, string? path) =>
        CalendarTable.Load(path ?? cfg.CalendarTable);

    // P3-4a2：通行证表的加载链。--season-pass-list 与三条连库入口共用这一份，
    // 免得「只读入口」与「真实启动」各校验一套（preflight 第 3 道闸门的规矩）。
    public static (SeasonPassTable Table, ItemTable Items) LoadSeasonPass(CharacterConfig cfg, string? path)
    {
        var currencies = CurrencyTable.Load("config/currency.json");
        var items = ItemTable.Load("config/items.json");
        var enhance = EnhanceTable.Load(cfg.EnhanceTable, items, currencies);
        var talismans = TalismanTable.Load(cfg.TalismanTable, items);
        var scrolls = ReviveScrollTable.Load(
            cfg.ReviveScrollTable, "config/classes.json", items, talismans);
        var boosters = ExpBoosterTable.Load(
            cfg.ExpBoosterTable, items, talismans, scrolls);
        var unbinds = UnbindTable.Load(
            cfg.UnbindTable, items, talismans, scrolls, boosters);
        var shop = ShopTable.Load(
            cfg.ShopTable, currencies, items, enhance, talismans, scrolls, boosters, unbinds);
        var table = SeasonPassTable.Load(
            path ?? cfg.SeasonPassTable, File.ReadAllText(cfg.SeasonTable), shop, items, enhance);
        return (table, items);
    }

    // Q6/J4：通行证进度表的加载链。与真实启动共用同一 SeasonPassProgressTable.Load：
    // 副本来源必须在 dungeons.json 里、daily / weekly 必须有次数上限。缺表降级。
    public static SeasonPassProgressTable LoadSeasonPassProgress(CharacterConfig cfg, string? path)
    {
        var target = path ?? cfg.SeasonPassProgressTable;
        if (!File.Exists(target))
        {
            return SeasonPassProgressTable.Missing();
        }

        var dungeons = DungeonTable.Load(cfg.DungeonTable);
        return SeasonPassProgressTable.Load(target, dungeons);
    }

    // Q5/J2：城池任务表的加载链。与真实启动同一份 CityTaskTable.Load：
    // 城池编号必须在 config/city.json 里、奖励只能是灵石五档、境界门槛必须在 realms.json。
    public static CityTaskTable LoadCityTasks(CharacterConfig cfg, string? path)
    {
        var currencies = CurrencyTable.Load("config/currency.json");
        var dungeons = DungeonTable.Load(cfg.DungeonTable);
        var realms = RealmTable.Load(cfg.RealmTable, dungeons, currencies);
        var stones = StoneExchangeTable.Load(cfg.StoneExchangeTable, currencies);
        var cities = CityTable.Load(cfg.CityTable);
        return CityTaskTable.Load(path ?? cfg.CityTaskTable, cities, stones, realms);
    }

    // C1：主线十二场的加载链。与真实启动同一份 MainlineTable.Load：
    // 十二场挂的 main 必须是 1 人 / 不扣疲劳 / 不占份额，等突破的三段必须在首发里。
    public static MainlineTable LoadMainline(CharacterConfig cfg, string? path)
    {
        var currencies = CurrencyTable.Load("config/currency.json");
        var dungeons = DungeonTable.Load(cfg.DungeonTable);
        var realms = RealmTable.Load(cfg.RealmTable, dungeons, currencies);
        return MainlineTable.Load(path ?? cfg.MainlineTable, dungeons, realms);
    }

    // P3-4b：月叠表的加载链。与真实启动同一份 MonthlyBundleTable.Load：
    // 商品与标价、物品与堆叠上限、红符编号都在这条链上交叉校验。
    public static MonthlyBundleTable LoadMonthlyBundle(CharacterConfig cfg, string? path)
    {
        var currencies = CurrencyTable.Load("config/currency.json");
        var items = ItemTable.Load("config/items.json");
        var enhance = EnhanceTable.Load(cfg.EnhanceTable, items, currencies);
        var talismans = TalismanTable.Load(cfg.TalismanTable, items);
        var scrolls = ReviveScrollTable.Load(
            cfg.ReviveScrollTable, "config/classes.json", items, talismans);
        var boosters = ExpBoosterTable.Load(cfg.ExpBoosterTable, items, talismans, scrolls);
        var unbinds = UnbindTable.Load(cfg.UnbindTable, items, talismans, scrolls, boosters);
        var shop = ShopTable.Load(
            cfg.ShopTable, currencies, items, enhance, talismans, scrolls, boosters, unbinds);
        return MonthlyBundleTable.Load(path ?? cfg.MonthlyBundleTable, shop, items, talismans);
    }

    // P3-4b：角色栏表的加载链。商城清单是同一份正本，商品与标价都从那里读。
    public static CharacterSlotTable LoadCharacterSlot(CharacterConfig cfg, string? path)
    {
        var currencies = CurrencyTable.Load("config/currency.json");
        var items = ItemTable.Load("config/items.json");
        var enhance = EnhanceTable.Load(cfg.EnhanceTable, items, currencies);
        var talismans = TalismanTable.Load(cfg.TalismanTable, items);
        var scrolls = ReviveScrollTable.Load(
            cfg.ReviveScrollTable, "config/classes.json", items, talismans);
        var boosters = ExpBoosterTable.Load(cfg.ExpBoosterTable, items, talismans, scrolls);
        var unbinds = UnbindTable.Load(cfg.UnbindTable, items, talismans, scrolls, boosters);
        var shop = ShopTable.Load(
            cfg.ShopTable, currencies, items, enhance, talismans, scrolls, boosters, unbinds);
        return CharacterSlotTable.Load(path ?? cfg.CharacterSlotTable, shop);
    }

    // Q12/K3：名字规则的加载链。只读入口与启动共用同一份 NameRulesTable.Load：
    // 物品表是同一份正本（rename_item_id 必须在里面），敏感词表路径也由规则文件指。
    public static NameRulesTable LoadNameRules(CharacterConfig cfg, string? path) =>
        NameRulesTable.Load(path ?? cfg.NameRulesTable, ItemTable.Load("config/items.json"));
}
