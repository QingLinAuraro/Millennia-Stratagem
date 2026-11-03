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
/// 血量:大营 25(归零立即判负)/ 军械库 10(被毁 → **主人**全场 ATK -1)/
/// 粮草 10(被毁 → **主人** CP 上限 -2)。军械库与粮草被摧毁后**不可修复**,只可摧毁一次;Debuff 结算在 `BattleSettlement.OnBuildingDestroyed`,
/// 维修卡把它修回来时由 `BattleSettlement.OnBuildingRepaired` 撤掉。
/// ⚠ displayName 必须和 `BattleRules.ArsenalName` / `BattleRules.GranaryName` 逐字一致,
///   结算层是拿名字认建筑的 —— 写成「粮草营」的话粮草 Debuff 永远不会触发。
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

    [Tooltip("初始 HP(大营 25 / 军械库 10 / 粮草 10)")]
    public int hp = 25;

    /// <summary>
    /// 立绘。留空 = 用 Build.prefab 自带的那张通用图。
    ///
    /// 军械库 / 粮草在这里直接连 Art/Buildings 下的 png;
    /// **大营不要在这里连** —— 大营的图跟着卡组里的那张大营卡走(秦用秦汉用汉),
    /// 由 BuildingManager 在开局按"这一局双方各带哪套卡组"临时解析,场景里写死会串朝代。
    /// </summary>
    [Tooltip("立绘。留空 = 用预制体自带的通用图。大营请留空(由大营卡的卡面决定)")]
    public Sprite artwork;
}
