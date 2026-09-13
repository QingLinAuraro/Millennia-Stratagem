using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 打出一张牌(部署兵牌 / 释放策略牌)时广播(策划案§3.3.2)。
///
/// Player.isLocal 用来区分是谁出的:提示条 BattleMessageUI 只报对方出的牌,
/// 我方出牌是即时反馈(卡牌自己飞出去 + 飘字),不再刷一条提示。
/// </summary>
public class CardPlayedEventArgs : GameEventArgs
{
    public CardData Card { get; set; }
    public PlayerState Player { get; set; }

    public CardPlayedEventArgs()
    {
        EventType = GameEventType.CardPlayed;
    }
}
