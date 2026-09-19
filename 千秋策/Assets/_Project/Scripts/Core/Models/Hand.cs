using System.Collections.Generic;

// 手牌
public class Hand
{
    public const int MaxSize = 7;
    private readonly List<CardData> cards = new();
    public int Count => cards.Count;
    public IReadOnlyList<CardData> Cards => cards;
    // true=加入成功; false=已满,卡被销毁
    public bool TryAdd(CardData card)
    {
        if (cards.Count >= MaxSize) return false;
        cards.Add(card);
        return true;
    }
    // 出牌
    public bool Remove(CardData card) => cards.Remove(card);
}