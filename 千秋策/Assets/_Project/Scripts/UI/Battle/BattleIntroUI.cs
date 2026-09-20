using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;

/// <summary>
/// 开局先手提示:进战斗后先弹一个遮罩,大字告诉玩家"谁先手",点掉之后对局才真正开始。
///
/// 【为什么要有它】先手是随机的(见 TurnController.firstSide),但原来这个结果只进了回合流转,
/// 玩家看不到 —— 一进战斗就直接是某一方的第 1 回合,很容易懵"怎么对方先动了"。
/// 把结果摆出来再开局,玩家对自己的处境有预期。
///
/// 【怎么保证"提示期间回合不偷偷开始"】
///   TurnController 把开局拆成了两步:
///     · DecideFirstSide()  —— 摇出先手并广播 FirstSideDecided(不开始回合);
///     · BeginTurnWithDecidedFirstSide() —— 真的开第 1 回合。
///   这个界面订阅前者、在玩家点掉时调后者,所以遮罩盖着的时候回合一定没开始。
///
/// 挂在:Battle.unity 的 BattleCanvas 上。没挂也能跑(GameBootstrap 会补,Instance 也会自举),
///       但那样字号/时长只能用默认值。
/// 引用:没有要手连的引用,所有 UI 都是运行时建的。
/// 常调:
///   · showSeconds:遮罩至少停多久。0 = 一直等玩家点。
///   · autoDismissSeconds:自动消失的等待时间(让玩家有时间看清)。0 = 不自动,必须点。
///   · 颜色、字号都是下面的 [Header] 字段。
/// </summary>
[DisallowMultipleComponent]
public class BattleIntroUI : MonoBehaviour
{
    private static BattleIntroUI instance;

    public static BattleIntroUI Instance
    {
        get
        {
            if (instance != null) return instance;
            instance = FindObjectOfType<BattleIntroUI>();
            if (instance != null) return instance;

            var go = new GameObject("BattleIntroUI");
            var canvas = CanvasUtil.FindRootCanvas();
            if (canvas != null) go.transform.SetParent(canvas.transform, false);
            instance = go.AddComponent<BattleIntroUI>();
            return instance;
        }
    }

    [Header("外观")]
    [Tooltip("遮罩颜色(半透明黑,越黑越强调)")]
    [SerializeField] private Color overlayColor = new Color(0f, 0f, 0f, 0.78f);
    [Tooltip("「我方先手 / 敌方先手」大字字号")]
    [SerializeField] private float titleFontSize = 88f;
    [Tooltip("下方小字(说明怎么继续)字号")]
    [SerializeField] private float hintFontSize = 28f;
    [Tooltip("我方先手时大字颜色(金)")]
    [SerializeField] private Color localColor = new Color(1f, 0.86f, 0.42f);
    [Tooltip("敌方先手时大字颜色(冷蓝,和金色一眼能分开)")]
    [SerializeField] private Color enemyColor = new Color(0.62f, 0.78f, 1f);

    [Header("节奏")]
    [Tooltip("遮罩出现后至少停留几秒才允许点掉(0 = 立刻可点)")]
    [SerializeField] private float showSeconds = 0.45f;
    [Tooltip("自动消失的等待秒数(给玩家看清的时间)。0 = 不自动消失,必须点一下")]
    [SerializeField] private float autoDismissSeconds = 2.6f;

    private GameObject root;
    private bool dismissed;
    private bool canDismiss;

    private void Awake() => instance = this;

    private void OnEnable() => TurnController.FirstSideDecided += OnFirstSideDecided;

    private void OnDisable() => TurnController.FirstSideDecided -= OnFirstSideDecided;

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private void OnFirstSideDecided(TurnController.Side first)
    {
        // 已经开起来了(比如直接从 Battle 场景调试、先手早就定过)就不再插一脚
        var turn = TurnController.Instance;
        if (turn != null && turn.TurnInProgress) return;

        Build(first == TurnController.Side.Local);
        StartCoroutine(EnableDismissAndAutoClose());
    }

