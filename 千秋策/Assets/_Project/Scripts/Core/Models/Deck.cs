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

    public CardData Draw()
    {
        if (cards.Count == 0) return null;
        var top = cards[0];
        cards.RemoveAt(0);
        return top;
    }
}
