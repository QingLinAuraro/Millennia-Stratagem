using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// 全卡池的效果登记表
///
/// 为什么要单独一张表,而不是新的 ScriptableObject 字段:
///   · 卡牌 .asset 是 Tools/build_card_assets.py 从《数据表demo.xlsx》生成的,字段与 CardData.cs 一一对应;
///     加一个「结构化效果」字段就要动 xlsx 的列和生成脚本,而策划案现在只给了效果文案(「效果」列)。
///   · 效果文案本身是中文长句,不适合当场正则硬解 —— 但**必须有个兜底**,
///     不然表里漏登记一张,那张策略卡打出去就是白付 CP。
///   所以这里做两层:
///     1. 显式登记(40 条,按 cardId 逐张写清,见下面的 Build())—— 正常走这条,改效果改这里;
///     2. 文案解析兜底(ParseFromText)—— 只在表里没登记的策略卡上触发,并打一条警告,
///        保证新加进卡池的卡至少能做「造成 N 点伤害 / 抽 N 张牌 / 回复 N 点 HP」这几件事。
///   开局还会跑一次 Validate() 对账:登记表里查不到的 id、没登记的策略卡都会直接打出来 ——
///   这张表是**按 id 索引**的,卡池批量改名之后如果这里不同步,全部效果会静默失效(踩过)。
///
/// 【挂载 & 调整】
///   挂在:不是组件。BattlefieldManager 出牌时与 CardEffectResolver 结算时访问,
///         第一次访问时惰性构建,构建完只读。它不碰任何场景物体。
///   常调:
///     · Build() 里那张逐卡登记表:策划案改了一张卡的效果,改这里对应那一行即可(见每条后面的卡面原文)。
///     · 卡面替换:登记项里的 displayText 会覆盖卡面上显示的效果文案;
///       不填就沿用 CardData.effectText(数据表里那份),所以两边会有一点点措辞差异 —— 数值以这里为准。
///     · ParseFromText():兜底解析的规则。想让它多认几种写法就在这里加一条正则。
/// </summary>
public static class CardEffectDatabase
{
    private static Dictionary<string, CardEffectSet> table;

    /// <summary>拿某张卡的效果。没登记的兵牌返回空表,没登记的策略卡会走文案兜底解析</summary>
    public static CardEffectSet Get(CardData card)
    {
        if (card == null) return new CardEffectSet();
        EnsureBuilt();

        if (table.TryGetValue(card.cardId ?? "", out var set)) return set;

        // 策略卡漏登记:文案兜底,让这张牌至少不算白打
        if (card.cardType == CardType.Tactic)
        {
            var fallback = ParseFromText(card);
            table[card.cardId ?? ""] = fallback;
            Debug.LogWarning($"[CardEffect] 策略卡「{card.cardName}」({card.cardId})没在 CardEffectDatabase 里登记," +
                             $"已按效果文案兜底解析出 {fallback.effects.Count} 条效果。建议补登记。");
            return fallback;
        }

        var empty = new CardEffectSet();
        table[card.cardId ?? ""] = empty;
        return empty;
    }

    /// <summary>这张卡的效果文案(登记表给了就用登记的,否则用卡面上的)</summary>
    public static string DisplayText(CardData card)
    {
        var set = Get(card);
        return !string.IsNullOrEmpty(set.displayText) ? set.displayText : (card != null ? card.effectText : "");
    }

    /// <summary>这张卡有没有需要玩家指定「第二个目标」的效果(多目标策略卡)</summary>
    public static bool HasExtraTarget(CardData card)
    {
        var set = Get(card);
        for (int i = 0; i < set.effects.Count; i++)
            if (set.effects[i].targetSlot != CardEffectTargetSlot.Primary) return true;
        return false;
    }

    private static void EnsureBuilt()
    {
        if (table != null) return;
        table = new Dictionary<string, CardEffectSet>();
        Build(table);
    }

