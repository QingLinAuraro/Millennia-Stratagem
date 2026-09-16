using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 手牌表现层:监听抽牌事件,把卡生成到手牌区并刷新扇形。
/// 不做任何规则判断(上限由数据层 Hand 拦截)。
/// </summary>
///
/// 【挂载 & 调整】
///   挂在:Battle.unity 里的物体「HandUI」上(Assets/_Project/Scenes/Battle.unity)。它在场景里是 BattleCanvas 的
///         直接子物体,身上只有普通 Transform + 本脚本(不是 UI 矩形 —— 手牌区是它引用的 HandArea1)。
///         没有 Instance、也不会自举:全场景就这一份,BattlefieldManager / BattleMessageUI 反而要用
///         FindObjectOfType<HandUI>() 回过头来找它。删掉或禁用 = 整局没有我方手牌表现层:抽牌事件不再生成卡面
///         (数据层照常进账)、打出的牌不会被移除、「拖回手牌区 = 取消」也失效。
///   引用:5 个引用全部靠手连,脚本里没有任何自动查找(只有 hoverPreviewRoot 有按名字的兜底)。
///         · cardPrefab:必须拖 Project 窗口里的 Assets/_Project/Prefabs/UI/Card.prefab 资产。
///           留空 → Awake 打 LogError,OnCardDrawn 直接 return,一张牌都不生成。
///           拖成场景里的实例 → 同样 LogError:那个实例上被 Inspector 覆盖过的字段(尤其 rarityImage)
///           会被原样复制到每一张抽出来的卡上,典型事故是把整块游戏背景染成稀有度颜色。
///         · battleCardPrefab:战场卡面 CardsInBattle.prefab,**只给 BattlefieldManager 拿去生战场卡**,
///           手牌和悬停预览都不用它。留空不算错 —— BattlefieldManager 会自己按路径认领,
///           认领不到才退回 cardPrefab 并警告(那样战场上的字会偏小)。
///         · handRoot:必须拖 HandArea1 的 RectTransform(800x140,底边贴着屏幕底边)。
///           留空 → LogError,而且 OnCardDrawn 直接 return(手牌没地方摆);同时 HandRect 为 null,
///           BattlefieldManager 判「松手点在不在手牌区里」时恒为 false —— 策略卡的「拖离手牌区即释放」失效。
///         · fanLayout:必须拖 HandArea1 上那份 FanLayout。留空 → LogError,卡还是照常生成,但不进扇形列表,
///           会停在 Card.prefab 里烘的锚点/位置上(预制体留着 3228 的残留位置,等于排到屏幕左下方外面去;
///           报错文案写的是「手牌会全部叠在手牌区中心」,和实际不符)。
///           注意 CardDragPlay 是自己 GetComponentInParent<FanLayout>() 找扇形的,所以这里留空不影响拖动和变灰。
///         · hoverPreviewRoot:留空则 CardHoverPreview.Ensure 按名字找场景里的 hover / Hover;两个都找不到只打
///           LogWarning,悬停不弹预览,手牌其余功能照常。它只决定预览落在哪个坐标系里,
///           具体弹在被悬停那张牌的哪个方位由 CardHoverPreview.PlaceNear 算(手牌/战场同一套规则)。
///   常调:本组件没有数值参数,五个引用都是「配对」用的,能调出来的效果其实都在别人身上:
///     · cardPrefab:换手牌样式就换这个预制体(手牌 + 悬停预览两处都取自这里)。
///       换完记得回头核对 Card.prefab 上 CardDisplay / CardHover / CardDragPlay 的字段,并确认它仍是资产而不是场景实例。
///     · battleCardPrefab:换战场卡面就换它(CardsInBattle.prefab),换完核对它身上的 CardDisplay / CardHover 字段。
///     · handRoot:它同时是「手牌区矩形」,一件事两个用途 —— 卡牌扇形以它的底边中心为基准,「拖离手牌区 = 释放」也以它为准。
///       把 HandArea1 的 rect 调大 = 更容易被算作「还在手牌区内」(取消更宽容、更不容易误出牌);调小则稍微拖出去一点就当作打出。
///     · fanLayout:间距 / 弧度 / 基准高度都在 FanLayout 那份实例上调,这里只负责接线;我方那份的现值见 FanLayout 的说明。
///     · hoverPreviewRoot:想让悬停预览换个落点就把它指到别的 RectTransform;留空 = 自动认领名为 hover 的格子(300x400)。
public class HandUI : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("必须是 Project 窗口里的 Card.prefab 资产,不要拖场景里的实例")]
    [SerializeField] private CardDisplay cardPrefab;
    [Tooltip("战场卡面:CardsInBattle.prefab。留空则由 BattlefieldManager 自己按路径认领;\n" +
             "它只影响战场上的卡,手牌与悬停预览永远用 cardPrefab")]
    [SerializeField] private CardDisplay battleCardPrefab;
    [Tooltip("手牌区:HandArea1")]
    [SerializeField] private Transform handRoot;
    [Tooltip("HandArea1 上的 FanLayout")]
    [SerializeField] private FanLayout fanLayout;

    [Header("悬停预览")]
    [Tooltip("悬停预览的落点:场景里的 hover(300x400)。留空则运行时自动按名字找 hover")]
    [SerializeField] private RectTransform hoverPreviewRoot;

    private readonly List<CardDisplay> spawned = new();
    public IReadOnlyList<CardDisplay> Spawned => spawned;

    /// <summary>手牌用的 Card.prefab(手牌 + 悬停预览都用它)</summary>
    public CardDisplay CardPrefab => cardPrefab;

    /// <summary>战场卡面 CardsInBattle.prefab(只给 BattlefieldManager 生战场卡用;留空则它自己按路径认领)</summary>
    public CardDisplay BattleCardPrefab => battleCardPrefab;

    /// <summary>手牌区矩形 —— 拖动出牌时"拖离这个范围才算释放"(策划案§7.2.1)</summary>
    public RectTransform HandRect => handRoot as RectTransform;

    private void Awake()
    {
        ValidateRefs();

        // 悬停预览位:CardHoverPreview 会自己在 hover 上就位,这里只把 Card.prefab 递过去
        CardHoverPreview.Ensure(hoverPreviewRoot, cardPrefab);

        // 出牌机制(CP 池 + 战场落点判定)是运行时自举的:这里各拉一下,保证开局就绪 ——
        // CommandPointController 还负责开局抽 5 张,不能等第一张牌把它带出来
        _ = CommandPointController.Instance;
        _ = BattlefieldManager.Instance;

        // 敌方那边同理:牌库账(摸牌/补给)和手牌表现(只显示牌背)都在运行时自举,
        // 拉一下保证开局那 5 张牌背就位
        _ = EnemyDeckController.Instance;
        _ = EnemyHandUI.Instance;
    }

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
        // 每一张抽出来的卡上。典型事故:实例的 rarityImage 被改成了 Background,
        // 于是每抽一张卡就把整块游戏背景染成稀有度颜色。
        if (cardPrefab.gameObject.scene.IsValid())
        {
            Debug.LogError(
                $"[HandUI] cardPrefab 引用的是场景里的实例「{cardPrefab.name}」,不是 Card.prefab 资产。" +
                "请从 Project 窗口把 Prefabs/UI/Card.prefab 拖进来 —— " +
                "否则该实例上被覆盖过的字段(尤其是 rarityImage)会污染每一张抽出来的卡。",
                this);
        }

        // 战场卡面同理:留空不算错(BattlefieldManager 会按路径认领),拖成场景实例就要提醒
        if (battleCardPrefab != null && battleCardPrefab.gameObject.scene.IsValid())
        {
            Debug.LogError(
                $"[HandUI] battleCardPrefab 引用的是场景里的实例「{battleCardPrefab.name}」,不是 CardsInBattle.prefab 资产。" +
                "请从 Project 窗口把 Prefabs/UI/CardsInBattle.prefab 拖进来。",
                this);
        }

        if (handRoot == null) Debug.LogError("[HandUI] handRoot 没有赋值。", this);
        if (fanLayout == null) Debug.LogError("[HandUI] fanLayout 没有赋值:卡不会被排布,会按 Card.prefab 自己的锚点/轴心摆在屏幕左下角外面。", this);
    }

    private void OnCardDrawn(CardDrawnEventArgs e)
    {
        if (e == null || e.Card == null || e.Player == null) return;
        if (!e.Player.isLocal) return;          // 敌方手牌不显示
        if (cardPrefab == null || handRoot == null) return;

        var card = Instantiate(cardPrefab, handRoot);
        card.name = $"Card_{e.Card.cardId}";

        // 出牌交互:按住拖动。先保证组件在 —— CardDragPlay 的 OnEnable 在 Instantiate 那一刻
        // 就跑完了,那会儿卡面还没绑定(Data 是空的),它算不出"这张能不能出"
        var drag = card.GetComponent<CardDragPlay>();
        if (drag == null) drag = card.gameObject.AddComponent<CardDragPlay>();

        card.Bind(e.Card);
        spawned.Add(card);

        // 绑定完卡面数据立刻重算一次:点数够不够(§10.3)、中军满没满(§8.1.1)才算得出来。
        // 少这一步的话,开局抽上来的牌永远是正常透明度,要等到点数变化才会变灰
        if (drag != null) drag.RefreshPlayable();

        // 交给 FanLayout 的列表(它不按子物体顺序排,悬停换 sibling 也不会乱序)
        fanLayout?.Add((RectTransform)card.transform);
    }

    /// <summary>
    /// 打出一张手牌时调用:销毁卡牌、让扇形重排。
    /// 注意手牌账在 DeckController 里(打出牌要先调 DeckController.PlayCard 扣掉那张),
    /// 这里只管表现层。
    /// </summary>
    public void RemoveCard(CardDisplay card)
    {
        if (card == null) return;
        spawned.Remove(card);
        fanLayout?.Remove((RectTransform)card.transform);
        Destroy(card.gameObject);
    }

    /// <summary>
    /// 清空手牌区(重开一局用)。只清表现层 —— 真要重开还得让 DeckController
    /// 把数据层的手牌账一起清掉,不然下一局的摸牌会按上一局的手牌数算上限。
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < spawned.Count; i++)
            if (spawned[i] != null) Destroy(spawned[i].gameObject);
        spawned.Clear();
        fanLayout?.Clear();
    }
}
