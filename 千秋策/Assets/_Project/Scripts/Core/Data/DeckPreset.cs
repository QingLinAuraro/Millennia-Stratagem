using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// 一套卡组的稀有度配额(策划案§5.2)。
///
/// 为什么单独一个可序列化类,而不是写死在 DeckPreset 里:
/// 配额是**规则**,将来调平衡(比如「传说放宽到 3 张」)只改资产上的数字,不用动代码;
/// 而且 Inspector 上看得见,不至于每次都要翻策划案。
/// </summary>
[System.Serializable]
public class DeckQuota
{
    [Tooltip("普通卡张数")]
    public int ordinary = 12;
    [Tooltip("稀有卡张数")]
    public int rare = 8;
    [Tooltip("史诗卡张数")]
    public int epic = 4;
    [Tooltip("传说卡张数")]
    public int legend = 2;
    [Tooltip("任意品质的弹性位(只能填普通/稀有/史诗,不能填传说)")]
    public int flexible = 4;

    /// <summary>硬性总张数 = 各档之和(12+8+4+2+4 = 30,策划案§5.1)</summary>
    public int Total => ordinary + rare + epic + legend + flexible;
}

/// <summary>
/// 一套**已构筑好的卡组**,作为资产存盘(菜单里选中的那套、以及两套默认卡组都是它)。
///
/// 它是"卡组"而不是"牌堆":牌堆是 Deck(运行时的洗牌/抽牌),这里只是**构筑结果**,
/// 进战斗时把 Cards 交给 DeckController.Init 洗成牌堆。
///
/// 【为什么卡组要独立于场景】
///   之前 DeckController.deckList 是手拖在 Battle.unity 上的 12 张引用 —— 那样主菜单选了什么卡组,
///   战斗场景根本无从得知(它是场景里写死的)。改成资产之后,菜单选中的 DeckPreset 通过
///   BattleContext(一个跨场景的静态中转)带进战斗场景,Battle 只负责"把选中那套洗成牌堆"。
///
/// 【挂载 & 调整】
///   挂在:不是组件。资产放 Assets/_Project/Resources/Decks/ 下 —— **必须在 Resources 里**,
///         主菜单是用 Resources.LoadAll&lt;DeckPreset&gt;("Decks") 拿卡组列表的。
///         右键 Create → 千秋策 → 卡组 可以手建;两套默认卡组由 DeckAssetGenerator 自动生成。
///   引用:Cards 手拖,或者用编辑器菜单「千秋策/卡组/重新生成两套默认卡组」生成。
///   常调:· Cards:卡组内容。**不要往里拖建筑(大营/军械库/粮草)** —— 建筑由
///           BuildingManager 开局按固定表摆,不进牌堆(见 DeckController 的说明)。
///         · quota:稀有度配额,见 DeckQuota。
///   自检:IsValid() 会把"张数不对 / 稀有度配额不符 / 同名超限 / 朝代混编违规"逐条列出来。
///         EditorDeckTools 的菜单「千秋策/卡组/校验全部卡组」会对着所有资产跑一遍。
/// </summary>
[CreateAssetMenu(fileName = "NewDeck", menuName = "千秋策/卡组")]
public class DeckPreset : ScriptableObject
{
    [Header("身份")]
    [Tooltip("卡组名,显示在主菜单的卡组列表里")]
    public string deckName = "新卡组";
    [Tooltip("主朝代(策划案§5.3.1:主朝代卡不少于 25 张)")]
    public string primaryDynasty = "汉";
    [Tooltip("一句话说明(卡组定位,显示在卡组名下面)")]
    [TextArea] public string description;

    [Header("构筑")]
    [Tooltip("卡组内容,共 30 张(§5.1)。可以重复同一张卡,同名上限见 quota / §5.2")]
    public List<CardData> cards = new List<CardData>();

    [Header("规则(策划案§5)")]
    [Tooltip("稀有度配额(§5.2)")]
    public DeckQuota quota = new DeckQuota();

    // ================================================================ 规则常量(§5)

    /// <summary>卡组总张数(§5.1)</summary>
    public const int DeckSize = 30;
    /// <summary>主朝代卡的下限(§5.3.1)</summary>
    public const int PrimaryDynastyMin = 25;
    /// <summary>次朝代卡的上限(§5.3.2)</summary>
    public const int SecondaryDynastyMax = 5;

    /// <summary>同名卡上限(§5.2):普通 4 / 稀有 3 / 史诗 2 / 传说 1,英雄档按传说处理</summary>
    public static int MaxCopiesOf(Rarity rarity) => rarity switch
    {
        Rarity.Ordinary => 4,
        Rarity.Rare => 3,
        Rarity.Epic => 2,
        Rarity.Legend => 1,
        _ => 1,          // Hero(英雄):还没用过这一档,按最严处理,免得漏判
    };

    /// <summary>这张卡属于哪一档配额</summary>
    private static int QuotaOf(DeckQuota q, Rarity rarity) => rarity switch
    {
        Rarity.Ordinary => q.ordinary,
        Rarity.Rare => q.rare,
        Rarity.Epic => q.epic,
        Rarity.Legend => q.legend,
        _ => 0,          // 英雄档不占配额(卡池里没有),出现就是多余的
    };

    public int CardCount => cards != null ? cards.Count : 0;

