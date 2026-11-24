using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 境界（原型 D07）。大境界只读 `/inventory` 回包的 `realm_display`
    /// （立仙第十段显示金仙、立魔显示散仙）；段内阶与被动读 N13 的 `GET /realm/phase`；
    /// 升一阶只 POST `/realm/phase/promote`，请求体只有角色号，门槛与被动都在角色服。
    /// 大境界突破（历练 / 炼心 / 灵石）仍由后面的卡承接，这一屏不给那个按钮，也不本地推境界。
    /// </summary>
    public static class UiRealmScreen
    {
        public const string ReadyLog = "u5_realm_screen=ready";

        public static void Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell, UiUpgradeCodex.RealmNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.44f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;

            var realm = flow.HasBag ? flow.Bag.DisplayRealm : string.Empty;
            var phase = flow.LastRealmPhase;
            var promoted = flow.LastPromote;

            // 左：大境界与段内阶都只抄回包，没请求就写「尚未请求」。
            var current = shell.Panel(UiStrings.UiRealmScreen.T001, m.Band(pad, top + m.Px(56f), leftW, m.Px(620f)), shell.Size(34f));
            UiUpgradeViews.Rows(shell, current,
                new[] { UiStrings.UiRealmScreen.T002, UiStrings.UiRealmScreen.T003, UiStrings.UiRealmScreen.T004, UiStrings.UiRealmScreen.T005 },
                new[]
                {
                    realm.Length > 0 ? realm : UiStrings.UiRealmScreen.T006,
                    CharacterRealmPhase.PhaseLine(flow.RealmPhaseRequested, phase),
                    flow.RealmPhaseRequested && phase.Ok ? phase.PassivePctBps + " bps" : "—",
                    UiStrings.UiRealmScreen.T007,
                },
                m.Px(92f), m.Px(32f));

            // 右：段内阶这一手。阶序号与门槛都照回包写。
            var actions = shell.Panel(UiStrings.UiRealmScreen.T003, m.Band(rightX, top + m.Px(56f), rightW, m.Px(520f)),
                shell.Size(34f));
            UiUpgradeViews.Rows(shell, actions,
                new[] { UiStrings.UiRealmScreen.T008, UiStrings.UiRealmScreen.T009, UiStrings.UiRealmScreen.T010 },
                new[]
                {
                    phase.Ok ? phase.Realm : UiStrings.UiRealmScreen.T011,
                    phase.Ok ? phase.Phase.ToString() : "—",
                    CharacterRealmPhase.RequiredLine(flow.HasPromote, promoted),
                },
                m.Px(84f), m.Px(32f));

            shell.ButtonIn(actions.Root,
                new Rect(m.Px(28f), m.Px(24f), m.Px(320f), m.Px(76f)), UiStrings.UiRealmScreen.T012,
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.LoadRealmPhase(shell.RefreshPage);
                }, UiButtonKind.Normal);

            shell.ButtonIn(actions.Root,
                new Rect(rightW - m.Px(28f) - m.Px(320f), m.Px(24f), m.Px(320f), m.Px(76f)), UiStrings.UiRealmScreen.T013,
                () =>
                {
                    if (flow.Busy)
                    {
                        return;
                    }

                    flow.PromoteRealmPhase(shell.RefreshPage);
                }, UiButtonKind.Primary);

            // 右下：升阶回包。被拒时门槛照回包给，按键本身不做本地判断。
            var result = shell.Panel(UiStrings.UiRealmScreen.T014, m.Band(rightX, top + m.Px(56f) + m.Px(600f), rightW,
                m.Px(440f)), shell.Size(34f));
            UiUpgradeViews.Result(shell, result, m.Px(300f), UiStrings.UiRealmScreen.T015,
                CharacterRealmPhase.PromoteLine(flow.HasPromote, promoted));
            UiUpgradeViews.Result(shell, result, m.Px(244f), UiStrings.UiRealmScreen.T004,
                flow.HasPromote && promoted.Ok ? promoted.PassivePctBps + " bps" : "—");
            UiUpgradeViews.Result(shell, result, m.Px(188f), UiStrings.UiRealmScreen.T016,
                flow.HasPromote ? (promoted.Duplicate ? UiStrings.UiRealmScreen.T017 : UiStrings.UiRealmScreen.T018) : "—");

            Debug.Log(ReadyLog + " realm=" + realm
                + " phase_requested=" + (flow.RealmPhaseRequested ? "true" : "false")
                + " phase=" + phase.Phase
                + " phase_name=" + phase.PhaseName
                + " passive_pct_bps=" + phase.PassivePctBps
                + " promote_ok=" + (promoted.Ok ? "true" : "false")
                + " level_required=" + promoted.LevelRequired);
        }
    }
}
