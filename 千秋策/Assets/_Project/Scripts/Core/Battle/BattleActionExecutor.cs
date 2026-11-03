using UnityEngine;

/// <summary>
/// 移动 / 攻击 / 扣费执行:一次行动的"判定 → 付费 → 落地"全在这里。
///
/// 职责:
///   · MoveUnit           :换排(含袭扰进入 / 撤回,§2.3 / §7.4)
///   · AttackUnit         :攻击(伤害在 BattleSettlement 里算,这里管判定顺序与付费)
///   · IsSideAllowedToAct :轮流回合下这一方现在能不能动
///   · SpendAction        :扣 1 AP + 行动费用 CP(§2.5)
///
/// 两条铁律(注释写在方法上,别颠倒):
///   1. 移动:容量判定放在扣 AP **之前** —— 进不去的排不该吃掉行动点。
///   2. 攻击/移动:**先把「AP + 行动费用 CP」付掉,再让任何状态发生改变**。
///      付不起 = 这一下根本没发生,不需要回滚。
///
/// ⚠ 第 2 条以前写成"先结算、扣费失败再回滚血量"。那个写法有两个致命后果:
///   · 目标被这一下打死时 `KillUnit` 已经把卡销毁了,回滚只能对着一个已销毁对象写 HP ——
///     卡消失了、伤害还留着,"回滚"名存实亡;
///   · 付款失败是在 `SpendActionPoint` **之前**返回的,所以 AP 也没扣 ——
///     于是"指挥点不足"时玩家可以**0 费 0 AP 无限攻击**。
///   现在付款是结算的前置条件,任何一项不够都到此为止。
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
    ///
    /// 两个入口,区别只在"落点怎么给":
    ///   · BattleRowType 版 —— 调用方只说"我要去中军",落点由 ResolveMoveRowTo 推(键盘、AI 用)。
    ///   · BattleRow     版 —— 调用方直接给"指针底下那条排"(鼠标拖动用)。
    ///     中军有两条(我方 / 敌方),RowType 都是 Mid,只说类型**分不出是哪一条** ——
    ///     拖动必须传具体的排,否则"拖到自己的中军"会被算成"冲进敌方中军袭扰"。
    /// </summary>
    public bool MoveUnit(FieldUnit unit, BattleRowType targetType, out string message)
        => MoveUnit(unit, targetType, -1, out message);

    public bool MoveUnit(FieldUnit unit, BattleRowType targetType, int slotIndex, out string message)
        => MoveToCarrier(unit, targetType, unit != null ? m.ResolveMoveRowTo(unit, targetType) : null,
                         slotIndex, out message);

    public bool MoveUnit(FieldUnit unit, BattleRow targetRow, int slotIndex, out string message)
    {
        if (targetRow == null) { message = "战场上没有这条排"; return false; }
        return MoveToCarrier(unit, targetRow.RowType, targetRow, slotIndex, out message);
    }

    private bool MoveToCarrier(FieldUnit unit, BattleRowType targetType, BattleRow carrier, int slotIndex, out string message)
    {
        message = null;
        if (unit == null) { message = "没有要移动的单位"; return false; }
        if (BattleSettlement.MatchOver) { message = "对局已经结束"; return false; }
        if (!IsSideAllowedToAct(unit.Side)) { message = "现在是对方的回合"; return false; }

        var from = unit.Row;

        // 调用方只给"排类型"时,方向的合法性由 ResolveMoveRowTo 按单位当前位置试前进/后退得出,
        // 不在这一层自己推方向 —— 共享前军的 row.Side 恒为 Player,
        // 各算一份必然出现"UI 判不合法、AI 却走得过去"。
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

        // 入排位置:slotIndex < 0 = 调用方没指定(键盘 / AI),贴到链末尾;
        // 鼠标拖动会把指针的横坐标换算成下标传进来(§8.1.1 落位,和出牌同一套 InsertIndexMath)。
        // §2.3 无格位模型下,位置只影响「守护」的左右相邻判定。
        int slot = slotIndex < 0 ? carrier.Units.Count : slotIndex;
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
        unit.MarkMoved();
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
    /// 顺序:① 规则(射程/守护/袭扰/AP) → ② **付款(AP + CP)** → ③ 结算伤害。
    /// 付款在结算之前,所以"付不起"时伤害根本不会发生 —— 也就不存在"回滚不回来"的问题
    /// (见类头注释:先结算后扣费会让致死的攻击把卡打没、还能 0 费无限连击)。
    /// </summary>
    public bool AttackUnit(FieldUnit attacker, FieldUnit target, out string message)
    {
        message = null;
        if (BattleSettlement.MatchOver) { message = "对局已经结束"; return false; }
        if (attacker != null && !IsSideAllowedToAct(attacker.Side)) { message = "现在是对方的回合"; return false; }

        // ① 规则:AP > 0、射程、守护、袭扰限制……(AP 在这里判一次,付款时还会再判一次)
        if (!BattleRules.CanAttack(attacker, target, out message)) return false;

        // ② 付款:AP 与行动费用 CP **任意一项不够就到此为止**
        if (!SpendAction(attacker, out message)) return false;

        // ③ 结算。付过费之后仍被拒(理论上不会:上面刚判过一遍)就把这次行动退回去
        if (!BattleSettlement.Attack(attacker, target, out message))
        {
            RefundAction(attacker);
            message = $"{message ?? "攻击失败"}（本次行动的费用已退回）";
            Debug.LogWarning($"[Battlefield] 「{attacker.DisplayName}」攻击「{target.DisplayName}」已付费用," +
                             $"但结算被拒,已退回:{message}");
            return false;
        }

        if (attacker != null) attacker.MarkAttacked();
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
    /// 这一步现在**付不付得起**(AP + 行动费用 CP)。**只读**,不碰任何账。
    /// UI 用它决定"这张卡现在能不能操作"(描边 / 提示),和真正扣费走同一份判据,
    /// 免得出现"绿框亮着、点了却提示指挥点不足"。
    /// </summary>
    public bool CanAffordAction(FieldUnit unit, out string message)
    {
        message = null;
        if (unit == null) { message = "没有可行动的单位"; return false; }
        if (unit.Ap <= 0) { message = "行动力已耗尽"; return false; }

        int cpCost = BattleRules.ActionCostOf(unit.Data);
        if (cpCost <= 0) return true;

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

        return true;
    }

    /// <summary>
    /// 扣一次行动的代价:1 AP + 该单位的行动费用 CP(§2.5)。**任一项不够就整体不生效**。
    /// 先把两项都确认过再扣;万一扣 CP 失败(理论上不会),把已经扣掉的 AP 退回去 ——
    /// 绝不允许出现"扣了 AP 没扣 CP"或"扣了 CP 没扣 AP"的半截状态。
    /// </summary>
    public bool SpendAction(FieldUnit unit, out string message)
    {
        message = null;

        if (!CanAffordAction(unit, out message)) return false;

        int cpCost = BattleRules.ActionCostOf(unit.Data);

        if (!unit.SpendActionPoint()) { message = "行动力已耗尽"; return false; }

        bool paid = true;
        if (cpCost > 0)
        {
            if (unit.Side == BattleSide.Player)
            {
                var cp = CommandPointController.Instance;
                if (cp != null) paid = cp.TrySpend(cpCost);
            }
            else
            {
                var enemy = EnemyDeckController.Instance;
                if (enemy != null) paid = enemy.TrySpend(cpCost);
            }
        }

        if (!paid)
        {
            unit.RefundActionPoint();
            message = "指挥点不足";
            return false;
        }

        return true;
    }

    /// <summary>把一次"付过费但没落地"的行动退回去(AP + 行动费用 CP)</summary>
    private static void RefundAction(FieldUnit unit)
    {
        if (unit == null) return;

        unit.RefundActionPoint();

        int cpCost = BattleRules.ActionCostOf(unit.Data);
        if (cpCost <= 0) return;

        if (unit.Side == BattleSide.Player) CommandPointController.Instance?.RefundCp(cpCost);
        else EnemyDeckController.Instance?.RefundCp(cpCost);
    }
}
