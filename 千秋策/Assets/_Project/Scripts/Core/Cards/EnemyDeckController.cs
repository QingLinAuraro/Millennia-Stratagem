using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// 敌方(对手)的牌库 + 手牌 + 指挥点 —— 本地那套 DeckController / CommandPointController 的镜像。
///
/// 为什么单独一份,不把 DeckController 改成"两边通用":
///   本地那套挂着调试抽牌键、接的是己方牌堆 UI,敌方要的是同一套规则、另一套账。
///   规则是共用的(都走 Deck / Hand / PlayerState 这三个模型),账和入口各自一份最省事。
///
/// 规则出处:
///   §2.4  开局双方各 5 张手牌;每回合 补给阶段(上限 +1 并补满)→ 抽牌阶段(抽 1 张)
///   §4.5  第 1 回合 CP 上限 1
///   §5.4  手牌上限 7,超出的牌直接退场(没有弃牌堆)
///   §5.4.1 牌堆空还继续摸 → 疲劳 2/4/6/8... 递增,扣摸牌方自己的大营
///
/// 敌方回合由谁驱动:订阅 TurnController.TurnStarted,只有轮到敌方时敌方才动 ——
/// 费用是异步涨的,敌方不会跟着我方的回合一起补给(双方各涨各的)。
/// 敌方 AI 也还没做,所以摸上来的牌就先停在敌方手牌里(数据上真实进账,表现上只看得见牌背)。
///
/// 表现层分两处:敌方牌堆(cards2)只更新剩余张数,敌方手牌见 EnemyHandUI(只显示牌背)。
///
/// 【挂载 & 调整】
///   挂在:BattleCanvas(整个 Canvas 物体上,场景 Assets/_Project/Scenes/Battle.unity 里 BattleCanvas 上就挂了这一个;
///         它和敌方牌堆 cards2 / CP 面板 cost2 是同一层级的兄弟物体,不是挂在 cards2 上)。
///         有自举兜底:Instance 先找场景里的,再没有就在第一个 Canvas 下建个空物体补上 ——
///         所以忘挂不会报错,但自举出来的那份 Inspector 引用全是空的,只剩按名字找 cards2 / cost2 的兜底。
///         回合流转由 TurnController 统一驱动:OnEnable 里订阅 TurnController.TurnStarted,只有 isLocal == false 时
///         才走 BeginTurn()(补给 + 摸牌)。HookTurn 找不到 TurnController 只打警告 ——
///         后果是敌方永远不过回合、不补给也不摸牌,而战斗还能照常打,容易误判成 AI 坏了。
///   引用:deckList 留空 = 借本地玩家 DeckController 的卡组(场景里现在就是空的,走借用);两边都空 →
///         BuildDeckList 打 LogError,敌方牌堆 0 张,每回合摸牌全走疲劳(疲劳现在只计数 + 打日志,
///         伤害还没接到敌方大营 HP)。
///         deckUI 场景里已连到 cards2 上的 DeckUI;留空会自己 GameObject.Find(「cards2」),
///         找到就补一个 DeckUI 组件并 Bind 它下面那个同名「CountNum」的计数文字;
///         找不到 cards2 只警告,敌方剩余张数不显示(数据照常扣,只是看不见)。
///         cpPanel 留空会自动找「cost2」;cpText 留空且 cpPanel 有效时,运行时在 cost2 下面 new 一个 CpText ——
///         场景里 cpText 是 None 属正常,别以为漏连了。cpPanel 和 cpText 都拿不到 = 敌方 CP 完全不显示,
///         但 CP 账照样在涨,打牌逻辑不受影响。
///   常调:· deckList:留空借我方卡组(测试对局最省事);要给敌方单独配卡组时才拖。
///         · followTurnAdvance:默认开。关掉 = OnEnable 不订阅 TurnController,敌方彻底不跟着过回合
///           (要自己调 BeginTurn()),只在手动驱动测试时才关。
///         · startingCpMax:开局 CP 上限,默认 1(策划案§4.5)。调大 = 敌方第 1 回合就能连出大牌,
///           用来快进到后期局面;调到 0 = 敌方第 1 回合一分钱都没有,什么也打不出。
///         · cpGrowthPerTurn:每回合的增长值(增量),默认 1(§2.4 补给阶段)。第 N 回合自然上限 =
///           起始值 + (N-1) × 它,封顶 PlayerState.CpGrowthCap(12)。调大 = 敌方费用更早到 12;
///           调 0 = 永远停在初始上限(只剩打策略牌那条 +1 的路)。它是按回合数重算的,不是每次累加。
///         · openingHandSize:开局手牌张数,默认 5(§2.4)。调大 = 敌方开局选择更多,
///           但 Hand.MaxSize 是 7,超过的部分会被 TryAdd 判满直接退场,白扔;调到 0 或负数 = Start 直接 return,
///           开局一张不摸,只剩回合内抽牌。
///         · waitOneFrameBeforeOpeningDraw:默认开。关掉 = 在 Start() 里立刻抽(不再等一帧),
///           这时自己的牌堆已经 Init 过、不会摸空,但其他组件还没跑 Start,靠 HandChanged 增删牌背的表现层
///           可能错过这一批通知(EnemyHandUI 现在会在自己的 Start 里再 Sync 一次,所以看不出问题)。
///         · cpFontSize / cpTextFormat / cpNormalColor / cpEmptyColor:CP 文字的字号、格式和颜色。
///           cpFontSize 只在 CpText 由本脚本生成时生效(拖了自己的 cpText 就没用了,自动缩放仍会被打开);
///           cpTextFormat 默认「{0}/{1}」= 当前/上限,想只显示当前值就改成「{0}」;
///           两个颜色是「还有费 / 没费」时的文字色,每次刷新 CP 都会覆盖文字颜色,自己拖的文字也会被改。
///         · debugPlayOnKey / debugPlayKey:默认开 + 键 M,让敌方随便打一张策略牌试提示条和牌背消失;
///           敌方 AI 做出来之后关掉,免得误按。
/// </summary>
public class EnemyDeckController : MonoBehaviour
{
    /// <summary>敌方牌堆占位图,cards2 的计数文字 CountNum 就在它下面</summary>
    public const string DeckPileObjectName = "cards2";

