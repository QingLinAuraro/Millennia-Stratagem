using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum CardType { Unit, Tactic }                                // 兵牌 / 策略牌
public enum UnitType { Infantry, Cavalry, Archer, Support, Strategy }          // 步 / 骑 / 弓 / 器 / 策
public enum Rarity { Standard, Limited, Special, Elite }             // 普通/稀有/史诗/传说
public enum WeightClass { Light, Medium, Heavy }                     // 轻/中/重
public enum Keyword { Blitz, Ambush, Guard, BloodBattle, DoubleStrike,
                      HeavyArmor, DrawCards, Summon, Heal, Oath }    // 策划案§4.3词条

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
}
