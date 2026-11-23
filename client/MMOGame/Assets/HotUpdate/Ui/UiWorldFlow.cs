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
    public sealed partial class UiWorldFlow : MonoBehaviour
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

        public bool HasGatherNodes { get; private set; }

        public bool HasFly { get; private set; }

        public bool HasRegion { get; private set; }

        /// <summary>最近一次进分线回包。没发过就是 default，界面照「尚未进分线」显示。</summary>
        public WorldEnterOutcome Enter { get; private set; }

        /// <summary>地点表。以服务端给的为准，不用本地展示表顶替。</summary>
        public WorldRegionsOutcome RegionsReply { get; private set; }

        public GatherOutcome GatherReply { get; private set; }

        public GatherNodesOutcome GatherNodesReply { get; private set; }

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
