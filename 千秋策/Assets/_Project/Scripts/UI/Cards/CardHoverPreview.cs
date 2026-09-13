using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

/// <summary>
/// 悬停预览:把鼠标停留够久的那张手牌,放大成 previewSize(默认 300x400)显示在这儿。
/// 这个组件挂在场景里的 hover 物体上 —— hover 摆在哪,预览就出现在哪。
///
/// 尺寸是怎么来的:Card.prefab 是 150x200,里面的字号、插画、费用格全是按这个尺寸标死的
/// 绝对像素,把 rect 直接改成 300x400 只会把留白撑大、字还是那么小。所以这里让实例保持
/// 150x200、把 transform 整体缩放 previewSize / 150 —— 外观正好 300x400,内容严格同比例放大。
///
/// 它只管显示:不认识手牌、不认识扇形,谁悬停谁调用 Show/Hide。
/// </summary>
///
/// 【挂载 & 调整】
///   挂在:场景 Battle.unity 里的「hover」物体上(300x400 的那一格,Canvas 下、HandUI 的兄弟节点)。
///     它是【运行时加上的】,场景和 Card.prefab 里都没有烘:HandUI.Awake 调
///     CardHoverPreview.Ensure(hoverPreviewRoot, cardPrefab) —— hoverPreviewRoot 就是场景里连好的 hover;
///     若那个引用留空,Ensure 会退化成 GameObject.Find(「hover」/「Hover」)按名字找。
///     找到之后才 AddComponent<CardHoverPreview>(找不到就 LogWarning 并返回 null,悬停预览整条链失效),
///     顺便把 Card.prefab 注入进 cardPrefab。所以:想在 Inspector 里手改这几个参数,
///     要么先在编辑器里手动把本组件加到 hover 上(Ensure 之后就直接用这一份,不会重复加),
///     要么改完记下来,因为运行时不落盘、退出 Play 模式就没了。
///     注意 hover 上原本只有一个 Image(全透明底板)且 raycastTarget 为 1,Awake 会把它改成 false ——
///     这一格只是落点,不接点击;想让底板吃射线要改回代码,别只在 Inspector 里勾。
///   引用:cardPrefab 是唯一手连项,而且留空不会出事 —— HandUI 开局会注入。
///     真正的失败点在 Show 时:cardPrefab 为 null → LogError「cardPrefab(Card.prefab)没有赋值」,
///     预览不显示(手牌悬停本身照常)。它必须是 Project 里的 Card.prefab 资产,不能拖场景实例
///     (和 HandUI 的 cardPrefab 同一个坑:实例上被覆盖的字段会跟着复制)。
///   常调:
///     · previewSize:预览卡片显示尺寸(默认 300x400)。实现只按【宽度】算缩放 previewSize.x / 150,
///       所以想稳妥就保持 3:4 的比例改数值;不成比例(比如 320x400)会 LogWarning 且高度不是你要的那个
///       (只按 x 放大)。调小 = 不挡战场;调大 = 看得清效果文案,但会盖住更多手牌。
///     · fadeDuration:淡入淡出时长,0 = 直接切换。0.08~0.15 最自然;调大到 0.3 以上会有明显残影感,
///       调成 0 则每次收起都是硬切。
///     · hideBackdropWhenIdle:收起预览时把 hover 上的底板 Image 一起关掉(默认开)。底板如果被你配成了
///       卡框底衬,才需要取消勾选让它常显 —— 常显之后它就在 hover 那一格一直画着,别配成不透明图。
///     · 「谁在什么时候拉起预览」不在本文件:CardHover.previewDelay 决定停留多久弹,CardHover 弹之前会先
///       问 host.Show(data),Show 返回 false(Card.prefab 没注入成功)就不弹,而且每帧都会重试
///       —— 所以那个 LogError 会一直刷,顺手也说明 HandUI 的 cardPrefab 没连上。
///     · 预览卡不参与交互:EnsureCard 会把预览卡上所有 Graphic 的 raycastTarget 关掉,
///       并禁用预览卡自己的 CardHover 与 CardDragPlay —— 所以预览卡不能拖、也不会自己再弹一层预览。
[DisallowMultipleComponent]
public class CardHoverPreview : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("Card.prefab。留空时由 HandUI 在运行时注入")]
    [SerializeField] private CardDisplay cardPrefab;

    [Header("显示")]
    [Tooltip("预览卡片的显示尺寸(px)。Card.prefab 是 150x200,内容按宽的比例整体放大")]
    [SerializeField] private Vector2 previewSize = new Vector2(300f, 400f);
    [Tooltip("淡入淡出时长(秒),0 = 直接切换")]
    [SerializeField] private float fadeDuration = 0.1f;
    [Tooltip("没在预览的时候把底板(这个物体上的 Image)一起关掉。取消勾选 = 底板常显")]
    [SerializeField] private bool hideBackdropWhenIdle = true;

    /// <summary>场景里唯一的预览位。HandUI 开局会 Ensure 一个出来</summary>
    public static CardHoverPreview Instance { get; private set; }

    private CardDisplay previewCard;
    private CanvasGroup cardGroup;      // 淡入淡出用,只作用在预览卡上
    private Image backdrop;
    private Tween fade;

    public bool IsShowing => previewCard != null && previewCard.gameObject.activeSelf;

    /// <summary>
    /// HandUI 开局调用:场景里没有预览位就在 hover 上补一个,并把 Card.prefab 注入进去。
    /// 找不到 hover 就返回 null(此时悬停不会弹预览,但手牌一切照常)。
    /// </summary>
    public static CardHoverPreview Ensure(RectTransform root, CardDisplay prefab)
    {
        if (Instance != null)
        {
            if (prefab != null && Instance.cardPrefab == null) Instance.cardPrefab = prefab;
            return Instance;
        }

        if (root == null) root = FindHoverRoot();
        if (root == null)
        {
            Debug.LogWarning("[CardHoverPreview] 场景里找不到 hover 物体,悬停预览不会显示。");
            return null;
        }

        var host = root.GetComponent<CardHoverPreview>();
        if (host == null) host = root.gameObject.AddComponent<CardHoverPreview>();
        if (prefab != null) host.cardPrefab = prefab;
        return host;
    }

    // 备用方案:HandUI 的 hoverPreviewRoot 没拖时按名字找(hover 就是场景里留的那一格)
    private static RectTransform FindHoverRoot()
    {
        var go = GameObject.Find("hover");
        if (go == null) go = GameObject.Find("Hover");
        return go != null ? go.transform as RectTransform : null;
    }

    private void Awake()
    {
        Instance = this;

        // 这一格只是落点,不是按钮:不能挡住后面的战场和 UI
        backdrop = GetComponent<Image>();
        if (backdrop != null) backdrop.raycastTarget = false;

        HideImmediate();
    }

    private void OnDestroy()
    {
        fade?.Kill();
        if (Instance == this) Instance = null;
    }

    /// <summary>把这张牌放大显示在 hover 的位置。失败(没配 Card.prefab 等)返回 false</summary>
    public bool Show(CardData data)
    {
        if (data == null) return false;
        if (!EnsureCard()) return false;

        previewCard.Bind(data, CardViewMode.Hand);      // 手牌布局:部署费用 + K + 行动费用都显示
        previewCard.gameObject.SetActive(true);
        if (backdrop != null) backdrop.enabled = true;

        fade?.Kill();
        if (fadeDuration > 0f)
        {
            cardGroup.alpha = 0f;                       // 从透明淡进来
            fade = cardGroup.DOFade(1f, fadeDuration).SetEase(Ease.OutQuad);
        }
        else cardGroup.alpha = 1f;

        return true;
    }

    public void Hide()
    {
        if (!IsShowing) return;

        fade?.Kill();
        if (fadeDuration > 0f)
            // 淡出结束再关物体,省得一直白白渲染;Kill 掉的 tween 不走 OnComplete,
            // 所以淡出途中又 Show 一次不会被这个回调顺手关掉
            fade = cardGroup.DOFade(0f, fadeDuration).SetEase(Ease.InQuad).OnComplete(HideImmediate);
        else
            HideImmediate();
    }

    private void HideImmediate()
    {
        if (previewCard != null) previewCard.gameObject.SetActive(false);
        if (backdrop != null) backdrop.enabled = !hideBackdropWhenIdle;
    }

    // ===== 预览卡实例 =====
    private bool EnsureCard()
    {
        if (previewCard != null) return true;

        if (cardPrefab == null)
        {
            Debug.LogError("[CardHoverPreview] cardPrefab(Card.prefab)没有赋值,悬停预览不会显示。", this);
            return false;
        }

        previewCard = Instantiate(cardPrefab, transform);
        previewCard.name = "HoverPreviewCard";

        // 预览不参与交互:鼠标必须一直留在手牌上,否则一移开预览就没了
        var graphics = previewCard.GetComponentsInChildren<Graphic>(true);
        for (int i = 0; i < graphics.Length; i++) graphics[i].raycastTarget = false;

        cardGroup = previewCard.GetComponent<CanvasGroup>();
        if (cardGroup == null) cardGroup = previewCard.gameObject.AddComponent<CanvasGroup>();
        cardGroup.interactable = false;
        cardGroup.blocksRaycasts = false;

        // 预览卡自己不再响应悬停(它身上就带着 CardHover)
        var hover = previewCard.GetComponent<CardHover>();
        if (hover != null) hover.enabled = false;

        // 也不响应拖动出牌(预览卡是拿来"看"的,不是手牌)
        var drag = previewCard.GetComponent<CardDragPlay>();
        if (drag != null) drag.enabled = false;

        LayoutCard((RectTransform)previewCard.transform, (RectTransform)cardPrefab.transform);
        return true;
    }

    /// <summary>把卡实例摆成"正好铺满 hover 这一格":锚点居中、保持预制体尺寸、整体缩放</summary>
    private void LayoutCard(RectTransform rt, RectTransform prefabRt)
    {
        // 预制体还没实例化时 rect 可能没算出来,优先用 sizeDelta
        Vector2 baseSize = prefabRt.sizeDelta;
        if (baseSize.x <= 0f || baseSize.y <= 0f) baseSize = prefabRt.rect.size;
        if (baseSize.x <= 0f || baseSize.y <= 0f) baseSize = new Vector2(150f, 200f);

        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.localRotation = Quaternion.identity;
        rt.sizeDelta = baseSize;                        // 还是 150x200,靠缩放放大

        float scale = previewSize.x / baseSize.x;       // 150 → 300 就是 2 倍,内容一起放大
        rt.localScale = new Vector3(scale, scale, 1f);

        if (previewSize.y > 0f && !Mathf.Approximately(scale, previewSize.y / baseSize.y))
            Debug.LogWarning(
                $"[CardHoverPreview] previewSize {previewSize} 和卡牌原始尺寸 {baseSize} 的比例不一致," +
                $"已按宽度缩放到 {scale:0.##} 倍,实际高度是 {baseSize.y * scale:0.#}px。", this);
    }

    [Header("编辑器测试")]
    [SerializeField] private CardData testData;

    [ContextMenu("试弹一次预览")]
    private void TestShow() { if (testData != null) Show(testData); }

    [ContextMenu("收起预览")]
    private void TestHide() => Hide();
}
