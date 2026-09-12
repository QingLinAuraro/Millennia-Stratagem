using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 手牌表现层:监听抽牌事件,把卡生成到手牌区并刷新扇形。
/// 不做任何规则判断(上限由数据层 Hand 拦截)。
/// </summary>
public class HandUI : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("必须是 Project 窗口里的 Card.prefab 资产,不要拖场景里的实例")]
    [SerializeField] private CardDisplay cardPrefab;
    [Tooltip("手牌区:HandArea1")]
    [SerializeField] private Transform handRoot;
    [Tooltip("HandArea1 上的 FanLayout")]
    [SerializeField] private FanLayout fanLayout;

    private readonly List<CardDisplay> spawned = new();
    public IReadOnlyList<CardDisplay> Spawned => spawned;

    private void Awake() => ValidateRefs();

    private void OnEnable()
    {
        EventManager.Subscribe<CardDrawnEventArgs>(GameEventType.CardDrawn, OnCardDrawn);
    }

    private void OnDisable()
    {
        EventManager.Unsubscribe<CardDrawnEventArgs>(GameEventType.CardDrawn, OnCardDrawn);
    }

    private void ValidateRefs()
    {
        if (cardPrefab == null)
        {
            Debug.LogError("[HandUI] cardPrefab 没有赋值,抽牌不会生成任何卡。", this);
            return;
        }

        // 拖成场景里的实例时,那个实例上被 Inspector 覆盖过的字段会被原样复制到
        // 每一张抽出来的卡上。典型事故:实例的 borderImage 被改成了 Background,
        // 于是每抽一张卡就把整块游戏背景染成稀有度颜色。
        if (cardPrefab.gameObject.scene.IsValid())
        {
            Debug.LogError(
                $"[HandUI] cardPrefab 引用的是场景里的实例「{cardPrefab.name}」,不是 Card.prefab 资产。" +
                "请从 Project 窗口把 Prefabs/UI/Card.prefab 拖进来 —— " +
                "否则该实例上被覆盖过的字段(尤其是 borderImage)会污染每一张抽出来的卡。",
                this);
        }

        if (handRoot == null) Debug.LogError("[HandUI] handRoot 没有赋值。", this);
        if (fanLayout == null) Debug.LogError("[HandUI] fanLayout 没有赋值,手牌会全部叠在手牌区中心。", this);
    }

    private void OnCardDrawn(CardDrawnEventArgs e)
    {
        if (e == null || e.Card == null || e.Player == null) return;
        if (!e.Player.isLocal) return;          // 敌方手牌不显示
        if (cardPrefab == null || handRoot == null) return;

        var card = Instantiate(cardPrefab, handRoot);
        card.name = $"Card_{e.Card.cardId}";
        card.Bind(e.Card);
        spawned.Add(card);

        // 交给 FanLayout 的列表(它不按子物体顺序排,悬停换 sibling 也不会乱序)
        fanLayout?.Add((RectTransform)card.transform);
    }

    /// <summary>打出一张手牌时调用(以后接出牌逻辑),销毁并重新排扇形</summary>
    public void RemoveCard(CardDisplay card)
    {
        if (card == null) return;
        spawned.Remove(card);
        fanLayout?.Remove((RectTransform)card.transform);
        Destroy(card.gameObject);
    }

    /// <summary>清空手牌区</summary>
    public void Clear()
    {
        for (int i = 0; i < spawned.Count; i++)
            if (spawned[i] != null) Destroy(spawned[i].gameObject);
        spawned.Clear();
        fanLayout?.Clear();
    }
}
