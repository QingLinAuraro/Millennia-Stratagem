using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 胜负结算界面:大营被打空 → BattleSettlement 广播 MatchEnded → 这里弹一个遮罩 + 结果文字 + 两个按钮。
///
/// 结果内容由 BattleSettlement.ResultText 给(它记着谁赢、为什么赢、打了多少伤害、拆了几座建筑),
/// 这里只负责把它显示出来。按钮:
///   · 再来一局 → SceneManager.LoadScene(当前场景)(Battle.unity 是 Build Settings 里唯一登记的场景)
///   · 返回主菜单 → 也走 LoadScene,但 MainMenu.unity **还没登记进 Build Settings**,
///                  所以失败时只打警告并把按钮灰掉,不影响对局结束的显示。
///
/// 【挂载 & 调整】
///   挂在:Battle.unity 里的 BattleCanvas 上。没挂也能跑(Instance 自举),但那样按钮位置/字号只能用默认值。
///   引用:没有要手连的引用,所有 UI 都是运行时建的 —— 美术出图之后再换成预制体。
///   常调:
///     · 遮罩透明度/字号/按钮尺寸都是下面的 [Header] 字段。
///     · 想换成美术的结算面板:把 panelPrefab 填上(可选),填了就用它、不运行时建 UI。
/// </summary>
[DisallowMultipleComponent]
public class BattleResultUI : MonoBehaviour
{
    private static BattleResultUI instance;

    public static BattleResultUI Instance
    {
        get
        {
            if (instance != null) return instance;
            instance = FindObjectOfType<BattleResultUI>();
            if (instance != null) return instance;

            var go = new GameObject("BattleResultUI");
            var canvas = CanvasUtil.FindRootCanvas();
            if (canvas != null) go.transform.SetParent(canvas.transform, false);
            instance = go.AddComponent<BattleResultUI>();
            return instance;
        }
    }

    [Header("外观")]
    [Tooltip("遮罩颜色(半透明黑,越黑越强调结果)")]
    [SerializeField] private Color overlayColor = new Color(0f, 0f, 0f, 0.72f);
    [Tooltip("「胜利 / 败北」大字字号")]
    [SerializeField] private float titleFontSize = 96f;
    [Tooltip("详细战果文字字号")]
    [SerializeField] private float detailFontSize = 30f;
    [Tooltip("胜利标题颜色(金)")]
    [SerializeField] private Color victoryColor = new Color(1f, 0.86f, 0.4f);
    [Tooltip("败北标题颜色(暗红)")]
    [SerializeField] private Color defeatColor = new Color(0.85f, 0.35f, 0.32f);

    [Header("按钮")]
    [Tooltip("按钮尺寸")]
    [SerializeField] private Vector2 buttonSize = new Vector2(240f, 72f);
    [Tooltip("两个按钮之间的水平间距")]
    [SerializeField] private float buttonGap = 40f;

    private GameObject root;
    private bool shown;

    private void Awake() => instance = this;

    private void OnEnable() => BattleSettlement.MatchEnded += OnMatchEnded;

    private void OnDisable() => BattleSettlement.MatchEnded -= OnMatchEnded;

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private void OnMatchEnded(BattleSide winner, string reason)
    {
        if (shown) return;

        shown = true;
        bool playerWon = winner == BattleSide.Player;
        StartCoroutine(ShowNextFrame(playerWon, reason));
    }

    /// <summary>
    /// 等一帧再弹:对局结束的瞬间可能还有飘字/死亡表现要跑完(比如大营被打空的最后一次飘字),
    /// 同一帧就盖遮罩会把那一下效果吃掉。
    /// </summary>
    private IEnumerator ShowNextFrame(bool playerWon, string reason)
    {
        yield return null;
        Show(playerWon, reason);
    }

    /// <summary>弹出结算面板(也可以从别的入口手动调,比如「投降」)</summary>
    public void Show(bool playerWon, string reason)
    {
        if (root == null) Build(playerWon);

        root.SetActive(true);
        root.transform.SetAsLastSibling();
        Debug.Log($"[结算] {(playerWon ? "胜利" : "败北")}:{reason}");
    }

