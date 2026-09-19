using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 策略卡效果结算 + 兵牌的部署增益(策划案§7.2.1 第 3 步「效果结算」)。
///
/// 出牌流程里它插在「扣费/弃牌」之前:BattlefieldManager 先问 CanResolve(这张牌现在打出去有没有用),
/// 再 Resolve 一次把效果全部结算完,然后才扣 CP、才把牌退场 —— 这样不会出现「费扣了、牌没了、效果空放」。
///
/// 一张卡的效果表来自 CardEffectDatabase(按 cardId 登记);多目标策略卡(决水灌城 / 盐铁论)的
/// 第 2、3 个目标 CardData 里没字段可放,由这里按「自动挑一个最合适的」补齐。
///
/// 【挂载 & 调整】
///   挂在:不是组件,不用挂。
///   常调:
///     · Caster 类:出牌方的四个入口(抽牌 / 弃牌 / 手牌 / 牌堆数)按 isLocal 走我方或敌方的账,
///       以后要接"AI 也能抽牌"之类的新效果,只要在 Caster 里加方法,效果分支不用改。
///     · Resolve 里的 switch:每一条对应一种 CardEffectKind,想加新效果就在这里加一个 case,
///       同时在 CardEffectDatabase 里登记哪张卡用它。
///     · PickAutoTarget / PickBestRow:多目标卡与支援卡「自动挑目标」的偏好。想换成另一种挑法只改这两个方法。
/// </summary>
public static class CardEffectResolver
{
    /// <summary>
    /// 出牌方(抽牌、弃牌、手牌、牌堆都从这里走)。isLocal 区分我方账还是敌方账。
    /// </summary>
    public class Caster
    {
        public readonly BattleSide side;

        public Caster(BattleSide side) { this.side = side; }

        public bool IsLocal => side == BattleSide.Player;
        public BattleSide EnemySide => side == BattleSide.Player ? BattleSide.Enemy : BattleSide.Player;

        private DeckController LocalDeck => Object.FindObjectOfType<DeckController>();
        private EnemyDeckController EnemyDeckCtrl => EnemyDeckController.Instance;

        /// <summary>抽 N 张(牌堆空时逐张走疲劳,§5.4.1)</summary>
        public void DrawCards(int count)
        {
            if (count <= 0) return;

            if (IsLocal)
            {
                var deck = LocalDeck;
                if (deck != null) deck.DrawCards(count);
                else Debug.LogWarning("[效果] 找不到我方 DeckController,抽牌没生效。");
            }
            else
            {
                var deck = EnemyDeckCtrl;
                if (deck != null) deck.DrawCards(count);
                else Debug.LogWarning("[效果] 找不到敌方 EnemyDeckController,抽牌没生效。");
            }
        }

        /// <summary>牌堆里还有几张(「召唤(手牌)」这类效果要判)</summary>
        public int DeckCount => IsLocal
            ? (LocalDeck != null ? LocalDeck.DeckCount : 0)
            : (EnemyDeckCtrl != null ? EnemyDeckCtrl.DeckCount : 0);

        /// <summary>往自己牌堆里加一张牌(§4.3 召唤(牌堆))。返回牌堆新张数</summary>
        public int AddToDeck(CardData card, int count = 1)
        {
            if (card == null || count <= 0) return DeckCount;

            if (IsLocal)
            {
                var deck = LocalDeck;
                if (deck == null) return 0;
                for (int i = 0; i < count; i++) deck.AddToDeck(card);
                deck.RefreshDeckCount();
                Debug.Log($"[效果] 我方牌堆加入「{card.cardName}」×{count},现有 {deck.DeckCount} 张");
                return deck.DeckCount;
            }

            var enemy = EnemyDeckCtrl;
            if (enemy == null) return 0;
            for (int i = 0; i < count; i++) enemy.AddCardToDeck(card);
            Debug.Log($"[效果] 敌方牌堆加入「{card.cardName}」×{count},现有 {enemy.DeckCount} 张");
            return enemy.DeckCount;
        }

        /// <summary>手牌张数</summary>
        public int HandCount => IsLocal
            ? (LocalDeck != null ? LocalDeck.HandCount : 0)
            : (EnemyDeckCtrl != null ? EnemyDeckCtrl.HandCount : 0);

