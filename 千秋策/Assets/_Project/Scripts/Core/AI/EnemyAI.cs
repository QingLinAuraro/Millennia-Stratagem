using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>AI 姿态(策划案§6.3):每回合开始时按场面算一次,影响评分倾向</summary>
public enum AIState
{
    Balanced,     // 均衡(默认)
    Aggressive,   // 抢血:前军压制权重 ×2、单位交换权重 ×0.5
    Defensive,    // 防守:大营守护权重 ×3,优先解掉前军敌方单位
}

/// <summary>难度(策划案§6.6:Demo 先实现简单难度,普通难度预留)</summary>
public enum AIDifficulty
{
    Easy,       // 简单:±5 随机抖动、10% 失误率
    Normal,     // 普通:无抖动、会算下回合斩杀线(预留)
}

/// <summary>
/// 敌方 AI:规则驱动的评分型 Bot(策划案§6)。不用行为树、不用机器学习,
/// 每层决策都是「枚举合法行动 → 逐项打分 → 执行最高分」,每个决策都打日志(§6.1 调试透明)。
///
/// 三层决策(§6.2):
///   Layer 1 出牌阶段:对每张手牌 × 每个合法入排位置打分;
///   Layer 2 行动阶段:对每个可动单位 × 每个合法的移动/攻击打分,循环执行到没有值得做的;
///   Layer 3 结束判断:没有高分行动就结束回合。
///
/// 它不直接操作 UI —— 全部走 BattlefieldManager 的公共 API(DeployUnit / PlayTactic / MoveUnit / AttackUnit),
/// 与玩家点击是同一条逻辑路径(§6.7),所以 AI 不会开挂、也不会出现"UI 不同步"。
///
/// 【挂载 & 调整】
///   挂在:Battle.unity 里的 BattleCanvas 上(和 TurnController / CommandPointController 同一个物体)。
///         不挂也行:GameBootstrap 会在战斗场景自动补一个,Instance 也会先找后建 —— 但那样 Inspector 上的值全是默认值。
///         它订阅 TurnController.TurnStarted,只有轮到敌方时才接管;找不到 TurnController 会一直重试并警告,
///         后果就是敌方回合没有任何人操作,回合卡在敌方。
///   引用:没有要手连的引用,全走单例(EnemyDeckController / BattlefieldManager / TurnController),
///         拿不到哪个就只跳过对应的那一层决策。
///   常调:
///     · difficulty:简单 / 普通。简单 = ±5 分抖动 + 10% 概率选第二优(§6.6),看着不像挂;
///       普通 = 不抖、不失误(预判 1 回合还没做)。
///     · thinkDelay:每一步之间的"思考"停顿(§6.1 要求 0.8~1.2s,方便玩家观察)。调 0 = 一瞬间走完,看不清过程。
///     · actionThreshold(默认 10):**只给 PeekBestAction()/自检用**,正式回合流程不再拿它卡门槛。
///       以前"低于这个分就不做"正是"有费、有单位能行动,AI 却什么都不做"的原因 ——
///       一次磨血攻击的分值很容易低于阈值,于是被整轮滤掉。现在回合内只要**合法且付得起**就做,
///       分数只决定先后。这个字段保留是为了对外问一句"AI 现在最想干什么、够不够格"。
///     · maxActionsPerTurn(默认 12):一回合最多做几步,防止评分出 bug 时无限循环。
///     · playCards:关掉 = AI 只动场上的单位、不出牌(调试用)。
/// </summary>
[DisallowMultipleComponent]
public class EnemyAI : MonoBehaviour
{    /// <summary>场景里没有就自己建一个,用到才建</summary>
    private static EnemyAI instance;

    public static EnemyAI Instance
    {
        get
        {
            if (instance != null) return instance;
            instance = FindObjectOfType<EnemyAI>();
            if (instance != null) return instance;

            var go = new GameObject("EnemyAI");
            var canvas = CanvasUtil.FindRootCanvas();
            if (canvas != null) go.transform.SetParent(canvas.transform, false);
            instance = go.AddComponent<EnemyAI>();
            return instance;
        }
    }

    /// <summary>
    /// AI 当前最优行动的一份只读快照(自检 / 调试用)。
    /// 想看「AI 是不是只会原地出牌、不肯进攻」,读这个比真跑一回合快得多。
    /// </summary>
    public struct AIDecision
    {
        public bool hasAction;
        public bool meetsThreshold;
        public int score;
        public string kind;
        public string describe;

        public override string ToString()
            => hasAction ? $"【{kind}】{describe}（{score} 分{(meetsThreshold ? "" : ",低于阈值不会执行")}）"
                         : "没有想做的行动";
    }

    /// <summary>AI 想做的一件事(部署 / 出策略牌 / 移动 / 攻击)</summary>
    private enum ActionKind { None, Deploy, Tactic, Move, Attack }

    /// <summary>一个候选行动 + 它的分数</summary>
    private class Candidate
    {
        public ActionKind kind;
        public int score;
        public string describe;

        public CardData card;
        public BattleRow row;
        public int slotIndex;
        public FieldUnit unit;
        public FieldUnit target;
        public BattleRowType moveTo;
    }

    [Header("难度(策划案§6.6)")]
    [Tooltip("简单:±5 分随机抖动 + 10% 概率选第二优;普通:不抖不失误")]
    [SerializeField] private AIDifficulty difficulty = AIDifficulty.Easy;
    [Tooltip("每步之间的思考停顿(策划案§6.1:0.8~1.2s,方便玩家观察)。0 = 一瞬间走完")]
    [SerializeField] private float thinkDelay = 1f;

    [Header("决策阈值(策划案§6.2)")]
    [Tooltip("低于这个分数的行动就不做,直接结束回合。必须低于「向前推进」的分值(进前军基础 20+5),否则 AI 只会原地出牌不进攻")]
    [SerializeField] private int actionThreshold = 10;
    [Tooltip("一回合最多做几步(防评分 bug 导致无限循环)")]
    [SerializeField] private int maxActionsPerTurn = 12;
    [Tooltip("关掉 = AI 只动场上单位、不出牌(调试用)")]
    [SerializeField] private bool playCards = true;

