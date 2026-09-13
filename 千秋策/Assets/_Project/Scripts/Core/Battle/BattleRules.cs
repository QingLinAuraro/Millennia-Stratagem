using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 出牌规则 + 战斗规则。全部照策划案抄下来,不碰任何 UI 和场景:
///
/// · §2.3 五排容量制:后军 3 = 军械库 + 粮草 + 至多 1 个单位;中军 5 = 大营 + 4 个单位;
///   共享前军 4,无建筑。容量是该排唯一的硬限制。
/// · §8.1.1 单位放置:单位卡默认只能进己方中军(单位容量未满);
///   只有「中军单位容量已满、且中军内驻有敌方袭扰骑兵」时才允许改部署进己方后军。
/// · §7.2.2 策略卡目标:伤害类/削弱类只能指敌方, buff 类只能指友方,
///   维修类只能指己方建筑, 抽卡过牌与随机目标类无目标, 弃置类指敌方手牌区。
/// · §2.6 射程:步/骑 1 排,弓/器 2 排(兵种自带,不计入词条)。
/// · §7.1.1 攻击:只能打前方的排,排距 ≤ 射程,目标必须活着,被守护覆盖的目标不可选。
/// · §2.6 反击:近战↔近战、远程↔远程才反击;近战↔远程交叉攻击不反击。
/// · §4.3 守护:守护单位存活时,同排相邻链上左右紧邻的友方单位与建筑不能被选中;守护单位之间不互保。
///
/// 结算(扣血、死亡、Debuff)在 BattleSettlement;这里只有"能不能"的判定。
/// </summary>
public static class BattleRules
{
    public const int MidUnitCapacity = 4;     // §2.3:中军 5 容量 - 大营 1
    public const int BackUnitCapacity = 1;    // §2.3:后军 3 容量 - 军械库 - 粮草
    public const int FrontUnitCapacity = 4;   // §2.3:共享前军,无建筑

    /// <summary>三座建筑的名字(和 BattlefieldManager 的默认建筑表、BattleRow 的查询一致)</summary>
    public const string CampName = "大营";
    public const string ArsenalName = "军械库";
    public const string GranaryName = "粮草";

    // ================================================================ 排的顺序与方向(§2.3)

    /// <summary>
    /// 这条排**按 side 自己的视角**排在前面第几位:数字越大越靠前。
    /// 后军 = 0、中军 = 1、共享前军 = 2、对面中军 = 3、对面后军 = 4。
    ///
    /// ⚠ 这是**按视角编号的相对坐标,不是棋盘的绝对位置** —— 同一个共享前军,
    /// 在双方视角里都是"第 2 位"(对两边来说它都紧挨着自己的中军、前面才是对面中军)。
    /// 正因如此,**两个不同阵营的排做减法时必须各用各的视角**:
    /// `RowRank(我方) - RowRank(敌方)` 才有意义,那正好是排距。
    ///
    /// ⚠ 曾经的写法是"绝对编号":共享前军恒为 2,于是 **敌方中军 = 3 > 2** ——
    /// 从敌方视角看,前军跑到他中军**后面**去了。后果是 `ForwardDistance(敌方中军, 共享前军) = -1`,
    /// 被 `CanAttack` 判成"目标在身后",于是**我方兵站在前军时,敌方近战永远打不到**,
    /// 日志表现是"满费用但不攻击"+"超出射程 32 条"(32 = 全场上所有"敌方 → 我方前军"的组合)。
    ///
    /// ⚠ 别用"序号 +1"去推"往前一步是哪条排" —— 见 TryForwardRow,那里是明文查表。
    /// </summary>
    public static int RowRank(BattleSide side, BattleRowType type)
    {
        // 先算"这一档在前面第几排",三档对双方都是同一个名字意义上的同一格
        // (Back = 谁的后军就是谁的后军,Front = 那条共享前军)
        int forwardSteps = type switch
        {
            BattleRowType.Back => 0,
            BattleRowType.Mid => 1,
            BattleRowType.Front => 2,
            _ => -1,
        };
        if (forwardSteps < 0) return -1;

        // 己方视角:后军 0 → 中军 1 → 共享前军 2 → 敌方中军 3 → 敌方后军 4
        if (side == BattleSide.Player) return forwardSteps;

        // 敌方视角是镜像的:敌方后军 0 → 敌方中军 1 → 共享前军 2 → 我方中军 3 → 我方后军 4
        // 后军/中军这两档在两边名字相同但排不同,所以要先翻过来再算。
        int ownBackFirst = type == BattleRowType.Back ? 0
                         : type == BattleRowType.Mid ? 1
                         : -1;
        if (ownBackFirst >= 0) return ownBackFirst;      // 敌方自己的后军/中军

        return forwardSteps;                              // 前军 2 / 我方中军 3 / 我方后军 4
    }

