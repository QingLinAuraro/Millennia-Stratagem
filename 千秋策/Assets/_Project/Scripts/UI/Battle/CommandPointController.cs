using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 指挥点(CP)池 + CP HUD + 结束回合。
///
/// 策划案§2.4 回合流程:回合开始 → 补给阶段(CP 上限 +1 并补满)→ 抽牌阶段(抽 1 张)→ 主阶段;
/// §4.5:第 1 回合 1 CP,第 3 回合 3 CP,第 7 回合后 7~12 CP。
/// 回合的推进交给 TurnController:它那边点结束回合/过一回合,才轮到我方(异步 ——
/// 双方各涨各的 CP,不跟着对方的回合一起涨)。这里只负责我方这一侧的补给 + 抽牌 + HUD。
/// 场景里做好的结束回合按钮(NextRound)由这里接上:点它 = 结束我方回合;
/// 不是我方回合时按钮自动灰掉(interactable = false)。
///
/// 敌方 CP(cost2)在 EnemyDeckController 里,同样是它自己回合才涨。
///
/// 【挂载 & 调整】
///   挂在:BattleCanvas 上 —— 场景里已经挂好,和 TurnController / EnemyDeckController / BattleHudButtons 在同一个物体上。
///         不挂也能跑:它是自举的,谁先访问 CommandPointController.Instance,就现建一个名为 CommandPointController 的物体,
///         挂到 CanvasUtil.FindRootCanvas() 找到的根画布下(Battle 场景里是 BattleCanvas);
///         自建的那一份用的是脚本里写的默认值(CP 1/1、开局抽 5 张),并且照样按名字去找 cost1 和 NextRound。
///   引用:cpPanel / cpText / endTurnButton 都是「留空也能跑」的,但兜底方式不同,坏掉的后果也不同:
///         · cpPanel 留空 → GameObject.Find(cpPanelObjectName)(默认 cost1,区分大小写,没有忽略大小写的二次查找);
///           找不到就不显示 CP(HUD 静默失效,不警告也不报错),找到的物体不是 UI(RectTransform 转换失败)时同样静默失效。
///         · cpText 留空 → 用 RuntimeText.Create 在 cpPanel 下现建一个名为 CpText 的 TextMeshProUGUI,并把 enableAutoSizing 打开;
///           它只是运行时的临时文字,关掉 Play 就没了,所以想在编辑器里细调排版的话得自己连一个 cpText。
///         · endTurnButton 留空且 autoWireEndTurnButton 打开 → 先 GameObject.Find(endTurnObjectName)(默认 NextRound),
///           不中再在所有 Button(含未激活的)里做一次忽略大小写的比对;只有找到的物体上没有 Button 时才会 AddComponent 补一个
///           (颜色/过渡不覆盖,归美术调)。找不到 = 只警告「场景里找不到结束回合按钮」,按钮点了没反应、也不会随回合变灰
///           (interactable 没人改),这时只能按 TurnController 的调试键过回合。
///         另外开局抽牌和每回合抽牌都是 FindObjectOfType<DeckController>() 现找牌堆的,场景里没有 DeckController 就只警告、不抽牌。
///   常调:
///     · startingCpMax(默认 1):开局 CP 上限,同时决定开局当前 CP(第 1 回合不走增长,直接按这个数补满)。
///       调大 = 开局就能连出大牌、节奏前压;调 0 = 第 1 回合什么都打不出。
///     · cpGrowthPerTurn(默认 1):**每回合的增长值**(增量)。第 N 回合自然上限 = 起始值 + (N-1) × 它,
///       封顶 PlayerState.CpGrowthCap(12)。默认 1、起始 1 时就是「第 N 回合 = N 点,第 12 回合及以后恒为 12」。
///       注意是按**回合数重算**而不是每次累加,所以同一回合被调两次也不会多涨。
///       调 0 = CP 永远停在起始值;调大只会更快摸到 12,不会突破 12(12 以上只能靠策略牌/卡牌效果加)。
///     · debugRaiseCpMaxOnKey / debugRaiseCpMaxKey(默认 = 键)/ debugRaiseCpMaxAmount(默认 1):调试用手动抬 CP 上限,验证 12 → 24 那条线。
///       验完、出包之前务必把 debugRaiseCpMaxOnKey 关掉,不然玩家按一下 = 就白拿 1 点上限(顶到硬顶 24)。
///     · drawOpeningHand / openingHandSize(默认 5)/ waitOneFrameBeforeOpeningDraw:开局手牌。
///       openingHandSize 调大 = 手牌扇形更挤(间距由 HandUI 的 stepPerCard 决定),调 0 = 开局空手;
///       waitOneFrameBeforeOpeningDraw 建议保持打开 —— 它等一帧,让 DeckController.Start() 先把牌堆初始化完再抽,关掉会抽到还没准备好的牌堆。
///     · cpFontSize(默认 40):运行时新建的 CpText 字号。占位面板小、字被挤掉就调小,看不清就调大;
///       脚本会把 enableAutoSizing 打开,最终字号由 TMP 自适应决定,想固定字号得自己连一个 cpText 并关掉自动缩放。
///     · cpTextFormat(默认 {0}/{1}):CP 文字格式,第 1 个占位符 = 当前 CP,第 2 个 = 上限。想显示成「CP 3/5」这类带前缀的改这里。
///     · cpNormalColor / cpEmptyColor:CP 大于 0 用 cpNormalColor(默认米黄),CP 等于 0 用 cpEmptyColor(默认半透明灰)。
///       想让「没费了」更醒目,把 cpEmptyColor 调暗或偏红。
///     · endTurnObjectName(默认 NextRound)/ autoWireEndTurnButton:结束回合按钮的兜底查找。
///       场景里给按钮改名、换物体之后,要么把 endTurnButton 手连上,要么改这个名字并保持 autoWireEndTurnButton 打开。
/// </summary>
[DisallowMultipleComponent]
public class CommandPointController : MonoBehaviour
{
    private static CommandPointController instance;

