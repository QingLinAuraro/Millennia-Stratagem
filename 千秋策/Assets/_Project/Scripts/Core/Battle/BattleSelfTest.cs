using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 战斗流程自检:按一下键,把「开局布置 → 部署 → 移动 → 攻击/反击 → 回合交接 → 疲劳 → 胜负」
/// 这条主线在**真实场景里**从头跑一遍,每条打一行 ✅/❌ 到 Console,最后给一句总结。
///
/// 为什么不用 Unity Test Runner:项目的脚本全在 Assembly-CSharp(没有任何 .asmdef),
/// 而测试程序集是 asmdef、引用不到预定义程序集 —— 要跑 NUnit 就得先把整个项目的脚本搬进 asmdef,
/// 那会改变所有脚本的所属程序集、牵扯场景里的组件引用,风险远大于收益。所以用这个自检代替:
/// 它就在游戏里跑,验的是真场景 + 真结算,只是断言换成了日志。
///
/// 怎么用:进 Play → 按 selfTestKey(默认 F9)。也可以从代码里调 RunAll()。
/// **自检会真的改动战场**(部署、扣大营血、抽空牌堆),所以只在调试时按;想接着正常玩就重新载一次场景。
///
/// 【挂载 & 调整】
///   挂在:Battle.unity 的 BattleCanvas 上;不挂也行,GameBootstrap 会在战斗场景自动补一个。
///   引用:没有要手连的引用,全走单例。
///   常调:
///     · enableSelfTestKey(默认开):发布前关掉,免得玩家误按。
///     · selfTestKey:默认 F9(别用 T / N —— 那两个被调试键占了)。
///     · verbose:把每条断言的过程也打出来(排查失败原因时打开)。
/// </summary>
[DisallowMultipleComponent]
public class BattleSelfTest : MonoBehaviour
{
    private static BattleSelfTest instance;

    public static BattleSelfTest Instance
    {
        get
        {
            if (instance != null) return instance;
            instance = FindObjectOfType<BattleSelfTest>();
            if (instance != null) return instance;

            var go = new GameObject("BattleSelfTest");
            var canvas = CanvasUtil.FindRootCanvas();
            if (canvas != null) go.transform.SetParent(canvas.transform, false);
            instance = go.AddComponent<BattleSelfTest>();
            return instance;
        }
    }

    [Header("开关")]
    [Tooltip("按 selfTestKey 就跑一遍自检。发布前关掉,免得玩家误按")]
    [SerializeField] private bool enableSelfTestKey = true;
    [Tooltip("自检按键(默认 F9)。别用 T / N,那两个是回合与袭扰的调试键")]
    [SerializeField] private KeyCode selfTestKey = KeyCode.F9;

    [Header("输出")]
    [Tooltip("把每条断言的过程也打出来(排查失败原因时打开)")]
    [SerializeField] private bool verbose = false;

    private int passed;
    private int failed;
    private bool running;

    private void Awake() => instance = this;

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private void Update()
    {
        if (enableSelfTestKey && !running && Input.GetKeyDown(selfTestKey)) RunAll();
    }

    /// <summary>跑一遍全部自检。返回失败条数(0 = 全过)</summary>
    public int RunAll()
    {
        StopAllCoroutines();
        StartCoroutine(RunAllRoutine());
        return failed;
    }

