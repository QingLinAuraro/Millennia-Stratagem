using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 对方出牌展示:把对方打出的策略牌**整张卡**显示在 ShowMessage 上,停留 3 秒后淡出。
///
/// 挂在场景里那个空的 ShowMessage 上(BattleCanvas 下面,左侧 300x400 —— 卡牌 150x200,
/// cardScale 填 2 正好铺满这块地方,默认 1.5 留点边)。
///
/// 为什么直接摆一张 Card.prefab 而不是写一行文字:
///   卡面本身就带着费用、卡名、效果、数值,信息比一行文案全,也不会因为效果描述太长而挤成一团。
///
/// 展示用的这张牌是"只看不能碰"的:CardDragPlay / CardHover 会被摘掉、所有 Graphic 关掉射线 ——
/// 否则对方打出的牌能被拖到场上。
///
/// 消息从哪来:EventManager 的 CardPlayed 事件(策划案§3.3.2)。
/// 敌方出牌走 EnemyDeckController.PlayCard,我方出牌走 BattlefieldManager.ConsumeHandCard,两边都会广播。
///
/// 【挂载 & 调整】
///   挂在:BattleCanvas/ShowMessage 上 —— 场景里已经挂好。ShowMessage 是个 300x400 的空 RectTransform,
///         锚在画布中心、位置 x = -550(屏幕左侧),它自己就是展示位,不用再建物体。
///   引用:messageRoot 和 cardPrefab 场景里都已经连好(前者是 ShowMessage 自己,后者是 Prefabs/UI/Card.prefab),现在不用动。
///         · messageRoot 留空 → 先 GameObject.Find(MessageObjectName)(常量「ShowMessage」,区分大小写),
///           再不行就用自己的 transform,所以只要脚本挂在这个展示位上,引用空着也能跑;
///           如果 ShowMessage 被删掉而脚本还挂在别处,就会拿脚本所在的物体当展示位,牌会摆到错的地方。
///         · cardPrefab 留空 → 找场景里的 HandUI,借它的 CardPrefab(就是 HandUI 上拖的那张 Card.prefab,少一处要维护的引用);
///           两个都拿不到只会警告(ResolveRefs 里报「找不到卡牌预制体」,Show 时再报「缺 messageRoot 或 cardPrefab」),
///           之后对方出牌什么都不显示(不报错也不崩)。
///   常调:
///     · cardScale(默认 1.5):展示牌的缩放。展示位 300x400、卡牌 150x200,填 2 正好铺满(会顶到边),1.5 留点边。
///       调大 = 卡面字更清楚,但更容易盖住两侧的战场和手牌;调到 1 以下卡面文字就看不清了。以后改展示位大小时按这个比例一起调。
///     · cardOffset(默认 0,0):相对展示位中心的偏移。只想整体挪位置,优先改 ShowMessage 自己的锚点和位置,这里留着做微调。
///     · holdSeconds(默认 3 秒):停留时长(不含淡入淡出)。调大 = 玩家有更多时间读卡面,但挡住视野更久;
///       调到 0.5 以下基本来不及看。整段时长 = 淡入 + 停留 + 淡出,和下面两个一起考虑。
///     · fadeInDuration / fadeOutDuration(默认 0.15 / 0.5):淡入、淡出时长。调大更柔和但整段更长;
///       淡入调 0 = 直接跳出来(比较硬),淡出调 0 = 瞬间消失(会显得突兀)。
///     · enemyOnly(默认开):只展示对方打出的牌。关掉 = 自己打出的牌也整张弹一遍(自己刚点过的牌再看一次,通常没必要)。
///       注意:出牌事件里拿不到出牌人(null)时,这个开关打开会当成「不是我方」不展示;关掉之后这种没主的牌也会弹出来。
///     · tacticOnly(默认开):只展示策略牌。关掉 = 对方部署的兵牌也弹一张大图(排上本来就能看到兵牌,重复而且更挡视线)。
///     · 右键菜单「测试:展示一张牌」:不用改参数。Play 模式下在组件右上角菜单点一下,会拿敌方手牌里第一张策略牌试播;
///       敌方手牌为空或没有策略牌时只警告(所以要先进 Play 跑一会儿,等敌方摸到牌)。
/// </summary>
public class BattleMessageUI : MonoBehaviour
{
    /// <summary>展示位物体名(场景里那个空物体)</summary>
    public const string MessageObjectName = "ShowMessage";

