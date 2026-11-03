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
    /// 表里留空就按策划案默认表(大营 25HP、军械库/粮草营各 10HP)。
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

        // 大营立绘在开局解析一次就够了:它跟着"这一局双方各带哪套卡组"走,一场里不会变。
        // 军械库/粮草不用解析 —— 它们的图直接连在场景的 BuildingSpec 上。
        var playerCampArt = ResolveCampArtwork(BattleSide.Player);
        var enemyCampArt = ResolveCampArtwork(BattleSide.Enemy);

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

            // 只有大营走"卡面图"这条路;军械库/粮草传 null,SpawnBuilding 会退回 spec.artwork
            var campArt = spec.displayName == BattleRules.CampName
                ? (spec.side == BattleSide.Player ? playerCampArt : enemyCampArt)
                : null;

            if (m.deployer.SpawnBuilding(spec, row, campArt) != null) placed++;
        }

        Debug.Log($"[Battlefield] 开局载入建筑 {placed} 座(§2.3:大营 25HP 在中军,军械库/粮草各 10HP 在后军,只可摧毁一次)");
        m.RaiseBoardChanged();
    }

    /// <summary>
    /// 找这一方大营该用哪张图:优先该方出战卡组里那张大营卡的 artwork(秦用秦汉用汉),
    /// 卡组里没有(老卡组/直接跑战斗场景)就退回卡池里按主朝代找大营卡,再不行随便挑一张大营卡。
    /// 一张都找不到返回 null —— SpawnBuilding 会保留 Build.prefab 自带的通用图。
    /// </summary>
    private static Sprite ResolveCampArtwork(BattleSide side)
    {
        var deck = side == BattleSide.Player ? BattleContext.PlayerDeck : BattleContext.OpponentDeck;

        // 1) 出战卡组里那张大营卡
        if (deck != null && deck.cards != null)
        {
            for (int i = 0; i < deck.cards.Count; i++)
            {
                var c = deck.cards[i];
                if (c != null && c.cardType == CardType.Building && c.artwork != null) return c.artwork;
            }
        }

        // 2) 卡池里按主朝代找(qin_b_01 / han_b_01),再不行不限朝代
        var library = CardLibrary.Current;
        if (library == null) return null;

        var camps = library.Filter(null, deck != null ? deck.primaryDynasty : null, CardType.Building);
        if (camps.Count == 0) camps = library.Filter(null, null, CardType.Building);

        for (int i = 0; i < camps.Count; i++)
            if (camps[i] != null && camps[i].artwork != null) return camps[i].artwork;

        Debug.LogWarning($"[Battlefield] {BattleRules.SideName(side)}大营找不到卡面图" +
                         "(卡池里没有 cardType=Building 的卡?),先用通用图。");
        return null;
    }

    /// <summary>
    /// §2.3 建筑表:大营 25 / 军械库 10 / 粮草 10 —— 这三个数是**起始血量**,不是"上限"。
    /// 打空 = 成废墟(留在场上),建筑不吃"修复"以外的回复手段;
    /// 军械库与粮草被摧毁后不可修复、只可摧毁一次,大营可以被修。
    /// 双方各一套。后军链接顺序 = 军械库 … 粮草
    /// </summary>
    private static List<BuildingSpec> DefaultBuildings() => new()
    {
        new BuildingSpec { displayName = BattleRules.CampName,    side = BattleSide.Player, row = BattleRowType.Mid,  hp = 25 },
        new BuildingSpec { displayName = BattleRules.ArsenalName, side = BattleSide.Player, row = BattleRowType.Back, hp = 10 },
        new BuildingSpec { displayName = BattleRules.GranaryName, side = BattleSide.Player, row = BattleRowType.Back, hp = 10 },
        new BuildingSpec { displayName = BattleRules.CampName,    side = BattleSide.Enemy,  row = BattleRowType.Mid,  hp = 25 },
        new BuildingSpec { displayName = BattleRules.ArsenalName, side = BattleSide.Enemy,  row = BattleRowType.Back, hp = 10 },
        new BuildingSpec { displayName = BattleRules.GranaryName, side = BattleSide.Enemy,  row = BattleRowType.Back, hp = 10 },
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

                // ⚠ 判据只能是**单位自己的 Side**,不能用 `row.Side`。
                //   共享前军的 row.Side 恒为 Player:照排判的话,我方军械库被毁会给
                //   站在前军的**敌方**单位一起 -1;敌方军械库被毁又漏掉站在前军的敌方单位。
                //   更常见的是袭扰骑兵 —— 它站在**对面中军**上(那条排的 Side 是对方),
                //   照排判会让它躲掉本该吃的那一刀,反而扣在我方自己头上。
                //   兵牌阵营是落位时定死的、进前军也不变(见 FieldUnit.SetRow),所以这里一定准。
                if (unit.Side == victimSide) unit.ApplyAtkDelta(-1);
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

                // 与 AddArsenalPenalty 同一套判据:只看单位自己的阵营(理由见那里)
                if (unit.Side == side) unit.ApplyAtkDelta(1);
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
