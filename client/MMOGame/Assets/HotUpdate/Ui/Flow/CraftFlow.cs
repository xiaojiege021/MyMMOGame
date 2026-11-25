using System;
using Mmo.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 制作（U20 / B3 的界面欠项）。四条口：配方目录 `GET /craft/recipes`、
    /// 当前状态 `GET /craft/state`、开始 `POST /craft/start`、收取 `POST /craft/finish`。
    ///
    /// 材料够不够、副职等级够不够、这一手成不成、掷到多少，全在角色服判；
    /// 这里只发角色号与配方编号（事务号网关生成），不读配方表与掷点表，
    /// 也不在本地掷点或把成功率当成这一次的预告。
    /// </summary>
    public sealed partial class UiUpgradeFlow : MonoBehaviour
    {
        // ------------------------------------------------------------------ 制作（U20 / B3）

        public CraftRecipesOutcome LastRecipes { get; private set; } = CraftRecipesOutcome.Failure("not_loaded");

        public bool HasRecipes { get; private set; }

        public bool RecipesTried { get; private set; }

        public CraftStateOutcome LastCraftState { get; private set; }

        public bool HasCraftState { get; private set; }

        public CraftStartOutcome LastCraftStart { get; private set; }

        public bool HasCraftStart { get; private set; }

        /// <summary>最近一次收取的回包。掷点（主属性 + 附加属性）就在这一份里。</summary>
        public CraftFinishOutcome LastCraftFinish { get; private set; }

        public bool HasCraftFinish { get; private set; }

        /// <summary>选中的配方编号。刷新屏不丢；能不能开始仍看回包。</summary>
        public string CraftRecipeId { get; set; } = string.Empty;

        /// <summary>读一次配方目录。进制作屏时调；副职、解锁等级、材料与费用都按回包重画。</summary>
        public void LoadRecipes(Action done = null)
        {
            if (_characterId == 0)
            {
                LastRecipes = CraftRecipesOutcome.Failure("no_character");
                HasRecipes = false;
                RecipesTried = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            RecipesTried = true;
            _send(CharacterCraft.RecipesQuery(_characterId), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastRecipes = CharacterCraft.ParseRecipes(text);
                    HasRecipes = LastRecipes.Ok;
                    Error = LastRecipes.Ok ? string.Empty : LastRecipes.Reason;
                    Debug.Log("u20_recipes=" + (LastRecipes.Ok ? "ok" : "reject")
                        + " table=" + LastRecipes.TableVersion
                        + " level=" + LastRecipes.Level
                        + " rows=" + LastRecipes.Recipes.Count
                        + " pending=" + (LastRecipes.Pending ? "true" : "false")
                        + " reason=" + LastRecipes.Reason);
                }
                else
                {
                    LastRecipes = CraftRecipesOutcome.Failure("http_" + status);
                    HasRecipes = false;
                    Error = UiStrings.CraftFlow.T001 + status;
                    Debug.Log("u20_recipes=reject status=" + status);
                }

                if (done != null) done();
            });
        }

        /// <summary>读一次当前有没有活。只读；不能靠它改结果或自己数到能收。</summary>
        public void LoadCraftState(Action done = null)
        {
            if (_characterId == 0)
            {
                HasCraftState = false;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterCraft.StateQuery(_characterId), string.Empty, _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastCraftState = CharacterCraft.ParseState(text);
                    HasCraftState = true;
                    Debug.Log("u20_craft_state=ok active=" + (LastCraftState.Active ? "true" : "false")
                        + " recipe=" + LastCraftState.RecipeId
                        + " remaining_s=" + LastCraftState.RemainingS);
                }
                else
                {
                    HasCraftState = false;
                    Error = UiStrings.CraftFlow.T002 + status;
                    Debug.Log("u20_craft_state=reject status=" + status);
                }

                if (done != null) done();
            });
        }

        /// <summary>开始制作。只发配方编号；材料与费用在角色服那一笔事务里扣。</summary>
        public void StartCraft(string recipeId, Action done = null)
        {
            if (_characterId == 0)
            {
                LastCraftStart = new CraftStartOutcome { Ok = false, Reason = "no_character", DisplayText = UiStrings.CraftFlow.T005 };
                HasCraftStart = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterCraft.StartPath, CharacterCraft.StartBody(_characterId, recipeId), _token,
                (status, text) =>
                {
                    Busy = false;
                    if (status == 200)
                    {
                        LastCraftStart = CharacterCraft.ParseStart(text);
                        Error = LastCraftStart.Ok ? string.Empty : LastCraftStart.DisplayText;
                        Debug.Log("u20_craft_start=" + (LastCraftStart.Ok ? "ok" : "reject")
                            + " recipe=" + LastCraftStart.RecipeId
                            + " duplicate=" + (LastCraftStart.Duplicate ? "true" : "false")
                            + " remaining_s=" + LastCraftStart.RemainingS
                            + " reason=" + LastCraftStart.Reason);
                    }
                    else
                    {
                        LastCraftStart = new CraftStartOutcome
                        {
                            Ok = false, Reason = "http_" + status, DisplayText = UiStrings.CraftFlow.T003 + status,
                        };
                        Error = UiStrings.CraftFlow.T003 + status;
                        Debug.Log("u20_craft_start=reject status=" + status);
                    }

                    HasCraftStart = true;
                    if (done != null) done();
                });
        }

        /// <summary>
        /// 收取。倒计时没到会被拒（`craft_not_ready`）；真做失败是 ok=true + success=false，
        /// 材料照扣、产出为 0。两种都不在客户端区分成别的说法，照回包写。
        /// </summary>
        public void FinishCraft(Action done = null)
        {
            if (_characterId == 0)
            {
                LastCraftFinish = new CraftFinishOutcome
                {
                    Ok = false, Reason = "no_character", DisplayText = UiStrings.CraftFlow.T005,
                    CraftExtras = new System.Collections.Generic.List<CraftExtraView>(),
                };
                HasCraftFinish = true;
                if (done != null) done();
                return;
            }

            Busy = true;
            Requests++;
            _send(CharacterCraft.FinishPath, CharacterCraft.FinishBody(_characterId), _token, (status, text) =>
            {
                Busy = false;
                if (status == 200)
                {
                    LastCraftFinish = CharacterCraft.ParseFinish(text);
                    Error = LastCraftFinish.Ok ? string.Empty : LastCraftFinish.DisplayText;
                    Debug.Log("u20_craft_finish=" + (LastCraftFinish.Ok ? "ok" : "reject")
                        + " success=" + (LastCraftFinish.Success ? "true" : "false")
                        + " output=" + LastCraftFinish.OutputItemId + "x" + LastCraftFinish.OutputCount
                        + " craft_attr=" + LastCraftFinish.CraftAttr
                        + " extras=" + LastCraftFinish.CraftExtras.Count
                        + " reason=" + LastCraftFinish.Reason);
                }
                else
                {
                    LastCraftFinish = new CraftFinishOutcome
                    {
                        Ok = false, Reason = "http_" + status, DisplayText = UiStrings.CraftFlow.T004 + status,
                        CraftExtras = new System.Collections.Generic.List<CraftExtraView>(),
                    };
                    Error = UiStrings.CraftFlow.T004 + status;
                    Debug.Log("u20_craft_finish=reject status=" + status);
                }

                HasCraftFinish = true;

                // 收取会把产出写进背包：读一次背包，掷点跟着那一行一起回来，
                // 不在本地把这件「记」进背包装备里。
                if (LastCraftFinish.Ok)
                {
                    LoadBag(done);
                    return;
                }

                if (done != null) done();
            });
        }
    }
}