        /// <summary>随机弃掉自己 count 张手牌,返回弃掉几张(被弃的牌直接退场,§5.4)</summary>
        public int DiscardRandom(int count)
        {
            if (count <= 0) return 0;

            int discarded = 0;
            for (int i = 0; i < count; i++)
            {
                if (IsLocal)
                {
                    var deck = LocalDeck;
                    var hand = deck != null ? deck.HandCards : null;
                    if (hand == null || hand.Count == 0) break;
                    if (!deck.DiscardRandom()) break;
                    discarded++;
                }
                else
                {
                    var deck = EnemyDeckCtrl;
                    var hand = deck != null ? deck.HandCards : null;
                    if (hand == null || hand.Count == 0) break;
                    if (!deck.DiscardRandom()) break;
                    discarded++;
                }
            }

            return discarded;
        }

        /// <summary>从卡池里按 cardId 找一张牌(召唤类效果用:优先手牌里那张原卡,找不到就借场上任意一张同 id 的卡面)</summary>
        public CardData FindCard(string cardId)
        {
            if (string.IsNullOrEmpty(cardId)) return null;

            var hand = IsLocal
                ? (LocalDeck != null ? LocalDeck.HandCards : null)
                : (EnemyDeckCtrl != null ? EnemyDeckCtrl.HandCards : null);

            if (hand != null)
                for (int i = 0; i < hand.Count; i++)
                    if (hand[i] != null && hand[i].cardId == cardId) return hand[i];

            // 场上找一张同 id 的(汉家大黄弩自己就在场上)
            var units = BattleSettlement.AllUnitsSnapshot();
            for (int i = 0; i < units.Count; i++)
                if (units[i] != null && units[i].Data != null && units[i].Data.cardId == cardId) return units[i].Data;

            return null;
        }
    }

    // ================================================================ 能不能结算

    /// <summary>
    /// 这张牌现在打出去有没有用。false 时 reason 说明为什么(牌不会被打掉,CP 也不扣)。
    /// 判的是「至少有一条效果能落到实处」,不是「每条都能」—— 例如「抽1张牌 + 造成3点伤害」
    /// 在对面空场时仍然可以打(抽牌那条有效)。
    /// </summary>
    public static bool CanResolve(CardData card, Caster caster, FieldUnit primaryUnit, BattleRow primaryRow, out string reason)
    {
        reason = null;
        if (card == null || caster == null) { reason = "这张牌没有卡面数据"; return false; }

        var set = CardEffectDatabase.Get(card);
        if (set.IsEmpty) return true;      // 没登记效果(理论上不会走到)

        bool anyViable = false;
        bool needsPrimary = false;
        bool primaryOk = false;

        for (int i = 0; i < set.effects.Count; i++)
        {
            var effect = set.effects[i];
            bool isPrimaryEffect = effect.targetSlot == CardEffectTargetSlot.Primary;

            switch (effect.kind)
            {
                case CardEffectKind.Damage:
                    if (effect.scope == CardEffectScope.Random || effect.scope == CardEffectScope.AllEnemy)
                    {
                        if (HasEnemyUnit(caster)) anyViable = true;
                    }
                    else if (effect.scope == CardEffectScope.Row)
                    {
                        if (effect.targetSlot == CardEffectTargetSlot.Primary)
                        {
                            needsPrimary = true;
                            if (primaryRow != null) primaryOk = true;
                        }
                        else if (HasEnemyUnit(caster)) anyViable = true;
                    }
                    else
                    {
                        needsPrimary = true;
                        if (primaryUnit != null && primaryUnit.Side != caster.side) primaryOk = true;
                    }
                    break;

                case CardEffectKind.Buff:
                    if (effect.scope == CardEffectScope.Auto || effect.scope != CardEffectScope.Target)
                    {
                        if (HasAllyUnit(caster)) anyViable = true;
                    }
                    else
                    {
                        needsPrimary = true;
                        if (primaryUnit != null && primaryUnit.Side == caster.side) primaryOk = true;
                    }
                    break;

                case CardEffectKind.DrawCards:
                    anyViable = true;      // 牌堆空了也是抽牌 → 走疲劳,照样算"打得出去"
                    break;

                case CardEffectKind.Heal:
                    // 回血只有一种 kind,目标是谁由玩家指定 / scope 决定,所以这里只判「场上有没有能回的东西」。
                    // 具体回建筑还是回兵牌在 ApplyHeal 里按目标类型分流。
                    if (HasRepairableBuilding(caster) || HasHealableUnit(caster)
                        || BattleSettlement.FindCamp(caster.side) != null)
                        anyViable = true;
                    break;

                case CardEffectKind.DiscardHand:
                    // 弃置敌方手牌:目标是敌方手牌区,给不给目标都行,只要对面有手牌
                    if (OpponentHandCount(caster) > 0) anyViable = true;
                    break;

                case CardEffectKind.Restrict:
                    if (HasEnemyUnit(caster)) anyViable = true;
                    break;

                case CardEffectKind.Summon:
                    anyViable = true;
                    break;
            }
        }

        if (anyViable) return true;

        if (needsPrimary && !primaryOk)
        {
            reason = card.RequiresTarget
                ? $"没有可以指定的目标：{BattleRules.TargetTypeName(card.targetType)}"
                : "没有合适的目标";
            return false;
        }

        reason = "现在没有能落地的效果";
        return false;
    }

