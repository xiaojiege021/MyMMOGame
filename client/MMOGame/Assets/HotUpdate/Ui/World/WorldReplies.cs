using System;
using System.Collections;
using System.Collections.Generic;
using Mmo.Net;
using Mmo.Proto.World;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 非 200 的回包折成与业务拒绝同形的结果（401 / 503 / 连不上）。
    /// </summary>
    public sealed partial class UiWorldFlow : MonoBehaviour
    {
        // ---------------------------------------------------------------- 非 200 的回包

        // 401 / 503 / 连不上时网关给的是 {"error":"..."}，没有业务字段。
        // 这里把它们折成与业务拒绝同形的结果，屏上照样分得开是哪一档。

        static string ErrorOf(int status, string json)
        {
            if (status == 0)
            {
                return "network";
            }

            var at = json != null ? json.IndexOf("\"error\"", StringComparison.Ordinal) : -1;
            if (at >= 0)
            {
                var start = json.IndexOf('"', at + 7);
                if (start >= 0)
                {
                    var end = json.IndexOf('"', start + 1);
                    if (end > start)
                    {
                        return json.Substring(start + 1, end - start - 1);
                    }
                }
            }

            return status == 401 ? "invalid_token"
                : status == 503 ? "unavailable"
                : "http_" + status;
        }

        // 字符串字段一律显式给空串：非 200 的回包里没有这些键，
        // 留着 null 会让屏上拼文案时撞空引用。
        static GatherOutcome RejectedGather(int status, string json)
        {
            var error = ErrorOf(status, json);
            return new GatherOutcome
            {
                Ok = false,
                Reason = error,
                NodeId = string.Empty,
                Kind = string.Empty,
                ItemId = string.Empty,
                TxnId = string.Empty,
                DisplayText = "采集没发出去：" + error,
            };
        }

        static FlyOutcome RejectedFly(int status, string json)
        {
            var error = ErrorOf(status, json);
            return new FlyOutcome
            {
                Ok = false,
                Reason = error,
                Realm = string.Empty,
                RequiredRealm = string.Empty,
                DisplayText = "飞行没发出去：" + error,
            };
        }

        static RegionOutcome RejectedRegion(int status, string json)
        {
            var error = ErrorOf(status, json);
            return new RegionOutcome
            {
                Ok = false,
                Reason = error,
                RegionId = string.Empty,
                LineId = string.Empty,
                Realm = string.Empty,
                RequiredRealm = string.Empty,
                DisplayText = "跨区没发出去：" + error,
            };
        }
    }
}
