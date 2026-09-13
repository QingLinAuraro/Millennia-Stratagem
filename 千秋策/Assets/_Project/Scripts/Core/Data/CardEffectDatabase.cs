using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// 全卡池的效果登记表(策划案§7.2.1 第 3 步「效果结算」的数据来源)。
///
/// 为什么要单独一张表,而不是新的 ScriptableObject 字段:
///   · 卡牌 .asset 是 Tools/build_card_assets.py 从《数据表demo.xlsx》生成的,字段与 CardData.cs 一一对应;
///     加一个「结构化效果」字段就要动 xlsx 的列和生成脚本,而策划案现在只给了效果文案(「效果」列)。
///   · 效果文案本身是中文长句,不适合当场正则硬解 —— 但**必须有个兜底**,
///     不然表里漏登记一张,那张策略卡打出去就是白付 CP。
///   所以这里做两层:
///     1. 显式登记(48 张卡按 cardId 逐张写清,见下面的 Build())—— 正常走这条,改效果改这里;
///     2. 文案解析兜底(ParseFromText)—— 只在表里没登记的策略卡上触发,并打一条警告,
///        保证新加进卡池的卡至少能做「造成 N 点伤害 / 抽 N 张牌 / 回复 N 点 HP」这几件事。
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

    // ================================================================ 登记表

    /// <summary>
    /// 48 张卡的效果。兵牌只登记「部署时」生效的那几条(其余效果靠关键词,见 §4.3);
    /// 策略卡按卡面文案逐条登记。
    /// </summary>
    private static void Build(Dictionary<string, CardEffectSet> t)
    {
        // ---------------------------------------------------------- 秦·兵牌
        // 连弩台:部署当回合,指定一个友方单位本回合 HP+3(§4.3 支援卡示例)
        t["qin_005"] = new CardEffectSet { displayText = "部署当回合，指定一个友方单位本回合 HP+3" }
            .Add(CardEffect.Buff(0, 3, CardEffectScope.Target, CardEffectTargetSlot.Primary));

        // ---------------------------------------------------------- 秦·策略牌
        // 弩矢督:对敌方随机单位造成3点伤害
        t["qin_007"] = new CardEffectSet { displayText = "对敌方随机单位造成3点伤害" }
            .Add(CardEffect.Damage(3, CardEffectScope.Random));

        // 攒射:对一名敌方兵牌造成3点伤害
        t["qin_008"] = new CardEffectSet { displayText = "对一名敌方兵牌造成3点伤害" }
            .Add(CardEffect.Damage(3));

        // 军功爵:指定一个友方单位本回合ATK+2、HP+3；抽取1张牌
        t["qin_015"] = new CardEffectSet { displayText = "指定一个友方单位本回合 ATK+2、HP+3；抽取 1 张牌" }
            .Add(CardEffect.Buff(2, 3))
            .Add(CardEffect.Draw(1));

        // 反间:弃置敌方1张手牌
        t["qin_016"] = new CardEffectSet { displayText = "弃置敌方 1 张手牌" }
            .Add(CardEffect.Discard(1));

        // 绝粮道:抽取1张牌；对敌方随机单位造成3点伤害
        t["qin_018"] = new CardEffectSet { displayText = "抽取 1 张牌；对敌方随机单位造成 3 点伤害" }
            .Add(CardEffect.Draw(1))
            .Add(CardEffect.Damage(3, CardEffectScope.Random));

        // 商君变法:抽取1张牌；为一座己方建筑回复7点HP
        t["qin_020"] = new CardEffectSet { displayText = "抽取 1 张牌；为一座己方建筑回复 7 点 HP" }
            .Add(CardEffect.Draw(1))
            .Add(CardEffect.Repair(7));

        // 移民实边:抽取3张牌
        t["qin_021"] = new CardEffectSet { displayText = "抽取 3 张牌" }
            .Add(CardEffect.Draw(3));

        // ---------------------------------------------------------- 汉·兵牌
        // 治粟都尉:部署当回合,指定一个友方单位本回合 HP+3
        t["han_005"] = new CardEffectSet { displayText = "部署当回合，指定一个友方单位本回合 HP+3" }
            .Add(CardEffect.Buff(0, 3, CardEffectScope.Target, CardEffectTargetSlot.Primary));

        // 汉家大黄弩:召唤(牌堆)—— §4.3「添加一张卡牌进入牌堆」,这里加一张自己进牌堆
        t["han_024"] = new CardEffectSet { displayText = "召唤（牌堆）：将一张「汉家大黄弩」加入己方牌堆" }
            .Add(new CardEffect { kind = CardEffectKind.Summon, summonCardId = "han_024", summonCount = 1 });

        // ---------------------------------------------------------- 汉·策略牌
        // 招降:对敌方随机单位造成3点伤害
        t["han_007"] = new CardEffectSet { displayText = "对敌方随机单位造成3点伤害" }
            .Add(CardEffect.Damage(3, CardEffectScope.Random));

        // 屯田:为一座己方建筑回复5点HP；抽取1张牌
        t["han_008"] = new CardEffectSet { displayText = "为一座己方建筑回复 5 点 HP；抽取 1 张牌" }
            .Add(CardEffect.Repair(5))
            .Add(CardEffect.Draw(1));

        // 破敌封赏:指定一个友方单位本回合ATK+2、HP+3；抽取1张牌
        t["han_015"] = new CardEffectSet { displayText = "指定一个友方单位本回合 ATK+2、HP+3；抽取 1 张牌" }
            .Add(CardEffect.Buff(2, 3))
            .Add(CardEffect.Draw(1));

        // 离间:抽取1张牌；对敌方随机单位造成3点伤害
        t["han_016"] = new CardEffectSet { displayText = "抽取 1 张牌；对敌方随机单位造成 3 点伤害" }
            .Add(CardEffect.Draw(1))
            .Add(CardEffect.Damage(3, CardEffectScope.Random));

        // 决水灌城:指定一个友方单位本回合HP+2；对指定一排单位造成2点伤害
        // 第二个目标(打哪一排)CardData 里没字段放,由结算器自动挑「敌方单位最多的那一排」。
        t["han_018"] = new CardEffectSet { displayText = "指定一个友方单位本回合 HP+2；对敌方一排单位造成 2 点伤害" }
            .Add(CardEffect.Buff(0, 2))
            .Add(CardEffect.Damage(2, CardEffectScope.Row, CardEffectTargetSlot.Secondary));

        // 盐铁论:限制敌方一个单位一回合行动；为一座己方建筑回复5点HP；为一个友方单位回复3点HP
        // 第二个目标(修哪座建筑)与第三个目标(给谁回血)同样由结算器自动挑。
        t["han_021"] = new CardEffectSet { displayText = "限制敌方一个单位一回合行动；为一座己方建筑回复 5 点 HP；为一个友方单位回复 3 点 HP" }
            .Add(CardEffect.Suppress(1))
            .Add(CardEffect.AutoRepair(5))
            .Add(CardEffect.AutoHealUnit(3));
    }

    // ================================================================ 文案兜底解析

    /// <summary>
    /// 按效果文案猜结构。只在登记表漏了这张策略卡时走 —— 目的是「不至于白付 CP」,不追求精确。
    /// 认得出的写法:「对…造成N点伤害」「随机单位」「抽取N张牌」「摸N张牌」「回复N点HP」「弃置…手牌」「限制…N回合」。
    /// </summary>
    private static CardEffectSet ParseFromText(CardData card)
    {
        var set = new CardEffectSet();
        string text = card != null ? card.effectText : "";
        if (string.IsNullOrEmpty(text)) return set;

        // 抽牌:抽取1张牌 / 摸牌3
        var draw = Regex.Match(text, @"(?:抽取|摸)(\d+)张牌");
        if (draw.Success) set.Add(CardEffect.Draw(int.Parse(draw.Groups[1].Value)));

        // 伤害:对…造成N点伤害
        var dmg = Regex.Match(text, @"造成(\d+)点伤害");
        if (dmg.Success)
        {
            int amount = int.Parse(dmg.Groups[1].Value);
            bool random = text.Contains("随机");
            bool wholeRow = text.Contains("一排") || text.Contains("整排");
            set.Add(CardEffect.Damage(amount,
                random ? CardEffectScope.Random : wholeRow ? CardEffectScope.Row : CardEffectScope.Target));
        }

        // 回血:为…（建筑）回复N点HP / 为一个友方单位回复N点HP
        var heal = Regex.Match(text, @"回复(\d+)点HP");
        if (heal.Success)
        {
            int amount = int.Parse(heal.Groups[1].Value);
            // 「回复」前面那一小段决定加在谁身上
            int idx = text.IndexOf("回复", System.StringComparison.Ordinal);
            string before = idx > 0 ? text.Substring(0, idx) : text;
            if (before.Contains("建筑")) set.Add(CardEffect.Repair(amount));
            else set.Add(CardEffect.HealUnitFor(amount));
        }

        // buff:ATK+N、HP+N(只认「本回合」那一套写法)
        var atk = Regex.Match(text, @"ATK\+(\d+)");
        var hp = Regex.Match(text, @"HP\+(\d+)");
        if (atk.Success || hp.Success)
        {
            set.Add(CardEffect.Buff(
                atk.Success ? int.Parse(atk.Groups[1].Value) : 0,
                hp.Success ? int.Parse(hp.Groups[1].Value) : 0));
        }

        // 弃置手牌
        var discard = Regex.Match(text, @"弃置敌方(\d+)张手牌");
        if (discard.Success) set.Add(CardEffect.Discard(int.Parse(discard.Groups[1].Value)));

        // 压制:限制…一回合行动
        if (text.Contains("限制") && text.Contains("回合行动")) set.Add(CardEffect.Suppress(1));

        return set;
    }
}