    // ================================================================ 结算

    /// <summary>
    /// 把这张卡的效果全部结算一遍。返回一句可以飘出来的结果说明。
    /// 出牌流程要在扣费之前调它 —— 返回 false 就说明这张牌不该被打出去。
    /// </summary>
    public static bool Resolve(CardData card, Caster caster, FieldUnit primaryUnit, BattleRow primaryRow, out string message)
    {
        message = null;
        if (card == null || caster == null) return false;
        if (!CanResolve(card, caster, primaryUnit, primaryRow, out message)) return false;

        var set = CardEffectDatabase.Get(card);
        var parts = new List<string>();

        for (int i = 0; i < set.effects.Count; i++)
        {
            var effect = set.effects[i];
            string part = ApplyEffect(effect, caster, primaryUnit, primaryRow);
            if (!string.IsNullOrEmpty(part)) parts.Add(part);
        }

        message = parts.Count > 0
            ? $"「{card.cardName}」：{string.Join("；", parts)}"
            : $"「{card.cardName}」已结算";

        Debug.Log($"[效果] {message}");
        return true;
    }

    /// <summary>结算一条效果,返回这句效果的可读结果(null = 没落到实处)</summary>
    private static string ApplyEffect(CardEffect effect, Caster caster, FieldUnit primaryUnit, BattleRow primaryRow)
    {
        switch (effect.kind)
        {
            case CardEffectKind.Damage:       return ApplyDamage(effect, caster, primaryUnit, primaryRow);
            case CardEffectKind.Buff:         return ApplyBuff(effect, caster, primaryUnit);
            case CardEffectKind.DrawCards:    return ApplyDraw(effect, caster);
            case CardEffectKind.Heal:         return ApplyHeal(effect, caster, primaryUnit);
            case CardEffectKind.DiscardHand:  return ApplyDiscard(effect, caster);
            case CardEffectKind.Restrict:     return ApplySuppress(effect, caster, primaryUnit);
            case CardEffectKind.Summon:       return ApplySummon(effect, caster);
            default:                          return null;
        }
    }

    // ---------------------------------------------------------------- 伤害