    /// <summary>
    /// 从 unit 所在的 from 排**往前走一步**会到哪条排(排距 1)。到头了(已经在对方后军)返回 false。
    ///
    /// ⚠ 方向必须按**单位自己的阵营**算,不能按 `from.Side` ——
    /// 共享前军在全场只有一条(物理上就是 playerFront),它的 `row.Side` **恒为 Player**。
    /// 拿 from.Side 推方向的后果:站在敌方中军往前走会算成"敌方后军",
    /// 于是"敌方中军 → 共享前军"这一步合法前进被判成"向后走"(实测日志就是这个)。
    ///
    /// ⚠ 这里刻意**不用 RowOrderIndex 加减一**去推:`Mid` 在己方是序号 1、在敌方是 3,
    /// 而排类型不带阵营,隔着共享前军做加减极容易把"前进"算成"后退"(这个坑踩了两次)。
    /// 直接查表 —— 每一步都是明文写出来的,没有算术可以出错。
    /// </summary>
    public static bool TryForwardRow(FieldUnit unit, BattleRow from, out BattleSide side, out BattleRowType type)
    {
        // 失败时按原样返回,调用方照着打日志就能看见"我以为在哪、实际在哪"
        side = unit != null ? unit.Side : (from != null ? from.Side : BattleSide.Player);
        type = from != null ? from.RowType : BattleRowType.Mid;
        if (from == null || unit == null) return false;

        switch (from.RowType)
        {
            // 自己后军 → 自己中军 → 共享前军 → 对面中军 → 对面后军
            case BattleRowType.Back:
                side = unit.Side;
                type = BattleRowType.Mid;
                return true;

            case BattleRowType.Mid:
                // 中军既可能是自己的(还没上前军),也可能是对面的(袭扰骑兵站在那儿)。
                // 前者是**前进**,后者是**向后撤** —— 必须分清楚,否则"从敌方中军往前走"
                // 会被当成合法,直接把人送回自己脸上。
                // 判据是"这条排属于谁":Mid 排是实打实属于某一方的,可以放心用 from.Side
                // (只有共享前军不能这么用,它的 Side 恒为 Player)。
                if (from.Side != unit.Side) return false;      // 站在对面中军:没有"再往前走"这回事

                side = BattleSide.Player;      // 共享前军物理上就是 playerFront
                type = BattleRowType.Front;
                return true;

            case BattleRowType.Front:
                // 站在共享前军上,再往前就是**对面的中军**(我方单位 → 敌方中军;敌方单位 → 我方中军)
                side = unit.Side == BattleSide.Player ? BattleSide.Enemy : BattleSide.Player;
                type = BattleRowType.Mid;
                return true;

            default:
                return false;
        }
    }

    /// <summary>某一方的中军(大营所在的那条排)。场上还没摆好时返回 null</summary>
    public static BattleRow MidRowOf(BattleSide side)
    {
        var board = BattlefieldManager.Instance;
        if (board == null) return null;
        return side == BattleSide.Player ? board.PlayerMid : board.EnemyMid;
    }

    /// <summary>当前回合数(没有 TurnController 时按 1 算)。判定"刚进来的回合"用</summary>
    public static int CurrentRound()
    {
        var turn = TurnController.Instance;
        return turn != null ? Mathf.Max(1, turn.RoundNumber) : 1;
    }

