using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>拖出去之后这手牌怎么了</summary>
public enum CardPlayResult
{
    /// <summary>拖回手牌区(不算打出去,也没失败)</summary>
    Cancelled,
    /// <summary>落点非法,已弹回手牌</summary>
    Failed,
    /// <summary>兵种牌落位成功</summary>
    Deployed,
    /// <summary>策略卡释放成功</summary>
    Released,
}

/// <summary>移动方向(§2.3 前后相邻)</summary>
public enum MoveDirection
{
    Forward,
    Backward,
}

/// <summary>
/// 战场总管。**现在只做接线、转发和必要的处理**,真正的活分成 8 个组件:
///
/// · <see cref="PrefabResolver"/>      预制体 / 场景引用解析(排、建筑、卡面、事件摄像机)
/// · <see cref="BattlefieldLayouts"/>  排布局、格子间距、按方向解析目标排
/// · <see cref="BuildingManager"/>     开局摆建筑、军械库被毁的 ATK -1 账
/// · <see cref="FieldHighlighter"/>    拖动高亮、落点竖条、敌手牌区提示
/// · <see cref="UnitDeployer"/>        单位/建筑生成与落位(所有 Instantiate)
/// · <see cref="CardPlayDirector"/>    出牌流程(玩家拖放与 AI 共用)
/// · <see cref="BattleActionExecutor"/> 移动 / 攻击 / 扣费执行
/// · <see cref="DebugTools"/>          调试开关
///
/// 这个类保留的东西只有三类,别往里加第四类:
///   1. MonoBehaviour 的生命周期(Awake / OnDestroy / OnDisable / Update)与场景接线;
///   2. 对外 API —— 外面(UnitActionController / EnemyAI / BattleRules / BattleSelfTest)
///      全是 board.Xxx(...) 这么调的,签名一个字都不能改;
///   3. 校验与转发 —— 参数该截的截一下,再交给对应组件,比如 ResolveInsertIndex 的 Clamp。
///
/// 设计约定(拆分时定的,后面跟着改):
///   · 8 个组件都是普通 C# 类,由本类持有;它们统一持有 <c>m</c> 字段回指总管,
///     靠 <c>internal</c> 成员拿到排、预制体这些共享状态。
///   · 全局没有 namespace,单程序集,所以 internal ≈ public;用它是为了**表明这些是新旧内部的**,
///     不是给外部模块用的公开契约。
/// </summary>
[DisallowMultipleComponent]
public class BattlefieldManager : MonoBehaviour
{
    /// <summary>场景里 5 条排的物体名,留空的字段按这些名字自动找</summary>
    internal const string PlayerBackName = "PlayerBack";
    internal const string PlayerMidName = "PlayerMid";
    internal const string PlayerFrontName = "Front";
    internal const string EnemyMidName = "EnemyMid";
    internal const string EnemyBackName = "EnemyBack";
    internal const string EnemyHandZoneName = "HandArea2";

    private static BattlefieldManager instance;

    /// <summary>场景里没有就自己建一个(挂在 RowsContainer 上),用到才建</summary>
    public static BattlefieldManager Instance
    {
        get
        {
            if (instance != null) return instance;
            instance = FindObjectOfType<BattlefieldManager>();
            if (instance != null) return instance;

            var host = PrefabResolver.FindRect("RowsContainer");
            if (host != null)
            {
                instance = host.gameObject.AddComponent<BattlefieldManager>();
            }
            else
            {
                var go = new GameObject("BattlefieldManager");
                var canvas = CanvasUtil.FindRootCanvas();
                if (canvas != null) go.transform.SetParent(canvas.transform, false);
                instance = go.AddComponent<BattlefieldManager>();
            }
            return instance;
        }
    }