    private IEnumerator RunAllRoutine()
    {
        running = true;
        passed = 0;
        failed = 0;

        Debug.Log("========== 战斗流程自检开始 ==========");

        var board = BattlefieldManager.Instance;
        Check(board != null, "战场管理器已就位", "找不到 BattlefieldManager");
        if (board == null) { Finish(); yield break; }

        // ---------- 1. 开局布置(§2.3) ----------
        Check(board.Rows.Count == 5, "5 条排", $"实际 {board.Rows.Count} 条");

        var playerCamp = board.FindBuilding(BattleSide.Player, "大营");
        var enemyCamp = board.FindBuilding(BattleSide.Enemy, "大营");
        Check(playerCamp != null, "我方大营已就位", "我方大营没摆上(检查 buildPrefab)");
        Check(enemyCamp != null, "敌方大营已就位", "敌方大营没摆上");
        Check(playerCamp != null && playerCamp.MaxHp == 25, "大营 25 HP", $"实际 {playerCamp?.MaxHp}");
        Check(board.FindBuilding(BattleSide.Player, "军械库") != null, "我方军械库已就位", "军械库没摆上");
        Check(board.FindBuilding(BattleSide.Player, "粮草") != null, "我方粮草已就位", "粮草没摆上");
        Check(board.PlayerMid.UnitCapacity == 5, "中军容量 5", $"实际 {board.PlayerMid.UnitCapacity}");
        Check(board.PlayerBack.UnitCapacity == 3, "后军容量 3", $"实际 {board.PlayerBack.UnitCapacity}");

        // ---------- 2. 回合系统与 AI(§2.4 / §6) ----------
        var turn = TurnController.Instance;
        Check(turn != null && turn.HasStarted, "回合已开始", "TurnController 没开始回合");
        Check(FindObjectOfType<EnemyAI>() != null, "敌方 AI 已装配", "找不到 EnemyAI");

        var cp = CommandPointController.Instance;
        Check(cp != null, "CP 控制器已就位", "找不到 CommandPointController");
        Check(cp != null && cp.CpMax >= 1, "第 1 回合 CP 上限 ≥ 1", $"实际 {cp?.CpMax}");

        // ---------- 3. 疲劳(§5.4.1)先验:它会改大营血,放在部署/攻击之前 ----------
        var deck = FindObjectOfType<DeckController>();
        if (deck != null && playerCamp != null)
        {
            int hpBefore = playerCamp.Hp;
            int fatigueBefore = deck.FatigueCount;
            deck.DrawCards(60);         // 抽干牌堆 → 连续触发疲劳

            int expected = 0;
            for (int i = fatigueBefore + 1; i <= deck.FatigueCount; i++) expected += BattleSettlement.FatigueDamageFor(i);

            Check(deck.FatigueCount > fatigueBefore, "牌堆抽空后疲劳计数递增", $"疲劳次数没涨: {fatigueBefore} → {deck.FatigueCount}");
            Check(playerCamp.Hp == Mathf.Max(0, hpBefore - expected),
                  $"疲劳伤害扣自己的大营（共 {expected} 点）", $"大营 HP {hpBefore} → {playerCamp.Hp},预期 {Mathf.Max(0, hpBefore - expected)}");
        }
        else
        {
            Check(false, "疲劳结算", "找不到 DeckController 或大营,跳过");
        }

        // ---------- 4. 部署与费用(§2.5) ----------
        if (cp != null)
        {
            cp.IncreaseCpMax(9);
            var card = FindMeleeUnitCard();
            Check(card != null, "卡池里能拿到兵牌", "找不到任何兵牌 .asset");

            if (card != null)
            {
                int cpBefore = cp.Cp;
                int unitsBefore = board.PlayerMid.UnitCount;

                bool ok = board.DeployUnit(card, board.PlayerMid, 1, out var deployed, out string reason);
                Check(ok, $"部署「{card.cardName}」成功", reason ?? "部署被拒");
                Check(deployed != null && board.PlayerMid.UnitCount == unitsBefore + 1, "单位登记进中军名单", "排的名单没变");
                Check(cp.Cp == cpBefore - card.deploymentCost, "部署费扣对", $"CP {cpBefore} → {cp.Cp},应扣 {card.deploymentCost}");

                if (deployed != null && !BattleRules.HasKeyword(card, Keyword.Blitz))
                    Check(!deployed.CanActNow, "刚部署的非闪击单位本回合不能行动", "刚部署就能动了(§2.5 反了)");
            }

            // ---------- 4b. 闪击:部署当回合必须**真的有行动力** ----------
            // 只判 BattleRules 不够:规则层只有"允不允许",真正的门槛是 Ap > 0。
            // 以前这里只放行规则、Init 却把 AP 写死 0,表现就是"闪击单位站着不动"。
            var blitzCard = FindBlitzUnitCard();
            if (blitzCard != null && board.PlayerMid.UnitCount < board.PlayerMid.UnitCapacity)
            {
                if (board.DeployUnit(blitzCard, board.PlayerMid, 1, out var blitzUnit, out string blitzReason))
                    Check(blitzUnit != null && blitzUnit.Ap > 0 && blitzUnit.CanActNow,
                          $"闪击「{blitzCard.cardName}」部署当回合能行动",
                          $"AP={blitzUnit?.Ap} —— 闪击只放行了规则,没给行动力");
                else
                    Check(false, "闪击单位能部署", blitzReason ?? "部署被拒");
            }
        }

        // ---------- 5. 移动 + 攻击 + 反击(§2.6 / §7.1) ----------
        var mine = SpawnForTest(board, BattleSide.Player);
        var his = SpawnForTest(board, BattleSide.Enemy);
        Check(mine != null && his != null, "能生成测试单位", "造不出测试单位");

        if (mine != null && his != null)
        {
            // ---- 共享前军是"抢"来的:只容得下一方的单位 ----
            bool hisToFront = board.MoveUnit(his, BattleRowType.Front, out string hisFrontReason);
            Check(hisToFront, "敌方单位能移动到共享前军", hisFrontReason ?? "移动被拒");

            if (hisToFront)
            {
                mine.GrantFullAp();
                int apBlocked = mine.Ap;
                bool walkedIn = board.MoveUnit(mine, BattleRowType.Front, out string blockedReason);
                Check(!walkedIn, "前军有敌方单位时我方进不去（前军只容一方）",
                      $"居然挤进去了（理由:{blockedReason ?? "无"}）");
                Check(mine.Ap == apBlocked, "被拒的进前军没有扣行动力", $"AP {apBlocked} → {mine.Ap}");

                // 把敌方那只挪走,前军一空出来我方就应该能进
                // (这是**测试摆位**,不是合法行动:它刚移动过,正常规则下本回合不能再移动)
                his.GrantFullAp();
                board.MoveUnit(his, BattleRowType.Mid, out _);
                Check(his.Row == null || his.Row.RowType == BattleRowType.Mid,
                      "敌方单位撤出前军（准备让位）", $"现在在 {his.Row?.DisplayName}");

                mine.GrantFullAp();
                bool moved1 = board.MoveUnit(mine, BattleRowType.Front, out string moveReason1);
                Check(moved1 && mine.Row != null && mine.Row.RowType == BattleRowType.Front,
                      "前军空出来后我方单位能进", moveReason1 ?? "移动被拒");

                // 对称的一条:现在前军是我方的,轮到敌方进不来了
                his.GrantFullAp();
                bool hisBack = board.MoveUnit(his, BattleRowType.Front, out string hisBackReason);
                Check(!hisBack, "前军有我方单位时敌方也进不来（规则对双方对称）",
                      $"敌方居然挤进了我方占据的前军（理由:{hisBackReason ?? "无"}）");

                // 两个人在同一条排上才验得了互相攻击 —— 把敌方那只直接用调试接口摆进前军
                // (移动这条路上面已经验过是堵死的,这里要的是"同排互相攻击"这个前提)
                his.Row?.RemoveUnit(his);
                his.SetRow(board.PlayerFront);
                board.PlayerFront.AddUnit(his, board.PlayerFront.MemberCount);
                his.GrantFullAp();
                Check(his.Row == board.PlayerFront, "把敌方单位摆回前军（准备验同排互攻）",
                      $"现在在 {his.Row?.DisplayName}");
            }
            else
            {
                Log("（敌方进前军就失败了,后面几条前军相关的检查会连带不准）");
            }

            // 前军不能退回己方后排(§2.3 只能向前):除袭扰骑兵外一律不许
            mine.GrantFullAp();
            int apNoRetreat = mine.Ap;
            bool retreated = board.MoveUnit(mine, BattleRowType.Back, out string retreatReason);
            Check(!retreated, "非袭扰单位不能退回己方后军",
                  $"居然退了回去（理由:{retreatReason ?? "无"}）");
            Check(mine.Ap == apNoRetreat, "被拒的后撤没有扣行动力", $"AP {apNoRetreat} → {mine.Ap}");
            Check(mine.Row != null && mine.Row.RowType == BattleRowType.Front,
                  "被拒后单位还留在前军", $"现在在 {mine.Row?.DisplayName}");

            // 同排原地:不算移动,必须被拒 —— 而且一个行动力一个 CP 都不能扣
            var cpForMove = CommandPointController.Instance;
            mine.GrantFullAp();
            int apSame = mine.Ap;
            int cpSame = cpForMove != null ? cpForMove.Cp : -1;

            bool sameRow = board.MoveUnit(mine, BattleRowType.Front, out string sameReason);
            Check(!sameRow, "移动到当前所在的排被拒（不算移动）", "居然「移动」了");
            Check(mine.Ap == apSame, "同排移动没有扣行动力", $"AP {apSame} → {mine.Ap}");
            if (cpForMove != null)
                Check(cpForMove.Cp == cpSame, "同排移动没有扣指挥点", $"CP {cpSame} → {cpForMove.Cp}");
            if (sameReason == null) Log("（同排移动的拒绝理由为空,应该给玩家一句说明）");

            Check(BattleRules.CanAttack(mine, his, out string canReason), "前军的近战单位能互相攻击", canReason ?? "不许攻击");

            int myAtk = mine.Atk, hisAtk = his.Atk;
            int myHpBefore = mine.Hp, hisHpBefore = his.Hp;

            bool attacked = BattleSettlement.Attack(mine, his, out string attackReason);
            Check(attacked, "攻击结算成功", attackReason ?? "结算被拒");

            int expected = Mathf.Max(0, myAtk - his.DamageReduction);
            Check(his.Hp == Mathf.Max(0, hisHpBefore - expected),
                  $"目标掉血正确（{expected} 点）", $"HP {hisHpBefore} → {his.Hp},预期 {Mathf.Max(0, hisHpBefore - expected)}");

            // 反击按"交换同时发生"算(§7.1.2 步骤 7):目标就算被这一下打死,它的反击伤害照样生效,
            // 所以这里**不再用 his.IsAlive 分情况** —— 那个分支在旧规则下永远不会走到,等于没测。
            if (BattleRules.Counters(mine, his) && hisAtk > 0)
            {
                int counter = Mathf.Max(0, hisAtk - mine.DamageReduction);
                Check(mine.Hp == Mathf.Max(0, myHpBefore - counter),
                      $"同类近战反击正确（{counter} 点）", $"HP {myHpBefore} → {mine.Hp},预期 {Mathf.Max(0, myHpBefore - counter)}");
            }
        }

        // ---------- 5b. 攻击扣费 / 缺费时整体不生效(§2.5:费用或行动力不满足都不能行动) ----------
        var cpCtrl = CommandPointController.Instance;
        if (cpCtrl != null && mine != null && his != null)
        {
            cpCtrl.IncreaseCpMax(20);
            cpCtrl.RefundCp(cpCtrl.CpMax);      // 先把当前 CP 补满:这一段验的是"扣费",不是"有没有钱"
            mine.GrantFullAp();

            int cpBefore = cpCtrl.Cp;
            int hisHpBefore = his.Hp;
            int hisMaxHp = his.MaxHp;
            int apBefore = mine.Ap;

            his.SetHp(hisMaxHp);        // 先补满,免得刚好这一下把它打死导致后面没法验

            bool ok = board.AttackUnit(mine, his, out string reason);
            Check(ok, "AttackUnit 结算成功", reason ?? "被拒");
            Check(his.Hp < hisMaxHp, "攻击确实扣了目标血量",
                  $"HP 还是 {his.Hp}/{hisMaxHp}（这就是「只扣费用没伤害」的现象）");
            Check(mine.Ap == apBefore - 1, "攻击扣 1 点行动力", $"AP {apBefore} → {mine.Ap}");
            Check(cpCtrl.Cp < cpBefore, "攻击扣了行动费用 CP", $"CP {cpBefore} → {cpCtrl.Cp}");

            // 把 CP 掏空 → 打不起:应该整体不生效,而不是扣掉什么再没下文
            int drain = cpCtrl.Cp;
            if (drain > 0) cpCtrl.TrySpend(drain);

            mine.GrantFullAp();
            int hpBefore2 = his.Hp;
            int apBefore2 = mine.Ap;

            bool ok2 = board.AttackUnit(mine, his, out string reason2);
            Check(!ok2, "指挥点不足时攻击被拒", "居然打出去了");
            Check(his.Hp == hpBefore2, "被拒的攻击没有造成任何伤害", $"HP {hpBefore2} → {his.Hp}");
            Check(mine.Ap == apBefore2, "被拒的攻击没有扣行动力", $"AP {apBefore2} → {mine.Ap}");

            // UI 的"能不能操作"也靠这一份只读判定,不能只拦结算、却让绿框照旧亮着
            bool affordable = board.CanAffordAction(mine, out string affordReason);
            Check(!affordable, "指挥点不足时 CanAffordAction 返回 false（绿框不会骗人）",
                  $"仍然报告付得起:{affordReason ?? "（无理由）"}");
            Log($"付不起的理由:{affordReason}");

            // ★ 最狠的一条:这一下**本来能打死人**。
            //   旧顺序是「先结算 → 再扣费」:目标先被 KillUnit 销毁(卡没了),扣费失败之后
            //   回滚只能对着一个已销毁对象写 HP —— 卡永远消失、伤害却已经生效。
            //   现在扣费在结算之前,这一下根本打不出去,目标必须原封不动。
            var doomed = board.SpawnUnitForDebug(FindMeleeUnitCard(), board.EnemyMid, BattleSide.Enemy, 0);
            if (doomed != null)
            {
                doomed.SetHp(1);        // 一下就能打死:旧代码会把它打没

                bool lethal = board.AttackUnit(mine, doomed, out string lethalReason);

                Check(!lethal, "指挥点不足时，连能打死人的攻击也打不出去", "居然打出去了");
                Check(doomed != null && doomed.IsAlive && doomed.Hp == 1,
                      "被拒的攻击没有把目标打死（卡还在场上）",
                      doomed == null ? "目标卡被这一下打没了" : $"目标 HP {doomed.Hp}（应为 1）,存活 {doomed.IsAlive}");

                if (doomed != null)
                {
                    doomed.Row?.RemoveUnit(doomed);
                    Destroy(doomed.gameObject);
                }
            }

            // 把 CP 还回去:后面几段(5c/5d)要验的是"方向/袭扰",别让它们因为没钱而以另一种理由被拒
            if (drain > 0) cpCtrl.RefundCp(drain);
        }

        // ---------- 5b2. 弓兵:付费之后结算不能再判 AP ----------
        // 坑是怎么来的:攻击改成「先付款 → 再结算」之后,付款已经把 AP 扣掉了;
        // 而 `BattleSettlement.Attack` 内部又复查了一次 `CanAttack`(它含 AP 判定)。
        // 于是 **1 AP 的兵(弓兵/步兵)** 付完费 AP = 0 → 被自己判成「行动力已耗尽」,
        // 表现为"弓兵明明在射程内,却说费用/行动力不足";骑兵 2 AP 付完还剩 1,看不出来。
        // 修法:结算层改用 `BattleRules.CanAttackTarget`(不含 AP/压制/刚部署)。
        {
            var archerCard = FindArcherCard();
            var archer = archerCard != null
                ? board.SpawnUnitForDebug(archerCard, board.PlayerFront, BattleSide.Player, 0)
                : null;
            var farTarget = archer != null
                ? board.SpawnUnitForDebug(FindMeleeUnitCard(), board.EnemyBack, BattleSide.Enemy, 0)
                : null;

            if (archer != null && farTarget != null)
            {
                cpCtrl?.RefundCp(cpCtrl.CpMax);      // 这一段要验的是"结算别判 AP",得先付得起
                archer.GrantFullAp();

                Check(archer.Ap == 1, "弓兵只有 1 点行动力（这个 bug 只在 1 AP 的单位上暴露）",
                      $"实际 AP {archer.Ap}/{archer.ApMax}");

                bool inRange = BattleRules.CanAttack(archer, farTarget, out string rangeReason);
                Check(inRange, "弓兵（射程 2）从前军能打到敌方后军的目标", rangeReason ?? "打不到");

                if (inRange)
                {
                    int apBefore = archer.Ap;
                    int hpBefore = farTarget.Hp;

                    bool ok = board.AttackUnit(archer, farTarget, out string attackReason);

                    Check(ok, "弓兵的攻击能正常结算（付完费后不会被误判「行动力已耗尽」）",
                          attackReason ?? "被拒");
                    Check(farTarget == null || farTarget.Hp < hpBefore, "弓兵的攻击确实造成了伤害",
                          farTarget == null ? "目标没了" : $"HP 还是 {farTarget.Hp}");
                    Check(archer.Ap == apBefore - 1, "弓兵的攻击扣了 1 点行动力",
                          $"AP {apBefore} → {archer.Ap}");
                }

                if (archer.Row != null) archer.Row.RemoveUnit(archer);
                Destroy(archer.gameObject);
                if (farTarget != null && farTarget.Row != null)
                {
                    farTarget.Row.RemoveUnit(farTarget);
                    Destroy(farTarget.gameObject);
                }
            }
        }

        // ---------- 5d. 方向硬闸门(§2.3:除袭扰撤回外,任何单位都不许朝己方后排走) ----------
        // 这一节直接拿 CanMoveTo 把"往后走"的每一种走法都试一遍,不看 UI、不看 AI ——
        // 只要有一处判反(共享前军的 row.Side 恒为 Player,最容易判反的就是这里),这里就会 ❌。
        if (mine != null)
        {
            Check(!board.MoveUnit(mine, BattleRowType.Back, out string backReason) || mine.Row?.RowType != BattleRowType.Back,
                  "非袭扰单位不能退回己方后军", "居然退回后军了");

            // 中军 → 后军:即便它现在就在中军,也不许往后退
            var midUnit = board.SpawnUnitForDebug(FindMeleeUnitCard(), board.PlayerMid, BattleSide.Player, 0);
            if (midUnit != null)
            {
                int apBefore = midUnit.Ap;
                bool moved = board.MoveUnit(midUnit, BattleRowType.Back, out string midBackReason);
                Check(!moved && midUnit.Row == board.PlayerMid,
                      "中军单位不能退回后军", $"居然退回去了（理由:{midBackReason ?? "无"}）");
                Check(!moved && midBackReason != null && midBackReason.Contains("不能退回"),
                      "退回后军被拒的理由是「方向」而不是别的（比如缺CP）",
                      $"拒绝理由不对:{midBackReason ?? "无"}");
                Check(midUnit.Ap == apBefore, "被拒的后退没有扣行动力", $"AP {apBefore} → {midUnit.Ap}");

                midUnit.Row?.RemoveUnit(midUnit);
                Destroy(midUnit.gameObject);
            }

            // 共享前军 → 己方中军:这在规则上是"往前走向敌方中军",不该落回自己的中军
            var frontUnit = board.SpawnUnitForDebug(FindMeleeUnitCard(), board.PlayerFront, BattleSide.Player, 0);
            if (frontUnit != null)
            {
                // 落点解析必须是"唯一权威":站在共享前军时,「中军」= 对面的中军。
                // 这里直接验 ResolveMoveRow —— UI 和 AI 都调它,以前三处各算一份,算错的就是这一条。
                var resolved = board.ResolveMoveRowTo(frontUnit, BattleRowType.Mid);
                Check(resolved == board.EnemyMid,
                      "站在共享前军时「中军」解析为敌方中军（ResolveMoveRowTo）",
                      $"解析成了 {resolved?.DisplayName ?? "null"}");

                board.MoveUnit(frontUnit, BattleRowType.Mid, out _);
                Check(frontUnit.Row != board.PlayerMid,
                      "站在前军时「中军」指的是对面中军,不会落回自己中军",
                      "落回己方中军了 —— 方向判反了");

                frontUnit.Row?.RemoveUnit(frontUnit);
                Destroy(frontUnit.gameObject);
            }

            Log($"（方向闸门自检结束:非袭扰单位连「退回后军」都进不去。理由示例:{backReason ?? "无"}）");
        }

        // ---------- 5e. 回归:方向判定必须按"单位自己的阵营"算 ----------
        // 这里连错过两次,两次都是同一个根:**方向不能从"排的 Side"推**。
        // 共享前军在全场只有一条(物理上就是 playerFront),它的 row.Side **恒为 Player**。
        //   错法① 用 RowOrderIndex 比"序号变大才算前进":序号是按某一方视角编的
        //         (己方中军 = 1、敌方中军 = 3),而 targetType(Mid/Back)不带阵营 ——
        //         敌方从自己的中军(3)回共享前军时,它的 Mid 被算成 2 < 3 → 合法前进被判成"向后走"。
        //   错法② TryForwardRow 内部用 from.Side 推方向:站在敌方中军(序号 3)时,
        //         它会算出"往前走一步 = 序号 4 = 敌方后军",于是合法前进同样被拒。
        // 现在 TryForwardRow 收 unit(按 unit.Side 算方向),判据只有这一条。
        // 现象就是「玩家前军还有兵的时候 AI 不攻击,反而一直试着移动然后一直被拒」,
        // 以及「我的前军单位死了之后 AI 满费不移动」。
        {
            var hisMidUnit = board.SpawnUnitForDebug(FindMeleeUnitCard(), board.EnemyMid, BattleSide.Enemy, 0);
            if (hisMidUnit != null)
            {
                // 把原始事实打出来:排类型、阵营、往回算的落点。
                // 这四条一直在日志里现成可见,就不用再靠猜"它到底站在哪条排"了。
                Log($"方向链原始数据:「{hisMidUnit.DisplayName}」side={hisMidUnit.Side} " +
                    $"row={hisMidUnit.Row?.DisplayName}({hisMidUnit.Row?.RowType}) row.Side={hisMidUnit.Row?.Side} " +
                    $"| 敌方视角排名 后军={BattleRules.RowRank(BattleSide.Enemy, BattleRowType.Back)} " +
                    $"中军={BattleRules.RowRank(BattleSide.Enemy, BattleRowType.Mid)} " +
                    $"前军={BattleRules.RowRank(BattleSide.Enemy, BattleRowType.Front)} " +
                    $"| from 敌方中军 往前走={ForwardOf(hisMidUnit, board.EnemyMid)}");

                // ---- 回归:共享前军对双方都必须是"前方相邻排"(§7.1.1) ----
                // 这里曾经是**绝对编号**:共享前军恒为 2,而敌方中军是 3 ——
                // 从敌方视角看前军跑到他中军后面去了,ForwardDistance(敌方中军, 共享前军) = -1,
                // 被 CanAttack 判成"目标在身后"。后果:**我方兵站在前军时,敌方近战永远打不到**,
                // 表现是"满费用但不攻击"+"超出射程 32 条"(32 = 全场上所有"敌方 → 我方前军"的组合)。
                // 现在 RowRank 是**按视角编号**的,双方视角里前军都是第 2 位。
                // ---- 全表回归:排距矩阵 ----
                // 这是射程/攻击方向的唯一真相源,一行一行写出来,别再让"视角"混进算术。
                // 纵轴 = 攻击者所在排,横轴 = 目标所在排;数值 = ForwardDistance(排距)。
                // 负数即"在身后,打不到"。正数 = 要隔几排(近战射程 1 只能吃 +1,远程射程 2 能吃 +1/+2)。
                //
                // ⚠ 共享前军那两格是**两个不同单位**在站:第 3 行是"我方单位站共享前军",
                //   第 3 列是"敌方单位站共享前军"。行/列的 side 指的是**站在那条排上的单位的阵营**,
                //   不是排自己的 Side —— 那条排是全场唯一的物理物体,row.Side 恒为 Player,
                //   照 row.Side 去填就会把敌人当成我方前军,这正是排距算错、敌人从桥上够到大营的老根。
                {
                    var rowOf = new System.Func<BattleSide, BattleRowType, BattleRow>((s, t) =>
                        t == BattleRowType.Front
                            ? board.PlayerFront                     // 共享前军:不分敌我,都是这一条
                            : s == BattleSide.Player
                                ? (t == BattleRowType.Back ? board.PlayerBack : board.PlayerMid)
                                : (t == BattleRowType.Back ? board.EnemyBack : board.EnemyMid));

                    int[,] expected =
                    {
                        // 目标:  敌方后军 敌方中军 共享前军(敌) 我方中军 我方后军
                        {         4,        3,       2,       1,       0 },   // 攻击者 = 我方后军
                        {         3,        2,       1,       0,      -1 },   // 攻击者 = 我方中军
                        {         2,        1,       0,      -1,      -2 },   // 攻击者 = 我方单位站共享前军
                        {        -1,        0,       1,       2,       3 },   // 攻击者 = 敌方中军(从敌方立场看,自己后军才是 0)
                        {         0,        1,       2,       3,       4 },   // 攻击者 = 敌方后军
                    };

                    var attackerRows = new[]
                    {
                        (BattleSide.Player, BattleRowType.Back), (BattleSide.Player, BattleRowType.Mid),
                        (BattleSide.Player, BattleRowType.Front), (BattleSide.Enemy, BattleRowType.Mid),
                        (BattleSide.Enemy, BattleRowType.Back),
                    };
                    var targetRows = new[]
                    {
                        (BattleSide.Enemy, BattleRowType.Back), (BattleSide.Enemy, BattleRowType.Mid),
                        (BattleSide.Enemy, BattleRowType.Front), (BattleSide.Player, BattleRowType.Mid),
                        (BattleSide.Player, BattleRowType.Back),
                    };

                    var wrong = new System.Text.StringBuilder();
                    for (int i = 0; i < attackerRows.Length; i++)
                    {
                        for (int j = 0; j < targetRows.Length; j++)
                        {
                            int got = BattleRules.ForwardDistance(attackerRows[i].Item1,
                                                                  rowOf(attackerRows[i].Item1, attackerRows[i].Item2),
                                                                  rowOf(targetRows[j].Item1, targetRows[j].Item2));
                            if (got != expected[i, j])
                            {
                                if (wrong.Length > 0) wrong.Append("、");
                                wrong.Append($"[{BattleRules.SideName(attackerRows[i].Item1)}{attackerRows[i].Item2}" +
                                             $"→{BattleRules.SideName(targetRows[j].Item1)}{targetRows[j].Item2}]" +
                                             $" 应为 {expected[i, j]} 实为 {got}");
                            }
                        }
                    }

                    Check(wrong.Length == 0,
                          "排距矩阵 25 格全对（共享前军按站在上面的单位算,对双方都是前方相邻排）",
                          wrong.ToString());
                }

                int enemyToFront = BattleRules.ForwardDistance(BattleSide.Enemy, board.EnemyMid, board.PlayerFront);
                Check(enemyToFront == 1,
                      "敌方中军 → 共享前军 的排距是 +1（前军在前方,不能是负数）",
                      $"算出 {enemyToFront} —— 负数意味着敌方永远打不到前军,前军就成了一堵单向的墙");

                int playerFromFrontToEnemyMid = BattleRules.ForwardDistance(BattleSide.Player, board.PlayerFront, board.EnemyMid);
                Check(playerFromFrontToEnemyMid == 1,
                      "我方单位站共享前军 → 敌方中军 的排距是 +1（按站在桥上的单位自己那方算,不是照排的 Side）",
                      $"算出 {playerFromFrontToEnemyMid} —— 照排的 Side 取号会算成 -1,桥上的人反而打不到对面中军");

                int enemyMidToMyMid = BattleRules.ForwardDistance(BattleSide.Enemy, board.EnemyMid, board.PlayerMid);
                Check(enemyMidToMyMid == 2,
                      "敌方中军 → 我方中军 的排距是 2（中间隔着整条共享前军：近战够不到大营,弓/器射程 2 刚好）",
                      $"算出 {enemyMidToMyMid} —— 算成 1 的话敌人站在自己中军就能直接够到我方中军和大营");

                // ---- 袭扰骑兵:我方单位站在**敌方中军**上 ----
                // 这是"骑兵冲进去之后拆不动后军建筑"的正面回归。攻击者是 Player 阵营,
                // 但物理上站在敌方中军那条排上:按**具体排**取号是第 3 位,敌方后军第 4 位 → 排距 1。
                // 曾经这里只拿 RowType 去查 RowRank(Player, Mid) = 1(自家中军),于是算出 4 - 1 = 3,
                // 近战射程 1 直接判「目标超出射程」—— 后军的军械库/粮草永远点不动。
                int raiderToHisBack = BattleRules.ForwardDistance(BattleSide.Player, board.EnemyMid, board.EnemyBack);
                Check(raiderToHisBack == 1,
                      "袭扰骑兵站敌方中军 → 敌方后军 的排距是 +1（射程 1 排正好够拆后军建筑）",
                      $"算出 {raiderToHisBack} —— 按 RowType 取号会算成 3,袭扰骑兵就永远打不动后军建筑");

                int playerToFront = BattleRules.ForwardDistance(BattleSide.Player, board.PlayerMid, board.PlayerFront);
                Check(playerToFront == 1, "我方中军 → 共享前军 的排距也是 +1（两边对称）",
                      $"算出 {playerToFront}");

                // ---- 共享前军的"视角"回归(排距算错会让敌人隔着一条前军就打到大营) ----
                // 那条排是全场唯一的物理物体、row.Side 恒为 Player。排距必须按**站在上面的单位**的阵营算;
                // 一旦照 row.Side 算,敌方单位站上去时它的排号会被读成我方前军的号(2),
                // 于是"敌方中军 → 我方中军"被算成 1,近战隔着前军就能拆大营。
                {
                    var enemyOnBridge = board.SpawnUnitForDebug(FindMeleeUnitCard(), board.PlayerFront, BattleSide.Enemy, 0);
                    var myCamp = BattleSettlement.FindCamp(BattleSide.Player);
                    if (enemyOnBridge != null && myCamp != null)
                    {
                        bool canHitCamp = BattleRules.CanAttack(enemyOnBridge, myCamp, out string why);
                        Check(canHitCamp,
                              "敌方单位站上共享前军后能打到我方大营（桥头被占=大营暴露）",
                              $"打不到:{why ?? "无理由"}");

                        if (enemyOnBridge.Row != null) enemyOnBridge.Row.RemoveUnit(enemyOnBridge);
                        Destroy(enemyOnBridge.gameObject);
                    }
                }

                // 前军上的近战必须能打到对面中军里的东西(这是"守住前军"的意义所在)
                var frontBlocker = board.SpawnUnitForDebug(FindMeleeUnitCard(), board.PlayerFront, BattleSide.Player, 0);
                var hisCamp = BattleSettlement.FindCamp(BattleSide.Enemy);
                if (frontBlocker != null && hisCamp != null)
                {
                    bool canHitCamp = BattleRules.CanAttack(frontBlocker, hisCamp, out string campWhy);
                    Check(canHitCamp,
                          "我方前军上的近战能打到敌方中军（前军不是单向的墙）",
                          $"打不到:{campWhy ?? "无理由"}");

                    if (frontBlocker.Row != null) frontBlocker.Row.RemoveUnit(frontBlocker);
                    Destroy(frontBlocker.gameObject);
                }

                // 逐个位置验"往前走一步到哪":这才是唯一的方向判据
                bool enemyChainOk =
                    BattleRules.TryForwardRow(hisMidUnit, board.EnemyBack, out _, out var fromBack) && fromBack == BattleRowType.Mid
                    && BattleRules.TryForwardRow(hisMidUnit, board.EnemyMid, out _, out var fromMid) && fromMid == BattleRowType.Front
                    && BattleRules.TryForwardRow(hisMidUnit, board.PlayerFront, out _, out var fromFront) && fromFront == BattleRowType.Mid;

                Check(enemyChainOk,
                      "敌方单位的前进链是 后军 → 中军 → 共享前军 → 我方中军（按它自己的阵营算）",
                      "某一级算错了 —— 方向很可能又是从排的 Side 推出来的");

                // 城破方向:敌人从共享前军往前走,应该是"我方中军",不是"敌方后军"
                if (BattleRules.TryForwardRow(hisMidUnit, board.PlayerFront, out var fSide, out var fType))
                    Check(fSide == BattleSide.Player && fType == BattleRowType.Mid,
                          "站在共享前军上往前走 = 我方中军（不能算成敌方后军）",
                          $"算成了 {fSide}/{fType}");

                var forward = board.ResolveMoveRowTo(hisMidUnit, BattleRowType.Front);
                Check(forward == board.PlayerFront,
                      "敌方中军单位往前一步解析到共享前军",
                      $"解析成了 {forward?.DisplayName ?? "null"}");

                int apBefore = hisMidUnit.Ap;
                bool moved = board.MoveUnit(hisMidUnit, BattleRowType.Front, out string hisReason);
                bool blockedByPlayer = hisReason != null && hisReason.Contains("还驻着");

                Check(moved || blockedByPlayer,
                      "敌方中军 → 共享前军 是合法前进（前军有我方兵时才因占位被拒）",
                      $"被当成非法方向拒掉了:{hisReason ?? "无"}");
                if (!moved) Check(hisMidUnit.Ap == apBefore, "因占位被拒的前进没有扣行动力",
                                  $"AP {apBefore} → {hisMidUnit.Ap}");

                // 反向:敌方中军 → 敌方后军 必须被拒(那才是真正的向后走)
                hisMidUnit.Row?.RemoveUnit(hisMidUnit);
                board.EnemyMid.AddUnit(hisMidUnit, board.EnemyMid.MemberCount);
                hisMidUnit.GrantFullAp();
                bool backIllegal = board.MoveUnit(hisMidUnit, BattleRowType.Back, out string backIllegalReason);
                Check(!backIllegal && hisMidUnit.Row == board.EnemyMid,
                      "敌方中军单位不能退回敌方后军（真正的向后走要拦住）",
                      $"居然退回去了（理由:{backIllegalReason ?? "无"}）");

                hisMidUnit.Row?.RemoveUnit(hisMidUnit);
                Destroy(hisMidUnit.gameObject);
            }
        }

        // ---------- 5c. 共享前军上的阵营(§2.3:前军是双方同一条排,阵营不能跟着 row.Side 走) ----------
        // 注意:正常对局里前军只容得下一方的单位,双方混站是**只有调试接口才造得出来**的状态。
        // 这里故意造出来,是为了直接验那个坑:阵营如果从 row.Side 推,共享前军上的敌方兵会被判成我方。
        {
            var card = FindMeleeUnitCard();
            var front = board.PlayerFront;
            var mineAtFront = card != null ? board.SpawnUnitForDebug(card, front, BattleSide.Player, 0) : null;
            var hisAtFront = card != null ? board.SpawnUnitForDebug(card, front, BattleSide.Enemy, 1) : null;

            Check(mineAtFront != null && hisAtFront != null, "调试接口能在共享前军上摆出双方的兵牌",
                  "造不出测试单位");

            if (mineAtFront != null && hisAtFront != null)
            {
                Check(mineAtFront.Side == BattleSide.Player && hisAtFront.Side == BattleSide.Enemy,
                      "摆在共享前军上的兵牌保持各自阵营",
                      $"我方单位阵营变成了 {mineAtFront.Side},敌方单位阵营是 {hisAtFront.Side}" +
                      "（前军 row.Side 恒为 Player,阵营不能从它推）");
                Check(BattleRules.CanAttack(mineAtFront, hisAtFront, out string crossReason),
                      "共享前军上敌我双方能互相攻击",
                      crossReason ?? "打不到（多半是阵营被判成同一方了）");
                Check(CardEffectResolver.UnitsOf(BattleSide.Enemy).Contains(hisAtFront),
                      "我方查询看不到共享前军里的敌方兵牌",
                      "UnitsOf 漏掉了站在共享前军里的敌人");

                // 这两张是"临时摆上来看一眼"的,收走 —— 不然会变成 AI 的额外目标,干扰后面那段
                foreach (var temp in new[] { mineAtFront, hisAtFront })
                {
                    if (temp == null) continue;
                    if (temp.Row != null) temp.Row.RemoveUnit(temp);
                    Destroy(temp.gameObject);
                }
            }
        }

        // ---------- 5b. 袭扰骑兵的进出(§7.4) ----------
        // 验两件事:①骑兵进敌方中军 = 袭扰;②撤回前军这条路在前军被敌方占着时也要堵住
        // (前军只容一方的单位,撤回同样受这条限制)。
        {
            var front = board.PlayerFront;
            ClearRowForTest(front);

            var cavalry = SpawnCavalryForTest(board);
            Check(cavalry != null, "卡池里能找到骑兵牌", "找不到任何 unitType = Cavalry 的兵牌");

            if (cavalry != null)
            {
                cavalry.GrantFullAp();
                board.MoveUnit(cavalry, BattleRowType.Front, out _);
                Check(cavalry.Row == front, "骑兵能先走到共享前军", $"现在在 {cavalry.Row?.DisplayName}");

                cavalry.GrantFullAp();
                bool raided = board.MoveUnit(cavalry, BattleRowType.Mid, out string raidReason);
                Check(raided && cavalry.Row == board.EnemyMid, "骑兵能进入敌方中军",
                      raidReason ?? "移动被拒");
                Check(cavalry.IsRaiding, "踏进敌方中军即进入袭扰状态", "IsRaiding 还是 false");

                // ★ 刚冲进去的那个回合不许撤回:袭扰的消耗(下回合 ATK+1 + 自损 2)还没结算过,
                //   放它回去等于白嫖一次进出 —— 骑兵就会在前军和敌方中军之间来回弹。
                cavalry.GrantFullAp();
                int apSameTurn = cavalry.Ap;
                bool bounce = board.MoveUnit(cavalry, BattleRowType.Front, out string bounceReason);
                Check(!bounce && cavalry.Row == board.EnemyMid,
                      "刚冲进敌方中军的骑兵本回合不能撤回（防来回弹）",
                      $"居然退了回去（理由:{bounceReason ?? "无"}）");
                Check(cavalry.Ap == apSameTurn, "被拒的撤回没有扣行动力", $"AP {apSameTurn} → {cavalry.Ap}");

                // 往后推一个回合,它才该有撤回的资格
                AdvanceRoundForTest();

                // 前军摆上敌方单位 → 撤不回去
                var blocker = board.SpawnUnitForDebug(FindMeleeUnitCard(), front, BattleSide.Enemy, 0);
                cavalry.GrantFullAp();
                int apRetreat = cavalry.Ap;
                bool retreatBlocked = board.MoveUnit(cavalry, BattleRowType.Front, out string blockedReason);
                Check(!retreatBlocked && cavalry.Row == board.EnemyMid,
                      "前军被敌方占着时袭扰骑兵撤回失败",
                      $"居然撤回成功（理由:{blockedReason ?? "无"}）");
                Check(cavalry.Ap == apRetreat, "被拒的撤回没有扣行动力", $"AP {apRetreat} → {cavalry.Ap}");

                // 把占位的挪走 → 撤回应该成功,而且袭扰状态要解除
                if (blocker != null)
                {
                    blocker.Row?.RemoveUnit(blocker);
                    Destroy(blocker.gameObject);
                }

                cavalry.GrantFullAp();
                bool retreated = board.MoveUnit(cavalry, BattleRowType.Front, out string retreatReason2);
                Check(retreated && cavalry.Row == front, "前军空出来后袭扰骑兵能撤回",
                      retreatReason2 ?? "移动被拒");
                Check(!cavalry.IsRaiding, "撤回后袭扰状态解除", "IsRaiding 还是 true");

                if (cavalry.Row != null) cavalry.Row.RemoveUnit(cavalry);
                Destroy(cavalry.gameObject);
            }
        }

        // ---------- 5c. 鼠标拖动那条路:直接把"指针底下那条排"传给 MoveUnit ----------
        // 拖动**不能**折成 BattleRowType(中军有两条、RowType 都是 Mid),所以走的是另一个重载。
        // 这里验的是"传具体排"时,方向闸门仍然关得住跨阵营、跨两排的错位 ——
        // 这两种输入只有鼠标拖动会产生(键盘 / AI 给的是 RowType,落点是 ResolveMoveRowTo 推的)。
        {
            var front = board.PlayerFront;
            ClearRowForTest(front);

            // ① 骑兵站共享前军,拖到敌方中军的空处 = 袭扰
            var cavalry = SpawnCavalryForTest(board);
            if (cavalry != null && board.EnemyMid != null && !board.EnemyMid.IsUnitCapacityFull)
            {
                cavalry.GrantFullAp();
                board.MoveUnit(cavalry, BattleRowType.Front, out _);

                cavalry.GrantFullAp();
                int apBefore = cavalry.Ap;
                bool raided = board.MoveUnit(cavalry, board.EnemyMid, 0, out string raidReason);
                Check(raided && cavalry.Row == board.EnemyMid,
                      "拖动:骑兵拖到敌方中军能袭扰",
                      raidReason ?? "移动被拒");
                Check(cavalry.IsRaiding, "拖动进敌方中军也挂上袭扰状态", "IsRaiding 还是 false");
                Check(cavalry.Ap < apBefore, "袭扰照常扣行动力", $"AP {apBefore} → {cavalry.Ap}");
                Check(board.EnemyMid.IndexOf(cavalry) == 0,
                      "拖动落位:slotIndex 生效(指定插在链首)",
                      $"实际下标 {board.EnemyMid.IndexOf(cavalry)}");

                if (cavalry.Row != null) cavalry.Row.RemoveUnit(cavalry);
                Destroy(cavalry.gameObject);
            }

            // ② 站共享前军的兵拖到**自己的**中军 = 后退,必须被拒(前军是单向的)
            var grunt = board.SpawnUnitForDebug(FindMeleeUnitCard(), front, BattleSide.Player, 0);
            if (grunt != null)
            {
                grunt.GrantFullAp();
                int apGrunt = grunt.Ap;
                bool wrongWay = board.MoveUnit(grunt, board.PlayerMid, 0, out string wrongReason);
                Check(!wrongWay && grunt.Row == front,
                      "拖动:站前军的兵拖到自己中军被拒(前军进去了就回不来)",
                      $"居然过去了(理由:{wrongReason ?? "无"})");
                Check(grunt.Ap == apGrunt, "被拒的拖动没有扣行动力", $"AP {apGrunt} → {grunt.Ap}");

                grunt.Row?.RemoveUnit(grunt);
                Destroy(grunt.gameObject);
            }

            // ③ 后军的兵直接拖到**敌方**中军 = 一次跨两条排,必须被拒
            var backUnit = board.SpawnUnitForDebug(FindMeleeUnitCard(), board.PlayerBack, BattleSide.Player, 0);
            if (backUnit != null)
            {
                backUnit.GrantFullAp();
                bool jumped = board.MoveUnit(backUnit, board.EnemyMid, 0, out string jumpReason);
                Check(!jumped && backUnit.Row == board.PlayerBack,
                      "拖动:后军的兵拖到敌方中军被拒(不能一次跨两条排)",
                      $"居然过去了(理由:{jumpReason ?? "无"})");

                backUnit.Row?.RemoveUnit(backUnit);
                Destroy(backUnit.gameObject);
            }
        }

        // ---------- 5d. 袭扰骑兵在敌方中军能做什么(§7.4):拆后军建筑 + 撤回前军 ----------
        // 这两条是玩家最常报"做不到"的两件事,而且都不走"往前一步"那条路:
        //   · 攻击:它站在敌方中军,目标是**再往前一排**的敌方后军建筑(射程 1 排刚好够);
        //   · 撤回:唯一允许反向的一步,而且**必须是隔了一个回合之后**才放开(袭扰代价要先结算)。
        // 所以这里单独把两件事在真场景里跑一遍 —— 规则层任何一处方向/距离算错,这里都会红。
        //
        // ⚠ 这里曾经整段都是绿的、游戏里却打不动后军建筑:因为当时排距对"攻击者那条排"只按 RowType 取号,
        //   站在敌方中军的骑兵被当成站在自家中军(第 1 位),敌方后军(第 4 位)算出排距 3 —— 测试也跟着一起错。
        //   现在矩阵与这里的断言都按"具体那条排"取号,再错就会红。
        {
            var front = board.PlayerFront;
            ClearRowForTest(front);

            // 敌方后军建筑(军械库 / 粮草;大营在中军,袭扰骑兵打不到它)
            var raidTarget = board.FindBuilding(BattleSide.Enemy, BattleRules.GranaryName)
                             ?? board.FindBuilding(BattleSide.Enemy, BattleRules.ArsenalName);

            var raider = SpawnCavalryForTest(board);
            Check(raider != null, "卡池里能找到骑兵牌(袭扰自检)", "找不到任何 unitType = Cavalry 的兵牌");

            if (raider != null && board.EnemyMid != null && raidTarget != null)
            {
                raider.GrantFullAp();
                board.MoveUnit(raider, BattleRowType.Front, out _);

                raider.GrantFullAp();
                bool raided = board.MoveUnit(raider, board.EnemyMid, 0, out string raidReason);
                Check(raided && raider.Row == board.EnemyMid && raider.IsRaiding,
                      "袭扰自检:骑兵先冲进敌方中军", raidReason ?? $"现在在 {raider.Row?.DisplayName}");

                if (raided)
                {
                    // ---- ① 站在敌方中军的袭扰骑兵,能拆敌方后军建筑 ----
                    Check(BattleRules.AttackRowOf(raider) == board.EnemyMid,
                          "袭扰骑兵的攻击起点就是它物理上站着的敌方中军",
                          $"AttackRowOf 给的是 {BattleRules.AttackRowOf(raider)?.DisplayName}");

                    int raidDistance = BattleRules.ForwardDistance(raider.Side, BattleRules.AttackRowOf(raider), raidTarget.Row);
                    Check(raidDistance == 1, "袭扰骑兵到敌方后军建筑的排距是 1（射程 1 排够得到）",
                          $"实际排距 {raidDistance} —— 按 RowType 取号会算成 3,后军建筑就永远打不动");

                    raider.GrantFullAp();
                    bool canHitBack = BattleRules.CanAttack(raider, raidTarget, out string raidHitReason);
                    Check(canHitBack, $"袭扰骑兵能攻击敌方后军建筑「{raidTarget.DisplayName}」", raidHitReason ?? "打不到");

                    if (canHitBack)
                    {
                        int hpBefore = raidTarget.Hp;
                        bool dealt = BattleSettlement.Attack(raider, raidTarget, out string raidSettleReason);
                        Check(dealt, "袭扰骑兵拆建筑的结算成功", raidSettleReason ?? "结算被拒");
                        Check(raidTarget.Hp < hpBefore, "敌方后军建筑掉了血",
                              $"HP {hpBefore} → {raidTarget.Hp}（攻击没落到建筑上）");
                    }

                    // ---- ①b 边界:袭扰骑兵只拆**建筑**,后军里的兵和大营都不许打(§7.4) ----
                    // 这条常被当成 bug 报上来("后军的单位也打不动"),记在这里当预期行为。
                    raider.GrantFullAp();       // 上面拆建筑可能已经把 AP 花掉了,边界检查要能走到射程那一条
                    var hisCamp = BattleSettlement.FindCamp(BattleSide.Enemy);
                    if (hisCamp != null)
                    {
                        Check(!BattleRules.CanAttack(raider, hisCamp, out string campReason),
                              "袭扰骑兵打不到敌方大营（大营在中军,不是后军建筑）",
                              "居然能打大营 —— 袭扰限制没生效");
                        Log($"打大营被拒的理由:{campReason}");
                    }

                    if (board.EnemyBack != null && !board.EnemyBack.IsUnitCapacityFull)
                    {
                        var hisBackUnit = board.SpawnUnitForDebug(FindMeleeUnitCard(), board.EnemyBack, BattleSide.Enemy, 0);
                        if (hisBackUnit != null)
                        {
                            Check(!BattleRules.CanAttack(raider, hisBackUnit, out string backUnitReason),
                                  "袭扰骑兵打不到敌方后军里的兵（袭扰只能拆建筑）",
                                  "居然能打后军的单位 —— §7.4 的「不能攻击任何单位」没生效");
                            Log($"打后军单位被拒的理由:{backUnitReason}");

                            hisBackUnit.Row?.RemoveUnit(hisBackUnit);
                            Destroy(hisBackUnit.gameObject);
                        }
                    }

                    // ---- ② 隔一个回合之后,袭扰骑兵能从敌方中军撤回共享前军 ----
                    // 先验"刚冲进去的那个回合不许撤回"的另一面:推一格之后必须放行。
                    AdvanceRoundForTest();
                    for (int i = 0; i < 2; i++) raider.GrantFullAp();   // 撤回要 AP,而上面拆建筑已经花掉一次了

                    Check(BattleRules.CanMoveTo(raider, raider.Row, BattleRowType.Front, front,
                                                out string retreatReason, out bool isRetreat) && isRetreat,
                          "袭扰骑兵隔一个回合后能撤回共享前军(判定放行,且标成后撤)",
                          retreatReason ?? "判定没放行");

                    int apBeforeRetreat = raider.Ap;
                    bool retreat = board.MoveUnit(raider, BattleRowType.Front, out string retreatMoveReason);
                    Check(retreat && raider.Row == front,
                          "袭扰骑兵真的撤回共享前军了", retreatMoveReason ?? $"现在在 {raider.Row?.DisplayName}");
                    Check(!raider.IsRaiding, "撤回后袭扰状态解除", "IsRaiding 还是 true");
                    Check(raider.Ap < apBeforeRetreat, "撤回照常扣行动力", $"AP {apBeforeRetreat} → {raider.Ap}");

                    // 撤回之后它只是普通骑兵:再打那个后军建筑应该够不着了(隔着敌方中军)
                    Check(!BattleRules.CanAttack(raider, raidTarget, out _),
                          "撤回后不再是袭扰状态,够不到敌方后军建筑了",
                          "撤回后仍然能打后军建筑（袭扰限制没被清掉）");
                }

                // 自检把敌方后军建筑打残/打毁了(5 HP,骑兵一两下就能拆掉):修回去 ——
                // 后面的 6 / 6c / 7 段还要用这座建筑,而且"被摧毁"会真的给它主人挂 Debuff。
                RepairForTest(raidTarget);

                raider.Row?.RemoveUnit(raider);
                Destroy(raider.gameObject);
            }
            else
            {
                Check(false, "袭扰骑兵进出敌方中军自检", "造不出骑兵,或找不到敌方后军建筑 / 敌方中军");
            }
        }

        // ---------- 6. 建筑血量绑定(护甲不减免、角标文本要跟着变) ----------
        var enemyBuilding = board.FindBuilding(BattleSide.Enemy, BattleRules.GranaryName)
                            ?? board.FindBuilding(BattleSide.Enemy, BattleRules.ArsenalName)
                            ?? BattleSettlement.FindCamp(BattleSide.Enemy);

        if (enemyBuilding != null)
        {
            var attacker = SpawnForTest(board, BattleSide.Player);
            if (attacker != null && MoveNextTo(attacker, board, enemyBuilding))
            {
                int buildingHpBefore = enemyBuilding.Hp;
                bool canHit = BattleRules.CanAttack(attacker, enemyBuilding, out string buildingReason);
                Check(canHit, $"能攻击敌方建筑「{enemyBuilding.DisplayName}」", buildingReason ?? "打不到");

                if (canHit)
                {
                    int expected = Mathf.Max(0, attacker.Atk);   // 建筑没有重甲,原样吃满
                    bool attacked = BattleSettlement.Attack(attacker, enemyBuilding, out string attackReason);

                    Check(attacked, "建筑受击结算成功", attackReason ?? "结算被拒");
                    Check(enemyBuilding.Hp == Mathf.Max(0, buildingHpBefore - expected),
                          $"建筑血量按伤害扣（{expected} 点）",
                          $"HP {buildingHpBefore} → {enemyBuilding.Hp},预期 {Mathf.Max(0, buildingHpBefore - expected)}");
                    Check(enemyBuilding.DisplayedHpText == enemyBuilding.Hp.ToString(),
                          "建筑角标上的血量数字跟着刷新",
                          $"角标写着「{enemyBuilding.DisplayedHpText ?? "（找不到那个 TMP 文本）"}」,实际 HP {enemyBuilding.Hp}" +
                          "（检查 Build.prefab 里那个文本的名字是否还叫「Text (TMP)」）");
                    Check(!BattleRules.Counters(attacker, enemyBuilding), "建筑不反击", "建筑反击了,§2.3 不允许");
                }
            }
            else
            {
                Check(false, "建筑受击检查", "造不出测试单位,或没法把单位摆到建筑旁边");
            }

            // 这一刀可能把 5 HP 的后军建筑打空(=真被摧毁、真挂 Debuff):
            // 修回去再往下走,免得 6b 的 AI 检查拿到一个被削了 CP / ATK 的对手。
            RepairForTest(enemyBuilding);
        }
        else
        {
            Check(false, "建筑受击检查", "找不到敌方建筑");
        }

        // ---------- 6b. AI 会不会进攻(§6.2:中军满了要会占前军、会打大营) ----------
        var ai = EnemyAI.Instance;
        if (ai != null)
        {
            // 1) 一排敌方兵站到中军:AI 应该想得到"往前顶"这一步,而不是原地不动
            var aiUnits = new List<FieldUnit>();
            for (int i = 0; i < 2; i++)
            {
                var unit = SpawnForTest(board, BattleSide.Enemy);
                if (unit != null) aiUnits.Add(unit);
            }

            foreach (var unit in aiUnits)
                unit.GrantFullAp();

            int movable = 0;
            foreach (var unit in aiUnits)
                if (BattleRules.CanMoveTo(unit, unit.Row, BattleRowType.Front, board.PlayerFront, out _, out _))
                    movable++;

            Check(movable > 0, "中军的敌方兵牌存在合法的「进前军」行动",
                  "一个都进不了前军（前军容量或规则有问题,AI 想进攻也没路）");

            // 2) 玩家在前军摆一个兵:AI 应该给出一次分数够格的行动(攻击或推上去打)
            var bait = SpawnForTest(board, BattleSide.Player);
            if (bait != null)
            {
                board.MoveUnit(bait, BattleRowType.Front, out _);
                bait.GrantFullAp();

                var decision = ai.PeekBestAction();
                Check(decision.hasAction && decision.meetsThreshold,
                      "AI 面对前军的敌方单位会做出行动（不是空过）",
                      decision.hasAction ? $"最优行动只有 {decision.score} 分,低于阈值 {decision}" : "AI 一个候选行动都没有");

                Log($"AI 当前最优行动:{decision}");
            }

            // 3) "能打就必须打":场上有人能打到,AI 的最优解不能是"再铺一张牌"
            //    (先把刚出的兵收掉,免得它们把敌方中军占满,新兵就没地方出、这条检查会假通过)
            foreach (var unit in aiUnits)
                if (unit != null) { if (unit.Row != null) unit.Row.RemoveUnit(unit); Destroy(unit.gameObject); }
            aiUnits.Clear();

            var shooter = SpawnForTest(board, BattleSide.Enemy);
            var victim = SpawnForTest(board, BattleSide.Player);

            if (shooter != null && victim != null && bait != null)
            {
                // 诱饵残血 + 旁边还有一个兵 → 攻击能"白吃一个",这是 AI 眼里性价比最高的行动
                bait.SetHp(1);

                shooter.GrantFullAp();
                victim.SetHp(victim.MaxHp);
                board.MoveUnit(victim, BattleRowType.Front, out _);
                victim.GrantFullAp();

                bool canHit = BattleRules.CanAttack(shooter, bait, out string hitReason)
                              || BattleRules.CanAttack(shooter, victim, out string hitReason2);

                if (canHit)
                {
                    var offence = ai.PeekBestAction();
                    Check(offence.kind == "Attack",
                          "场上有能打的目标时,AI 的最优解是攻击而不是出牌",
                          $"AI 选了 {offence.kind}:{offence.describe}（{offence.score} 分）");
                    Log($"AI 在能出手时的选择:{offence}");

                    // 设计定的优先级是 部署 > 策略 > 移动 > 攻击 > 空过 —— 攻击排在"空过"前面,
                    // 所以只要打得到、又付得起,分数必须是正的(空过 = 白扔一次行动力)。
                    // 以前"打不动"是 −50、"磨血"是 −100,这些攻击全被阈值滤掉,
                    // 表现就是"有费、有单位能行动,AI 却什么都不做"。
                    Check(offence.score > 0,
                          "任何一次合法攻击的分值都是正的（攻击优先级高于空过）",
                          $"攻击只有 {offence.score} 分,会被当成空过");
                }
                else
                {
                    Log($"（射击位摆不出来,跳过「能打就必须打」这条检查:{hitReason}）");
                }
            }

            // 把这几张牌收走,别影响后面的回合/胜负检查
            foreach (var unit in aiUnits)
                if (unit != null) { if (unit.Row != null) unit.Row.RemoveUnit(unit); Destroy(unit.gameObject); }
            foreach (var unit in new[] { shooter, victim, bait })
                if (unit != null) { if (unit.Row != null) unit.Row.RemoveUnit(unit); Destroy(unit.gameObject); }
        }
        else
        {
            Check(false, "AI 进攻能力检查", "战场上没有 EnemyAI 组件");
        }

        // ---------- 6c. 建筑被毁的 Debuff 落在**建筑主人**身上(§2.3) ----------
        // 这里曾经是最荒唐的一条:玩家拆掉敌方粮草/军械库,挨罚的却是自己 ——
        // CP 上限 -2、全场兵牌 ATK -1,等于"打得越好越挨罚"。
        // 受害方必须是 `building.Side`;而且判"哪些兵牌吃这一刀"只能看单位自己的 Side,
        // 不能看 `row.Side` —— 共享前军的 row.Side 恒为 Player,袭扰骑兵更是站在对面中军上。
        {
            var cpForDebuff = CommandPointController.Instance;
            var enemyDeck = EnemyDeckController.Instance;

            // 先把四座建筑恢复成"满血、非废墟":前面的段落可能已经把某座打残了。
            // 只有原本就是废墟的才走"修回来"那条(会撤 Debuff),不然会凭空多还一份上限/攻击力。
            RepairForTest(board.FindBuilding(BattleSide.Player, BattleRules.GranaryName));
            RepairForTest(board.FindBuilding(BattleSide.Enemy, BattleRules.GranaryName));
            RepairForTest(board.FindBuilding(BattleSide.Player, BattleRules.ArsenalName));
            RepairForTest(board.FindBuilding(BattleSide.Enemy, BattleRules.ArsenalName));

            var myGranary = board.FindBuilding(BattleSide.Player, BattleRules.GranaryName);
            var hisGranary = board.FindBuilding(BattleSide.Enemy, BattleRules.GranaryName);
            var myArsenal = board.FindBuilding(BattleSide.Player, BattleRules.ArsenalName);
            var hisArsenal = board.FindBuilding(BattleSide.Enemy, BattleRules.ArsenalName);

            if (cpForDebuff != null && enemyDeck != null && myGranary != null && hisGranary != null)
            {
                int myCpBefore = cpForDebuff.CpMax;
                int hisCpBefore = enemyDeck.CpMax;

                // ① 拆敌方粮草:只有敌方上限降,我方一分不动(用户报的就是这一条)
                DestroyForTest(hisGranary);
                Check(cpForDebuff.CpMax == myCpBefore,
                      "拆掉敌方粮草,我方 CP 上限不受影响",
                      $"我方上限 {myCpBefore} → {cpForDebuff.CpMax} —— Debuff 挂到了拆建筑的那一方身上");
                Check(enemyDeck.CpMax == hisCpBefore - 2,
                      "拆掉敌方粮草,敌方 CP 上限 -2",
                      $"敌方上限 {hisCpBefore} → {enemyDeck.CpMax},预期 {hisCpBefore - 2}");

                RepairForTest(hisGranary);
                Check(enemyDeck.CpMax == hisCpBefore, "修好敌方粮草,敌方 CP 上限还原",
                      $"敌方上限 {enemyDeck.CpMax},预期 {hisCpBefore}");

                // ② 拆我方粮草:这回该我方降(双方对称,不是"只看玩家")
                DestroyForTest(myGranary);
                Check(cpForDebuff.CpMax == myCpBefore - 2,
                      "我方粮草被毁,我方 CP 上限 -2",
                      $"我方上限 {myCpBefore} → {cpForDebuff.CpMax},预期 {myCpBefore - 2}");

                RepairForTest(myGranary);
                Check(cpForDebuff.CpMax == myCpBefore, "修好我方粮草,我方 CP 上限还原",
                      $"我方上限 {cpForDebuff.CpMax},预期 {myCpBefore}");
            }
            else
            {
                Check(false, "建筑 Debuff 自检（粮草）", "找不到 CP 控制器或双方的粮草");
            }

            // ③ 军械库:用两个 ATK > 0 的探针单位量增量。
            //    敌方探针故意摆在**我方中军**上(袭扰骑兵的位置):照 row.Side 判的话它会被
            //    我方军械库的 -1 一起扣掉,而真正该扣的我方兵反而漏掉。
            if (myArsenal != null && hisArsenal != null)
            {
                // 先把可能残留的惩罚清干净,增量才可预期(军械库惩罚只叠一层,残留会让 Add 直接返回)
                board.RemoveArsenalPenalty(BattleSide.Player);
                board.RemoveArsenalPenalty(BattleSide.Enemy);

                var probeMyUnit = board.SpawnUnitForDebug(FindMeleeUnitCard(), board.PlayerMid, BattleSide.Player, 0);
                var probeHisUnit = board.SpawnUnitForDebug(FindMeleeUnitCard(), board.PlayerMid, BattleSide.Enemy, 0);

                if (probeMyUnit != null && probeHisUnit != null && probeMyUnit.Atk > 0 && probeHisUnit.Atk > 0)
                {
                    int myAtkBefore = probeMyUnit.Atk;
                    int hisAtkBefore = probeHisUnit.Atk;

                    // 拆敌方军械库 → 只有敌方掉
                    DestroyForTest(hisArsenal);
                    Check(probeMyUnit.Atk == myAtkBefore,
                          "拆掉敌方军械库,我方兵牌攻击力不变",
                          $"我方 {myAtkBefore} → {probeMyUnit.Atk} —— Debuff 挂到了拆建筑的那一方身上");
                    Check(probeHisUnit.Atk == hisAtkBefore - 1,
                          "拆掉敌方军械库,敌方兵牌 ATK -1",
                          $"敌方 {hisAtkBefore} → {probeHisUnit.Atk},预期 {hisAtkBefore - 1}");

                    RepairForTest(hisArsenal);
                    Check(probeHisUnit.Atk == hisAtkBefore, "修好敌方军械库,敌方攻击力还原",
                          $"敌方 {probeHisUnit.Atk},预期 {hisAtkBefore}");

                    // 拆我方军械库 → 只有我方掉;摆在我方中军的敌方探针不该被扣
                    DestroyForTest(myArsenal);
                    Check(probeMyUnit.Atk == myAtkBefore - 1,
                          "我方军械库被毁,我方兵牌 ATK -1",
                          $"我方 {myAtkBefore} → {probeMyUnit.Atk},预期 {myAtkBefore - 1}");
                    Check(probeHisUnit.Atk == hisAtkBefore,
                          "我方军械库被毁不影响站在我方中军的敌方兵牌（判据是单位自己的 Side）",
                          $"敌方 {hisAtkBefore} → {probeHisUnit.Atk} —— 用排的阵营判定会把这一刀扣错人");

                    RepairForTest(myArsenal);
                    Check(probeMyUnit.Atk == myAtkBefore, "修好我方军械库,我方攻击力还原",
                          $"我方 {probeMyUnit.Atk},预期 {myAtkBefore}");
                }
                else
                {
                    Check(false, "建筑 Debuff 自检（军械库）", "造不出 ATK > 0 的探针单位");
                }

                // 收走探针,别让它们影响后面的胜负检查
                foreach (var probe in new[] { probeMyUnit, probeHisUnit })
                    if (probe != null) { if (probe.Row != null) probe.Row.RemoveUnit(probe); Destroy(probe.gameObject); }
            }
            else
            {
                Check(false, "建筑 Debuff 自检（军械库）", "找不到双方的军械库");
            }
        }

        // ---------- 7. 胜负与广播(§8.2) ----------
        var endedWith = new List<BattleSide>();     // 用 List 接:匿名方法不能给捕获的局部变量赋值
        System.Action<BattleSide, string> onEnded = (w, reason) => endedWith.Add(w);

        BattleSettlement.MatchEnded += onEnded;
        var camp = BattleSettlement.FindCamp(BattleSide.Player);
        if (camp != null)
        {
            BattleSettlement.DealCampDamage(BattleSide.Player, camp.Hp + 100, "自检:打空我方大营");
            Check(BattleSettlement.MatchOver, "大营被打空 → 对局结束", "MatchOver 还是 false");
            Check(endedWith.Count > 0, "结算事件已广播", "MatchEnded 没触发,结算界面收不到");
            Check(endedWith.Count > 0 && endedWith[0] == BattleSide.Enemy, "判敌方获胜",
                  endedWith.Count > 0 ? $"判给了 {endedWith[0]}" : "没有收到胜负");
            Check(!string.IsNullOrEmpty(BattleSettlement.ResultText), "战报文本已生成", "ResultText 是空的");
        }
        else
        {
            Check(false, "胜负判定", "找不到大营,跳过");
        }
        BattleSettlement.MatchEnded -= onEnded;

        Finish();

        yield return null;
        running = false;

        // 自检把对局打结束了:重新载一遍场景,免得留在"已结束"的状态里
        if (BattleSettlement.MatchOver)
        {
            Debug.Log("[自检] 自检改动了对局状态,重新载入场景以便继续正常游玩。");
            UnityEngine.SceneManagement.SceneManager.LoadScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
        }
    }

