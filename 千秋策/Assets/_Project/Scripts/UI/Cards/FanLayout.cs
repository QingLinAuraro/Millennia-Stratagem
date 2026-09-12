using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 手牌扇形排布(KARDS 式:底部居中、浅弧、相邻卡重叠)。
///
/// 两个必须记住的点:
/// 1) 布局只认自己的 cards 列表,不按子物体下标排。因为悬停时 CardHover 会把卡
///    提到最后一个 sibling 去压住别人,如果按子物体顺序算扇形,鼠标一滑整手牌就乱序。
/// 2) 每张卡的锚点和轴心由这里统一改成"手牌区底边中心 + 卡牌底边中心"。
///    Card.prefab 里的锚点是父物体左下角、轴心是顶边中心、还留着 3228 的残留位置,
///    直接用会把牌排到屏幕左下方外面去,而且绕顶边旋转看着不像扇形。
/// </summary>
[DisallowMultipleComponent]
public class FanLayout : MonoBehaviour
{
    [Header("展开")]
    [Tooltip("相邻两张卡最多分开多少像素。卡宽 150,所以 72 = 相邻叠住 78px")]
    [SerializeField] private float stepPerCard = 72f;
    [Tooltip("整手牌横向展开的宽度上限(px)。还会再被手牌区自身宽度夹一次")]
    [SerializeField] private float maxSpread = 640f;

    [Header("弧度")]
    [Tooltip("中间那张卡比两侧高多少像素,做成上凸的彩虹弧(0 = 排成一条直线)。两侧始终停在 baseY")]
    [SerializeField] private float arcDepth = 40f;
    [Tooltip("最外侧的卡倾斜多少度")]
    [SerializeField] private float maxTilt = 15f;

    [Header("基准")]
    [Tooltip("最外侧那张卡离手牌区底边多高(px)。填负值就是把整手牌沉下去,只露出上面一截。\n" +
             "本场景手牌区底边正好贴着屏幕底边,卡牌高 200,费用和卡名占 175~195 —— " +
             "所以 -120 就是露出卡牌顶部约 80px(费用 + 卡名 + 上半截插画)。想让卡多露一点就往大了调")]
    [SerializeField] private float baseY = -120f;
    [Tooltip("卡牌尺寸,用来算重叠和夹宽度。x<=0 表示自动读第一张卡的实际宽度")]
    [SerializeField] private Vector2 cardSize = new Vector2(150f, 200f);
    [Tooltip("卡牌锚点/轴心。手牌扇形用底边中心最自然,缩放和旋转都绕底边")]
    [SerializeField] private Vector2 cardPivot = new Vector2(0.5f, 0f);

    // ===== 参数版本 =====
    // Unity 里"场景序列化的值"永远优先于脚本里的默认值:只要场景保存过一次,
    // 上面那些字段的旧值就存在场景里了,之后改脚本的 = 默认值是【不生效】的。
    // 所以每次调整这批推荐参数就把 CurrentTuneVersion +1 ——
    // 重新编译 / 打开场景时 OnValidate 会把场景里的旧值覆盖成新值,不用手动去 Inspector 改。
    // (想保留自己在 Inspector 里手调的值,就不要动这个版本号)
    private const int CurrentTuneVersion = 2;
    [SerializeField, HideInInspector] private int tuneVersion;

    private readonly List<RectTransform> cards = new();

    public int Count => cards.Count;
    public IReadOnlyList<RectTransform> Cards => cards;

    private void OnValidate()
    {
        if (tuneVersion < CurrentTuneVersion) ApplyRecommendedTuning();
    }

    /// <summary>把布局参数恢复成当前推荐的数值(Inspector 右键菜单也能手动调)</summary>
    [ContextMenu("恢复推荐的手牌布局参数")]
    private void ApplyRecommendedTuning()
    {
        stepPerCard = 72f;                      // 相邻间距(卡宽 150,所以叠 78px)
        maxSpread = 640f;                       // 整手牌横向展开上限
        arcDepth = 40f;                         // 弧度:中间比两侧高 40px
        maxTilt = 15f;                          // 两端倾角
        baseY = -120f;                          // 沉下去,露出卡牌顶部约 80px
        cardSize = new Vector2(150f, 200f);
        cardPivot = new Vector2(0.5f, 0f);
        tuneVersion = CurrentTuneVersion;
    }

    private void Start()
    {
        // 手牌区上如果挂了 LayoutGroup,它会每帧覆盖这里排好的位置
        if (GetComponent<UnityEngine.UI.LayoutGroup>() != null)
            Debug.LogError("[FanLayout] 手牌区上挂了 LayoutGroup(Horizontal/Vertical Layout Group),它会覆盖扇形排布,请删掉它。", this);
    }

