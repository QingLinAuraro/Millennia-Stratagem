using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 战斗界面这几个按钮:设置、投降。
///
/// 场景里它们已经是货真价实的 Button 物体了(Settings / Surrender,带自己的 Image 和文字子物体),
/// 所以这个脚本只做两件事:
///   1. 把它们找出来(Inspector 里填了就直接用,没填按名字找);
///   2. 把 onClick 接到流程上 —— 设置面板和投降结算都还没做,先留两个事件钩子。
///
/// 「结束回合」(NextRound)不在这里:它的可点状态要跟着回合切(对方回合要变灰),
/// 那份状态在 CommandPointController 手里(它同时管着 CP HUD),所以由它绑。
///
/// 以后做设置面板 / 投降结算时,订阅 SettingsClicked / SurrenderRequested 就行,
/// 不用再动按钮本身的接线。
///
/// 【挂载 & 调整】
///   挂在:BattleCanvas 上 —— 场景里已经挂好,和 TurnController / CommandPointController / EnemyDeckController 同一个物体。
///         Settings 和 Surrender 本身是 BattleCanvas 下两个独立的 Button 物体,不用给它们加脚本,挂在这个脚本上的只有 BattleCanvas。
///   引用:settingsButton / surrenderButton 场景里都已经连好(Settings、Surrender 上的 Button 组件),现在不用动。
///         · 留空且 autoWireByName 打开 → 按常量 SettingsObjectName(「Settings」)/ SurrenderObjectName(「Surrender」)找:
///           先 GameObject.Find(区分大小写),不中再在所有 Button(含未激活的)里做一次忽略大小写的比对,还找不到就警告「场景里找不到按钮」。
///         · 找不到的后果:那个按钮点了完全没反应 —— 点击回调只有这个脚本在接(Button 自己的 onClick 是空的),
///           引用没解析出来就没接上,连「点了设置 / 点了投降」那条 Debug.Log 都不会有,SettingsClicked / SurrenderRequested 更是永远不会触发。
///         · 引用只在 Awake 里解析一次:运行中才创建(或改名成 Settings / Surrender)的按钮不会被重新接上,得手连引用。
///         · 它不会自己造按钮 —— 和 CommandPointController 给没有 Button 的占位图补一个按钮的行为不同。
///   常调:
///     · autoWireByName(默认开):关掉之后就完全只认手连的引用,引用留空 = 按钮点了没反应。
///       只有在「不想让脚本按名字认领同名物体」时才关(例如场景里另有一个叫 Settings 的装饰物)。
///     · 两个按钮引用:场景里给按钮改名、换物体、或者整体换一套 HUD 之后,要么把引用重新拖上,
///       要么保持 autoWireByName 打开并让物体名和代码里的常量一致(常量写死在 BattleHudButtons 里,改名得同时改代码)。
///     · 事件钩子 SettingsClicked / SurrenderRequested:设置面板和投降结算都还没做,现在点下去只有一条 Debug.Log。
///       以后做面板/结算时订阅这两个事件即可;投降没有二次确认,要加确认弹窗就在订阅方加。
///     · 按钮的可点状态和显隐不归这里管:脚本从不改 Interactable,想临时屏蔽投降按钮,直接在场景里把那个 Button 的 Interactable 关掉就行,不会和脚本打架。
///       (会随回合自动变灰的只有结束回合按钮 NextRound,那个在 CommandPointController 手里。)
///     · 位置和尺寸:脚本完全不碰这两个物体的 RectTransform,排版自由,没有为它留的参数。
/// </summary>
[DisallowMultipleComponent]
public class BattleHudButtons : MonoBehaviour
{
    /// <summary>设置按钮的物体名</summary>
    public const string SettingsObjectName = "Settings";

    /// <summary>投降按钮的物体名</summary>
    public const string SurrenderObjectName = "Surrender";

    [Header("按钮(留空则运行时按名字找)")]
    [Tooltip("设置按钮(场景物体 Settings 上的 Button)。留空则按名字找;找不到 = 点了没反应")]
    [SerializeField] private Button settingsButton;
    [Tooltip("投降按钮(场景物体 Surrender 上的 Button)。留空则按名字找;找不到 = 点了没反应")]
    [SerializeField] private Button surrenderButton;
    [Tooltip("开:引用留空的部分按 Settings / Surrender 名字去找。\n" +
             "关:完全只认 Inspector 手连的引用 —— 填了一个空一个也不会去找,空的那个就是没接")]
    [SerializeField] private bool autoWireByName = true;

    /// <summary>点了设置。设置面板还没做(策划案里是暂停/音量那一套),先给个钩子</summary>
    public event Action SettingsClicked;

    /// <summary>点了投降。对局结束流程还没做,先给个钩子</summary>
    public event Action SurrenderRequested;

    public Button SettingsButton => settingsButton;
    public Button SurrenderButton => surrenderButton;

    private void Awake()
    {
        ResolveRefs();
    }

    private void OnEnable()
    {
        if (settingsButton != null) settingsButton.onClick.AddListener(OnSettingsClicked);
        if (surrenderButton != null) surrenderButton.onClick.AddListener(OnSurrenderClicked);
    }

    private void OnDisable()
    {
        if (settingsButton != null) settingsButton.onClick.RemoveListener(OnSettingsClicked);
        if (surrenderButton != null) surrenderButton.onClick.RemoveListener(OnSurrenderClicked);
    }

    // ================================================================ 点击

    private void OnSettingsClicked()
    {
        // TODO(设置面板): 面板有了之后在这里开关它,或者订阅 SettingsClicked
        Debug.Log("[HUD] 点了设置 —— 设置面板还没做。");
        SettingsClicked?.Invoke();
    }

    private void OnSurrenderClicked()
    {
        if (BattleSettlement.MatchOver)
        {
            Debug.Log("[HUD] 点了投降 —— 但这一局已经结束了。");
            return;
        }

        Debug.Log("[HUD] 点了投降 —— 判负,对局结束。");
        SurrenderRequested?.Invoke();

        // 投降 = 直接判我方输(策划案§6 对局结束)。二次确认弹窗还没做:要加的话在订阅方插一层,别改这里
        BattleSettlement.EndMatch(BattleSide.Enemy, "我方投降");
    }

    // ================================================================ 引用

    private void ResolveRefs()
    {
        if (!autoWireByName) return;

        if (settingsButton == null) settingsButton = FindButton(SettingsObjectName);
        if (surrenderButton == null) surrenderButton = FindButton(SurrenderObjectName);
    }

    /// <summary>
    /// 按名字找 Button。GameObject.Find 区分大小写(场景里改个大小写就找不到),
    /// 所以先按名字找,不中再在所有 Button 里做一次忽略大小写的比对。
    /// </summary>
    private static Button FindButton(string objectName)
    {
        var go = GameObject.Find(objectName);
        if (go != null)
        {
            var button = go.GetComponent<Button>();
            if (button != null) return button;
        }

        foreach (var candidate in FindObjectsOfType<Button>(true))
        {
            if (candidate != null && string.Equals(candidate.name, objectName, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        Debug.LogWarning($"[HUD] 场景里找不到按钮「{objectName}」。");
        return null;
    }
}
