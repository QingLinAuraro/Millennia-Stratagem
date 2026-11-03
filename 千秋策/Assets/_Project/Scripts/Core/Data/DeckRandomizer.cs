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
///   稀有度只设**上限**(普通 ≤39 / 稀有 ≤12 / 史诗 ≤8 / 传说 ≤4),没有下限,
///   所以"合不合法"归结成两件事:总张数正好 40、每一档都别超上限。
///   这里的做法是**按目标分配倒着填**,填不满就往下一档让位:
///     1) 先随机定主朝代(汉或秦,排除「通用」补位池);
///     2) 按 传说 → 史诗 → 稀有 → 普通 的顺序填,每档的目标张数取自 DeckQuota.TargetOf(4/8/12/15),
///        实际取 min(目标, 该档上限, 剩余坑位);某档凑不满(比如传说只有 3 种且不能同名)就把
///        空出来的坑留给下一档 —— 这也是为什么不再要求"必须正好 4 张传说";
///     3) 普通卡兜底把 39 个坑填满,再从次朝代随机换进最多 5 张(同稀有度替换,不吃掉任何上限);
///     4) 最后插 1 张大营卡(不占稀有度),然后拿 ValidateCards 自检一遍。
///   任何一步都填不出卡时(卡池太小)会 LogError 并返回 null,由调用方决定怎么办。
///
/// 【挂载 & 调整】
///   挂在:不是组件,纯静态。
///   引用:没有。
///   常调:· 想让 AI 更强/更弱,改 DeckQuota.TargetOf 的目标分配(上限是规则,目标是偏好)。
///         · 不想让 AI 混次朝代,把下面 SplashSecondary 那句删掉即可(主朝代 39 张本身也合法)。
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
        // 「通用」是补位卡池,不是阵营:它一张传说都没有,当主朝代会被稀有度/朝代规则卡住。
        dynasties.Remove(CardLibrary.CommonDynasty);
        if (dynasties.Count == 0)
        {
            Debug.LogError("[AI 构筑] 排除通用卡池后没有可选的主朝代。");
            return null;
        }

        if (string.IsNullOrEmpty(primaryDynasty))
            primaryDynasty = dynasties[Random.Range(0, dynasties.Count)];
        else if (!dynasties.Contains(primaryDynasty))
        {
            Debug.LogError($"[AI 构筑] 指定的主朝代「{primaryDynasty}」不在卡池里" +
                           $"(可选:{string.Join("、", dynasties)})。");
            return null;
        }

        string secondary = null;
        for (int i = 0; i < dynasties.Count; i++)
        {
            if (dynasties[i] != primaryDynasty) { secondary = dynasties[i]; break; }
        }

        var quota = new DeckQuota();
        var result = new List<CardData>();
        var used = new Dictionary<CardData, int>();

        // ---- 大营固定位:先占掉,它不计入稀有度上限,所以剩下的坑 = 40 - 1 = 39 ----
        var campCards = library.Filter(null, primaryDynasty, CardType.Building);
        if (campCards.Count == 0)
        {
            Debug.LogError($"[AI 构筑] 主朝代「{primaryDynasty}」没有大营卡(cardType=Building)。");
            return null;
        }
        result.Add(campCards[Random.Range(0, campCards.Count)]);

        // ---- 按 传说 → 史诗 → 稀有 → 普通 填满 39 个坑 ----
        // 每档取 min(目标, 上限, 剩余坑位);FillUpTo 填不满就返回实际张数,剩下的让给下一档
        int budget = DeckPreset.DeckSize - 1;
        var tiers = new[] { Rarity.Legend, Rarity.Epic, Rarity.Rare, Rarity.Ordinary };
        for (int i = 0; i < tiers.Length; i++)
        {
            var rarity = tiers[i];
            if (budget <= 0) break;

            // 普通档是**兜底档**:它必须把剩下的坑全吃掉,不然总张数会差几张 ——
            // 传说只有 3 种(每种 1 张)这种"凑不满目标"的情况很常见,少出来的位置全靠它补。
            int want = rarity == Rarity.Ordinary
                ? Mathf.Min(quota.MaxOf(rarity), budget)
                : Mathf.Min(Mathf.Min(quota.TargetOf(rarity), quota.MaxOf(rarity)), budget);
            if (want <= 0) continue;

            budget -= FillUpTo(result, used, PlayablePool(library, rarity, primaryDynasty), want, rarity);
        }

        // 主朝代的卡不够 39 张时,用次朝代的普通/稀有/史诗补(不得编入传说,§5.3.2)
        if (budget > 0 && !string.IsNullOrEmpty(secondary))
        {
            var pool = new List<CardData>();
            pool.AddRange(PlayablePool(library, Rarity.Ordinary, secondary));
            pool.AddRange(PlayablePool(library, Rarity.Rare, secondary));
            pool.AddRange(PlayablePool(library, Rarity.Epic, secondary));
            budget -= FillUpToMixed(result, used, pool, budget);
        }

        if (budget > 0)
        {
            Debug.LogError($"[AI 构筑] 主朝代「{primaryDynasty}」的卡池凑不满 {DeckPreset.DeckSize} 张" +
                           $"(还差 {budget} 张),卡池是不是少了几种卡?");
            return null;
        }

        // ---- 次朝代风味:同稀有度换进最多 5 张(替换不吃掉任何上限,主朝代仍 ≥ 34 张) ----
        if (!string.IsNullOrEmpty(secondary))
            SplashSecondary(result, used, library, primaryDynasty, secondary, DeckPreset.SecondaryDynastyMax);

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
    /// 某一朝代、某一稀有度里**可以进牌堆**的卡。
    /// 必须排掉大营卡:它的 rarity 也是「普通」,不排的话会被当普通卡抽进牌堆,
    /// 结果卡组里出现 2 张大营(一张固定位 + 一张"普通卡"),校验直接判非法。
    /// </summary>
    private static List<CardData> PlayablePool(CardLibrary library, Rarity rarity, string dynasty)
    {
        var pool = library.Filter(rarity, dynasty, null);
        for (int i = pool.Count - 1; i >= 0; i--)
            if (pool[i] == null || pool[i].cardType == CardType.Building) pool.RemoveAt(i);
        return pool;
    }

    /// <summary>
    /// 从 pool 里随机拿**最多** want 张填进去,只用同一个朝代。同名上限内允许重复。
    /// 返回实际填了几张 —— 凑不满不算失败(上限规则下少几张是合法的,空位让给下一档)。
    /// </summary>
    private static int FillUpTo(List<CardData> result, Dictionary<CardData, int> used,
                                List<CardData> pool, int want, Rarity rarity)
    {
        if (want <= 0 || pool == null || pool.Count == 0) return 0;

        int limit = DeckPreset.MaxCopiesOf(rarity);

        // 候选表:每种卡最多能再放几张(用副本计数,避免反复算)
        var candidates = new List<CardData>();
        for (int i = 0; i < pool.Count; i++)
        {
            if (pool[i] == null) continue;
            used.TryGetValue(pool[i], out int have);
            for (int k = have; k < limit; k++) candidates.Add(pool[i]);
        }

        int take = Mathf.Min(want, candidates.Count);
        for (int n = 0; n < take; n++)
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
        return take;
    }

    /// <summary>同 FillUpTo,但 pool 是混合朝代(次朝代的补位用),同名上限按各自稀有度算</summary>
    private static int FillUpToMixed(List<CardData> result, Dictionary<CardData, int> used,
                                     List<CardData> pool, int want)
    {
        if (want <= 0 || pool == null || pool.Count == 0) return 0;

        var candidates = new List<CardData>();
        for (int i = 0; i < pool.Count; i++)
        {
            var card = pool[i];
            if (card == null) continue;
            used.TryGetValue(card, out int have);
            int limit = DeckPreset.MaxCopiesOf(card.rarity);
            for (int k = have; k < limit; k++) candidates.Add(card);
        }

        int take = Mathf.Min(want, candidates.Count);
        for (int n = 0; n < take; n++)
        {
            int pick = Random.Range(0, candidates.Count);
            var card = candidates[pick];
            candidates.RemoveAt(pick);

            used.TryGetValue(card, out int cur);
            used[card] = cur + 1;
            result.Add(card);
        }
        return take;
    }

    /// <summary>
    /// 把最多 maxSplash 张主朝代卡换成次朝代的**同稀有度**卡(只换普通/稀有/史诗 —— 次朝代不得编入传说)。
    /// 因为是"换"不是"加",稀有度上限、总张数都不动;主朝代从 39 掉到 34 仍然 ≥ PrimaryDynastyMin。
    /// 返回换成了几张。
    /// </summary>
    private static int SplashSecondary(List<CardData> result, Dictionary<CardData, int> used,
                                       CardLibrary library, string primaryDynasty, string secondary,
                                       int maxSplash)
    {
        if (maxSplash <= 0 || string.IsNullOrEmpty(secondary)) return 0;

        // 可被替换的位置:主朝代的普通/稀有/史诗(大营和传说都不动)
        var indices = new List<int>();
        for (int i = 0; i < result.Count; i++)
        {
            var c = result[i];
            if (c == null || c.dynasty != primaryDynasty) continue;
            if (c.cardType == CardType.Building) continue;
            if (c.rarity == Rarity.Legend || c.rarity == Rarity.Hero) continue;
            indices.Add(i);
        }

        int want = Mathf.Min(maxSplash, indices.Count);
        int done = 0;
        for (int n = 0; n < want && indices.Count > 0; n++)
        {
            int pick = Random.Range(0, indices.Count);
            int idx = indices[pick];
            indices.RemoveAt(pick);

            var old = result[idx];
            var pool = PlayablePool(library, old.rarity, secondary);

            // 只挑还没吃满同名上限的,免得换完自己把自己判非法
            var usable = new List<CardData>();
            for (int i = 0; i < pool.Count; i++)
            {
                var candidate = pool[i];
                if (candidate == null) continue;
                used.TryGetValue(candidate, out int have);
                if (have < DeckPreset.MaxCopiesOf(candidate.rarity)) usable.Add(candidate);
            }
            if (usable.Count == 0) continue;

            var replacement = usable[Random.Range(0, usable.Count)];
            used[old] = used[old] - 1;
            used.TryGetValue(replacement, out int rc);
            used[replacement] = rc + 1;
            result[idx] = replacement;
            done++;
        }
        return done;
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
