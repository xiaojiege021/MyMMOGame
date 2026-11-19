using System;
using System.Collections.Generic;
using Mmo.Net;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U5 角色养成七屏的流程与唯一判定出口（原型 D01–D07）。
    ///
    /// 这一层只做三件事：把请求发出去、把回包拆成字段、把字段交给屏。
    /// 强化成功率、费用、等级、绑定、镶嵌位、背包分类一条都不在这里算——
    /// 屏上出现的数要么来自 `/inventory`，要么来自 `/character/enhance`、
    /// `/character/transfer`、`/character/crystal/*` 的回包；没收到回包就写「尚未请求」。
    ///
    /// 与 U3 / U4 同一个形状：发送可以替换（<see cref="SetSender"/>），
    /// 编辑器自检拿它喂假回包，真机走 <see cref="SendByUnityWebRequest"/>。
    /// </summary>
    public sealed class UiUpgradeFlow : MonoBehaviour
    {
        /// <summary>这一层建好的日志行。</summary>
        public const string ReadyLog = "u5_upgrade=ready";

        /// <summary>网关地址。真机用 `adb reverse` 把 61000 转出来。</summary>
        public static string Gateway = "http://127.0.0.1:61000";

        /// <summary>发一次请求。参数是路径、请求体（空串表示 GET）、令牌、回调。</summary>
        public delegate void UpgradeSend(string path, string body, string token, Action<int, string> done);

        UiShell _shell;

        UpgradeSend _send;

        string _token = string.Empty;

        ulong _characterId;

        /// <summary>正在等回包。等的时候按钮不可按，免得连点。</summary>
        public bool Busy { get; private set; }

        /// <summary>发过几次请求。核对拿它判断「没联网时也确实没发」。</summary>
        public int Requests { get; private set; }

        /// <summary>背包回包。没发过就是 default，屏上照「尚未读背包」显示。</summary>
        public BagOutcome Bag { get; private set; } = BagOutcome.Failure("not_loaded");

        public bool HasBag { get; private set; }

        public EnhanceOutcome LastEnhance { get; private set; }

        public bool HasEnhance { get; private set; }

        public TransferOutcome LastTransfer { get; private set; }

        public bool HasTransfer { get; private set; }

        public CrystalOutcome LastCrystal { get; private set; }

        public bool HasCrystal { get; private set; }

        /// <summary>U13：段内阶只读回包（N13 的 `GET /realm/phase`）。</summary>
        public RealmPhaseOutcome LastRealmPhase { get; private set; } = RealmPhaseOutcome.Failure("not_loaded");

        /// <summary>发过只读请求没有。屏上用它区分「尚未请求」与「没读到」。</summary>
        public bool RealmPhaseRequested { get; private set; }

        /// <summary>U13：升一阶回包（`POST /realm/phase/promote`）。</summary>
        public RealmPhaseOutcome LastPromote { get; private set; } = RealmPhaseOutcome.Failure("not_loaded");

        public bool HasPromote { get; private set; }

        /// <summary>最近一条失败说明（没到网关 / 令牌失效 / 服务端 5xx）。</summary>
        public string Error { get; private set; } = string.Empty;

        /// <summary>取一份流程，没有就建。</summary>
        public static UiUpgradeFlow Ensure(UiShell shell)
        {
            var existing = FindObjectOfType<UiUpgradeFlow>();
            if (existing != null)
            {
                existing._shell = shell;
                return existing;
            }

            var go = new GameObject("UiUpgradeFlow");
            var flow = go.AddComponent<UiUpgradeFlow>();
            flow._shell = shell;
            flow._send = flow.SendByUnityWebRequest;
            Debug.Log(ReadyLog);
            return flow;
        }

        /// <summary>换掉发送实现。自检与真机共用同一条解析路径。</summary>
        public void SetSender(UpgradeSend send)
        {
            _send = send;
        }

        /// <summary>绑定会话。U2 登入后把令牌与角色号带过来。</summary>
        public void Bind(string token, ulong characterId)
        {
            _token = token ?? string.Empty;
            _characterId = characterId;
        }

        public ulong CharacterId => _characterId;

        public bool HasSession => _characterId != 0;

        // ------------------------------------------------------------------ 动作

        /// <summary>读背包。界面进 U5 时先调一次。</summary>
        public void LoadBag(Action done = null)
        {
            if (_characterId == 0)
            {
                Bag = BagOutcome.Failure("no_character");
                HasBag = false;
                Debug.Log("u5_bag=reject reason=no_character");
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterBag.RequestPath(_characterId), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status != 200)
                {
                    Bag = BagOutcome.Failure("status_" + status);
                    HasBag = false;
                    Error = "背包没读到：" + status;
                    Debug.Log("u5_bag=reject status=" + status);
                }
                else
                {
                    Bag = CharacterBag.Parse(text);
                    HasBag = Bag.Ok;
                    Error = Bag.Ok ? string.Empty : "背包回包读不出来";
                    Debug.Log("u5_bag=" + (Bag.Ok ? "ok" : "reject")
                        + " slots=" + (Bag.Slots != null ? Bag.Slots.Count : 0)
                        + " bag=" + Bag.BagSlots
                        + " name=" + Bag.CharacterName
                        + " realm=" + Bag.Realm
                        + " realm_display=" + Bag.DisplayRealm);
                }

                if (done != null) done();
            });
        }

        /// <summary>强化一格。石头编号只作请求参数，成不成、掉几级、扣多少全看回包。</summary>
        public void Enhance(uint slot, string stoneId, bool useCoupon, Action done = null)
        {
            if (_characterId == 0)
            {
                LastEnhance = new EnhanceOutcome { Reason = "no_character", DisplayText = "还没有角色" };
                HasEnhance = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            var body = CharacterEnhance.RequestBody(_characterId, slot, stoneId, useCoupon);
            _send("/character/enhance", body, _token, (status, text) =>
            {
                Busy = false;
                if (status != 200)
                {
                    LastEnhance = new EnhanceOutcome { Reason = "status_" + status, DisplayText = "强化没发出去：" + status };
                    Error = LastEnhance.DisplayText;
                    Debug.Log("u5_enhance=reject status=" + status);
                }
                else
                {
                    LastEnhance = CharacterEnhance.Parse(text);
                    Error = LastEnhance.Ok ? string.Empty : LastEnhance.DisplayText;
                    Debug.Log("u5_enhance=" + (LastEnhance.Ok ? "ok" : "reject")
                        + " before=" + LastEnhance.LevelBefore
                        + " level=" + LastEnhance.Level
                        + " success=" + (LastEnhance.Success ? "true" : "false")
                        + " reason=" + LastEnhance.Reason);
                }

                HasEnhance = true;
                if (done != null) done();
            });
        }

        /// <summary>把强化从一格转到另一格。</summary>
        public void Transfer(uint fromSlot, uint toSlot, Action done = null)
        {
            if (_characterId == 0)
            {
                LastTransfer = new TransferOutcome { Reason = "no_character", DisplayText = "还没有角色" };
                HasTransfer = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            var body = CharacterTransfer.RequestBody(_characterId, fromSlot, toSlot);
            _send("/character/transfer", body, _token, (status, text) =>
            {
                Busy = false;
                if (status != 200)
                {
                    LastTransfer = new TransferOutcome { Reason = "status_" + status, DisplayText = "转移没发出去：" + status };
                    Error = LastTransfer.DisplayText;
                    Debug.Log("u5_transfer=reject status=" + status);
                }
                else
                {
                    LastTransfer = CharacterTransfer.Parse(text);
                    Error = LastTransfer.Ok ? string.Empty : LastTransfer.DisplayText;
                    Debug.Log("u5_transfer=" + (LastTransfer.Ok ? "ok" : "reject")
                        + " from=" + LastTransfer.FromLevel
                        + " to=" + LastTransfer.ToLevel
                        + " reason=" + LastTransfer.Reason);
                }

                HasTransfer = true;
                if (done != null) done();
            });
        }

        /// <summary>装上结晶。部位、类别、镶嵌位数量都看回包。</summary>
        public void InstallCrystal(uint slot, uint crystalSlot, uint socket, Action done = null)
        {
            CrystalRequest("/character/crystal/install",
                CharacterCrystal.InstallBody(_characterId, slot, crystalSlot, socket), done);
        }

        /// <summary>拆下结晶。可拆卸复用，拆下不改强化、金币与绑定。</summary>
        public void RemoveCrystal(uint slot, uint socket, Action done = null)
        {
            CrystalRequest("/character/crystal/remove",
                CharacterCrystal.RemoveBody(_characterId, slot, socket), done);
        }

        void CrystalRequest(string path, string body, Action done)
        {
            if (_characterId == 0)
            {
                LastCrystal = new CrystalOutcome { Reason = "no_character", DisplayText = "还没有角色" };
                HasCrystal = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(path, body, _token, (status, text) =>
            {
                Busy = false;
                if (status != 200)
                {
                    LastCrystal = new CrystalOutcome { Reason = "status_" + status, DisplayText = "结晶请求没发出去：" + status };
                    Error = LastCrystal.DisplayText;
                    Debug.Log("u5_crystal=reject status=" + status);
                }
                else
                {
                    LastCrystal = CharacterCrystal.Parse(text);
                    Error = LastCrystal.Ok ? string.Empty : LastCrystal.DisplayText;
                    Debug.Log("u5_crystal=" + (LastCrystal.Ok ? "ok" : "reject")
                        + " installed=" + (LastCrystal.Installed ? "true" : "false")
                        + " socket=" + LastCrystal.Socket
                        + " filled=" + LastCrystal.Filled
                        + " reason=" + LastCrystal.Reason);
                }

                HasCrystal = true;
                if (done != null) done();
            });
        }

        // ------------------------------------------------------------------ 境界段内阶

        /// <summary>
        /// 读段内阶与被动。进境界屏时调一次；阶名、被动万分比、门槛全看回包，
        /// 客户端不认阶名表、不按等级自己判「能不能升」。
        /// </summary>
        public void LoadRealmPhase(Action done = null)
        {
            RealmPhaseRequested = true;
            if (_characterId == 0)
            {
                LastRealmPhase = RealmPhaseOutcome.Failure("no_character");
                Error = "还没有角色";
                Debug.Log("u13_realm_phase=reject reason=no_character");
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterRealmPhase.ReadQuery(_characterId), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status != 200)
                {
                    LastRealmPhase = RealmPhaseOutcome.Failure("status_" + status);
                    Error = "段内阶没读到：" + status;
                    Debug.Log("u13_realm_phase=reject status=" + status);
                }
                else
                {
                    LastRealmPhase = CharacterRealmPhase.Parse(text);
                    Error = LastRealmPhase.Ok ? string.Empty : "段内阶回包读不出来";
                    Debug.Log("u13_realm_phase=" + (LastRealmPhase.Ok ? "ok" : "reject")
                        + " realm=" + LastRealmPhase.Realm
                        + " phase=" + LastRealmPhase.Phase
                        + " phase_name=" + LastRealmPhase.PhaseName
                        + " passive_pct_bps=" + LastRealmPhase.PassivePctBps
                        + " reason=" + LastRealmPhase.Reason);
                }

                if (done != null) done();
            });
        }

        /// <summary>
        /// 段内升一阶。请求体只有角色号（事务号由网关生成），门槛与被动全在角色服；
        /// 成了就把回包里的新阶名与新被动抄进屏上那份，不自己推下一阶。
        /// </summary>
        public void PromoteRealmPhase(Action done = null)
        {
            if (_characterId == 0)
            {
                LastPromote = RealmPhaseOutcome.Failure("no_character");
                HasPromote = true;
                Debug.Log("u13_promote=reject reason=no_character");
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterRealmPhase.PromotePath, CharacterRealmPhase.PromoteBody(_characterId), _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status != 200)
                    {
                        LastPromote = RealmPhaseOutcome.Failure("status_" + status);
                        Error = "升阶没发出去：" + status;
                        Debug.Log("u13_promote=reject status=" + status);
                    }
                    else
                    {
                        LastPromote = CharacterRealmPhase.Parse(text);
                        Error = LastPromote.Ok ? string.Empty : "被拒：" + LastPromote.Reason;
                        Debug.Log("u13_promote=" + (LastPromote.Ok ? "ok" : "reject")
                            + " from=" + LastPromote.FromPhase
                            + " to=" + LastPromote.ToPhase
                            + " phase_name=" + LastPromote.PhaseName
                            + " passive_pct_bps=" + LastPromote.PassivePctBps
                            + " level=" + LastPromote.Level
                            + " level_required=" + LastPromote.LevelRequired
                            + " reason=" + LastPromote.Reason);
                    }

                    if (LastPromote.Ok)
                    {
                        // 只抄回包：新阶序号、新阶名、新被动都是服务端给的。
                        var shown = LastRealmPhase;
                        shown.Ok = true;
                        shown.Reason = string.Empty;
                        shown.CharacterId = _characterId;
                        shown.Phase = LastPromote.ToPhase;
                        shown.PhaseName = LastPromote.PhaseName;
                        shown.PassivePctBps = LastPromote.PassivePctBps;
                        shown.DisplayText = LastPromote.PhaseName + " · 被动 " + LastPromote.PassivePctBps + " bps";
                        LastRealmPhase = shown;
                        RealmPhaseRequested = true;
                    }

                    HasPromote = true;
                    if (done != null) done();
                });
        }

        // ------------------------------------------------------------------ Q10 背包操作

        /// <summary>Q10：移动回包（格号、数量、合并 / 交换、拒绝码都在回包里）。</summary>
        public MoveOutcome LastMove { get; private set; }

        public bool HasMove { get; private set; }

        /// <summary>Q10：丢弃回包。被拒时这一格还剩几个照回包写。</summary>
        public DiscardOutcome LastDiscard { get; private set; }

        public bool HasDiscard { get; private set; }

        /// <summary>Q10：排序回包（换了几行、排完之后的整份背包）。</summary>
        public SortOutcome LastSort { get; private set; }

        public bool HasSort { get; private set; }

        /// <summary>Q10：批量使用回包（用掉几个、还剩几个、拒绝码）。</summary>
        public UseBatchOutcome LastUseBatch { get; private set; }

        public bool HasUseBatch { get; private set; }

        /// <summary>
        /// 背包屏选中的来源格、目标格与数量。存在流程里而不是屏里：
        /// 回包里来了新事实要重建屏，界面一重建就把局部变量丢了，选中的格子不能跟着丢。
        /// </summary>
        public uint BagFromSlot { get; set; }

        public uint BagToSlot { get; set; }

        public int BagCount { get; set; } = 1;

        /// <summary>Q10：移动一格。合并还是交换、能不能合，全看回包。</summary>
        public void MoveItem(uint fromSlot, uint toSlot, Action done = null)
        {
            BagPost("/inventory/move", CharacterBag.MoveBody(_characterId, fromSlot, toSlot),
                (status, text) =>
                {
                    if (status != 200)
                    {
                        LastMove = new MoveOutcome
                        {
                            Reason = "status_" + status,
                            DisplayText = "移动没发出去：" + status,
                        };
                        Error = LastMove.DisplayText;
                        Debug.Log("q10_move=reject status=" + status);
                    }
                    else
                    {
                        LastMove = CharacterBag.ParseMove(text);
                        Error = LastMove.Ok ? string.Empty : LastMove.DisplayText;
                        Debug.Log("q10_move=" + (LastMove.Ok ? "ok" : "reject")
                            + " source=" + LastMove.Source.Slot + ":" + LastMove.Source.ItemId
                            + " target=" + LastMove.Target.Slot + ":" + LastMove.Target.ItemId
                            + " merged=" + (LastMove.Merged ? "true" : "false")
                            + " swapped=" + (LastMove.Swapped ? "true" : "false")
                            + " reason=" + LastMove.Reason);
                    }

                    LastOpText = LastMove.DisplayText;
                    HasMove = true;
                }, done);
        }

        /// <summary>
        /// Q10：丢一件。调用它的只有确认弹窗那一下，所以 confirm 恒为 true——
        /// 服务端仍按这个字段判，界面点一次就发一次不算二次确认。
        /// </summary>
        public void DiscardItem(uint slot, int count, Action done = null)
        {
            BagPost("/inventory/discard", CharacterBag.DiscardBody(_characterId, slot, count, true),
                (status, text) =>
                {
                    if (status != 200)
                    {
                        LastDiscard = new DiscardOutcome
                        {
                            Reason = "status_" + status,
                            DisplayText = "丢弃没发出去：" + status,
                        };
                        Error = LastDiscard.DisplayText;
                        Debug.Log("q10_discard=reject status=" + status);
                    }
                    else
                    {
                        LastDiscard = CharacterBag.ParseDiscard(text);
                        Error = LastDiscard.Ok ? string.Empty : LastDiscard.DisplayText;
                        Debug.Log("q10_discard=" + (LastDiscard.Ok ? "ok" : "reject")
                            + " slot=" + slot + " count=" + count
                            + " item=" + LastDiscard.ItemId
                            + " left=" + LastDiscard.CountLeft
                            + " duplicate=" + (LastDiscard.Duplicate ? "true" : "false")
                            + " reason=" + LastDiscard.Reason);
                    }

                    LastOpText = LastDiscard.DisplayText;
                    HasDiscard = true;
                }, done);
        }

        /// <summary>Q10：排序。顺序、换了几行都由角色服算，屏上照回包重画。</summary>
        public void SortBag(Action done = null)
        {
            BagPost("/inventory/sort", CharacterBag.SortBody(_characterId),
                (status, text) =>
                {
                    if (status != 200)
                    {
                        LastSort = new SortOutcome
                        {
                            Reason = "status_" + status,
                            DisplayText = "排序没发出去：" + status,
                            Slots = new List<BagSlotView>(),
                        };
                        Error = LastSort.DisplayText;
                        Debug.Log("q10_sort=reject status=" + status);
                    }
                    else
                    {
                        LastSort = CharacterBag.ParseSort(text);
                        Error = LastSort.Ok ? string.Empty : LastSort.DisplayText;
                        Debug.Log("q10_sort=" + (LastSort.Ok ? "ok" : "reject")
                            + " moved=" + LastSort.Moved
                            + " rows=" + (LastSort.Slots != null ? LastSort.Slots.Count : 0)
                            + " reason=" + LastSort.Reason);

                        // 排序回包自带整份背包：直接抄进去，少发一次只读请求。
                        if (LastSort.Ok && LastSort.Slots != null)
                        {
                            var bag = Bag;
                            bag.Slots = LastSort.Slots;
                            Bag = bag;
                            HasBag = true;
                        }
                    }

                    LastOpText = LastSort.DisplayText;
                    HasSort = true;
                }, done);
        }

        /// <summary>
        /// Q10：一次用掉叠里的若干个。能连用几个由服务端判（今天只有「同时只生效一个」
        /// 那一类挂着，多张会被拒并回 effect_single_instance），界面照回包写。
        /// </summary>
        public void UseItemBatch(uint slot, int count, Action done = null)
        {
            BagPost("/inventory/use-batch", CharacterBag.UseBatchBody(_characterId, slot, count),
                (status, text) =>
                {
                    if (status != 200)
                    {
                        LastUseBatch = new UseBatchOutcome
                        {
                            Reason = "status_" + status,
                            DisplayText = "使用没发出去：" + status,
                        };
                        Error = LastUseBatch.DisplayText;
                        Debug.Log("q10_use=reject status=" + status);
                    }
                    else
                    {
                        LastUseBatch = CharacterBag.ParseUseBatch(text);
                        Error = LastUseBatch.Ok ? string.Empty : LastUseBatch.DisplayText;
                        Debug.Log("q10_use=" + (LastUseBatch.Ok ? "ok" : "reject")
                            + " slot=" + slot + " count=" + count
                            + " item=" + LastUseBatch.ItemId
                            + " used=" + LastUseBatch.Used
                            + " left=" + LastUseBatch.CountLeft
                            + " reason=" + LastUseBatch.Reason);
                    }

                    LastOpText = LastUseBatch.DisplayText;
                    HasUseBatch = true;
                }, done);
        }

        // ------------------------------------------------------------------ B9 穿戴

        /// <summary>B9：穿上回包（部位、装备攻击之和、等级与拒绝码都在回包里）。</summary>
        public EquipOutcome LastEquip { get; private set; }

        public bool HasEquip { get; private set; }

        /// <summary>B9：脱下回包（脱下不换格，bag_slot 是它原来那一格）。</summary>
        public UnequipOutcome LastUnequip { get; private set; }

        public bool HasUnequip { get; private set; }

        /// <summary>
        /// 最近一笔操作在屏上要写的那句话（穿戴与背包六种操作共用一条）。
        /// 界面只读这一格，不自己按 Has* 猜「哪一条更新」。
        /// </summary>
        public string LastOpText { get; private set; } = string.Empty;

        /// <summary>
        /// B9：把这一格的装备穿上。部位从回包读，界面不按物品编号猜；
        /// 同部位已经穿着一件时回 slot_occupied，屏上照回包写「先脱下」。
        /// </summary>
        public void EquipItem(uint slot, Action done = null)
        {
            BagPost("/character/equip", CharacterBag.EquipBody(_characterId, slot),
                (status, text) =>
                {
                    if (status != 200)
                    {
                        LastEquip = new EquipOutcome
                        {
                            Reason = "status_" + status,
                            DisplayText = "穿戴没发出去：" + status,
                        };
                        Error = LastEquip.DisplayText;
                        Debug.Log("b9_equip=reject status=" + status);
                    }
                    else
                    {
                        LastEquip = CharacterBag.ParseEquip(text);
                        Error = LastEquip.Ok ? string.Empty : LastEquip.DisplayText;
                        Debug.Log("b9_equip=" + (LastEquip.Ok ? "ok" : "reject")
                            + " slot=" + slot
                            + " item=" + LastEquip.ItemId
                            + " worn=" + LastEquip.EquipSlot
                            + " attack=" + LastEquip.EquipAttack
                            + " level=" + LastEquip.Level
                            + " reason=" + LastEquip.Reason);
                    }

                    LastOpText = LastEquip.DisplayText;
                    HasEquip = true;
                }, done);
        }

        /// <summary>B9：按部位脱下。格号由回包给，界面不自己记它原来在哪一格。</summary>
        public void UnequipItem(string equipSlot, Action done = null)
        {
            BagPost("/character/unequip", CharacterBag.UnequipBody(_characterId, equipSlot),
                (status, text) =>
                {
                    if (status != 200)
                    {
                        LastUnequip = new UnequipOutcome
                        {
                            Reason = "status_" + status,
                            DisplayText = "脱下没发出去：" + status,
                        };
                        Error = LastUnequip.DisplayText;
                        Debug.Log("b9_unequip=reject status=" + status);
                    }
                    else
                    {
                        LastUnequip = CharacterBag.ParseUnequip(text);
                        Error = LastUnequip.Ok ? string.Empty : LastUnequip.DisplayText;
                        Debug.Log("b9_unequip=" + (LastUnequip.Ok ? "ok" : "reject")
                            + " worn=" + LastUnequip.EquipSlot
                            + " bag_slot=" + LastUnequip.BagSlot
                            + " attack=" + LastUnequip.EquipAttack
                            + " reason=" + LastUnequip.Reason);
                    }

                    LastOpText = LastUnequip.DisplayText;
                    HasUnequip = true;
                }, done);
        }

        /// <summary>四条背包操作的共同发送：忙标记、请求计数、拆回包，一条不省。</summary>
        void BagPost(string path, string body, Action<int, string> parse, Action done)
        {
            if (_characterId == 0)
            {
                Error = "还没有角色";
                Debug.Log("q10_bag=reject reason=no_character path=" + path);
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(path, body, _token, (status, text) =>
            {
                Busy = false;
                parse(status, text);
                if (done != null) done();
            });
        }

        // ------------------------------------------------------------------ 真机发送

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
                if (request.result != UnityWebRequest.Result.Success && status == 0)
                {
                    status = 0;
                }

                done(status, text);
                request.Dispose();
            };
        }
    }
}