    /// <summary>这份构筑是否成立。不成立时 problems 里是逐条原因(给菜单和自检看)</summary>
    public bool IsValid(List<string> problems = null)
        => ValidateCards(cards, quota, primaryDynasty, problems);

    /// <summary>
    /// 校验任意一组卡是否满足构筑规则(§5)。抽成静态是为了让**还没落成资产**的构筑也能验:
    /// 编辑器把 PlayerPrefs 里存的卡组转成资产之前,得先确认它合法;菜单里也要实时提示。
    /// </summary>
    public static bool ValidateCards(List<CardData> cards, DeckQuota quota, string primaryDynasty,
                                     List<string> problems = null)
    {
        problems ??= new List<string>();
        problems.Clear();
        quota ??= new DeckQuota();

        if (cards == null || cards.Count == 0)
        {
            problems.Add("卡组是空的");
            return false;
        }

        // ---- 张数(§5.1) ----
        int expected = quota.Total;
        if (cards.Count != expected)
            problems.Add($"共 {cards.Count} 张,规则要求 {expected} 张(§5.1)");

        // ---- 稀有度配额 + 同名 + 朝代混编 ----
        var byRarity = new Dictionary<Rarity, int>();
        var byCard = new Dictionary<CardData, int>();
        int primary = 0, secondary = 0, legendFromSecondary = 0;

        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c == null) { problems.Add($"第 {i + 1} 个位置是空的"); continue; }

            byRarity.TryGetValue(c.rarity, out int rc);
            byRarity[c.rarity] = rc + 1;

            byCard.TryGetValue(c, out int cc);
            byCard[c] = cc + 1;

            if (c.dynasty == primaryDynasty) primary++;
            else
            {
                secondary++;
                if (c.rarity == Rarity.Legend || c.rarity == Rarity.Hero)
                    legendFromSecondary++;
            }
        }

        // 弹性位只能被普通/稀有/史诗占用,所以这三档允许在 [配额, 配额+弹性位] 之间
        foreach (var kv in byRarity)
        {
            int got = kv.Value;
            int want = QuotaOf(quota, kv.Key);
            int flexibleRoom = IsElastic(kv.Key) ? quota.flexible : 0;
            if (got < want)
                problems.Add($"{RarityName(kv.Key)} {got} 张,少于配额 {want} 张(§5.2)");
            else if (got > want + flexibleRoom)
                problems.Add($"{RarityName(kv.Key)} {got} 张,超出配额 {want}(弹性位最多再塞 {flexibleRoom} 张,§5.2)");
        }

        // 传说一张都不能少:它是唯一不许被弹性位替代的档(§5.2「4任意普通/稀有/史诗」)
        byRarity.TryGetValue(Rarity.Legend, out int legends);
        if (legends != quota.legend)
            problems.Add($"传说 {legends} 张,必须正好 {quota.legend} 张(§5.2)");

        // ---- 同名上限(§5.2) ----
        foreach (var kv in byCard)
        {
            int limit = MaxCopiesOf(kv.Key.rarity);
            if (kv.Value > limit)
                problems.Add($"「{kv.Key.cardName}」放了 {kv.Value} 张,同名上限 {limit} 张(§5.2)");
        }

        // ---- 朝代混编(§5.3) ----
        if (primary < PrimaryDynastyMin)
            problems.Add($"主朝代「{primaryDynasty}」只有 {primary} 张,不少于 {PrimaryDynastyMin} 张(§5.3.1)");
        if (secondary > SecondaryDynastyMax)
            problems.Add($"次朝代共 {secondary} 张,不多于 {SecondaryDynastyMax} 张(§5.3.2)");
        if (legendFromSecondary > 0)
            problems.Add($"次朝代编入了 {legendFromSecondary} 张传说卡,次朝代不得编入传说(§5.3.2)");

        return problems.Count == 0;
    }

    /// <summary>这一档能不能占用弹性位(§5.2:只有「4 张任意普通/稀有/史诗」这三种能占)</summary>
    private static bool IsElastic(Rarity rarity)
        => rarity == Rarity.Ordinary || rarity == Rarity.Rare || rarity == Rarity.Epic;


    /// <summary>把这份构筑摊成一行行文字(卡组详情面板 / 校验日志用)</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append($"{deckName}(主{primaryDynasty}, {CardCount} 张)");

        var byRarity = new Dictionary<Rarity, int>();
        int primary = 0, secondary = 0;
        for (int i = 0; i < CardCount; i++)
        {
            var c = cards[i];
            if (c == null) continue;
            byRarity.TryGetValue(c.rarity, out int rc);
            byRarity[c.rarity] = rc + 1;
            if (c.dynasty == primaryDynasty) primary++; else secondary++;
        }

        sb.Append($"\n  主朝 {primary} / 次朝 {secondary}");
        foreach (Rarity r in new[] { Rarity.Ordinary, Rarity.Rare, Rarity.Epic, Rarity.Legend })
        {
            byRarity.TryGetValue(r, out int n);
            sb.Append($" · {RarityName(r)} {n}");
        }
        return sb.ToString();
    }

    public static string RarityName(Rarity rarity) => rarity switch
    {
        Rarity.Ordinary => "普通",
        Rarity.Rare => "稀有",
        Rarity.Epic => "史诗",
        Rarity.Legend => "传说",
        Rarity.Hero => "英雄",
        _ => rarity.ToString(),
    };
}
