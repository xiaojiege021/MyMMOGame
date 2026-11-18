namespace Mmo.Shared.Domain;

/// <summary>
/// 队伍分配口径的词（Q8/H2）。这些字符串只认一处写法：社交服按
/// config/party.json 校验取值，角色服把社交服回的事实值转给战斗服，
/// 战斗服按它分拣掉落。三个进程共用这一份常量，不各写一遍字面量。
/// </summary>
public static class PartyLoot
{
    /// <summary>个人份额：每人各发整份，不掷点（默认口径，方案写定）。</summary>
    public const string Personal = "personal";

    /// <summary>需求 / 贪婪：全队只出这一份，服务端掷点选一个赢家。</summary>
    public const string NeedGreed = "need_greed";

    public const string RollNone = "none";

    /// <summary>掷点只在服务端。客户端不掷、也不上报点数。</summary>
    public const string RollServer = "server";

    /// <summary>需求：参与掷点，且比贪婪优先（同一件上有人 need 时只有 need 的人参与）。</summary>
    public const string IntentNeed = "need";

    /// <summary>贪婪：参与掷点，但没有 need 时才算。</summary>
    public const string IntentGreed = "greed";

    /// <summary>放弃：这一件不参与。</summary>
    public const string IntentPass = "pass";
}
