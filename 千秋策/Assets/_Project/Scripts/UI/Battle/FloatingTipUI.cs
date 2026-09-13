using DG.Tweening;
using TMPro;
using UnityEngine;

/// <summary>
/// 飘字提示(策划案§10.3:非法反馈用「变灰 / 抖动 / 飘字」表达,不许弹窗打断操作)。
///
/// 用法:FloatingTipUI.Show(pointer.position, "中军已满，无法部署", warning: true);
/// 第一次用到时才在画布里建一个容器(盖在手牌和悬停预览之上),文字上浮 + 淡出后自销毁。
///
/// 【挂载 & 调整】
///   挂在:场景和预制体里都没有,也不用手动挂 —— 靠自举,谁先访问 FloatingTipUI.Instance 或调用 FloatingTipUI.Show 谁来建:
///         先 FindObjectOfType 找现成的 FloatingTipUI,找不到就新建一个名为 FloatingTips 的物体(自带 RectTransform),
///         挂到 CanvasUtil.FindRootCanvas() 找到的根画布下(Battle 场景里是 BattleCanvas),
///         给它 AddComponent 上 FloatingTipUI,然后四个锚点拉满整块画布、SetAsLastSibling() 排到最上层,
///         保证飘字盖住手牌和 hover 预览;容器建好后就一直留着,不销毁。
///         每一次飘字都是在这个容器下现建一个名为 Tip 的文本(RuntimeText.Create),上浮 + 淡出播完自己销毁。
///   引用:没有任何要手连的引用 —— 下面 6 个字段全是数值和颜色,没有物体引用,所以不连也不会断。
///         隐式依赖两处:
///         · 容器必须是画布的直接子物体:Spawn 里把 transform.parent 当作画布用,拿不到画布(场景里一个 Canvas 都没有,
///           或者容器被挪到别的父物体下)就警告「飘字容器不在画布下,飘不出来」,然后一个字都不显示。
///           另外 FindObjectOfType 找 Canvas 的返回顺序不保证:手牌里的 Card.prefab 根节点自带嵌套 Canvas,
///           万一自举时先找到的是某张牌的那个画布,容器就会挂在牌下面(飘字跟着牌走)。真遇到位置/层级不对,
///           就在 BattleCanvas 下手动建一个空物体挂上 FloatingTipUI,自举分支就不会再走。
///         · 文字靠 RuntimeText 借场景里已有的 TMP 字体(优先挑有中文字形的那个,认字「策」),借不到会警告「可能显示不出来」。
///         还有一点:场景里没有这个组件,Inspector 就没得调,下面这些参数实际生效的就是脚本里写的默认值。
///   常调:
///     · fontSize(默认 34):字号。调大更醒目,但一行放不下会折成多行(宽度受 tipSize 限制);调小在高分辨率下看不清。
///     · tipSize(默认 660x80):飘字文本框的尺寸,也就是文字的换行宽度。文案长(例如「需要指定目标:xxx」)就把宽度调大,不然一句话会折成三四行。
///     · riseDistance(默认 70):往上飘多远(画布单位)。调大更显眼,但容易飘出画布可视区
///       (容器没有遮罩,不会被裁切,只是飘到看不见的地方);调 0 = 原地淡出。
///     · duration(默认 1.1 秒):整段时长。调大 = 看得更清楚,但连续非法操作时会同时飘好几条叠在一起;
///       调到 0.4 以下基本读不完。淡出固定在整段的 45% 处开始、占后 55%,改 duration 会连淡出时段一起变。
///     · normalColor / warningColor:普通提示与失败提示的字色,由 Show 的 warning 参数决定用哪个(warningColor 默认偏红)。
///       非法反馈要更醒目就调 warningColor —— 策划案§10.3 要求用变灰/抖动/飘字表达,不许弹窗打断操作。
///     · 传进来的 screenPosition:飘字直接用屏幕坐标定位(内部换算成画布局部坐标),想让提示从某个按钮或卡牌旁边冒出来,
///       把那个位置的屏幕坐标传进来即可;坐标换算失败时兜底落在画布中心(anchoredPosition 归零)。
/// </summary>
[DisallowMultipleComponent]
public class FloatingTipUI : MonoBehaviour
{
    private static FloatingTipUI instance;

