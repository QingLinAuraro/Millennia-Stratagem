using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 牌堆 + 手牌的数据层，并把牌堆 UI 的刷新接起来。
///
/// 抽牌入口是公开的 DrawOne() / DrawCards(n)：回合流程（自己回合开始自动摸牌）
/// 或者「摸牌N」策略卡直接调这两个方法即可，把 drawOnKeyD 关掉就不再吃键盘。
/// </summary>
public class DeckController : MonoBehaviour
{
    [Header("构筑(暂时拖12张,以后换成30张卡组)")]
    [SerializeField] private List<CardData> deckList;
    [SerializeField] private DeckUI deckUI;

    [Header("调试")]
    [Tooltip("仅调试用:按 debugDrawKey 摸一张。正式接回合流程后关掉")]
    [SerializeField] private bool drawOnKeyD = true;
    [SerializeField] private KeyCode debugDrawKey = KeyCode.D;

    private readonly Deck deck = new();
    private readonly Hand hand = new();
    private readonly PlayerState localPlayer = new PlayerState(isLocal: true);

    /// <summary>疲劳触发次数。每名玩家各自独立计数,永不重置(策划案§5.4.1)</summary>
    private int fatigueCount;

    public int DeckCount => deck.Count;
    public int HandCount => hand.Count;
    public int FatigueCount => fatigueCount;
    public Deck Pile => deck;

    private void Start()
    {
        if (deckList == null || deckList.Count == 0)
            Debug.LogError("[DeckController] deckList 是空的,牌堆一开局就是 0 张。", this);

        deck.Init(deckList ?? new List<CardData>());
        RefreshDeckUI();
    }

    private void Update()
    {
        if (drawOnKeyD && Input.GetKeyDown(debugDrawKey)) DrawOne();
    }

    /// <summary>摸 1 张。</summary>
    public void DrawOne() => DrawCards(1);

    /// <summary>
    /// 摸 count 张。回合开始摸牌、「摸牌N」策略卡都走这里。
    /// 每一张单独结算:牌堆空 → 各自触发一次疲劳;手牌满 → 那一张直接退场。
    /// </summary>
    public void DrawCards(int count)
    {
        for (int i = 0; i < count; i++) DrawSingle();
    }

    private void DrawSingle()
    {
        var card = deck.Draw();

        // 牌堆先刷新:不管摸到的牌是进手牌还是直接退场,牌堆都实实在在少了一张。
        // (原来这行在手牌满的 return 之后,于是手牌满时 UI 不更新,
        //  下次成功摸牌时计数会一次跳 2 —— 这就是"计数错误"的来源)
        RefreshDeckUI();

        if (card == null)
        {
            TriggerFatigue();
            return;
        }

        if (!hand.TryAdd(card))
        {
            // 策划案§5.4:手牌上限 7,超出的牌直接退场(没有弃牌堆)
            Debug.Log($"[DeckController] 手牌已满({Hand.MaxSize}张),摸到的「{card.cardName}」直接退场");
            return;
        }

        EventManager.Trigger(new CardDrawnEventArgs { Card = card, Player = localPlayer });
    }

    /// <summary>
    /// 牌堆已空还继续摸 → 疲劳:2/4/6/8/10...递增,扣的是摸牌方自己的大营。
    /// 策划案§5.4.1:空牌堆摸「摸牌3」= 2+4+6 = 12 点自伤。
    /// </summary>
    private void TriggerFatigue()
    {
        fatigueCount++;
        int damage = fatigueCount * 2;
        Debug.Log($"[DeckController] 牌堆已空,第 {fatigueCount} 次疲劳,大营受到 {damage} 点伤害(待接入大营HP)");
        // TODO: EventManager.Trigger(new FatigueEventArgs { Damage = damage, Player = localPlayer });
    }

    private void RefreshDeckUI()
    {
        if (deckUI != null) deckUI.Refresh(deck.Count);
    }
}
