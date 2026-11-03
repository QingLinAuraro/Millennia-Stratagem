/// <summary>
/// 词条的「名字 + 效果」文案表(§4.3 基础表 B26:D64)。
///
/// 【为什么要单独一份,而不是直接读 [Description]】
///   CardData.cs 里 Keyword 枚举上的 [Description] 只有**名字**(闪击/重甲…),
///   没有"它到底干什么"的说明 —— 而词条栏要弹的正是这句说明。
///   把效果文案集中在这里,卡面、词条栏、以后的帮助页都读同一份,不会各写一套。
///
/// 【改文案改这里】
///   数值口径以《数据表demo.xlsx·基础公式》为准(血战是回复 2 HP、重甲是每层 -1 伤害…),
///   策划改了数值只改这个文件。
///
/// 【buff 类不在这里】
///   「+ATK / +HP」属于 buff 类**效果**,不是词条(§4.3 表末明确写了"只写进效果栏,不占词条栏"),
///   所以词条栏不列 ATK/HP 的变化,这里也就不需要它们。
/// </summary>
public static class KeywordInfo
{
    /// <summary>词条名(闪击 / 重甲 …),取枚举上的 Description</summary>
    public static string Name(Keyword keyword) => keyword.GetDescription();

    /// <summary>词条效果的一句话说明(词条栏/悬停面板用)</summary>
    public static string Describe(Keyword keyword) => keyword switch
    {
        Keyword.Blitz          => "部署回合即可移动或攻击",
        Keyword.Ambush         => "主动攻击前不可被攻击",
        Keyword.Guard          => "同排相邻的友方单位与建筑不能被选中",
        Keyword.BloodBattle    => "攻击后回复 2 点 HP",
        Keyword.DoubleStrike   => "同一回合可连续攻击两次",
        Keyword.HeavyArmor     => "每层使受到的伤害 -1",
        Keyword.Resist         => "免疫压制",
        _                      => "",
    };

    /// <summary>带层数的词条名:重甲 2 层 →「重甲 ×2」,单层就直接是「重甲」</summary>
    public static string NameWithCount(Keyword keyword, int count)
        => count > 1 ? $"{Name(keyword)} ×{count}" : Name(keyword);
}
