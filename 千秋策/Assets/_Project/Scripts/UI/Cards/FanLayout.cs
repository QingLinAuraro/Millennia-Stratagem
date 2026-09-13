using System.Collections.Generic;
using UnityEngine;

/// <summary>悬停预览弹出时,手牌里怎么标出正在看的是哪张</summary>
public enum HoverSpotlightMode
{
    DimOthers,   // 把其余手牌压暗
    HideCard,    // 把正在看的这张藏起来(透明,仍然接得到鼠标,移开就能恢复)
    None,        // 手牌保持原样
}

/// <summary>
/// 手牌扇形排布(KARDS 式:底部居中、浅弧、相邻卡重叠)。
///
/// 两个必须记住的点:
/// 1) 布局只认自己的 cards 列表,不按子物体下标排 —— 列表顺序就是抽牌顺序。
///    (悬停预览只改卡牌透明度,不再动 sibling 顺序,但别改成按子物体排。)
/// 2) 每张卡的锚点和轴心由这里统一改成"手牌区底边中心 + 卡牌底边中心"。
///    Card.prefab 里的锚点是父物体左下角、轴心是顶边中心、还留着 3228 的残留位置,
///    直接用会把牌排到屏幕左下方外面去,而且绕顶边旋转看着不像扇形。
/// </summary>
///
/// 【挂载 & 调整】
///   挂在:Battle.unity 里挂了两份,分别在 BattleCanvas/HandArea1(我方手牌区,HandUI.fanLayout 手连它)和
///         BattleCanvas/HandArea2(敌方手牌区,EnemyHandUI.fanLayout 手连它)上。
///         两份实例的参数是各自独立调过的、互为镜像,别把一份的值抄到另一份:
///           · HandArea1(我方):arcDepth 40、maxTilt 15、baseY -120、cardPivot (0.5,0) —— 以手牌区底边为基准往上凸;
///           · HandArea2(敌方):arcDepth -40、maxTilt -15、baseY 240、cardPivot (0.5,1) —— 以手牌区顶边为基准往下挂。
///         [DisallowMultipleComponent] 限一个物体一份,两份实例靠「挂在哪个手牌区上」区分。
///   引用:本组件没有任何手连引用 —— 卡片列表由 HandUI.Add / EnemyHandUI.Sync 喂进来,CardHover 由 Normalize 里的
///         Bind 注入,CardDragPlay 自己 GetComponentInParent<FanLayout>() 找上来。所以只要它挂在手牌区物体上就能工作;
///         反过来,HandUI.fanLayout 留空是另一回事:卡不进列表、不排扇形,会停在 Card.prefab 烘的锚点位置上
///         (不是叠在手牌区中心)。手牌区上如果还挂了 Horizontal/VerticalLayoutGroup,Start 会 LogError 提醒删掉它 ——
///         LayoutGroup 每帧按子物体顺序覆盖位置,扇形排布会被它冲掉。
///   常调:
///     · stepPerCard:相邻两张卡最多分开多少像素(卡宽 150,填 72 = 相邻叠住 78px)。实际取
///       min(stepPerCard, maxSpread/(n-1), 可用宽/(n-1)) —— 现在后两项都夹不住它(7 张时分别是 106 和 108),
///       所以调大 = 每张露出的边更多、整手牌更宽,调小 = 叠得更紧、一屏塞下更多牌。
///     · maxSpread:整手牌横向总宽的上限(第二道夹子)。按现在的值它其实夹不到任何东西:手牌上限 7 张时
///       640/6≈106 已经大于 stepPerCard 的 72。想让它真正生效得先调到 432(7 张 × 72)以下 ——
///       那时整手牌的总宽就是 maxSpread,调小 = 手牌一多就被压紧。
///     · arcDepth:弧高。位置公式是 baseY + arcDepth × (1 - t²),t 在最外侧 = ±1,所以中间那张 = baseY + arcDepth,
///       两端永远停在 baseY。我方填正值、敌方填负值才是各自的凸 / 凹;填 0 = 排成一条直线;
///       绝对值调大 = 弧更弯,中间和两端的高度差更明显(露出的面积差也更大)。
///     · maxTilt:最外侧那张的倾角,公式 rot = -maxTilt × t。我方正、敌方负才是「两端往外撇」;
///       调 0 = 全部不旋转;调到 25 以上两端的卡会歪得压住旁边的卡名。
///     · baseY:整手牌的基准高度,相对手牌区【底边中心】往上量(px)。调大 = 整手牌上移。
///       我方 pivot 在底边:填负值就是沉下去,现在 -120 = 两端只露卡牌顶部 80px、中间 120px,想多露就调大;
///       敌方 pivot 在顶边:填得越大反而露得越少(现在 240 = 两端露 100px、中间 140px)。
///       它和 cardPivot 必须一起对:卡牌锚点被强制成「手牌区底边中心」,所以 (0.5,0) = 绕卡牌底边转(我方),
///       (0.5,1) = 绕卡牌顶边转(敌方往下挂的镜像);只改 cardPivot 不动 baseY,整手牌会整体平移。
///     · cardSize:算重叠和夹宽度用的卡牌尺寸,x<=0 就改读第一张卡的实际宽度。卡面预制体尺寸改了要同步这里,
///       否则重叠量和「最外侧也要完整落在区内」的夹宽都会算错。
///     · cardScale:整手牌缩放(只改 localScale,不动 rect,所以卡面和字号不会被拉变形)。调小 = 手牌更小;
///       注意它会按「缩放后的视觉宽度」重算 step 和夹宽,所以缩放和间距是联动的。敌方牌背就是靠它缩小的。
///     · spotlightMode / spotlightDimAlpha / unplayableAlpha:悬停预览与「出不了的牌」的透明度,统一由 ApplyAlphas 算:
///       DimOthers = 只把其余手牌压暗到 spotlightDimAlpha(0.35),HideCard = 把正在看的那张设成 0(仍然接得到鼠标,
///       移开就恢复),出不了的牌用 unplayableAlpha(0.5);两者同时命中时取更暗的那个。调大 = 更不明显、更接近原样。
///   【版本号与覆写规则】CurrentTuneVersion = 2;tuneVersion 是 [SerializeField, HideInInspector](Inspector 里看不见,
///     但会序列化进场景)。OnValidate 只在 tuneVersion < CurrentTuneVersion 时调 ApplyRecommendedTuning(),
///     而那批值是【我方手牌】的推荐值(arcDepth 40 / maxTilt 15 / baseY -120 / cardPivot (0.5,0))。
///       · 场景里两份实例的 tuneVersion 现在都是 2 = 当前版本,所以 OnValidate 不会再动它们;
///       · 改脚本里的 = 默认值对这两份实例无效(场景序列化的值优先),想推着所有人都更新才把版本号 +1;
///       · 但版本号一 +1,两份实例都会被刷成我方参数 —— HandArea2 那份会从「往下挂」变成「往上凸」,
///         得再勾 EnemyHandUI.forceFanPreset(或用它右键菜单「按敌方手牌参数刷新扇形」)才修回来;
///       · 想在 Inspector 里保留自己手调的值,就别动这个版本号(右键菜单「恢复推荐的手牌布局参数」也是同一套我方值)。
///       · ApplyPreset()(EnemyHandUI 运行时补组件时调)会把 tuneVersion 顶到当前版本,防止之后 OnValidate 用我方推荐值
///         把敌方参数盖回去;但运行时 AddComponent 出来的组件不进场景序列化,要持久化还是得在场景里挂好或勾 forceFanPreset。
///   【排布公式】offset = (i - 中点) × step;t = offset / 最外侧偏移(两端 = ±1);
///     位置 = (offset, baseY + arcDepth × (1 - t²));角度 = -maxTilt × t。
///     (1 - t²) 让两端固定在 baseY、中间抬 arcDepth,弧线形状不随张数变形;我方 baseY 为负、arcDepth 为正,
///     只会在 baseY 之上加,所以两端最低、不会被切到屏幕下沿。
///   【图层与透明度】RestoreRenderOrder 按抽牌顺序逐张 SetAsLastSibling:后摸的在上层(手牌是左搭右,先摸的那张右边会被
///     盖住,顺序反了费用和卡名就被压掉),正被拖出去的那张永远在最上层。透明度一律靠 CanvasGroup 整张淡,没用过的卡不会
///     被挂上 CanvasGroup。右键菜单「把当前子物体收集为手牌」会清空列表后按【子物体顺序】重收并 Normalize,只适合手摆测试。
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
    [Tooltip("整手牌缩放(只改 localScale,不改 rect —— 卡面/字号不会被拉变形)。\n" +
             "敌方手牌的牌背想小一点就调它;己方手牌保持 1")]
    [SerializeField] private float cardScale = 1f;

    [Header("悬停预览时的手牌表现")]
    [Tooltip("预览弹出后,手牌里怎么标出正在看的是哪张(只改透明度,不动位置和缩放)")]
    [SerializeField] private HoverSpotlightMode spotlightMode = HoverSpotlightMode.DimOthers;
    [Tooltip("DimOthers:其余手牌保留的透明度")]
    [Range(0f, 1f)]
    [SerializeField] private float spotlightDimAlpha = 0.35f;

    [Header("出不了的手牌")]
    [Tooltip("点数不足 / 无处可放时手牌变灰的透明度(策划案§8.1.1、§10.3 的「卡牌变灰」)")]
    [Range(0f, 1f)]
    [SerializeField] private float unplayableAlpha = 0.5f;

    // ===== 参数版本 =====
    // Unity 里"场景序列化的值"永远优先于脚本里的默认值:只要场景保存过一次,
    // 上面那些字段的旧值就存在场景里了,之后改脚本的 = 默认值是【不生效】的。
    // 所以每次调整这批推荐参数就把 CurrentTuneVersion +1 ——
    // 重新编译 / 打开场景时 OnValidate 会把场景里的旧值覆盖成新值,不用手动去 Inspector 改。
    // (想保留自己在 Inspector 里手调的值,就不要动这个版本号)
    private const int CurrentTuneVersion = 2;
    [SerializeField, HideInInspector] private int tuneVersion;

    private readonly List<RectTransform> cards = new();

    /// <summary>每张手牌在扇形里的位置(拖动取消时要飞回这里)。每次 Refresh 重建</summary>
    private readonly List<CardHome> homes = new();

    /// <summary>变灰的手牌(点数不足 / 无处可放),和悬停聚光共用同一套 α 计算</summary>
    private readonly HashSet<RectTransform> dimmedCards = new();

    // 正在被拖出去的那张:扇形重排要放过它,位置由拖动接管
    private RectTransform heldCard;

    // 正在被悬停预览的那张(高亮/隐藏都跟着它走),null = 没有预览
    private RectTransform spotlightFocus;

    private struct CardHome
    {
        public RectTransform Card;
        public Vector2 Position;
        public float Rotation;
    }

    public int Count => cards.Count;
    public IReadOnlyList<RectTransform> Cards => cards;

    /// <summary>整手牌的缩放(拖动放大/飞回原位都要乘上它,不然松手会跳回 1 倍)</summary>
    public float CardScale => Mathf.Max(0.01f, cardScale);

    /// <summary>
    /// 从代码里配一整套扇形参数。运行时补出来的 FanLayout 用得上 ——
    /// 敌方手牌挂在屏幕顶边、往下方挂,和本地手牌那套"底边往上凸"的数值完全相反。
    /// 配完立刻按新参数重排。
    /// </summary>
    public void ApplyPreset(float step, float spread, float depth, float tilt,
                            float baseOffset, Vector2 pivot, float scale = 1f)
    {
        stepPerCard = step;
        maxSpread = spread;
        arcDepth = depth;
        maxTilt = tilt;
        baseY = baseOffset;
        cardPivot = pivot;
        cardScale = Mathf.Max(0.01f, scale);

        tuneVersion = CurrentTuneVersion;   // 别让 OnValidate 的"本地手牌推荐值"把它盖回去
        Refresh();
    }

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
        dimmedCards.Remove(card);
        if (heldCard == card) heldCard = null;
        if (spotlightFocus == card) spotlightFocus = null;

        if (!cards.Remove(card)) return false;
        Refresh();
        return true;
    }

    /// <summary>只清列表,不销毁物体(销毁由 HandUI 负责)</summary>
    public void Clear()
    {
        cards.Clear();
        homes.Clear();
        dimmedCards.Clear();
        heldCard = null;
        spotlightFocus = null;
    }

    /// <summary>
    /// 某张手牌正在被拖出去(出牌)。它还在手牌列表里,但扇形重排时要放过它 ——
    /// 位置由拖动接管,拖回来时再按 TryGetHome 飞回原位。
    /// </summary>
    public void SetHeld(RectTransform card, bool held)
    {
        if (held) heldCard = card;
        else if (heldCard == card) heldCard = null;
    }

    /// <summary>某张手牌在扇形里的家(拖动取消时飞回的目标)</summary>
    public bool TryGetHome(RectTransform card, out Vector2 position, out float rotation)
    {
        for (int i = 0; i < homes.Count; i++)
        {
            if (homes[i].Card != card) continue;
            position = homes[i].Position;
            rotation = homes[i].Rotation;
            return true;
        }

        position = card != null ? card.anchoredPosition : Vector2.zero;
        rotation = card != null ? card.localEulerAngles.z : 0f;
        return false;
    }

    /// <summary>
    /// 把一张手牌标成"出不了"(变灰)。和悬停聚光是两回事,但 α 只该由一个地方算,
    /// 否则置灰(0.5)和聚光压暗(0.35)会互相覆盖。
    /// </summary>
    public void SetDimmed(RectTransform card, bool dimmed)
    {
        if (card == null) return;
        bool changed = dimmed ? dimmedCards.Add(card) : dimmedCards.Remove(card);
        if (changed) ApplyAlphas();
    }

    /// <summary>手牌数量/内容变化后调用</summary>
    public void Refresh()
    {
        // 清掉已经被销毁的卡(打出去、场景重载)
        for (int i = cards.Count - 1; i >= 0; i--)
            if (cards[i] == null) cards.RemoveAt(i);

        homes.Clear();

        int n = cards.Count;
        if (n == 0) { spotlightFocus = null; heldCard = null; return; }

        float width = cardSize.x > 0f ? cardSize.x : cards[0].rect.width;
        width *= CardScale;     // 整体缩放过的话,视觉宽度也要跟着缩,否则重叠量算错

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

            homes.Add(new CardHome { Card = rt, Position = pos, Rotation = rot });

            if (rt == heldCard) continue;    // 正被拖着的那张:记下家在哪儿就行,别把它拽回来

            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = cardPivot;
            rt.localScale = Vector3.one * CardScale;

            var hover = rt.GetComponent<CardHover>();
            if (hover != null) hover.SetHome(pos, rot);
            else
            {
                rt.anchoredPosition = pos;
                rt.localRotation = Quaternion.Euler(0f, 0f, rot);
            }
        }

        RestoreRenderOrder();

        // 排布期间可能有牌被抽走/打出:还在预览就把高亮重新贴一遍,预览对象没了就全部恢复
        if (spotlightFocus != null && cards.Contains(spotlightFocus)) ApplySpotlight();
        else ClearSpotlight();
    }

    /// <summary>
    /// 悬停预览弹出/收起时调用。focus = 正在看的那张牌,null = 取消高亮(手牌全部恢复)。
    /// 只改透明度,不动位置和缩放 —— 扇形始终是稳的。
    /// </summary>
    public void SetSpotlight(RectTransform focus)
    {
        spotlightFocus = focus;
        ApplyAlphas();
    }

    private void ApplySpotlight() => ApplyAlphas();

    /// <summary>
    /// 唯一改手牌透明度的地方:置灰(出不了)和聚光(正在看哪张)一起算,取更暗的那个。
    /// </summary>
    private void ApplyAlphas()
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var rt = cards[i];
            if (rt == null) continue;

            float alpha = dimmedCards.Contains(rt) ? unplayableAlpha : 1f;

            if (spotlightFocus != null && rt == spotlightFocus)
            {
                if (spotlightMode == HoverSpotlightMode.HideCard) alpha = 0f;
            }
            else if (spotlightFocus != null && spotlightMode == HoverSpotlightMode.DimOthers)
            {
                alpha = Mathf.Min(alpha, spotlightDimAlpha);
            }

            SetCardAlpha(rt, alpha);
        }
    }

    private void ClearSpotlight()
    {
        spotlightFocus = null;
        ApplyAlphas();
    }

    // 透明度靠 CanvasGroup:整张牌(底图 + 文字 + 插画)一起淡,不会只淡掉一半。
    // 没用过悬停预览的手牌就不会被挂上这个组件(alpha 本来就是 1)
    private static void SetCardAlpha(RectTransform card, float alpha)
    {
        var group = card.GetComponent<CanvasGroup>();
        if (group == null)
        {
            if (Mathf.Approximately(alpha, 1f)) return;
            group = card.gameObject.AddComponent<CanvasGroup>();
        }

        group.alpha = alpha;
    }

    /// <summary>
    /// 按抽牌顺序重排图层:后摸的牌在上层,压住先摸的牌。
    /// 手牌是左边搭右边的,每张牌露出来的是它自己左边那一块(右边被后一张盖住),
    /// 所以后摸的必须在上层 —— 否则先摸的牌会把后摸那张的费用和卡名盖掉。
    /// (悬停预览显示在 hover 那一格,不再把卡牌提到最上层,手牌原地不动)
    /// </summary>
    public void RestoreRenderOrder()
    {
        for (int i = 0; i < cards.Count; i++)
            if (cards[i] != null && cards[i] != heldCard) cards[i].SetAsLastSibling();

        // 正被拖着的那张必须在最上层:它跟着指针走,不能被别的手牌盖住
        if (heldCard != null && cards.Contains(heldCard)) heldCard.SetAsLastSibling();
    }

    /// <summary>统一卡牌的锚点/轴心,并把预制体里残留的位置、角度、缩放抹掉</summary>
    private void Normalize(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = cardPivot;
        rt.localScale = Vector3.one * CardScale;
        rt.localRotation = Quaternion.identity;
        rt.anchoredPosition = new Vector2(0f, baseY);   // 先落在手牌区底边中心,避免闪一下

        // 让 CardHover 认得管自己的是这个扇形:悬停预览弹出时靠它给手牌打高亮
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