    /// <summary>敌方牌堆的剩余张数文字。场景里有两个同名的 CountNum,不能直接按名字找</summary>
    public const string CountTextObjectName = "CountNum";

    /// <summary>敌方 CP 面板占位图</summary>
    public const string CpPanelObjectName = "cost2";

    private static EnemyDeckController instance;

    /// <summary>场景里没挂就自己补一个(和 BattlefieldManager / CommandPointController 的自举一致)</summary>
    public static EnemyDeckController Instance
    {
        get
        {
            if (instance != null) return instance;

            instance = FindObjectOfType<EnemyDeckController>();
            if (instance != null) return instance;

            var go = new GameObject("EnemyDeckController");
            var canvas = CanvasUtil.FindRootCanvas();
            if (canvas != null) go.transform.SetParent(canvas.transform, false);
            instance = go.AddComponent<EnemyDeckController>();
            return instance;
        }
    }

    [Header("构筑(留空 = 借用本地玩家那套卡组,方便测试对局)")]
    [Tooltip("敌方卡组。留空 = 借 DeckController 的卡组(现阶段测试对局用)。\n" +
             "两边都空 → BuildDeckList 报错,敌方牌堆 0 张,每回合摸牌全是疲劳")]
    [SerializeField] private List<CardData> deckList = new List<CardData>();

