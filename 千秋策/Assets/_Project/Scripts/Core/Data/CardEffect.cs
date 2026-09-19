using System.Collections.Generic;
using System.ComponentModel;   // [Description]

public enum CardEffectKind
{
    [Description("无")] None,                   // 没登记效果(兵牌多半是空的,只有带部署增益的支援卡才有)
    [Description("造成伤害")] Damage,             // 造成伤害
    [Description("增减属性")] Buff,               // 加减 ATK / HP;默认一次性增幅、持续整局(见 CardEffect.durationRounds)
    [Description("抽牌")] DrawCards,             // 抽 N 张(牌堆空时逐张结算疲劳,§5.4.1)
    [Description("回复 HP")] Heal,               // 回血:建筑(维修,§7.3)/ 兵牌 / 大营都走这一条,目标由 scope 定
    [Description("弃置手牌")] DiscardHand,        // 弃置敌方手牌(被弃的牌直接退场,§5.4)
    [Description("压制")] Restrict,              // 压制:限制目标一回合行动
    [Description("召唤(牌堆)")] Summon,           // 召唤:往牌堆里加一张牌(§4.3 召唤(牌堆))
}

/// <summary>效果的结算范围:打谁 / 加谁 / 回谁</summary>
public enum CardEffectScope
{
    [Description("指定目标")] Target,      // 就是这张牌指定的那个目标(§7.2.2 拖过去的那一个)
    [Description("敌方随机单位")] Random,    // 敌方随机一个单位(卡面写「随机单位」,不需要玩家指定)
    [Description("敌方全体")] AllEnemy,    // 敌方全体
    [Description("己方全体")] AllAlly,     // 友方全体
    [Description("指定整排")] Row,         // 指定的整排(以排为目标的范围效果,无视守护,§4.3)
    [Description("出牌方")] Caster,        // 出牌方自己(「为一座己方建筑回复 HP」这类无目标回血走这条)
    [Description("自动挑选")] Auto,        // 由结算器自动挑一个最合适的目标(多目标卡的第 2/3 个目标、支援卡的部署增益)
}

/// <summary>
/// 一条效果绑在哪一个目标上。多目标策略卡(决水灌城:友方单位 + 敌方排;盐铁论:敌方单位 + 己方建筑 + 友方单位)
/// 的第 2、3 个目标在 CardData 里没有字段可放(§7.2.2 只登记了第一个),所以在这里按顺序登记:
/// 结算时玩家先指定第一个目标,剩下的由这里按「自动挑最优」补齐。
/// </summary>
public enum CardEffectTargetSlot
{
    [Description("第一目标(玩家指定)")] Primary,     // 第一个目标 = 玩家拖过去指定的那个(CardData.targetType 里登记的就是它)
    [Description("第二目标(自动)")] Secondary,       // 第二个目标 = 自动挑(例如「对指定一排单位造成2点伤害」的那一排)
    [Description("第三目标(自动)")] Tertiary,        // 第三个目标 = 自动挑
}

/// <summary>
/// 兵种大类:决定射程与反击(策划案§2.6「同类互攻才反击」)。
/// 不属于卡牌效果,但当初就住在这个文件里,Table 里的 BattleRules.ClassOf 要用它。
/// </summary>
public enum CombatClass
{
    [Description("近战")] Melee,      // 步兵 / 骑兵
    [Description("远程")] Ranged,     // 弓兵 / 器械(支援)
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
///       **默认 0 = 持续无限回合**(一次性增幅,加了就一直留着,直到单位退场);
///       填 1 = 只扛到下一次轮到自己开始时失效(卡面写「本回合」的才填 1);填 N = 扛 N 轮。
///     · scope:打谁 / 加谁 / 回谁,见上面 CardEffectScope 的说明。
///       回血类靠它区分目标:Target = 玩家指的那个,Auto = 结算器自动挑一个最该回的,
///       Caster = 出牌方自己(「为一座己方建筑回复 HP」这种无目标写法)。
///     · targetSlot:绑这张卡的第几个目标。
///     · atkValue / hpValue:只有 kind == Buff 用;atkValue 是 ATK 增减,hpValue 是 HP 增减(可为负)。
/// </summary>
public class CardEffect
{
    // ===== 核心字段(私有 set,保证运行时不可变) =====
    public CardEffectKind kind { get; private set; } = CardEffectKind.None;
    public CardEffectScope scope { get; private set; } = CardEffectScope.Target;
    public CardEffectTargetSlot targetSlot { get; private set; } = CardEffectTargetSlot.Primary;

    /// <summary>点数:伤害 / 回血 / 抽牌张数 / 压制回合数</summary>
    public int amount { get; private set; }

