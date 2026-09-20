using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 全卡池登记表:卡组构筑界面要"按稀有度/朝代/兵种翻牌",不能只有牌堆里的引用。
///
/// 【为什么不是手挂的资产】
///   卡牌资产放在 Assets/_Project/Resources/Cards/ 下,这里的 Current 直接
///   Resources.LoadAll 扫出来 —— **加一张新卡不用改任何东西**,而手拖一份清单则每加一卡就得记得来补
///   (漏了的表现是"卡池里看不到这张卡",而且不报错)。
///   代价是卡牌资产必须待在 Resources 目录里(Unity 的硬性要求),移动资产别搬出这个目录。
///
/// 【挂载 & 调整】
///   挂在:不是组件、也不是资产文件 —— 纯运行时对象,Current 按需扫一次并缓存。
///   引用:没有要手连的引用。在 Data/Cards 之外另建卡池目录的话,改 CardsResourcePath。
///   常调:· CardsResourcePath:卡牌资产所在 Resources 子目录,默认 "Cards"。
/// </summary>
public class CardLibrary : ScriptableObject
{
    /// <summary>卡牌资产所在的 Resources 子目录(相对 Assets/_Project/Resources)</summary>
    public const string CardsResourcePath = "Cards";

    public List<CardData> cards = new List<CardData>();

    private static CardLibrary current;

    /// <summary>
    /// 运行时可用的那一份。第一次访问时扫描 + 排序,之后返回缓存。
    /// 菜单和战斗侧都从这儿取,省得各处自己 Resources.LoadAll。
    /// </summary>
    public static CardLibrary Current
    {
        get
        {
            if (current != null) return current;

            current = CreateInstance<CardLibrary>();
            current.name = "CardLibrary(运行时)";

            var loaded = Resources.LoadAll<CardData>(CardsResourcePath);
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Length; i++)
                    if (loaded[i] != null) current.cards.Add(loaded[i]);

                // 排序:朝代 → 稀有度 → cardId。界面按这个顺序排牌,不用每个界面自己排一遍。
                // cardId 带朝代前缀(han_001 / qin_001),同朝代内按 id 排就是按序号排。
                current.cards.Sort((a, b) =>
                {
                    int d = string.CompareOrdinal(a.dynasty, b.dynasty);
                    if (d != 0) return d;
                    int r = a.rarity.CompareTo(b.rarity);
                    if (r != 0) return r;
                    return string.CompareOrdinal(a.cardId, b.cardId);
                });
            }

            if (current.cards.Count == 0)
                Debug.LogError($"[卡池] Resources/{CardsResourcePath} 下一张卡都没扫到。" +
                               "检查卡牌资产是不是还在那个目录里(Unity 只认 Resources 目录下的资产)。");

            return current;
        }
    }

    /// <summary>强制重新扫一遍(编辑器里改完卡牌资产用;运行时不用调)</summary>
    public static void Reload()
    {
        current = null;
        _ = Current;
    }

    public int Count => cards != null ? cards.Count : 0;

    /// <summary>按 cardId 找卡。找不到返回 null</summary>
    public CardData Find(string cardId)
    {
        if (string.IsNullOrEmpty(cardId) || cards == null) return null;
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c != null && c.cardId == cardId) return c;
        }
        return null;
    }

    /// <summary>按卡名找卡(调试/工具用,正式逻辑请走 cardId)</summary>
    public CardData FindByName(string cardName)
    {
        if (string.IsNullOrEmpty(cardName) || cards == null) return null;
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c != null && c.cardName == cardName) return c;
        }
        return null;
    }

    /// <summary>挑出符合条件的一批卡(构筑界面分页/筛选用)。两个条件传 null 表示不限</summary>
    public List<CardData> Filter(Rarity? rarity = null, string dynasty = null, CardType? type = null)
    {
        var result = new List<CardData>();
        if (cards == null) return result;

        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c == null) continue;
            if (rarity.HasValue && c.rarity != rarity.Value) continue;
            if (!string.IsNullOrEmpty(dynasty) && c.dynasty != dynasty) continue;
            if (type.HasValue && c.cardType != type.Value) continue;
            result.Add(c);
        }
        return result;
    }

    /// <summary>卡池里出现过的所有朝代(按首次出现顺序,稳定的列表顺序便于界面排版)</summary>
    public List<string> Dynasties()
    {
        var result = new List<string>();
        if (cards == null) return result;
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c == null || string.IsNullOrEmpty(c.dynasty)) continue;
            if (!result.Contains(c.dynasty)) result.Add(c.dynasty);
        }
        return result;
    }

    /// <summary>按稀有度统计张数(卡组构筑界面显示"普通 8 种 / 稀有 8 种"这类信息)</summary>
    public int CountOf(Rarity rarity)
    {
        int n = 0;
        if (cards == null) return 0;
        for (int i = 0; i < cards.Count; i++)
            if (cards[i] != null && cards[i].rarity == rarity) n++;
        return n;
    }
}