    // ================================================================ 断言与输出

    private void Check(bool condition, string what, string failure)
    {
        if (condition)
        {
            passed++;
            Debug.Log($"<color=#8fdc8f>✅ {what}</color>");
        }
        else
        {
            failed++;
            Debug.LogError($"❌ {what} —— {failure}");
        }
    }

    private void Log(string message)
    {
        if (verbose) Debug.Log($"[自检] {message}");
    }

    private void Finish()
    {
        string summary = failed == 0
            ? $"<color=#8fdc8f>========== 自检通过（{passed} 项） ==========</color>"
            : $"<color=#ff8a80>========== 自检结束：{passed} 项通过，{failed} 项失败 ==========</color>";

        if (failed == 0) Debug.Log(summary);
        else Debug.LogError(summary);

        running = false;
    }

    // ================================================================ 工具

    /// <summary>从卡池里挑一张近战兵牌(步/骑,射程 1 排)</summary>
    private static CardData FindMeleeUnitCard()
    {
        var all = Resources.FindObjectsOfTypeAll<CardData>();
        CardData fallback = null;

        for (int i = 0; i < all.Length; i++)
        {
            var card = all[i];
            if (card == null || !card.IsUnitCard) continue;

            if (fallback == null) fallback = card;
            if (BattleRules.IsMelee(card)) return card;
        }

        return fallback;
    }

