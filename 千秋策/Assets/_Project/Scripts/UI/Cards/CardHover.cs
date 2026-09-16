using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 卡牌悬停:鼠标在同一张牌上停留超过 previewDelay 秒(默认 0.5 秒),就把这张牌按手牌布局
/// 放大成 300x400 弹在 CardHoverPreview 上,同时让 FanLayout 把手牌里这张标出来(默认把其余手牌压暗)。
///
/// 预览弹在哪儿由**本组件算好中点再交给 CardHoverPreview**:
///   · 手牌:弹在这张牌的**正上方**(下沿离卡牌顶边 previewOffset 像素,水平方向对齐卡牌中点)。
///     弹在上方是刻意的 —— 预览有 400 高,往下弹会盖住整条手牌和扇形。
///   · 战场:也弹在上方,并**按中轴分左右**(见 CardHoverPreview.PlaceNear)—— 中轴左边的卡弹在右边、
///     右边的卡弹在左边、正压在中轴上的弹在右边,免得大卡把看的那张自己盖住。
///
/// 卡牌自己不再上浮/放大/回正:手牌扇形在悬停期间一动不动,位置由 FanLayout 通过 SetHome 喂进来;
/// 战场的卡也不能动(位置就是排里的格位)。预览是独立的一层,所以也不需要再抢图层。
/// </summary>
///
/// 【挂载 & 调整】
///   挂在:**两份卡面预制体的根物体上**,和 CardDisplay / CardDragPlay 同一个物体:
///     · Card.prefab 的根物体「Card」—— 手牌 + 悬停预览实例;
///     · CardsInBattle.prefab 的根物体「CardsInBattle」—— 战场卡面。
///     三种实例上原始的 CardHover 都会被运行时处理:
///       · 手牌:照常工作,由 FanLayout.Normalize 喂 fan(预览弹出时给手牌打高亮)。
///       · 战场卡:CardsInBattle 上这一份**开着**,战场卡也能悬停看完整信息。
///         它的 Data 绑的是卡面数据、FieldUnit 绑的是场上当前值 —— 悬停预览显示当前攻/血。
///       · 预览卡:enabled = false,预览卡自己不再响应悬停。
///       · 对局战报(BattleMessageUI)展示用的卡:enabled = false 之后还被 Destroy 掉了。
///   引用:没有手连引用。依赖全是运行时拿的:RectTransform 和 CardDisplay 在 Awake 里 GetComponent;
///     FanLayout 由 FanLayout.Add → Normalize → Bind(this) 喂进来(只有手牌会有);
///     FieldUnit 由 FieldUnit.Init 喂进来(只有战场卡会有),用来读当前攻/血。
///     拿不到会怎样:display 为 null 或 Data 还没绑 → ShowPreview 直接返回,预览不弹,不报错;
///     fan 为 null → 预览照弹,只是手牌不压暗/不隐藏(战场卡就是没有fan的正常情况);
///     CardHoverPreview.Instance 为 null → 警告一次「场景里没有 CardHoverPreview」,预览不显示。
///   常调:
///     · previewDelay:手牌停留几秒才弹预览(Card.prefab 上被覆盖成 0.5)。
///       调小 = 更容易触发(0.1 左右几乎一碰就弹,鼠标扫过手牌会一直闪预览);调大 = 更像「刻意查看」,
///       但太长(>5 秒)会被当成没反应。计时走 unscaledTime,和 Time.timeScale 无关。
///     · fieldPreviewDelay:战场卡的停留秒数(CardsInBattle 上被覆盖成 0.25)。
///       战场摆着一排卡,鼠标经常只是路过,这一档建议比手牌更短(扫过就出,不用刻意停),
///       但别调到 0 —— 那会让鼠标划过整排卡时预览狂闪。
///     · previewOffset:预览下沿离卡牌视觉顶边多少像素(手牌 8、战场 12)。
///       调大 = 预览离卡更远(不容易看错是哪张);调小甚至负值 = 贴上去,可能盖住卡名。
///     · 预览出现在哪儿、多大、淡入多快都不在这里:位置由 CardHoverPreview.PlaceNear 算,尺寸在它身上。
public class CardHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [Header("悬停预览")]
    [Tooltip("手牌要在同一张牌上停留多少秒才弹出预览")]
    [SerializeField] private float previewDelay = 3f;

    [Tooltip("战场卡要在同一张牌上停留多少秒才弹出预览(战场建议比手牌短一点)")]
    [SerializeField] private float fieldPreviewDelay = 0.25f;

    [Tooltip("预览下沿离卡牌视觉顶边多少像素")]
    [SerializeField] private float previewOffset = 8f;

    /// <summary>鼠标现在是否停在这张牌上</summary>
    public bool IsHovered { get; private set; }

    /// <summary>预览是不是已经弹出来了(鼠标移开 / 卡被销毁时会收起)</summary>
    public bool IsPreviewing { get; private set; }

    /// <summary>本组件挂在手牌上还是战场卡上(CardHoverPreview 按它决定预览的落点规则)</summary>
    public CardViewMode ViewMode { get; private set; } = CardViewMode.Hand;

    /// <summary>这张牌当前的视觉尺寸(已乘 localScale)。预览靠它贴到卡牌顶边上方</summary>
    public Vector2 VisualSize
    {
        get
        {
            var r = rt != null ? rt.rect.size : new Vector2(150f, 200f);
            float s = Mathf.Abs(transform.lossyScale.x);
            if (s <= 0.0001f) s = 1f;
            return r * s;
        }
    }

    private static bool warnedMissingHost;

    private float hoverTime;
    private RectTransform rt;
    private CardDisplay display;
    private FanLayout fan;

    // 战场卡才有的场上数据(当前攻/血)。手牌是 null
    private FieldUnit fieldUnit;

    // 拖动出牌期间抑制预览:牌跟着指针乱跑,这时候弹预览会挡住落点
    private bool suppressed;

    private void Awake()
    {
        rt = (RectTransform)transform;
        display = GetComponent<CardDisplay>();
    }

    /// <summary>FanLayout 在 Add 的时候调用:预览弹出时靠它给手牌打高亮(只有手牌会有)</summary>
    public void Bind(FanLayout owner) => fan = owner;

    /// <summary>
    /// FieldUnit 落位时调用:让悬停预览显示**场上当前**的攻/血而不是卡面数值,
    /// 并按战场布局走(落点规则、停留秒数都换成战场那一档)。
    /// </summary>
    public void Bind(FieldUnit unit)
    {
        fieldUnit = unit;
        ViewMode = CardViewMode.Field;
        hoverTime = 0f;

        // 战场卡位置就是格位,不能像手牌那样被 SetHome 摆位置;预览只是额外的一层
        if (IsPreviewing) DismissPreview();
    }

    /// <summary>拖动卡牌期间抑制悬停预览(松手后恢复计时)</summary>
    public void SetSuppressed(bool value)
    {
        if (suppressed == value) return;
        suppressed = value;
        if (suppressed) CancelPreview();
        else hoverTime = 0f;      // 松手后重新数
    }

    /// <summary>立刻收起已经弹出来的预览</summary>
    public void CancelPreview() => DismissPreview();

    /// <summary>FanLayout 每次排完扇形调用(手牌专用)。悬停不再改变卡牌外观,所以直接归位</summary>
    public void SetHome(Vector2 pos, float rot)
    {
        if (ViewMode != CardViewMode.Hand) return;      // 战场卡的位置归排里的布局组管
        rt.anchoredPosition = pos;
        rt.localRotation = Quaternion.Euler(0f, 0f, rot);
    }

    public void OnPointerEnter(PointerEventData e)
    {
        IsHovered = true;
        hoverTime = 0f;
    }

    public void OnPointerExit(PointerEventData e)
    {
        IsHovered = false;
        DismissPreview();
    }

    // 计时走 unscaled:游戏暂停/慢放时悬停预览该弹还是弹
    private void Update()
    {
        if (!IsHovered || IsPreviewing || suppressed) return;

        hoverTime += Time.unscaledDeltaTime;
        if (hoverTime < DelayFor(ViewMode)) return;

        IsPreviewing = true;        // 只尝试一次:配错了也不至于每帧刷日志
        ShowPreview();
    }

    private float DelayFor(CardViewMode mode) =>
        mode == CardViewMode.Field ? Mathf.Max(0f, fieldPreviewDelay) : Mathf.Max(0f, previewDelay);

    private void ShowPreview()
    {
        var host = CardHoverPreview.Instance;
        if (host == null)
        {
            if (!warnedMissingHost)
            {
                warnedMissingHost = true;
                Debug.LogWarning(
                    "[CardHover] 场景里没有 CardHoverPreview(HandUI 开局会在 hover 上补一个)," +
                    "悬停预览不会显示。", this);
            }
            IsPreviewing = false;
            return;
        }

        if (display == null || display.Data == null) { IsPreviewing = false; return; }

        if (!host.Show(display.Data, ViewMode, rt, VisualSize, previewOffset))
        {
            IsPreviewing = false;
            return;
        }

        // 战场卡的预览要显示场上当前值,不是卡面数值
        if (ViewMode == CardViewMode.Field && fieldUnit != null)
            host.ShowUnitStats(fieldUnit.Atk, fieldUnit.Hp, fieldUnit.MaxHp);

        if (fan != null) fan.SetSpotlight(rt);      // 手牌里标出"现在看的是这张"
    }

    private void DismissPreview()
    {
        hoverTime = 0f;
        if (!IsPreviewing) return;
        IsPreviewing = false;

        var host = CardHoverPreview.Instance;
        if (host != null) host.Hide();
        if (fan != null) fan.SetSpotlight(null);
    }

    private void OnDisable()
    {
        // 卡被打出去 / 手牌清空:预览和高亮都要跟着收,否则会留着一张牌亮在那儿
        IsHovered = false;
        DismissPreview();
    }
}
