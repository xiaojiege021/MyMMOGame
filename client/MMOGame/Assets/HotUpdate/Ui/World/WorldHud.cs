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
    /// 屏上那几行文案、按 kind 筛节点与同屏档位展示（一律照抄回包）。
    /// </summary>
    public sealed partial class UiWorldFlow : MonoBehaviour
    {
        /// <summary>按 kind 筛节点，顺序与回包一致。</summary>
        public List<GatherNodeRow> NodesOfKind(string kind)
        {
            var list = new List<GatherNodeRow>();
            if (!HasGatherNodes || GatherNodesReply.Nodes == null)
            {
                return list;
            }

            for (var i = 0; i < GatherNodesReply.Nodes.Length; i++)
            {
                if (string.Equals(GatherNodesReply.Nodes[i].Kind, kind, StringComparison.Ordinal))
                {
                    list.Add(GatherNodesReply.Nodes[i]);
                }
            }

            return list;
        }
        // ---------------------------------------------------------------- 展示

        /// <summary>别人该画成什么样。档位只认服务端下发的 AppearanceTier。</summary>
        public PeerVisual VisualOf(AvatarPosition avatar)
        {
            return WorldPeerTiers.For(avatar);
        }

        /// <summary>三档的展示名。未识别的档位按最省的一档叫。</summary>
        public static string TierName(AppearanceTier tier)
        {
            switch (tier)
            {
                case AppearanceTier.Full:
                    return UiWorldCodex.TierLabels[0];
                case AppearanceTier.Simple:
                    return UiWorldCodex.TierLabels[1];
                default:
                    return UiWorldCodex.TierLabels[2];
            }
        }

        /// <summary>HUD 底部那行：分线、容量、周围连通的地表，全部来自进分线回包。</summary>
        public string EnterLine()
        {
            if (!HasEnter)
            {
                return UiWorldCodex.NoEnterYet;
            }

            switch (Enter.Kind)
            {
                case WorldEnterKind.Entered:
                    return Enter.RegionId + " · 第 " + Enter.LineNo + " 线"
                        + " · " + Enter.LineSize + " / " + Enter.LineCapacity + " 人"
                        + (Enter.Duplicate ? " · 原来就在这条线" : string.Empty);
                case WorldEnterKind.Refused:
                    return "没进去 · " + UiWorldCodex.ReasonText(Enter.Reason);
                case WorldEnterKind.Network:
                    return "连不上网关 · 检查网络再试";
                case WorldEnterKind.InvalidToken:
                    return "令牌失效 · 回登录重来";
                case WorldEnterKind.Unavailable:
                    return "世界服暂时不可用 · 稍后再试";
                default:
                    return "进分线没成 · " + Enter.Error;
            }
        }

        /// <summary>跨区那行。被拒时写原因与「留在原地」，不写一个假装成功的目标。</summary>
        public string RegionLine()
        {
            if (!HasRegion)
            {
                return UiWorldCodex.NoRegionYet;
            }

            if (RegionReply.Ok)
            {
                return "已到 " + RegionReply.RegionId
                    + (RegionReply.LineId.Length > 0 ? " · 第 " + RegionReply.LineNo + " 线" : string.Empty);
            }

            var tail = RegionReply.RequiredRealm.Length > 0
                ? "（这块地表要 " + RegionReply.RequiredRealm + "）"
                : string.Empty;
            return "没跨成 · " + UiWorldCodex.ReasonText(RegionReply.Reason) + tail
                + (RegionId.Length > 0 ? " · 仍在 " + RegionId : string.Empty);
        }

        /// <summary>采集那行。产物与数量照抄回包，没回包就写「尚未采集」。</summary>
        public string GatherLine()
        {
            if (!HasGather)
            {
                return UiWorldCodex.NoGatherYet;
            }

            if (GatherReply.Ok)
            {
                return GatherReply.DisplayText
                    + " · 入格位 " + GatherReply.BagSlot
                    + (GatherReply.RespawnSeconds > 0 ? " · " + GatherReply.RespawnSeconds + " 秒后刷新" : string.Empty);
            }

            return "没采到 · " + UiWorldCodex.ReasonText(GatherReply.Reason);
        }

        /// <summary>飞行那行。成功后写回包高度，失败写原因与拉回后的高度。</summary>
        public string FlyLine()
        {
            if (!HasFly)
            {
                return UiWorldCodex.NoFlyYet;
            }

            if (FlyReply.Ok)
            {
                return (FlyReply.Flying ? "已起飞" : "已落地")
                    + " · 高度 " + FlyReply.Y.ToString("0.##")
                    + " · 境界 " + (FlyReply.Realm.Length > 0 ? FlyReply.Realm : "—");
            }

            return "飞不起来 · " + UiWorldCodex.ReasonText(FlyReply.Reason)
                + (FlyReply.RequiredRealm.Length > 0 ? "（要 " + FlyReply.RequiredRealm + "）" : string.Empty)
                + " · 已回到地面高度 " + FlyReply.Y.ToString("0.##");
        }

        /// <summary>屏上写的高度，与 <see cref="ScreenY"/> 同源。</summary>
        public string YLine()
        {
            return HasFly
                ? "画面高度 " + ScreenY.ToString("0.##") + " · 拖尾 " + (TrailAllowed ? "开" : "关")
                : "画面高度以回包为准 · 拖尾只在能飞时开";
        }
    }
}