    [Header("开局(策划案§2.4 / §4.5)")]
    [Tooltip("开局 CP 上限,第 1 回合双方都是 1")]
    [SerializeField] private int startingCpMax = 1;
    [Tooltip("每回合的增长值(增量):第 N 回合自然上限 = 起始值 + (N-1) × 它(默认 1 → 第 12 回合及以后恒为 12)。\n" +
             "按回合数重算,不是每次累加。自然增长封顶 PlayerState.CpGrowthCap(12),硬顶 CpMaxLimit(24)")]
    [SerializeField] private int cpGrowthPerTurn = 1;
    [Tooltip("开局手牌张数,策划案§2.4 = 双方各 5 张")]
    [SerializeField] private int openingHandSize = 5;
    [Tooltip("开局手牌等一帧再抽:让其他组件的 Start() 先跑完(自己的牌堆已经在 Start 里 Init 过,不会摸空)。\n" +
             "关掉 = Start 里立刻抽,靠 HandChanged 增删牌背的表现层可能漏掉这一批通知")]
    [SerializeField] private bool waitOneFrameBeforeOpeningDraw = true;

    [Header("回合推进")]
    [Tooltip("轮到敌方回合时补给 + 摸牌(由 TurnController 通知)。关掉 = 敌方永远不补给也不摸牌")]
    [SerializeField] private bool followTurnAdvance = true;

    [Header("HUD(留空则运行时按名字找 cards2 / cost2)")]
    [Tooltip("敌方牌堆 HUD(cards2 上的 DeckUI)。留空会自动找场景里的「cards2」,\n" +
             "找到就补组件并 Bind 它下面那个计数文字;找不到只警告,敌方剩余张数不显示")]
    [SerializeField] private DeckUI deckUI;
    [Tooltip("敌方 CP 面板占位图。留空会自动找「cost2」;拿不到就没地方生成 CP 文字(敌方 CP 不显示,账照常涨)")]
    [SerializeField] private RectTransform cpPanel;
    [Tooltip("敌方 CP 文字。留空且 cpPanel 有效时,运行时在 cost2 下面生成一个 CpText(场景里是 None 属正常);\n" +
             "拖了自己的就用拖的,cpFontSize 随之失效")]
    [SerializeField] private TMP_Text cpText;
    [Tooltip("CP 文字字号(默认 40)。只在 CpText 由本脚本生成时生效;\n" +
             "生成的文字还会开自动缩放,所以它只是基准字号")]
    [SerializeField] private float cpFontSize = 40f;
    [Tooltip("CP 文字格式,默认「{0}/{1}」= 当前/上限;只想显示当前值就改成「{0}」")]
    [SerializeField] private string cpTextFormat = "{0}/{1}";
    [Tooltip("敌方还有 CP 时 CP 文字的颜色。每次刷新 CP 都会覆盖文字颜色,自己拖的文字也会被改")]
    [SerializeField] private Color cpNormalColor = new Color(0.98f, 0.86f, 0.55f, 1f);
    [Tooltip("敌方 CP 为 0 时 CP 文字的颜色(默认灰)。想更醒目地提示「对方没费了」就把它调亮/调暖")]
    [SerializeField] private Color cpEmptyColor = new Color(0.72f, 0.72f, 0.72f, 0.85f);

    [Header("调试")]
    [Tooltip("调试用:按 debugPlayKey 让敌方随便打一张策略牌(不走 AI 评分)。\n" +
             "敌方 AI(EnemyAI)已经接管了回合,平时关掉 —— 开着会和 AI 抢着出牌、也容易误按")]
    [SerializeField] private bool debugPlayOnKey = false;
    [Tooltip("调试出牌键,配合 debugPlayOnKey 用,默认 M。\n" +
             "和结束回合(T)/ 抬费用(=)/ 袭扰标记(N)/ 自检(F9) 的调试键撞了可以换一个")]
    [SerializeField] private KeyCode debugPlayKey = KeyCode.M;

    private readonly Deck deck = new();
    private readonly Hand hand = new();
    private readonly PlayerState enemy = new PlayerState(isLocal: false);

    /// <summary>疲劳触发次数。每名玩家各自独立计数,永不重置(策划案§5.4.1)</summary>
    private int fatigueCount;

    private bool turnHooked;

    /// <summary>订阅的是哪一个 TurnController(退订要用同一个实例)</summary>
    private TurnController turnSource;

    /// <summary>敌方手牌账变了(摸到 / 打出):敌方手牌 UI 据此增删牌背</summary>
    public event Action HandChanged;

