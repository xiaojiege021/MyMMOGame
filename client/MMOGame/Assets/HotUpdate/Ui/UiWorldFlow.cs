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
    /// 表世界五屏的流程与唯一判定出口（原型 B01–B05）。
    ///
    /// 这一层只做三件事：把请求发出去、把回包拆成人能读的字段、把字段交给屏。
    /// 能不能进这块地表、分到第几条线、采到什么、能不能飞、飞到多高，一条都不在这里算——
    /// 屏上出现的每个数字要么来自 `/world/*` 回包，要么来自世界服推送的快照，
    /// 没有第三种来源。没收到回包时屏上写「尚未…」，不拿配置里的顶格值假装。
    ///
    /// 同屏三档一律交给 <see cref="WorldPeerTiers"/> 翻译服务端下发的 AppearanceTier，
    /// 这里不读坐标、不算距离、不按两块阈值自己推档位。
    ///
    /// 与 U2 / U3 同一个形状：发送可以替换（<see cref="SetSender"/>），
    /// 编辑器自检拿它喂假回包，真机走 <see cref="SendByUnityWebRequest"/>。
    /// </summary>
    public sealed class UiWorldFlow : MonoBehaviour
    {
        /// <summary>这一层建好的日志行。</summary>
        public const string ReadyLog = "u4_world=ready";

        /// <summary>网关地址。真机用 `adb reverse` 把 61000 转出来。</summary>
        public static string Gateway = "http://127.0.0.1:61000";

        /// <summary>发一次请求。GET 的 body 传空串。</summary>
        public delegate void WorldSend(string method, string path, string body, string token, Action<int, string> done);

        UiShell _shell;

        WorldSend _send;

        string _token = string.Empty;

        ulong _characterId;

        readonly List<Action> _views = new List<Action>();

        /// <summary>正在等回包。等的时候按钮不可按，免得连点发两次。</summary>
        public bool Busy { get; private set; }

        /// <summary>发过几次请求。核对拿它判断「没联网时也确实没发」。</summary>
        public int Requests { get; private set; }

        // ---------------------------------------------------------------- 状态

        public bool HasEnter { get; private set; }

        public bool HasRegions { get; private set; }

        public bool HasGather { get; private set; }

        public bool HasFly { get; private set; }

        public bool HasRegion { get; private set; }

        /// <summary>最近一次进分线回包。没发过就是 default，界面照「尚未进分线」显示。</summary>
        public WorldEnterOutcome Enter { get; private set; }

        /// <summary>地点表。以服务端给的为准，不用本地展示表顶替。</summary>
        public WorldRegionsOutcome RegionsReply { get; private set; }

        public GatherOutcome GatherReply { get; private set; }

        public FlyOutcome FlyReply { get; private set; }

        public RegionOutcome RegionReply { get; private set; }

        /// <summary>当前站在哪块地表。只由进分线 / 跨区的回包写，界面改不了。</summary>
        public string RegionId { get; private set; } = string.Empty;

        /// <summary>现在分到第几条线。只由回包写。</summary>
        public string LineId { get; private set; } = string.Empty;

        /// <summary>这一屏选中的采集点与跨区目标（只是选中态，不是结果）。</summary>
        public string ChosenNode { get; private set; } = "ore_kulan_1";

        public string ChosenRegion { get; private set; } = string.Empty;

        /// <summary>
        /// 画面该画在多高。有飞行回包就用回包里的 y——
        /// 被拒时世界服给的是拉回后的地面高度，这里照抄，不保留起飞前本地攒的高度。
        /// 没回包时是 0，屏上同时写「以回包为准」。
        /// </summary>
        public float ScreenY => HasFly ? FlyReply.Y : 0f;

        /// <summary>能不能画拖尾。只跟服务端判完的 flying 走，买了拖尾也不算。</summary>
        public bool TrailAllowed => HasFly && FlyReply.TrailAllowed;

        /// <summary>挂到骨架上。已有就复用，不重复建。</summary>
        public static UiWorldFlow Ensure(UiShell shell)
        {
            var existing = shell.GetComponent<UiWorldFlow>();
            if (existing != null)
            {
                existing.Init(shell);
                return existing;
            }

            var flow = shell.gameObject.AddComponent<UiWorldFlow>();
            flow.Init(shell);
            return flow;
        }

        /// <summary>建一次。编辑模式下 `AddComponent` 不触发 `Awake`，所以这里由 `Ensure` 显式调。</summary>
        public void Init(UiShell shell)
        {
            _shell = shell;
            if (_send == null)
            {
                _send = SendByUnityWebRequest;
            }

            Debug.Log(ReadyLog + " gateway=" + Gateway + " screens=5");
        }

        /// <summary>令牌与角色号。U2 出口确认之后由入口流程交过来。</summary>
        public void Configure(string token, ulong characterId)
        {
            _token = token ?? string.Empty;
            _characterId = characterId;
        }

        /// <summary>换掉发送实现。自检与探针走这条路，不真的连网关。</summary>
        public void SetSender(WorldSend send)
        {
            _send = send ?? SendByUnityWebRequest;
        }

        /// <summary>
        /// 肉眼验收用：确保骨架与本流程在，然后开指定的一屏
        /// （`hud` / `map` / `dialogue` / `gather` / `fly`）。
        /// 编辑器菜单走反射调这里，不填令牌也不造假回包——屏上写的就是没回包时的样子。
        /// </summary>
        public static void Open(string which)
        {
            var shell = UiShell.Ensure();
            var flow = Ensure(shell);
            switch (which)
            {
                case "map":
                    shell.ShowPage("世界地图", s => UiWorldMapScreen.Build(s, flow));
                    break;
                case "dialogue":
                    shell.ShowPage("对白", s => UiDialogueScreen.Build(s, flow));
                    break;
                case "gather":
                    shell.ShowPage("采集", s => UiGatherScreen.Build(s, flow));
                    break;
                case "fly":
                    shell.ShowPage("飞行", s => UiFlightScreen.Build(s, flow));
                    break;
                default:
                    shell.ShowPage("表世界", s => UiWorldHudScreen.Build(s, flow));
                    break;
            }

            Debug.Log("u4_preview=" + which + " gateway=" + Gateway);
        }

        // ---------------------------------------------------------------- 视图

        /// <summary>屏建完把自己的刷新函数挂上来。每次回包落地都会调一遍。</summary>
        public void RegisterView(Action refresh)
        {
            if (refresh == null)
            {
                return;
            }

            _views.Add(refresh);
            refresh();
        }

        /// <summary>换屏前先清干净：旧屏的控件已经销毁，留着会在下一次刷新时撞空引用。</summary>
        public void ClearViews()
        {
            _views.Clear();
        }

        void RefreshViews()
        {
            for (var i = 0; i < _views.Count; i++)
            {
                var view = _views[i];
                if (view != null)
                {
                    view();
                }
            }
        }

        // ---------------------------------------------------------------- 动作

        /// <summary>
        /// 进分线。请求里只有角色号与地表编号；分到第几线、这条线满不满都由世界服定。
        /// 界面不预判结果：按下就是等回包。
        /// </summary>
        public void SubmitEnter(string regionId)
        {
            if (Busy)
            {
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
            });
        }

        /// <summary>读一次地点表。八块地表的邻接、开放与对白编号都以这份回包为准。</summary>
        public void LoadRegions()
        {
            if (Busy)
            {
                return;
            }

            Busy = true;
            Requests++;
            RefreshViews();

            _send("GET", "/world/regions", string.Empty, _token, (status, text) =>
            {
                Busy = false;
                RegionsReply = WorldEntry.ParseRegions(status, text);
                HasRegions = RegionsReply.Kind == WorldEnterKind.Entered;

                Debug.Log("u4_regions=reply status=" + status
                    + " kind=" + RegionsReply.Kind
                    + " regions=" + (RegionsReply.Regions != null ? RegionsReply.Regions.Length : 0)
                    + " speakers=" + (RegionsReply.Speakers != null ? RegionsReply.Speakers.Length : 0)
                    + " version=" + RegionsReply.Version);

                RefreshViews();
            });
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

        // ---------------------------------------------------------------- 发送

        void SendByUnityWebRequest(string method, string path, string body, string token, Action<int, string> done)
        {
            StartCoroutine(SendRoutine(method, path, body, token, done));
        }

        IEnumerator SendRoutine(string method, string path, string body, string token, Action<int, string> done)
        {
            var url = Gateway + path;
            using (var request = new UnityWebRequest(url, method))
            {
                if (method != "GET")
                {
                    var payload = System.Text.Encoding.UTF8.GetBytes(body ?? string.Empty);
                    request.uploadHandler = new UploadHandlerRaw(payload);
                }

                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(token))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + token);
                }

                yield return request.SendWebRequest();

                // 连接没建起来时没有 HTTP 状态，按 0 交给调用方——那是不算「服务端拒绝」的一档。
                var status = request.result == UnityWebRequest.Result.ConnectionError ? 0 : (int)request.responseCode;
                var text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                done(status, text ?? string.Empty);
            }
        }
    }
}