    /// <summary>抽到一张新手牌时调用(HandUI 负责 Instantiate)</summary>
    public void Add(RectTransform card)
    {
        if (card == null || cards.Contains(card)) return;
        Normalize(card);
        cards.Add(card);
        Refresh();
    }

    public bool Remove(RectTransform card)
    {
        if (!cards.Remove(card)) return false;
        Refresh();
        return true;
    }

    /// <summary>只清列表,不销毁物体(销毁由 HandUI 负责)</summary>
    public void Clear() => cards.Clear();

    /// <summary>手牌数量/内容变化后调用</summary>
    public void Refresh()
    {
        // 清掉已经被销毁的卡(打出去、场景重载)
        for (int i = cards.Count - 1; i >= 0; i--)
            if (cards[i] == null) cards.RemoveAt(i);

        int n = cards.Count;
        if (n == 0) return;

        float width = cardSize.x > 0f ? cardSize.x : cards[0].rect.width;

        // 手牌区自身的宽度,减去一整张卡,保证最外侧的卡也完整落在区内
        var self = (RectTransform)transform;
        float areaWidth = self.rect.width > 0f ? self.rect.width : self.sizeDelta.x;
        float usable = Mathf.Max(0f, areaWidth - width);

        // 相邻间距:不超过 stepPerCard,不超过总宽度上限,也不超过手牌区能放下的宽度
        float step = n > 1
            ? Mathf.Min(stepPerCard, maxSpread / (n - 1), usable / (n - 1))
            : 0f;

        float mid = (n - 1) * 0.5f;
        // 用最外侧那张卡的偏移量归一化:不管手牌几张,两端永远落在 baseY,
        // 正中间永远抬到 baseY + arcDepth,弧线形状不会随张数变形
        float maxOffset = step * (n - 1) * 0.5f;

        for (int i = 0; i < n; i++)
        {
            var rt = cards[i];
            float offset = (i - mid) * step;                                           // 相对中心的横向偏移
            float t = maxOffset > 0f ? Mathf.Clamp(offset / maxOffset, -1f, 1f) : 0f;  // -1(最左) ~ +1(最右)

            // 中间最高,两侧落到 baseY —— 两侧不会再低于 baseY,所以不会被切到屏幕外
            var pos = new Vector2(offset, baseY + arcDepth * (1f - t * t));
            float rot = -maxTilt * t;                                     // 越靠边倾得越多

            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = cardPivot;

            var hover = rt.GetComponent<CardHover>();
            if (hover != null) hover.SetHome(pos, rot);
            else
            {
                rt.anchoredPosition = pos;
                rt.localRotation = Quaternion.Euler(0f, 0f, rot);
            }
        }

        RestoreRenderOrder();
    }

    /// <summary>
    /// 按抽牌顺序重排图层:后摸的牌在上层,压住先摸的牌。
    /// 手牌是左边搭右边的,每张牌露出来的是它自己左边那一块(右边被后一张盖住),
    /// 所以后摸的必须在上层 —— 否则先摸的牌会把后摸那张的费用和卡名盖掉。
    /// 悬停中的那张单独留在最上面。
    /// </summary>
    public void RestoreRenderOrder()
    {
        // 先把没被悬停的按抽牌顺序摞好,悬停中的那张跳过
        for (int i = 0; i < cards.Count; i++)
        {
            var rt = cards[i];
            if (rt == null) continue;
            var hover = rt.GetComponent<CardHover>();
            if (hover != null && hover.IsHovered) continue;
            rt.SetAsLastSibling();
        }

        // 悬停中的那张最后再提上来
        for (int i = 0; i < cards.Count; i++)
        {
            var rt = cards[i];
            if (rt == null) continue;
            var hover = rt.GetComponent<CardHover>();
            if (hover != null && hover.IsHovered) rt.SetAsLastSibling();
        }
    }

    /// <summary>统一卡牌的锚点/轴心,并把预制体里残留的位置、角度、缩放抹掉</summary>
    private void Normalize(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = cardPivot;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        rt.anchoredPosition = new Vector2(0f, baseY);   // 先落在手牌区底边中心,避免闪一下

        // 让 CardHover 认得管自己的是这个扇形:悬停结束时要靠它把图层顺序恢复回去
        var hover = rt.GetComponent<CardHover>();
        if (hover != null) hover.Bind(this);
    }

    [ContextMenu("把当前子物体收集为手牌(编辑器用)")]
    private void RecollectChildren()
    {
        cards.Clear();
        for (int i = 0; i < transform.childCount; i++)
            if (transform.GetChild(i) is RectTransform rt) cards.Add(rt);
        for (int i = 0; i < cards.Count; i++) Normalize(cards[i]);
        Refresh();
    }
}