    /// <summary>场景里没有就自己建(挂在 Canvas 下),用到才建</summary>
    public static FloatingTipUI Instance
    {
        get
        {
            if (instance != null) return instance;
            instance = FindObjectOfType<FloatingTipUI>();
            if (instance != null) return instance;

            var go = new GameObject("FloatingTips", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            var canvas = CanvasUtil.FindRootCanvas();
            rt.SetParent(canvas != null ? canvas.transform : null, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.SetAsLastSibling();     // 盖在手牌、hover 预览之上
            instance = go.AddComponent<FloatingTipUI>();
            return instance;
        }
    }

    [Header("飘字")]
    [Tooltip("字号,默认 34。调大更醒目,但一行放不下会折行")]
    [SerializeField] private float fontSize = 34f;
    [Tooltip("飘字文本框尺寸(默认 660x80),也就是文字的换行宽度。文案长就把宽度调大")]
    [SerializeField] private Vector2 tipSize = new Vector2(660f, 80f);
    [Tooltip("往上飘多远(画布单位)")]
    [SerializeField] private float riseDistance = 70f;
    [Tooltip("整段时长(秒),默认 1.1:上浮走完整段,淡出从 45% 处开始、占后 55%")]
    [SerializeField] private float duration = 1.1f;
    [Tooltip("普通提示的字色(Show 的 warning 传 false 时用)")]
    [SerializeField] private Color normalColor = new Color(0.98f, 0.95f, 0.82f);
    [Tooltip("失败提示的字色(Show 的 warning 传 true 时用),默认偏红")]
    [SerializeField] private Color warningColor = new Color(1f, 0.45f, 0.4f);

    /// <summary>在某个屏幕坐标飘一句话(warning = 红字,用于失败提示)</summary>
    public static void Show(Vector2 screenPosition, string message, bool warning = false)
    {
        if (string.IsNullOrEmpty(message)) return;
        Instance.Spawn(screenPosition, message, warning);
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private void Spawn(Vector2 screenPosition, string message, bool warning)
    {
        var self = (RectTransform)transform;
        var canvas = self.parent as RectTransform;      // BattleCanvas
        if (canvas == null) { Debug.LogWarning("[FloatingTipUI] 飘字容器不在画布下,飘不出来。", this); return; }

        var text = RuntimeText.Create(self, "Tip", message, fontSize, TextAlignmentOptions.Center,
                                     warning ? warningColor : normalColor);
        var rt = (RectTransform)text.transform;

        // 锚点放在画布轴心上 → ScreenPointToLocalPointInRectangle 算出来的局部坐标可以直接当 anchoredPosition 用
        rt.anchorMin = rt.anchorMax = canvas.pivot;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = tipSize;

        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, screenPosition, RuntimeText.EventCameraFor(canvas), out var local))
            rt.anchoredPosition = local;
        else
            rt.anchoredPosition = Vector2.zero;

        var group = text.gameObject.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;       // 飘字不吃点击
        group.interactable = false;

        // 上浮 + 后半程淡出
        var seq = DOTween.Sequence();
        seq.Append(rt.DOAnchorPos(rt.anchoredPosition + new Vector2(0f, riseDistance), duration).SetEase(Ease.OutCubic));
        seq.Insert(duration * 0.45f, group.DOFade(0f, duration * 0.55f));
        seq.SetUpdate(true);                // 不吃 timeScale(以后做暂停也不影响飘字)
        seq.OnComplete(() => { if (text != null) Destroy(text.gameObject); });
    }
}
