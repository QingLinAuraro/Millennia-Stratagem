using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 给 AI 随机构筑一套**合法**卡组(策划案§5)。
///
/// 【为什么随机筑而不是固定一套】
///   固定一套 AI 卡组打两局就被摸透了。随机筑出来的每局都不一样,而且它同时是一个
///   "规则自检器" —— 只要随机筑的结果能通过 ValidateCards,就说明 §5 的规则是可满足的
///   (不是自相矛盾的),这比人肉核对有用。
///
/// 【怎么保证随机出来一定合法】
///   不是"随便抽 30 张再校验,不合法就重抽"(那可能在坏卡池上死循环)。而是**按配额倒着填**:
///     1) 先随机定主朝代(汉或秦);
///     2) 缺口从"最难满足"的那档开始填 —— 传说只有 3 种可选且不能同名,先占掉;
///     3) 主朝代至少 25 张这条最容易破,所以先用主朝代的卡把 25 个坑填满,
///        剩下的 5 张才轮到次朝代 —— 这样主朝代下限天然成立,不需要事后修;
///     4) 每填一张就检查同名上限,超了就换一张,而不是硬塞。
///   任何一步都填不出卡时(卡池太小)会 LogError 并返回 null,由调用方决定怎么办。
///
/// 【挂载 & 调整】
///   挂在:不是组件,纯静态。
///   引用:没有。
///   常调:· 想换 AI 的构筑风格,改普通/稀有/史诗三档的"权重"(现在各档均匀随机)。
///         · 想让 AI 更强,把配额里普通卡的比例调低 —— 但那要同步改 DeckQuota。
/// </summary>
public static class DeckRandomizer
{
    /// <summary>
    /// 随机构筑一套合法卡组。失败(卡池不足/找不到卡)返回 null。
    /// </summary>
    /// <param name="library">卡池。留空自动取 CardLibrary.Current</param>
    /// <param name="primaryDynasty">指定主朝代。留空则在卡池所有朝代里随机</param>
    public static List<CardData> Build(CardLibrary library = null, string primaryDynasty = null)
    {
        library ??= CardLibrary.Current;
        if (library == null || library.Count == 0)
        {
            Debug.LogError("[AI 构筑] 卡池是空的(CardLibrary.Current 没扫到卡),没法随机筑。");
            return null;
        }

        var dynasties = library.Dynasties();
        if (dynasties.Count == 0)
        {
            Debug.LogError("[AI 构筑] 卡池里一个朝代都没有。");
            return null;
        }

        if (string.IsNullOrEmpty(primaryDynasty))
            primaryDynasty = dynasties[Random.Range(0, dynasties.Count)];

        string secondary = null;
        for (int i = 0; i < dynasties.Count; i++)
        {
            if (dynasties[i] != primaryDynasty) { secondary = dynasties[i]; break; }
        }

        var quota = new DeckQuota();
        var result = new List<CardData>();
        var used = new Dictionary<CardData, int>();

        // ---- 先把传说占掉:可选种类最少(每朝代 3 种)、且不能同名,最容易后面没得选 ----
        var primaryLegends = library.Filter(Rarity.Legend, primaryDynasty, null);
        if (!TryFill(result, used, primaryLegends, quota.legend, Rarity.Legend))
        {
            Debug.LogError($"[AI 构筑] 主朝代「{primaryDynasty}」凑不出 {quota.legend} 张传说卡" +
                           $"(只有 {primaryLegends.Count} 种可选,且不能同名)。");
            return null;
        }

        // ---- 史诗、稀有、普通:只用主朝代的卡 ----
        // 先用主朝代把 25 张下限填满,主朝代不足 25 张时下面会报出来
        if (!TryFill(result, used, library.Filter(Rarity.Epic, primaryDynasty, null), quota.epic, Rarity.Epic))
        {
            Debug.LogError($"[AI 构筑] 主朝代「{primaryDynasty}」凑不出 {quota.epic} 张史诗卡。");
            return null;
        }

        if (!TryFill(result, used, library.Filter(Rarity.Rare, primaryDynasty, null), quota.rare, Rarity.Rare))
        {
            Debug.LogError($"[AI 构筑] 主朝代「{primaryDynasty}」凑不出 {quota.rare} 张稀有卡。");
            return null;
        }

        // 普通位 + 弹性位一起用普通卡填(普通卡同名能放 4 张,最容易凑够)
        int ordinaryNeed = quota.ordinary + quota.flexible;
        if (!TryFill(result, used, library.Filter(Rarity.Ordinary, primaryDynasty, null),
                     ordinaryNeed, Rarity.Ordinary))
        {
            Debug.LogError($"[AI 构筑] 主朝代「{primaryDynasty}」凑不出 {ordinaryNeed} 张普通卡。");
            return null;
        }

        // ---- 到这里主朝代张数可能只有 25 上下,多余的坑用次朝代补到 30 ----
        // 注意:目标不是"一定要混次朝代",而是"主朝代 >= 25 且总数 30"。
        // 上面按配额填完正好是 quota.Total 张,所以这段通常一张都不加 ——
        // 留着是为了改配额(比如普通位调少)之后仍然成立。
        int deficit = quota.Total - result.Count;
        if (deficit > 0 && !string.IsNullOrEmpty(secondary))
        {
            // 次朝代只能填普通/稀有/史诗(§5.3.2 不得编入传说)
            var pool = new List<CardData>();
            pool.AddRange(library.Filter(Rarity.Ordinary, secondary, null));
            pool.AddRange(library.Filter(Rarity.Rare, secondary, null));
            pool.AddRange(library.Filter(Rarity.Epic, secondary, null));

            int want = Mathf.Min(deficit, DeckPreset.SecondaryDynastyMax);
            if (!TryFillMixed(result, used, pool, want))
                Debug.LogWarning($"[AI 构筑] 次朝代「{secondary}」只补上了部分缺口,卡组会短 {deficit - (quota.Total - result.Count)} 张。");
        }

        // ---- 自检:随机筑的结果必须过规则,不过就说明这个函数有 bug ----
        var problems = new List<string>();
        if (!DeckPreset.ValidateCards(result, quota, primaryDynasty, problems))
        {
            Debug.LogError($"[AI 构筑] 随机构筑出来的卡组**不合法**(这是 DeckRandomizer 的 bug,不是卡池问题):\n" +
                           $"  · {string.Join("\n  · ", problems)}");
            return null;
        }

        // 打乱顺序:不然牌堆里同一种卡是连着的(Deck.Shuffle 也会洗,但这样更自然)
        for (int i = 0; i < result.Count; i++)
        {
            int j = Random.Range(i, result.Count);
            (result[i], result[j]) = (result[j], result[i]);
        }

        return result;
    }