    /// <summary>从卡池里挑一张带「闪击」的兵牌(§2.5 部署当回合就能行动)</summary>
    private static CardData FindBlitzUnitCard()
    {
        var all = Resources.FindObjectsOfTypeAll<CardData>();
        for (int i = 0; i < all.Length; i++)
        {
            var card = all[i];
            if (card != null && card.IsUnitCard && BattleRules.HasKeyword(card, Keyword.Blitz)) return card;
        }
        return null;
    }

    /// <summary>从卡池里挑一张骑兵牌(§7.4 袭扰只有骑兵能做)</summary>
    private static CardData FindCavalryCard()    {
        var all = Resources.FindObjectsOfTypeAll<CardData>();
        for (int i = 0; i < all.Length; i++)
        {
            var card = all[i];
            if (card != null && card.IsUnitCard && card.unitType == UnitType.Cavalry) return card;
        }
        return null;
    }

    /// <summary>
    /// 从卡池里挑一张**只有 1 AP** 的弓兵牌(射程 2)。
    /// 必须卡死 1 AP:那个"付费后结算又判 AP"的 bug 只在 1 AP 的单位上暴露
    /// (2 AP 的兵付完还剩 1 点,`CanAttack` 照样放行)。
    /// </summary>
    private static CardData FindArcherCard()
    {
        var all = Resources.FindObjectsOfTypeAll<CardData>();
        for (int i = 0; i < all.Length; i++)
        {
            var card = all[i];
            if (card == null || !card.IsUnitCard || card.unitType != UnitType.Archer) continue;
            if (BattleRules.ActionPointsOf(card) == 1) return card;
        }
        return null;
    }

