using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Deck
{
    private readonly List<CardData> cards = new();
    public int Count => cards.Count;

    public void Init(IEnumerable<CardData> list)
    {
        cards.Clear();
        cards.AddRange(list);
        Shuffle();
    }

    public void Shuffle()
    {
        for (int i = cards.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
    }

    /// <summary>
    /// 往牌堆里加一张(「召唤(牌堆)」效果用,策划案§4.3)。
    /// 放回**牌堆顶**(下一张就摸到),这是"召唤"类卡的本意:立刻补一张资源。
    /// </summary>
    public void Add(CardData card, bool toTop = true)
    {
        if (card == null) return;

        if (toTop) cards.Insert(0, card);
        else cards.Add(card);
    }

    public CardData Draw()
    {
        if (cards.Count == 0) return null;
        var top = cards[0];
        cards.RemoveAt(0);
        return top;
    }
}