    [Header("排(留空则运行时按名字找)")]
    [Tooltip("己方后军这条排。留空 → 按场景物体名 PlayerBack 自动找;场景里没有就 LogError,这条排落不了牌")]
    [SerializeField] internal BattleRow playerBack;
    [Tooltip("己方中军这条排,兵种牌的默认落点。留空 → 按名字 PlayerMid 自动找;它找不到会 LogError,兵种牌就彻底没地方落")]
    [SerializeField] internal BattleRow playerMid;
    [Tooltip("共享前军这条排。留空 → 按名字 Front 自动找。注意前军本来就只能靠移动进入、不能直接部署,填上只是为了让它一起参与拖动高亮")]
    [SerializeField] internal BattleRow playerFront;
    [Tooltip("敌方中军这条排(以整排为目标的策略卡落点)。留空 → 按名字 EnemyMid 自动找;找不到就没有这个目标")]
    [SerializeField] internal BattleRow enemyMid;
    [Tooltip("敌方后军这条排。留空 → 按名字 EnemyBack 自动找;找不到就没有这个目标")]
    [SerializeField] internal BattleRow enemyBack;

    [Header("敌方手牌区(弃置敌牌类策略卡的目标)")]
    [Tooltip("敌方手牌区的 RectTransform。留空 → 按名字 HandArea2 自动找;找不到时「弃置敌牌」类策略卡无处可落,拖动时也不会有绿框提示")]
    [SerializeField] internal RectTransform enemyHandZone;
    [Tooltip("拖动「弃置敌牌」类策略卡时,敌方手牌区染上的提示色(alpha 直接决定提示强度,原色在首次高亮时被缓存下来)")]
    [SerializeField] internal Color enemyHandZoneTint = new Color(0.35f, 1f, 0.55f, 0.45f);

    [Header("排布(策划案§2.3:行高固定 + 链上成员定距)")]
    [Tooltip("每排高度钉死成多少像素(初始帧 5 条排平摊后大约就是 144)")]
    [SerializeField] internal float rowHeight = 144f;
    [Tooltip("链上相邻两个成员之间留多少像素 —— 这段空隙是留给 buff 图标的")]
    [SerializeField] internal float memberGap = 20f;
    [Tooltip("要不要由本脚本把排高钉死(不勾就完全听场景里那两层的布局组)")]
    [SerializeField] private bool lockRowLayout = true;

    [Header("战场小卡")]
    [Tooltip("战场卡面的预制体(CardsInBattle.prefab:字号更大的精简卡面 —— 只有行动费用/攻血/兵种/朝代/插画,\n" +
             "没有卡名、关键词、效果和稀有度小方框)。留空 → 先借 HandUI 的战场卡引用,\n" +
             "还是没有就在编辑器里按路径自动认领 CardsInBattle.prefab;\n" +
             "连那份都认领不到才退回手牌用的 Card.prefab(字会小、还占着费用区和描述区)。\n" +
             "必须是 Project 里的资产,不要拖场景实例")]
    [SerializeField] internal CardDisplay fieldCardPrefab;
    [Tooltip("部署进排里的兵牌缩到多大(1 = 和手牌一样大)。卡面内容整体等比缩放:\n" +
             "只改 rect 不会放大字号和插画,整体缩放才是真的同比例变小。\n" +
             "0.7 → 150×200 的卡面正好变成 105×140,填满 105×140 的布局格子")]
    [Range(0.2f, 1.5f)]
    [SerializeField] internal float fieldCardScale = 0.7f;
    [Tooltip("战场上的兵牌悬停时能不能弹出完整信息卡(鼠标停在牌上不动,按卡面上的 fieldPreviewDelay 秒数弹)\n" +
             "关掉 = 战场上只能看卡面那点信息,看不到效果文案。这是新字段(老字段名叫 allowFieldHoverPreview、默认关),\n" +
             "默认开着 —— 拖着牌从排上路过时可能误弹,嫌烦就取消勾选")]
    [SerializeField] internal bool fieldHoverPreview = true;

