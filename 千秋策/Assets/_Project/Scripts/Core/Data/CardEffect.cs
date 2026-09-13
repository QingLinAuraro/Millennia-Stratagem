using System.Collections.Generic;

/// <summary>
/// 一条卡牌效果的类型(策划案§7.2.1 第 3 步「效果结算」要做的事)。
/// 一条效果 = 一个动作,一张卡可以挂多条(例如「抽取1张牌；对敌方随机单位造成3点伤害」= 两条)。
/// </summary>
public enum CardEffectKind
{
    None,          // 没登记效果(兵牌多半是空的,只有带部署增益的支援卡才有)
    Damage,        // 造成伤害
    Buff,          // 加 ATK / 加 HP(§4.3 buff 类:不占词条栏,写进效果栏)
    DrawCards,     // 抽 N 张(牌堆空时逐张结算疲劳,§5.4.1)
    HealBuilding,  // 为建筑回复 HP(维修类,§7.3)
    HealUnit,      // 为兵牌回复 HP
    HealCamp,      // 为大营回复 HP(「攻击后为大营回复 2 HP」那类走词条,这里留给以后的卡)
    DiscardHand,   // 弃置敌方手牌(被弃的牌直接退场,§5.4)
    Restrict,      // 压制:限制目标一回合行动
    Summon,        // 召唤:往牌堆里加一张牌(§4.3 召唤(牌堆))
}

/// <summary>效果的结算范围:打谁 / 加谁</summary>
public enum CardEffectScope
{
    Target,     // 就是这张牌指定的那个目标(§7.2.2 拖过去的那一个)
    Random,     // 敌方随机一个单位(卡面写「随机单位」,不需要玩家指定)
    AllEnemy,   // 敌方全体
    AllAlly,    // 友方全体
    Row,        // 指定的整排(以排为目标的范围效果,无视守护,§4.3)
    Caster,     // 出牌方自己(目前只用于「大营回血」这类)
    Auto,       // 由结算器自动挑一个最合适的目标(多目标卡的第 2/3 个目标、支援卡的部署增益)
}

/// <summary>
/// 一条效果绑在哪一个目标上。多目标策略卡(决水灌城:友方单位 + 敌方排;盐铁论:敌方单位 + 己方建筑 + 友方单位)
/// 的第 2、3 个目标在 CardData 里没有字段可放(§7.2.2 只登记了第一个),所以在这里按顺序登记:
/// 结算时玩家先指定第一个目标,剩下的由这里按「自动挑最优」补齐。
/// </summary>
public enum CardEffectTargetSlot
{
    Primary,     // 第一个目标 = 玩家拖过去指定的那个(CardData.targetType 里登记的就是它)
    Secondary,   // 第二个目标 = 自动挑(例如「对指定一排单位造成2点伤害」的那一排)
    Tertiary,    // 第三个目标 = 自动挑
}

/// <summary>兵种大类:决定射程与反击(策划案§2.6「同类互攻才反击」)</summary>
public enum CombatClass
{
    Melee,      // 步兵 / 骑兵
    Ranged,     // 弓兵 / 器械(支援)
}

/// <summary>
/// 卡面上的一条效果。
///
/// 【挂载 & 调整】
///   挂在:不是组件,也不用手动挂 —— 它由 CardEffectDatabase 按 cardId 建出来,
///         运行时只读;卡牌 .asset 里没有这个字段(效果文案在 CardData.effectText 里),
///         所以改效果 = 改 CardEffectDatabase 里那张卡的登记项,不用碰 .asset。
///   常调:
///     · amount:伤害 / 增益 / 回血 / 抽牌的点数,含义由 kind 决定。
///     · durationRounds:增益持续几个「轮」(双方各走一次算一轮,§2.4)。
///       卡面写「本回合」的一律按 1 轮算(策划案§4.3 支援卡示例就是「本回合 HP+3」),
///       也就是到下一次轮到出牌方自己的回合开始时失效 —— 调大 = 增益多扛一个完整轮次。
///     · scope / targetSlot:打谁 / 绑哪个目标,见上面两个枚举的说明。
///     · atkValue / hpValue:只有 kind == Buff 用;atkValue 是 ATK 增减,hpValue 是 HP 增减(可为负)。
///     · healBuilding / healUnit / healCamp:只有 Heal* 用,现在合并进 amount,留这几个只是为了可读。
/// </summary>
public class CardEffect
{
    public CardEffectKind kind = CardEffectKind.None;
    public CardEffectScope scope = CardEffectScope.Target;

    /// <summary>多目标卡的第几个目标(默认第一个 = 玩家指定的那个)</summary>
    public CardEffectTargetSlot targetSlot = CardEffectTargetSlot.Primary;

    /// <summary>点数:伤害 / 回血 / 抽牌张数 / 压制回合数</summary>
    public int amount;

    /// <summary>kind == Buff 时的 ATK 增减</summary>
    public int atkValue;

    /// <summary>kind == Buff 时的 HP 增减</summary>
    public int hpValue;

    /// <summary>持续几个轮次(1 = 本回合,到下一次轮到自己时失效)</summary>
    public int durationRounds = 1;