    /// <summary>
    /// 对账:把「登记表」和「当前卡池」比一遍。**专治"效果静默失效"** ——
    /// 登记表是按 cardId 建的,卡池一改 id(本项目就批量改过一次),所有 key 全部落空,
    /// 表现是"卡还能打、飘字也说结算了,但什么都没发生",而且一句警告都没有。
    ///
    /// 由 BattlefieldManager 开局调一次(那时卡池一定已经加载好了)。查两件事:
    ///   · 表里登记了、卡池里没有的 id —— 说明这张卡改过名/id,那条效果永远用不上(LogError);
    ///   · 策略卡没登记 —— 会掉进文案兜底,兜不出东西就等于白打(LogWarning)。
    /// </summary>
    public static void Validate()
    {
        EnsureBuilt();

        var library = CardLibrary.Current;
        if (library == null || library.Count == 0) return;

        var known = new HashSet<string>();
        for (int i = 0; i < library.Count; i++)
            if (library.cards[i] != null && !string.IsNullOrEmpty(library.cards[i].cardId))
                known.Add(library.cards[i].cardId);

        var dangling = new List<string>();
        foreach (var kv in table)
            if (!known.Contains(kv.Key)) dangling.Add($"{kv.Key}「{kv.Value.displayText}」");

        var missing = new List<string>();
        for (int i = 0; i < library.Count; i++)
        {
            var c = library.cards[i];
            if (c == null || c.cardType != CardType.Tactic) continue;
            if (!table.ContainsKey(c.cardId ?? "")) missing.Add($"{c.cardId}「{c.cardName}」");
        }

        if (dangling.Count > 0)
            Debug.LogError($"[CardEffect] 效果登记表里有 {dangling.Count} 条登记的 cardId 在卡池里找不到 —— " +
                           $"这些卡的效果永远不会生效(卡池改过 id,登记表没跟着改?):\n  · " +
                           string.Join("\n  · ", dangling));

        if (missing.Count > 0)
            Debug.LogWarning($"[CardEffect] 有 {missing.Count} 张策略卡没登记效果,只能靠文案兜底解析 —— " +
                             $"兜不出来的话打出去就是白付费用:\n  · " + string.Join("\n  · ", missing));
    }

    // ================================================================ 登记表

    /// <summary>
    /// 33 张策略卡 + 7 张兵牌的「部署后」效果,共 40 条,按**当前 cardId** 逐条写清。
    /// 兵牌其余能力靠关键词(见 §4.3),不在这里登记。
    ///
    /// ⚠ 这张表是效果的**唯一来源**,同一个 cardId 只写一条(以前有过"先写旧值、后面再覆盖"的写法,
    ///   一旦有人调整顺序,效果就悄悄变了)。
    /// ⚠ cardId 跟着《数据表demo.xlsx》走,卡池改名/重建之后这里必须同步,否则整表落空 —— 
    ///   开局 CardEffectDatabase.Validate() 会对账并报错,别忽略那条 LogError。
    /// </summary>
    private static void Build(Dictionary<string, CardEffectSet> t)
    {
        // ---------------------------------------------------------- 2026-10-31 卡池重建：策略卡效果重做
        // 说明：效果以《数据表demo.xlsx·卡池总表》的「效果」栏为准；**这张表是唯一来源**，同 id 只写一条；
        //       兵牌的「部署后」效果为描述类特殊效果，不计入卡牌面板/效果等价。

        // 秦·策略
        t["qin_t_01"] = One("对敌方随机单位造成3点伤害", CardEffect.Damage(3, CardEffectScope.Random));
        t["qin_t_02"] = One("为建筑回复3hp", CardEffect.Heal(3, CardEffectScope.Auto));
        t["qin_t_03"] = One("限制敌方单位一回合行动", CardEffect.Suppress(1));
        t["qin_t_04"] = One("指定一个友方单位HP+2", CardEffect.Buff(0, 2));
        t["qin_t_05"] = One("对指定一排单位造成2点伤害", CardEffect.Damage(2, CardEffectScope.Row));
        t["qin_t_06"] = One("弃置敌方一张手牌", CardEffect.Discard(1));
        t["qin_t_07"] = One("若敌方单位数多于己方，对敌方随机单位造成3点伤害",
            CardEffect.Damage(3, CardEffectScope.Random).When(CardEffectCondition.EnemyUnitsMore));
        t["qin_t_08"] = One("抽取2张牌", CardEffect.Draw(2));
        t["qin_t_09"] = One("将对方任意单位的atk调整到1", CardEffect.SetAtk(1));
        t["qin_t_10"] = One("使一个友方单位获得「血战」", CardEffect.GrantKeyword(Keyword.BloodBattle));
        t["qin_t_11"] = One("费用上限+1", CardEffect.RaiseCpMax(1));
        t["qin_t_12"] = One("抽取3张牌", CardEffect.Draw(3));

        // 汉·策略
        t["han_t_01"] = One("将对方任意单位的atk调整到1", CardEffect.SetAtk(1));
        t["han_t_02"] = One("限制敌方单位一回合行动", CardEffect.Suppress(1));
        t["han_t_03"] = One("抽取2张牌", CardEffect.Draw(2));
        t["han_t_04"] = One("若己方大营HP≤10，为建筑回复5hp",
            CardEffect.Heal(5, CardEffectScope.Auto).When(CardEffectCondition.CampHpAtMost, 10));
        t["han_t_05"] = One("弃置敌方一张手牌", CardEffect.Discard(1));
        t["han_t_06"] = One("为兵回复3hp", CardEffect.Heal(3, CardEffectScope.Auto));
        t["han_t_07"] = One("对指定单位造成3点伤害", CardEffect.Damage(3));
        t["han_t_08"] = One("若敌方手牌≥4，弃置敌方2张手牌",
            CardEffect.Discard(2).When(CardEffectCondition.EnemyHandAtLeast, 4));
        t["han_t_09"] = One("使一个友方单位获得「守护」", CardEffect.GrantKeyword(Keyword.Guard));
        t["han_t_10"] = One("对指定一排单位造成2点伤害", CardEffect.Damage(2, CardEffectScope.Row));
        t["han_t_11"] = One("费用上限+1", CardEffect.RaiseCpMax(1));
        t["han_t_12"] = One("费用上限+2", CardEffect.RaiseCpMax(2));

        // 通用·策略
        t["gen_t_01"] = One("对指定一排单位造成2点伤害", CardEffect.Damage(2, CardEffectScope.Row));
        t["gen_t_02"] = One("对指定两排单位各造成2点伤害", CardEffect.Damage(2, CardEffectScope.TwoRows));
        t["gen_t_03"] = One("若敌方单位数≥3，限制敌方单位一回合行动",
            CardEffect.Suppress(1).When(CardEffectCondition.EnemyUnitsAtLeast, 3));
        t["gen_t_04"] = One("对敌方攻击力最高的单位造成3点伤害", CardEffect.Damage(3, CardEffectScope.StrongestEnemy));
        t["gen_t_05"] = One("为建筑回复7hp", CardEffect.Heal(7, CardEffectScope.Auto));
        t["gen_t_06"] = One("使一个友方单位获得「重甲1」", CardEffect.GrantKeyword(Keyword.HeavyArmor));
        t["gen_t_07"] = One("抽取2张牌", CardEffect.Draw(2));
        t["gen_t_08"] = One("费用上限+1", CardEffect.RaiseCpMax(1));
        t["gen_t_09"] = One("指定敌方两个单位互相攻击一次", CardEffect.TradeBlows());

        // 兵牌·部署后（描述类特殊效果，不计入面板/效果等价）
        t["qin_u_04"] = One("部署后：对一个敌方单位造成2点伤害", CardEffect.Damage(2, CardEffectScope.Random));
        t["han_u_04"] = One("部署后：为一个友方单位HP+2", CardEffect.Buff(0, 2));
        t["qin_u_17"] = One("部署后：对敌方随机单位造成2点伤害", CardEffect.Damage(2, CardEffectScope.Random));
        t["han_u_14"] = One("部署后：抽1张牌", CardEffect.Draw(1));
        t["gen_u_03"] = One("部署后：为一个友方单位HP+2", CardEffect.Buff(0, 2));

        // 汉家大黄弩 / 蹶张神弩:§4.3 召唤(牌堆)—— 加一张**自己**进牌堆。
        // ⚠ summonCardId 必须是当前 cardId:牌堆查卡是拿 id 查的,写旧 id 会静默失败。
        t["han_u_17"] = One("部署时，将一张同名卡加入牌堆", CardEffect.Summon("han_u_17", 1));
        t["qin_u_16"] = One("部署时，将一张同名卡加入牌堆", CardEffect.Summon("qin_u_16", 1));
    }

