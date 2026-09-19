using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// 轮流轮次:一方结束回合,才轮到另一方。
///
/// 策划案§2.4 的回合流程(补给 → 抽牌 → 主阶段)从此按"谁在走"来推进:
///   我方第 1 回合 → (点结束回合) → 敌方第 1 回合 → (敌方走完) → 我方第 2 回合 → ……
/// 双方各走一次算一轮(RoundNumber)。
///
/// 关键在于**部署费用是异步涨的**:每一方只在自己的回合开始时补给(+1 并补满),
/// 不会再出现"对方结束回合、我方跟着涨"的同步推进。
///
/// 费用的增长时机(**只剩回合这一条路**):
///   1. 每个玩家的第 1 回合:CP 上限**直接初始化成 1**(不算增长);
///   2. 之后每次进入自己的回合:上限 = 起始值 + (回合数-1) × cpGrowthPerTurn(封顶 PlayerState.CpGrowthCap = 12)。
///
/// 各方自己接 TurnStarted 做事(补给 + 抽牌):CommandPointController = 我方,EnemyDeckController = 敌方。
///
/// 【挂载 & 调整】
///   挂在:Battle.unity 里挂在 BattleCanvas 上(Assets/_Project/Scenes/Battle.unity;同一个物体上还有
///         CommandPointController、EnemyDeckController、BattleHudButtons)。场景里没挂也能跑:Instance 会先全场景找,
///         找不到就新建一个空物体挂到 Canvas 下 —— 但那样得有人先用到 Instance 才会建,Start 里的 autoStart 照常生效。
///   引用:没有要手连的引用,全是自己填自己的:协作对象都走单例 —— CommandPointController.Instance(我方费用)、
///   常调:
///     · firstSide:谁先手;改成 Enemy 就是敌方开局先走,想直接观察敌方回合从它下手最快。
///     · autoStart:开局要不要自动进第一个回合;关掉后必须有人自己调 StartFirstTurn(),否则回合永远不开始、
///       按调试键只会打印一句警告,出牌也会因为 HasStarted 为假而完全不拦。
///     · enemyTurnSeconds:敌方回合等几秒自动结束 —— **这是没有 AI 时的兜底**。
///       默认 0(不自动结束):EnemyAI 接管回合时会自己调 EndTurn(),开着定时器会和 AI 抢着过回合。
///       想在没有 AI 的场景里调试,把它填 2 左右即可。
///     · debugEndTurnKey / debugEndTurnOnKey:调试结束回合的键和开关;发布前把 on 关掉即可,不用清空按键。
///       默认 T;别再用 N —— BattlefieldManager 的袭扰调试键(场景里设成了 N)会跟着一起触发。
/// </summary>

// 禁止在同一个 GameObject 上挂载多个 TurnController 组件
[DisallowMultipleComponent]


public class TurnController : MonoBehaviour
{
    /// <summary>轮到谁</summary>
    public enum Side { Local, Enemy, Random }

    /// <summary>场景里没有就自己建,用到才建</summary>
    private static TurnController instance;

    // 通过 TurnController.Instance 全局访问
    public static TurnController Instance
    {
        get
        {
            // 如果静态引用存在，返回
            // 静态引用不存在，搜索挂载TurnController的物体，找到则赋值返回
            if (instance != null) return instance;
            instance = FindObjectOfType<TurnController>();
            if (instance != null) return instance;

            // 不存在上述物体时，创建新物体并挂载TurnController,如果场景中存在根 Canvas，将新物体设为它的子级
            var go = new GameObject("TurnController");
            var canvas = CanvasUtil.FindRootCanvas();
            if (canvas != null) go.transform.SetParent(canvas.transform, false);
            instance = go.AddComponent<TurnController>();
            return instance;
        }
    }

    [Header("先手")]
    [Tooltip("谁先手:Local = 我方开局先走,Enemy = 敌方开局先走,Random = 双方随机先手。只影响第一个回合,之后双方轮流")]
    [SerializeField] private Side firstSide = Side.Random;

