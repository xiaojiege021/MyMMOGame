using System;
using Mmo.Net;
using UnityEngine;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 觉醒 / 坐骑 / 灵兽（U21，B6 与 B4 的界面欠项）。
    ///
    /// 六条口：觉醒 `GET /character/awaken` 与 `POST /character/awaken`、
    /// 坐骑 `GET /character/mounts` 与 `POST /character/mount` / `POST /character/dismount`、
    /// 灵兽 `GET /character/spirit-beasts`。
    ///
    /// 请求只带角色号、分支号与背包格号：等级够不够、分支对不对、能不能骑、
    /// 档位与速度、收集度与生活协助全在角色服判；这里只把回包摆上屏。
    /// 写成功之后**再读一次只读口**，不自己在客户端把状态改成已觉醒 / 已骑上。
    /// </summary>
    public sealed partial class UiUpgradeFlow : MonoBehaviour
    {
        // ------------------------------------------------------------------ 觉醒（U21 / B6）

        /// <summary>最近一次 `GET /character/awaken` 的回包。没觉醒时 `skill_id` 是 0，不是错误。</summary>
        public AwakenState LastAwaken { get; private set; } = AwakenState.Failure("not_loaded");

        public bool HasAwaken { get; private set; }

        /// <summary>觉醒状态这条口发过请求没有。发过但没拿到 200 时，屏上写回执而不是「尚未请求」。</summary>
        public bool AwakenRequested { get; private set; }

        /// <summary>最近一次觉醒这一笔的回包。被拒时原因原样带出。</summary>
        public AwakenOutcome LastAwakenAct { get; private set; } = AwakenOutcome.Failure("not_loaded");

        public bool HasAwakenAct { get; private set; }

        /// <summary>读一次觉醒状态。进觉醒屏时调；写成功之后也靠它重画。</summary>
        public void LoadAwaken(Action done = null)
        {
            if (_characterId == 0)
            {
                LastAwaken = AwakenState.Failure("no_character");
                HasAwaken = false;
                AwakenRequested = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            AwakenRequested = true;
            _send(Companion.Query(Companion.AwakenPath, _characterId), string.Empty, _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastAwaken = Companion.ParseState(text);
                        HasAwaken = LastAwaken.Ok;
                        Error = LastAwaken.Ok ? string.Empty : LastAwaken.Reason;
                        Debug.Log("u21_awaken_state=" + (LastAwaken.Ok ? "ok" : "reject")
                            + " branch=" + LastAwaken.Branch
                            + " skill=" + LastAwaken.SkillId
                            + " level=" + LastAwaken.Level
                            + " at_ms=" + LastAwaken.AwakenAtMs
                            + " reason=" + LastAwaken.Reason);
                    }
                    else
                    {
                        LastAwaken = AwakenState.Failure("http_" + status);
                        HasAwaken = false;
                        Error = UiStrings.CompanionFlow.T001 + status;
                        Debug.Log("u21_awaken_state=reject status=" + status);
                    }

                    if (done != null) done();
                });
        }

        /// <summary>
        /// 觉醒。只发角色号与分支号（分支取只读回包里服务端写下的那条）；
        /// 等级、技能编号与「是不是已经觉醒过」全在角色服判。
        /// </summary>
        public void Awaken(string branchId, Action done = null)
        {
            if (_characterId == 0)
            {
                LastAwakenAct = AwakenOutcome.Failure("no_character");
                HasAwakenAct = true;
                Debug.Log("u21_awaken=reject reason=no_character");
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(Companion.AwakenPath, Companion.AwakenBody(_characterId, branchId), _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastAwakenAct = Companion.ParseAwaken(text);
                        Error = LastAwakenAct.Ok ? string.Empty : LastAwakenAct.Reason;
                        Debug.Log("u21_awaken=" + (LastAwakenAct.Ok ? "ok" : "reject")
                            + " branch=" + LastAwakenAct.Branch
                            + " skill=" + LastAwakenAct.SkillId
                            + " level=" + LastAwakenAct.Level
                            + " duplicate=" + (LastAwakenAct.Duplicate ? "true" : "false")
                            + " reason=" + LastAwakenAct.Reason);
                    }
                    else
                    {
                        LastAwakenAct = AwakenOutcome.Failure("http_" + status);
                        Error = UiStrings.CompanionFlow.T002 + status;
                        Debug.Log("u21_awaken=reject status=" + status);
                    }

                    HasAwakenAct = true;

                    // 觉醒是服务端写定的状态：做完再读一次只读口，不自己把屏标成已觉醒。
                    if (LastAwakenAct.Ok)
                    {
                        LoadAwaken(done);
                        return;
                    }

                    if (done != null) done();
                });
        }

        // ------------------------------------------------------------------ 坐骑（U21 / B4）

        /// <summary>最近一次 `GET /character/mounts` 的回包。没骑时装备两项是空串与 0。</summary>
        public MountBoardOutcome LastMounts { get; private set; } = MountBoardOutcome.Failure("not_loaded");

        public bool HasMounts { get; private set; }

        public bool MountsRequested { get; private set; }

        /// <summary>最近一次骑上 / 下坐骑的回包（两条口同一个形状）。</summary>
        public MountActionOutcome LastMountAct { get; private set; } = MountActionOutcome.Failure("not_loaded");

        public bool HasMountAct { get; private set; }

        /// <summary>读一次坐骑栏。进屏时调；骑上 / 下坐骑之后也靠它重画。</summary>
        public void LoadMounts(Action done = null)
        {
            if (_characterId == 0)
            {
                LastMounts = MountBoardOutcome.Failure("no_character");
                HasMounts = false;
                MountsRequested = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            MountsRequested = true;
            _send(Companion.Query(Companion.MountsPath, _characterId), string.Empty, _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastMounts = Companion.ParseMounts(text);
                        HasMounts = LastMounts.Ok;
                        Error = LastMounts.Ok ? string.Empty : LastMounts.Reason;
                        Debug.Log("u21_mounts=" + (LastMounts.Ok ? "ok" : "reject")
                            + " equipped=" + LastMounts.EquippedItemId
                            + " speed_bps=" + LastMounts.EquippedSpeedBps
                            + " owned=" + LastMounts.Owned.Count
                            + " reason=" + LastMounts.Reason);
                    }
                    else
                    {
                        LastMounts = MountBoardOutcome.Failure("http_" + status);
                        HasMounts = false;
                        Error = UiStrings.CompanionFlow.T003 + status;
                        Debug.Log("u21_mounts=reject status=" + status);
                    }

                    if (done != null) done();
                });
        }

        /// <summary>骑上某一格背包里的坐骑。档位与速度在角色服，界面带不进数值。</summary>
        public void MountUp(int slot, Action done = null)
        {
            MountAct(Companion.MountUpPath, Companion.MountUpBody(_characterId, slot), "mount", done);
        }

        /// <summary>下坐骑。只发角色号。</summary>
        public void Dismount(Action done = null)
        {
            MountAct(Companion.DismountPath, Companion.DismountBody(_characterId), "dismount", done);
        }

        void MountAct(string path, string body, string tag, Action done)
        {
            if (_characterId == 0)
            {
                LastMountAct = MountActionOutcome.Failure("no_character");
                HasMountAct = true;
                Debug.Log("u21_" + tag + "=reject reason=no_character");
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(path, body, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastMountAct = Companion.ParseMountAction(text);
                    Error = LastMountAct.Ok ? string.Empty : LastMountAct.Reason;
                    Debug.Log("u21_" + tag + "=" + (LastMountAct.Ok ? "ok" : "reject")
                        + " item=" + LastMountAct.ItemId
                        + " tier=" + LastMountAct.Tier
                        + " speed_bps=" + LastMountAct.SpeedBps
                        + " bag_slot=" + LastMountAct.BagSlot
                        + " reason=" + LastMountAct.Reason);
                }
                else
                {
                    LastMountAct = MountActionOutcome.Failure("http_" + status);
                    Error = UiStrings.CompanionFlow.T004 + status;
                    Debug.Log("u21_" + tag + "=reject status=" + status);
                }

                HasMountAct = true;

                // 骑没骑上由服务端写定：成功后再读一次坐骑栏，不本地翻 mounted。
                if (LastMountAct.Ok)
                {
                    LoadMounts(done);
                    return;
                }

                if (done != null) done();
            });
        }

        // ------------------------------------------------------------------ 灵兽图鉴（U21 / B4）

        /// <summary>最近一次 `GET /character/spirit-beasts` 的回包。只读，没有写口。</summary>
        public SpiritBeastOutcome LastBeasts { get; private set; } = SpiritBeastOutcome.Failure("not_loaded");

        public bool HasBeasts { get; private set; }

        public bool BeastsRequested { get; private set; }

        /// <summary>读一次灵兽图鉴。收集度与生活协助合计都是角色服算好的。</summary>
        public void LoadSpiritBeasts(Action done = null)
        {
            if (_characterId == 0)
            {
                LastBeasts = SpiritBeastOutcome.Failure("no_character");
                HasBeasts = false;
                BeastsRequested = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            BeastsRequested = true;
            _send(Companion.Query(Companion.SpiritPath, _characterId), string.Empty, _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastBeasts = Companion.ParseSpiritBeasts(text);
                        HasBeasts = LastBeasts.Ok;
                        Error = LastBeasts.Ok ? string.Empty : LastBeasts.Reason;
                        Debug.Log("u21_beasts=" + (LastBeasts.Ok ? "ok" : "reject")
                            + " collected=" + LastBeasts.CollectedCount
                            + " entries=" + LastBeasts.EntryCount
                            + " rows=" + LastBeasts.Entries.Count
                            + " bps=" + LastBeasts.CollectionPctBps
                            + " life_bps=" + LastBeasts.TotalLifeAssistPctBps
                            + " passive=" + LastBeasts.PassiveKind
                            + " reason=" + LastBeasts.Reason);
                    }
                    else
                    {
                        LastBeasts = SpiritBeastOutcome.Failure("http_" + status);
                        HasBeasts = false;
                        Error = UiStrings.CompanionFlow.T005 + status;
                        Debug.Log("u21_beasts=reject status=" + status);
                    }

                    if (done != null) done();
                });
        }
    }
}
