using System;
using UnityEngine;

/// <summary>
/// 开局要摆上去的一座建筑锚点(策划案§2.3)。
///
/// 大营固定存在于己方中军,军械库与粮草固定存在于己方后军;它们占所在排的容量、
/// 不可移动、不可被部署替换,入排的单位绕它们左右贴放。建筑不是卡片资产
/// (不在卡池里),所以这里只记"叫什么、在哪一方哪条排、多少血":
/// 实体由 BattlefieldManager 用 Build.prefab 生成,血数字写进预制体上那颗角标。
///
/// 血量照§2.3 建筑表:大营 20(归零立即判负)/ 军械库 5(被毁 → 敌方 ATK -1)/
/// 粮草 5(被毁 → 敌方 CP 上限 -2)。摧毁后的 Debuff 结算还没接。
/// </summary>
[Serializable]
public class BuildingSpec
{
    [Tooltip("显示名(飘字/日志用),也用来给生成的物体命名")]
    public string displayName = "大营";

    [Tooltip("摆在哪一方")]
    public BattleSide side = BattleSide.Player;

    [Tooltip("落在哪条排(§2.3:大营 → 中军;军械库 / 粮草 → 后军)")]
    public BattleRowType row = BattleRowType.Mid;

    [Tooltip("初始 HP(§2.3:大营 20 / 军械库 5 / 粮草 5)")]
    public int hp = 20;
}