    /// <summary>敌方 CP 变了</summary>
    public event Action<int, int> CpChanged;

    /// <summary>敌方玩家状态(CP 池、isLocal = false)</summary>
    public PlayerState Player => enemy;
    public int DeckCount => deck.Count;
    public int HandCount => hand.Count;
    public int FatigueCount => fatigueCount;

    /// <summary>敌方手牌账(只读)。注意:这是敌方情报,只有 AI / 结算能用,别往表现层递</summary>
    public IReadOnlyList<CardData> HandCards => hand.Cards;

    public int Cp => enemy.cp;
    public int CpMax => enemy.cpMax;

    // ================================================================ 生命周期

    private void Awake()
    {
        instance = this;

        enemy.StartMatch(startingCpMax);
        ResolveRefs();
        RefreshHud();
    }

    private void OnEnable()
    {
        if (followTurnAdvance) HookTurn();
    }

    private void OnDisable()
    {
        UnhookTurn();
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private void Start()
    {
        deck.Init(BuildDeckList());
        RefreshDeckUI();

        if (openingHandSize <= 0) return;
        if (waitOneFrameBeforeOpeningDraw) StartCoroutine(DrawOpeningHandNextFrame());
        else DrawOpeningHand();
    }

    private void Update()
    {
        // 调试:让敌方随便打一张策略牌(不走 AI 评分)。默认关着 —— 敌方回合现在由 EnemyAI 接管
        if (debugPlayOnKey && Input.GetKeyDown(debugPlayKey)) DebugPlayRandomTactic();
    }

    /// <summary>
    /// 订阅回合推进(TurnController)。它也是自举的,可能要先把它建出来 ——
    /// 在它把回合交到敌方之前,顺序上都是安全的。
    /// </summary>
    private void HookTurn()
    {
        if (turnHooked) return;

        turnSource = TurnController.Instance;
        if (turnSource == null)
        {
            Debug.LogWarning("[EnemyDeckController] 找不到 TurnController,敌方不会跟着过回合。", this);
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

    /// <summary>回合通知:只有轮到敌方时敌方才动(费用异步涨,不跟着我方回合涨)</summary>
    private void OnTurnStarted(bool isLocal, int turnNumber)
    {
        if (isLocal) return;
        BeginTurn(turnNumber);
    }

    // ================================================================ 回合

    /// <summary>
    /// 敌方回合开始:补给阶段 + 抽牌阶段(抽 1 张)。策划案§2.4。
    ///
    /// 第 1 回合:CP 直接是开局初始化的 1(不算增长),开局手牌已经抽过 5 张,所以也不再抽。
    /// 之后每次进入敌方回合才 +1(自然增长封顶 PlayerState.CpGrowthCap = 12)。
    /// </summary>
    public void BeginTurn(int turnNumber = 1)
    {
        if (turnNumber <= 1)
        {
            enemy.StartMatch(startingCpMax);
            Debug.Log($"[EnemyDeck] 敌方第 1 回合:CP 初始化为 {Cp}/{CpMax}(第 1 回合不走增长)");
        }
        else
        {
            enemy.BeginTurn(turnNumber, cpGrowthPerTurn);
            Debug.Log($"[EnemyDeck] 敌方第 {turnNumber} 回合:补给阶段 CP {Cp}/{CpMax}");
        }

        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);

        // 敌方 AI 还没做,这回合只能先把牌摸上来(开局 5 张已经在 Start 里抽过)
        if (turnNumber > 1) DrawCards(1);
    }

    /// <summary>扣费。不够就返回 false 且一分不扣(给以后敌方 AI 用)</summary>
    public bool TrySpend(int cost)
    {
        if (!enemy.TrySpend(cost)) return false;

        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);
        return true;
    }

    public bool CanAfford(int cost) => enemy.CanAfford(cost);

    /// <summary>粮草被焚等效果:敌方 CP 上限下降(策划案§2.3)</summary>
    public void ReduceCpMax(int amount)
    {
        enemy.ChangeBonus(-amount);
        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);
        Debug.Log($"[EnemyDeck] 敌方 CP 上限 -{amount},现在是 {Cp}/{CpMax}");
    }

