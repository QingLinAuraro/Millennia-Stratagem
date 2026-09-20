using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 建筑状态 / 军械库 Debuff(§2.3):
///   · 开局把大营 / 军械库 / 粮草营摆到各自排上,并代管"建筑该摆在哪"的默认表
///   · 军械库被毁 → 那一方全场兵牌 ATK -1(只叠一层);被修好 → 加回去
///   · 新上场的兵牌补上这笔 ATK 惩罚(「后续上场兵牌 ATK -1」)
///
/// 袭扰(§7.4)不在这里 —— 袭扰是骑兵进出中军产生的实时状态,由 BattleRow 自己算
/// (见 BattleRow.RefreshRaiderFlag),不是建筑这类常驻账。
/// </summary>
public class BuildingManager
{
    /// <summary>军械库被毁后积下来的 ATK 惩罚(§2.3):场上兵牌与后续上场兵牌各 -1</summary>
    private bool playerArsenalPenalty;
    private bool enemyArsenalPenalty;

    private readonly BattlefieldManager m;
    private readonly PrefabResolver prefabs;

    public BuildingManager(BattlefieldManager manager, PrefabResolver prefabResolver)
    {
        m = manager;
        prefabs = prefabResolver;
    }

    /// <summary>
    /// 开局载入建筑锚点(§2.3):大营 → 己方中军,军械库 + 粮草营 → 己方后军;敌方同构。
    /// 表里留空就按策划案默认表(大营 20HP、军械库/粮草营各 5HP)。
    /// </summary>
    public void LoadBuildings()
    {
        if (prefabs.ResolveBuildPrefab() == null)
        {
            Debug.LogWarning("[BattlefieldManager] 没给 buildPrefab,开局建筑摆不出来" +
                             "(把资产里的 Build.prefab 拖到这个字段上)。", m);
            return;
        }

        var list = m.buildings != null && m.buildings.Count > 0 ? m.buildings : DefaultBuildings();

        int placed = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var spec = list[i];
            if (spec == null) continue;

            var row = m.layouts.FindRow(spec.side, spec.row);
            if (row == null)
            {
                Debug.LogWarning($"[BattlefieldManager] 建筑「{spec.displayName}」找不到 {spec.side} 的 {spec.row} 排,没摆上去。", m);
                continue;
            }

            if (m.deployer.SpawnBuilding(spec, row) != null) placed++;
        }

        Debug.Log($"[Battlefield] 开局载入建筑 {placed} 座(§2.3:大营 20HP 在中军,军械库/粮草各 5HP 在后军)");
        m.RaiseBoardChanged();
    }

    /// <summary>
    /// §2.3 建筑表:大营 20 / 军械库 5 / 粮草营 5 —— 这三个数是**起始血量**,不是"上限"。
    /// 打空 = 成废墟(留在场上,能被维修卡修回来),修回来的血量就是这里的起始值。
    /// 双方各一套。后军链接顺序 = 军械库 … 粮草营
    /// </summary>
    private static List<BuildingSpec> DefaultBuildings() => new()
    {
        new BuildingSpec { displayName = "大营",   side = BattleSide.Player, row = BattleRowType.Mid,  hp = 20 },
        new BuildingSpec { displayName = "军械库", side = BattleSide.Player, row = BattleRowType.Back, hp = 5 },
        new BuildingSpec { displayName = "粮草营", side = BattleSide.Player, row = BattleRowType.Back, hp = 5 },
        new BuildingSpec { displayName = "大营",   side = BattleSide.Enemy,  row = BattleRowType.Mid,  hp = 20 },
        new BuildingSpec { displayName = "军械库", side = BattleSide.Enemy,  row = BattleRowType.Back, hp = 5 },
        new BuildingSpec { displayName = "粮草营", side = BattleSide.Enemy,  row = BattleRowType.Back, hp = 5 },
    };

    /// <summary>找某一方的某座建筑(大营/军械库/粮草营)。找不到返回 null</summary>
    public FieldUnit FindBuilding(BattleSide side, string buildingName)
    {
        for (int i = 0; i < m.rows.Count; i++)
        {
            var row = m.rows[i];
            if (row == null || row.Side != side) continue;

            var members = row.Units;
            for (int j = 0; j < members.Count; j++)
            {
                var unit = members[j];
                if (unit != null && unit.IsBuilding && unit.BuildingName == buildingName) return unit;
            }
        }
        return null;
    }

    /// <summary>某一方的军械库被毁了几座积下来的 ATK 惩罚(§2.3:军械库被毁 → 全场兵牌 ATK -1)</summary>
    public int ArsenalPenalty(BattleSide side) => side == BattleSide.Player ? (playerArsenalPenalty ? 1 : 0) : (enemyArsenalPenalty ? 1 : 0);

    /// <summary>军械库被毁:给那一方记一笔 ATK -1</summary>
    public void AddArsenalPenalty(BattleSide victimSide)
    {
        // 如果已经有惩罚了,不能再叠(军械库被毁的 Debuff 只有一层)
        if (victimSide == BattleSide.Player)
        {
            if (playerArsenalPenalty) return;
            playerArsenalPenalty = true;
        }
        else
        {
            if (enemyArsenalPenalty) return;
            enemyArsenalPenalty = true;
        }

        // 场上现有的兵牌立刻吃这一刀(建筑不吃)
        for (int i = 0; i < m.rows.Count; i++)
        {
            var row = m.rows[i];
            if (row == null) continue;

            // 遍历这条排上的所有单位
            var members = row.Units;
            for (int j = 0; j < members.Count; j++)
            {
                var unit = members[j];
                if (unit == null || !unit.IsUnit || !unit.IsAlive) continue;

                // 判断这个兵牌是否属于受害方:
                // 1. 排的阵营 == 受害方(自己后场/中场的兵)
                // 2. 或者排是敌方中军,但这个单位正在袭扰(属于受害方)
                var isMine = row.Side == victimSide;
                var isRaiderFromMySide = row.RowType == BattleRowType.Mid && row.Side != victimSide && unit.Side == victimSide;
                if (isMine || isRaiderFromMySide) unit.ApplyAtkDelta(-1);
            }
        }

        m.RaiseBoardChanged();
    }

    /// <summary>军械库被修复:清除该方 ATK -1 的惩罚,场上兵牌恢复攻击力</summary>
    public void RemoveArsenalPenalty(BattleSide side)
    {
        if (side == BattleSide.Player)
        {
            if (!playerArsenalPenalty) return;
            playerArsenalPenalty = false;
        }
        else
        {
            if (!enemyArsenalPenalty) return;
            enemyArsenalPenalty = false;
        }

        for (int i = 0; i < m.rows.Count; i++)
        {
            var row = m.rows[i];
            if (row == null) continue;

            var members = row.Units;
            for (int j = 0; j < members.Count; j++)
            {
                var unit = members[j];
                if (unit == null || !unit.IsUnit || !unit.IsAlive) continue;

                var isMine = row.Side == side;
                var isRaiderFromMySide = row.RowType == BattleRowType.Mid && row.Side != side && unit.Side == side;
                if (isMine || isRaiderFromMySide) unit.ApplyAtkDelta(1);
            }
        }

        m.RaiseBoardChanged();
    }

    /// <summary>新上场的兵牌补上军械库的 ATK 惩罚(§2.3「后续上场兵牌 ATK -1」)</summary>
    public void ApplyArsenalPenalty(FieldUnit unit)
    {
        if (unit == null || !unit.IsUnit) return;

        int penalty = ArsenalPenalty(unit.Side);
        if (penalty > 0) unit.ApplyAtkDelta(-penalty);
    }
}