    [Header("建筑锚点")]
    [Tooltip("建筑用的预制体(Build.prefab:一张建筑图 + 角上一颗 HP 数字)")]
    [SerializeField] internal GameObject buildPrefab;
    [Tooltip("开局要不要把下表里的建筑摆上去")]
    [SerializeField] private bool loadBuildingsAtStart = true;
    [Tooltip("开局摆哪些建筑。留空 = 按策划案§2.3 的默认表(双方各一座大营 + 军械库 + 粮草营)")]
    [SerializeField] internal List<BuildingSpec> buildings = new();

    [Header("手牌区(拖离这里才算释放)")]
    [Tooltip("手牌区。留空 → 用 FindObjectOfType 自动找 HandUI(拖动手牌时还会再找一次)。它管两件事:判断「拖回手牌区 = 取消出牌」,以及把打出的牌从扇形里移除 —— 找不到则取消判定失效、手牌也不消失")]
    [SerializeField] internal HandUI handUI;

    // 落点提示线宽度设为0，保持界面整洁
    [Header("拖动落点提示")]
    [Tooltip("拖动兵种牌时,要在链上的插入位置显示一根竖条(宽 × 高 = 这张 × 排高);宽度填 0 = 不显示")]
    [SerializeField] internal float insertMarkerWidth = 0f;
    [Tooltip("落点竖条的颜色(它不挡射线,raycastTarget 已关)。alpha 填 0 就等于看不见,和把 insertMarkerWidth 填 0 一个效果")]
    [SerializeField] internal Color insertMarkerColor = new Color(1f, 0.92f, 0.4f, 0.85f);

    /// <summary>局面变了(部署了新单位等):手牌要重新算"这张还能不能出"</summary>
    public event Action BoardChanged;

    // ---------------------------------------------------------------- 8 个组件
    // 都是普通 C# 类,不是 MonoBehaviour:它们的生命周期跟着本对象走,
    // 排/预制体的引用在 ResolveRefs 里一次填好,不存在"组件自己 Awake 得太早"的问题。
    internal PrefabResolver prefabs;
    internal BattlefieldLayouts layouts;
    internal BuildingManager buildingManager;
    internal FieldHighlighter highlight;
    internal UnitDeployer deployer;
    internal CardPlayDirector cards;
    internal BattleActionExecutor actions;

    // ---------------------------------------------------------------- 共享状态
    // 给组件用的 internal 成员:排、射线缓存、落点竖条这些是多个组件共用的。
    internal readonly List<BattleRow> rows = new();
    // 射线检测的临时缓存，存储"指针下面有哪些物体"，每次拖动时填充，可复用。
    internal readonly List<RaycastResult> hitBuffer = new();
    // 落点竖条:每条排一根,用到才建(它是排的子物体但 ignoreLayout,不参与排的布局)
    internal readonly Dictionary<BattleRow, Image> insertMarkers = new();

    public IReadOnlyList<BattleRow> Rows => rows;
    public BattleRow PlayerMid => playerMid;
    public BattleRow PlayerBack => playerBack;
    public BattleRow PlayerFront => playerFront;
    public BattleRow EnemyMid => enemyMid;
    public BattleRow EnemyBack => enemyBack;
    public RectTransform EnemyHandZone => enemyHandZone;

    private void Awake()
    {
        instance = this;

        prefabs = new PrefabResolver(this);
        layouts = new BattlefieldLayouts(this);
        buildingManager = new BuildingManager(this, prefabs);
        highlight = new FieldHighlighter(this);
        deployer = new UnitDeployer(this);
        cards = new CardPlayDirector(this);
        actions = new BattleActionExecutor(this);

        BattleSettlement.SetBoard(this);
        ResolveRefs();

        if (lockRowLayout) layouts.ApplyRowLayout();
        if (loadBuildingsAtStart) buildingManager.LoadBuildings();
    }

    private void OnDestroy() => highlight?.ClearHighlights();

    private void OnDisable() => highlight?.ClearHighlights();