    /// <summary>在共享前军摆一个我方骑兵(调试接口),专门用来验袭扰的进出</summary>
    private static FieldUnit SpawnCavalryForTest(BattlefieldManager board)
    {
        if (board == null) return null;

        var card = FindCavalryCard();
        if (card == null) return null;

        var unit = board.SpawnUnitForDebug(card, board.PlayerFront, BattleSide.Player, 0);
        unit?.GrantFullAp();
        return unit;
    }

    /// <summary>
    /// 把"从某条排往前走一步到哪条"打成一句可读文本(自检日志用)。
    /// 判方向出错时,这一句就是原始证据:它站在哪条排、RowType 是什么、以为下一站是哪条。
    /// </summary>
    private static string ForwardOf(FieldUnit unit, BattleRow from)
    {
        if (from == null) return "（这条排是 null）";

        return BattleRules.TryForwardRow(unit, from, out var fSide, out var fType)
            ? $"{fSide}/{fType}"
            : "到头了(TryForwardRow 返回 false)";
    }

    /// <summary>
    /// 把回合数往后推一格(自检专用)。
    /// 只动 TurnController 的轮次计数,不真走一个回合 —— 否则会触发敌方 AI、抽牌、疲劳这一整套流程,
    /// 自检就不可控了。用来验"隔了一个回合之后,某些事才被允许"(比如袭扰骑兵能不能撤回)。
    /// </summary>
    private static void AdvanceRoundForTest()
    {
        var turn = TurnController.Instance;
        if (turn == null) return;

        var field = typeof(TurnController).GetField("roundNumber",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null) return;

        int current = (int)field.GetValue(turn);
        field.SetValue(turn, current + 1);
    }

