using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Mmo.Net;
using Debug = UnityEngine.Debug;

// P1-5a 编辑器核对：写定的 5%–8% 抽税区间只在配置与角色服、税没有收款方、
// 挂单与购买的请求只带格位 / 标价 / 挂单号、结果与税额跟着回包走、
// 客户端不在本地算税。全程不进 playmode，不连服务端，不重打 AB。
public static class P15aCheck
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var cfg = File.ReadAllText(Path.Combine(root, "config/auction.json"));
        var rateMatch = Regex.Match(cfg, @"""tax_rate_pct""\s*:\s*(\d+)");
        var rate = rateMatch.Success && int.TryParse(rateMatch.Groups[1].Value, out var parsed) ? parsed : 0;

        // 1. 配置：写定区间、金币标价、税没有收款方，没写定的数值注明待定
        var configOk = rate >= 5 && rate <= 8
                       && Regex.IsMatch(cfg, @"""rounding""\s*:\s*""down""")
                       && Regex.IsMatch(cfg, @"""gold_kind""\s*:\s*""gold""")
                       && cfg.Contains("待定")
                       && cfg.Contains("直接销毁")
                       && !cfg.Contains("recipient")
                       && !cfg.Contains("success_rate");
        Debug.Log("p15a_config=" + (configOk ? "ok" : "fail"));
        failed |= !configOk;

        // 2. 契约：挂单 / 购买 / 结算请求只带身份与编号，税额与到账只在回包里
        var trade = File.ReadAllText(Path.Combine(root, "proto/mmo/trade.proto"));
        var characterProto = File.ReadAllText(Path.Combine(root, "proto/mmo/character.proto"));
        var listFields = Fields(Body(trade, "ListAuctionRequest"));
        var buyFields = Fields(Body(trade, "BuyAuctionRequest"));
        var settleFields = Fields(Body(characterProto, "AuctionSettleRequest"));
        var buyReply = Fields(Body(trade, "BuyAuctionResponse"));
        var protoOk = listFields.SetEquals(
                          new[] { "account", "character_id", "slot", "count", "price_gold", "txn_id" })
                      && buyFields.SetEquals(
                          new[] { "account", "character_id", "listing_id", "txn_id" })
                      && settleFields.SetEquals(
                          new[] { "account", "character_id", "listing_id", "txn_id" })
                      && !listFields.Contains("tax_rate_pct")
                      && !buyFields.Contains("tax_gold")
                      && !settleFields.Contains("price_gold")
                      && buyReply.Contains("tax_gold")
                      && buyReply.Contains("net_gold")
                      && trade.Contains("service TradeService");
        Debug.Log("p15a_proto=" + (protoOk ? "ok" : "fail"));
        failed |= !protoOk;

        // 3. 客户端拆装：两条固定形状的请求，税额只在回包里读
        var client = File.ReadAllText(Path.Combine(root, "client/MMOGame/Assets/Net/CharacterAuction.cs"));
        var listBody = CharacterAuction.ListBody(7, 3, 2, 1000);
        var buyBody = CharacterAuction.BuyBody(7, 42);
        var parsedReply = CharacterAuction.Parse(
            "{\"ok\":true,\"reason\":\"\",\"listing_id\":42,\"price_gold\":1000,"
            + "\"tax_gold\":60,\"net_gold\":940,\"buyer_gold\":0,\"seller_gold\":940,"
            + "\"tax_rate_pct\":6,\"item_id\":\"ore_iron\",\"count\":2,\"state\":\"sold\","
            + "\"duplicate\":false}");
        var rejected = CharacterAuction.Parse(
            "{\"ok\":false,\"reason\":\"duplicate\",\"duplicate\":true,\"tax_gold\":0}");
        var wireOk = client.Contains("ListBody(ulong characterId, uint slot, uint count, long priceGold)")
                     && client.Contains("BuyBody(ulong characterId, ulong listingId)")
                     && listBody == "{\"character_id\":7,\"slot\":3,\"count\":2,\"price_gold\":1000}"
                     && buyBody == "{\"character_id\":7,\"listing_id\":42}"
                     && parsedReply.Ok && parsedReply.TaxGold == 60 && parsedReply.NetGold == 940
                     && parsedReply.PriceGold == parsedReply.TaxGold + parsedReply.NetGold
                     && parsedReply.ListingId == 42 && parsedReply.State == "sold"
                     && !rejected.Ok && rejected.Duplicate && rejected.TaxGold == 0
                     && !client.Contains("AuctionTable")
                     && !client.Contains("Random")
                     && !client.Contains("JsonUtility")
                     && !client.Contains("txn_id");
        Debug.Log("p15a_wire=" + (wireOk ? "ok" : "fail"));
        failed |= !wireOk;

        // 4. 进程边界：全局服务只管挂单，角色服一笔事务写货币 / 物品 / 成交状态；
        //    C# 源码里 SQL 的引号是转义写法，断言只查不依赖引号的片段。
        var repo = File.ReadAllText(Path.Combine(root, "src/Character/AuctionRepository.cs"));
        var table = File.ReadAllText(Path.Combine(root, "src/Character/AuctionTable.cs"));
        var global = File.ReadAllText(Path.Combine(root, "src/Global/GlobalGrpcService.cs"));
        var api = File.ReadAllText(Path.Combine(root, "src/Gateway/AuctionApi.cs"));
        var process = File.ReadAllText(Path.Combine(root, "src/Character/CharacterProcess.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src/Character/Program.cs"));
        var sql = File.ReadAllText(Path.Combine(root, "db/trade/auction.sql"));
        var boundaryOk = repo.Contains("BeginTransactionAsync")
                         && repo.Contains("auction_txn")
                         && repo.Contains("auction_settlement")
                         && repo.Contains("delete from")
                         && repo.Contains(".auction_listing")
                         && repo.Contains("amount + $3 >= 0")
                         && repo.Contains("set state = 'sold'")
                         && !repo.Contains("Random")
                         && table.Contains("WrittenMinRatePct = 5")
                         && table.Contains("WrittenMaxRatePct = 8")
                         && table.Contains("不许出现税的收款方字段")
                         && global.Contains("CanTradeAsync")
                         && global.Contains("SettleAuctionAsync")
                         && api.Contains("MapPost(\"/auction/list\"")
                         && api.Contains("MapPost(\"/auction/buy\"")
                         && api.Contains("OwnsAsync")
                         && api.Contains("Guid.NewGuid()")
                         && process.Contains("auction_table_version=")
                         && program.Contains("case \"--auction-state\"")
                         && program.Contains("case \"--auction-ledger\"")
                         && sql.Contains("auction_listing")
                         && sql.Contains("auction_txn")
                         && sql.Contains("auction_settlement")
                         && !cfg.Contains("tax_account");
        Debug.Log("p15a_boundary=" + (boundaryOk ? "ok" : "fail"));
        failed |= !boundaryOk;

        EditorApplication.Exit(failed ? 1 : 0);
    }

    static string Body(string proto, string message)
    {
        var match = Regex.Match(proto, @"message " + message + @" \{(.*?)\}", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    // 按字段名逐个比对，不用裸子串：account 里也含 count 这几个字母。
    static HashSet<string> Fields(string body)
    {
        var fields = new HashSet<string>();
        foreach (Match field in Regex.Matches(
            body, @"(?:string|uint64|uint32|int64|int32|bool|double)\s+(\w+)\s*="))
        {
            fields.Add(field.Groups[1].Value);
        }

        return fields;
    }
}