    /// <summary>场景里没有就自己建,用到才建</summary>
    public static CommandPointController Instance
    {
        get
        {
            if (instance != null) return instance;
            instance = FindObjectOfType<CommandPointController>();
            if (instance != null) return instance;

            var go = new GameObject("CommandPointController");
            var canvas = CanvasUtil.FindRootCanvas();
            if (canvas != null) go.transform.SetParent(canvas.transform, false);
            instance = go.AddComponent<CommandPointController>();
            return instance;
        }
    }

    /// <summary>
    /// 只取**已经存在**的 CP 控制器,不存在就返回 null —— **绝不新建**。
    ///
    /// 为什么需要它:Instance 找不到会现建,而建出来的这份 Awake 里会去 ResolveRefs +
    /// DrawOpeningHand,于是**在任何场景**里摸一下 Instance 都会触发"开局抽牌"。
    /// 卡面预制体(Card.prefab)在卡组构筑界面的卡池预览里会被 Instantiate 几十次,
    /// 每一次 OnEnable 摸一下 Instance,就够把开局抽卡在主菜单里跑一遍。
    /// 表现层"有就订阅、没有就算了"的场合一律用这个。
    /// </summary>
    public static CommandPointController TryGetInstance
    {
        get
        {
            if (instance != null) return instance;
            instance = FindObjectOfType<CommandPointController>();
            return instance;
        }
    }

    [Header("CP 池(策划案§4.5)")]
    [Tooltip("开局 CP 上限:第 1 回合 = 1")]
    [SerializeField] private int startingCpMax = 1;
    [Tooltip("每回合的增长值(增量):第 N 回合自然上限 = 起始值 + (N-1) × 它(默认 1 → 第 12 回合及以后恒为 12)。\n" +
             "按回合数重算,不是每次累加。自然增长封顶 PlayerState.CpGrowthCap(12),硬顶 CpMaxLimit(24)")]
    [SerializeField] private int cpGrowthPerTurn = 1;

    [Header("调试:费用上限")]
    [Tooltip("调试用:按 debugRaiseCpMaxKey 手动把 CP 上限 +1(代替还没做的「提升费用上限」卡牌),\n" +
             "用来验证 12 → 24 这条线")]
    [SerializeField] private bool debugRaiseCpMaxOnKey = true;
    [Tooltip("按哪个键手动抬 CP 上限,默认 = 键(KeyCode.Equals)")]
    [SerializeField] private KeyCode debugRaiseCpMaxKey = KeyCode.Equals;
    [Tooltip("按一次键抬几点上限。调大能更快摸到硬顶 24,配合 debugRaiseCpMaxOnKey 用")]
    [SerializeField] private int debugRaiseCpMaxAmount = 1;