    [Header("调试")]
    [Tooltip("把每个候选行动的分数打到 Console(策划案§6.1 要求决策可打印、可查 bug)")]
    [SerializeField] private bool logScores = false;

    private TurnController turnSource;
    private bool hooked;
    private bool running;
    private AIState currentState = AIState.Balanced;

    /// <summary>当前姿态(表现层/调试想看可以读)</summary>
    public AIState CurrentState => currentState;

    private void Awake() => instance = this;

    private void OnEnable() => HookTurn();

    private void OnDisable() => UnhookTurn();

    private void Update()
    {
        // TurnController 可能比本组件晚一帧才建出来(它也是自举的),订阅失败了就重试
        if (!hooked) HookTurn();
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
        UnhookTurn();
        BattleSettlement.MatchEnded -= OnMatchEnded;
    }

    private void Start()
    {
        BattleSettlement.MatchEnded += OnMatchEnded;
    }

    private void HookTurn()
    {
        if (hooked) return;

        turnSource = TurnController.Instance;
        if (turnSource == null)
        {
            Debug.LogWarning("[AI] 找不到 TurnController,敌方回合不会有人操作。", this);
            return;
        }

        turnSource.TurnStarted += OnTurnStarted;
        hooked = true;
        Debug.Log("[AI] 已接管敌方回合(策划案§6 规则型 AI)");
    }

    private void UnhookTurn()
    {
        if (!hooked) return;
        if (turnSource != null) turnSource.TurnStarted -= OnTurnStarted;
        turnSource = null;
        hooked = false;
    }

    private void OnTurnStarted(bool isLocal, int turnNumber)
    {
        if (isLocal) return;                    // 只接管敌方回合
        if (BattleSettlement.MatchOver) return;

        // AI 接管之后,回合的结束权归 AI(否则 TurnController 那个"等 2 秒自动结束"会和 AI 抢着过回合)
        turnSource.SetAutoEndEnemyTurn(false);

        if (!running) StartCoroutine(TakeTurn());
    }

    private void OnMatchEnded(BattleSide winner, string reason)
    {
        StopAllCoroutines();
        running = false;
    }

    // ================================================================ 主流程(策划案§6.4)

    /// <summary>敌方回合:出牌阶段 → 行动阶段 → 结束回合</summary>
    public IEnumerator TakeTurn()
    {
        running = true;

        var enemy = EnemyDeckController.Instance;
        var board = BattlefieldManager.Instance;

        if (enemy == null || board == null)
        {
            Debug.LogWarning("[AI] 缺 EnemyDeckController 或 BattlefieldManager,这一回合什么都不做。");
            running = false;
            EndTurn();
            yield break;
        }

        currentState = EvaluateState();
        Debug.Log($"[AI] 敌方回合开始:姿态 {StateName(currentState)},CP {enemy.Cp}/{enemy.CpMax},手牌 {enemy.HandCount} 张" +
                  (AnyAttackAvailable() ? ",场上有可出手的攻击" : ",场上暂时打不到人"));

        yield return new WaitForSeconds(thinkDelay);

        // ---------- 回合结构:分层,按"优先级"消费 CP ----------
        //
        // 设计定的优先级(前面的先做,能做完就做完):
        //     部署 > 策略 > 移动 > 攻击 > 空过
        // 而且**尽可能把费用花光**。
        //
        // 这里刻意不用"所有候选放一起按分数排序":那样一个高分攻击会插在两次部署中间,
        // 结果既不按优先级、也容易把 CP 花得七零八落。改成分层之后,每一层自己挑最优解,
        // 上一层做不动了(没钱/没牌/没人能动)才轮到下一层。
        //
        // 注意:层与层之间**不互相压低分数**。以前"场上有能打的攻击 → 部署 −150",
        // 那条和这里的优先级是冲突的 —— 按设计部署本来就排在攻击前面,不该被攻击挤掉;
        // 「部署把 CP 吃光、轮到行动时没费可用」那个真问题改由下面的分层顺序解决。
        if (playCards)
        {
            // 第 1 层:部署(兵种牌)
            yield return PlayCardsOfKind(ActionKind.Deploy);

            // 第 2 层:策略(先部署后放策略 —— 策略卡能配合刚上场的单位)
            yield return PlayCardsOfKind(ActionKind.Tactic);
        }

        // 第 3 层:移动(每个单位每回合 1 AP,所以这一层最多"每个单位一次")
        yield return DoUnitActions(ActionKind.Move);

        // 第 4 层:攻击(移动完再打 —— 设计上移动优先)
        yield return DoUnitActions(ActionKind.Attack);

        // ---------- 结束判断 ----------
        yield return new WaitForSeconds(thinkDelay);    // 让玩家看清最后一步再交回合
        running = false;
        EndTurn();
    }

    /// <summary>
    /// 某一层的出牌循环(部署 / 策略)。挑出**指定类型**里分数最高的那张,一直出到出不动为止
    /// (没牌 / 没钱 / 分数不为正)。
    /// </summary>
    private IEnumerator PlayCardsOfKind(ActionKind kind)
    {
        for (int guard = 0; guard < maxActionsPerTurn && !BattleSettlement.MatchOver; guard++)
        {
            var best = FindBestCardPlay(kind);
            if (best == null || best.score <= 0) yield break;

            yield return Execute(best);
            yield return new WaitForSeconds(thinkDelay);
        }
    }

    /// <summary>
    /// 某一层的单位行动循环(移动 / 攻击)。
    ///
    /// **这里不用 actionThreshold 卡门槛** —— 那正是"有费、有单位能行动,AI 却什么都不做"的原因:
    /// 只要还有单位能行动、又付得起钱,空着就是白扔一次行动。行动力每回合补满、不用就作废,
    /// 所以守法只有一个:**不合法(打不到 / 去不了)才不做**,分数只决定"先做哪一件"。
    /// </summary>
    private IEnumerator DoUnitActions(ActionKind kind)
    {
        for (int guard = 0; guard < maxActionsPerTurn && !BattleSettlement.MatchOver; guard++)
        {
            var best = FindBestUnitAction(kind);
            if (best == null) yield break;            // 这一层没得做了

            yield return Execute(best);
            yield return new WaitForSeconds(thinkDelay);
        }
    }