    /// <summary>
    /// 敌方 CP 上限 +N —— 「过费类」卡牌的入口,由 CardEffectResolver.ResolvePlayCostEffects
    /// 在扣完卡费之后调用。自然增长封顶在 PlayerState.CpGrowthCap(12),
    /// 硬顶 PlayerState.CpMaxLimit(24)。返回实际涨了多少(顶到 24 之后是 0)。
    /// </summary>
    public int IncreaseCpMax(int amount)
    {
        int before = CpMax;
        enemy.ChangeBonus(amount);
        int gained = CpMax - before;

        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);

        if (gained > 0) Debug.Log($"[EnemyDeck] 敌方 CP 上限 +{gained},现在是 {Cp}/{CpMax}");
        return gained;
    }

    /// <summary>
    /// 敌方回费 —— 「回费类」卡牌的入口,同样在扣完卡费之后调用。
    /// 只补当前费用,上限不涨(最多补到 CpMax)。返回实际补了多少(满费时是 0)。
    /// </summary>
    public int RefundCp(int amount)
    {
        int gained = enemy.RefundCp(amount);
        if (gained <= 0) return 0;

        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);

        Debug.Log($"[EnemyDeck] 敌方回复 {gained} 点费用,现在是 {Cp}/{CpMax}");
        return gained;
    }

    // ================================================================ 摸牌 / 出牌

    /// <summary>敌方摸 count 张。每一张单独结算:牌堆空 → 各自触发一次疲劳;手牌满 → 那一张直接退场</summary>
    public void DrawCards(int count)
    {
        for (int i = 0; i < count; i++) DrawSingle();
    }

    private void DrawSingle()
    {
        var card = deck.Draw();

        // 牌堆先刷新:不管摸到的牌进不进手牌,牌堆都实实在在少了一张
        RefreshDeckUI();

        if (card == null)
        {
            TriggerFatigue();
            return;
        }

        if (!hand.TryAdd(card))
        {
            // 策划案§5.4:手牌上限 7,超出的牌直接退场(没有弃牌堆)
            Debug.Log($"[EnemyDeck] 敌方手牌已满({Hand.MaxSize}张),摸到的「{card.cardName}」直接退场");
            return;
        }

        // 同一个事件:本地手牌区(HandUI)会自己过滤掉 isLocal == false 的那一份
        EventManager.Trigger(new CardDrawnEventArgs { Card = card, Player = enemy });
        HandChanged?.Invoke();
    }

    /// <summary>
    /// 敌方打出一张牌:从手牌账里扣掉(牌背的收尾是 EnemyHandUI 的事),
    /// 并广播 CardPlayed —— 提示条(BattleMessageUI)靠它把对方的策略牌念出来。
    /// </summary>
    public bool PlayCard(CardData card)
    {
        if (card == null) return false;

        if (!hand.Remove(card))
        {
            Debug.LogWarning($"[EnemyDeck] 打出的「{card.cardName}」不在敌方手牌账里。", this);
            return false;
        }

        HandChanged?.Invoke();
        EventManager.Trigger(new CardPlayedEventArgs { Card = card, Player = enemy });
        return true;
    }

    /// <summary>按手牌下标打出一张(以后敌方 AI 挑牌出用这个)</summary>
    public bool PlayCardAt(int index)
    {
        var cards = hand.Cards;
        if (index < 0 || index >= cards.Count) return false;
        return PlayCard(cards[index]);
    }

    /// <summary>
    /// 调试:敌方打出手牌里的任意一张策略牌,走的是正常出牌流程(扣手牌账 + 广播 CardPlayed)。
    /// 用来试"对方释放策略牌"的提示条和牌背收走;敌方 AI 做好之后可以删掉。
    /// </summary>
    public bool DebugPlayRandomTactic()
    {
        var cards = hand.Cards;
        var tactics = new List<CardData>();
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i] != null && cards[i].cardType == CardType.Tactic) tactics.Add(cards[i]);
        }

        if (tactics.Count == 0)
        {
            Debug.LogWarning("[EnemyDeck] 敌方手牌里没有策略牌,试不了提示条。", this);
            return false;
        }

        var pick = tactics[UnityEngine.Random.Range(0, tactics.Count)];
        Debug.Log($"[EnemyDeck] 调试:敌方打出「{pick.cardName}」");
        return PlayCard(pick);
    }

    /// <summary>
    /// 牌堆已空还继续摸 → 疲劳:2/4/6/8/10...递增,扣的是摸牌方自己的大营。
    /// 策划案§5.4.1 —— 空牌堆摸「摸牌3」= 2+4+6 = 12 点自伤,双方各自计数、永不重置。
    /// 伤害落到大营 HP 上在 BattleSettlement.TriggerFatigue 里(它还要负责"大营被打空就判负")。
    /// </summary>
    private void TriggerFatigue()
    {
        fatigueCount++;
        int damage = BattleSettlement.FatigueDamageFor(fatigueCount);

        Debug.Log($"[EnemyDeck] 敌方牌堆已空,第 {fatigueCount} 次疲劳,敌方大营受到 {damage} 点伤害");

        // 敌方疲劳也让玩家看得见:飘在敌方牌堆上方
        var rect = deckUI != null ? deckUI.transform as RectTransform : null;
        var canvas = CanvasUtil.FindRootCanvas();
        Vector2 pos = new Vector2(Screen.width * 0.5f, Screen.height * 0.72f);
        if (rect != null && canvas != null)
        {
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            pos = RectTransformUtility.WorldToScreenPoint(cam, rect.position);
        }
        FloatingTipUI.Show(pos, $"敌方牌库已空！大营 -{damage}", warning: true);

        BattleSettlement.TriggerFatigue(BattleSide.Enemy, fatigueCount);
    }

    /// <summary>牌堆张数变了刷新 HUD(「召唤(牌堆)」这类往敌方牌堆里加牌的效果也会调它)</summary>
    public void RefreshDeckCount() => RefreshDeckUI();

    /// <summary>CP 账被外部改过(粮草被焚)之后把 HUD 刷一遍</summary>
    public void NotifyCpChanged()
    {
        RefreshHud();
        CpChanged?.Invoke(Cp, CpMax);
    }

    /// <summary>往敌方牌堆里塞一张牌(「召唤(牌堆)」效果;§4.3 唯一能让牌堆变多的效果)</summary>
    public void AddCardToDeck(CardData card, int count = 1)
    {
        if (card == null || count <= 0) return;

        for (int i = 0; i < count; i++) deck.Add(card);
        RefreshDeckUI();
        Debug.Log($"[EnemyDeck] 敌方牌堆加入「{card.cardName}」×{count},现有 {DeckCount} 张");
    }

    /// <summary>
    /// 随机弃掉敌方一张手牌(策划案§5.4:被弃的牌**直接退场**,不回牌堆)。
    /// 「反间」这类弃牌效果打向敌方时走这里。
    /// </summary>
    public bool DiscardRandom()
    {
        var cards = hand.Cards;
        if (cards.Count == 0) return false;

        return Discard(cards[UnityEngine.Random.Range(0, cards.Count)]);
    }

    /// <summary>弃掉指定的那张敌方手牌(手牌账 + 牌背一起收)</summary>
    public bool Discard(CardData card)
    {
        if (card == null) return false;
        if (!hand.Remove(card))
        {
            Debug.LogWarning($"[EnemyDeck] 要弃置的「{card.cardName}」不在敌方手牌账里。", this);
            return false;
        }

        Debug.Log($"[EnemyDeck] 敌方手牌「{card.cardName}」被弃置,直接退场（§5.4：本局不再回到牌堆）");
        HandChanged?.Invoke();      // 牌背收走;弃牌不是"打出",不广播 CardPlayed(免得提示条把弃牌当成对方出牌念一遍)
        return true;
    }

    // ================================================================ 开局手牌

    private IEnumerator DrawOpeningHandNextFrame()
    {
        yield return null;      // 等所有 Start() 跑完(牌堆在自己的 Start 里初始化)
        DrawOpeningHand();
    }

    private void DrawOpeningHand()
    {
        Debug.Log($"[EnemyDeck] 开局:敌方 CP {Cp}/{CpMax},摸 {openingHandSize} 张手牌");
        DrawCards(openingHandSize);
    }

    /// <summary>
    /// 用哪套构筑:优先自己配的,没配就借本地玩家的卡组(现阶段测试对局方便)。
    /// 两边都用同一份 CardData 资产没问题 —— 卡面数据是只读的,牌堆账各自独立。
    /// </summary>
    private List<CardData> BuildDeckList()
    {
        // 最优先:主菜单带进来的敌方卡组(现在是 AI 随机构筑的一套)。
        // BattleContext 是跨场景静态中转 —— 场景在切到 Battle 时被重建,但静态字段还在。
        var fromMenu = BattleContext.OpponentDeck;
        if (fromMenu != null && fromMenu.cards != null && fromMenu.cards.Count > 0)
        {
            Debug.Log($"[EnemyDeck] 用主菜单带来的敌方卡组「{fromMenu.deckName}」{fromMenu.cards.Count} 张" +
                      (BattleContext.OpponentIsRandom ? "(随机构筑)" : ""));
            return new List<CardData>(fromMenu.cards);
        }

        if (deckList != null && deckList.Count > 0) return new List<CardData>(deckList);

        var local = FindObjectOfType<DeckController>();
        if (local != null && local.DeckList != null && local.DeckList.Count > 0)
        {
            Debug.Log("[EnemyDeck] 敌方没单独配构筑,先用本地玩家那套牌组顶上。");
            return new List<CardData>(local.DeckList);
        }

        Debug.LogError("[EnemyDeck] 敌方牌堆是空的:既没配 deckList,也找不到 DeckController 的构筑。", this);
        return new List<CardData>();
    }

    // ================================================================ HUD

    private void ResolveRefs()
    {
        if (deckUI == null)
        {
            var go = GameObject.Find(DeckPileObjectName);
            if (go != null)
            {
                deckUI = go.GetComponent<DeckUI>();
                if (deckUI == null) deckUI = go.AddComponent<DeckUI>();

                // 场景里的两张 CountNum 同名,只能顺着 cards2 往下找自己的那个
                deckUI.Bind(FindChildText(go.transform, CountTextObjectName));
            }
            else
            {
                Debug.LogWarning($"[EnemyDeck] 场景里找不到敌方牌堆「{DeckPileObjectName}」,敌方剩余张数不会显示。", this);
            }
        }

        if (cpPanel == null)
        {
            var go = GameObject.Find(CpPanelObjectName);
            if (go != null) cpPanel = go.transform as RectTransform;
        }

        if (cpText == null && cpPanel != null)
        {
            // cost2 和 cost1 一样只是占位图,没有文字子物体 —— 运行时补一个
            cpText = RuntimeText.Create(cpPanel, "CpText", "", cpFontSize, TextAlignmentOptions.Center, cpNormalColor);
        }

        if (cpText != null) cpText.enableAutoSizing = true;
    }

    /// <summary>在 root 下面按名字找计数文字,找不到就退而求其次用第一个</summary>
    private static TMP_Text FindChildText(Transform root, string childName)
    {
        if (root == null) return null;

        var texts = root.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] != null && texts[i].gameObject.name == childName) return texts[i];
        }

        return texts.Length > 0 ? texts[0] : null;
    }

    private void RefreshHud()
    {
        if (cpText == null) return;

        cpText.text = string.Format(cpTextFormat, Cp, CpMax);
        cpText.color = Cp > 0 ? cpNormalColor : cpEmptyColor;
    }

    private void RefreshDeckUI()
    {
        if (deckUI != null) deckUI.Refresh(deck.Count);
    }
}
