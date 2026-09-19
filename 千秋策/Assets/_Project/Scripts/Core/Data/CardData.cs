using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.ComponentModel;

public enum CardType 
{ 
    [Description("单位")] Unit, 
    [Description("策略")] Tactic 
}
public enum UnitType 
{
    [Description("步兵")] Infantry, 
    [Description("骑兵")] Cavalry, 
    [Description("弓兵")] Archer, 
    [Description("器械")] Support, 
    [Description("策略")] Strategy, 
    [Description("反制")] Counter
}
// 卡面小方框已按 白→蓝→紫→金→红
public enum Rarity 
{ 
    [Description("普通")] Ordinary, 
    [Description("稀有")] Rare, 
    [Description("史诗")] Epic, 
    [Description("传说")] Legend, 
    [Description("英雄")] Hero 
}
public enum WeightClass 
{ 
    [Description("轻型")] Light, 
    [Description("中型")] Medium, 
    [Description("重型")] Heavy 
}
/// <summary>
/// 卡面词条(§4.3)。只放**固定数值/固定内容**的词条 —— 这些是单位自带的、不随卡面文字变化的能力。
///
/// 「摸牌 / 召唤 / 回血 / 誓师」**不属于词条**:它们是卡牌效果本身,登记在 CardEffectDatabase
/// 并写在 CardData.effectText 里(见 §4.3 的效果栏),塞进枚举里会和效果栏重复一套数据源。
///
/// ⚠ 本枚举的成员顺序必须和 Tools/build_card_assets.py 的 KEYWORD_VALUE / KEYWORD_NAME 对齐
///   (脚本按名字写进 .asset,顺序只影响可读性;但两边增删必须同步,否则脚本会报「不在 Keyword 枚举内」)。
/// </summary>
public enum Keyword 
{
    [Description("闪击")] Blitz,
    [Description("伏兵")] Ambush,
    [Description("守护")] Guard,
    [Description("血战")] BloodBattle,
    [Description("连战")] DoubleStrike,
    [Description("重甲")] HeavyArmor,
    [Description("抵抗")] Resist,
}
public enum TacticTargetType
{
    [Description("无目标")] None,
    [Description("敌方兵牌")] EnemyUnit,
    [Description("敌方建筑")] EnemyBuilding,
    [Description("敌方排")] EnemyRow,
    [Description("友方兵牌")] AllyUnit,
    [Description("己方排")] AllyRow,
    [Description("己方建筑")] AllyBuilding,
    [Description("敌方手牌区")] EnemyHand,
}
// 快速创建
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
    [TextArea] public string flavorText;    // 古文风味
    [TextArea] public string effectText;    // 效果描述

    [Header("费用")]
    public int deploymentCost = 1;             // 部署费用
    public int actionCost = 1;       // 行动费用(策略卡忽略)
    public int actionCount = 1;      // 行动次数(策略卡忽略)

    [Header("卡牌属性")]
    public int atk = 1;   // 攻击力，策略卡忽略
    public int hp = 1;   // 生命值，策略卡忽略
    public UnitType unitType;  // 卡牌定位
    public WeightClass weight;  // 忽略，直接用词条区分
    public List<Keyword> keywords = new List<Keyword>();

    [Header("策略卡目标")]
    [Tooltip("None = 无目标:拖离手牌区即释放。\n" +
             "其余 = 必须把这牌拖到对应目标上释放(伤害类只能指定敌方、buff 类只能指定友方)。\n" +
             "随机目标。\n" +
             "多目标策略卡先填第一个要指定的目标")]
    public TacticTargetType targetType = TacticTargetType.None;
    public bool IsUnitCard => cardType == CardType.Unit && unitType != UnitType.Strategy && unitType != UnitType.Counter;
    public bool RequiresTarget => cardType == CardType.Tactic && targetType != TacticTargetType.None;
}