    [Header("开局手牌")]
    [Tooltip("开局抽 5 张(策划案§2.4:游戏开始抽 5 张)。关掉就看 DeckController 的调试抽牌键")]
    [SerializeField] private bool drawOpeningHand = true;
    [Tooltip("开局抽几张手牌(策划案§2.4 = 5)。调大 = 手牌扇形更挤,调 0 = 开局空手")]
    [SerializeField] private int openingHandSize = 5;
    [Tooltip("等一帧再抽:保证 DeckController.Start() 已经把牌堆初始化好")]
    [SerializeField] private bool waitOneFrameBeforeOpeningDraw = true;

    [Header("CP HUD(留空则运行时按名字找 cost1,并在上面生成文字)")]
    [Tooltip("CP 面板(场景物体 cost1)。留空则按下面的名字找;找不到就不显示 CP,也不会报错")]
    [SerializeField] private RectTransform cpPanel;
    [Tooltip("cpPanel 留空时按这个名字找面板,默认 cost1(区分大小写,没有忽略大小写的二次查找)")]
    [SerializeField] private string cpPanelObjectName = "cost1";
    [Tooltip("CP 文字。留空则在面板下运行时新建一个 CpText,并自动打开 TMP 自适应字号")]
    [SerializeField] private TMP_Text cpText;
    [Tooltip("CP 文字格式,默认 {0}/{1}:第 1 个占位符是当前 CP,第 2 个是上限")]
    [SerializeField] private string cpTextFormat = "{0}/{1}";
    [Tooltip("运行时新建的 CP 文字字号,默认 40。面板小就调小;TMP 会自动缩放,这个只是起始值")]
    [SerializeField] private float cpFontSize = 40f;
    [Tooltip("CP 大于 0 时的文字颜色(米黄)")]
    [SerializeField] private Color cpNormalColor = new Color(0.98f, 0.94f, 0.8f);
    [Tooltip("CP 等于 0 时的文字颜色(半透明灰)。想强调「没费了」就调暗或调红")]
    [SerializeField] private Color cpEmptyColor = new Color(0.7f, 0.7f, 0.7f, 0.8f);

    [Header("结束回合")]
    [Tooltip("场景里那个结束回合按钮(NextRound)。填了就直接用,不填按名字找")]
    [SerializeField] private Button endTurnButton;
    [Tooltip("endTurnButton 留空时按名字找它(先精确匹配,再忽略大小写找一遍)。\n" +
             "找不到只警告;找到物体但它上面没有 Button 组件时,会补一个 ColorTint 按钮")]
    [SerializeField] private bool autoWireEndTurnButton = true;
    [Tooltip("endTurnButton 留空时按这个名字找按钮,默认 NextRound(先区分大小写找,再忽略大小写在所有 Button 里找)")]
    [SerializeField] private string endTurnObjectName = "NextRound";

    private PlayerState localPlayer;

    /// <summary>本地玩家的状态(CP 池在这里)</summary>
    public PlayerState LocalPlayer => localPlayer;
    public int Cp => localPlayer != null ? localPlayer.cp : 0;
    public int CpMax => localPlayer != null ? localPlayer.cpMax : 0;
    public int TurnNumber { get; private set; }

    /// <summary>CP 变了(扣费/回合补给):手牌据此重新算"这张还打不打得起"</summary>
    public event Action<int, int> CpChanged;

    /// <summary>订阅本回合流程的源头(TurnController),回合开始通知我方补给 + 抽牌</summary>
    private TurnController turnSource;
    private bool turnHooked;

    private void Awake()
    {
        instance = this;

        localPlayer = new PlayerState(isLocal: true);
        localPlayer.StartMatch(startingCpMax);
        TurnNumber = 1;

        ResolveRefs();
        RefreshHud();
    }

    private void OnEnable()
    {
        if (endTurnButton != null) endTurnButton.onClick.AddListener(OnEndTurnClicked);
        HookTurn();
    }

