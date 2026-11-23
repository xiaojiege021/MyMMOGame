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
    /// 表世界五屏的动作：进分线、读地点表、跨区、采集、飞行。
    /// </summary>
    public sealed partial class UiWorldFlow : MonoBehaviour
    {
        // ---------------------------------------------------------------- 动作

        /// <summary>
        /// 进分线。请求里只有角色号与地表编号；分到第几线、这条线满不满都由世界服定。
        /// 界面不预判结果：按下就是等回包。可选 <paramref name="done"/> 给 U8 载入闸门用。
        /// </summary>
        public void SubmitEnter(string regionId, Action done = null)
        {
            if (Busy)
            {
                if (done != null)
                {
                    done();
                }

                return;
            }

            Busy = true;
            Requests++;
            RefreshViews();

            var body = WorldEntry.EnterBody(_characterId, regionId);
            _send("POST", "/world/enter", body, _token, (status, text) =>
            {
                Busy = false;
                Enter = WorldEntry.ParseEnter(status, text);
                HasEnter = true;
                if (Enter.Kind == WorldEnterKind.Entered || Enter.Kind == WorldEnterKind.Refused)
                {
                    // 被拒时世界服回的是原来那块地表，照抄，不保留本地以为进了的目标。
                    if (Enter.RegionId.Length > 0)
                    {
                        RegionId = Enter.RegionId;
                    }

                    LineId = Enter.LineId;
                }

                Debug.Log("u4_enter=reply status=" + status
                    + " kind=" + Enter.Kind
                    + " reason=" + Enter.Reason
                    + " region=" + Enter.RegionId
                    + " line=" + Enter.LineNo
                    + " size=" + Enter.LineSize + "/" + Enter.LineCapacity
                    + " entered=" + (Enter.Entered ? "true" : "false"));

                RefreshViews();
                if (done != null)
                {
                    done();
                }
            });
        }

        /// <summary>读一次地点表。八块地表的邻接、开放与对白编号都以这份回包为准。</summary>
        /// <summary>节点表以回包为准。没请求前屏上写尚未请求。</summary>
        public void LoadGatherNodes(Action onDone = null)
        {
            if (Busy)
            {
                return;
            }

            Busy = true;
            Requests++;
            RefreshViews();
            _send("GET", "/world/gather/nodes", string.Empty, _token, (status, text) =>
            {
                Busy = false;
                GatherNodesReply = WorldGather.ParseList(status, text);
                HasGatherNodes = status == 200 && GatherNodesReply.Error.Length == 0;

                Debug.Log("u4_gather_nodes=reply status=" + status
                    + " count=" + GatherNodesReply.Count
                    + " nodes=" + (GatherNodesReply.Nodes != null ? GatherNodesReply.Nodes.Length : 0)
                    + " error=" + GatherNodesReply.Error);

                onDone?.Invoke();
                RefreshViews();
            });
        }

        public void LoadRegions()
        {
            if (Busy)
            {
                return;
            }

            Busy = true;
            Requests++;
            RefreshViews();

            _send("GET", WorldEntry.RegionsPath(_characterId), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                RegionsReply = WorldEntry.ParseRegions(status, text);
                HasRegions = RegionsReply.Kind == WorldEnterKind.Entered;

                Debug.Log("u4_regions=reply status=" + status
                    + " kind=" + RegionsReply.Kind
                    + " character=" + _characterId
                    + " regions=" + (RegionsReply.Regions != null ? RegionsReply.Regions.Length : 0)
                    + " open=" + CountOpenRegions(RegionsReply)
                    + " speakers=" + (RegionsReply.Speakers != null ? RegionsReply.Speakers.Length : 0)
                    + " version=" + RegionsReply.Version);

                RefreshViews();
            });
        }

        /// <summary>
        /// 一块地表这一刻开没开，只由 `/world/regions` 回包说：有这一条就用回包的 open 与 reason，
        /// 没有回包就 false（屏上退回配置静态标并写明「还没读地点表」）。
        /// 这里不算门槛、不看主线进度——进度在服务端，客户端带不进来也猜不出来。
        /// </summary>
        public bool TryRegionState(string regionId, out bool open, out string text)
        {
            if (HasRegions && WorldEntry.TryRegion(RegionsReply, regionId, out var row))
            {
                open = row.Open;
                text = WorldEntry.RegionStateText(row);
                return true;
            }

            open = false;
            text = string.Empty;
            return false;
        }

        static int CountOpenRegions(WorldRegionsOutcome outcome)
        {
            if (outcome.Regions == null)
            {
                return 0;
            }

            var open = 0;
            for (var i = 0; i < outcome.Regions.Length; i++)
            {
                if (outcome.Regions[i].Open)
                {
                    open++;
                }
            }

            return open;
        }

        /// <summary>在世界地图上换一个目标。只动选中态，不发请求——发不发由「跨过去」决定。</summary>
        public void ChooseRegion(string regionId)
        {
            if (Busy)
            {
                return;
            }

            ChosenRegion = regionId ?? string.Empty;
            RefreshViews();
        }

        /// <summary>跨区。请求只带目标地表编号；相连、开放与境界门槛都在世界服判。</summary>
        public void CrossRegion(string target)
        {
            if (Busy || string.IsNullOrEmpty(target))
            {
                return;
            }

            ChosenRegion = target;
            Busy = true;
            Requests++;
            RefreshViews();

            var body = WorldFlight.RegionRequestBody(target);
            _send("POST", "/world/region", body, _token, (status, text) =>
            {
                Busy = false;
                RegionReply = status == 200
                    ? WorldFlight.ParseRegion(text)
                    : RejectedRegion(status, text);
                HasRegion = true;
                if (RegionReply.Ok && RegionReply.RegionId.Length > 0)
                {
                    RegionId = RegionReply.RegionId;
                    LineId = RegionReply.LineId;
                }

                Debug.Log("u4_region=reply status=" + status
                    + " ok=" + (RegionReply.Ok ? "true" : "false")
                    + " reason=" + RegionReply.Reason
                    + " region=" + RegionReply.RegionId
                    + " target=" + target
                    + " required=" + RegionReply.RequiredRealm);

                RefreshViews();
            });
        }

        /// <summary>采集。产物、数量、格位、刷新秒数都由世界服判完再回，界面只照抄。</summary>
        public void Gather(string nodeId)
        {
            if (Busy || string.IsNullOrEmpty(nodeId))
            {
                return;
            }

            ChosenNode = nodeId;
            Busy = true;
            Requests++;
            RefreshViews();

            var body = WorldGather.RequestBody(nodeId);
            _send("POST", "/world/gather", body, _token, (status, text) =>
            {
                Busy = false;
                GatherReply = status == 200 ? WorldGather.Parse(text) : RejectedGather(status, text);
                HasGather = true;

                Debug.Log("u4_gather=reply status=" + status
                    + " ok=" + (GatherReply.Ok ? "true" : "false")
                    + " reason=" + GatherReply.Reason
                    + " node=" + GatherReply.NodeId
                    + " item=" + GatherReply.ItemId
                    + " count=" + GatherReply.Count
                    + " slot=" + GatherReply.BagSlot);

                RefreshViews();
            });
        }

        /// <summary>
        /// 起飞或落地。权限由世界服问角色服的境界后判；
        /// 没权限时回包里的 y 是拉回后的地面高度，画面按它回地面。
        /// </summary>
        public void Fly(bool wantFly)
        {
            if (Busy)
            {
                return;
            }

            Busy = true;
            Requests++;
            RefreshViews();

            var body = WorldFlight.FlyRequestBody(wantFly);
            _send("POST", "/world/fly", body, _token, (status, text) =>
            {
                Busy = false;
                FlyReply = status == 200 ? WorldFlight.ParseFly(text) : RejectedFly(status, text);
                HasFly = true;

                Debug.Log("u4_fly=reply status=" + status
                    + " ok=" + (FlyReply.Ok ? "true" : "false")
                    + " reason=" + FlyReply.Reason
                    + " flying=" + (FlyReply.Flying ? "true" : "false")
                    + " can_fly=" + (FlyReply.CanFly ? "true" : "false")
                    + " realm=" + FlyReply.Realm
                    + " required=" + FlyReply.RequiredRealm
                    + " y=" + FlyReply.Y.ToString("0.###"));

                RefreshViews();
            });
        }
    }
}