    private static string ApplyDamage(CardEffect effect, Caster caster, FieldUnit primaryUnit, BattleRow primaryRow)
    {
        switch (effect.scope)
        {
            case CardEffectScope.Random:
            {
                var target = PickRandomEnemyUnit(caster);
                if (target == null) return null;
                int dealt = BattleSettlement.DealSpellDamage(target, effect.amount);
                CheckDeath(target);
                if (caster.side == BattleSide.Player) BattleSettlement.AddPlayerDamage(dealt);
                return $"对随机目标「{target.DisplayName}」造成 {dealt} 点伤害";
            }

            case CardEffectScope.AllEnemy:
            {
                var victims = UnitsOf(caster.EnemySide);
                int total = 0;
                var names = new List<string>();
                for (int i = 0; i < victims.Count; i++)
                {
                    var v = victims[i];
                    if (v == null || !v.IsUnit || !v.IsAlive) continue;
                    total += BattleSettlement.DealSpellDamage(v, effect.amount);
                    names.Add(v.DisplayName);
                    CheckDeath(v);
                }
                if (names.Count == 0) return null;
                if (caster.side == BattleSide.Player) BattleSettlement.AddPlayerDamage(total);
                return $"对 {names.Count} 个敌方单位共造成 {total} 点伤害";
            }

            case CardEffectScope.Row:
            {
                var row = effect.targetSlot == CardEffectTargetSlot.Primary ? primaryRow : PickBestEnemyRow(caster);
                if (row == null) return null;

                var members = BattleRules.Snapshot(row);
                int total = 0, hits = 0;
                for (int i = 0; i < members.Count; i++)
                {
                    var m = members[i];
                    if (m == null || !m.IsAlive) continue;

                    // 以排为目标的范围效果不选中具体单位,无视守护(§4.3),建筑同样吃伤害
                    total += BattleSettlement.DealSpellDamage(m, effect.amount);
                    hits++;
                    CheckDeath(m);
                }
                if (hits == 0) return null;
                if (caster.side == BattleSide.Player) BattleSettlement.AddPlayerDamage(total);
                return $"对{row.DisplayName}整排造成 {effect.amount} 点伤害（{hits} 个目标）";
            }

            default:
            {
                // 单体伤害:优先用玩家指定的那个目标;没有就自动挑一个
                var target = primaryUnit;
                if (target == null || target.Side == caster.side || !target.IsAlive) target = PickBestEnemyTarget(caster);
                if (target == null) return null;

                int dealt = BattleSettlement.DealSpellDamage(target, effect.amount);
                CheckDeath(target);
                if (caster.side == BattleSide.Player) BattleSettlement.AddPlayerDamage(dealt);
                return $"对「{target.DisplayName}」造成 {dealt} 点伤害";
            }
        }
    }

    // ---------------------------------------------------------------- 增益

    private static string ApplyBuff(CardEffect effect, Caster caster, FieldUnit primaryUnit)
    {
        var target = primaryUnit;
        if (target == null || target.Side != caster.side || !target.IsUnit) target = PickAutoTarget(caster, ally: true);

        if (target == null) return null;

        target.AddTempBuff(effect.atkValue, effect.hpValue, effect.durationRounds, BattleSettlement.CurrentRound);

        string stats = effect.atkValue != 0 && effect.hpValue != 0
            ? $"ATK+{effect.atkValue}、HP+{effect.hpValue}"
            : effect.atkValue != 0 ? $"ATK+{effect.atkValue}" : $"HP+{effect.hpValue}";

        // durationRounds ≤ 0 = 持续无限回合(一次性永久增幅),别再说成「本回合」
        string span = effect.durationRounds <= 0 ? "" : "本回合 ";
        return caster.side == BattleSide.Player
            ? $"「{target.DisplayName}」{span}{stats}"
            : $"对方「{target.DisplayName}」{span}{stats}";
    }

    // ---------------------------------------------------------------- 抽牌 / 召唤 / 弃牌

    private static string ApplyDraw(CardEffect effect, Caster caster)
    {
        int before = caster.HandCount;
        caster.DrawCards(effect.amount);
        int gained = caster.HandCount - before;

        if (caster.side == BattleSide.Player)
            return gained > 0 ? $"抽了 {gained} 张牌" : $"抽 {effect.amount} 张牌（牌堆已空，触发疲劳）";

        return $"对方抽了 {gained} 张牌";
    }

    private static string ApplySummon(CardEffect effect, Caster caster)
    {
        var card = caster.FindCard(effect.summonCardId);
        if (card == null)
        {
            Debug.LogWarning($"[效果] 召唤失败:找不到 cardId = {effect.summonCardId} 的卡面。");
            return null;
        }

        int count = caster.AddToDeck(card, effect.summonCount);
        return $"召唤「{card.cardName}」×{effect.summonCount} 进入牌堆（现 {count} 张）";
    }

    private static string ApplyDiscard(CardEffect effect, Caster caster)
    {
        int handBefore = OpponentHandCount(caster);
        if (handBefore == 0) return null;

        int discarded = OpponentDiscard(caster, effect.amount);
        if (discarded <= 0) return null;

        return caster.side == BattleSide.Player
            ? $"弃置敌方 {discarded} 张手牌"
            : $"敌方弃置了我方 {discarded} 张手牌";
    }

    private static string ApplySuppress(CardEffect effect, Caster caster, FieldUnit primaryUnit)
    {
        var target = primaryUnit;
        if (target == null || target.Side == caster.side || !target.IsUnit) target = PickBestEnemyUnitToSuppress(caster);

        if (target == null) return null;

        target.ApplySuppress(effect.amount);
        return caster.side == BattleSide.Player
            ? $"「{target.DisplayName}」被压制 {effect.amount} 回合"
            : $"我方「{target.DisplayName}」被压制 {effect.amount} 回合";
    }