    private void OnDisable()
    {
        if (endTurnButton != null) endTurnButton.onClick.RemoveListener(OnEndTurnClicked);
        UnhookTurn();
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private void Start()
    {
        if (!drawOpeningHand) return;

        if (waitOneFrameBeforeOpeningDraw) StartCoroutine(DrawOpeningHandNextFrame());
        else DrawOpeningHand();
    }

    private void Update()
    {
        // 调试:手动抬 CP 上限,顶到 24 就涨不动了(代替还没做的费用上限卡)
        if (debugRaiseCpMaxOnKey && Input.GetKeyDown(debugRaiseCpMaxKey)) IncreaseCpMax(debugRaiseCpMaxAmount);
    }

    // ================================================================ 回合

    /// <summary>
    /// 订阅 TurnController(它是自举的,可能要先把它建出来 —— 点结束回合之前肯定已经建好了)。
    /// </summary>
    private void HookTurn()
    {
        if (turnHooked) return;

        turnSource = TurnController.Instance;
        if (turnSource == null)
        {
            Debug.LogWarning("[CP] 找不到 TurnController,我方不会跟着回合走。", this);
            return;
        }

        turnSource.TurnStarted += OnTurnStarted;
        turnHooked = true;
    }

    private void UnhookTurn()
    {
        if (!turnHooked) return;

        if (turnSource != null) turnSource.TurnStarted -= OnTurnStarted;
        turnSource = null;
        turnHooked = false;
    }

    private void OnEndTurnClicked() => TurnController.Instance.EndLocalTurn();

    /// <summary>
    /// 某一方的回合开始了。我方才动:敌方回合里点结束回合按钮也没用(按钮直接灰掉)。
    ///
    /// 费用是异步涨的 —— 这里只在我方回合进入时补给,不再跟着对方的回合一起涨。
    /// 第 1 回合:CP 直接是开局初始化的 1(不算增长),开局手牌也已经抽过,所以也不再抽。
    /// </summary>
    private void OnTurnStarted(bool isLocal, int turnNumber)
    {
        if (endTurnButton != null) endTurnButton.interactable = isLocal;
        if (!isLocal) return;
        if (localPlayer == null) return;

        TurnNumber = turnNumber;

        if (turnNumber <= 1)
        {
            localPlayer.StartMatch(startingCpMax);
            Debug.Log($"[CP] 我方第 1 回合:CP 初始化为 {Cp}/{CpMax}(第 1 回合不走增长)");
        }
        else
        {
            bool wasCapped = localPlayer.IsGrowthCapped;
            localPlayer.BeginTurn(turnNumber, cpGrowthPerTurn);

            if (!wasCapped && localPlayer.IsGrowthCapped)
                Debug.Log($"[CP] 我方第 {turnNumber} 回合:CP 上限已到自然增长封顶 {PlayerState.CpGrowthCap}");
            else
                Debug.Log($"[CP] 我方第 {turnNumber} 回合:补给阶段 CP 上限 {CpMax} 并补满");

            var deck = FindObjectOfType<DeckController>();
            if (deck != null) deck.DrawOne();      // 抽牌阶段:抽 1 张
            else Debug.LogWarning("[CP] 找不到 DeckController,这回合没抽牌。", this);
        }

        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);
    }

    // ================================================================ CP

    public bool CanAfford(int cost) => localPlayer != null && localPlayer.CanAfford(cost);