    /// <summary>把一条排清空(只清兵牌,建筑留着)—— 自检里腾地方用</summary>
    private static void ClearRowForTest(BattleRow row)    {
        if (row == null) return;

        var members = BattleRules.Snapshot(row);
        for (int i = 0; i < members.Count; i++)
        {
            var unit = members[i];
            if (unit == null || unit.IsBuilding) continue;
            row.RemoveUnit(unit);
            Destroy(unit.gameObject);
        }
    }

    /// <summary>
    /// 自检专用:把一座建筑打空(=被摧毁),走的是**真的**摧毁结算 `KillUnit → OnBuildingDestroyed`。
    /// 已经是废墟就直接跳过 —— 再打一次不会重复挂 Debuff,自检的增量断言会被带偏。
    /// </summary>
    private static void DestroyForTest(FieldUnit building)
    {
        if (building == null || !building.IsBuilding || !building.IsAlive) return;

        building.SetHp(0);
        BattleSettlement.KillUnit(building);
    }

    /// <summary>
    /// 自检专用:把一座建筑恢复成"满血、非废墟"。
    /// 只有**原本就是废墟**的才调 `OnBuildingRepaired` 撤 Debuff —— 对活着的建筑凭空补一份
    /// CP 上限 / 攻击力会让后面的增量断言失真。
    /// </summary>
    private static void RepairForTest(FieldUnit building)
    {
        if (building == null || !building.IsBuilding || building.IsCamp) return;

        bool wasWrecked = !building.IsAlive;
        if (wasWrecked) building.ClearWrecked();
        building.SetHp(building.MaxHp);
        if (wasWrecked) BattleSettlement.OnBuildingRepaired(building);
    }