    /// <summary>一行登记：卡面文案 + 一条效果</summary>
    private static CardEffectSet One(string text, CardEffect effect)
        => new CardEffectSet { displayText = text }.Add(effect);

    // ================================================================ 文案兜底解析

    /// <summary>
    /// 按效果文案猜结构。只在登记表漏了这张策略卡时走 —— 目的是「不至于白付 CP」,不追求精确。
    /// 认得出的写法:抽N张 / 造成N点伤害 / 回复N(点)HP / ATK+N / HP+N / ATK调整到N /
    /// 弃置N张手牌 / 限制…回合行动 / 费用上限±N / 获得「词条」/ 互相攻击 / 消灭。
    ///
    /// ⚠ 数值写法要宽松:数据表里既有「回复 7 点 HP」也有「为建筑回复3hp」(没空格、没"点"、小写)——
    ///   早期只认第一种,第二种一个都匹配不到,于是"兜底"等于没兜。
    /// </summary>
    private static CardEffectSet ParseFromText(CardData card)
    {
        var set = new CardEffectSet();
        string text = card != null ? card.effectText : "";
        if (string.IsNullOrEmpty(text)) return set;

        // 抽牌:抽取1张牌 / 摸牌3
        var draw = Regex.Match(text, @"(?:抽取|摸牌?)\s*(\d+)\s*张牌");
        if (draw.Success) set.Add(CardEffect.Draw(int.Parse(draw.Groups[1].Value)));

        // 伤害:对…造成N点伤害
        var dmg = Regex.Match(text, @"造成\s*(\d+)\s*点伤害");
        if (dmg.Success)
        {
            int amount = int.Parse(dmg.Groups[1].Value);
            bool random = text.Contains("随机");
            bool twoRows = text.Contains("两排");
            bool wholeRow = text.Contains("一排") || text.Contains("整排");
            set.Add(CardEffect.Damage(amount,
                random ? CardEffectScope.Random
                : twoRows ? CardEffectScope.TwoRows
                : wholeRow ? CardEffectScope.Row
                : CardEffectScope.Target));
        }

        // 回血:为…（建筑）回复N点HP / 为一个友方单位回复N点HP / 为建筑回复3hp
        // 回血只有一种 kind,靠 scope 区分目标:「一座己方建筑」这类没指定目标的写法 → Auto,由结算器自动挑。
        var heal = Regex.Match(text, @"回复\s*(\d+)\s*(?:点)?\s*HP", RegexOptions.IgnoreCase);
        if (heal.Success)
        {
            int amount = int.Parse(heal.Groups[1].Value);
            // 「回复」前面那一小段决定加在谁身上
            int idx = text.IndexOf("回复", System.StringComparison.Ordinal);
            string before = idx > 0 ? text.Substring(0, idx) : text;
            bool noTarget = before.Contains("一座") || before.Contains("随机");
            set.Add(CardEffect.Heal(amount, noTarget ? CardEffectScope.Auto : CardEffectScope.Target));
        }

        // buff:ATK+N、HP+N。卡面写「本回合」的只扛 1 轮,其余一律按持续无限回合算
        var atk = Regex.Match(text, @"ATK\s*\+\s*(\d+)", RegexOptions.IgnoreCase);
        var hp = Regex.Match(text, @"HP\s*\+\s*(\d+)", RegexOptions.IgnoreCase);
        if (atk.Success || hp.Success)
        {
            set.Add(CardEffect.Buff(
                atk.Success ? int.Parse(atk.Groups[1].Value) : 0,
                hp.Success ? int.Parse(hp.Groups[1].Value) : 0,
                CardEffectScope.Target,
                CardEffectTargetSlot.Primary,
                text.Contains("本回合") ? 1 : 0));
        }

        // 弃置手牌(「一张」这种中文数字也要认)
        var discard = Regex.Match(text, @"弃置敌方\s*([0-9一二两三四五])\s*张手牌");
        if (discard.Success) set.Add(CardEffect.Discard(ParseCount(discard.Groups[1].Value)));

        // 压制:限制…一回合行动
        if (text.Contains("限制") && text.Contains("回合行动")) set.Add(CardEffect.Suppress(1));

        // 费用上限+N / -N
        var cpMax = Regex.Match(text, @"费用上限\s*([+\-]\s*\d+)");
        if (cpMax.Success)
            set.Add(CardEffect.RaiseCpMax(int.Parse(cpMax.Groups[1].Value.Replace(" ", ""))));

        // ATK 调整到 N(等价类:远交近攻 / 招降)
        var setAtk = Regex.Match(text, @"ATK\s*(?:值)?\s*(?:调整|设置|调|改)(?:到|为)\s*(\d+)", RegexOptions.IgnoreCase);
        if (setAtk.Success) set.Add(CardEffect.SetAtk(int.Parse(setAtk.Groups[1].Value)));

        // 获得「词条」
        var kw = Regex.Match(text, @"获得\s*[「""']([^」""']+)[」""']");
        if (kw.Success && KeywordByName.TryGetValue(kw.Groups[1].Value, out var keyword))
            set.Add(CardEffect.GrantKeyword(keyword));

        // 两敌互击(借刀杀人)
        if (text.Contains("互相攻击")) set.Add(CardEffect.TradeBlows());

        // 消灭
        if (text.Contains("消灭"))
            set.Add(CardEffect.Destroy(text.Contains("随机") ? CardEffectScope.Random : CardEffectScope.Target));

        return set;
    }

    /// <summary>「一 / 两 / 二 / 3」这类写法统一成数字(只覆盖卡面会用到的 1~5)</summary>
    private static int ParseCount(string s) => s switch
    {
        "一" => 1, "二" => 2, "两" => 2, "三" => 3, "四" => 4, "五" => 5,
        _ => int.TryParse(s, out int n) ? n : 1,
    };

    /// <summary>文案兜底用的词条名映射(卡面写中文,枚举是英文)</summary>
    private static readonly Dictionary<string, Keyword> KeywordByName = new()
    {
        { "闪击", Keyword.Blitz },
        { "伏兵", Keyword.Ambush },
        { "守护", Keyword.Guard },
        { "血战", Keyword.BloodBattle },
        { "连战", Keyword.DoubleStrike },
        { "重甲", Keyword.HeavyArmor },
        { "重甲1", Keyword.HeavyArmor },
        { "重甲2", Keyword.HeavyArmor },
        { "重甲3", Keyword.HeavyArmor },
        { "抵抗", Keyword.Resist },
    };
}
