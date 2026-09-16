using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

/// <summary>
/// 悬停预览:把鼠标停留够久的那张牌,按手牌布局放大成 previewSize(默认 300x400)显示在这儿。
/// 这个组件挂在场景里的 hover 物体上 —— hover 的父物体决定预览在哪个坐标系里摆。
///
/// 尺寸是怎么来的:Card.prefab 是 150x200,里面的字号、插画、费用格全是按这个尺寸标死的
/// 绝对像素,把 rect 直接改成 300x400 只会把留白撑大、字还是那么小。所以这里让实例保持
/// 150x200、把 transform 整体缩放 previewSize / 150 —— 外观正好 300x400,内容严格同比例放大。
///
/// 位置由调用方给"要点":CardHover 会传被悬停那张牌的 RectTransform 和它的视觉尺寸,
/// PlaceNear 负责算出这张牌左上角的**屏幕中点**,再按中轴决定卡片弹在牌的上方偏左还是偏右:
///   · 手牌:牌本来就在屏幕中下部,预览一律弹在它的正上方(不会盖住扇形)。
///   · 战场:同一套规则 —— 中轴左边的牌弹在右边、右边的牌弹在左边、压在中轴上的弹在右边。
/// 预览自己不认识手牌、不认识扇形、也不认识战场,谁悬停谁调用 Show/Hide。
/// </summary>
///
/// 【挂载 & 调整】
///   挂在:场景 Battle.unity 里的「hover」物体上(300x400 的那一格,Canvas 下、HandUI 的兄弟节点)。
///     它是【运行时加上的】,场景和两份卡面预制体里都没有烘:HandUI.Awake 调
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
///     预览不显示(手牌/战场悬停本身照常)。它必须是 Project 里的 Card.prefab 资产,不能拖场景实例
///     (和 HandUI 的 cardPrefab 同一个坑:实例上被覆盖的字段会跟着复制)。
///     战场卡面用的是另一份 CardsInBattle.prefab,那是"被看的那张牌"的样子,和这里的预览卡无关。
///   常调:
///     · previewSize:预览卡片显示尺寸(默认 300x400)。实现只按【宽度】算缩放 previewSize.x / 150,
///       所以想稳妥就保持 3:4 的比例改数值;不成比例(比如 320x400)会 LogWarning 且高度不是你要的那个
///       (只按 x 放大)。调小 = 不挡战场;调大 = 看得清效果文案,但会盖住更多手牌。
///     · gapAboveCard:预览下沿离被悬停卡牌顶边多少像素 —— **实际用的是 CardHover.previewOffset**,
///       这里只是兜底默认值(调用方没给偏移时才用它)。
///     · axisBandWidth:中轴死区的半宽(占屏幕宽度的比例,默认 0.02 = 两侧各 2%)。
///       判定用的是【卡片左上角的屏幕中点】而不是整张牌的宽度:手牌扇形是斜的、战场卡还带缩放,
///       用小方框的角点当锚更稳。死区内的牌算"压在中轴上",按需求一律弹在右边。
///       调大 = 更多牌被判成中轴牌(一律弹右边);调小 = 更早按左右分边。
///     · sideMargin:预览卡片离屏幕左右边至少留多少像素(默认 12),免得贴边被切掉。
///     · fadeDuration:淡入淡出时长,0 = 直接切换。0.08~0.15 最自然;调大到 0.3 以上会有明显残影感。
///     · hideBackdropWhenIdle:收起预览时把 hover 上的底板 Image 一起关掉(默认开)。底板如果被你配成了
///       卡框底衬,才需要取消勾选让它常显 —— 常显之后它就在 hover 那一格一直画着,别配成不透明图。
///     · 「谁在什么时候拉起预览」不在本文件:CardHover.previewDelay(手牌)/ fieldPreviewDelay(战场)。
///       弹之前会先问 host.Show(...),Show 返回 false(Card.prefab 没注入成功)就不弹;
///       配错时每帧都会重试 —— 所以那个 LogError 会一直刷,顺手也说明 HandUI 的 cardPrefab 没连上。
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

    [Header("落点")]
    [Tooltip("调用方没给偏移时,预览下沿离卡牌顶边多少像素")]
    [SerializeField] private float gapAboveCard = 8f;
    [Range(0f, 0.2f)]
    [Tooltip("中轴死区的半宽(占屏幕宽度的比例)。锚点落在这条带子里 = 算压在中轴上,一律弹在右边")]
    [SerializeField] private float axisBandWidth = 0.02f;
    [Tooltip("预览卡片离屏幕左右边至少留多少像素")]
    [SerializeField] private float sideMargin = 12f;
    [Tooltip("中轴左侧的牌把预览弹在右边(按设计:左卡看右、右卡看左)。取消勾选就是反过来")]
    [SerializeField] private bool leftCardsShowOnRight = true;

    /// <summary>场景里唯一的预览位。HandUI 开局会 Ensure 一个出来</summary>
    public static CardHoverPreview Instance { get; private set; }

    private CardDisplay previewCard;
    private CanvasGroup cardGroup;      // 淡入淡出用,只作用在预览卡上
    private Image backdrop;
    private Tween fade;

    public bool IsShowing => previewCard != null && previewCard.gameObject.activeSelf;

    /// <summary>
    /// HandUI 开局调用:场景里没有预览位就在 hover 上补一个,并把 Card.prefab 注入进去。
    /// 找不到 hover 就返回 null(此时悬停不会弹预览,但手牌/战场一切照常)。
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

    /// <summary>
    /// 把这张牌放大显示出来,落在 focusedCard 的上方(按中轴分左右)。
    /// 老的调用方式(手牌 hover 根上固定那一格)仍然可用 —— 见下面那个两参数重载。
    /// </summary>
    /// <param name="data">要展示的牌</param>
    /// <param name="mode">被悬停的牌是手牌还是战场卡(只影响落点规则)</param>
    /// <param name="focusedCard">被悬停那张牌的 RectTransform</param>
    /// <param name="focusedVisualSize">被悬停那张牌的视觉尺寸(已乘 localScale)</param>
    /// <param name="gap">预览下沿离卡牌顶边多少像素;小于 0 表示用面板上的 gapAboveCard</param>
    public bool Show(CardData data, CardViewMode mode, RectTransform focusedCard,
                     Vector2 focusedVisualSize, float gap = -1f)
    {
        if (data == null) return false;
        if (!EnsureCard()) return false;

        previewCard.Bind(data, CardViewMode.Hand);      // 预览一律用完整手牌布局:
        // 部署费用 + K + 行动费用 + 卡名/朝代/关键词/效果/兵种/攻血/稀有度小方框全都在
        previewCard.gameObject.SetActive(true);
        if (backdrop != null) backdrop.enabled = true;

        PlaceNear(mode, focusedCard, focusedVisualSize, gap);

        fade?.Kill();
        if (fadeDuration > 0f)
        {
            cardGroup.alpha = 0f;                       // 从透明淡进来
            fade = cardGroup.DOFade(1f, fadeDuration).SetEase(Ease.OutQuad);
        }
        else cardGroup.alpha = 1f;

        return true;
    }

    /// <summary>老的调用方式:只给数据,预览就落在 hover 自己那一格(锚点居中、不重排)</summary>
    public bool Show(CardData data)
    {
        if (!Show(data, CardViewMode.Hand, null, Vector2.zero)) return false;

        var rt = (RectTransform)transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        return true;
    }

    /// <summary>战场卡的预览:把场上**当前**的攻/血刷到预览卡上(吃过 buff / 挨过打要看得出来)</summary>
    public void ShowUnitStats(int currentATK, int currentHP, int maxHP)
    {
        if (previewCard == null || previewCard.Data == null) return;
        previewCard.ShowPreviewStats(currentATK, currentHP, previewCard.Data.atk, maxHP);
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

    // ================================================================ 落点

    /// <summary>
    /// 预览卡片的视觉尺寸:Card.prefab 的 rect 尺寸 × 本组件的缩放。
    /// 预览一律按宽度缩放(见 LayoutCard),所以高度 = 宽度 / 预制体宽高比。
    /// </summary>
    private Vector2 PreviewCardSize()
    {
        Vector2 baseSize = new Vector2(150f, 200f);
        if (cardPrefab != null)
        {
            baseSize = cardPrefab.transform is RectTransform prt ? prt.rect.size : baseSize;
            if (baseSize.x <= 0f || baseSize.y <= 0f) baseSize = new Vector2(150f, 200f);
        }

        float width = previewSize.x > 0f ? previewSize.x : baseSize.x;
        float height = width * (baseSize.y / baseSize.x);
        return new Vector2(width, height);
    }

    /// <summary>
    /// 把预览摆到 focusedCard 的上方:
    ///   锚点 = 被悬停那张牌的【左上角】屏幕中点(手牌是斜的、战场卡还带缩放,角点比整张牌的中线更稳);
    ///   左右 = 锚点在中轴左侧 → 预览向右展开;右侧 → 向左展开;压在中轴上 → 向右展开。
    ///   垂直 = 预览下沿贴在被悬停牌顶边上方 gap 像素。
    /// </summary>
    private void PlaceNear(CardViewMode mode, RectTransform focusedCard, Vector2 focusedVisualSize, float gap)
    {
        var rt = (RectTransform)transform;

        if (focusedCard == null)
        {
            // 没给牌就没法算落点:保持预制体烘好的位置
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            return;
        }

        // 被悬停那张牌的视觉尺寸:x<=0 说明调用方没给,退回它的 rect 尺寸
        if (focusedVisualSize.x <= 0f || focusedVisualSize.y <= 0f)
        {
            var raw = focusedCard.rect.size;
            if (raw.x <= 0f || raw.y <= 0f) raw = new Vector2(150f, 200f);
            float scale = Mathf.Abs(focusedCard.lossyScale.x);
            focusedVisualSize = raw * (scale > 0.0001f ? scale : 1f);
        }

        var corners = new Vector3[4];
        focusedCard.GetWorldCorners(corners);

        var rootCanvas = CanvasUtil.FindRootCanvas();
        Camera camera = rootCanvas != null && rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? rootCanvas.worldCamera
            : null;

        // 左上角(corners[1])的屏幕中点 —— 也就是"锚点"
        Vector2 anchorScreen = RectTransformUtility.WorldToScreenPoint(camera, corners[1]);

        float cardHeight = Mathf.Abs(corners[1].y - corners[0].y);      // 世界单位,世界空间下就是像素
        if (cardHeight <= 0.01f) cardHeight = focusedVisualSize.y;

        float halfWidth = Screen.width * 0.5f;
        float halfBand = Screen.width * axisBandWidth;
        bool onRight = anchorScreen.x > halfWidth + halfBand;           // 明显在右半边
        bool showOnRight = !onRight;                                    // 左半 + 中轴 → 弹右边
        if (!leftCardsShowOnRight) showOnRight = onRight;

        Vector2 previewSizePx = PreviewCardSize();

        // 预览这一格挂到父物体下,父物体就是它摆放的坐标系(场景里就是 BattleCanvas)
        var parent = rt.parent as RectTransform;
        if (parent == null)
        {
            Debug.LogWarning("[CardHoverPreview] hover 没有 RectTransform 父物体,预览保持预制体的位置。", this);
            return;
        }

        // 锚点的屏幕中点 → 父物体的局部坐标(比 worldToLocal 稳,过 Canvas 缩放/相机都算得对)
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent, anchorScreen, camera, out var anchorLocal))
        {
            Debug.LogWarning("[CardHoverPreview] 换算锚点坐标失败,预览保持预制体的位置。", this);
            return;
        }

        float gapPx = gap >= 0f ? gap : gapAboveCard;

        // 预览的左下角离锚点多远(父物体局部坐标,轴心在父物体中心)
        float offsetX = showOnRight ? 0f : -previewSizePx.x;
        float offsetY = cardHeight + gapPx;

        Vector2 min = parent.rect.min;
        Vector2 max = parent.rect.max;

        // 出屏幕就夹回来:预览整张必须落在父矩形里,至少留 sideMargin
        float minX = min.x + sideMargin - offsetX;
        float maxX = max.x - previewSizePx.x - sideMargin - offsetX;
        if (minX > maxX) maxX = minX;                                   // 父矩形比预览还小:贴着左边
        float posX = Mathf.Clamp(anchorLocal.x, minX, maxX);

        float minY = min.y - offsetY;
        float maxY = max.y - previewSizePx.y - offsetY;
        if (minY > maxY) maxY = minY;
        float posY = Mathf.Clamp(anchorLocal.y, minY, maxY);

        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0f, 0f);                                 // 轴心放左下角:位置好算,也方便夹边界
        rt.anchoredPosition = new Vector2(posX, posY);
    }

    // ================================================================ 预览卡实例
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

        // 预览不参与交互:鼠标必须一直留在被悬停的牌上,否则一移开预览就没了
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

    /// <summary>把卡实例摆成"正好铺满这一格":锚点居中、保持预制体尺寸、整体缩放</summary>
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