    private static BattleMessageUI instance;

    /// <summary>
    /// 场景里那一个。想展示别的牌可以直接 BattleMessageUI.Instance.Show(card)。
    /// 注意:它没有自举分支 —— Instance 只在 Awake 里赋值,场景里没挂这个组件时 Instance 就是 null,
    /// 调用方(BattlefieldManager / EnemyDeckController 等)记得判空。
    /// </summary>
    public static BattleMessageUI Instance => instance;

    [Header("引用(留空则按名字找 ShowMessage)")]
    [Tooltip("展示位(场景物体 ShowMessage)。留空则按名字找 ShowMessage,再不行就退而用自己所在的 transform")]
    [SerializeField] private RectTransform messageRoot;
    [Tooltip("要展示的卡牌预制体。留空则借 HandUI 的 Card.prefab")]
    [SerializeField] private CardDisplay cardPrefab;

    [Header("展示哪些牌")]
    [Tooltip("只展示对方打出的牌(我方出牌不展示)")]
    [SerializeField] private bool enemyOnly = true;
    [Tooltip("只展示策略牌。勾掉的话对方部署的兵牌也会展示一遍")]
    [SerializeField] private bool tacticOnly = true;

    [Header("卡牌摆放")]
    [Tooltip("展示用的缩放。展示位 300x400、卡牌 150x200:填 2 正好铺满,默认 1.5 留边")]
    [SerializeField] private float cardScale = 1.5f;
    [Tooltip("相对展示位中心的偏移")]
    [SerializeField] private Vector2 cardOffset = Vector2.zero;

    [Header("时长")]
    [Tooltip("停留几秒(不含淡入淡出)")]
    [SerializeField] private float holdSeconds = 3f;
    [Tooltip("淡入时长(秒),0 = 直接出现。整段时长 = 淡入 + 停留 + 淡出")]
    [SerializeField] private float fadeInDuration = 0.15f;
    [Tooltip("淡出时长(秒)。调大收得更柔和,但整段展示也更久")]
    [SerializeField] private float fadeOutDuration = 0.5f;

    /// <summary>正在播的那个序列(再来一张时先掐掉旧的)</summary>
    private Sequence sequence;

    /// <summary>当前展示着的卡牌实例</summary>
    private CardDisplay shownCard;

    private void Awake()
    {
        instance = this;
        ResolveRefs();
        HideImmediately();
    }

    private void OnEnable()
    {
        EventManager.Subscribe<CardPlayedEventArgs>(GameEventType.CardPlayed, OnCardPlayed);
    }

    private void OnDisable()
    {
        EventManager.Unsubscribe<CardPlayedEventArgs>(GameEventType.CardPlayed, OnCardPlayed);
    }

    private void OnDestroy()
    {
        sequence?.Kill();
        if (instance == this) instance = null;
    }

    // ================================================================ 出牌 → 展示

    private void OnCardPlayed(CardPlayedEventArgs e)
    {
        if (e == null || e.Card == null) return;

        // enemyOnly:拿不到出牌人(null)就当成不是我方,不展示 —— 宁可不弹也别把我方的牌说成对方的
        if (enemyOnly && (e.Player == null || e.Player.isLocal)) return;
        if (tacticOnly && e.Card.cardType != CardType.Tactic) return;

        Show(e.Card);
    }

    // ================================================================ 展示 / 淡出