    private void EndTurn()
    {
        if (BattleSettlement.MatchOver) return;

        var turn = TurnController.Instance;
        if (turn != null && turn.CurrentSide == TurnController.Side.Enemy) turn.EndTurn();
    }

    /// <summary>把候选行动落到场上(出牌 / 移动 / 攻击),结果写成日志(§6.1 决策可查)</summary>
    private IEnumerator Execute(Candidate action)
    {
        if (action == null) yield break;

        var board = BattlefieldManager.Instance;
        if (board == null) yield break;

        switch (action.kind)
        {
            case ActionKind.Deploy:
            {
                if (!board.DeployUnit(action.card, action.row, action.slotIndex, out _, out string reason))
                {
                    Debug.Log($"[AI] 部署「{action.card.cardName}」失败:{reason}");
                    break;
                }

                EnemyDeckController.Instance?.PlayCard(action.card);
                Debug.Log($"[AI] 部署「{action.card.cardName}」→ {action.row.DisplayName}（分数 {action.score}）");
                break;
            }

            case ActionKind.Tactic:
            {
                if (!board.PlayTactic(action.card, BattleSide.Enemy, action.target, action.row, out string reason))
                {
                    Debug.Log($"[AI] 释放「{action.card.cardName}」失败:{reason}");
                    break;
                }

                Debug.Log($"[AI] 释放策略卡「{action.card.cardName}」（分数 {action.score}）:{reason}");
                break;
            }

            case ActionKind.Move:
            {
                if (!board.MoveUnit(action.unit, action.moveTo, out string reason))
                {
                    Debug.Log($"[AI] 「{action.unit.DisplayName}」移动失败:{reason}");
                    break;
                }

                // 日志带上落点排的真实名字:光看「中军」分不清是它自己的中军还是玩家的中军
                Debug.Log($"[AI] 「{action.unit.DisplayName}」移动（分数 {action.score}）:" +
                          $"「{action.unit.DisplayName}」移动到 {RowName(action.moveTo)}" +
                          $"（现在落在 {action.unit.Row?.DisplayName}）");
                break;
            }

            case ActionKind.Attack:
            {
                if (!board.AttackUnit(action.unit, action.target, out string reason))
                {
                    Debug.Log($"[AI] 「{action.unit.DisplayName}」攻击失败:{reason}");
                    break;
                }

                Debug.Log($"[AI] 「{action.unit.DisplayName}」攻击「{action.target.DisplayName}」（分数 {action.score}）");
                break;
            }
        }
    }

    // ================================================================ 姿态(§6.3)

    private AIState EvaluateState()
    {
        var myCamp = BattleSettlement.FindCamp(BattleSide.Enemy);
        var hisCamp = BattleSettlement.FindCamp(BattleSide.Player);

        // 敌方大营快没了 → 抢血;自己大营快没了 → 防守(先看更急的那条)
        if (myCamp != null && myCamp.Hp <= 10) return AIState.Defensive;
        if (hisCamp != null && hisCamp.Hp <= 10) return AIState.Aggressive;

        int myPower = BoardPower(BattleSide.Enemy);
        int hisPower = BoardPower(BattleSide.Player);

        if (hisPower > 0 && myPower >= hisPower * 1.5f) return AIState.Aggressive;
        if (myPower > 0 && hisPower >= myPower * 1.5f) return AIState.Defensive;

        return AIState.Balanced;
    }