    /// <summary>
    /// 粮草被焚(策划案§2.3):我方 CP 上限 -N,当前值跟着夹一下,并把 HUD 与手牌刷新一遍。
    /// 别的地方直接改 PlayerState.cpMax 的话,HUD 与"这张卡还打不打得起"都不会跟着变。
    /// </summary>
    public void ReduceCpMax(int amount)
    {
        if (localPlayer == null || amount <= 0) return;

        localPlayer.ChangeBonus(-amount);
        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);
        Debug.Log($"[CP] 粮草被焚:我方 CP 上限 -{amount},现在是 {Cp}/{CpMax}");
    }

    /// <summary>CP 上限被外部改过之后刷 HUD(粮草被毁的 Debuff 走这条路)</summary>
    public void RefreshCpMaxDisplay()
    {
        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);
    }

    /// <summary>扣费。不够就返回 false 且一分不扣(调用方先判定后执行,不会出现扣了钱没出牌)</summary>
    public bool TrySpend(int cost)
    {
        if (localPlayer == null) return false;
        if (!localPlayer.TrySpend(cost)) return false;

        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);
        return true;
    }

    /// <summary>
    /// 抬 CP 上限 —— 「过费类」卡牌(数据表:费用上限+1 / +2)的入口,由
    /// CardEffectResolver.ResolvePlayCostEffects 在扣完卡费之后调用。
    ///
    /// 自然增长只到 PlayerState.CpGrowthCap(12) 就停,12 以上的部分只能靠这个方法来加;
    /// 而不管谁加,都越不过 PlayerState.CpMaxLimit(24)。返回实际涨了多少(顶到 24 之后是 0)。
    ///
    /// 只有登记了 CardEffectKind.RaiseCpMax 的卡会调到这里。别的卡、别的类型都不可能改上限。
    /// </summary>
    public int IncreaseCpMax(int amount)
    {
        if (localPlayer == null) return 0;

        int before = CpMax;
        localPlayer.ChangeBonus(amount);
        int gained = CpMax - before;

        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);

        if (gained > 0) Debug.Log($"[CP] CP 上限 +{gained}(卡牌/调试),现在是 {Cp}/{CpMax}");
        else if (localPlayer.IsAtMaxLimit) Debug.Log($"[CP] CP 上限已经到硬顶 {PlayerState.CpMaxLimit},加不上去了");

        return gained;
    }

    /// <summary>
    /// 回费 —— 「回费类」卡牌(数据表:回复 x 点费用)的入口,同样在扣完卡费之后调用。
    ///
    /// 只补当前费用,**上限不涨**:最多补到 CpMax,已花掉的补回来但不会超出上限。
    /// 返回实际补了多少(满费时是 0)。只有登记了 CardEffectKind.RefundCp 的卡会调到这里。
    /// </summary>
    public int RefundCp(int amount)
    {
        if (localPlayer == null) return 0;

        int gained = localPlayer.RefundCp(amount);
        if (gained <= 0) return 0;

        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);

        Debug.Log($"[CP] 回复 {gained} 点费用,现在是 {Cp}/{CpMax}");
        return gained;
    }

    // ================================================================ HUD

    private void ResolveRefs()
    {
        if (cpPanel == null)
        {
            var go = GameObject.Find(cpPanelObjectName);
            if (go != null) cpPanel = go.transform as RectTransform;
        }

        if (cpText == null && cpPanel != null)
        {
            // cost1 只是个占位图,没有文字子物体 —— 运行时补一个(策划案还没定 HUD 样式)
            cpText = RuntimeText.Create(cpPanel, "CpText", "", cpFontSize, TextAlignmentOptions.Center, cpNormalColor);
        }

        if (cpText != null) cpText.enableAutoSizing = true;

        if (endTurnButton == null && autoWireEndTurnButton) endTurnButton = EnsureEndTurnButton();
    }

    /// <summary>
    /// 结束回合按钮。场景里 NextRound 已经是做好的 Button 了,这里只把它找出来 ——
    /// 只有挂在没有 Button 的占位图上时才补一个(颜色/过渡都归美术调,不覆盖)。
    /// </summary>
    private Button EnsureEndTurnButton()
    {
        var go = FindObjectByName(endTurnObjectName);
        if (go == null)
        {
            Debug.LogWarning($"[CP] 场景里找不到结束回合按钮「{endTurnObjectName}」(还能按 TurnController 的调试键过回合)。", this);
            return null;
        }

        var button = go.GetComponent<Button>();
        if (button != null) return button;

        // 占位图才补一个可点的按钮
        button = go.AddComponent<Button>();
        button.targetGraphic = go.GetComponent<Graphic>();
        button.transition = Selectable.Transition.ColorTint;
        return button;
    }

    /// <summary>
    /// 按名字找物体。GameObject.Find 区分大小写(nextround / NextRound 差一个大小写就找不到),
    /// 所以先按名字找,不中再在所有 Button 里做一次忽略大小写的比对。
    /// </summary>
    private static GameObject FindObjectByName(string objectName)
    {
        var go = GameObject.Find(objectName);
        if (go != null) return go;

        foreach (var button in FindObjectsOfType<Button>(true))
        {
            if (button != null && string.Equals(button.name, objectName, StringComparison.OrdinalIgnoreCase))
                return button.gameObject;
        }

        return null;
    }

    private void RefreshHud()
    {
        if (cpText == null) return;
        cpText.text = string.Format(cpTextFormat, Cp, CpMax);
        cpText.color = Cp > 0 ? cpNormalColor : cpEmptyColor;
    }

    // ================================================================ 开局手牌

    private IEnumerator DrawOpeningHandNextFrame()
    {
        yield return null;      // 等所有 Start() 跑完(牌堆在 DeckController.Start() 里初始化)
        DrawOpeningHand();
    }

    private void DrawOpeningHand()
    {
        var deck = FindObjectOfType<DeckController>();
        if (deck == null)
        {
            Debug.LogWarning("[CP] 找不到 DeckController,开局手牌没抽。", this);
            return;
        }

        Debug.Log($"[CP] 开局:CP {Cp}/{CpMax},抽 {openingHandSize} 张手牌");
        deck.DrawCards(openingHandSize);
    }
}
