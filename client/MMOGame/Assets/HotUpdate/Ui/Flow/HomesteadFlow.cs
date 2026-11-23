using System;
using System.Collections.Generic;
using System.Globalization;
using Mmo.Net;
using Mmo.Proto.Battle;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 洞府摆放与种植（U9 / U10）：只读事实 + 摆件 / 种 / 收 / 家园币商城。
    /// </summary>
    public sealed partial class UiSocialFlow : MonoBehaviour
    {
        // ------------------------------------------------------------------ 洞府摆放（U9 / C3）

        /// <summary>只读洞府摆放。owner 默认本角色；访客可传别人的角色号。</summary>
        public void LoadHomestead(ulong ownerCharacterId = 0, Action done = null)
        {
            var owner = ownerCharacterId != 0 ? ownerCharacterId : _characterId;
            if (owner == 0)
            {
                LastHomestead = HomesteadOutcome.Failure("no_character");
                HasHomestead = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(HomesteadEntry.DescribePath(owner), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastHomestead = HomesteadEntry.Parse(text);
                    HasHomestead = true;
                    Error = string.Empty;
                    Debug.Log("u9_homestead=ok slots=" + LastHomestead.SlotCount
                        + " placed=" + (LastHomestead.Placements != null ? LastHomestead.Placements.Count : 0)
                        + " reason=" + LastHomestead.Reason);
                }
                else
                {
                    LastHomestead = HomesteadOutcome.Failure("http_" + status);
                    HasHomestead = false;
                    Error = "洞府没读到：" + status;
                    Debug.Log("u9_homestead=reject status=" + status);
                }

                if (done != null) done();
            });
        }

        /// <summary>读背包，只为洞府屏挑出可摆的 home_decor。不改背包。</summary>
        public void LoadHomesteadBag(Action done = null)
        {
            if (_characterId == 0)
            {
                HomesteadBag = BagOutcome.Failure("no_character");
                HasHomesteadBag = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterBag.RequestPath(_characterId), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    HomesteadBag = CharacterBag.Parse(text);
                    HasHomesteadBag = HomesteadBag.Ok;
                    Debug.Log("u9_homestead_bag=ok slots="
                        + (HomesteadBag.Slots != null ? HomesteadBag.Slots.Count : 0));
                }
                else
                {
                    HomesteadBag = BagOutcome.Failure("http_" + status);
                    HasHomesteadBag = false;
                    Debug.Log("u9_homestead_bag=reject status=" + status);
                }

                if (done != null) done();
            });
        }

        public void PlaceDecor(string slotId, string itemId, Action done = null)
        {
            Send(HomesteadEntry.PlacePath,
                HomesteadEntry.PlaceBody(_characterId, slotId, itemId),
                HomesteadEntry.Parse,
                outcome =>
                {
                    LastHomestead = outcome;
                    HasHomestead = true;
                },
                "u9_homestead_place", done);
        }

        public void RemoveDecor(string slotId, Action done = null)
        {
            Send(HomesteadEntry.RemovePath,
                HomesteadEntry.RemoveBody(_characterId, slotId),
                HomesteadEntry.Parse,
                outcome =>
                {
                    LastHomestead = outcome;
                    HasHomestead = true;
                },
                "u9_homestead_remove", done);
        }

        /// <summary>背包里可摆的洞府摆件编号（与 C3 发货的 decor_s* 对齐）。</summary>
        public List<string> DecorInBag()
        {
            var list = new List<string>();
            if (!HasHomesteadBag || HomesteadBag.Slots == null)
            {
                return list;
            }

            for (var i = 0; i < HomesteadBag.Slots.Count; i++)
            {
                var id = HomesteadBag.Slots[i].ItemId;
                if (!UiSocialCodex.IsDecorItem(id))
                {
                    continue;
                }

                if (list.Contains(id))
                {
                    continue;
                }

                list.Add(id);
            }

            return list;
        }
        // ------------------------------------------------------------------ 洞府种植（U10 / N11）

        /// <summary>只读种植事实。owner 默认本角色；访客可传别人的角色号。</summary>
        public void LoadPlants(ulong ownerCharacterId = 0, Action done = null)
        {
            var owner = ownerCharacterId != 0 ? ownerCharacterId : _characterId;
            if (owner == 0)
            {
                LastPlants = HomesteadPlantOutcome.Failure("no_character");
                HasPlants = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(HomesteadPlantEntry.DescribePath(owner), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastPlants = HomesteadPlantEntry.Parse(text);
                    HasPlants = true;
                    Error = string.Empty;
                    Debug.Log("u10_plants=ok slots=" + LastPlants.SlotCount
                        + " planted=" + (LastPlants.Plants != null ? LastPlants.Plants.Count : 0)
                        + " reason=" + LastPlants.Reason);
                }
                else
                {
                    LastPlants = HomesteadPlantOutcome.Failure("http_" + status);
                    HasPlants = false;
                    Error = "种植没读到：" + status;
                    Debug.Log("u10_plants=reject status=" + status);
                }

                if (done != null) done();
            });
        }

        /// <summary>种下一格。作物编号只作请求参数，产量与时长在服务端配置里。</summary>
        public void PlantCrop(string slotId, string cropId, Action done = null)
        {
            Send(HomesteadPlantEntry.PlantPath,
                HomesteadPlantEntry.PlantBody(_characterId, slotId, cropId),
                HomesteadPlantEntry.Parse,
                outcome =>
                {
                    LastPlantAction = outcome;
                    HasPlantAction = true;
                    if (outcome.Ok)
                    {
                        // 回包带回了整份种植事实，格子照它重画，不再本地改状态。
                        LastPlants = outcome;
                        HasPlants = true;
                    }
                },
                "u10_plant", done);
        }

        /// <summary>收取成熟的一格。收了多少与余额都是回包给的，客户端不乘不加。</summary>
        public void HarvestCrop(string slotId, Action done = null)
        {
            if (_characterId == 0)
            {
                return;
            }

            Busy = true;
            Requests++;
            _send(HomesteadPlantEntry.HarvestPath,
                HomesteadPlantEntry.HarvestBody(_characterId, slotId), _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        var outcome = HomesteadPlantEntry.Parse(text);
                        LastPlantAction = outcome;
                        HasPlantAction = true;
                        if (outcome.Ok)
                        {
                            LastPlants = outcome;
                            HasPlants = true;
                            HomeCoinBalance = outcome.HomeCoinBalance;
                            HasHomeCoinBalance = true;
                        }

                        Error = string.Empty;
                        Debug.Log("u10_harvest=ok slot=" + slotId
                            + " granted=" + outcome.GrantedHomeCoin
                            + " balance=" + outcome.HomeCoinBalance);
                    }
                    else
                    {
                        LastPlantAction = HomesteadPlantOutcome.Failure("http_" + status);
                        HasPlantAction = true;
                        Error = "收取没回应：" + status;
                        Debug.Log("u10_harvest=reject status=" + status);
                    }

                    if (done != null) done();
                });
        }

        /// <summary>用家园币买一件外观或便捷。标价、扣多少、有没有发货只认回包。</summary>
        public void BuyHomeCoinItem(string itemId, Action done = null)
        {
            Send(HomeCoinShopEntry.BuyPath,
                HomeCoinShopEntry.BuyBody(_characterId, itemId),
                HomeCoinShopEntry.Parse,
                outcome =>
                {
                    LastHomeCoin = outcome;
                    HasHomeCoin = true;
                    if (outcome.Reason != "bad_reply")
                    {
                        // 回包只要认得出形状，balance 就是角色服算过的余额，照抄；
                        // 认不出形状（bad_reply）时余额仍是 0，不拿它冒充。
                        HomeCoinBalance = outcome.Balance;
                        HasHomeCoinBalance = true;
                    }
                },
                "u10_home_coin_buy", done);
        }
    }
}