    /// <summary>场面战力(场上兵牌的 ATK+HP 之和),姿态判定用</summary>
    private int BoardPower(BattleSide side)
    {
        var units = CardEffectResolver.UnitsOf(side);
        int power = 0;

        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit == null || !unit.IsUnit || !unit.IsAlive) continue;
            power += unit.Atk + unit.Hp;
        }

        return power;
    }

    private static string StateName(AIState state) => state switch
    {
        AIState.Aggressive => "抢血",
        AIState.Defensive => "防守",
        _ => "均衡",
    };

    // ================================================================ Layer 1:出牌

    private Candidate FindBestCardPlay(ActionKind kind)
    {
        var enemy = EnemyDeckController.Instance;
        var board = BattlefieldManager.Instance;
        if (enemy == null || board == null) return null;

        var hand = enemy.HandCards;
        if (hand == null || hand.Count == 0) return null;

        var candidates = new List<Candidate>();

        for (int i = 0; i < hand.Count; i++)
        {
            var card = hand[i];
            if (card == null) continue;
            if (!enemy.CanAfford(card.deploymentCost)) continue;

            if (card.IsUnitCard)
            {
                // 这一层只干"部署",策略卡归上一层/下一层 —— 设计定的优先级是 部署 > 策略
                if (kind != ActionKind.Deploy) continue;

                // §8.1.1:一条兵牌可以贴在任意成员左右 —— 排内位置只影响守护的相邻判定,
                // 所以 AI 只在「链首 / 链尾」两个位置里挑(中间的插法对 AI 的收益没有区别)
                AddDeployCandidates(candidates, board, card, board.EnemyMid);
                AddDeployCandidates(candidates, board, card, board.EnemyBack);
            }
            else
            {
                if (kind != ActionKind.Tactic) continue;

                AddTacticCandidates(candidates, card);
            }
        }

        if (logScores) LogCandidates(kind == ActionKind.Deploy ? "部署" : "策略", candidates);
        return PickBest(candidates);
    }

    private void AddDeployCandidates(List<Candidate> candidates, BattlefieldManager board, CardData card, BattleRow row)
    {
        if (row == null) return;
        if (!BattleRules.CanDeployUnit(card, row, board.EnemyMid, out _)) return;

        int chainLength = row.Units.Count;
        int[] slots = chainLength <= 0 ? new[] { 0 } : new[] { 0, chainLength };

        for (int i = 0; i < slots.Length; i++)
        {
            candidates.Add(new Candidate
            {
                kind = ActionKind.Deploy,
                card = card,
                row = row,
                slotIndex = slots[i],
                score = ScoreDeploy(card, row),
                describe = $"部署「{card.cardName}」→ {row.DisplayName}",
            });
        }
    }

    private void AddTacticCandidates(List<Candidate> candidates, CardData card)
    {
        var caster = new CardEffectResolver.Caster(BattleSide.Enemy);

        // 目标型策略卡:把每个可能的合法目标都试一遍,取最高分的那一个
        FieldUnit bestUnit = null;
        BattleRow bestRow = null;
        int bestScore = int.MinValue;

        foreach (var unit in EnemyVisibleTargets(card))
        {
            if (!CardEffectResolver.CanResolve(card, caster, unit, null, out _)) continue;

            int score = ScoreTacticOnUnit(card, unit);
            if (score > bestScore) { bestScore = score; bestUnit = unit; bestRow = null; }
        }

        foreach (var row in EnemyVisibleRows(card))
        {
            if (!CardEffectResolver.CanResolve(card, caster, null, row, out _)) continue;

            int score = ScoreTacticOnRow(card, row);
            if (score > bestScore) { bestScore = score; bestUnit = null; bestRow = row; }
        }

        if (bestUnit == null && bestRow == null)
        {
            // 无目标类(抽牌 / 随机目标 / 弃牌):只要效果能落地就算一个候选
            if (!CardEffectResolver.CanResolve(card, caster, null, null, out _)) return;
            bestScore = ScoreTacticNoTarget(card);
        }

        candidates.Add(new Candidate
        {
            kind = ActionKind.Tactic,
            card = card,
            unit = bestUnit,
            row = bestRow,
            score = bestScore,
            describe = $"释放「{card.cardName}」",
        });
    }

    /// <summary>AI 能指定哪些单位当目标(按卡的 targetType:敌方卡看我方、buff 卡看敌方自己)</summary>
    private IEnumerable<FieldUnit> EnemyVisibleTargets(CardData card)
    {
        var mine = CardEffectResolver.UnitsOf(BattleSide.Enemy);
        var yours = CardEffectResolver.UnitsOf(BattleSide.Player);

        bool wantAlly = card.targetType == TacticTargetType.AllyUnit || card.targetType == TacticTargetType.AllyBuilding;
        var pool = wantAlly ? mine : yours;

        for (int i = 0; i < pool.Count; i++)
        {
            var unit = pool[i];
            if (unit == null || !unit.IsAlive) continue;
            if (BattleRules.CanTargetUnit(card, unit, out _)) yield return unit;
        }
    }

    private IEnumerable<BattleRow> EnemyVisibleRows(CardData card)
    {
        var board = BattlefieldManager.Instance;
        if (board == null) yield break;

        var rows = board.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null) continue;
            if (BattleRules.CanTargetRow(card, row, out _)) yield return row;
        }
    }

    // ================================================================ Layer 2:行动

    /// <summary>
    /// AI 这一步打算干什么(只读,不改战场)。给自检和调试用:
    /// 「AI 到底会不会进攻」不用真跑一整个回合去看,问一句就知道它现在的最优解是什么。
    ///
    /// 正式回合是分层的(部署 > 策略 > 移动 > 攻击),这里只问**行动**这两层:
    /// 两边各取最优再比一次,分数高的那个就是它下一步真会做的(攻击并列时优先报攻击,
    /// 因为自检要验的正是"场上有能打的目标时不会空过")。
    /// </summary>
    public AIDecision PeekBestAction()
    {
        var move = FindBestUnitAction(ActionKind.Move);
        var attack = FindBestUnitAction(ActionKind.Attack);

        Candidate best = attack;
        if (best == null || (move != null && move.score > best.score)) best = move;

        if (best == null)
            return new AIDecision { hasAction = false, score = int.MinValue, describe = "没有想做的行动", kind = "无" };

        return new AIDecision
        {
            hasAction = true,
            score = best.score,
            describe = best.describe,
            kind = best.kind.ToString(),
            meetsThreshold = best.score >= actionThreshold,
        };
    }

    private Candidate FindBestUnitAction(ActionKind kind)
    {
        var board = BattlefieldManager.Instance;
        if (board == null) return null;

        var candidates = new List<Candidate>();
        var mine = CardEffectResolver.UnitsOf(BattleSide.Enemy);
        var enemy = EnemyDeckController.Instance;

        for (int i = 0; i < mine.Count; i++)
        {
            var unit = mine[i];
            if (unit == null || !unit.IsUnit || !unit.CanActNow) continue;

            // 行动要先付得起:§2.5 每次行动 = 1 AP + 该单位的行动费用 CP
            if (enemy != null && !enemy.CanAfford(BattleRules.ActionCostOf(unit.Data))) continue;

            if (kind == ActionKind.Attack)
            {
                foreach (var target in AllAttackTargets())
                {
                    if (!BattleRules.CanAttack(unit, target, out _)) continue;

                    candidates.Add(new Candidate
                    {
                        kind = ActionKind.Attack,
                        unit = unit,
                        target = target,
                        score = ScoreAttack(unit, target),
                        describe = $"「{unit.DisplayName}」攻击「{target.DisplayName}」",
                    });
                }
            }
            else
            {
                // 移动(§2.3 只能进相邻排;§7.4 袭扰骑兵可撤回共享前军)
                foreach (var rowType in new[] { BattleRowType.Back, BattleRowType.Mid, BattleRowType.Front })
                {
                    var carrier = FindRowForMove(unit, rowType);
                    if (!BattleRules.CanMoveTo(unit, unit.Row, rowType, carrier, out _, out _)) continue;

                    candidates.Add(new Candidate
                    {
                        kind = ActionKind.Move,
                        unit = unit,
                        moveTo = rowType,
                        score = ScoreMove(unit, rowType),
                        describe = $"「{unit.DisplayName}」移动到 {RowName(rowType)}（落在 {DestName(unit, rowType)}）",
                    });
                }
            }
        }

        if (logScores) LogCandidates(kind == ActionKind.Attack ? "攻击" : "移动", candidates);

        // 一个攻击候选都没有,却确实有人站得够近 —— 那不是"没人能打",而是被守护挡住了(§4.3)。
        // 这句日志就是要区分这两种情况:没有它,界面/日志上只会显示"打不到人",
        // 看上去像 bug,其实规则完全正确(守护单位自身可选,它保护的邻居不可选)。
        if (kind == ActionKind.Attack && candidates.Count == 0) ExplainNoAttack();

        return PickBest(candidates);
    }

    /// <summary>场上有人站得够近却一个攻击候选都没有 → 把"为什么打不着"打出来(守护是主因)</summary>
    private void ExplainNoAttack()
    {
        var mine = CardEffectResolver.UnitsOf(BattleSide.Enemy);
        var reasons = new List<string>();
        int guarded = 0, outOfRange = 0, blocked = 0;

        for (int i = 0; i < mine.Count; i++)
        {
            var unit = mine[i];
            if (unit == null || !unit.IsUnit || !unit.IsAlive) continue;

            foreach (var target in AllAttackTargets())
            {
                if (target == null || !target.IsAlive) continue;

                // 能打就不该走到 ExplainNoAttack(它是"一个候选都没有"时才调的)
                if (BattleRules.CanAttack(unit, target, out _)) continue;

                BattleRules.CanAttack(unit, target, out string why);
                why ??= "未知";

                if (why.Contains("守护")) { guarded++; reasons.Add($"{unit.DisplayName}→{target.DisplayName}:{why}"); }
                else if (why.Contains("射程") || why.Contains("排距") || why.Contains("相邻") || why.Contains("前方"))
                    outOfRange++;
                else blocked++;
            }
        }

        Debug.Log($"[AI] 这回合没有攻击可选。逐条原因统计:超出射程 {outOfRange} 条、被守护挡住 {guarded} 条、" +
                  $"其它 {blocked} 条。");

        if (guarded > 0)
            Debug.Log("[AI] 被守护挡住的具体目标(§4.3:守护单位自身可选,它左右紧邻的邻居不可选):" +
                      string.Join("、", reasons));

        // 其实常常还能打守护单位本身 —— 那才是这一层唯一打得着的东西
        for (int i = 0; i < mine.Count; i++)
        {
            var unit = mine[i];
            if (unit == null || !unit.IsUnit || !unit.CanActNow) continue;

            foreach (var target in AllAttackTargets())
            {
                if (target == null || !BattleRules.CanAttack(unit, target, out _)) continue;

                Debug.Log($"[AI] 但守护单位本身能打:「{unit.DisplayName}」→「{target.DisplayName}」");
                return;
            }
        }
    }

    /// <summary>场上有没有可立刻出手的攻击(只问一句,不改战场)。回合开始打一行日志、自检也用</summary>
    private bool AnyAttackAvailable()
    {
        var mine = CardEffectResolver.UnitsOf(BattleSide.Enemy);
        var enemy = EnemyDeckController.Instance;

        for (int i = 0; i < mine.Count; i++)
        {
            var unit = mine[i];
            if (unit == null || !unit.IsUnit || !unit.CanActNow) continue;
            if (enemy != null && !enemy.CanAfford(BattleRules.ActionCostOf(unit.Data))) continue;

            foreach (var target in AllAttackTargets())
                if (BattleRules.CanAttack(unit, target, out _)) return true;
        }

        return false;
    }

    /// <summary>
    /// 这一步会落到哪条排。
    /// 直接问 BattlefieldManager.ResolveMoveRow —— **不要再在这里算一份**:
    /// 以前这里自己算(和 BattlefieldManager 的 FindRow 不是同一套),站在共享前军时
    /// 两边对"中军"的理解会不一致,判定就用错排的容量与占位。
    /// </summary>
    private static BattleRow FindRowForMove(FieldUnit unit, BattleRowType type)
    {
        var board = BattlefieldManager.Instance;
        return board != null ? board.ResolveMoveRow(unit, type) : null;
    }

    private static string RowName(BattleRowType type) => type switch
    {
        BattleRowType.Back => "后军",
        BattleRowType.Mid => "中军",
        _ => "前军",
    };

    /// <summary>
    /// 这一步实际会落到哪条排的**具体名字**。
    /// 「中军」这种叫法是有歧义的:敌方单位站在共享前军时,它的「中军」是**玩家的中军**
    /// (BattlefieldManager.ResolveMoveRow:前军 → 中军 = 对面中军 = 袭扰),
    /// 而玩家单位的中军是它自己的。日志里不写清楚就会看成"AI 退回自己中军了",
    /// 所以这里把落点排的真实名字打出来。
    /// </summary>
    private static string DestName(FieldUnit unit, BattleRowType targetType)
    {
        var board = BattlefieldManager.Instance;
        if (board == null) return RowName(targetType);

        // 落点解析统一走 ResolveMoveRow,日志和判定才不会各说各话
        var row = board.ResolveMoveRow(unit, targetType);
        return row != null ? row.DisplayName : RowName(targetType);
    }

    /// <summary>场上所有我方(玩家)成员 —— 攻击目标池</summary>
    private static IEnumerable<FieldUnit> AllAttackTargets()
    {
        var yours = CardEffectResolver.UnitsOf(BattleSide.Player);
        for (int i = 0; i < yours.Count; i++)
        {
            var unit = yours[i];
            if (unit == null || !unit.IsAlive) continue;
            yield return unit;
        }
    }

    // ================================================================ 评分(§6.2 / §6.3)

    private int ScoreDeploy(CardData card, BattleRow row)
    {
        // 铺场价值:费用效率(§6.2)
        int score = (card.atk + card.hp) / Mathf.Max(1, card.deploymentCost) * 5;

        // 注:这里曾经有"场上有能打的攻击就 −150"。删掉了 ——
        // 设计定的优先级是 **部署 > 策略 > 移动 > 攻击 > 空过**,部署本来就该排在攻击前面,
        // 用攻击去压低部署和优先级是反的。「部署把 CP 吃光、轮到行动时没费可用」那个真问题
        // 改由 TakeTurn 的分层顺序解决(部署层做不动了才轮到移动层、攻击层)。
        // canAttackNow 保留:ScoreAttack 里"能打就必须打"的加成还要用。

        // 「闪击」当天能动,评分时算一点额外价值;「守护」保护大营(§6.3 防守姿态更值钱)
        if (BattleRules.HasKeyword(card, Keyword.Blitz)) score += 15;
        if (BattleRules.HasKeyword(card, Keyword.Guard))
            score += currentState == AIState.Defensive ? 45 : 20;

        // 中军挤压:中军满了还往上堆 = 白占位置,先让场上的兵往前打(推动作由 ScoreMove 负责)
        var enemyMid = BattlefieldManager.Instance != null ? BattlefieldManager.Instance.EnemyMid : null;
        bool boardIsFull = enemyMid != null && enemyMid.IsUnitCapacityFull;

        if (row != null && row.RowType == BattleRowType.Mid)
        {
            if (boardIsFull) score -= 80;
            else
            {
                score += currentState == AIState.Defensive ? 30 * 3 : 30;   // 大营守护(§6.2 +30)
                score += 20;                                                // 前军压制(§6.2 +20)
            }
        }

        // 后军只有 1 个兵牌位(§2.3):铺场阶段别指望它,中军满了再去
        if (row != null && row.RowType == BattleRowType.Back && !boardIsFull) score -= 10;

        // 抽牌类卡在牌堆空了之后是纯自伤,给个重罚(§6.5 卡库抽空)
        if (IsDrawCard(card) && DeckIsEmpty(BattleSide.Enemy)) score -= 200;

        return score + Jitter();
    }

    private int ScoreTacticOnUnit(CardData card, FieldUnit target)
    {
        if (target == null) return int.MinValue;

        bool hostile = target.Side == BattleSide.Player;
        int score = 0;

        if (hostile)
        {
            // 伤害类:能一下打死就是大赚;打建筑按"拆掉的价值"算
            if (target.IsCamp) score += 120;
            else if (target.IsBuilding) score += 500;        // §6.2 建筑斩杀 +500
            else if (target.Hp <= 3) score += 200;           // 能清掉的单位
            else score += 30;

            if (currentState == AIState.Aggressive) score = Mathf.RoundToInt(score * 0.5f);
        }
        else
        {
            // 友方增益:给场上最能打的那个加
            score += 20 + target.Atk * 5;
            if (target.Hp <= 2) score += 10;                 // 快死了,保一下
        }

        return score + Jitter();
    }

    private int ScoreTacticOnRow(CardData card, BattleRow row)
    {
        if (row == null) return int.MinValue;

        int score = 0;
        var members = BattleRules.Snapshot(row);
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (m == null || !m.IsAlive) continue;
            score += m.IsBuilding && m.IsCamp ? 40 : 15;
        }

        return score + Jitter();
    }

    private int ScoreTacticNoTarget(CardData card)
    {
        int score;

        if (IsDrawCard(card))
        {
            // §6.5 卡库抽空:牌堆已空时不再打抽牌类策略卡(白付 CP 且加速自伤)
            if (DeckIsEmpty(BattleSide.Enemy)) return -500;

            int handRoom = Hand.MaxSize - (EnemyDeckController.Instance?.HandCount ?? 0);
            score = handRoom > 0 ? 40 : -50;                 // 手牌满了再抽就是白扔
        }
        else
        {
            score = 60;                                      // 随机伤害 / 弃牌这类,默认给个中等分
        }

        return score + Jitter();
    }

    /// <summary>
    /// 攻击的价值(§6.2)。**原则:任何一次攻击都是正的。**
    ///
    /// 设计定的优先级是 部署 > 策略 > 移动 > 攻击 > 空过,攻击排在"空过"前面 ——
    /// 也就是说只要打得到、又付得起,打出去就比什么都不做强(削掉的血是真的,行动力不用就作废)。
    /// 所以这里不再出现负分:以前"打不动"是 −50、"磨血"是 −100,结果这些攻击全被阈值滤掉,
    /// 变成"有费、有单位能行动,AI 却什么都不做"。分数现在只决定**先打哪一个**。
    /// (真要拦住某次攻击,靠的是 CanAttack 判不合法,而不是给负分。)
    /// </summary>
    private int ScoreAttack(FieldUnit attacker, FieldUnit target)
    {
        if (attacker == null || target == null) return int.MinValue;

        int attackerAtk = Mathf.Max(0, attacker.Atk - target.DamageReduction);
        int targetAtk = Mathf.Max(0, target.Atk - attacker.DamageReduction);

        // 斩杀检测:能直接打掉大营 → 最高优先(§6.2 斩杀检测 +9999)
        if (target.IsCamp && attackerAtk >= target.Hp) return 9999;

        // 建筑斩杀(§6.2 +500)
        if (target.IsBuilding && attackerAtk >= target.Hp) return 500;

        int score = 0;

        bool kills = attackerAtk > 0 && attackerAtk >= target.Hp;
        bool dies = BattleRules.Counters(attacker, target) && targetAtk >= attacker.Hp;

        if (attackerAtk <= 0 && targetAtk > 0)
        {
            // 被重甲吃光伤害、还要白挨一次反击 —— 这一下确实不划算,但仍然给它一个**很小的正分**:
            // 排序上垫底就够了(有别的攻击就打别的),不该变成"空过"。
            score += 1;
        }
        else if (kills && !dies) score += 200;                    // 白吃一个
        else if (kills && dies) score += 50 + (target.Atk + target.Hp) - (attacker.Atk + attacker.Hp);   // 一换一,看身材差
        else
        {
            // 磨血不是"没换到就亏"。这里原本是 -100,结果是"打一下大营 3 点"输给"再铺一张牌",
            // AI 就只出牌不打人 —— 攻击的分数必须压过部署,场上有人能打就必须打。
            score += 15;                                     // 压制底分:削掉的血是真的
            score += attackerAtk * 4;                        // 削得越多越值
        }

        // 打大营 = 推进胜利,抢血姿态下更值钱
        if (target.IsCamp) score += currentState == AIState.Aggressive ? 60 : 40;

        // 单位交换效率:抢血姿态不怕换,防守姿态更看重保住自己人
        if (currentState == AIState.Aggressive) score = Mathf.RoundToInt(score * 0.5f);

        // 防守姿态:优先解掉已经压到我方中军/前军的敌方单位
        // (判据用 target.Side —— 前军是共享排,它的 row.Side 永远是 Player,拿它判不出来)
        if (currentState == AIState.Defensive && (target.Row == null || target.Row.RowType != BattleRowType.Back))
            score += 40;

        // 打建筑不加太多分:建筑不是胜负条件,拆了才有 Debuff
        if (target.IsBuilding && !target.IsCamp) score += 25;

        // ---- 破盾:把守护单位打掉,后面那一串才打得着(§4.3) ----
        // 守护单位本身永远可选(守护之间不互保),所以它经常是**唯一**能打的那一个。
        // 打掉它 = 解开左右紧邻的保护,这是有额外价值的,否则 AI 会觉得"只有这一个能打"
        // 而把行动力浪费在别处。
        if (BattleRules.HasKeyword(target.Data, Keyword.Guard) && kills && !dies)
            score += 40;

        // 兜底:半血交换在抢血姿态下可能被乘成 0 或负数,拉回"至少比空过强"
        return Mathf.Max(1, score + Jitter());
    }

    /// <summary>
    /// 移动的价值(§6.2)。核心只有一句:**移动是为了"下一步能打"**,不是为了让棋盘好看。
    ///
    /// 所以"从这儿走到那儿能不能打到人"是主项 —— 本来打不到、挪一步就能打到(尤其是能打大营),
    /// 分值必须远高于原地不动的底分,否则 AI 会在中军堆满兵牌却一步不往前挪(实战里就是这么卡住的)。
    /// </summary>
    private int ScoreMove(FieldUnit unit, BattleRowType targetType)
    {
        if (unit == null) return int.MinValue;

        int score = 0;
        var boardRef = BattlefieldManager.Instance;

        // 这个单位"站在原地"能打出的最好一下是多少分(0 = 现在打不着任何人)。
        // 走位评分要用它:「打得到就先打」是 §6.2 的第一原则,走位不该把一次出手机会顶掉。
        int bestAttackFromHere = BestAttackFrom(unit, unit.Row);

        // 挪过去之后能打出的最好一下(比较"离开原地会损失多少出手机会"用)
        int destAttack = BestAttackFrom(unit, FindRowForMove(unit, targetType));

        // 前军压制:进共享前军 = 威胁敌方大营(§6.2 +20,抢血姿态 ×2)
        if (targetType == BattleRowType.Front)
            score += 20 * (currentState == AIState.Aggressive ? 2 : 1);

        // 骑兵袭扰:能占住敌方中军 1 容量、还能拆后军建筑(§7.4)
        if (unit.Row != null && unit.Row.RowType == BattleRowType.Front && targetType == BattleRowType.Mid
            && unit.Data != null && unit.Data.unitType == UnitType.Cavalry)
        {
            bool healthy = unit.Hp > 2;

            // 已经站在前军、这一回合又打不着人:横竖是站着,进敌方中军至少占掉对面一格容量。
            // 打不着就往前顶,别在前军白站着 —— 对手随时可以把你从前军清掉。
            if (healthy && bestAttackFromHere == 0) score += 60;
            // 打得到人:先把这一下打出去,下回合再考虑袭扰(进敌方中军这一步本身就要花一次行动)
            else if (healthy) score -= 20;
            // 残血:不值得再往里冲,给个负分让它去干别的(想撤的话下面的撤回项会给分)
            else score -= 10;
        }

        // §6.5 袭扰骑兵残血 → 撤回保战力。
        // 注意"刚冲进去的那个回合"在 BattleRules.CanMoveTo 里已经堵死了 —— 袭扰的代价
        // (下回合 ATK+1 + 自损 2)还没结算过,不能让它白进白出。这里只给"该不该撤"打分。
        if (unit.IsRaiding && targetType == BattleRowType.Front)
            score += unit.Hp <= 2 ? 80 : 10;

        // 防守姿态:往后退守中军(大营在中军)
        if (currentState == AIState.Defensive)
        {
            if (unit.Row != null && unit.Row.RowType == BattleRowType.Front && targetType == BattleRowType.Mid) score += 40;
        }

        // 底分:位置微调。放在"打破僵局"之前算,这样推进的净收益一目了然
        score += 5;

        // ---- 打破僵局:这一步能不能把"打不到"变成"打得到" ----
        // 这是 AI 从"铺场"切换到"进攻"的那个开关。没有它,中军满了之后 AI 会一直原地出牌,
        // 眼睁睁看着玩家先动手。
        int breakthrough = BreakthroughGain(unit, targetType, boardRef);
        if (breakthrough > 0) score += breakthrough;

        // ---- 原地已经能打,就别为了挪窝把这次出手机会扔掉 ----
        // 走位只有在"打不着"或者"能打得更好"的时候才划算。上面那条"骑兵有仗打时进敌方中军 −20"
        // 就是这条的一个特例(骑兵射程 2,从前军能打,进了敌方中军反而打不着)。
        // 少了这一条,骑兵就会在前面那种情况下反复折返:每一次都在放弃一次实打实的攻击。
        if (destAttack >= 15 && bestAttackFromHere - destAttack >= 20) score -= bestAttackFromHere;

        // ---- 中军挤压:中军塞满了就没必要再往回塞,往前走才是出路 ----
        var mid = boardRef != null ? boardRef.EnemyMid : null;
        if (mid != null && mid.IsUnitCapacityFull && targetType == BattleRowType.Front) score += 30;

        // ---- 不许往回缩 ----
        // 前军是"抢"来的(§2.3 只容一方),站上去就等于占住了进攻的桥头;主动退下来基本是白让节奏,
        // 尤其当玩家还有兵在前军时 —— 那是把阵地直接让出去。所以往回走要给足惩罚,
        // 只留一个例外:防守姿态下令退守中军(大营在中军)。
        if (!unit.IsRaiding && currentState != AIState.Defensive && unit.Row != null)
        {
            bool fallingBack = unit.Row.RowType == BattleRowType.Front && targetType == BattleRowType.Mid;
            bool retreating = unit.Row.RowType == BattleRowType.Mid && targetType == BattleRowType.Back;

            if (fallingBack)
            {
                score -= 60;
                // 前军里还有玩家单位 = 退下来就是把桥头让给他
                if (HasPlayerUnitInFront(boardRef)) score -= 60;
            }
            else if (retreating)
            {
                score -= 60;      // 中军往前顶才是正事,别往后躲
            }
        }

        // 大营告急时的「守护」:留在中军挡刀比自己冲出去更值,大营就在中军(§4.3 / §6.3)
        if (BattleRules.HasGuard(unit) && targetType == BattleRowType.Front)
        {
            var myCamp = BattleSettlement.FindCamp(BattleSide.Enemy);
            if (myCamp != null && myCamp.Hp <= 10) score -= 40;
        }

        return score + Jitter();
    }

    /// <summary>
    /// 走到这条排能"多打到"什么:返回新位置可攻击目标里最高的那次攻击的分数(打不到就是 0)。
    /// 只比"出手机会",不改场上的任何东西 —— 移过去之后能打到谁,用 CanAttack 在目标排上原地试算。
    ///
    /// 除了"能不能打到人",还要算上"离大营够不够得着":近战射程只有 1 排,从敌方中军是够不到玩家大营的,
    /// **必须先站上前军** —— 所以只要前军这个位置能威胁到大营,这一项就要给足,AI 才会往上顶。
    /// </summary>
    private int BreakthroughGain(FieldUnit unit, BattleRowType targetType, BattlefieldManager board)
    {
        if (board == null || unit == null || unit.Row == null) return 0;

        var dest = FindRowForMove(unit, targetType);
        if (dest == null || dest == unit.Row) return 0;

        int best = 0;
        var yours = CardEffectResolver.UnitsOf(BattleSide.Player);

        for (int i = 0; i < yours.Count; i++)
        {
            var target = yours[i];
            if (target == null || !target.IsAlive) continue;

            // 站在目标排上打得到吗?打得到就说明这一步是真的"够到了"
            if (!BattleRules.CanAttack(unit, target, dest, out _)) continue;

            int gain = ScoreAttack(unit, target);
            if (gain > best) best = gain;
        }

        // 大营的"射程压力":站在这个位置上够不够得到玩家大营?够得到就说明这一步是往胜利走
        var theirCamp = BattleSettlement.FindCamp(BattleSide.Player);
        if (theirCamp != null && theirCamp.IsAlive
            && BattleRules.CanAttack(unit, theirCamp, dest, out _))
        {
            int campGain = ScoreAttack(unit, theirCamp);
            // 本来在大营的射程之内就别重复加分了(那是"已经在打",不是"这一步换来的")
            bool alreadyThere = unit.Row != null && BattleRules.CanAttack(unit, theirCamp, unit.Row, out _);
            if (!alreadyThere && campGain > best) best = campGain;
        }

        return best;
    }

    /// <summary>
    /// 这个单位站在 fromRow 上能打出的最好一下值多少分(打不着任何人返回 0)。
    /// 走位评分用它判"原地就有出手机会吗" —— 有就别挪窝(§6.2:能打就先打)。
    /// </summary>
    private int BestAttackFrom(FieldUnit unit, BattleRow fromRow)
    {
        if (unit == null || fromRow == null || !unit.IsAlive) return 0;

        int best = 0;
        var yours = CardEffectResolver.UnitsOf(BattleSide.Player);

        for (int i = 0; i < yours.Count; i++)
        {
            var target = yours[i];
            if (target == null || !target.IsAlive) continue;
            if (!BattleRules.CanAttack(unit, target, fromRow, out _)) continue;

            int gain = ScoreAttack(unit, target);
            if (gain > best) best = gain;
        }

        return best;
    }

    /// <summary>共享前军里还有没有玩家单位(判断"退下来是不是等于把桥头让出去")</summary>
    private static bool HasPlayerUnitInFront(BattlefieldManager board)
    {
        if (board == null || board.PlayerFront == null) return false;

        var members = BattleRules.Snapshot(board.PlayerFront);
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (m != null && m.IsAlive && !m.IsBuilding && m.Side == BattleSide.Player) return true;
        }

        return false;
    }

    // ================================================================ 打分工具

    /// <summary>简单难度的随机抖动(§6.2 -5 ~ +5);普通难度不抖</summary>
    private int Jitter()
        => difficulty == AIDifficulty.Easy ? Random.Range(-5, 6) : 0;

    /// <summary>挑最高分。简单难度有 10% 概率挑第二优(§6.6 失误率)</summary>
    private Candidate PickBest(List<Candidate> candidates)
    {
        if (candidates == null || candidates.Count == 0) return null;

        candidates.Sort((a, b) => b.score.CompareTo(a.score));

        if (difficulty == AIDifficulty.Easy && candidates.Count > 1 && Random.value < 0.1f)
        {
            Debug.Log($"[AI] 简单难度失误:挑了第二优的「{candidates[1].describe}」（{candidates[1].score}）" +
                      $",最优是「{candidates[0].describe}」（{candidates[0].score}）");
            return candidates[1];
        }

        return candidates[0];
    }

    private void LogCandidates(string phase, List<Candidate> candidates)
    {
        if (!logScores) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[AI] {phase}候选 {candidates.Count} 个(姿态 {StateName(currentState)}):");
        for (int i = 0; i < candidates.Count && i < 12; i++)
            sb.AppendLine($"    {candidates[i].score,6}  {candidates[i].describe}");
        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// 这张牌会不会过牌。「摸牌」已经不作为卡面词条存在了(词条只放固定数值/内容的能力),
    /// 所以判据从 Keyword.DrawCards 改成:先看登记表里有没有抽牌效果,再退回效果文案。
    /// </summary>
    private static bool IsDrawCard(CardData card)
    {
        if (card == null) return false;

        var set = CardEffectDatabase.Get(card);
        if (set != null && !set.IsEmpty)
        {
            for (int i = 0; i < set.effects.Count; i++)
                if (set.effects[i] != null && set.effects[i].kind == CardEffectKind.DrawCards) return true;
        }

        return card.effectText != null
               && (card.effectText.Contains("抽取") || card.effectText.Contains("摸牌"));
    }

    private static bool DeckIsEmpty(BattleSide side)
    {
        if (side == BattleSide.Enemy) return (EnemyDeckController.Instance?.DeckCount ?? 1) <= 0;

        var deck = Object.FindObjectOfType<DeckController>();
        return deck == null || deck.DeckCount <= 0;
    }
}