    /// <summary>
    /// 前方排距:目标排在攻击者"前面"几排。负数 = 在身后(不能打)。
    /// 共享前军对双方都是**前方相邻排**(§7.1.1)—— 它在双方视角里都是"第 2 位"。
    ///
    /// ⚠ 两个排各用各的视角(attacker.Row 用 attacker.Side、target.Row 用 target.Side)。
    /// 不能拿同一套"绝对编号":那样共享前军(2)会落在敌方中军(3)后面,
    /// 我方兵站前军时敌方近战就永远打不着了。
    /// </summary>
    public static int ForwardDistance(BattleRow attacker, BattleRow target)
    {
        if (attacker == null || target == null) return -99;

        int a = RowRank(attacker.Side, attacker.RowType);
        int b = RowRank(target.Side, target.RowType);
        if (a < 0 || b < 0) return -99;

        return b - a;
    }

    // ================================================================ 兵种:射程 / 兵种固有值(§2.5 / §2.6)

    /// <summary>兵种自带射程:步/骑 1 排,弓/器(支援)2 排</summary>
    public static int RangeOf(UnitType type)
        => type == UnitType.Archer || type == UnitType.Support ? 2 : 1;

    public static int RangeOf(CardData card) => card == null ? 1 : RangeOf(card.unitType);

    /// <summary>近战(步/骑)还是远程(弓/器)。反击只看这一档(§2.6)</summary>
    public static CombatClass ClassOf(UnitType type)
        => type == UnitType.Archer || type == UnitType.Support ? CombatClass.Ranged : CombatClass.Melee;

    public static bool IsMelee(CardData card) => card != null && ClassOf(card.unitType) == CombatClass.Melee;
    public static bool IsRanged(CardData card) => card != null && ClassOf(card.unitType) == CombatClass.Ranged;

    /// <summary>单位的射程(建筑 0,不能攻击)</summary>
    public static int RangeOf(FieldUnit unit)
        => unit == null || unit.IsBuilding || unit.Data == null ? 0 : RangeOf(unit.Data);

    /// <summary>单位属于近战还是远程(建筑按近战算,反正建筑不能行动)</summary>
    public static CombatClass ClassOf(FieldUnit unit)
        => unit == null || unit.Data == null ? CombatClass.Melee : ClassOf(unit.Data.unitType);

    /// <summary>同类互攻才反击(§2.6)</summary>
    public static bool Counters(FieldUnit attacker, FieldUnit target)
        => attacker != null && target != null && !attacker.IsBuilding && !target.IsBuilding
           && ClassOf(attacker) == ClassOf(target);

    /// <summary>行动次数(AP 上限):骑兵 2、带「连战」的 2、其余 1(§2.5)</summary>
    public static int ActionPointsOf(CardData card)
    {
        if (card == null || card.cardType != CardType.Unit || card.unitType == UnitType.Strategy) return 0;
        if (card.unitType == UnitType.Cavalry) return 2;
        if (HasKeyword(card, Keyword.DoubleStrike)) return 2;
        return 1;
    }

    /// <summary>行动费用:每次移动/攻击额外扣的 CP(§2.5)。策略卡返回 0</summary>
    public static int ActionCostOf(CardData card)
        => card == null || card.cardType != CardType.Unit ? 0 : Mathf.Max(0, card.actionCost);

    /// <summary>卡面上带不带这个关键词</summary>
    public static bool HasKeyword(CardData card, Keyword keyword)
        => card != null && card.keywords != null && card.keywords.Contains(keyword);

    /// <summary>
    /// 卡面上某个关键词出现了几次。用来还原「重甲1 / 重甲2 / 重甲3」这类档位 ——
    /// CardData 的 Keyword 枚举只有一个 HeavyArmor 档位,层数只能靠数据表里重复登记(§4.3 基础表 B42:B44)。
    /// </summary>
    public static int KeywordCount(CardData card, Keyword keyword)
    {
        if (card == null || card.keywords == null) return 0;

        int n = 0;
        for (int i = 0; i < card.keywords.Count; i++)
            if (card.keywords[i] == keyword) n++;
        return n;
    }

    /// <summary>重甲层数(§4.3:每层伤害 -1)。枚举去重之后至少 1 层</summary>
    public static int HeavyArmorLayers(CardData card)
    {
        int n = KeywordCount(card, Keyword.HeavyArmor);
        return n > 0 ? n : (HasKeyword(card, Keyword.HeavyArmor) ? 1 : 0);
    }

    // ================================================================ 守护(§4.3)

    /// <summary>这个成员带不带「守护」</summary>
    public static bool HasGuard(FieldUnit unit)
        => unit != null && unit.IsUnit && HasKeyword(unit.Data, Keyword.Guard);

