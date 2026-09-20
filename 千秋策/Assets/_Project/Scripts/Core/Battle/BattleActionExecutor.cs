using UnityEngine;

/// <summary>
/// 移动 / 攻击 / 扣费执行:一次行动的"判定 → 付费 → 落地"全在这里。
///
/// 职责:
///   · MoveUnit           :换排(含袭扰进入 / 撤回,§2.3 / §7.4)
///   · AttackUnit         :攻击(伤害在 BattleSettlement 里算,这里管顺序与回滚)
///   · IsSideAllowedToAct :轮流回合下这一方现在能不能动
///   · SpendAction        :扣 1 AP + 行动费用 CP(§2.5)
///
/// 两条铁律(注释写在方法上,别颠倒):
///   1. 移动:容量判定放在扣 AP **之前** —— 进不去的排不该吃掉行动点。
///   2. 攻击:是「先判 → 先结算 → 再扣费」,扣费失败要把伤害**原样回滚**。
///      反过来写会留下"只扣费用、没有伤害"的坑,真机上极难复现。
///
/// 玩家点击(UnitActionController)与 AI(EnemyAI)都走这一套,所以规则只有一份。
/// </summary>
public class BattleActionExecutor
{
    private readonly BattlefieldManager m;

    public BattleActionExecutor(BattlefieldManager manager)
    {
        m = manager;
    }

    /// <summary>
    /// 单位移动(策划案§2.3:只能进相邻的前方排;§7.4:袭扰骑兵可撤回共享前军)。
    /// 消耗 1 AP + 行动费用 CP。玩家点击与 AI 都走这一条。
    /// </summary>
    public bool MoveUnit(FieldUnit unit, BattleRowType targetType, out string message)
    {
        message = null;
        if (unit == null) { message = "没有要移动的单位"; return false; }
        if (BattleSettlement.MatchOver) { message = "对局已经结束"; return false; }
        if (!IsSideAllowedToAct(unit.Side)) { message = "现在是对方的回合"; return false; }

        var from = unit.Row;

        // 调用方说的是"我要去哪条排"(BattleRowType);方向的合法性由 ResolveMoveRowTo 按
        // 单位当前位置试前进/后退得出,不在这一层自己推方向 —— 共享前军的 row.Side 恒为 Player,
        // 各算一份必然出现"UI 判不合法、AI 却走得过去"。
        var carrier = m.ResolveMoveRowTo(unit, targetType);
        if (carrier == null) { message = "战场上没有这条排"; return false; }

        if (!BattleRules.CanMoveTo(unit, from, targetType, carrier, out message, out bool isRetreat)) return false;

        // 容量判定放在扣 AP 之前:进不去的排不该吃掉行动点
        if (!isRetreat && carrier.IsUnitCapacityFull)
        {
            message = $"{carrier.DisplayName}已满，进不去";
            return false;
        }

        // 顺序说明:和攻击一样,先判合法/容量,再扣费,最后真正换排。
        // 换排只剩"元数据搬运",不会再失败 —— 所以这里不需要攻击那种回滚。
        if (!SpendAction(unit, out message)) return false;

        // 入排:贴到这条排链的末尾(§2.3 无格位模型下,位置只影响「守护」的相邻判定)
        int slot = carrier.Units.Count;
        from.RemoveUnit(unit);
        from.RebuildLayout();

        carrier.InsertSlot(unit, slot);
        carrier.AddUnit(unit, slot);
        unit.SetRow(carrier);

        if (isRetreat)
        {
            unit.ExitRaid();        // §7.4:撤回时袭扰状态与 ATK 加成一并清除
            Debug.Log($"[Battlefield] 「{unit.DisplayName}」撤回 {carrier.DisplayName},袭扰状态解除");
        }
        else if (targetType == BattleRowType.Mid && carrier.Side != unit.Side)
        {
            // §7.4:踏进敌方中军 = 袭扰。把当前回合数记进去,用来判"刚进来的这个回合不许撤回"
            var turn = TurnController.Instance;
            unit.EnterRaid(turn != null ? turn.RoundNumber : 1);
            Debug.Log($"[Battlefield] 骑兵「{unit.DisplayName}」进入 {carrier.DisplayName},进入袭扰状态");
        }

        carrier.RebuildLayout();
        m.RefreshRaidFlags();
        m.RaiseBoardChanged();

        message = isRetreat
            ? $"「{unit.DisplayName}」撤回 {carrier.DisplayName}"
            : $"「{unit.DisplayName}」移动到 {carrier.DisplayName}";
        return true;
    }