    /// <summary>
    /// 从 pool 里随机拿 want 张填进去,**只用同一个朝代**。同名上限内允许重复。
    /// 凑不齐返回 false(卡池太小),此时 result 里已经填进去的会保留 —— 调用方直接放弃即可。
    /// </summary>
    private static bool TryFill(List<CardData> result, Dictionary<CardData, int> used,
                                List<CardData> pool, int want, Rarity rarity)
    {
        if (want <= 0) return true;
        if (pool == null || pool.Count == 0) return false;

        int limit = DeckPreset.MaxCopiesOf(rarity);

        // 候选表:每种卡最多能再放几张(用副本计数,避免反复算)
        var candidates = new List<CardData>();
        for (int i = 0; i < pool.Count; i++)
        {
            used.TryGetValue(pool[i], out int have);
            for (int k = have; k < limit; k++) candidates.Add(pool[i]);
        }

        if (candidates.Count < want) return false;

        for (int n = 0; n < want; n++)
        {
            int pick = Random.Range(0, candidates.Count);
            var card = candidates[pick];

            // 抽走这一张。candidates 里每种卡已经按"还能放几张"摊成了重复项
            // (比如某张普通卡还能放 3 张就出现 3 次),所以这里只移除被抽中的**这一项**,
            // 不要再去删同名项 —— 多删会让后面凑不满 want 张。
            candidates.RemoveAt(pick);

            used.TryGetValue(card, out int cur);
            used[card] = cur + 1;
            result.Add(card);
        }
        return true;
    }

    /// <summary>同 TryFill,但 pool 是混合朝代(次朝代的补位用),不额外按稀有度限同名</summary>
    private static bool TryFillMixed(List<CardData> result, Dictionary<CardData, int> used,
                                     List<CardData> pool, int want)
    {
        if (want <= 0) return true;
        if (pool == null || pool.Count == 0) return false;

        var candidates = new List<CardData>();
        for (int i = 0; i < pool.Count; i++)
        {
            var card = pool[i];
            if (card == null) continue;
            used.TryGetValue(card, out int have);
            int limit = DeckPreset.MaxCopiesOf(card.rarity);
            for (int k = have; k < limit; k++) candidates.Add(card);
        }

        if (candidates.Count == 0) return false;

        int placed = 0;
        for (int n = 0; n < want && candidates.Count > 0; n++)
        {
            int pick = Random.Range(0, candidates.Count);
            var card = candidates[pick];

            used.TryGetValue(card, out int cur);
            used[card] = cur + 1;
            result.Add(card);
            placed++;

            int limit = DeckPreset.MaxCopiesOf(card.rarity);
            for (int c = candidates.Count - 1; c >= 0; c--)
                if (candidates[c] == card && used[card] >= limit) candidates.RemoveAt(c);
        }
        return placed == want;
    }

    /// <summary>随机构筑并包成一份 DeckPreset(内存实例,不落盘)。战斗里 AI 用它,不用资产</summary>
    public static DeckPreset BuildPreset(CardLibrary library = null, string primaryDynasty = null)
    {
        var cards = Build(library, primaryDynasty);
        if (cards == null) return null;

        var preset = ScriptableObject.CreateInstance<DeckPreset>();
        preset.deckName = "AI 随机卡组";
        preset.primaryDynasty = primaryDynasty ?? (cards.Count > 0 ? cards[0].dynasty : "汉");
        preset.description = "每局随机生成";
        preset.cards = cards;
        return preset;
    }
}