    /// <summary>
    /// 目标是不是被同排相邻的守护单位保护着(§4.3)。
    /// 只看排内相邻链上的左右紧邻成员,且守护单位之间不互相保护;守护单位自身始终可被选中。
    /// </summary>
    public static bool IsGuarded(FieldUnit target, out FieldUnit guardian)
    {
        guardian = null;
        if (target == null || target.Row == null) return false;
        if (HasGuard(target)) return false;         // 守护单位之间不互保,它自己也永远可选

        var chain = target.Row.Units;
        int index = target.Row.IndexOf(target);
        if (index < 0) return false;

        // 左邻、右邻各看一个(至多 2 个直接邻居)
        for (int offset = -1; offset <= 1; offset += 2)
        {
            int i = index + offset;
            if (i < 0 || i >= chain.Count) continue;

            var neighbour = chain[i];
            if (neighbour == null || !neighbour.IsAlive) continue;
            if (!HasGuard(neighbour)) continue;

            guardian = neighbour;
            return true;
        }

        return false;
    }

    // ================================================================ 部署(§8.1.1)

    /// <summary>
    /// 兵种牌能落到这条排上吗(§8.1.1)。
    /// carrierMid = **落位方自己的中军**:后军的例外条件要看它满没满、里面有没有敌方袭扰骑兵。
    ///
    /// 注意:这个方法不能假定"落位方一定是玩家" —— 敌方 AI 走的是同一条落位流程(EnemyAI → BattlefieldManager.DeployUnit),
    /// 所以只校验「row 与 carrierMid 属于同一方」,双方各用各的中军判定。
    /// </summary>
    public static bool CanDeployUnit(CardData card, BattleRow row, BattleRow carrierMid, out string reason)
    {
        reason = null;

        if (card == null) { reason = "这张牌没有卡面数据"; return false; }
        if (!card.IsUnitCard) { reason = "只有兵种牌才能部署"; return false; }
        if (row == null) { reason = "只能部署到己方中军"; return false; }
        if (carrierMid != null && carrierMid.Side != row.Side) { reason = "落位排与己方中军不是同一方"; return false; }

        switch (row.RowType)
        {
            case BattleRowType.Mid:
                if (row.IsUnitCapacityFull)
                {
                    reason = "中军已满，无法部署";
                    return false;
                }
                return true;

            case BattleRowType.Back:
                // §8.1.1 例外:中军单位容量已满、且中军内驻有敌方袭扰骑兵时,才允许部署进后军
                bool midFull = carrierMid != null && carrierMid.IsUnitCapacityFull;
                bool hasRaider = carrierMid != null && carrierMid.HasEnemyRaider;
                if (!midFull)
                {
                    reason = "只能部署到中军（后军要中军满员时才能进）";
                    return false;
                }
                if (!hasRaider)
                {
                    reason = "后军要中军里驻有敌方袭扰骑兵时才可部署";
                    return false;
                }
                if (row.IsUnitCapacityFull)
                {
                    reason = "后军已满，无法部署";
                    return false;
                }
                return true;

            default:   // Front
                reason = "前军只能靠移动进入";
                return false;
        }
    }

    // ================================================================ 攻击 / 移动(§7.1.1 / §2.3)

    /// <summary>
    /// 这个兵牌能不能攻击那个目标。理由写进 reason,飘字直接用。
    /// 检查顺序照 §7.1.1:AP → 状态(压制/无敌/袭扰) → 活着 → 阵营 → 方向 → 射程 → 守护。
    /// </summary>
    public static bool CanAttack(FieldUnit attacker, FieldUnit target, out string reason)
        => CanAttack(attacker, target, attacker != null ? attacker.Row : null, out reason);

