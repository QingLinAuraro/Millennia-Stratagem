using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 排布局 / 格子 / 间距:5 条排的几何与查找都归这里。
///
/// 职责:
///   · ApplyRowLayout :把行高与链间距钉死(§2.3),不让排里的卡把整排撑高
///   · FindRow        :按「阵营 + 位置」查排(前军是双方共享的同一条,单独处理)
///   · ResolveMoveRow :移动方向 → 目标排(§2.3 相邻前进 / §7.4 袭扰撤回)
///
/// 它只管"排在哪、有几个格子、能走到哪",不碰出牌与结算。
/// </summary>
public class BattlefieldLayouts
{
    private readonly BattlefieldManager m;

    public BattlefieldLayouts(BattlefieldManager manager)
    {
        m = manager;
    }

    /// <summary>
    /// 把行高和链间距钉死(§2.3)。
    ///
    /// 原来 RowsContainer 上的 VerticalLayoutGroup 是按子物体的偏好高度撑各排的:
    /// 排里一塞卡牌,这条排的偏好高度就变成卡牌高度,整排变高、5 条排全被挤走位。
    /// 这里关掉它对高度的接管(childControlHeight),高度改由每条排自己写死
    /// (BattleRow.LockLayout),间距由容器自己那层 spacing 出。
    /// </summary>
    public void ApplyRowLayout()
    {
        var container = m.GetComponent<VerticalLayoutGroup>();
        if (container != null)
        {
            container.childControlHeight = false;     // 高度各排自己定,别按内容撑
            container.childForceExpandHeight = false; // 不摊余量:5×144 + 4×8 = 752,铺满 756 差 4px(间距来自 RowsContainer 的 VerticalLayoutGroup)
        }

        for (int i = 0; i < m.rows.Count; i++)
            if (m.rows[i] != null) m.rows[i].LockLayout(m.rowHeight, m.memberGap);
    }

    public BattleRow FindRow(BattleSide side, BattleRowType rowType)
    {
        // §2.3 前军是双方共用的**同一条排**(场景里它就是 playerFront,row.Side 恒为 Player)。
        // 所以这里不能按 side 过滤后军/中军那样去查前军 —— 否则敌方单位一往前走
        // 就会得到"战场上没有这条排",而它明明就在场中央。
        if (rowType == BattleRowType.Front) return m.playerFront;

        for (int i = 0; i < m.rows.Count; i++)
            if (m.rows[i] != null && m.rows[i].Side == side && m.rows[i].RowType == rowType) return m.rows[i];
        return null;
    }

    /// <summary>
    /// 移动方向 → 目标排。**转发给 BattleRules.ResolveMove**,不要在这里再写一份方向判定:
    /// 方向必须按单位自己的阵营算(共享前军那条排的 row.Side 恒为 Player),
    /// 这里自己写一份就会和 BattleRules.TryForwardRow 的明文查表对不上 ——
    /// 历史上"三处各算一份、算错的就是这一条"就是这么来的。
    /// </summary>
    public BattleRow ResolveMoveRow(FieldUnit unit, MoveDirection direction)
        => BattleRules.ResolveMove(unit, direction).Row;

    /// <summary>移动方向 → 完整结果(合不合法 + 落到哪条排 + 是不是后撤),给需要原因/后撤标记的调用方用</summary>
    public BattleRules.MoveStep ResolveMove(FieldUnit unit, MoveDirection direction)
        => BattleRules.ResolveMove(unit, direction);

    /// <summary>
    /// 目标**排类型** → 哪条排。这是"合法移动中,这行向前/向后的下一行是哪行"的入口:
    /// 调用方说"我要去中军 / 前军 / 后军",这里按单位当前位置试前进、再试后退,
    /// 命中就返回那条排。而不是让每个调用点自己去推方向。
    /// </summary>
    public BattleRow ResolveMoveRowTo(FieldUnit unit, BattleRowType targetType)
    {
        if (unit == null || unit.Row == null) return null;
        if (unit.Row.RowType == targetType) return null;      // 原地不算"移动到"

        if (BattleRules.TryForwardRow(unit, unit.Row, out var fSide, out var fType) && fType == targetType)
            return m.FindRow(fSide, fType);

        if (BattleRules.TryBackwardRow(unit, unit.Row, out var bSide, out var bType) && bType == targetType)
            return m.FindRow(bSide, bType);

        return null;
    }
}