    /// <summary>kind == Buff 时的 ATK 增减</summary>
    public int atkValue { get; private set; }

    /// <summary>kind == Buff 时的 HP 增减</summary>
    public int hpValue { get; private set; }

    /// <summary>持续几个轮次。0(默认)= 持续无限回合,即一次性永久增幅;1 = 本回合;N = N 轮</summary>
    public int durationRounds { get; private set; }

    /// <summary>kind == Summon 时:往牌堆里加几张什么牌(cardId)</summary>
    public string summonCardId { get; private set; }

    /// <summary>kind == Summon 时:加几张</summary>
    public int summonCount { get; private set; } = 1;

    // 私有构造函数,禁止外部直接创建 —— 统一走 Create / 下面的语义化工厂方法
    private CardEffect() { }

    /// <summary>唯一创建入口。想加一种没被工厂方法覆盖的效果,从这里走</summary>
    public static CardEffect Create(
        CardEffectKind kind,
        int amount = 0,
        CardEffectScope scope = CardEffectScope.Target,
        CardEffectTargetSlot slot = CardEffectTargetSlot.Primary,
        int atkValue = 0,
        int hpValue = 0,
        string summonCardId = null,
        int summonCount = 1,
        int durationRounds = 0)
    {
        return new CardEffect
        {
            kind = kind,
            amount = amount,
            scope = scope,
            targetSlot = slot,
            atkValue = atkValue,
            hpValue = hpValue,
            summonCardId = summonCardId,
            summonCount = summonCount,
            durationRounds = durationRounds,
        };
    }

    /// <summary>日志/飘字里用的可读说明</summary>
    public string Describe()
    {
        switch (kind)
        {
            case CardEffectKind.Damage:
                return $"{ScopeName(scope)}造成 {amount} 点伤害";

            case CardEffectKind.Buff:
            {
                string span = durationRounds > 0 ? $"本回合" : "永久";
                return $"{ScopeName(scope)}{span} ATK{(atkValue >= 0 ? "+" : "")}{atkValue}、HP{(hpValue >= 0 ? "+" : "")}{hpValue}";
            }

            case CardEffectKind.DrawCards:    return $"抽 {amount} 张牌";
            case CardEffectKind.Heal:         return $"{ScopeName(scope)}回复 {amount} 点 HP";
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
        CardEffectScope.Auto     => "自动挑选的",
        _                        => "目标",
    };

    // ===== 语义化工厂方法(登记表里一行一张,省得到处填字段) =====

    public static CardEffect Damage(int amount, CardEffectScope scope = CardEffectScope.Target,
                                    CardEffectTargetSlot slot = CardEffectTargetSlot.Primary)
        => Create(CardEffectKind.Damage, amount, scope, slot);

    /// <summary>加减 ATK / HP。默认持续无限回合(一次性永久增幅),要「本回合」就传 rounds: 1</summary>
    public static CardEffect Buff(int atk, int hp, CardEffectScope scope = CardEffectScope.Target,
                                  CardEffectTargetSlot slot = CardEffectTargetSlot.Primary, int rounds = 0)
        => Create(CardEffectKind.Buff, 0, scope, slot, atk, hp, null, 1, rounds);

    public static CardEffect Draw(int count)
        => Create(CardEffectKind.DrawCards, count, CardEffectScope.Caster);

    /// <summary>
    /// 回血(建筑 / 兵牌 / 大营都走这一条)。目标靠 scope 区分:
    ///   · 不传 scope → Target:玩家指定的那个目标(维修类卡拖到己方建筑上就是回建筑)
    ///   · CardEffectScope.Auto → 结算器自动挑最该回的那个
    ///   · CardEffectScope.Caster → 出牌方自己(无目标回血,例如「为一座己方建筑回复 7 点 HP」)
    /// </summary>
    public static CardEffect Heal(int amount, CardEffectScope scope = CardEffectScope.Target,
                                  CardEffectTargetSlot slot = CardEffectTargetSlot.Primary)
        => Create(CardEffectKind.Heal, amount, scope, slot);

    public static CardEffect Discard(int count)
        => Create(CardEffectKind.DiscardHand, count, CardEffectScope.Target);

    public static CardEffect Suppress(int rounds = 1, CardEffectTargetSlot slot = CardEffectTargetSlot.Primary)
        => Create(CardEffectKind.Restrict, rounds, CardEffectScope.Target, slot);

    /// <summary>召唤(牌堆):往己方牌堆里加 summonCount 张 summonCardId(§4.3)</summary>
    public static CardEffect Summon(string summonCardId, int summonCount = 1)
        => Create(CardEffectKind.Summon, 0, CardEffectScope.Caster, CardEffectTargetSlot.Primary,
                  summonCardId: summonCardId, summonCount: summonCount);
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
