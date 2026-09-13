using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 卡牌悬停:鼠标在同一张手牌上停留超过 previewDelay 秒(默认 3 秒),
/// 就把这张牌放大到 300x400 显示在场景里的 hover 位置(CardHoverPreview),
/// 同时让 FanLayout 把手牌里这张标出来(默认把其余手牌压暗)。
///
/// 卡牌自己不再上浮/放大/回正 —— 手牌扇形在悬停期间一动不动,位置由 FanLayout 通过
/// SetHome 喂进来。预览本身显示在 hover 那一格,不会盖住手牌,所以也不需要再抢图层。
/// </summary>
///
/// 【挂载 & 调整】
///   挂在:Card.prefab 的根物体「Card」上,和 CardDisplay / CardDragPlay 同一个物体。
///     手牌实例由 HandUI 生成(父物体 HandArea1),战场小卡由 BattlefieldManager 生成,
///     预览卡由 CardHoverPreview 生成 —— 三种实例上原始的 CardHover 都会被运行时处理:
///       · 战场小卡:enabled = allowFieldHoverPreview(默认 false),也就是【组件被禁用】,
///         所以战场牌不会弹预览,而且 OnPointerEnter 不会再触发。
///       · 预览卡:enabled = false,预览卡自己不再响应悬停。
///       · 对局战报(BattleMessageUI)展示用的卡:enabled = false 之后还被 Destroy 掉了。
///     要测手感就用编辑器测试卡(见 CardDisplay 的右键菜单)或在场景里临时放一个 Card.prefab 实例。
///   引用:没有手连引用。三个依赖都是运行时拿的:RectTransform 和 CardDisplay 在 Awake 里
///     GetComponent;FanLayout 由 FanLayout.Add → Normalize → Bind(this) 喂进来。
///     拿不到会怎样:display 为 null 或 Data 还没绑 → ShowPreview 直接返回,预览不弹,不报错;
///     fan 为 null → 预览照弹,只是手牌不压暗/不隐藏,而且没有任何提示,不好查。
///   常调:
///     · previewDelay:停留几秒才弹预览。调小 = 更容易触发(0.5 以上手感正常,0.1 左右几乎一碰就弹,
///       鼠标扫过手牌会一直闪预览);调大 = 更像「刻意查看」,但太长(>5 秒)会被当成没反应。
///       计时走 unscaledTime,游戏暂停/慢放时照样计时,和 Time.timeScale 无关。
///     · 本组件的量都在 Card.prefab 上(previewDelay 就在预制体根)。注意脚本里的 = 3f 只是新建实例的默认值:
///       Card.prefab 上被覆盖成了 0.5,改脚本默认值不会影响已经烘好的预制体。
///     · 预览出现在哪儿不在这里调:hoverPreviewRoot(HandUI)→ 场景里的 hover(300x400);
///       预览多大、淡入多快在 CardHoverPreview 上。
public class CardHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [Header("悬停预览")]
    [Tooltip("要在同一张牌上停留多少秒才弹出预览")]
    [SerializeField] private float previewDelay = 3f;

    /// <summary>鼠标现在是否停在这张牌上</summary>
    public bool IsHovered { get; private set; }

    /// <summary>预览是不是已经弹出来了(鼠标移开 / 卡被销毁时会收起)</summary>
    public bool IsPreviewing { get; private set; }

    private static bool warnedMissingHost;

    private float hoverTime;
    private RectTransform rt;
    private CardDisplay display;
    private FanLayout fan;

    // 拖动出牌期间抑制预览:牌跟着指针乱跑,这时候弹预览会挡住落点
    private bool suppressed;

    private void Awake()
    {
        rt = (RectTransform)transform;
        display = GetComponent<CardDisplay>();
    }

    /// <summary>FanLayout 在 Add 的时候调用:预览弹出时靠它给手牌打高亮</summary>
    public void Bind(FanLayout owner) => fan = owner;

    /// <summary>拖动卡牌期间抑制悬停预览(松手后恢复计时)</summary>
    public void SetSuppressed(bool value)
    {
        if (suppressed == value) return;
        suppressed = value;
        if (suppressed) CancelPreview();
        else hoverTime = 0f;      // 松手后重新数 3 秒
    }

    /// <summary>立刻收起已经弹出来的预览</summary>
    public void CancelPreview() => DismissPreview();

    /// <summary>FanLayout 每次排完扇形调用。悬停不再改变卡牌外观,所以直接归位</summary>
    public void SetHome(Vector2 pos, float rot)
    {
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
        if (hoverTime < previewDelay) return;

        IsPreviewing = true;        // 只尝试一次:配错了也不至于每帧刷日志
        ShowPreview();
    }

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

        if (!host.Show(display.Data)) { IsPreviewing = false; return; }

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