    // ---------------------------------------------------------------- 回血 / 维修

    /// <summary>
    /// 回血只有这一条:回建筑(维修,§7.3)/ 回兵牌 / 回大营的区别**落在目标是谁上**,不靠 kind 分家。
    ///
    /// 目标的决定顺序:
    ///   1. 玩家指定的那个目标(primaryUnit),只要它确实是友方、且还活着 —— 拖到己方建筑上就是修建筑,拖到兵牌上就是回兵牌;
    ///   2. scope == Caster(「为一座己方建筑回复 7 点 HP」这种无目标写法)/ 第 1 个目标不可用时:
    ///      按目标类型自动挑 —— 建筑优先挑最该修的(PickRepairableBuilding),兵牌挑最该回的(PickAutoTarget);
    ///   3. 都没有就退到大营。
    /// 「被摧毁的建筑」只有维修卡修得回来(对齐卡面血量 + 1 回合无敌,§7.3)。
    /// </summary>
    private static string ApplyHeal(CardEffect effect, Caster caster, FieldUnit primaryUnit)
    {
        FieldUnit target = primaryUnit;

        // 玩家指定的目标:必须是友方、还活着。建筑被摧毁时 IsAlive 为假,所以这里额外放行"友方建筑"
        bool primaryUsable = target != null && target.Side == caster.side
                             && (target.IsAlive || (target.IsBuilding && !target.IsCamp));

        if (!primaryUsable)
        {
            // 自动挑:先建筑(维修优先,含被摧毁的),再兵牌,最后大营
            target = PickRepairableBuilding(caster);
            if (target == null) target = PickAutoTarget(caster, ally: true);
            if (target == null) target = BattleSettlement.FindCamp(caster.side);
        }

        if (target == null) return null;

        // §7.3 维修卡:除了回血,还给建筑 1 回合无敌,防止同一回合反复用低费修缮卡刷建筑。
        // 判据 = 「这张牌的第一目标本来就是己方建筑」—— 兵牌效果里没有修自己建筑的写法。
        bool isRepairCard = primaryUnit != null && primaryUnit.IsBuilding && primaryUnit.Side == caster.side;

        if (target.IsBuilding)
        {
            // 已经被摧毁的建筑:维修卡把它「修回来」并对齐到卡面血量,顺带给无敌
            if (!target.IsAlive)
            {
                int restore = Mathf.Max(effect.amount, target.MaxHp);
                target.ClearWrecked();
                target.SetHp(restore);
                target.GrantInvincible(1);
                target.ShowNumber(restore, healing: true, 0, target.Hp);
                return $"修复了被摧毁的「{target.BuildingName}」（{restore} HP，1 回合无敌）";
            }

            int repaired = BattleSettlement.Heal(target, effect.amount);
            if (isRepairCard) target.GrantInvincible(1);

            if (repaired <= 0 && !isRepairCard) return null;
            return repaired > 0
                ? $"为「{target.BuildingName}」回复 {repaired} 点 HP{(isRepairCard ? "，并获得 1 回合无敌" : "")}"
                : $"「{target.BuildingName}」已满血，获得 1 回合无敌";
        }

        // 兵牌 / 大营:直接回血
        int healed = BattleSettlement.Heal(target, effect.amount);
        if (healed <= 0) return null;

        // 回血还被「守护」挡着吗?不 —— 治疗是友方效果,守护只挡选中敌方目标(§4.3)
        if (target.IsCamp) return $"大营回复 {healed} 点 HP";

        return caster.side == BattleSide.Player
            ? $"「{target.DisplayName}」回复 {healed} 点 HP"
            : $"对方为「{target.DisplayName}」回复 {healed} 点 HP";
    }

    // ================================================================ 兵牌部署时(支援卡增益 / 召唤)