    /// <summary>
    /// 同上,但假设攻击者站在 fromRow 上(不动场上任何东西)。
    ///
    /// 给 AI 的走位评分用:想知道"挪到前军之后打不打得到大营",不能真把它挪过去 ——
    /// 那会修改战场,评分阶段只能试算。真正的攻击照旧走三参数那版(用单位当前所在的排)。
    /// </summary>
    public static bool CanAttack(FieldUnit attacker, FieldUnit target, BattleRow fromRow, out string reason)
    {
        reason = null;

        if (attacker == null || target == null) { reason = "没有可攻击的目标"; return false; }
        if (attacker.IsBuilding) { reason = "建筑不能攻击"; return false; }
        if (!attacker.IsAlive) { reason = "这个单位已经阵亡"; return false; }
        if (attacker.SuppressTurns > 0) { reason = "这个单位被压制，无法行动"; return false; }
        if (attacker.Ap <= 0) { reason = "行动力已耗尽"; return false; }
        if (attacker.DeployedThisTurn && !HasKeyword(attacker.Data, Keyword.Blitz))
        { reason = "刚部署的单位本回合不能行动（需要「闪击」）"; return false; }

        if (!target.IsAlive) { reason = "目标已经阵亡"; return false; }
        if (attacker.Side == target.Side) { reason = "不能攻击己方单位"; return false; }
        if (target.InvincibleRounds > 0) { reason = "该建筑处于无敌状态，不能被指定"; return false; }

        // §7.4 袭扰骑兵:只能拆敌方后军建筑,不能攻击任何单位
        if (attacker.IsRaiding && !target.IsBuilding)
        { reason = "袭扰中的骑兵只能攻击敌方后军建筑"; return false; }
        if (attacker.IsRaiding && target.Row != null && target.Row.RowType != BattleRowType.Back)
        { reason = "袭扰中的骑兵只能拆敌方后军建筑"; return false; }

        int distance = ForwardDistance(fromRow, target.Row);
        if (distance < 0) { reason = "只能攻击前方的目标"; return false; }

        int range = RangeOf(attacker);
        if (range <= 0) { reason = "这个单位不能攻击"; return false; }
        if (distance > range) { reason = $"目标超出射程（{range} 排）"; return false; }

        if (IsGuarded(target, out var guardian))
        { reason = $"该目标受「{guardian.DisplayName}」守护保护"; return false; }

        return true;
    }

