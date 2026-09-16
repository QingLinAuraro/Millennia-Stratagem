using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum CardType { Unit, Tactic }                                // 兵牌 / 策略牌
public enum UnitType { Infantry, Cavalry, Archer, Support, Strategy }          // 步 / 骑 / 弓 / 器 / 策
// 普通/稀有/史诗/传说 + 后续档位(红)。卡面小方框已按 白→蓝→紫→金→红 配好色,暂时没有卡用 Future
public enum Rarity { Standard, Limited, Special, Elite, Future }
public enum WeightClass { Light, Medium, Heavy }                     // 轻/中/重
public enum Keyword { Blitz, Ambush, Guard, BloodBattle, DoubleStrike,
                      HeavyArmor, DrawCards, Summon, Heal, Oath }    // 策划案§4.3词条

/// <summary>
/// 策略卡释放时要指定的目标类型(策划案§7.2.2)。
/// None = 无目标:把手牌拖出手牌区就释放;其余取值表示必须把牌拖到对应目标上才算释放。
/// </summary>
public enum TacticTargetType
{
    None,             // 无目标(抽卡/过牌类、随机目标类):拖离手牌区即释放
    EnemyUnit,        // 敌方兵牌
    EnemyBuilding,    // 敌方建筑
    EnemyRow,         // 敌方排(以整排为目标)
    AllyUnit,         // 友方兵牌
    AllyRow,          // 己方排
    AllyBuilding,     // 己方建筑(维修类)
    EnemyHand,        // 敌方手牌区(弃置敌方牌类)
}

[CreateAssetMenu(fileName = "NewCard", menuName = "千秋策/卡牌")]
public class CardData : ScriptableObject
{
    [Header("基础信息")]
    public string cardId;            
    public string cardName;          
    public string dynasty;           
    public Rarity rarity;
    public CardType cardType;
    public Sprite artwork;

    [Header("文本")]
    [TextArea] public string flavorText;    // 古文风味(暂未显示,先存着)
    [TextArea] public string effectText;    // 效果描述

    [Header("费用")]
    public int deploymentCost = 1;             // 部署费用
    public int actionCost = 1;       // 行动费用(策略卡忽略)
    public int actionCount = 1;      // 行动次数(策略卡忽略)

    [Header("兵牌属性(策略卡忽略)")]
    public int atk = 1;
    public int hp = 1;
    public UnitType unitType;
    public WeightClass weight;  // 忽略，直接用词条区分
    public List<Keyword> keywords = new List<Keyword>();

    [Header("策略卡目标(策划案§7.2.2，兵牌忽略)")]
    [Tooltip("None = 无目标:拖离手牌区即释放。\n" +
             "其余 = 必须把这牌拖到对应目标上释放(伤害类只能指定敌方、buff 类只能指定友方)。\n" +
             "随机目标(卡面写「随机单位」)算无目标。\n" +
             "多目标策略卡(决水灌城/盐铁论)先填第一个要指定的目标")]
    public TacticTargetType targetType = TacticTargetType.None;

    /// <summary>兵牌(策略卡与 unitType=Strategy 都不算)</summary>
    public bool IsUnitCard => cardType == CardType.Unit && unitType != UnitType.Strategy;

    /// <summary>释放前必须指定目标</summary>
    public bool RequiresTarget => cardType == CardType.Tactic && targetType != TacticTargetType.None;
}
