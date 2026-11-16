using System;
using System.Globalization;
using System.Text;
using Mmo.Net;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// U11 图鉴自检：九格名字、收集度、每位与合计的被动 bps 只认 `/hero-codex` 回包。
    /// 回包里的中文按网关的形态写成 `\uXXXX`，自检顺带验一遍拆装层有没有把名字解开；
    /// 屏上不算百分比、不按主线场次推、不补编回包里没有的人。
    /// </summary>
    public static class UiHeroCodexSelfCheck
    {
        /// <summary>九位（与产品表同名，只作这一份假回包的内容，运行期不打开任何配置文件）。</summary>
        static readonly string[] Names =
        {
            "乔小满", "苏晚织", "阮青", "阿弦", "闻嫂", "孟七", "澄川", "灰生", "周晚棠",
        };

        /// <summary>各条在主线首场出现的场次（假回包自己的设定，自检只按它推收没收到）。</summary>
        static readonly int[] UnlockStages = { 1, 1, 2, 2, 3, 3, 4, 4, 5 };

        /// <summary>各条的被动万分比（假回包的占位数，屏上只照抄）。</summary>
        static readonly int[] PassiveBps = { 20, 20, 25, 25, 30, 30, 35, 35, 40 };

        static bool _failed;

        static int _mainStage;

        static int _status;

        static bool _emptyEntries;

        public static bool Run()
        {
            _failed = false;
            _mainStage = 3;
            _status = 200;
            _emptyEntries = false;

            UiSkinStore.Reset();
            var shell = UiShell.Ensure();
            shell.SetSkin(UiSkin.Light, false);
            var flow = UiSocialFlow.Ensure(shell);
            flow.Bind("u11-self-token", 9);

            flow.SetSender((path, body, token, done) =>
            {
                if (path.IndexOf(HeroCodexEntry.Path + "?", StringComparison.Ordinal) < 0)
                {
                    done(404, "{}");
                    return;
                }

                done(_status, _status == 200 ? CodexJson() : "{\"error\":\"character_not_found\"}");
            });

            // 一、尚未请求：不画格子、不拿原型里的名字顶上。
            shell.ShowPage("英灵图鉴", s => UiHeroCodexScreen.Build(s, flow));
            var pending = Text(shell);
            Report("pending", pending.Contains("尚未请求") && pending.Contains("读取图鉴")
                && pending.Contains("还没有回包")
                && !pending.Contains(Names[0])
                && !pending.Contains("已收集"));
            Debug.Log("u11_self_pending=" + pending.Contains("读取图鉴"));

            // 二、九格与名字：都来自回包，服务端把中文写成 \uXXXX 也要解开。
            flow.LoadHeroCodex();
            shell.ShowPage("英灵图鉴", s => UiHeroCodexScreen.Build(s, flow));
            var entries = Text(shell);
            var allNames = true;
            for (var i = 0; i < Names.Length; i++)
            {
                allNames &= entries.Contains(Names[i]);
            }

            Report("entries", flow.HasCodex && flow.LastCodex.Entries.Count == 9
                && entries.Contains("回包 9 条")
                && entries.Contains("回包 收集 6 / 9 · 万分比 6666 · 被动合计 150 bps · life_assist")
                && entries.Contains("已接 /hero-codex")
                && allNames);
            Debug.Log("u11_self_entries names=" + allNames
                + " count=" + flow.LastCodex.Entries.Count
                + " collected=" + flow.LastCodex.CollectedCount);

            // 三、收集度：前六位已收集、后三位未收集，都按回包的 collected 写。
            Report("collected", entries.Contains(Names[0] + "\n已收集 · 被动 20 bps")
                && entries.Contains(Names[5] + "\n已收集 · 被动 30 bps")
                && entries.Contains(Names[6] + "\n未收集 · 被动 0 bps")
                && entries.Contains(Names[8] + "\n未收集 · 被动 0 bps"));

            // 四、被动 bps：每位与合计都照抄回包的万分比，不换算、不乘。
            var entrySix = HeroCodexEntry.EntryAt(flow.LastCodex, 6);
            Report("passive", flow.LastCodex.TotalPassivePctBps == 150
                && flow.LastCodex.CollectionPctBps == 6666
                && entrySix != null && entrySix.Value.PassivePctBps == 0
                && entries.Contains("被动合计 150 bps")
                && entries.Contains("主线场次 main_stage = 3")
                && entries.Contains("被动种类 passive_kind = life_assist"));
            Debug.Log("u11_self_passive total=" + flow.LastCodex.TotalPassivePctBps
                + " pct=" + flow.LastCodex.CollectionPctBps);

            // 五、主线推进后重读：收集度跟着回包变，屏上不自己加一位。
            _mainStage = 4;
            flow.LoadHeroCodex();
            shell.ShowPage("英灵图鉴", s => UiHeroCodexScreen.Build(s, flow));
            var advanced = Text(shell);
            Report("advance", advanced.Contains("回包 收集 8 / 9 · 万分比 8888 · 被动合计 220 bps")
                && advanced.Contains(Names[7] + "\n已收集 · 被动 35 bps")
                && advanced.Contains(Names[8] + "\n未收集 · 被动 0 bps"));
            Debug.Log("u11_self_advance collected=" + flow.LastCodex.CollectedCount);

            // 六、回包没给条目：不画格子，也不补编一位。
            _emptyEntries = true;
            flow.LoadHeroCodex();
            shell.ShowPage("英灵图鉴", s => UiHeroCodexScreen.Build(s, flow));
            var empty = Text(shell);
            Report("no_entries", flow.LastCodex.Entries.Count == 0
                && empty.Contains("回包里没有条目 · 不补编一位")
                && empty.Contains("服务端这次没给条目（entry_count=0）")
                && !empty.Contains(Names[0]));
            _emptyEntries = false;

            // 七、没读到：写回执，不假装「尚未请求」，也不编一个 0/9。
            _status = 404;
            flow.LoadHeroCodex();
            shell.ShowPage("英灵图鉴", s => UiHeroCodexScreen.Build(s, flow));
            var rejected = Text(shell);
            Report("reject", !flow.HasCodex && rejected.Contains("图鉴没读到：404")
                && rejected.Contains("再读一次")
                && rejected.Contains("没读到")
                && !rejected.Contains(Names[0]));
            Debug.Log("u11_self_reject=" + rejected.Contains("图鉴没读到：404"));

            // 八、切皮肤只换颜色：布局与文案一字不差。
            _status = 200;
            _mainStage = 3;
            flow.LoadHeroCodex();
            shell.ShowPage("英灵图鉴", s => UiHeroCodexScreen.Build(s, flow));
            var layout = Layout(shell);
            var copy = Text(shell);
            shell.SetSkin(UiSkin.Dark, false);
            Report("skin_layout", layout == Layout(shell));
            Report("skin_copy", copy == Text(shell));

            shell.ClosePage();
            UiSkinStore.Reset();
            if (flow)
            {
                UnityEngine.Object.DestroyImmediate(flow.gameObject);
            }

            if (shell)
            {
                try { UnityEngine.Object.DestroyImmediate(shell.gameObject); }
                catch (Exception) { }
            }

            Debug.Log("u11_self_all=" + (_failed ? "fail" : "ok"));
            return !_failed;
        }

        /// <summary>
        /// 假回包。中文按网关的形态写成 `\uXXXX`（`WriteAsJsonAsync` 默认转义非 ASCII），
        /// 拆装层不解开的话九格上会是反斜杠加十六进制。
        /// </summary>
        static string CodexJson()
        {
            var count = _emptyEntries ? 0 : Names.Length;
            var collected = 0;
            var total = 0;
            var rows = new bool[count];
            var passives = new int[count];
            for (var i = 0; i < count; i++)
            {
                rows[i] = _mainStage >= UnlockStages[i];
                passives[i] = rows[i] ? PassiveBps[i] : 0;
                if (rows[i])
                {
                    collected++;
                    total += passives[i];
                }
            }

            var pct = count == 0 ? 0 : collected * 10000 / count;
            var sb = new StringBuilder(512);
            sb.Append("{\"ok\":true,\"character_id\":9,\"main_stage\":").Append(_mainStage)
                .Append(",\"collected_count\":").Append(collected)
                .Append(",\"entry_count\":").Append(count)
                .Append(",\"collection_pct_bps\":").Append(pct)
                .Append(",\"total_passive_pct_bps\":").Append(total)
                .Append(",\"passive_kind\":\"life_assist\",\"entries\":[");
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"id\":\"").Append("hero_").Append(i)
                    .Append("\",\"name\":\"").Append(Escaped(Names[i]))
                    .Append("\",\"unlock_main_stage\":").Append(UnlockStages[i])
                    .Append(",\"collected\":").Append(rows[i] ? "true" : "false")
                    .Append(",\"passive_pct_bps\":").Append(passives[i]).Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>把一段文字写成 JSON 的 `\uXXXX`，与网关发出来的形态一致。</summary>
        static string Escaped(string text)
        {
            var sb = new StringBuilder(text.Length * 6);
            for (var i = 0; i < text.Length; i++)
            {
                sb.Append("\\u").Append(((int)text[i]).ToString("x4", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        static string Text(UiShell shell)
        {
            var sb = new StringBuilder();
            var labels = shell.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                if (labels[i] != null && labels[i].text.Length > 0)
                {
                    sb.AppendLine(labels[i].text);
                }
            }

            return sb.ToString();
        }

        static string Layout(UiShell shell)
        {
            var sb = new StringBuilder();
            var rects = shell.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                var r = rects[i];
                sb.Append(r.name).Append(':')
                    .Append(r.anchoredPosition.x.ToString("0")).Append(',')
                    .Append(r.anchoredPosition.y.ToString("0")).Append(',')
                    .Append(r.sizeDelta.x.ToString("0")).Append(',')
                    .Append(r.sizeDelta.y.ToString("0")).Append(';');
            }

            return sb.ToString();
        }

        static void Report(string key, bool ok)
        {
            Debug.Log("u11_self_" + key + "=" + (ok ? "ok" : "fail"));
            if (!ok) _failed = true;
        }
    }
}