    // 自动开局
    [Tooltip("开局自动进入第一个回合。关掉的话要自己调 StartFirstTurn()")]
    [SerializeField] private bool autoStart = true;

    // 敌方回合自动读秒
    [Header("敌方回合")]
    [Tooltip("没有 AI 时的兜底:进入敌方回合后等几秒自动结束。0 = 不自动结束(由 EnemyAI 自己结束回合)")]
    [SerializeField] private float enemyTurnSeconds = 30f;


    [Header("调试")]
    [Tooltip("结束当前回合(谁的回合都行),用来把双方轮流跑通")]
    [SerializeField] private bool debugEndTurnOnKey = true;
    [Tooltip("调试结束回合的按键(谁的回合都行)。注意 BattlefieldManager 的袭扰调试键在场景里也是这个键(都是 N),按一下会同时切换袭扰标记并结束回合")]
    [SerializeField] private KeyCode debugEndTurnKey = KeyCode.T;

    private Side currentSide = Side.Local;
    private int roundNumber;        // 轮次:双方各走一次算一轮
    private int localTurns;         // 我方走到第几个回合
    private int enemyTurns;         // 敌方走到第几个回合
    private bool started;

    // 存放敌方自动结束的协程引用，便于取消停止
    private Coroutine autoEndRoutine;

    /// <summary>
    /// 某一方的回合开始。参数:是不是我方、这是这一方自己的第几个回合(第 1 回合只初始化,不涨费用)。
    /// </summary>
    public event Action<bool, int> TurnStarted;

    public Side CurrentSide => currentSide;
    public bool IsLocalTurn => currentSide == Side.Local;
    public bool HasStarted => started;
    public int RoundNumber => roundNumber;
    public int LocalTurnNumber => localTurns;
    public int EnemyTurnNumber => enemyTurns;

    /// <summary>当前这一方自己的回合数</summary>
    public int CurrentSideTurnNumber => currentSide == Side.Local ? localTurns : enemyTurns;