    /// <summary>把一张牌摊出来给玩家看:淡入 → 停 holdSeconds 秒 → 淡出。重复调用会掐掉上一张</summary>
    public void Show(CardData card)
    {
        if (card == null) return;

        ResolveRefs();
        if (messageRoot == null || cardPrefab == null)
        {
            Debug.LogWarning("[BattleMessageUI] 缺 messageRoot 或 cardPrefab,这张牌展示不出来。", this);
            return;
        }

        ClearCard();

        var display = Instantiate(cardPrefab, messageRoot);
        display.name = $"ShownCard_{card.cardId}";
        display.Bind(card);         // 卡面:费用 / 卡名 / 效果 / 数值,和手牌里那张一模一样
        shownCard = display;

        LayoutCard((RectTransform)display.transform);
        var cardGroup = MakeNonInteractive(display);

        // 淡入淡出挂在卡牌自己的 CanvasGroup 上,不能挂在 ShowMessage 上:
        // Card.prefab 的根节点自带一个 Canvas,是嵌套画布,父级 CanvasGroup 的 alpha 管不到它。
        if (cardGroup == null) return;

        sequence?.Kill();
        cardGroup.alpha = 0f;

        sequence = DOTween.Sequence();
        sequence.Append(cardGroup.DOFade(1f, fadeInDuration).SetEase(Ease.OutQuad));
        sequence.AppendInterval(holdSeconds);
        sequence.Append(cardGroup.DOFade(0f, fadeOutDuration).SetEase(Ease.InQuad));
        sequence.OnComplete(ClearCard);
    }

    /// <summary>立刻收起(不播淡出)</summary>
    public void HideImmediately()
    {
        sequence?.Kill();
        sequence = null;
        ClearCard();
    }

    /// <summary>把展示用的那张牌销毁掉</summary>
    private void ClearCard()
    {
        if (shownCard == null) return;

        Destroy(shownCard.gameObject);
        shownCard = null;
    }

    /// <summary>
    /// 摆正 Card.prefab:它的根节点是"父物体左下角的锚点 + 顶边轴心 + 3228 的残留位置",
    /// 直接摆会跑到屏幕外面去,所以这里全部按"展示位正中间"重设。
    /// </summary>
    private void LayoutCard(RectTransform rt)
    {
        if (rt == null) return;

        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = cardOffset;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one * Mathf.Max(0.01f, cardScale);
    }

    /// <summary>
    /// 摘掉交互:这张牌只是给人看的。
    /// 不摘的话 CardDragPlay 会把它当手牌 —— 玩家能把对方打出的牌拖到自己场上(还不用付费用)。
    /// 返回卡牌自己的 CanvasGroup(顺带给它盖上,淡入淡出也用它)。
    /// </summary>
    private CanvasGroup MakeNonInteractive(CardDisplay display)
    {
        if (display == null) return null;

        var drag = display.GetComponent<CardDragPlay>();
        if (drag != null) { drag.enabled = false; Destroy(drag); }

        var hover = display.GetComponent<CardHover>();
        if (hover != null) { hover.enabled = false; Destroy(hover); }

        foreach (var graphic in display.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;

        var group = display.GetComponent<CanvasGroup>();
        if (group == null) group = display.gameObject.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;
        return group;
    }

    [ContextMenu("测试:展示一张牌")]
    private void DebugShow()
    {
        var deck = EnemyDeckController.Instance;
        var cards = deck != null ? deck.HandCards : null;
        if (cards != null)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] != null && cards[i].cardType == CardType.Tactic) { Show(cards[i]); return; }
            }
        }

        Debug.LogWarning("[BattleMessageUI] 敌方手牌里没有策略牌可展示(要在 Play 模式下测)。", this);
    }

    // ================================================================ 引用

    private void ResolveRefs()
    {
        if (messageRoot == null)
        {
            var go = GameObject.Find(MessageObjectName);
            if (go != null) messageRoot = go.transform as RectTransform;
        }

        if (messageRoot == null) messageRoot = transform as RectTransform;

        if (cardPrefab == null)
        {
            // 借手牌那份 Card.prefab(HandUI 上拖的就是它),少一处要维护的引用
            var hand = FindObjectOfType<HandUI>();
            if (hand != null) cardPrefab = hand.CardPrefab;
        }

        if (cardPrefab == null)
            Debug.LogWarning("[BattleMessageUI] 找不到卡牌预制体(Card.prefab),对方出牌展示不出来。", this);
    }
}