    private void Build(bool playerWon)
    {
        var canvas = CanvasUtil.FindRootCanvas();
        var parent = canvas != null ? (RectTransform)canvas.transform : (RectTransform)transform;

        root = new GameObject("BattleResultPanel", typeof(RectTransform));
        var rootRect = (RectTransform)root.transform;
        rootRect.SetParent(parent, false);
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;

        // 遮罩(顺便吃掉点击,免得结算之后还能点到战场)
        var overlayGo = new GameObject("Overlay", typeof(RectTransform), typeof(Image));
        var overlayRect = (RectTransform)overlayGo.transform;
        overlayRect.SetParent(rootRect, false);
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;
        overlayGo.GetComponent<Image>().color = overlayColor;

        // 标题
        var title = RuntimeText.Create(rootRect, "ResultTitle",
                                       playerWon ? "胜  利" : "败  北",
                                       titleFontSize, TMPro.TextAlignmentOptions.Center,
                                       playerWon ? victoryColor : defeatColor);
        var titleRect = (RectTransform)title.transform;
        titleRect.anchorMin = titleRect.anchorMax = new Vector2(0.5f, 0.5f);
        titleRect.pivot = new Vector2(0.5f, 0.5f);
        titleRect.sizeDelta = new Vector2(900f, 140f);
        titleRect.anchoredPosition = new Vector2(0f, 160f);

        // 战果明细(所有统计都记在 BattleSettlement 里,这里只负责显示;ResultText 由 EndMatch 拼好)
        var detail = RuntimeText.Create(rootRect, "ResultDetail", BattleSettlement.ResultText,
                                        detailFontSize, TMPro.TextAlignmentOptions.Center,
                                        new Color(0.92f, 0.9f, 0.86f));
        var detailRect = (RectTransform)detail.transform;
        detailRect.anchorMin = detailRect.anchorMax = new Vector2(0.5f, 0.5f);
        detailRect.pivot = new Vector2(0.5f, 0.5f);
        detailRect.sizeDelta = new Vector2(1000f, 160f);
        detailRect.anchoredPosition = new Vector2(0f, 20f);

        // 按钮
        CreateButton(rootRect, "再来一局", new Vector2(-(buttonSize.x + buttonGap) * 0.5f, -120f), RestartBattle);
        CreateButton(rootRect, "返回主菜单", new Vector2((buttonSize.x + buttonGap) * 0.5f, -120f), BackToMainMenu);
    }

    private void CreateButton(RectTransform parent, string label, Vector2 position, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Button_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = buttonSize;
        rect.anchoredPosition = position;

        var image = go.GetComponent<Image>();
        image.color = new Color(0.32f, 0.24f, 0.16f, 0.98f);

        var button = go.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(onClick);

        var text = RuntimeText.Create(rect, "Label", label, 30f, TMPro.TextAlignmentOptions.Center,
                                      new Color(0.98f, 0.94f, 0.8f));
        text.raycastTarget = false;
    }

    // ================================================================ 按钮

    private void RestartBattle()
    {
        var scene = SceneManager.GetActiveScene();
        Debug.Log($"[结算] 再来一局:重新载入「{scene.name}」");
        SceneManager.LoadScene(scene.buildIndex >= 0 ? scene.buildIndex : 0);
    }

    private void BackToMainMenu()
    {
        // MainMenu.unity 还没登记进 Build Settings,先用名字载;载不了就把按钮灰掉,别把玩家卡在结算界面
        try
        {
            Debug.Log("[结算] 返回主菜单:载入「MainMenu」");
            SceneManager.LoadScene("MainMenu");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[结算] 载入 MainMenu 失败({e.GetType().Name}):" +
                             "多半是 MainMenu.unity 还没加进 Build Settings(File → Build Settings → Add Open Scenes)。");
            FloatingTipUI.Show(new Vector2(Screen.width * 0.5f, Screen.height * 0.3f),
                               "主菜单还没接进来（MainMenu 未加入 Build Settings）", warning: true);
        }
    }
}