    /// <summary>在指定方的中军摆一个测试单位(调试接口,不扣费),用来验移动与攻击</summary>
    private static FieldUnit SpawnForTest(BattlefieldManager board, BattleSide side)
    {
        if (board == null) return null;

        var card = FindMeleeUnitCard();
        if (card == null) return null;

        var row = side == BattleSide.Player ? board.PlayerMid : board.EnemyMid;
        return board.SpawnUnitForDebug(card, row, 0);
    }

    /// <summary>
    /// 把单位挪到"下一步就能打到那个目标"的排上(调试用,绕过规则直接换排)。
    /// 找法:把目标所在排的**前面一排**试出来 —— 近战(射程 1)要贴到相邻排,远程(射程 2)可以隔一排。
    /// </summary>
    private static bool MoveNextTo(FieldUnit unit, BattlefieldManager board, FieldUnit target)
    {
        if (unit == null || board == null || target == null || target.Row == null) return false;

        var rows = board.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null || row.Side != unit.Side) continue;    // 只能挪到自己的排上
            if (row.RowType == BattleRowType.Back) continue;       // 后军打不到任何东西

            var from = unit.Row;
            if (from != null) from.RemoveUnit(unit);

            unit.SetRow(row);
            row.AddUnit(unit, 0);

            if (BattleRules.CanAttack(unit, target, out _)) return true;

            row.RemoveUnit(unit);
            unit.SetRow(from);
            if (from != null) from.AddUnit(unit, 0);
        }

        return false;
    }
}