    // ================================================================ 场景接线

    private void ResolveRefs()
    {
        rows.Clear();

        playerBack = prefabs.ResolveRow(playerBack, PlayerBackName, BattleSide.Player, BattleRowType.Back, BattleRules.BackUnitCapacity);
        playerMid = prefabs.ResolveRow(playerMid, PlayerMidName, BattleSide.Player, BattleRowType.Mid, BattleRules.MidUnitCapacity);
        playerFront = prefabs.ResolveRow(playerFront, PlayerFrontName, BattleSide.Player, BattleRowType.Front, BattleRules.FrontUnitCapacity);
        enemyMid = prefabs.ResolveRow(enemyMid, EnemyMidName, BattleSide.Enemy, BattleRowType.Mid, BattleRules.MidUnitCapacity);
        enemyBack = prefabs.ResolveRow(enemyBack, EnemyBackName, BattleSide.Enemy, BattleRowType.Back, BattleRules.BackUnitCapacity);

        // 链顺序 = 场景里从上到下,后军 → 中军 → 前军 → 敌方中军 → 敌方后军
        AddRow(playerBack);
        AddRow(playerMid);
        AddRow(playerFront);
        AddRow(enemyMid);
        AddRow(enemyBack);

        if (enemyHandZone == null) enemyHandZone = PrefabResolver.FindRect(EnemyHandZoneName);
    }

    private void AddRow(BattleRow row)
    {
        if (row != null && !rows.Contains(row)) rows.Add(row);
    }

    [ContextMenu("把 5 条排按名字重新认领一遍")]
    private void RebindRows()
    {
        playerBack = playerMid = playerFront = enemyMid = enemyBack = null;
        enemyHandZone = null;
        handUI = null;
        ResolveRefs();
    }

    // ================================================================ 转发给组件

    /// <summary>局面变了(部署/移动/攻击/建筑被毁):广播出去,手牌区据此重算"这张还能不能出"</summary>
    internal void RaiseBoardChanged() => BoardChanged?.Invoke();

    /// <summary>按「阵营 + 位置」查排(前军是双方共享的同一条,见 BattlefieldLayouts.FindRow)</summary>
    public BattleRow FindRow(BattleSide side, BattleRowType rowType) => layouts.FindRow(side, rowType);

    /// <summary>移动方向 → 目标排(§2.3 相邻前进 / §7.4 袭扰撤回)</summary>
    public BattleRow ResolveMoveRow(FieldUnit unit, MoveDirection direction) => layouts.ResolveMoveRow(unit, direction);

    /// <summary>
    /// 移动方向 → 完整结果(合不合法 + 落到哪条排 + 是不是后撤)。
    /// 方向判定与"相邻下一步是哪行"都在 BattleRules 里查表,这里只做转发。
    /// </summary>
    public BattleRules.MoveStep ResolveMove(FieldUnit unit, MoveDirection direction) => layouts.ResolveMove(unit, direction);

    /// <summary>目标排类型 → 哪条排(按 unit 当前位置试前进/后退,命中即返回)</summary>
    public BattleRow ResolveMoveRowTo(FieldUnit unit, BattleRowType targetType) => layouts.ResolveMoveRowTo(unit, targetType);

    /// <summary>找某一方的某座建筑(大营/军械库/粮草营)。找不到返回 null</summary>
    public FieldUnit FindBuilding(BattleSide side, string buildingName) => buildingManager.FindBuilding(side, buildingName);

    /// <summary>某一方的军械库被毁了几座积下来的 ATK 惩罚(§2.3)</summary>
    public int ArsenalPenalty(BattleSide side) => buildingManager.ArsenalPenalty(side);

    /// <summary>军械库被毁:给那一方记一笔 ATK -1</summary>
    public void AddArsenalPenalty(BattleSide victimSide) => buildingManager.AddArsenalPenalty(victimSide);

