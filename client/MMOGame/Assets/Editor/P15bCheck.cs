using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Mmo.Net;
using Debug = UnityEngine.Debug;

// P1-5b 编辑器核对：寄售的是元宝、换的是金币，商城不直接卖金币；
// 挂单与购买只带元宝数量 / 标价 / 挂单号，税额与到账跟着回包走，
// 客户端不在本地算税。全程不进 playmode，不连服务端，不重打 AB。
public static class P15bCheck
{
    static bool failed;

    public static void Run()
    {
        // Assets -> MMOGame -> client -> 仓库根，要退三层。
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var cfg = File.ReadAllText(Path.Combine(root, "config/auction.json"));
        var currency = File.ReadAllText(Path.Combine(root, "config/currency.json"));
        var rateMatch = Regex.Match(cfg, @"""tax_rate_pct""\s*:\s*(\d+)");
        var rate = rateMatch.Success && int.TryParse(rateMatch.Groups[1].Value, out var parsed) ? parsed : 0;

        // 1. 配置：写定区间、寄售元宝 / 结算金币、商城不卖金币写在币种说明里
        var configOk = rate >= 5 && rate <= 8
                       && Regex.IsMatch(cfg, @"""gold_kind""\s*:\s*""gold""")
                       && Regex.IsMatch(cfg, @"""ingot_kind""\s*:\s*""yuanbao""")
                       && cfg.Contains("待定")
                       && cfg.Contains("直接销毁")
                       && cfg.Contains("只经寄售换成金币")
                       && !cfg.Contains("recipient")
                       && currency.Contains("\"id\": \"yuanbao\"")
                       && currency.Contains("只经寄售换成金币")
                       && currency.Contains("拍卖与寄售用");
        Debug.Log("p15b_config=" + (configOk ? "ok" : "fail"));
        failed |= !configOk;

        // 2. 契约：挂单 / 购买 / 结算请求只带身份与编号，税额与元宝只在回包里
        var trade = File.ReadAllText(Path.Combine(root, "proto/mmo/trade.proto"));
        var characterProto = File.ReadAllText(Path.Combine(root, "proto/mmo/character.proto"));
        var listFields = Fields(Body(trade, "ListIngotRequest"));
        var buyFields = Fields(Body(trade, "BuyIngotRequest"));
        var settleFields = Fields(Body(characterProto, "AuctionIngotSettleRequest"));
        var buyReply = Fields(Body(trade, "BuyIngotResponse"));
        var protoOk = listFields.SetEquals(
                          new[] { "account", "character_id", "ingot_amount", "price_gold", "txn_id" })
                      && buyFields.SetEquals(
                          new[] { "account", "character_id", "listing_id", "txn_id" })
                      && settleFields.SetEquals(
                          new[] { "account", "character_id", "listing_id", "txn_id" })
                      && !listFields.Contains("tax_rate_pct")
                      && !buyFields.Contains("tax_gold")
                      && !settleFields.Contains("price_gold")
                      && !settleFields.Contains("ingot_amount")
                      && buyReply.Contains("tax_gold")
                      && buyReply.Contains("net_gold")
                      && buyReply.Contains("ingot_amount")
                      && buyReply.Contains("buyer_ingot")
                      && trade.Contains("rpc ListIngot(ListIngotRequest)")
                      && trade.Contains("rpc BuyIngot(BuyIngotRequest)")
                      && characterProto.Contains("rpc SettleIngot(AuctionIngotSettleRequest)");
        Debug.Log("p15b_proto=" + (protoOk ? "ok" : "fail"));
        failed |= !protoOk;

        // 3. 客户端拆装：两条固定形状的请求，税额只在回包里读
        var client = File.ReadAllText(Path.Combine(root, "client/MMOGame/Assets/Net/CharacterAuction.cs"));
        var listBody = CharacterAuction.IngotListBody(7, 100, 1000);
        var buyBody = CharacterAuction.IngotBuyBody(7, 42);
        var parsedReply = CharacterAuction.ParseIngot(
            "{\"ok\":true,\"reason\":\"\",\"listing_id\":42,\"ingot_amount\":100,"
            + "\"price_gold\":1000,\"tax_gold\":60,\"net_gold\":940,\"buyer_gold\":0,"
            + "\"seller_gold\":940,\"buyer_ingot\":100,\"seller_ingot\":0,"
            + "\"tax_rate_pct\":6,\"state\":\"sold\",\"duplicate\":false}");
        var rejected = CharacterAuction.ParseIngot(
            "{\"ok\":false,\"reason\":\"duplicate\",\"duplicate\":true,\"tax_gold\":0}");
        var wireOk = client.Contains("IngotListBody(ulong characterId, long ingotAmount, long priceGold)")
                     && client.Contains("IngotBuyBody(ulong characterId, ulong listingId)")
                     && listBody == "{\"character_id\":7,\"ingot_amount\":100,\"price_gold\":1000}"
                     && buyBody == "{\"character_id\":7,\"listing_id\":42}"
                     && parsedReply.Ok && parsedReply.IngotAmount == 100
                     && parsedReply.TaxGold == 60 && parsedReply.NetGold == 940
                     && parsedReply.PriceGold == parsedReply.TaxGold + parsedReply.NetGold
                     && parsedReply.BuyerIngot == 100 && parsedReply.SellerIngot == 0
                     && parsedReply.ListingId == 42 && parsedReply.State == "sold"
                     && !rejected.Ok && rejected.Duplicate && rejected.TaxGold == 0
                     && !client.Contains("AuctionTable")
                     && !client.Contains("Random")
                     && !client.Contains("JsonUtility")
                     && !client.Contains("txn_id");
        Debug.Log("p15b_wire=" + (wireOk ? "ok" : "fail"));
        failed |= !wireOk;

        // 4. 进程边界：全局服务只管挂单，角色服一笔事务写元宝 / 金币 / 成交状态；
        //    C# 源码里 SQL 的引号是转义写法，断言只查不依赖引号的片段。
        var repo = File.ReadAllText(Path.Combine(root, "src/Character/IngotRepository.cs"));
        var table = File.ReadAllText(Path.Combine(root, "src/Character/AuctionTable.cs"));
        var global = File.ReadAllText(Path.Combine(root, "src/Global/GlobalGrpcService.cs"));
        var api = File.ReadAllText(Path.Combine(root, "src/Gateway/AuctionApi.cs"));
        var process = File.ReadAllText(Path.Combine(root, "src/Character/CharacterProcess.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src/Character/Program.cs"));
        var sql = File.ReadAllText(Path.Combine(root, "db/trade/ingot.sql"));
        var boundaryOk = repo.Contains("BeginTransactionAsync")
                         && repo.Contains("auction_ingot_txn")
                         && repo.Contains("auction_ingot_settlement")
                         && repo.Contains("auction_ingot_listing")
                         && repo.Contains("amount + $3 >= 0")
                         && repo.Contains("set state = 'sold'")
                         && repo.Contains("ingot_insufficient")
                         && repo.Contains("gold_insufficient")
                         && !repo.Contains("Random")
                         && table.Contains("WrittenIngotKind = \"yuanbao\"")
                         && global.Contains("SettleIngotAsync")
                         && api.Contains("MapPost(\"/auction/ingot/list\"")
                         && api.Contains("MapPost(\"/auction/ingot/buy\"")
                         && api.Contains("Guid.NewGuid()")
                         && process.Contains("new IngotRepository(")
                         && program.Contains("case \"--ingot-ledger\"")
                         && sql.Contains("auction_ingot_listing")
                         && sql.Contains("auction_ingot_txn")
                         && sql.Contains("auction_ingot_settlement")
                         && sql.Contains("CHECK (net_gold + tax_gold = price_gold)");
        Debug.Log("p15b_boundary=" + (boundaryOk ? "ok" : "fail"));
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
