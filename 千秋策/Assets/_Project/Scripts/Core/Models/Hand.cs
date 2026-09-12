using System.Collections.Generic;

public class Hand
{
    public const int MaxSize = 7;    // 策划案§5.4:手牌上限7张

    private readonly List<CardData> cards = new();
    public int Count => cards.Count;
    public IReadOnlyList<CardData> Cards => cards;

    /// <summary>true=加入成功; false=已满,卡被销毁(无弃牌堆设计)</summary>
    public bool TryAdd(CardData card)
    {
        if (cards.Count >= MaxSize) return false;
        cards.Add(card);
        return true;
    }

    public bool Remove(CardData card) => cards.Remove(card);   // 打出卡牌时调用
}