    /// <summary>军械库被修复:清除该方 ATK -1 的惩罚,场上兵牌恢复攻击力</summary>
    public void RemoveArsenalPenalty(BattleSide side) => buildingManager.RemoveArsenalPenalty(side);

    /// <summary>这张兵种牌现在有没有地方可落(手牌变灰用:满场时不该让人把牌拖起来才发现落不下)</summary>
    public bool CanDeployUnitAnywhere(CardData card, out string reason)
        => highlight.CanDeployUnitAnywhere(card, out reason);

    public void HighlightDropTargets(CardData card) => highlight.HighlightDropTargets(card);

    public void UpdatePointerHighlight(CardData card, PointerEventData pointer) => highlight.UpdatePointerHighlight(card, pointer);

    public void ClearHighlights() => highlight.ClearHighlights();

    public CardPlayResult TryResolveDrop(CardDisplay handCard, PointerEventData pointer, out string message)
        => cards.TryResolveDrop(handCard, pointer, out message);

    /// <summary>
    /// 真正落位(扣费 → 生成成员 → 结算部署增益 → 刷新袭扰标记)。
    /// 玩家拖牌(TryDeployUnit)和敌方 AI(EnemyAI 调它)共用这一条,保证 AI 不开挂、也不漏结算。
    /// </summary>
    public bool DeployUnit(CardData card, BattleRow row, int slotIndex, out FieldUnit deployed, out string message)
        => deployer.DeployUnit(card, row, slotIndex, out deployed, out message);

    /// <summary>调试/自检用:直接摆一个单位,不扣费不判容量</summary>
    public FieldUnit SpawnUnitForDebug(CardData card, BattleRow row, int slotIndex = 0)
        => deployer.SpawnUnitForDebug(card, row, slotIndex);

    /// <summary>调试/自检用:阵营可以显式指定(共享前军上要摆敌方单位时用)</summary>
    public FieldUnit SpawnUnitForDebug(CardData card, BattleRow row, BattleSide side, int slotIndex = 0)
        => deployer.SpawnUnitForDebug(card, row, side, slotIndex);

    /// <summary>
    /// 敌方(或任何一方)释放策略卡:AI 走这条,与玩家点击走的是同一套结算
    /// (策划案§6.7:AI 不直接操作 UI,所有行动通过公共 API 完成,避免"AI 开挂")
    /// </summary>
    public bool PlayTactic(CardData card, BattleSide casterSide, FieldUnit unitTarget, BattleRow rowTarget, out string message)
        => cards.PlayTactic(card, casterSide, unitTarget, rowTarget, out message);

    /// <summary>打出去的手牌退场(数据层 + 表现层 + 广播 CardPlayed)</summary>
    public void ConsumeHandCard(CardDisplay handCard) => cards.ConsumeHandCard(handCard);

    /// <summary>单位移动(§2.3 相邻前进 / §7.4 袭扰撤回),玩家点击与 AI 共用</summary>
    public bool MoveUnit(FieldUnit unit, BattleRowType targetType, out string message)
        => actions.MoveUnit(unit, targetType, out message);

    /// <summary>单位攻击(§7.1),玩家点击与 AI 共用</summary>
    public bool AttackUnit(FieldUnit attacker, FieldUnit target, out string message)
        => actions.AttackUnit(attacker, target, out message);

    /// <summary>这一方现在能不能行动(轮流回合:只有轮到自己时才能出牌、移动、攻击)</summary>
    public bool IsSideAllowedToAct(BattleSide side) => actions.IsSideAllowedToAct(side);

    public void RaycastHits(PointerEventData pointer, CardDisplay ignore) => cards.RaycastHits(pointer, ignore);

    public bool IsInsideHandArea(Vector2 screenPosition) => cards.IsInsideHandArea(screenPosition);

    // ================================================================ 必要的处理(校验/派生)