    /// <summary>
    /// 兵牌部署落地后调一次:把卡上登记的效果结算掉(§4.3 支援卡的部署增益、汉家大黄弩的召唤)。
    /// 需要指定友方目标的增益由这里自动挑一个最合适的,并飘字说明加在谁身上 ——
    /// 「部署时手动选目标」的交互还没做,自动挑是暂时的口径。
    /// </summary>
    public static string ResolveOnDeploy(CardData card, Caster caster, FieldUnit deployed)
    {
        var set = CardEffectDatabase.Get(card);
        if (set.IsEmpty) return null;

        var parts = new List<string>();
        for (int i = 0; i < set.effects.Count; i++)
        {
            var effect = set.effects[i];

            // 部署增益只作用在友方单位上,不指向"这张牌自己";召唤类照常结算
            string part = ApplyEffect(effect, caster, deployed, deployed != null ? deployed.Row : null);
            if (!string.IsNullOrEmpty(part)) parts.Add(part);
        }

        if (parts.Count == 0) return null;

        string message = $"「{card.cardName}」部署效果：{string.Join("；", parts)}";
        Debug.Log($"[效果] {message}");
        return message;
    }

    // ================================================================ 挑目标

    /// <summary>自动挑一个最合适的友方单位(支援卡增益、自动回血的第 2/3 目标):优先给能打到人的、血少的</summary>
    public static FieldUnit PickAutoTarget(Caster caster, bool ally)
    {
        var units = UnitsOf(ally ? caster.side : caster.EnemySide);

        FieldUnit best = null;
        int bestScore = int.MinValue;

        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit == null || !unit.IsUnit || !unit.IsAlive) continue;

