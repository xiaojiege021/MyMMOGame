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
    /// <summary>一个城战单位在屏上该画成什么样。编号与血量都是服务端下发的。</summary>
    public struct SiegeRow
    {
        public ulong MemberId;
        public uint Team;
        public long Hp;
        public long HpMax;
        public SiegeVisualSpec Visual;
    }

    /// <summary>
    /// U7 社交与组织五屏的流程与唯一判定出口（原型 F01–F05）。
    ///
    /// 只做三件事：发请求、拆回包、把字段交给屏。名册上限、等级、席位、建设、
    /// 掌门、世界 Boss 归属、城战同屏档位、洞府摆放一条都不在这里算——要么来自
    /// `/sect/*`、`/world/worldboss`、`/homestead*`，要么来自战斗服下发的 `SiegeView`。
    /// </summary>
    public sealed class UiSocialFlow : MonoBehaviour
    {
        public const string ReadyLog = "u7_social=ready";

        public static string Gateway = "http://127.0.0.1:61000";

        public delegate void SocialSend(string path, string body, string token, Action<int, string> done);

        UiShell _shell;

        SocialSend _send;

        string _token = string.Empty;

        ulong _characterId;

        public bool Busy { get; private set; }

        public int Requests { get; private set; }

        public SectOutcome LastSect { get; private set; }

        public bool HasSect { get; private set; }

        public PositionOutcome LastPosition { get; private set; }

        public bool HasPosition { get; private set; }

        public LeadershipOutcome LastLeadership { get; private set; }

        public bool HasLeadership { get; private set; }

        public UpgradeOutcome LastUpgrade { get; private set; }

        public bool HasUpgrade { get; private set; }

        public RosterOutcome LastRoster { get; private set; }

        public bool HasRoster { get; private set; }

        public WorldBossFacts LastBoss { get; private set; }

        public bool HasBoss { get; private set; }

        public bool HasSiege { get; private set; }

        public uint SiegeFullLimit { get; private set; }

        public uint SiegeFullCount { get; private set; }

        public uint SiegeUnitCount { get; private set; }

        public List<SiegeRow> SiegeRows { get; private set; } = new List<SiegeRow>();

        public HomesteadOutcome LastHomestead { get; private set; }

        public bool HasHomestead { get; private set; }

        public BagOutcome HomesteadBag { get; private set; } = BagOutcome.Failure("not_loaded");

        public bool HasHomesteadBag { get; private set; }

        public string Error { get; private set; } = string.Empty;

        /// <summary>最近一次回包给的宗门编号。没有回包就是 0，屏上不猜一个。</summary>
        public ulong SectId
        {
            get
            {
                if (LastSect.SectId != 0)
                {
                    return LastSect.SectId;
                }

                if (LastPosition.SectId != 0)
                {
                    return LastPosition.SectId;
                }

                if (LastLeadership.SectId != 0)
                {
                    return LastLeadership.SectId;
                }

                return LastUpgrade.SectId;
            }
        }

        public static UiSocialFlow Ensure(UiShell shell)
        {
            var existing = FindObjectOfType<UiSocialFlow>();
            if (existing != null)
            {
                existing._shell = shell;
                return existing;
            }

            var go = new GameObject("UiSocialFlow");
            var flow = go.AddComponent<UiSocialFlow>();
            flow._shell = shell;
            flow._send = flow.SendByUnityWebRequest;
            Debug.Log(ReadyLog);
            return flow;
        }

        public void SetSender(SocialSend send)
        {
            _send = send;
        }

        public void Bind(string token, ulong characterId)
        {
            _token = token ?? string.Empty;
            _characterId = characterId;
        }

        public ulong CharacterId => _characterId;

        // ------------------------------------------------------------------ 宗门

        public void Create(string name, Action done = null)
        {
            Send("/sect/create", SectGuild.CreateBody(_characterId, name),
                SectGuild.ParseSect, outcome => { LastSect = outcome; HasSect = true; },
                "u7_sect_create", done);
        }

        public void Join(ulong sectId, Action done = null)
        {
            Send("/sect/join", SectGuild.JoinBody(_characterId, sectId),
                SectGuild.ParseSect, outcome => { LastSect = outcome; HasSect = true; },
                "u7_sect_join", done);
        }

        public void Upgrade(ulong sectId, Action done = null)
        {
            Send("/sect/upgrade", SectGuild.UpgradeBody(_characterId, sectId),
                SectGuild.ParseUpgrade, outcome => { LastUpgrade = outcome; HasUpgrade = true; },
                "u7_sect_upgrade", done);
        }

        public void Appoint(ulong sectId, ulong targetId, string position, Action done = null)
        {
            Send("/sect/appoint", SectGuild.PositionBody(_characterId, sectId, targetId, position),
                SectGuild.ParsePosition, outcome => { LastPosition = outcome; HasPosition = true; },
                "u7_sect_appoint", done);
        }

        public void Dismiss(ulong sectId, ulong targetId, string position, Action done = null)
        {
            Send("/sect/dismiss", SectGuild.PositionBody(_characterId, sectId, targetId, position),
                SectGuild.ParsePosition, outcome => { LastPosition = outcome; HasPosition = true; },
                "u7_sect_dismiss", done);
        }

        public void Transfer(ulong sectId, ulong targetId, Action done = null)
        {
            Send("/sect/transfer", SectGuild.TransferBody(_characterId, sectId, targetId),
                SectGuild.ParseLeadership, outcome => { LastLeadership = outcome; HasLeadership = true; },
                "u7_sect_transfer", done);
        }

        public void Leave(ulong sectId, Action done = null)
        {
            Send("/sect/leave", SectGuild.LeaveBody(_characterId, sectId),
                SectGuild.ParseLeadership, outcome => { LastLeadership = outcome; HasLeadership = true; },
                "u7_sect_leave", done);
        }

        /// <summary>
        /// 读名册。只带宗门编号，走 GET /sect/roster。不任免、不改人数上限、不写库。
        /// 非 200 不算一份名册回包。
        /// </summary>
        public void LoadRoster(ulong sectId, Action done = null)
        {
            if (sectId == 0)
            {
                return;
            }

            Busy = true;
            Requests++;
            _send("/sect/roster?sect_id=" + sectId.ToString(CultureInfo.InvariantCulture),
                string.Empty, _token, (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastRoster = SectGuild.ParseRoster(text);
                        HasRoster = true;
                        Error = string.Empty;
                        var rows = LastRoster.Members != null ? LastRoster.Members.Count : 0;
                        Debug.Log("n3_roster=ok rows=" + rows.ToString(CultureInfo.InvariantCulture)
                            + " reason=" + LastRoster.Reason);
                    }
                    else
                    {
                        HasRoster = false;
                        Error = "名册没读到：" + status.ToString(CultureInfo.InvariantCulture);
                        Debug.Log("n3_roster=reject status=" + status.ToString(CultureInfo.InvariantCulture));
                    }

                    if (done != null) done();
                });
        }

        /// <summary>读世界 Boss 入口的只读事实值。请求体为空，走 GET。</summary>
        public void LoadBoss(Action done = null)
        {
            Busy = true;
            Requests++;
            _send(WorldBossEntry.RequestPath, string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastBoss = WorldBossEntry.Parse(text);
                    HasBoss = true;
                    Debug.Log("u7_boss=ok visible=" + LastBoss.Visible
                        + " owner=" + (LastBoss.OwnerSectName.Length == 0 ? "none" : LastBoss.OwnerSectName));
                }
                else
                {
                    Error = "世界 Boss 入口没读到：" + status;
                    Debug.Log("u7_boss=reject status=" + status);
                }

                if (done != null) done();
            });
        }

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

        // ------------------------------------------------------------------ 城战同屏

        /// <summary>
        /// 把战斗服下发的城战同屏快照翻译成展示规格。档位只有服务端给，
        /// 这里按 <see cref="SiegeViewTiers"/> 翻一次，不按距离或人数重排。
        /// </summary>
        public void ApplySiegeView(SiegeView view)
        {
            if (view == null)
            {
                return;
            }

            SiegeFullLimit = view.FullModelLimit;
            SiegeFullCount = view.FullModelCount;
            SiegeUnitCount = view.UnitCount;
            SiegeRows.Clear();

            var markers = 0;
            for (var i = 0; i < view.Units.Count; i++)
            {
                var unit = view.Units[i];
                var visual = SiegeViewTiers.For(unit);
                SiegeRows.Add(new SiegeRow
                {
                    MemberId = unit.MemberId,
                    Team = unit.Team,
                    Hp = unit.Hp,
                    HpMax = unit.HpMax,
                    Visual = visual,
                });

                if (visual.Kind == SiegeVisualKind.MarkerOnly)
                {
                    markers++;
                }
            }

            HasSiege = true;
            Debug.Log("u7_siege_view units=" + SiegeUnitCount
                + " full=" + SiegeFullCount + "/" + SiegeFullLimit
                + " markers=" + markers);
        }

        /// <summary>战斗进程收到城战同屏快照时调这里，由它交给屏。没有屏就只记一行。</summary>
        public static void Feed(SiegeView view)
        {
            if (view == null)
            {
                return;
            }

            var flow = FindObjectOfType<UiSocialFlow>();
            if (flow == null)
            {
                Debug.Log("u7_siege_view units=" + view.UnitCount + " (no_screen)");
                return;
            }

            flow.ApplySiegeView(view);
        }

        /// <summary>超限那些行：只有队伍颜色和血条，没有完整模型与名字牌。</summary>
        public List<SiegeRow> Markers()
        {
            var rows = new List<SiegeRow>();
            for (var i = 0; i < SiegeRows.Count; i++)
            {
                if (SiegeRows[i].Visual.Kind == SiegeVisualKind.MarkerOnly)
                {
                    rows.Add(SiegeRows[i]);
                }
            }

            return rows;
        }

        // ------------------------------------------------------------------ 发送

        void Send<T>(string path, string body,
            Func<string, T> parse, Action<T> keep, string logTag, Action done)
        {
            if (_characterId == 0)
            {
                return;
            }

            Busy = true;
            Requests++;
            _send(path, body, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    var outcome = parse(text);
                    keep(outcome);
                    Error = string.Empty;
                    Debug.Log(logTag + "=ok");
                }
                else
                {
                    Error = "请求失败：" + status;
                    Debug.Log(logTag + "=reject status=" + status);
                }

                if (done != null) done();
            });
        }

        void SendByUnityWebRequest(string path, string body, string token, Action<int, string> done)
        {
            var isGet = string.IsNullOrEmpty(body);
            var request = new UnityWebRequest(
                Gateway + path, isGet ? UnityWebRequest.kHttpVerbGET : UnityWebRequest.kHttpVerbPOST);
            if (!isGet)
            {
                request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
                request.SetRequestHeader("Content-Type", "application/json");
            }

            if (!string.IsNullOrEmpty(token))
            {
                request.SetRequestHeader("Authorization", "Bearer " + token);
            }

            request.downloadHandler = new DownloadHandlerBuffer();
            request.SendWebRequest().completed += _ =>
            {
                var status = (int)request.responseCode;
                var text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                done(status, text);
                request.Dispose();
            };
        }
    }
}
