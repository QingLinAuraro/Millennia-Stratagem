using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 战场卡的「词条栏」:卡面**右上角外侧**一个小方块,有词条才显示;
/// 在上面停留一小会儿(默认 0.55 秒)弹出面板,逐条列「词条名 + 它到底干什么」。
///
/// 【为什么挂在卡外面,而不是塞进卡面】
///   战场卡(CardsInBattle)只有 150×200,右上角是血量角标、左上角是攻击力,卡里已经没有空位。
///   做成角外的小方块,不占卡面布局,以后换成真正的图标也只改 CreateBadge 一处。
///   方块尺寸默认 40×40 —— 和卡面左侧费用区那个方块一样大(它也是 40×40)。
///
/// 【为什么方块只探出去半个】
///   排里相邻两张卡的间距只有 20(画布单位),方块本身是 40×40(卡面局部,乘 0.7 后 28)。
///   让它左右各探出一半(卡外 14 < 20 的间距),就正好落在空隙里 —— 既在卡外,又不会被右边那张卡盖住,
///   不需要给它加嵌套 Canvas 强行提层(那样它会连悬停预览的大卡也一起盖掉)。
///   上下同理贴着卡顶放平,不探到上一排去。
///
/// 【词条从哪来】
///   以 FieldUnit 为准,不读卡面资产 —— 因为词条会被策略卡**后置授予**(军功爵给「血战」、
///   犒赏加一层「重甲」),卡面 CardData.keywords 是只读资产,看不到这些:
///     · 卡面自带:Data.keywords(重复项算层数);
///     · 后置授予:FieldUnit.GrantedKeywords;
///     · 重甲层数:FieldUnit.HeavyArmor(它同时包含卡面的重甲1/2/3 和策略卡加的那层,以它为准)。
///   刷新靠 FieldUnit.KeywordsChanged 事件推送,不轮询。
///
/// 【buff 类不进词条栏】
///   「+ATK / +HP」是 buff 类**效果**,按 §4.3 只写进效果栏,不算词条,所以这里一条都不列。
///
/// 【挂载 & 调整】
///   挂在:战场卡根物体上,由 UnitDeployer.SpawnUnit 生成时 AddComponent + Bind。
///         手牌(Card.prefab)不会加,所以手牌上没有这个词条栏。
///   引用:没有要手连的引用,方块和面板都是运行时按尺寸/颜色建的。
///   常调:
///     · BadgeSize:方块边长(默认 40,和费用区方块一致)。
///     · BadgeOffset:相对卡牌右上角的偏移。x 越大越靠外(超过约 10 就会被右邻卡压住);
///       y 越小越往下(0 = 贴着卡顶)。
///     · HoverDelay:长悬停多久弹面板。
///     · BadgeColor / BadgeEdgeColor / PanelColor:配色。
/// </summary>
[DisallowMultipleComponent]
public class FieldKeywordBadge : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    // ================================================================ 尺寸与配色

    /// <summary>方块边长。默认 40 —— 和卡面费用区那个方块同尺寸</summary>
    private const float BadgeSize = 40f;

    /// <summary>
    /// 相对卡牌右上角的偏移。x=8 → 方块右边缘离卡右边缘 28(卡外 28 &lt; 排间距 28.6,刚好不出界);
    /// y=-20 → 方块顶边贴着卡顶。两个值一起保证它"在卡外但不压到邻居"。
    /// </summary>
    private static readonly Vector2 BadgeOffset = new Vector2(8f, -20f);

    /// <summary>长悬停多久才弹面板(秒)。太短会和拖动/点击打架</summary>
    private const float HoverDelay = 0.55f;

    private const float PanelWidth = 460f;
    private const float PanelPadding = 14f;
    private const float PanelFontSize = 20f;

    /// <summary>方块里那个字。以后换成图标时删掉它、给底图换 sprite 即可</summary>
    private const string BadgeLabel = "词";

    private static readonly Color BadgeEdgeColor = new Color(0.85f, 0.72f, 0.36f, 1f);   // 金边
    private static readonly Color BadgeColor = new Color(0.14f, 0.13f, 0.11f, 0.95f);      // 深底
    private static readonly Color PanelColor = new Color(0.08f, 0.08f, 0.09f, 0.95f);
    private static readonly Color PanelTextColor = new Color(0.96f, 0.94f, 0.88f);

    // ================================================================ 运行时状态

    private FieldUnit unit;

    private RectTransform badgeRect;
    private float hoverTime;
    private bool hovering;

    private RectTransform panelRoot;
    private TMP_Text panelText;
    private CanvasGroup panelGroup;

    /// <summary>去重后的词条顺序(按卡面顺序,后置授予的接在后面)</summary>
    private readonly List<Keyword> order = new List<Keyword>();

    /// <summary>词条 → 层数(只有重甲会 > 1)</summary>
    private readonly Dictionary<Keyword, int> counts = new Dictionary<Keyword, int>();

    private readonly StringBuilder textBuilder = new StringBuilder();

    // ================================================================ 绑定

    /// <summary>绑定一个场上单位。重复调用会换人并重新订阅</summary>
    public void Bind(FieldUnit owner)
    {
        if (unit != null) unit.KeywordsChanged -= Refresh;
        unit = owner;

        if (badgeRect == null) CreateBadge();
        if (unit != null) unit.KeywordsChanged += Refresh;

        Refresh();
    }

    private void OnDestroy()
    {
        if (unit != null) unit.KeywordsChanged -= Refresh;
        if (panelRoot != null) Destroy(panelRoot.gameObject);   // 面板挂在画布下,不是本物体的子物体
    }

    private void OnDisable() => HidePanel();

    // ================================================================ 刷新

    /// <summary>重算词条集合并同步方块显隐。词条为空时整块藏起来(建筑、无词条兵牌都没有)</summary>
    private void Refresh()
    {
        if (badgeRect == null) return;

        CollectKeywords();

        bool has = order.Count > 0;
        if (badgeRect.gameObject.activeSelf != has) badgeRect.gameObject.SetActive(has);
        if (!has) HidePanel();
    }

    /// <summary>
    /// 汇总这个单位现在的全部词条:
    /// 卡面自带的(重复项 = 层数)→ 后置授予的 → 重甲层数以 FieldUnit.HeavyArmor 覆盖。
    /// </summary>
    private void CollectKeywords()
    {
        order.Clear();
        counts.Clear();
        if (unit == null) return;

        // 1) 卡面自带
        var data = unit.Data;
        if (data != null && data.keywords != null)
        {
            for (int i = 0; i < data.keywords.Count; i++)
            {
                var k = data.keywords[i];
                if (counts.TryGetValue(k, out int n)) counts[k] = n + 1;
                else { counts[k] = 1; order.Add(k); }
            }
        }

        // 2) 本局后置授予(策略卡给的)
        var granted = unit.GrantedKeywords;
        if (granted != null)
        {
            for (int i = 0; i < granted.Count; i++)
            {
                var k = granted[i];
                if (!counts.ContainsKey(k)) { counts[k] = 1; order.Add(k); }
            }
        }

        // 3) 重甲:层数一律以 FieldUnit 为准 —— 卡面的「重甲2」在落位时已经被换算成层数,
        //    策略卡「犒赏」加的那层也只记在 HeavyArmor 上。
        if (unit.HeavyArmor > 0)
        {
            if (!counts.ContainsKey(Keyword.HeavyArmor)) order.Add(Keyword.HeavyArmor);
            counts[Keyword.HeavyArmor] = unit.HeavyArmor;
        }
        else if (counts.Remove(Keyword.HeavyArmor))
        {
            order.Remove(Keyword.HeavyArmor);
        }
    }

    // ================================================================ 方块

    private void CreateBadge()
    {
        var go = new GameObject("KeywordBadge", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));

        badgeRect = (RectTransform)go.transform;
        badgeRect.SetParent(transform, false);
        badgeRect.anchorMin = badgeRect.anchorMax = new Vector2(1f, 1f);   // 卡牌右上角
        badgeRect.pivot = new Vector2(0.5f, 0.5f);
        badgeRect.sizeDelta = new Vector2(BadgeSize, BadgeSize);
        badgeRect.anchoredPosition = BadgeOffset;

        // 外层 = 金色描边
        var frame = go.GetComponent<Image>();
        frame.color = BadgeEdgeColor;
        frame.raycastTarget = true;              // 悬停事件靠它命中

        // 内层 = 深色底(往里缩 2px,露出来的边就是描边)
        var bgGo = new GameObject("Bg", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var bgRect = (RectTransform)bgGo.transform;
        bgRect.SetParent(badgeRect, false);
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = new Vector2(2f, 2f);
        bgRect.offsetMax = new Vector2(-2f, -2f);
        var bg = bgGo.GetComponent<Image>();
        bg.color = BadgeColor;
        bg.raycastTarget = false;

        RuntimeText.Create(badgeRect, "Label", BadgeLabel, 20f, TextAlignmentOptions.Center, BadgeEdgeColor);
    }

    // ================================================================ 长悬停

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (order.Count == 0) return;
        hovering = true;
        hoverTime = 0f;
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        hovering = false;
        hoverTime = 0f;
        HidePanel();
    }

    private void Update()
    {
        if (!hovering) return;

        hoverTime += Time.unscaledDeltaTime;     // 以后做暂停也不影响这个计时
        if (hoverTime < HoverDelay) return;

        if (panelRoot == null || !panelRoot.gameObject.activeSelf) ShowPanel();
        else PlacePanel();                       // 卡在移动/换排时跟着走
    }

    // ================================================================ 词条面板

    private void ShowPanel()
    {
        if (order.Count == 0) return;
        if (!EnsurePanel()) return;

        panelText.text = BuildPanelText();

        // 先把宽度定下来,再让 TMP 按这个宽度算高度(否则折行后的高度会少一行)
        panelRoot.sizeDelta = new Vector2(PanelWidth, 120f);
        panelText.ForceMeshUpdate();
        float height = Mathf.Max(64f, panelText.preferredHeight + PanelPadding * 2f);
        panelRoot.sizeDelta = new Vector2(PanelWidth, height);

        panelRoot.gameObject.SetActive(true);
        if (panelGroup != null) panelGroup.alpha = 1f;
        panelRoot.SetAsLastSibling();            // 盖在手牌、飘字之下/之上都行,这里保证在卡牌之上
        PlacePanel();
    }

    private void HidePanel()
    {
        if (panelRoot != null && panelRoot.gameObject.activeSelf) panelRoot.gameObject.SetActive(false);
    }

    private string BuildPanelText()
    {
        textBuilder.Length = 0;
        textBuilder.Append("词条\n");
        for (int i = 0; i < order.Count; i++)
        {
            var k = order[i];
            counts.TryGetValue(k, out int n);
            textBuilder.Append(KeywordInfo.NameWithCount(k, n))
                       .Append("　")
                       .Append(KeywordInfo.Describe(k))
                       .Append('\n');
        }
        return textBuilder.ToString().TrimEnd('\n');
    }

    private bool EnsurePanel()
    {
        if (panelRoot != null) return true;

        var canvas = CanvasUtil.FindRootCanvas();
        if (canvas == null)
        {
            Debug.LogWarning("[词条栏] 找不到根画布,词条面板弹不出来。", this);
            return false;
        }

        var go = new GameObject("KeywordPanel", typeof(RectTransform), typeof(CanvasRenderer),
                                typeof(Image), typeof(CanvasGroup));
        panelRoot = (RectTransform)go.transform;
        panelRoot.SetParent(canvas.transform, false);
        panelRoot.anchorMin = panelRoot.anchorMax = new Vector2(0.5f, 0.5f);
        panelRoot.pivot = new Vector2(1f, 0.5f);          // 右边缘对齐方块,向左展开
        panelRoot.sizeDelta = new Vector2(PanelWidth, 120f);

        var bg = go.GetComponent<Image>();
        bg.color = PanelColor;
        bg.raycastTarget = false;                         // 面板不吃鼠标,免得一挪上去就触发 Exit

        panelGroup = go.GetComponent<CanvasGroup>();
        panelGroup.blocksRaycasts = false;
        panelGroup.interactable = false;

        panelText = RuntimeText.Create(panelRoot, "Text", "", PanelFontSize,
                                       TextAlignmentOptions.TopLeft, PanelTextColor);
        var textRect = (RectTransform)panelText.transform;
        textRect.offsetMin = new Vector2(PanelPadding, PanelPadding);
        textRect.offsetMax = new Vector2(-PanelPadding, -PanelPadding);
        panelText.enableWordWrapping = true;

        panelRoot.gameObject.SetActive(false);
        return true;
    }

    /// <summary>把面板摆到方块左边并夹在画布可见范围内</summary>
    private void PlacePanel()
    {
        if (panelRoot == null || badgeRect == null) return;

        var canvasRect = panelRoot.parent as RectTransform;
        if (canvasRect == null) return;

        var camera = RuntimeText.EventCameraFor(canvasRect);
        Vector3 world = badgeRect.TransformPoint(badgeRect.rect.center);
        Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, world);

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screen, camera, out var local))
            return;

        Vector2 size = panelRoot.sizeDelta;
        Vector2 half = canvasRect.rect.size * 0.5f;

        // 方块半宽要按**画布单位**算:卡面被整体缩了 0.7,直接用 rect.width 会把间距算大一截
        float halfBadge = badgeRect.rect.width * badgeRect.lossyScale.x * 0.5f;
        float x = local.x - halfBadge - 8f;              // 方块左边留 8px
        float y = local.y;

        // 夹在画布内:右边不出界,左边也不出界;上下同理
        float minX = -half.x + size.x + 6f;
        float maxX = half.x - 6f;
        float minY = -half.y + size.y * 0.5f + 6f;
        float maxY = half.y - size.y * 0.5f - 6f;
        x = Mathf.Clamp(x, Mathf.Min(minX, maxX), maxX);
        y = Mathf.Clamp(y, Mathf.Min(minY, maxY), maxY);

        panelRoot.anchoredPosition = new Vector2(x, y);
    }
}