            // 越靠前、ATK 越高的单位越值得加;已经残血的也稍微加点分(让它活下来)
            int score = unit.Atk * 10 + (unit.MaxHp - unit.Hp) * 2 - ForwardGap(unit) * 3;
            if (score > bestScore) { bestScore = score; best = unit; }
        }

        return best;
    }

    /// <summary>自动挑一个最该修的建筑(§7.3):血最少、且优先"已经被摧毁的"</summary>
    public static FieldUnit PickRepairableBuilding(Caster caster)
    {
        var buildings = BuildingsOf(caster.side);
        FieldUnit best = null;
        int bestScore = int.MinValue;

        for (int i = 0; i < buildings.Count; i++)
        {
            var b = buildings[i];
            if (b == null || !b.IsBuilding) continue;

            int missing = b.MaxHp - b.Hp;
            if (missing <= 0 && b.IsAlive) continue;         // 满血的先不考虑

            int score = missing * 10 + (b.IsCamp ? 5 : 0) + (!b.IsAlive ? 100 : 0);
            if (score > bestScore) { bestScore = score; best = b; }
        }

        return best;
    }

    /// <summary>随机一个敌方兵牌(卡面写「随机单位」的走这条;建筑不算)</summary>
    public static FieldUnit PickRandomEnemyUnit(Caster caster)
    {
        var units = UnitsOf(caster.EnemySide);
        var pool = new List<FieldUnit>();
        for (int i = 0; i < units.Count; i++)
            if (units[i] != null && units[i].IsUnit && units[i].IsAlive) pool.Add(units[i]);

        if (pool.Count == 0) return null;
        return pool[Random.Range(0, pool.Count)];
    }

    /// <summary>挑一个最值得打的敌方目标(单体伤害没用玩家指定时的兜底)</summary>
    public static FieldUnit PickBestEnemyTarget(Caster caster)
    {
        var units = UnitsOf(caster.EnemySide);
        FieldUnit best = null;
        int bestScore = int.MinValue;

        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit == null || !unit.IsAlive) continue;

            // 能一下打死的优先;血少的优先;兵牌优先于建筑;被守护的先跳过
            if (BattleRules.IsGuarded(unit, out _)) continue;

            int score = (unit.Hp <= 3 ? 500 : 0) + unit.Atk * 10 + (100 - unit.Hp * 5) + (unit.IsUnit ? 30 : 0);
            if (score > bestScore) { bestScore = score; best = unit; }
        }

        return best;
    }

    /// <summary>挑一个最该压制的敌方兵牌(ATK 最高的那个,§4.3 压制)</summary>
    public static FieldUnit PickBestEnemyUnitToSuppress(Caster caster)
    {
        var units = UnitsOf(caster.EnemySide);
        FieldUnit best = null;
        int bestScore = int.MinValue;

        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit == null || !unit.IsUnit || !unit.IsAlive) continue;

            int score = unit.Atk * 10 + unit.Hp * 2;
            if (score > bestScore) { bestScore = score; best = unit; }
        }

        return best;
    }

    /// <summary>自动挑一条敌方排(决水灌城那类「对指定一排造成伤害」用):里面站的人最多、ATK 最高</summary>
    public static BattleRow PickBestEnemyRow(Caster caster)
    {
        var board = BattleSettlement.Board;
        if (board == null) return null;

        BattleRow best = null;
        int bestScore = int.MinValue;

        var rows = board.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            // 自己的后军/中军跳过。注意共享前军的 row.Side 恒为 Player,这里不能用 row.Side 判敌我,
            // 真正的判据是"这条排里站着多少个敌方成员"(下面逐个成员数)。
            if (row == null || (row.Side == caster.side && row.RowType != BattleRowType.Front)) continue;

            int score = 0;
            var members = row.Units;
            for (int j = 0; j < members.Count; j++)
            {
                var m = members[j];
                if (m == null || !m.IsAlive) continue;
                if (m.Side == caster.side) continue;      // 前军里混着自己人,别算进去
                score += 10 + m.Atk + m.Hp;
            }

            if (score > bestScore) { bestScore = score; best = row; }
        }

        return bestScore > 0 ? best : null;
    }

    // ================================================================ 小工具

    /// <summary>
    /// 某一方场上的全部成员(兵牌 + 建筑)。
    ///
    /// 按**单位自己的 Side**筛,不按 row.Side:前军是双方共享的那一条排(row.Side 恒为 Player),
    /// 按 row.Side 会漏掉站在前军里的敌方单位 —— AI 就找不到攻击目标、策略卡也点不到它们。
    /// </summary>
    public static List<FieldUnit> UnitsOf(BattleSide side)
    {
        var list = new List<FieldUnit>();
        var board = BattleSettlement.Board;
        if (board == null) return list;

        var rows = board.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var members = BattleRules.Snapshot(rows[i]);
            for (int m = 0; m < members.Count; m++)
            {
                var unit = members[m];
                if (unit != null && unit.Side == side) list.Add(unit);
            }
        }
        return list;
    }

    private static List<FieldUnit> BuildingsOf(BattleSide side)
    {
        var all = UnitsOf(side);
        var list = new List<FieldUnit>();
        for (int i = 0; i < all.Count; i++)
            if (all[i] != null && all[i].IsBuilding) list.Add(all[i]);
        return list;
    }

    private static bool HasEnemyUnit(Caster caster)
    {
        var units = UnitsOf(caster.EnemySide);
        for (int i = 0; i < units.Count; i++)
            if (units[i] != null && units[i].IsUnit && units[i].IsAlive) return true;
        return false;
    }

    private static bool HasAllyUnit(Caster caster)
    {
        var units = UnitsOf(caster.side);
        for (int i = 0; i < units.Count; i++)
            if (units[i] != null && units[i].IsUnit && units[i].IsAlive) return true;
        return false;
    }

    private static bool HasHealableUnit(Caster caster)
    {
        var units = UnitsOf(caster.side);
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (u != null && u.IsUnit && u.IsAlive && u.Hp < u.MaxHp) return true;
        }
        return false;
    }

    private static bool HasRepairableBuilding(Caster caster)
    {
        var buildings = BuildingsOf(caster.side);
        for (int i = 0; i < buildings.Count; i++)
        {
            var b = buildings[i];
            if (b != null && (b.Hp < b.MaxHp || !b.IsAlive)) return true;
        }
        return false;
    }

    private static int OpponentHandCount(Caster caster) => new Caster(caster.EnemySide).HandCount;

    private static int OpponentDiscard(Caster caster, int count) => new Caster(caster.EnemySide).DiscardRandom(count);

    /// <summary>单位离敌方大营还有几排(越小越靠前,自动挑目标时给靠前的加分)</summary>
    private static int ForwardGap(FieldUnit unit)
    {
        if (unit == null || unit.Row == null) return 0;

        int own = BattleRules.RowRank(unit.Side, unit.Row.RowType);
        var enemySide = unit.Side == BattleSide.Player ? BattleSide.Enemy : BattleSide.Player;
        int enemyCamp = BattleRules.RowRank(enemySide, BattleRowType.Mid);
        return Mathf.Abs(enemyCamp - own);
    }

    /// <summary>伤害结算之后如果目标死了,立刻走死亡流程(不在战斗结算里的那次攻击也算)</summary>
    private static void CheckDeath(FieldUnit unit)
    {
        if (unit != null && !unit.IsAlive) BattleSettlement.KillUnit(unit);
    }
}