    /// <summary>
    /// 这个兵牌能不能移动到那条排(§2.3:只能进相邻排;§7.4:袭扰骑兵可撤回共享前军)。
    /// sideCarrier = 这次移动**实际会落进去的那条排**(玩家排 → 玩家排,敌方排 → 敌方排)。
    /// </summary>
    public static bool CanMoveTo(FieldUnit unit, BattleRow from, BattleRowType targetType, BattleRow sideCarrier,
                                 out string reason, out bool isRetreat)
    {
        reason = null;
        isRetreat = false;

        if (unit == null || from == null) { reason = "没有可移动的单位"; return false; }
        if (unit.IsBuilding) { reason = "建筑不能移动"; return false; }
        if (!unit.IsAlive) { reason = "这个单位已经阵亡"; return false; }
        if (unit.SuppressTurns > 0) { reason = "这个单位被压制，无法行动"; return false; }
        if (unit.Ap <= 0) { reason = "行动力已耗尽"; return false; }
        if (unit.DeployedThisTurn && !HasKeyword(unit.Data, Keyword.Blitz))
        { reason = "刚部署的单位本回合不能行动（需要「闪击」）"; return false; }

        // 已经在目标排上 = 没移动。不算合法,也不该收行动力与 CP —— 判进来的话玩家会为"原地不动"付一次费
        if (sideCarrier != null && sideCarrier == from) { reason = "该单位已经在这条排上"; return false; }

        // ---- §7.4 唯一的向后移动:袭扰骑兵从敌方中军**撤回**共享前军 ----
        // 四个条件缺一不可,每一条都是踩过的坑:
        //   ① 目标必须是共享前军;
        //   ② 它必须真的站在敌方中军 —— 否则"从敌方中军再往前"也会被当成撤回放过去;
        //   ③ **不能是刚进来的那个回合** —— 袭扰的代价(下回合 ATK+1、自损 2)还没结算过,
        //      此时放它回去等于白嫖一次进出,骑兵会在前军和敌方中军之间来回弹;
        //   ④ 前军没被敌方占着(前军只容得下一方的单位,撤回同样受这条限制)。
        //
        // ⚠ 这一段必须**排在下面的"方向闸门"之前** —— 它是唯一允许反向移动的分支,
        //   方向闸门会把所有反向移动一律拒掉。顺序写反 = 袭扰骑兵再也撤不回来。
        if (unit.IsRaiding)
        {
            var ownMid = MidRowOf(unit.Side);
            if (targetType != BattleRowType.Front || ownMid == null || from != ownMid)
            { reason = "袭扰中的骑兵只能撤回共享前军"; return false; }

            int round = CurrentRound();
            if (unit.RaidStartedRound > 0 && round <= unit.RaidStartedRound)
            {
                reason = "刚冲进敌方中军的骑兵本回合不能撤回（袭扰的消耗下回合才结算）";
                return false;
            }

            if (!OnlySideOccupies(sideCarrier, unit.Side))
            { reason = $"{sideCarrier.DisplayName}被敌方占着，撤不回去"; return false; }

            isRetreat = true;
            return true;
        }

        // ---- 方向闸门:除上面的袭扰撤回,任何单位都只能进"往前一步"的那条排 ----
        //
        // 判据只有这一个:落点排类型 == TryForwardRow(unit, from) 指出的那一条。
        // 别拿"落点排的 RowOrderIndex 比当前位置大"来判 —— RowOrderIndex 是**按某一方的视角**编号的
        // (己方中军 = 1、敌方中军 = 3),而 targetType(Mid/Back)不带阵营:敌方单位从自己的中军
        // (序号 3)回共享前军时,它的 Mid 会被算成序号 2 < 3,于是**合法的一步会被判成"向后走"**,
        // 表现就是"玩家前军还有兵的时候 AI 不攻击、反而一直试着移动然后被拒"。
        //
        // 这一条同时顶掉了两件事:①不许退回后军;②不许横着/反着走。
        // 「已经在同一条排上」上面已经单独判过,所以这里不用再管 to == from。
        // 注意:站在**敌方中军**的袭扰骑兵上面已经走掉了(unit.IsRaiding 那个分支),
        // 所以能走到这里的、站在中军上的单位一定是站在**自己的中军**上,再往前就是共享前军。
        if (!TryForwardRow(unit, from, out _, out var forwardType) || forwardType != targetType)
        {
            reason = targetType == BattleRowType.Back
                ? "只能向前推进，不能退回己方后军（只有袭扰中的骑兵能撤回前军）"
                : "只能移动到相邻的前方排";
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogWarning($"[规则] 拦下一次非前进的移动:「{unit.DisplayName}」({unit.Side})" +
                             $"在 {from.DisplayName}（{from.RowType}），想走到 {targetType}；" +
                             $"按它自己的阵营,往前走一步是 {forwardType}");
#endif
            return false;
        }

        // 容量判定。前军 → 中军这一步是"进敌方中军袭扰"(§7.4),要额外确认是骑兵、且对面中军还有位置。
        if (targetType == BattleRowType.Mid && from.RowType == BattleRowType.Front)
        {
            if (unit.Data == null || unit.Data.unitType != UnitType.Cavalry)
            { reason = "只有骑兵才能进入敌方中军袭扰"; return false; }

            var enemyMid = sideCarrier;
            if (enemyMid == null || enemyMid.IsUnitCapacityFull)
            { reason = "敌方中军已无空余容量，进不去"; return false; }
        }
        else if (sideCarrier != null && sideCarrier.IsUnitCapacityFull)
        {
            reason = $"{sideCarrier.DisplayName}已满，进不去";
            return false;
        }

        // §2.3 共享前军**只容得下一方的单位**:里面站着敌人的兵就进不去,得先把他们清掉。
        // 这条对双方都一样,所以前军是"抢"来的:谁先站上去谁占着,对面只能隔着打。
        // 注意"前军里还有敌人的兵"和"前军里还有我的兵"是两件事:敌人占着 → 进不去;
        // 我自己已经在前军里 → 上面"同一条排"那条已经拦过了。
        if (targetType == BattleRowType.Front && !OnlySideOccupies(sideCarrier, unit.Side))
        {
            reason = $"{sideCarrier.DisplayName}还驻着敌方单位，要先清掉才能进";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 这条排里的兵牌是不是"只有这一方"(空排也算)。
    /// 共享前军专用:前军只容得下一方的单位,不同方混着站 = 谁也进不来新的。
    /// 建筑不算 —— 建筑不会移动,不会造成"混住"。
    /// </summary>
    public static bool OnlySideOccupies(BattleRow row, BattleSide side)
    {
        if (row == null) return false;

        var members = row.Units;
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (m == null || !m.IsAlive || m.IsBuilding) continue;
            if (m.Side != side) return false;
        }

        return true;
    }

    // ================================================================ 策略卡目标(§7.2.2)

    /// <summary>策略卡能指定这个单位/建筑为目标吗(§7.2.2 + §4.3 守护)</summary>
    public static bool CanTargetUnit(CardData card, FieldUnit target, out string reason)
    {
        reason = null;

        if (card == null) { reason = "这张牌没有卡面数据"; return false; }
        if (target == null) { reason = "这里没有可以指定的目标"; return false; }
        if (!target.IsAlive) { reason = target.IsBuilding ? "这座建筑已经被摧毁" : "目标已经阵亡"; return false; }

        bool ally = target.Side == BattleSide.Player;

        switch (card.targetType)
        {
            case TacticTargetType.EnemyUnit:
                if (ally || target.IsBuilding) { reason = "伤害/削弱类只能指定敌方兵牌"; return false; }
                break;

            case TacticTargetType.AllyUnit:
                if (!ally || target.IsBuilding) { reason = "这张牌只能指定友方兵牌"; return false; }
                break;

            case TacticTargetType.EnemyBuilding:
                if (ally || !target.IsBuilding) { reason = "这张牌只能指定敌方建筑"; return false; }
                break;

            case TacticTargetType.AllyBuilding:
                if (!ally || !target.IsBuilding) { reason = "维修类只能指定己方建筑"; return false; }
                break;

            case TacticTargetType.EnemyRow:
            case TacticTargetType.AllyRow:
                reason = "这张牌要以整排为目标";
                return false;

            case TacticTargetType.EnemyHand:
                reason = "这张牌要拖到敌方手牌区";
                return false;

            default:
                reason = "这张牌不需要指定目标";
                return false;
        }

        // §4.3:被守护覆盖的目标,攻击与策略卡都不可指定(以排为目标的范围效果不受此限,见 CanTargetRow)
        if (IsGuarded(target, out var guardian))
        {
            reason = $"该目标受「{guardian.DisplayName}」守护保护";
            return false;
        }

        // §7.3 维修后的无敌建筑不能被指定
        if (target.InvincibleRounds > 0)
        {
            reason = "该建筑处于无敌状态，不能被指定";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 策略卡能指定这条排为目标吗(§7.2.2:以排为目标的范围效果不选中具体单位,不受守护影响)
    /// </summary>
    public static bool CanTargetRow(CardData card, BattleRow row, out string reason)
    {
        reason = null;

        if (card == null) { reason = "这张牌没有卡面数据"; return false; }
        if (row == null) { reason = "这里没有可以指定的排"; return false; }

        switch (card.targetType)
        {
            case TacticTargetType.EnemyRow:
                if (row.Side != BattleSide.Enemy) { reason = "这张牌只能指定敌方排"; return false; }
                return true;

            case TacticTargetType.AllyRow:
                if (row.Side != BattleSide.Player) { reason = "这张牌只能指定己方排"; return false; }
                return true;

            default:
                reason = "这张牌不能以整排为目标";
                return false;
        }
    }

    /// <summary>目标类型的中文说明(飘字"需要指定目标:xxx"用)</summary>
    public static string TargetTypeName(TacticTargetType type) => type switch
    {
        TacticTargetType.EnemyUnit     => "敌方兵牌",
        TacticTargetType.EnemyBuilding => "敌方建筑",
        TacticTargetType.EnemyRow      => "敌方排",
        TacticTargetType.AllyUnit      => "友方兵牌",
        TacticTargetType.AllyRow       => "己方排",
        TacticTargetType.AllyBuilding  => "己方建筑",
        TacticTargetType.EnemyHand     => "敌方手牌区",
        _                              => "无目标",
    };

    // ================================================================ 小工具

    /// <summary>阵营的中文名</summary>
    public static string SideName(BattleSide side) => side == BattleSide.Player ? "我方" : "敌方";

    /// <summary>把一条排上的成员按链顺序抄一份(遍历时可能有人死,别直接遍历原表)</summary>
    public static List<FieldUnit> Snapshot(BattleRow row)
        => row == null ? new List<FieldUnit>() : new List<FieldUnit>(row.Units);
}
