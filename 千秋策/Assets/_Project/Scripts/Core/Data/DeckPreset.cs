using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// 一套卡组的稀有度**上限**(策划案§5.2)。
///
/// 为什么是上限而不是"正好几张":
///   规则只关心「别把一套牌堆成四张传说」,不关心玩家到底塞了几张普通卡 ——
///   卡组总张数由 DeckPreset.DeckSize 卡死,普通位天然就是"剩下多少全归我",
///   所以四档里只有三档需要真正设限,普通那一档填 39(= 40 张减掉大营那 1 张)即可。
///   如果按"正好几张"来卡,玩家想多带两张稀有卡、少带两张普通卡都算非法 —— 没有必要。
///
/// 大营卡**不计入**这里的任何一档:它是固定位,开局必带 1 张,不进稀有度统计
/// (见 ValidateCards 里 `cardType == Building` 直接 continue 的那一段)。
///
/// 为什么单独一个可序列化类,而不是写死在 DeckPreset 里:
///   上限是**规则**,将来调平衡(比如「传说放宽到 6 张」)只改资产上的数字,不用动代码;
///   而且 Inspector 上看得见,不至于每次都要翻策划案。
/// </summary>
[System.Serializable]
public class DeckQuota
{
    [Tooltip("普通卡张数上限(默认 39 = 40 张减去 1 张大营,等于「普通卡没有实际限制」)")]
    public int maxOrdinary = 39;
    [Tooltip("稀有卡张数上限")]
    public int maxRare = 12;
    [Tooltip("史诗卡张数上限")]
    public int maxEpic = 8;
    [Tooltip("传说卡张数上限")]
    public int maxLegend = 4;

    /// <summary>这一档的**上限**。英雄档不占位(cardType 里没有这种卡),出现就是多余,按 0 处理</summary>
    public int MaxOf(Rarity rarity) => rarity switch
    {
        Rarity.Ordinary => maxOrdinary,
        Rarity.Rare => maxRare,
        Rarity.Epic => maxEpic,
        Rarity.Legend => maxLegend,
        _ => 0,
    };

    /// <summary>
    /// 目标构筑(不是规则,只是**建议分配**):4 传说 + 8 史诗 + 12 稀有 + 15 普通 = 39。
    /// 上限吃满前三档、剩下全给普通,正好铺满 40 张里的 39 个非大营位。
    /// 给 DeckRandomizer(AI 随机筑)和 DeckAssetGenerator(两套默认卡组)当铺牌方案用。
    /// </summary>
    public int TargetOf(Rarity rarity) => rarity switch
    {
        Rarity.Ordinary => 15,
        Rarity.Rare => 12,
        Rarity.Epic => 8,
        Rarity.Legend => 4,
        _ => 0,
    };
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
///   常调:· Cards:卡组内容。**必须带正好 1 张大营卡**(cardType = Building)——
///           它只占一个位置、决定战场大营用哪张图,不进牌堆,也不计入稀有度上限;
///           军械库/粮草**不是卡**,由 BuildingManager 开局按固定表摆。
///         · quota:稀有度**上限**,见 DeckQuota。
///   自检:IsValid() 会把"张数不对 / 稀有度超上限 / 同名超限 / 朝代混编违规 / 大营不是 1 张"逐条列出来。
///         EditorDeckTools 的菜单「千秋策/卡组/校验全部卡组」会对着所有资产跑一遍。
/// </summary>
[CreateAssetMenu(fileName = "NewDeck", menuName = "千秋策/卡组")]
public class DeckPreset : ScriptableObject
{
    [Header("身份")]
    [Tooltip("卡组名,显示在主菜单的卡组列表里")]
    public string deckName = "新卡组";
    [Tooltip("主朝代(策划案§5.3.1:主朝代卡不少于 29 张)")]
    public string primaryDynasty = "汉";
    [Tooltip("一句话说明(卡组定位,显示在卡组名下面)")]
    [TextArea] public string description;

    [Header("构筑")]
    [Tooltip("卡组内容,共 40 张 = 39 张可构筑 + 1 张大营(§5.1)。可以重复同一张卡,同名上限见 MaxCopiesOf")]
    public List<CardData> cards = new List<CardData>();

    [Header("规则(策划案§5)")]
    [Tooltip("稀有度上限(§5.2):普通 39 / 稀有 12 / 史诗 8 / 传说 4,大营不计入")]
    public DeckQuota quota = new DeckQuota();

    // ================================================================ 规则常量(§5)

    /// <summary>卡组总张数(§5.1):39 张可构筑 + 1 张固定大营</summary>
    public const int DeckSize = 40;
    /// <summary>主朝代卡的下限(§5.3.1)</summary>
    public const int PrimaryDynastyMin = 29;
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
        int expected = DeckSize;
        if (cards.Count != expected)
            problems.Add($"共 {cards.Count} 张,规则要求 {expected} 张(§5.1)");

        // ---- 稀有度上限 + 同名 + 朝代混编 ----
        var byRarity = new Dictionary<Rarity, int>();
        var byCard = new Dictionary<CardData, int>();
        int primary = 0, secondary = 0, legendFromSecondary = 0, camps = 0;

        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c == null) { problems.Add($"第 {i + 1} 个位置是空的"); continue; }

            // 大营卡是"固定位":不占稀有度上限,也不参与朝代混编统计(它本来就是主朝代的)
            if (c.cardType == CardType.Building) { camps++; continue; }

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

        // 稀有度只设上限,不设下限(§5.2):不是"必须正好 4 张传说",而是"最多 4 张"。
        // 少带传说完全合法 —— 少出来的位置自然被普通卡填满(总张数已经由上面卡死)。
        foreach (var kv in byRarity)
        {
            int got = kv.Value;
            int limit = quota.MaxOf(kv.Key);
            if (got > limit)
                problems.Add($"{RarityName(kv.Key)} {got} 张,超过上限 {limit} 张(§5.2)");
        }

        // ---- 大营固定位 ----
        if (camps != 1)
            problems.Add($"大营卡 {camps} 张,卡组必须正好带 1 张大营卡");

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

    /// <summary>把这份构筑摊成一行行文字(卡组详情面板 / 校验日志用)</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append($"{deckName}(主{primaryDynasty}, {CardCount} 张)");

        var byRarity = new Dictionary<Rarity, int>();
        int primary = 0, secondary = 0, camps = 0;
        for (int i = 0; i < CardCount; i++)
        {
            var c = cards[i];
            if (c == null) continue;
            // 大营不计入稀有度统计(和 ValidateCards 同一套口径)
            if (c.cardType == CardType.Building) { camps++; continue; }

            byRarity.TryGetValue(c.rarity, out int rc);
            byRarity[c.rarity] = rc + 1;
            if (c.dynasty == primaryDynasty) primary++; else secondary++;
        }

        sb.Append($"\n  主朝 {primary} / 次朝 {secondary} · 大营 {camps}");
        var q = quota ?? new DeckQuota();
        foreach (Rarity r in new[] { Rarity.Ordinary, Rarity.Rare, Rarity.Epic, Rarity.Legend })
        {
            byRarity.TryGetValue(r, out int n);
            sb.Append($" · {RarityName(r)} {n}/{q.MaxOf(r)}");
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
