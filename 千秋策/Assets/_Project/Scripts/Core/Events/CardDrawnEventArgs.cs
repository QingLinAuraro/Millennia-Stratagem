using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CardDrawnEventArgs : GameEventArgs
{
    public CardData Card { get; set; }
    public PlayerState Player { get; set; }

    public CardDrawnEventArgs()
    {
        EventType = GameEventType.CardDrawn;
    }
}