    /// <summary>先把遮罩立起来,过 showSeconds 才允许点掉;autoDismissSeconds 到了自己关</summary>
    private IEnumerator EnableDismissAndAutoClose()
    {
        yield return new WaitForSecondsRealtime(showSeconds);
        canDismiss = true;

        if (autoDismissSeconds > 0f)
        {
            yield return new WaitForSecondsRealtime(autoDismissSeconds);
            Dismiss();
        }
    }

    private void Update()
    {
        // 点任意处 / 按任意常见"确认"键都能继续
        if (!canDismiss || dismissed) return;

        if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) ||
            Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) ||
            Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Escape))
        {
            Dismiss();
        }
    }

    /// <summary>收起遮罩,并真正开始第 1 回合</summary>
    public void Dismiss()
    {
        if (dismissed) return;
        dismissed = true;

        var turn = TurnController.Instance;
        if (turn != null) turn.BeginTurnWithDecidedFirstSide();

        if (root != null)
        {
            var fading = root.GetComponent<CanvasGroup>();
            if (fading != null)
                fading.DOFade(0f, 0.25f).SetUpdate(true).OnComplete(() => { if (root != null) Destroy(root); });
            else
                Destroy(root);
        }

        enabled = false;    // 收工,不用再每帧听输入
    }

    // ================================================================ 建界面

    private void Build(bool localFirst)
    {
        var canvas = CanvasUtil.FindRootCanvas();
        var parent = canvas != null ? (RectTransform)canvas.transform : (RectTransform)transform;

        root = new GameObject("BattleIntroPanel", typeof(RectTransform), typeof(CanvasGroup));
        var rootRect = (RectTransform)root.transform;
        rootRect.SetParent(parent, false);
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;
        root.transform.SetAsLastSibling();      // 盖在战场和手牌上面

        // 遮罩:顺便吃掉点击,免得还在提示的时候手已经点到卡牌了
        var overlay = new GameObject("Overlay", typeof(RectTransform), typeof(Image));
        var ovRect = (RectTransform)overlay.transform;
        ovRect.SetParent(rootRect, false);
        ovRect.anchorMin = Vector2.zero;
        ovRect.anchorMax = Vector2.one;
        ovRect.offsetMin = Vector2.zero;
        ovRect.offsetMax = Vector2.zero;
        overlay.GetComponent<Image>().color = overlayColor;

        // 大字:谁先手
        var title = RuntimeText.Create(rootRect, "IntroTitle",
            localFirst ? "我 方 先 手" : "敌 方 先 手", titleFontSize,
            TextAlignmentOptions.Center, localFirst ? localColor : enemyColor);
        var tRect = (RectTransform)title.transform;
        tRect.anchorMin = tRect.anchorMax = new Vector2(0.5f, 0.5f);
        tRect.pivot = new Vector2(0.5f, 0.5f);
        tRect.sizeDelta = new Vector2(1200f, 140f);
        tRect.anchoredPosition = new Vector2(0f, 40f);

        // 小字:怎么继续
        var hint = RuntimeText.Create(rootRect, "IntroHint",
            localFirst ? "由你先行动　·　点一下开始" : "敌方先行动　·　点一下开始", hintFontSize,
            TextAlignmentOptions.Center, new Color(0.88f, 0.86f, 0.8f));
        var hRect = (RectTransform)hint.transform;
        hRect.anchorMin = hRect.anchorMax = new Vector2(0.5f, 0.5f);
        hRect.pivot = new Vector2(0.5f, 0.5f);
        hRect.sizeDelta = new Vector2(1200f, 60f);
        hRect.anchoredPosition = new Vector2(0f, -60f);

        Debug.Log($"[先手] {(localFirst ? "我方" : "敌方")}先手,提示已弹出,点掉之后开局");
    }
}