    /// <summary>
    /// 落位方自己的中军(§8.1.1 后军的例外条件要拿它比)。
    /// 前军是共享排、row.Side 恒为 Player,所以这里按"谁在落位"取 —— 和后军的取法区分开。
    /// </summary>
    internal BattleRow CarrierMidFor(BattleRow row)
        => SidePayingFor(row.Side) == BattleSide.Player ? playerMid : enemyMid;

    /// <summary>这一方该走哪个费用池(§2.5:谁的回合扣谁的费用,不看操作单位的所在行和目标行)</summary>
    internal static BattleSide SidePayingFor(BattleSide operatorSide) => operatorSide;

    /// <summary>敌方手牌区是否被指到(弃置敌牌类策略卡的目标)</summary>
    internal bool IsInsideEnemyHandZone(Vector2 screenPosition) => cards.IsInsideEnemyHandZone(screenPosition);

    /// <summary>把指针的横坐标换算成"插到链上第几位"。这个 Clamp 是落位的关键:</summary>
    /// <remarks>
    /// 返回的既是 sibling 下标也是成员名单下标(排里只有"一个成员一个格子"这一种子物体)。
    /// </remarks>
    internal static int ResolveInsertIndex(BattleRow row, PointerEventData pointer) => InsertIndexMath.Resolve(row, pointer);

    /// <summary>
    /// 按链上成员重算「这条排里驻有敌方袭扰骑兵」(§7.4)。
    /// 袭扰是骑兵进出中军时产生的实时状态:进中军挂上、离开就掉,
    /// 所以每次移动 / 部署 / 效果结算之后都要把 5 条排重算一遍 ——
    /// §8.1.1 的「中军满员时能否部署进后军」就是拿这个标记判的。
    /// </summary>
    public void RefreshRaidFlags()
    {
        for (int i = 0; i < rows.Count; i++) rows[i]?.RefreshRaiderFlag();
    }

    // ================================================================ 调试

    [Header("调试")]
    [Tooltip("按一下切换「己方中军里驻有敌方袭扰骑兵」。\n" +
             "袭扰(§7.4)现在是骑兵进出中军时的实时状态,正常流程由 RefreshRaidFlags 自己算;\n" +
             "这个键是给「手上还没有骑兵,但想验 §8.1.1 后军部署」时手动模拟用的")]
    [SerializeField] private KeyCode debugRaiderToggleKey = KeyCode.F9;

    private void Update()
    {
        if (debugRaiderToggleKey != KeyCode.None && Input.GetKeyDown(debugRaiderToggleKey))
            DebugTools.ToggleDebugRaider(this);
    }
}

/// <summary>
/// 指针横坐标 → 链上插入位置(§8.1.1 落位的几何计算)。
/// 单独拎出来是因为 FieldHighlighter 画落点竖条要用同一套坐标,
/// 两边各算一份必然对不上(竖条说插这儿、落位插那儿)。
/// </summary>
internal static class InsertIndexMath
{
    /// <summary>
    /// 返回的既是 sibling 下标也是成员名单下标(排里只有"一个成员一个格子"这一种子物体)。
    /// </summary>
    public static int Resolve(BattleRow row, PointerEventData pointer)
    {
        if (row == null) return 0;

        var rowRect = row.transform as RectTransform;
        if (rowRect == null || pointer == null) return row.MemberCount;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rowRect, pointer.position, pointer.pressEventCamera, out var local))
            return row.MemberCount;

        // 局部坐标以排的轴心为原点,换算成"离排左边缘多少像素"
        float pointerX = local.x + rowRect.rect.width * 0.5f;

        var members = row.Units;
        for (int i = 0; i < members.Count; i++)
        {
            var rt = members[i] != null ? members[i].transform as RectTransform : null;
            if (rt == null) continue;

            // 格子的锚点在排的左边(0,1),所以 anchoredPosition.x 就是它中心的横坐标
            if (pointerX < rt.anchoredPosition.x) return i;
        }

        return row.MemberCount;
    }
}
