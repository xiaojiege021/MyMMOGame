using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 制作（U20 / B3 的界面欠项）。左边是角色服给的配方目录（副职、解锁等级、
    /// 材料、费用、成功率、耗时都照回包）；右边开始 / 收取 / 读状态。
    ///
    /// 收取回包里带回这一手的掷点（主属性 + 最多两条附加属性），屏上照抄；
    /// 不读配方表与掷点表，不在本地掷点、不把成功率当成本次结果。
    /// </summary>
    public static class UiCraftScreen
    {
        public const string ReadyLog = "u5_craft_screen=ready";

        public static void Build(UiShell shell, UiUpgradeFlow flow)
        {
            UiUpgradeViews.Note(shell, UiUpgradeCodex.CraftNote);

            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(160f);
            var gap = m.Px(48f);
            var leftW = (m.Width - pad * 2f - gap) * 0.5f;
            var rightX = pad + leftW + gap;
            var rightW = m.Width - pad - rightX;

            var recipes = flow.LastRecipes;
            var rows = flow.HasRecipes && recipes.Recipes != null ? recipes.Recipes : null;

            // 左：配方目录。选中的配方在刷新后仍保留（存在流程里，不另存一份状态）。
            var listPanel = shell.Panel(UiStrings.UiCraftScreen.T003,
                m.Band(pad, top + m.Px(56f), leftW, m.Px(720f)), shell.Size(30f));
            if (rows == null || rows.Count == 0)
            {
                shell.LabelIn(listPanel.Root, new Rect(m.Px(28f), m.Px(64f), leftW - m.Px(56f), m.Px(96f)),
                    flow.HasRecipes ? UiStrings.UiCraftScreen.T019 : UiStrings.UiCraftScreen.T011,
                    UiTextRole.Faint, shell.Size(24f), TextAnchor.MiddleLeft, false);
            }
            else
            {
                if (flow.CraftRecipeId.Length == 0)
                {
                    flow.CraftRecipeId = rows[0].Id;
                }

                var show = rows.Count > 7 ? 7 : rows.Count;
                for (var i = 0; i < show; i++)
                {
                    var row = rows[i];
                    var opt = new UiOption(
                        listPanel.Root,
                        new Rect(m.Px(24f), m.Px(58f) + i * m.Px(86f), leftW - m.Px(48f), m.Px(78f)),
                        row.Id, row.Profession,
                        row.Unlocked ? row.SuccessPct + "%" : "^" + row.UnlockLevel,
                        UiOptionKind.Card, shell.Size(22f), shell.Size(18f));
                    opt.Selected = row.Id == flow.CraftRecipeId;
                    var id = row.Id;
                    opt.OnPick = picked =>
                    {
                        flow.CraftRecipeId = id;
                        shell.RefreshPage();
                    };
                    shell.Register(opt);
                }
            }

            shell.ButtonIn(listPanel.Root, new Rect(m.Px(24f), m.Px(640f), m.Px(240f), m.Px(64f)),
                UiStrings.UiCraftScreen.T018,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.LoadRecipes(shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);

            // 右一：副职与选中的配方。材料 / 费用 / 成功率都是回包里的串，不重算。
            var detail = shell.Panel(UiStrings.UiCraftScreen.T001,
                m.Band(rightX, top + m.Px(56f), rightW, m.Px(380f)), shell.Size(28f));
            UiUpgradeViews.Result(shell, detail, m.Px(300f), UiStrings.UiCraftScreen.T002,
                flow.HasRecipes ? flow.LastRecipes.ProfessionsText + " · " + rows.Count : "—");
            var picked = Pick(rows, flow.CraftRecipeId);
            UiUpgradeViews.Result(shell, detail, m.Px(244f), UiStrings.UiCraftScreen.T004,
                picked.Id.Length > 0 ? picked.Id : UiStrings.UiCraftScreen.T011);
            UiUpgradeViews.Result(shell, detail, m.Px(188f), UiStrings.UiCraftScreen.T005,
                picked.InputsText.Length > 0 ? picked.InputsText + " · " + picked.FeeText : "—");
            UiUpgradeViews.Result(shell, detail, m.Px(132f), UiStrings.UiCraftScreen.T006,
                picked.Id.Length > 0 ? picked.SuccessPct + "% / " + picked.CraftTimeS + "s" : "—");

            // 右二：三件事 + 回包 + 掷点。
            var opsTop = top + m.Px(56f) + m.Px(400f);
            var ops = shell.Panel(UiStrings.UiCraftScreen.T010,
                m.Band(rightX, opsTop, rightW, m.Px(700f)), shell.Size(28f));
            shell.ButtonIn(ops.Root, new Rect(m.Px(28f), m.Px(64f), m.Px(220f), m.Px(72f)),
                UiStrings.UiCraftScreen.T007,
                () =>
                {
                    if (!flow.Busy && flow.CraftRecipeId.Length > 0)
                    {
                        flow.StartCraft(flow.CraftRecipeId, shell.RefreshPage);
                    }
                }, UiButtonKind.Primary);
            shell.ButtonIn(ops.Root, new Rect(m.Px(268f), m.Px(64f), m.Px(200f), m.Px(72f)),
                UiStrings.UiCraftScreen.T008,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.FinishCraft(shell.RefreshPage);
                    }
                }, UiButtonKind.Primary);
            shell.ButtonIn(ops.Root, new Rect(m.Px(488f), m.Px(64f), m.Px(200f), m.Px(72f)),
                UiStrings.UiCraftScreen.T009,
                () =>
                {
                    if (!flow.Busy)
                    {
                        flow.LoadCraftState(shell.RefreshPage);
                    }
                }, UiButtonKind.Normal);

            var state = flow.LastCraftState;
            UiUpgradeViews.Result(shell, ops, m.Px(570f), UiStrings.UiCraftScreen.T013,
                flow.HasCraftState
                    ? (state.Active
                        ? state.RecipeId + " / " + state.RemainingS
                        : UiStrings.UiCraftScreen.T012)
                    : UiStrings.UiCraftScreen.T011);

            var start = flow.LastCraftStart;
            UiUpgradeViews.Result(shell, ops, m.Px(510f), UiStrings.UiCraftScreen.T007,
                flow.HasCraftStart
                    ? (start.Ok ? start.DisplayText : UiStrings.UiCraftScreen.T017 + start.Reason)
                    : UiStrings.UiCraftScreen.T011);

            var finish = flow.LastCraftFinish;
            UiUpgradeViews.Result(shell, ops, m.Px(450f), UiStrings.UiCraftScreen.T008,
                flow.HasCraftFinish
                    ? (finish.Ok ? finish.DisplayText : UiStrings.UiCraftScreen.T017 + finish.Reason)
                    : UiStrings.UiCraftScreen.T011);
            UiUpgradeViews.Result(shell, ops, m.Px(390f), UiStrings.UiCraftScreen.T016,
                flow.HasCraftFinish && finish.Ok
                    ? finish.OutputItemId + " × " + finish.OutputCount
                    : "—");
            UiUpgradeViews.Result(shell, ops, m.Px(330f), UiStrings.UiCraftScreen.T014,
                flow.HasCraftFinish && finish.Ok && finish.CraftAttr > 0
                    ? finish.CraftAttr + " / " + finish.CraftExtras.Count
                    : "—");

            Debug.Log(ReadyLog + " rows=" + (rows != null ? rows.Count : 0)
                + " picked=" + flow.CraftRecipeId
                + " finish=" + (flow.HasCraftFinish ? (finish.Ok ? "ok" : "reject") : "none")
                + " craft_attr=" + (flow.HasCraftFinish ? finish.CraftAttr : 0)
                + " extras=" + (flow.HasCraftFinish ? finish.CraftExtras.Count : 0));
        }

        /// <summary>
        /// 按编号找选中的那条配方；找不到回一条「空配方」（字符串都是空串而不是 null）。
        /// 屏上没请求过目录时也走这一条，所以这里不返回 default(T) —— 那个的字符串是 null。
        /// </summary>
        static Mmo.Net.CraftRecipeView Pick(System.Collections.Generic.List<Mmo.Net.CraftRecipeView> rows, string id)
        {
            if (rows == null || string.IsNullOrEmpty(id))
            {
                return Blank();
            }

            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Id == id)
                {
                    return rows[i];
                }
            }

            return Blank();
        }

        static Mmo.Net.CraftRecipeView Blank()
        {
            return new Mmo.Net.CraftRecipeView
            {
                Id = string.Empty,
                Profession = string.Empty,
                OutputItemId = string.Empty,
                Note = string.Empty,
                InputsText = string.Empty,
                FeeText = string.Empty,
            };
        }
    }
}
