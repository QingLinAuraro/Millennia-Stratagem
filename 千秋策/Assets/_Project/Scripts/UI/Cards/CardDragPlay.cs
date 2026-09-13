using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 手牌的出牌交互:按住卡牌拖出去。
///
/// 玩法(策划案§7.2.1 / §8.1.1 / §10.2):
///   · 兵种牌:拖到己方中军松手落位(§8.1.1 容量判定;后军要中军满员且驻有敌方袭扰骑兵)
///   · 策略卡:无目标 → 拖离手牌区即释放;需要目标 → 拖到目标上释放
///   · 拖回手牌区 = 取消,卡牌飞回扇形原位
/// 落不下去的卡牌自己飞回原位,并飘字说明原因(§10.3 的非法反馈:变灰 / 抖动 / 飘字)。
///
/// 挂在 HandUI 生成的手牌实例上(Card.prefab 上也有一份,HandUI 会兜底补;
/// 战场上的小卡会把本组件关掉,那些牌不能再拖)。
/// </summary>
///
/// 【挂载 & 调整】
///   挂在:Card.prefab 的根物体「Card」上(CardDisplay / CardHover 同一个物体)。
///     三处实例的运行时处理不一样,这块最容易踩坑:
///       · 手牌:HandUI 抽牌时 Instantiate,然后兜底 GetComponent / AddComponent 保证组件在;
///         因为它会自己算「这张能不能出」,所以必须挂在根物体上 —— 拖拽事件靠根物体的
///         CanvasGroup + 子物体的 Graphic 收到,拆到子物体上就收不全。
///       · 战场小卡:BattlefieldManager.SpawnUnit 直接 drag.enabled = false,
///         所以战场牌(以及排里被拖住的那些)不会再响应拖动。组件在但禁用,不要以为它坏了。
///       · 战报展示卡:BattleMessageUI 先 enabled = false 再 Destroy,彻底摘掉。
///     本组件带 [RequireComponent(typeof(CardDisplay))],根物体上必须有 CardDisplay(预制体里已有)。
///   引用:没有手连引用,全部运行时获取 —— CardDisplay / RectTransform / CanvasGroup 走 GetComponent,
///     FanLayout 走 GetComponentInParent(所以要挂在 HandArea1 底下才有扇形),
///     CardHover 走 GetComponent,CommandPointController / BattlefieldManager 走 .Instance 单例。
///     找不到的后果:display.Data 为 null → 拖不起来也不报错;fan 为 null → 不飞回扇形原位,
///     而是把「当前位置」当成家(松手就停在落点,看着像没反应),另外该变灰的牌也不会变灰;
///     两个单例为 null → 只是不做点数/落点判定,不会崩。
///   常调:
///     · dragScale:拖起来放大多少(1 = 不变)。略大于 1(1.05~1.10)手感上像把牌「捏」起来;
///       调太大卡牌会盖住落点提示;填小于 1 会让牌在拖动时反而缩小,很别扭。
///     · returnDuration:非法落点/拖回手牌区后飞回扇形要多久。0.15~0.25 干脆利落;
///       调大(>0.4)像是迟疑,但配大 shakeDuration 时更柔和;填 0 会瞬间归位、看不出动画。
///     · disabledAlpha:出不了的牌变灰的透明度(Range 0.1~1)。调小 = 更明显「我现在出不了」;
///       注意它只在拿不到 FanLayout 时才会被本组件直接用 —— 手牌扇形里这个 α 由 FanLayout.unplayableAlpha
///       统一算,所以两个值要一起改,不然改这个看不到效果。
///     · shakeDuration / shakeStrength:点已经变灰的牌时抖动的时长与幅度(像素级位移)。
///       幅度调大 = 更凶,8~12 足够,超过 20 会像牌飞出去又被拉回来;抖完必定回原位
///       (DOShakeAnchorPos 的末位参数),和飞回的补间互斥,所以改动幅度不用担心留位移。
[RequireComponent(typeof(CardDisplay))]
[DisallowMultipleComponent]
public class CardDragPlay : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    [Header("拖动表现")]
    [Tooltip("拖动时卡牌放大多少(1 = 不变)")]
    [SerializeField] private float dragScale = 1.06f;
    [Tooltip("飞回原位要多久")]
    [SerializeField] private float returnDuration = 0.18f;

    [Header("非法反馈(策划案§10.3)")]
    [Tooltip("点数不足 / 无处可放时手牌变灰的透明度")]
    [Range(0.1f, 1f)]
    [SerializeField] private float disabledAlpha = 0.5f;
    [Tooltip("点不动的时候抖一下")]
    [SerializeField] private float shakeDuration = 0.18f;
    [Tooltip("点不动时抖动的幅度(像素)。8~12 足够,调太大像牌飞出去又被拉回来;抖完会自动回原位")]
    [SerializeField] private float shakeStrength = 8f;

    private CardDisplay display;
    private RectTransform rect;
    private CanvasGroup group;
    private FanLayout fan;
    private CardHover hover;
    private BattlefieldManager battlefield;
    private CommandPointController commandPoints;

    private Vector2 grabOffset;          // 抓取点相对卡牌轴心的偏移,拖动时保持手感
    private Vector2 homePosition;        // 扇形里的家(拖回来用)
    private float homeRotation;
    private string blockReason;          // 非 null = 现在出不了,点它只能抖一下 + 飘字

    public bool IsDragging { get; private set; }

    /// <summary>现在出不了的原因(null = 可以出)</summary>
    public string BlockReason => blockReason;

    private void Awake()
    {
        display = GetComponent<CardDisplay>();
        rect = (RectTransform)transform;
        fan = GetComponentInParent<FanLayout>();
        hover = GetComponent<CardHover>();
    }

    private void OnEnable()
    {
        commandPoints = CommandPointController.Instance;
        battlefield = BattlefieldManager.Instance;

        if (commandPoints != null) commandPoints.CpChanged += OnCommandPointsChanged;
        if (battlefield != null) battlefield.BoardChanged += RefreshPlayable;

        RefreshPlayable();
    }

    private void OnDisable()
    {
        if (commandPoints != null) commandPoints.CpChanged -= OnCommandPointsChanged;
        if (battlefield != null) battlefield.BoardChanged -= RefreshPlayable;

        if (IsDragging) ReleaseDragState();
    }

    private void OnCommandPointsChanged(int cp, int cpMax) => RefreshPlayable();

    // ================================================================ 能出吗

    /// <summary>
    /// 重新算「这张牌现在出不出得了」:
    /// 先看点数够不够(§10.3),再看兵种牌有没有地方落(§8.1.1 中军满员)。
    /// 出不了就变灰,点它的时候抖动 + 飘字。
    /// </summary>
    public void RefreshPlayable()
    {
        if (display == null || display.Data == null) { blockReason = null; return; }

        var card = display.Data;
        string reason = null;

        if (commandPoints != null && !commandPoints.CanAfford(card.deploymentCost))
        {
            reason = $"指挥点不足（{commandPoints.Cp}/{card.deploymentCost}）";
        }
        else if (card.IsUnitCard && battlefield != null && !battlefield.CanDeployUnitAnywhere(card, out string deployReason))
        {
            reason = deployReason;      // 例如「中军已满，无法部署」(§8.1.1)
        }

        blockReason = reason;

        bool dim = reason != null;
        if (fan != null) fan.SetDimmed(rect, dim);           // 手牌的透明度统一由 FanLayout 算
        else SetAlpha(dim ? disabledAlpha : 1f);
    }

    private void SetAlpha(float alpha)
    {
        var g = GetComponent<CanvasGroup>();
        if (g == null)
        {
            if (Mathf.Approximately(alpha, 1f)) return;
            g = gameObject.AddComponent<CanvasGroup>();
        }
        g.alpha = alpha;
    }

    // ================================================================ 拖动

    public void OnPointerDown(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;
        if (blockReason == null) return;

        // §10.3:点数不足 → 卡牌变灰 + 抖动 + 飘字;无处可落 → 飘字说明(中军已满)
        rect.DOKill();      // 抖完要回到原位,和飞回的补间不能撞车
        rect.DOShakeAnchorPos(shakeDuration, shakeStrength, 12, 90f, false, true);
        FloatingTipUI.Show(eventData.position, blockReason, warning: true);
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left) return;
        if (display == null || display.Data == null) return;
        if (IsDragging) return;

        IsDragging = true;

        // 家在扇形里的哪儿(拖动取消要飞回去)
        if (fan == null || !fan.TryGetHome(rect, out homePosition, out homeRotation))
        {
            homePosition = rect.anchoredPosition;
            homeRotation = rect.localEulerAngles.z;
        }

        // 抓哪儿拖哪儿,牌不会突然跳到指针中心
        var parent = rect.parent as RectTransform;
        if (parent != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent, eventData.position, eventData.pressEventCamera, out var pressLocal))
            grabOffset = rect.anchoredPosition - pressLocal;
        else
            grabOffset = Vector2.zero;

        // 拖动期间:扇形重排先放过它、悬停预览关掉、别让它自己挡住落点判定
        fan?.SetHeld(rect, true);
        if (hover != null) { hover.SetSuppressed(true); hover.CancelPreview(); }

        group = EnsureGroup();
        group.blocksRaycasts = false;

        rect.DOKill();
        rect.SetAsLastSibling();
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one * (fan != null ? fan.CardScale * dragScale : dragScale);

        // §10.2:拖起来的时候把能落的排高亮出来
        battlefield?.HighlightDropTargets(display.Data);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!IsDragging || eventData == null) return;

        var parent = rect.parent as RectTransform;
        if (parent != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent, eventData.position, eventData.pressEventCamera, out var local))
            rect.anchoredPosition = local + grabOffset;

        battlefield?.UpdatePointerHighlight(display.Data, eventData);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (!IsDragging) return;

        Vector2 tipPosition = eventData != null ? eventData.position : (Vector2)rect.position;
        var result = CardPlayResult.Cancelled;
        string message = null;

        if (battlefield != null && eventData != null)
            result = battlefield.TryResolveDrop(display, eventData, out message);

        battlefield?.ClearHighlights();

        if (result == CardPlayResult.Deployed || result == CardPlayResult.Released)
        {
            // 这张手牌已经被 HandUI 收走了(销毁在帧末),这里只飘字,别再碰它
            IsDragging = false;
            if (!string.IsNullOrEmpty(message)) FloatingTipUI.Show(tipPosition, message);
            return;
        }

        ReturnHome();

        if (result == CardPlayResult.Failed && !string.IsNullOrEmpty(message))
            FloatingTipUI.Show(tipPosition, message, warning: true);
    }

    /// <summary>飞回扇形原位,并把拖动期间改过的东西还回去</summary>
    private void ReturnHome()
    {
        IsDragging = false;

        // 拖动期间可能又抽了牌,扇形重排过 —— 家要按最新的拿
        if (fan != null && fan.TryGetHome(rect, out var home, out float rot))
        {
            homePosition = home;
            homeRotation = rot;
        }

        ReleaseDragState();

        rect.DOKill();
        rect.DOAnchorPos(homePosition, returnDuration).SetEase(Ease.OutQuad);
        rect.DOLocalRotate(new Vector3(0f, 0f, homeRotation), returnDuration).SetEase(Ease.OutQuad);
        rect.DOScale(fan != null ? fan.CardScale : 1f, returnDuration).SetEase(Ease.OutQuad)
            // 拖动期间这张牌被提到最上层(SetAsLastSibling),飞回来以后要把图层还给扇形 ——
            // 否则它会一直压在其他手牌上面,挡住旁边那几张的卡面和费用
            .OnComplete(() => fan?.RestoreRenderOrder());
    }

    /// <summary>把手牌交还给扇形、恢复射线、恢复悬停预览(销毁时也走这里)</summary>
    private void ReleaseDragState()
    {
        IsDragging = false;

        fan?.SetHeld(rect, false);
        if (hover != null) hover.SetSuppressed(false);
        if (group != null) group.blocksRaycasts = true;
    }

    private CanvasGroup EnsureGroup()
    {
        if (group == null) group = GetComponent<CanvasGroup>();
        if (group == null) group = gameObject.AddComponent<CanvasGroup>();
        return group;
    }
}