    /// <summary>
    /// 单位攻击(§7.1):消耗 1 AP + 行动费用 CP,伤害与反击在 BattleSettlement 里算。
    ///
    /// 顺序是「先判能不能打 → 先结算 → 再扣费」,扣费万一失败就把伤害原样回滚。
    /// 反过来写(先扣费再结算)会留下一个坑:结算一旦被拒,玩家就白付了 AP 和 CP,
    /// 表现成「只扣费用、没有伤害」—— 这个 bug 在真机上很难复现、极难定位,所以宁可在这一层多做一次回滚。
    /// </summary>
    public bool AttackUnit(FieldUnit attacker, FieldUnit target, out string message)
    {
        message = null;
        if (BattleSettlement.MatchOver) { message = "对局已经结束"; return false; }
        if (attacker != null && !IsSideAllowedToAct(attacker.Side)) { message = "现在是对方的回合"; return false; }

        if (!BattleRules.CanAttack(attacker, target, out message)) return false;

        int targetHpBefore = target.Hp;
        int attackerHpBefore = attacker.Hp;

        if (!BattleSettlement.Attack(attacker, target, out message))
        {
            message = message ?? "攻击失败";
            return false;
        }

        if (!SpendAction(attacker, out message))
        {
            // 付不起就当作没打:把两边血量原样还回去(建筑被毁/单位阵亡这种大改还是得重载场景,极罕见)
            target.SetHp(targetHpBefore);
            attacker.SetHp(attackerHpBefore);
            message = $"{message}（本次攻击已回滚）";
            Debug.LogWarning($"[Battlefield] 「{attacker.DisplayName}」攻击「{target.DisplayName}」后扣费失败," +
                             $"已回滚双方血量:{message}");
            return false;
        }

        m.RefreshRaidFlags();
        m.RaiseBoardChanged();
        return true;
    }

    /// <summary>这一方现在能不能行动(轮流回合:只有轮到自己时才能出牌、移动、攻击)</summary>
    public bool IsSideAllowedToAct(BattleSide side)
    {
        var turn = TurnController.Instance;
        if (turn == null || !turn.HasStarted) return true;      // 回合还没开始(纯逻辑测试)时不拦

        return turn.IsLocalTurn == (side == BattleSide.Player);
    }

    /// <summary>
    /// 扣一次行动的代价:1 AP + 该单位的行动费用 CP(§2.5)。任一项不够就整体不生效。
    /// 先检查再扣,所以「检查通过、扣款失败」只可能发生在外部同时改了费用池的情况下。
    /// </summary>
    public bool SpendAction(FieldUnit unit, out string message)
    {
        message = null;
        if (unit == null) { message = "没有可行动的单位"; return false; }
        if (unit.Ap <= 0) { message = "行动力已耗尽"; return false; }

        int cpCost = BattleRules.ActionCostOf(unit.Data);

        if (unit.Side == BattleSide.Player)
        {
            var cp = CommandPointController.Instance;
            if (cp != null && !cp.CanAfford(cpCost)) { message = $"指挥点不足（行动费用 {cpCost}）"; return false; }
        }
        else
        {
            var enemy = EnemyDeckController.Instance;
            if (enemy != null && !enemy.CanAfford(cpCost)) { message = $"敌方指挥点不足（行动费用 {cpCost}）"; return false; }
        }

        if (!unit.SpendActionPoint()) { message = "行动力已耗尽"; return false; }

        if (unit.Side == BattleSide.Player)
        {
            var cp = CommandPointController.Instance;
            if (cp != null && !cp.TrySpend(cpCost)) { message = "指挥点不足"; return false; }
        }
        else
        {
            var enemy = EnemyDeckController.Instance;
            if (enemy != null && !enemy.TrySpend(cpCost)) { message = "敌方指挥点不足"; return false; }
        }

        return true;
    }
}