    /// <summary>kind == Summon 时:往牌堆里加几张什么牌(cardId)</summary>
    public string summonCardId;

    /// <summary>kind == Summon 时:加几张</summary>
    public int summonCount = 1;

    /// <summary>日志/飘字里用的可读说明</summary>
    public string Describe()
    {
        switch (kind)
        {
            case CardEffectKind.Damage:       return $"{ScopeName(scope)}造成 {amount} 点伤害";
            case CardEffectKind.Buff:         return $"{ScopeName(scope)}本回合 ATK{(atkValue >= 0 ? "+" : "")}{atkValue}、HP{(hpValue >= 0 ? "+" : "")}{hpValue}";
            case CardEffectKind.DrawCards:    return $"抽 {amount} 张牌";
            case CardEffectKind.HealBuilding: return $"{ScopeName(scope)}回复 {amount} 点 HP（建筑）";
            case CardEffectKind.HealUnit:     return $"{ScopeName(scope)}回复 {amount} 点 HP（兵牌）";
            case CardEffectKind.HealCamp:     return $"{ScopeName(scope)}回复 {amount} 点 HP（大营）";
            case CardEffectKind.DiscardHand:  return $"弃置敌方 {amount} 张手牌";
            case CardEffectKind.Restrict:     return $"限制{ScopeName(scope)} {amount} 回合行动";
            case CardEffectKind.Summon:       return $"召唤「{summonCardId}」×{summonCount} 进牌堆";
            default:                          return "（无效果）";
        }
    }

    private static string ScopeName(CardEffectScope scope) => scope switch
    {
        CardEffectScope.Random   => "敌方随机单位",
        CardEffectScope.AllEnemy => "敌方全体",
        CardEffectScope.AllAlly  => "己方全体",
        CardEffectScope.Row      => "指定排",
        CardEffectScope.Caster   => "出牌方",
        _                        => "目标",
    };

    // ===== 常用构造(登记表里一行一张,省得到处填字段) =====

    public static CardEffect Damage(int amount, CardEffectScope scope = CardEffectScope.Target,
                                    CardEffectTargetSlot slot = CardEffectTargetSlot.Primary)
        => new CardEffect { kind = CardEffectKind.Damage, amount = amount, scope = scope, targetSlot = slot };

    public static CardEffect Buff(int atk, int hp, CardEffectScope scope = CardEffectScope.Target,
                                  CardEffectTargetSlot slot = CardEffectTargetSlot.Primary, int rounds = 1)
        => new CardEffect { kind = CardEffectKind.Buff, atkValue = atk, hpValue = hp, scope = scope, targetSlot = slot, durationRounds = rounds };

    public static CardEffect Draw(int count)
        => new CardEffect { kind = CardEffectKind.DrawCards, amount = count, scope = CardEffectScope.Caster };

    public static CardEffect Repair(int amount, CardEffectScope scope = CardEffectScope.Target)
        => new CardEffect { kind = CardEffectKind.HealBuilding, amount = amount, scope = scope };

    public static CardEffect HealUnitFor(int amount, CardEffectScope scope = CardEffectScope.Target,
                                         CardEffectTargetSlot slot = CardEffectTargetSlot.Primary)
        => new CardEffect { kind = CardEffectKind.HealUnit, amount = amount, scope = scope, targetSlot = slot };

    /// <summary>为「离目标最近那个建筑/单位」回血(多目标卡的第 2/3 个目标由结算器自动挑)</summary>
    public static CardEffect AutoRepair(int amount, CardEffectTargetSlot slot = CardEffectTargetSlot.Secondary)
        => new CardEffect { kind = CardEffectKind.HealBuilding, amount = amount, scope = CardEffectScope.Auto, targetSlot = slot };

    public static CardEffect AutoHealUnit(int amount, CardEffectTargetSlot slot = CardEffectTargetSlot.Tertiary)
        => new CardEffect { kind = CardEffectKind.HealUnit, amount = amount, scope = CardEffectScope.Auto, targetSlot = slot };

    public static CardEffect Discard(int count)
        => new CardEffect { kind = CardEffectKind.DiscardHand, amount = count, scope = CardEffectScope.Target };

    public static CardEffect Suppress(int rounds = 1, CardEffectTargetSlot slot = CardEffectTargetSlot.Primary)
        => new CardEffect { kind = CardEffectKind.Restrict, amount = rounds, scope = CardEffectScope.Target, targetSlot = slot };
}

/// <summary>
/// 一张卡的全部效果(按顺序结算,§7.2.1)。兵牌大多只有空表,除了带部署增益的支援卡。
/// </summary>
public class CardEffectSet
{
    public readonly List<CardEffect> effects = new();

    /// <summary>这张卡的效果文案(卡面替换用;为空则沿用 CardData.effectText)</summary>
    public string displayText;

    /// <summary>兵牌部署当回合能不能直接行动(只有「闪击」能,§2.5;这里留给以后"部署即行动"类效果)</summary>
    public bool actsOnDeploy;

    public bool IsEmpty => effects.Count == 0;

    public CardEffectSet Add(CardEffect effect)
    {
        if (effect != null) effects.Add(effect);
        return this;
    }
}