    // 当场景中存在该组件时，Awake 被调用，将自身设为单例
    private void Awake()
    {
        instance = this;
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private void Start()
    {
        if (autoStart) StartCoroutine(StartFirstTurnNextFrame());
    }

    private void Update()
    {
        if (debugEndTurnOnKey && Input.GetKeyDown(debugEndTurnKey)) EndTurn();
    }

    // ================================================================ 回合流转

    private IEnumerator StartFirstTurnNextFrame()
    {
        // 等一帧:各方(DeckController / CommandPointController / EnemyDeckController)先把自己 Start 走完,
        // 免得第一个回合的补给和开局抽牌撞在一起
        yield return null;
        StartFirstTurn();
    }

    /// <summary>开局:进入先手方的第 1 回合</summary>
    public void StartFirstTurn()
    {
        if (started) return;
        started = true;
        roundNumber = 1;

        Side actualFirst = firstSide;
        if(firstSide == Side.Random)
        {
            actualFirst = UnityEngine.Random.Range(0,2) == 0 ? Side.Local : Side.Enemy;
        }
        StartSideTurn(actualFirst);
    }

    /// <summary>
    /// 结束**当前这一方**的回合,把回合交给另一方。调试键和敌方自动结束走这里。
    /// </summary>
    public void EndTurn()
    {
        if (!started)
        {
            Debug.LogWarning("[Turn] 回合还没开始,先 StartFirstTurn()。", this);
            return;
        }

        var next = currentSide == Side.Local ? Side.Enemy : Side.Local;

        // §2.4 第 4 步:清理当前这一方的「本回合」标记(刚部署标记、压制倒计时、临时 buff 的到期检查)
        CleanupSide(currentSide == Side.Local ? BattleSide.Player : BattleSide.Enemy);

        if (next == Side.Local) roundNumber++;      // 又回到我方 = 新的一轮

        StartSideTurn(next);
    }

    /// <summary>结束**我方**回合(结束回合按钮用)。不是我方回合就什么都不做</summary>
    public void EndLocalTurn()
    {
        if (!started) return;

        if (currentSide != Side.Local)
        {
            Debug.Log("[Turn] 现在是对方的回合,结束不了。");
            return;
        }

        EndTurn();
    }

    private void StartSideTurn(Side side)
    {
        if (BattleSettlement.MatchOver)
        {
            Debug.Log("[Turn] 对局已经结束,不再推进回合。");
            return;
        }
        if (side == Side.Random)
        {
            Debug.LogError("[Turn] StartSideTurn 不能接收 Random，请先在 StartFirstTurn 解析先手胜方！");
            return;
        }
        currentSide = side;
        int sideTurn = side == Side.Local ? ++localTurns : ++enemyTurns;

        Debug.Log($"[Turn] 第 {roundNumber} 轮 · {(side == Side.Local ? "我方" : "敌方")}第 {sideTurn} 回合开始");

        // 结算层的回合维护(§2.4 / §7.4 / §7.3):重置 AP、过期临时 buff、袭扰骑兵 ATK+1 与 -2 HP、无敌走一格。
        // 放在 TurnStarted 之前 —— 各方(CommandPointController / EnemyDeckController)一收到通知就会补给并摸牌,
        // 摸牌可能触发的疲劳要按"新回合"的场面算。
        BattleSettlement.SetRound(roundNumber);
        BattleSettlement.BeginTurnFor(side == Side.Local ? BattleSide.Player : BattleSide.Enemy);

        TurnStarted?.Invoke(side == Side.Local, sideTurn);

        // 回合维护里可能有人被袭扰自损打死、大营也可能被打空,先看一眼再决定要不要排敌方自动结束
        if (BattleSettlement.MatchOver) return;

        if (autoEndRoutine != null) { StopCoroutine(autoEndRoutine); autoEndRoutine = null; }
        if (side == Side.Enemy && enemyTurnSeconds > 0f) autoEndRoutine = StartCoroutine(AutoEndEnemyTurn());
    }

    /// <summary>敌方 AI 还没做:等一会儿就当它走完了</summary>
    private IEnumerator AutoEndEnemyTurn()
    {
        yield return new WaitForSeconds(enemyTurnSeconds);
        autoEndRoutine = null;

        if (currentSide == Side.Enemy) EndTurn();
    }

    /// <summary>
    /// 敌方的"等几秒自动结束回合"开关。敌方 AI(EnemyAI)在接管回合时会把它关掉 ——
    /// 不然 AI 还在打分算着,定时器先把回合结束掉了。
    /// </summary>
    public void SetAutoEndEnemyTurn(bool on)
    {
        enemyTurnSeconds = on ? Mathf.Max(0.1f, enemyTurnSeconds <= 0f ? 2f : enemyTurnSeconds) : 0f;

        if (!on && autoEndRoutine != null)
        {
            StopCoroutine(autoEndRoutine);
            autoEndRoutine = null;
        }
    }

    /// <summary>
    /// 对局结束时把"定时自动过回合"和调试键停掉(结算层 BattleSettlement.EndMatch 会调)。
    /// 不停的话,大营被打空之后敌方还会继续过回合、继续摸牌、继续触发疲劳。
    /// </summary>
    public void StopTurnAdvance()
    {
        if (autoEndRoutine != null) { StopCoroutine(autoEndRoutine); autoEndRoutine = null; }
        debugEndTurnOnKey = false;
    }

    /// <summary>结束当前这一方回合时,把这一方的"本回合"标记清掉(§2.4 第 4 步:清「本回合」标记)</summary>
    private void CleanupSide(BattleSide side)
    {
        BattleSettlement.EndTurnFor(side);
    }
}
