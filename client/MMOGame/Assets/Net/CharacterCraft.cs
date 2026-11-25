using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mmo.Net
{
    /// <summary>
    /// 一条配方（K3 配方目录）。材料、费用、成功率、耗时、解锁等级都是角色服从
    /// 配方表给的事实值；客户端只照着摆，不判够不够、
    /// 不把成功率当成本次结果的预告。
    /// </summary>
    public struct CraftRecipeView
    {
        public string Id;
        public string Profession;
        public int UnlockLevel;
        public bool Unlocked;
        public int SuccessPct;
        public int CraftTimeS;
        public string OutputItemId;
        public int OutputCount;
        public string Note;

        /// <summary>材料与费用照回包拼成一段（只拼串，不做任何算术）。</summary>
        public string InputsText;
        public string FeeText;
    }

    public struct CraftRecipesOutcome
    {
        public bool Ok;
        public string Reason;
        public int TableVersion;
        public bool Pending;
        public int Level;
        public string ProfessionsText;
        public List<CraftRecipeView> Recipes;

        public static CraftRecipesOutcome Failure(string reason) => new CraftRecipesOutcome
        {
            Ok = false,
            Reason = reason,
            Recipes = new List<CraftRecipeView>(),
            DisplayText = "回包读不出来：" + reason,
        };

        public string DisplayText;
    }

    /// <summary>当前有没有活在手上（K3 `GET /craft/state`）。只读，不能靠它改结果。</summary>
    public struct CraftStateOutcome
    {
        public bool Ok;
        public string Reason;
        public bool Active;
        public string RecipeId;
        public string Profession;
        public int CraftTimeS;
        public long StartedAtUnix;
        public long ReadyAtUnix;
        public int RemainingS;

        public string DisplayText;
    }

    /// <summary>开始制作（K3 `POST /craft/start`）。材料与费用在角色服那一笔事务里扣。</summary>
    public struct CraftStartOutcome
    {
        public bool Ok;
        public string Reason;
        public bool Duplicate;
        public string RecipeId;
        public string Profession;
        public int CraftTimeS;
        public long StartedAtUnix;
        public long ReadyAtUnix;
        public int RemainingS;

        public string DisplayText;
    }

    /// <summary>
    /// 收取（K3 `POST /craft/finish`）。B3 起这一次还带回掷点：
    /// 主属性 `craft_attr` 与最多两条附加属性 `craft_extras`，写在产出那一件上。
    /// 没成也是 ok=true + success=false（材料照扣），与「被拒绝一个字节没动」分开。
    /// </summary>
    public struct CraftFinishOutcome
    {
        public bool Ok;
        public string Reason;
        public bool Duplicate;
        public string RecipeId;
        public string Profession;
        public bool Success;
        public string OutputItemId;
        public int OutputCount;
        public int SuccessPct;
        public int CraftTimeS;
        public long ReadyAtUnix;
        public int CraftAttr;
        public List<CraftExtraView> CraftExtras;

        public string DisplayText;
    }

    /// <summary>
    /// K3 制作的四条 HTTP 口（配方目录 / 当前状态 / 开始 / 收取）。
    ///
    /// 只发角色号与配方编号：材料够不够、副职等级够不够、这一手成不成、掷到多少，
    /// 全在角色服；事务号由网关生成，请求体里不带。客户端不读 `recipes.json`、
    /// 不读掷点表，也不在本地掷点或把成功率先算成本次结果。
    /// </summary>
    public static class CharacterCraft
    {
        public const string RecipesPath = "/craft/recipes";

        public static string RecipesQuery(ulong characterId) =>
            RecipesPath + "?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        public const string StatePath = "/craft/state";

        public static string StateQuery(ulong characterId) =>
            StatePath + "?character_id=" + characterId.ToString(CultureInfo.InvariantCulture);

        public const string StartPath = "/craft/start";

        /// <summary>开始制作：只有角色号与配方编号，事务号网关生成。</summary>
        public static string StartBody(ulong characterId, string recipeId)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture)
                + ",\"recipe_id\":\"" + Escape(recipeId) + "\"}";
        }

        public const string FinishPath = "/craft/finish";

        /// <summary>收取：只有角色号。成没成、掷到多少都看回包。</summary>
        public static string FinishBody(ulong characterId)
        {
            return "{\"character_id\":" + characterId.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static CraftRecipesOutcome ParseRecipes(string json)
        {
            if (string.IsNullOrEmpty(json) || !JsonField.Bool(json, "ok"))
            {
                return CraftRecipesOutcome.Failure(
                    JsonField.Of(json, "reason").Length > 0 ? JsonField.Of(json, "reason") : "bad_reply");
            }

            var outcome = new CraftRecipesOutcome
            {
                Ok = true,
                Reason = JsonField.Of(json, "reason"),
                TableVersion = JsonField.Int(json, "table_version"),
                Pending = JsonField.Bool(json, "pending"),
                Level = JsonField.Int(json, "level"),
                ProfessionsText = Professions(json),
                Recipes = new List<CraftRecipeView>(),
                DisplayText = "配方读好了",
            };

            foreach (var one in JsonField.Objects(json, "recipes"))
            {
                outcome.Recipes.Add(new CraftRecipeView
                {
                    Id = JsonField.Of(one, "id"),
                    Profession = JsonField.Of(one, "profession"),
                    UnlockLevel = JsonField.Int(one, "unlock_level"),
                    Unlocked = JsonField.Bool(one, "unlocked"),
                    SuccessPct = JsonField.Int(one, "success_pct"),
                    CraftTimeS = JsonField.Int(one, "craft_time_s"),
                    OutputItemId = OutputOf(one, "item_id"),
                    OutputCount = OutputInt(one, "count"),
                    Note = JsonField.Of(one, "note"),
                    InputsText = Pairs(one, "inputs", "item_id", "count"),
                    FeeText = Pairs(one, "fee", "kind", "amount"),
                });
            }

            return outcome;
        }

        public static CraftStateOutcome ParseState(string json)
        {
            return new CraftStateOutcome
            {
                Ok = JsonField.Bool(json, "ok"),
                Reason = JsonField.Of(json, "reason"),
                Active = JsonField.Bool(json, "active"),
                RecipeId = JsonField.Of(json, "recipe_id"),
                Profession = JsonField.Of(json, "profession"),
                CraftTimeS = JsonField.Int(json, "craft_time_s"),
                StartedAtUnix = JsonField.Long(json, "started_at_unix"),
                ReadyAtUnix = JsonField.Long(json, "ready_at_unix"),
                RemainingS = JsonField.Int(json, "remaining_s"),
                DisplayText = JsonField.Bool(json, "active") ? "手上有活" : "手上没活",
            };
        }

        public static CraftStartOutcome ParseStart(string json)
        {
            var ok = JsonField.Bool(json, "ok");
            var reason = JsonField.Of(json, "reason");
            var duplicate = JsonField.Bool(json, "duplicate");
            return new CraftStartOutcome
            {
                Ok = ok,
                Reason = reason,
                Duplicate = duplicate,
                RecipeId = JsonField.Of(json, "recipe_id"),
                Profession = JsonField.Of(json, "profession"),
                CraftTimeS = JsonField.Int(json, "craft_time_s"),
                StartedAtUnix = JsonField.Long(json, "started_at_unix"),
                ReadyAtUnix = JsonField.Long(json, "ready_at_unix"),
                RemainingS = JsonField.Int(json, "remaining_s"),
                DisplayText = ok
                    ? (duplicate ? "这一手已经记过账，不重复扣材料" : "已经开始，等倒计时")
                    : "开始被拒：" + reason,
            };
        }

        public static CraftFinishOutcome ParseFinish(string json)
        {
            var ok = JsonField.Bool(json, "ok");
            var reason = JsonField.Of(json, "reason");
            var duplicate = JsonField.Bool(json, "duplicate");
            var success = JsonField.Bool(json, "success");
            var attr = JsonField.Int(json, "craft_attr");
            var extras = new List<CraftExtraView>();
            foreach (var one in JsonField.Objects(json, "craft_extras"))
            {
                extras.Add(new CraftExtraView
                {
                    Attr = JsonField.Of(one, "attr"),
                    Value = JsonField.Int(one, "value"),
                });
            }

            string text;
            if (!ok)
            {
                text = "收取被拒：" + reason;
            }
            else if (!success)
            {
                text = "这一手没成（材料照扣），产出 0";
            }
            else if (attr > 0 || extras.Count > 0)
            {
                text = "做成 " + JsonField.Of(json, "output_item_id") + "，掷点 " + attr
                    + " + " + extras.Count + " 条附加";
            }
            else
            {
                text = "做成 " + JsonField.Of(json, "output_item_id") + "（不是装备，没有掷点）";
            }

            return new CraftFinishOutcome
            {
                Ok = ok,
                Reason = reason,
                Duplicate = duplicate,
                RecipeId = JsonField.Of(json, "recipe_id"),
                Profession = JsonField.Of(json, "profession"),
                Success = success,
                OutputItemId = JsonField.Of(json, "output_item_id"),
                OutputCount = JsonField.Int(json, "output_count"),
                SuccessPct = JsonField.Int(json, "success_pct"),
                CraftTimeS = JsonField.Int(json, "craft_time_s"),
                ReadyAtUnix = JsonField.Long(json, "ready_at_unix"),
                CraftAttr = attr,
                CraftExtras = extras,
                DisplayText = text,
            };
        }

        /// <summary>副职名照回包拼一段；没写就是空串。</summary>
        static string Professions(string json)
        {
            var text = string.Empty;
            foreach (var one in JsonField.Objects(json, "professions"))
            {
                var name = JsonField.Of(one, "name");
                if (name.Length == 0)
                {
                    continue;
                }

                text = text.Length == 0 ? name : text + " / " + name;
            }

            return text;
        }

        /// <summary>产出是嵌在 `output` 对象里的；读不到就回空。里面没有嵌套对象，按第一个花括号取即可。</summary>
        static string OutputOf(string json, string key)
        {
            var at = json.IndexOf("\"output\"", StringComparison.Ordinal);
            if (at < 0)
            {
                return string.Empty;
            }

            var open = json.IndexOf('{', at);
            if (open < 0)
            {
                return string.Empty;
            }

            var close = json.IndexOf('}', open);
            if (close < 0)
            {
                return string.Empty;
            }

            return JsonField.Of(json.Substring(open, close - open + 1), key);
        }

        static int OutputInt(string json, string key)
        {
            int.TryParse(OutputOf(json, key), out var value);
            return value;
        }

        /// <summary>把一组「键 + 数量」拼成「k×n / k×n」；只拼串，不求和。</summary>
        static string Pairs(string json, string arrayKey, string nameKey, string valueKey)
        {
            var text = string.Empty;
            foreach (var one in JsonField.Objects(json, arrayKey))
            {
                var name = JsonField.Of(one, nameKey);
                if (name.Length == 0)
                {
                    continue;
                }

                var one1 = name + "×" + JsonField.Int(one, valueKey).ToString(CultureInfo.InvariantCulture);
                text = text.Length == 0 ? one1 : text + " / " + one1;
            }

            return text;
        }

        static string Escape(string value) =>
            (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
