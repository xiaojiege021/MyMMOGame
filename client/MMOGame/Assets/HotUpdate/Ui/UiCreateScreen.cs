using System;
using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 立身屏（原型 A02「创角」）。
    ///
    /// 四种出身、五个职业，名号一个输入框。出身只改外观与出身对白，不产生属性差、
    /// 不锁职业；分支不列——15 级一转才选，创角时分支留空（方案 8.4）。
    ///
    /// 三样齐了「入世」才可按；按下去只把编号与外观交给服务端，等级、境界、
    /// 阵营、分支一概不发。
    /// </summary>
    public static class UiCreateScreen
    {
        /// <summary>这一屏建好的日志行。</summary>
        public const string ReadyLog = "u2_create=ready";

        /// <summary>写定规则四行：左边是什么，右边管什么。</summary>
        static readonly string[][] Rules =
        {
            new[] { "出身", "外观 + 出身对白，无属性" },
            new[] { "职业", "决定技能与职责" },
            new[] { "分支", "15 级一转，五职业各两条" },
            new[] { "境界", "45 级后才开启" },
        };

        /// <summary>建屏。</summary>
        public static void Build(UiShell shell, UiEntryFlow flow)
        {
            var m = shell.Metrics;
            var pad = m.Px(120f);
            var top = m.Px(190f);
            var gap = m.Px(60f);
            var colW = (m.Width - pad * 2f - gap) / 2f;
            var leftX = pad;
            var rightX = pad + colW + gap;

            var raceId = string.Empty;
            var classId = string.Empty;

            var races = new List<UiOption>();
            var roles = new List<UiOption>();
            UiTextField name = null;
            UiButton submit = null;

            Action sync = () =>
            {
                for (var i = 0; i < races.Count; i++)
                {
                    races[i].Apply(shell.Palette);
                }

                for (var i = 0; i < roles.Count; i++)
                {
                    roles[i].Apply(shell.Palette);
                }

                flow.SyncCreateButton();
            };

            // ---------------------------------------------------------- 左：出身与名号

            shell.Label(m.Band(leftX, top, colW, m.Px(40f)),
                "出 身 · 四 选 一", UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);

            var cardW = (colW - m.Px(24f)) / 2f;
            var cardH = m.Px(150f);
            var gridTop = top + m.Px(56f);

            for (var i = 0; i < UiEntryCodex.Races.Length; i++)
            {
                var race = UiEntryCodex.Races[i];
                var column = i % 2;
                var row = i / 2;
                var rect = m.Band(leftX + column * (cardW + m.Px(24f)),
                    gridTop + row * (cardH + m.Px(20f)), cardW, cardH);

                var option = new UiOption(shell.Content, rect, race.Id, race.Name, race.Origin,
                    UiOptionKind.Card, shell.Size(30f), shell.Size(20f));
                var captured = race.Id;
                option.OnPick = picked =>
                {
                    raceId = captured;
                    for (var k = 0; k < races.Count; k++)
                    {
                        races[k].Selected = races[k] == picked;
                    }

                    sync();
                    Debug.Log("u2_create=race id=" + raceId);
                };
                races.Add(option);
                shell.Register(option);
                option.Apply(shell.Palette);
            }

            var nameLabelTop = gridTop + cardH * 2f + m.Px(20f) + m.Px(40f);
            shell.Label(m.Band(leftX, nameLabelTop, colW, m.Px(40f)),
                "名 号 · 至多 " + UiEntryCodex.NameMaxLength + " 字", UiTextRole.Muted,
                shell.Size(24f), TextAnchor.MiddleLeft, false);

            var nameRect = m.Band(leftX, nameLabelTop + m.Px(44f), colW, m.Px(112f));
            // 上面已经有一行「名 号」，输入框自己不重复写标签。
            name = new UiTextField(shell, shell.Content, nameRect, string.Empty, "给这名修士起个名字",
                false, UiEntryCodex.NameMaxLength);
            name.OnChanged = () => sync();

            // ---------------------------------------------------------- 右：职责与写定规则

            shell.Label(m.Band(rightX, top, colW, m.Px(40f)),
                "职 责 · 五 选 一", UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);

            var chipGap = m.Px(14f);
            var chipW = (colW - chipGap * 4f) / 5f;
            var chipH = m.Px(88f);
            var chipTop = top + m.Px(56f);

            for (var i = 0; i < UiEntryCodex.Roles.Length; i++)
            {
                var role = UiEntryCodex.Roles[i];
                var rect = m.Band(rightX + i * (chipW + chipGap), chipTop, chipW, chipH);
                var option = new UiOption(shell.Content, rect, role.Id, role.Name, null,
                    UiOptionKind.Chip, shell.Size(26f), shell.Size(20f));
                var captured = role.Id;
                option.OnPick = picked =>
                {
                    classId = captured;
                    for (var k = 0; k < roles.Count; k++)
                    {
                        roles[k].Selected = roles[k] == picked;
                    }

                    sync();
                    Debug.Log("u2_create=class id=" + classId);
                };
                roles.Add(option);
                shell.Register(option);
                option.Apply(shell.Palette);
            }

            shell.Label(m.Band(rightX, chipTop + chipH + m.Px(36f), colW, m.Px(130f)),
                "出身只改外观与出身对白，不决定阵营、不产生属性差、也不锁职业。"
                + "15 级一转时才选分支，45 级二转只把这一条往前推。创建时分支留空。",
                UiTextRole.Faint, shell.Size(22f), TextAnchor.UpperLeft, true);

            var ruleTop = chipTop + chipH + m.Px(176f);
            var ruleH = m.Px(104f) + Rules.Length * m.Px(46f);
            var panel = shell.Panel("写定规则", m.Band(rightX, ruleTop, colW, ruleH), shell.Size(30f));
            var rowPad = m.Px(28f);
            var rowH = m.Px(46f);
            var y = ruleH - m.Px(84f);
            for (var i = 0; i < Rules.Length; i++)
            {
                y -= rowH;
                shell.LabelIn(panel.Root, new Rect(rowPad, y, colW * 0.24f, rowH), Rules[i][0],
                    UiTextRole.Body, shell.Size(24f), TextAnchor.MiddleLeft, false);
                shell.LabelIn(panel.Root, new Rect(rowPad + colW * 0.24f, y, colW * 0.72f - rowPad * 2f, rowH),
                    Rules[i][1], UiTextRole.Muted, shell.Size(24f), TextAnchor.MiddleLeft, false);
            }

            shell.Label(m.Band(rightX, ruleTop + ruleH + m.Px(24f), colW, m.Px(48f)),
                "不发明：捏脸、性别、模型预览、创角列分支、注册入口。",
                UiTextRole.Warm, shell.Size(22f), TextAnchor.MiddleLeft, false);

            // ---------------------------------------------------------- 底部

            var hint = shell.Label(new Rect(m.Width - pad - m.Px(760f), m.Px(216f), m.Px(760f), m.Px(48f)),
                string.Empty, UiTextRole.Danger, shell.Size(24f), TextAnchor.MiddleRight, false).Label;

            shell.Button(new Rect(pad, m.Px(120f), m.Px(300f), m.Px(80f)),
                "← 返回登入", flow.OpenLogin, UiButtonKind.Quiet);

            submit = shell.Button(new Rect(m.Width - pad - m.Px(320f), m.Px(120f), m.Px(320f), m.Px(84f)),
                "入 世", null, UiButtonKind.Primary);
            var ready = new Func<bool>(() =>
                raceId.Length > 0 && classId.Length > 0 && name.Text.Trim().Length > 0);
            submit.OnClick = () => flow.SubmitCreate(raceId, classId, name.Text);

            flow.BindCreate(name, hint, submit, ready);
            sync();

            Debug.Log(ReadyLog + " races=" + UiEntryCodex.Races.Length
                + " roles=" + UiEntryCodex.Roles.Length
                + " branches_listed=0 name_max=" + UiEntryCodex.NameMaxLength);
        }
    }
}
