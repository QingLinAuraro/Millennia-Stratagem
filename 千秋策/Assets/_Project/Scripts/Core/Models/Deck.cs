using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 牌堆、洗牌、抽牌
public class Deck
{
    // 随机洗牌
    private readonly List<CardData> cards = new();
    // 获取牌堆中剩余牌数量
    public int Count => cards.Count;

    public void Init(IEnumerable<CardData> list)
    {
        cards.Clear();
        if (list != null)
        {
            foreach (var card in list)
            {
                // 大营卡(cardType = Building)是**固定位**,不是能打出去的牌:
                // 它只负责"这套卡组带的是秦大营还是汉大营",用来决定战场大营的立绘。
                // 卡组资产里必须有它(校验要求正好 1 张),但牌堆里不能有它 ——
                // 过滤放在 Init 里最省事:我方 DeckController 与敌方 EnemyDeckController 都走这里。
                if (card == null || card.cardType == CardType.Building) continue;
                cards.Add(card);
            }
        }
        Shuffle();
    }

    // 洗牌
    public void Shuffle()
    {
        for (int i = cards.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
    }

    // 将卡牌加入到牌堆
    public void Add(CardData card, bool toTop = true)
    {
        if (card == null) return;

        if (toTop) cards.Insert(0, card);
        else cards.Add(card);
    }

    // 抽取卡牌
    public CardData Draw()
    {
        if (cards.Count == 0) return null;
        var top = cards[0];
        cards.RemoveAt(0);
        return top;
    }
}
