using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>拖出去之后这手牌怎么了</summary>
public enum CardPlayResult
{
    Cancelled,   // 拖回手牌区 = 取消,什么都不做
    Failed,      // 试过但落不下去(message 是原因)
    Deployed,    // 兵种牌落位成功
    Released,    // 策略卡释放成功
}

/// <summary>
/// 战场总控:把场景里那 5 条排接成"可以落牌"的目标,并负责把一张手牌真正打出去。
///
/// 出牌流程(策划案§7.2.1 / §8.1.1 / §10.2):
///   · 兵种牌:按住拖到己方中军(§8.1.1 容量判定;后军要中军满员且驻有敌方袭扰骑兵)
///   · 策略卡:无目标 → 拖离手牌区即释放;需要目标 → 拖到目标上释放(§7.2.2)
/// 判定失败的手牌自己飞回扇形,原因由 CardDragPlay 飘字。
///
/// 所有场景引用都能留空:运行时按名字在场景里找 RowsContainer 底下那 5 条排
/// (PlayerBack/PlayerMid/Front/EnemyMid/EnemyBack),敌方手牌区找 HandArea2,
/// 连 BattleRow 组件都会按名字补上并配好容量 —— 所以本功能不需要改场景。
///
/// 【挂载 & 调整】
///   挂在:Battle.unity 里挂在 BattleArea/RowsContainer 上(Assets/_Project/Scenes/Battle.unity,
///         该物体上还有 VerticalLayoutGroup + 一张全透明 Image)。场景里漏挂也能自救:Instance 先全场景找,
///         找不到就给 RowsContainer 现加一个,连 RowsContainer 都没有才新建空物体挂到 Canvas 下。
///   引用:5 条排(playerBack/playerMid/playerFront/enemyMid/enemyBack)、enemyHandZone、handUI 都能留空 ——
///         Awake 的 ResolveRefs 会按场景物体名字自动补(PlayerBack/PlayerMid/Front/EnemyMid/EnemyBack、
///         HandArea2,HandUI 用 FindObjectOfType 找);排上还没有 BattleRow 组件时,还会当场 AddComponent
///         并配好阵营/位置/容量。当前 Battle.unity 里这几个排字段就是空的,全靠这套兜底在跑
///         (只有 enemyHandZone 是手连的)。找不到己方中军会 LogError,兵种牌没地方落;
///         handUI 找不到则打出的牌不移除、「拖回手牌区 = 取消」判定失效;
///         buildPrefab 留空时编辑器里会按 Assets/_Project/Prefabs/Battle/Build.prefab 自动认领,认领不到只警告、建筑不摆;
///         卡牌预制体取自 handUI 的 cardPrefab,那个没填就 LogError、部署不了单位。
///   常调:
///     · rowHeight:每排钉死的高度;调大 = 每条排更胖(5 条排总高会超出 RowsContainer 而溢出),调小 = 每条排更扁、缝更大;改完要保证 5×rowHeight + 4×容器 spacing 还塞得进容器。
///     · memberGap:链上相邻成员之间的间距,这段空隙是留给 buff 图标的;调大 = 卡与卡隔得开、一条排横向更挤,调小 = 贴在一起,0 就是完全挨着。
///     · fieldCardScale:战场小卡相对卡面设计尺寸 150×200 的缩放,0.7 → 格子 105×140;调大 = 卡面更清楚但一条排能放的成员更少,调小 = 能放更多但字变小;格子尺寸和卡面缩放都由它算,两边一起变。
///     · allowFieldHoverPreview:战场小卡要不要 3 秒悬停预览;默认关(拖着牌从上面经过容易误弹),想让玩家在战场上看卡详情就勾上。
///     · insertMarkerWidth:拖兵种牌时那根落点竖条的宽度;场景里现在填的是 0,也就是这根提示条是关着的,想看到「松手会插在哪个缝」就填 3~6。
///     · loadBuildingsAtStart:开局要不要摆大营/军械库/粮草;关掉后场上没有建筑(但容量仍按建筑占位扣过,后军只剩 1 格),想空场调试或自己做布阵就关掉。
///     · lockRowLayout:要不要由本脚本钉死行高与间距;关掉就完全听 RowsContainer 的 VerticalLayoutGroup,排里一塞卡整排会被撑高、5 条排挤走位。
///     · debugRaiderToggleKey:调试键,按一下切换「己方中军驻有敌方袭扰骑兵」;袭扰机制(§7.4)没做,要试 §8.1.1 的后军部署只能靠它(场景里这个键被改成 N,和 TurnController 的结束回合键是同一个键)。
/// </summary>
[DisallowMultipleComponent]
public class BattlefieldManager : MonoBehaviour
{
    // 场景里的名字(和策划案§2.3 的排顺序一致;RowsContainer 上那个 VerticalLayoutGroup
    // 是自上而下排的,所以 EnemyBack 在最上面、PlayerBack 在最下面)
    private const string PlayerBackName = "PlayerBack";
    private const string PlayerMidName = "PlayerMid";
    private const string PlayerFrontName = "Front";
    private const string EnemyMidName = "EnemyMid";
    private const string EnemyBackName = "EnemyBack";
    private const string EnemyHandZoneName = "HandArea2";

    /// <summary>编辑器里自动认领建筑预制体用的路径(Inspector 上没拖时才用)</summary>
    private const string BuildPrefabPath = "Assets/_Project/Prefabs/Battle/Build.prefab";

    private static BattlefieldManager instance;

    /// <summary>场景里没有就自己建一个(挂在 RowsContainer 上),用到才建</summary>
    public static BattlefieldManager Instance
    {
        get
        {
            if (instance != null) return instance;
            instance = FindObjectOfType<BattlefieldManager>();
            if (instance != null) return instance;

            var host = FindRect("RowsContainer");
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
    [SerializeField] private BattleRow playerBack;
    [Tooltip("己方中军这条排,兵种牌的默认落点。留空 → 按名字 PlayerMid 自动找;它找不到会 LogError,兵种牌就彻底没地方落")]
    [SerializeField] private BattleRow playerMid;
    [Tooltip("共享前军这条排。留空 → 按名字 Front 自动找。注意前军本来就只能靠移动进入、不能直接部署,填上只是为了让它一起参与拖动高亮")]
    [SerializeField] private BattleRow playerFront;
    [Tooltip("敌方中军这条排(以整排为目标的策略卡落点)。留空 → 按名字 EnemyMid 自动找;找不到就没有这个目标")]
    [SerializeField] private BattleRow enemyMid;
    [Tooltip("敌方后军这条排。留空 → 按名字 EnemyBack 自动找;找不到就没有这个目标")]
    [SerializeField] private BattleRow enemyBack;

    [Header("敌方手牌区(弃置敌牌类策略卡的目标)")]
    [Tooltip("敌方手牌区的 RectTransform。留空 → 按名字 HandArea2 自动找;找不到时「弃置敌牌」类策略卡无处可落,拖动时也不会有绿框提示")]
    [SerializeField] private RectTransform enemyHandZone;
    [Tooltip("拖动「弃置敌牌」类策略卡时,敌方手牌区染上的提示色(alpha 直接决定提示强度,原色在首次高亮时被缓存下来)")]
    [SerializeField] private Color enemyHandZoneTint = new Color(0.35f, 1f, 0.55f, 0.45f);

    [Header("排布(策划案§2.3:行高固定 + 链上成员定距)")]
    [Tooltip("每排高度钉死成多少像素(初始帧 5 条排平摊后大约就是 144)")]
    [SerializeField] private float rowHeight = 144f;
    [Tooltip("链上相邻两个成员之间留多少像素 —— 这段空隙是留给 buff 图标的")]
    [SerializeField] private float memberGap = 20f;
    [Tooltip("要不要由本脚本把排高钉死(不勾就完全听场景里那两层的布局组)")]
    [SerializeField] private bool lockRowLayout = true;

    [Header("战场小卡")]
    [Tooltip("部署进排里的兵牌缩到多大(1 = 和手牌一样大)。卡面内容整体等比缩放:\n" +
             "只改 rect 不会放大字号和插画,整体缩放才是真的同比例变小。\n" +
             "0.7 → 150×200 的卡面正好变成 105×140,填满 105×140 的布局格子")]
    [Range(0.2f, 1.5f)]
    [SerializeField] private float fieldCardScale = 0.7f;
    [Tooltip("战场上的兵牌还要不要 3 秒悬停预览(拖着牌从上面经过时容易误弹)")]
    [SerializeField] private bool allowFieldHoverPreview = false;

    [Header("建筑锚点(策划案§2.3:大营在中军,军械库/粮草在后军)")]
    [Tooltip("建筑用的预制体(Build.prefab:一张建筑图 + 角上一颗 HP 数字)")]
    [SerializeField] private GameObject buildPrefab;
    [Tooltip("开局要不要把下表里的建筑摆上去")]
    [SerializeField] private bool loadBuildingsAtStart = true;
    [Tooltip("开局摆哪些建筑。留空 = 按策划案§2.3 的默认表(双方各一座大营 + 军械库 + 粮草)")]
    [SerializeField] private List<BuildingSpec> buildings = new();

    [Header("手牌区(拖离这里才算释放)")]
    [Tooltip("手牌区。留空 → 用 FindObjectOfType 自动找 HandUI(拖动手牌时还会再找一次)。它管两件事:判断「拖回手牌区 = 取消出牌」,以及把打出的牌从扇形里移除 —— 找不到则取消判定失效、手牌也不消失")]
    [SerializeField] private HandUI handUI;

    [Header("拖动落点提示")]
    [Tooltip("拖动兵种牌时,要在链上的插入位置显示一根竖条(宽 × 高 = 这张 × 排高);宽度填 0 = 不显示")]
    [SerializeField] private float insertMarkerWidth = 5f;
    [Tooltip("落点竖条的颜色(它不挡射线,raycastTarget 已关)。alpha 填 0 就等于看不见,和把 insertMarkerWidth 填 0 一个效果")]
    [SerializeField] private Color insertMarkerColor = new Color(1f, 0.92f, 0.4f, 0.85f);

    /// <summary>手的牌账在 DeckController 里(打出牌要从那儿扣掉)</summary>
    private DeckController deckController;

    /// <summary>局面变了(部署了新单位等):手牌要重新算"这张还能不能出"</summary>
    public event Action BoardChanged;

    private readonly List<BattleRow> rows = new();
    private readonly List<RaycastResult> hitBuffer = new();

    /// <summary>军械库被毁后积下来的 ATK 惩罚(§2.3):场上兵牌与后续上场兵牌各 -1,可叠加</summary>
    private int playerArsenalPenalty;
    private int enemyArsenalPenalty;

    // 拖动期间的高亮缓存:拖动开始时算一次"哪些排本来就合法",指针移动时只改指针底下那一条
    private readonly HashSet<BattleRow> baseValidRows = new();
    private BattleRow hoveredRow;
    private FieldUnit hoveredUnit;
    private Image enemyHandBackground;
    private Color enemyHandOriginalColor;
    private bool enemyHandColorCached;

    // 落点竖条:每条排一根,用到才建(它是排的子物体但 ignoreLayout,不参与排的布局)
    private readonly Dictionary<BattleRow, Image> insertMarkers = new();
    private Image activeInsertMarker;

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
        ResolveRefs();

        // 结算层要按「哪条排有谁」算射程/守护/大营血量,先把账交给它
        BattleSettlement.SetBoard(this);

        // 开局把排布钉死(行高固定)再把建筑摆上去 —— 都要早于第一张手牌抽出来,
        // 否则手牌变灰的判定会先按"空场"算一遍
        if (lockRowLayout) ApplyRowLayout();
        if (loadBuildingsAtStart) LoadBuildings();
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
        ClearHighlights();
    }

    private void OnDisable() => ClearHighlights();

    // ================================================================ 场景接线

    private void ResolveRefs()
    {
        playerBack  = ResolveRow(playerBack,  PlayerBackName,  BattleSide.Player, BattleRowType.Back,  BattleRules.BackUnitCapacity);
        playerMid   = ResolveRow(playerMid,   PlayerMidName,   BattleSide.Player, BattleRowType.Mid,   BattleRules.MidUnitCapacity);
        playerFront = ResolveRow(playerFront, PlayerFrontName, BattleSide.Player, BattleRowType.Front, BattleRules.FrontUnitCapacity);
        enemyMid    = ResolveRow(enemyMid,    EnemyMidName,    BattleSide.Enemy,  BattleRowType.Mid,   BattleRules.MidUnitCapacity);
        enemyBack   = ResolveRow(enemyBack,   EnemyBackName,   BattleSide.Enemy,  BattleRowType.Back,  BattleRules.BackUnitCapacity);

        if (enemyHandZone == null) enemyHandZone = FindRect(EnemyHandZoneName);
        if (handUI == null) handUI = FindObjectOfType<HandUI>();

        rows.Clear();
        AddRow(playerBack);
        AddRow(playerMid);
        AddRow(playerFront);
        AddRow(enemyMid);
        AddRow(enemyBack);

        if (playerMid == null)
            Debug.LogError("[BattlefieldManager] 找不到己方中军这条排,兵种牌没地方落。", this);
    }

    private void AddRow(BattleRow row)
    {
        if (row != null && !rows.Contains(row)) rows.Add(row);
    }

    // ================================================================ 开局排场

    /// <summary>
    /// 把行高和链间距钉死(§2.3)。
    ///
    /// 原来 RowsContainer 上的 VerticalLayoutGroup 是按子物体的偏好高度撑各排的:
    /// 排里一塞卡牌,这条排的偏好高度就变成卡牌高度,整排变高、5 条排全被挤走位。
    /// 这里关掉它对高度的接管(childControlHeight),高度改由每条排自己写死
    /// (BattleRow.LockLayout),间距由容器自己那层 spacing 出。
    /// </summary>
    private void ApplyRowLayout()
    {
        var container = GetComponent<VerticalLayoutGroup>();
        if (container != null)
        {
            container.childControlHeight = false;     // 高度各排自己定,别按内容撑
            container.childForceExpandHeight = false; // 不摊余量:5×144 + 4×8 = 752,铺满 756 差 4px(间距来自 RowsContainer 的 VerticalLayoutGroup)
        }

        for (int i = 0; i < rows.Count; i++)
            if (rows[i] != null) rows[i].LockLayout(rowHeight, memberGap);
    }

    /// <summary>
    /// 开局载入建筑锚点(§2.3):大营 → 己方中军,军械库 + 粮草 → 己方后军;敌方同构。
    /// 表里留空就按策划案默认表(大营 20HP、军械库/粮草各 5HP)。
    /// </summary>
    private void LoadBuildings()
    {
        if (ResolveBuildPrefab() == null)
        {
            Debug.LogWarning("[BattlefieldManager] 没给 buildPrefab,开局建筑摆不出来" +
                             "(把资产里的 Build.prefab 拖到这个字段上)。", this);
            return;
        }

        var list = buildings != null && buildings.Count > 0 ? buildings : DefaultBuildings();

        int placed = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var spec = list[i];
            if (spec == null) continue;

            var row = FindRow(spec.side, spec.row);
            if (row == null)
            {
                Debug.LogWarning($"[BattlefieldManager] 建筑「{spec.displayName}」找不到 {spec.side} 的 {spec.row} 排,没摆上去。", this);
                continue;
            }

            if (SpawnBuilding(spec, row) != null) placed++;
        }

        Debug.Log($"[Battlefield] 开局载入建筑 {placed} 座(§2.3:大营 20HP 在中军,军械库/粮草各 5HP 在后军)");
        BoardChanged?.Invoke();
    }

    /// <summary>
    /// §2.3 建筑表:大营 20 / 军械库 5 / 粮草 5 —— 这三个数是**起始血量**,不是"上限"。
    /// 打空 = 成废墟(留在场上,能被维修卡修回来),修回来的血量就是这里的起始值。
    /// 双方各一套。后军链接顺序 = 军械库 … 粮草
    /// </summary>
    private static List<BuildingSpec> DefaultBuildings() => new()
    {
        new BuildingSpec { displayName = "大营",   side = BattleSide.Player, row = BattleRowType.Mid,  hp = 20 },
        new BuildingSpec { displayName = "军械库", side = BattleSide.Player, row = BattleRowType.Back, hp = 5 },
        new BuildingSpec { displayName = "粮草",   side = BattleSide.Player, row = BattleRowType.Back, hp = 5 },
        new BuildingSpec { displayName = "大营",   side = BattleSide.Enemy,  row = BattleRowType.Mid,  hp = 20 },
        new BuildingSpec { displayName = "军械库", side = BattleSide.Enemy,  row = BattleRowType.Back, hp = 5 },
        new BuildingSpec { displayName = "粮草",   side = BattleSide.Enemy,  row = BattleRowType.Back, hp = 5 },
    };

    private BattleRow FindRow(BattleSide side, BattleRowType rowType)
    {
        // §2.3 前军是双方共用的**同一条排**(场景里它就是 playerFront,row.Side 恒为 Player)。
        // 所以这里不能按 side 过滤后军/中军那样去查前军 —— 否则敌方单位一往前走
        // 就会得到"战场上没有这条排",而它明明就在场中央。
        if (rowType == BattleRowType.Front) return playerFront;

        for (int i = 0; i < rows.Count; i++)
            if (rows[i] != null && rows[i].Side == side && rows[i].RowType == rowType) return rows[i];
        return null;
    }

    /// <summary>
    /// **移动**的落点排(和"部署"不同,移动的落点要看单位现在站在哪儿)。
    ///
    /// 关键就是共享前军这一条:排类型只写 Back/Mid/Front,而"中军"到底是谁的中军要看方向 ——
    /// 从后军往上走 = 自己的中军;从共享前军往前走 = **对面的中军**(§7.4 袭扰)。
    /// 以前这里用的是 FindRow(按阵营查),于是站在前军时"中军"会被解析成**自己的中军**,
    /// 而 AI 和玩家点的却是对面中军 —— 两边算出来的落点不是同一条排,判定就会用错排的容量、
    /// 也可能把一次前进当成"往回走"。
    ///
    /// 这是移动落点的**唯一权威解析**,UI / AI / 结算都必须走它,别再各算一份。
    /// </summary>
    public BattleRow ResolveMoveRow(FieldUnit unit, BattleRowType rowType)
    {
        if (unit == null) return null;

        // 前军永远是双方共享的那一条
        if (rowType == BattleRowType.Front) return playerFront;

        // 后军:只能是自己的
        if (rowType == BattleRowType.Back)
            return unit.Side == BattleSide.Player ? playerBack : enemyBack;

        // 中军:从共享前军往前走 = 对面中军(袭扰);其余情况 = 自己的中军
        if (unit.Row != null && unit.Row.RowType == BattleRowType.Front)
            return unit.Side == BattleSide.Player ? enemyMid : playerMid;

        return unit.Side == BattleSide.Player ? playerMid : enemyMid;
    }

    /// <summary>
    /// 这次落位该扣谁的费(§2.5 双方各有指挥点池)。
    ///
    /// 不能用 row.Side:前军是双方共享的那一条,它的 row.Side 永远是 Player ——
    /// 敌方要是往共享前军落位,费用会被算到我方账上。所以这里按"落位方的排"来判:
    /// 后军/中军各归各家,前军(共享)则看这回合轮到谁 —— 分回合制下能落位的只有当前行动方。
    /// </summary>
    private static BattleSide SidePayingFor(BattleRow row)
    {
        if (row == null) return BattleSide.Player;
        if (row.RowType != BattleRowType.Front) return row.Side;

        var turn = TurnController.Instance;
        if (turn != null && turn.HasStarted) return turn.IsLocalTurn ? BattleSide.Player : BattleSide.Enemy;
        return row.Side;
    }

    // ================================================================ 建筑与 Debuff(§2.3)

    /// <summary>找某一方的某座建筑(大营/军械库/粮草)。找不到返回 null</summary>
    public FieldUnit FindBuilding(BattleSide side, string buildingName)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null || row.Side != side) continue;

            var members = row.Units;
            for (int j = 0; j < members.Count; j++)
            {
                var unit = members[j];
                if (unit != null && unit.IsBuilding && unit.BuildingName == buildingName) return unit;
            }
        }
        return null;
    }

    /// <summary>某一方的军械库被毁了几座积下来的 ATK 惩罚(§2.3:军械库被毁 → 全场兵牌 ATK -1)</summary>
    public int ArsenalPenalty(BattleSide side) => side == BattleSide.Player ? playerArsenalPenalty : enemyArsenalPenalty;

    /// <summary>军械库被毁:给那一方记一笔 ATK -1(场上已有的兵牌立刻扣,之后上场的在 SpawnUnit 里扣)</summary>
    public void AddArsenalPenalty(BattleSide victimSide)
    {
        if (victimSide == BattleSide.Player) playerArsenalPenalty++;
        else enemyArsenalPenalty++;

        // 场上现有的兵牌立刻吃这一刀(建筑不吃)
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null || row.Side != victimSide) continue;

            var members = row.Units;
            for (int j = 0; j < members.Count; j++)
                if (members[j] != null && members[j].IsUnit && members[j].IsAlive) members[j].ApplyAtkDelta(-1);
        }

        BoardChanged?.Invoke();
    }

    /// <summary>新上场的兵牌补上军械库的 ATK 惩罚(§2.3「后续上场兵牌 ATK -1」)</summary>
    private void ApplyArsenalPenalty(FieldUnit unit)
    {
        if (unit == null || !unit.IsUnit) return;

        int penalty = ArsenalPenalty(unit.Side);
        if (penalty > 0) unit.ApplyAtkDelta(-penalty);
    }

    /// <summary>按链上成员重算 5 条排的「驻有敌方袭扰骑兵」标记(§7.4 → §8.1.1 的后军部署靠它)</summary>
    public void RefreshRaidFlags()
    {
        for (int i = 0; i < rows.Count; i++) rows[i]?.RefreshRaiderFlag();
        BoardChanged?.Invoke();
    }

    /// <summary>
    /// 拿到建筑预制体。优先用 Inspector 里拖的那个;没拖就在编辑器里按路径认领一份 ——
    /// 场景被 Unity 从内存里另存、把引用冲掉时,不用手动再拖一次也能跑起来。
    /// </summary>
    private GameObject ResolveBuildPrefab()
    {
        if (buildPrefab != null) return buildPrefab;

#if UNITY_EDITOR
        buildPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BuildPrefabPath);
        if (buildPrefab != null)
            Debug.Log($"[BattlefieldManager] buildPrefab 没赋值,已按路径自动认领 {BuildPrefabPath}", this);
#endif
        return buildPrefab;
    }

    /// <summary>
    /// 按名字认领一条排。场景里本来就有 BattleRow(自己配好了身份)就用它;
    /// 没有就补一个并按名字配好阵营/位置/容量。
    /// </summary>
    private BattleRow ResolveRow(BattleRow current, string objectName, BattleSide side, BattleRowType rowType, int capacity)
    {
        bool created = false;

        if (current == null)
        {
            var rt = FindRect(objectName);
            if (rt == null)
            {
                Debug.LogError($"[BattlefieldManager] 场景里找不到排「{objectName}」,这条排落不了牌。", this);
                return null;
            }

            current = rt.GetComponent<BattleRow>();
            if (current == null)
            {
                current = rt.gameObject.AddComponent<BattleRow>();
                created = true;
            }
        }

        if (created) current.Configure(side, rowType, capacity);
        return current;
    }

    private static RectTransform FindRect(string objectName)
    {
        var go = GameObject.Find(objectName);
        return go != null ? go.transform as RectTransform : null;
    }

    private CardDisplay ResolveCardPrefab()
    {
        if (handUI == null) handUI = FindObjectOfType<HandUI>();
        if (handUI == null || handUI.CardPrefab == null)
        {
            Debug.LogError("[BattlefieldManager] 拿不到 Card.prefab(HandUI 的 cardPrefab 没赋值),部署不了单位。", this);
            return null;
        }
        return handUI.CardPrefab;
    }

    private static Camera EventCameraFor(RectTransform rect)
    {
        if (rect == null) return null;
        var canvas = rect.GetComponentInParent<Canvas>();
        if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
        return canvas.worldCamera;
    }

    // ================================================================ 查询(给手牌变灰用)

    /// <summary>
    /// 这张兵种牌现在有没有地方能落(§8.1.1:中军没满就能进;中军满了且驻有敌方袭扰骑兵时能进后军)。
    /// 没有的话 reason 说明原因,手牌据此变灰 + 点击飘字。
    /// </summary>
    public bool CanDeployUnitAnywhere(CardData card, out string reason)
    {
        reason = "中军已满，无法部署";
        if (card == null || !card.IsUnitCard) { reason = "只有兵种牌才能部署"; return false; }

        if (playerMid != null && BattleRules.CanDeployUnit(card, playerMid, playerMid, out reason)) return true;
        if (playerBack != null && BattleRules.CanDeployUnit(card, playerBack, playerMid, out string backReason)) return true;

        // 中军的原因更有代表性(玩家最先想知道的就是中军为什么进不去)
        if (playerMid != null) BattleRules.CanDeployUnit(card, playerMid, playerMid, out reason);
        return false;
    }

    // ================================================================ 拖动高亮

    /// <summary>拖动开始:§10.2「合法部署行高亮」</summary>
    public void HighlightDropTargets(CardData card)
    {
        ClearHighlights();
        if (card == null) return;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            bool valid = IsRowValidFor(card, row);
            if (valid) baseValidRows.Add(row);
            row.SetHighlight(valid ? RowHighlight.Valid : RowHighlight.None);
        }

        if (card.targetType == TacticTargetType.EnemyHand) SetEnemyHandHighlight(true);
    }

    /// <summary>拖动过程中:指针底下那条排/那个单位单独高亮(非法就红闪色)</summary>
    public void UpdatePointerHighlight(CardData card, PointerEventData pointer)
    {
        if (card == null || pointer == null) { ClearPointerHighlight(); return; }

        RaycastHits(pointer, null);
        BattleRow row = null;
        FieldUnit unit = null;
        for (int i = 0; i < hitBuffer.Count; i++)
        {
            var go = hitBuffer[i].gameObject;
            if (go == null) continue;

            var hitUnit = go.GetComponentInParent<FieldUnit>();
            if (hitUnit != null) { unit = hitUnit; row = hitUnit.Row; break; }

            var hitRow = go.GetComponentInParent<BattleRow>();
            if (hitRow != null) { row = hitRow; break; }
        }

        bool rowValid = row != null && IsRowValidFor(card, row);
        bool unitValid = unit != null && BattleRules.CanTargetUnit(card, unit, out _);

        // 单位高亮(只有合法目标才亮)
        if (hoveredUnit != unit)
        {
            if (hoveredUnit != null) hoveredUnit.SetTargetHighlight(false);
            hoveredUnit = unit;
        }
        if (hoveredUnit != null) hoveredUnit.SetTargetHighlight(unitValid);

        // 排高亮:指针底下这条按合法性上色,离开后回到"拖动开始时"的状态
        if (hoveredRow != row)
        {
            if (hoveredRow != null) RestoreRowHighlight(hoveredRow);
            hoveredRow = row;
        }
        if (hoveredRow != null)
        {
            if (rowValid) hoveredRow.SetHighlight(RowHighlight.Valid);
            else if (IsRowCandidateFor(card, hoveredRow)) hoveredRow.SetHighlight(RowHighlight.Invalid);
            else RestoreRowHighlight(hoveredRow);
        }

        // 兵种牌:再把"松手会插在链上哪个缝"标出来 —— 想贴大营左边就把竖条对到它左边
        if (card.IsUnitCard && rowValid) ShowInsertMarker(hoveredRow, ResolveInsertIndex(hoveredRow, pointer));
        else HideInsertMarker();
    }

    /// <summary>松手/取消:把拖动期间上的色全部还回去</summary>
    public void ClearHighlights()
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i] != null) rows[i].SetHighlight(RowHighlight.None);

        baseValidRows.Clear();
        hoveredRow = null;

        if (hoveredUnit != null) hoveredUnit.SetTargetHighlight(false);
        hoveredUnit = null;

        HideInsertMarker();
        SetEnemyHandHighlight(false);
    }

    private void ClearPointerHighlight()
    {
        if (hoveredUnit != null) hoveredUnit.SetTargetHighlight(false);
        hoveredUnit = null;

        HideInsertMarker();

        if (hoveredRow != null) RestoreRowHighlight(hoveredRow);
        hoveredRow = null;
    }

    private void RestoreRowHighlight(BattleRow row)
    {
        if (row == null) return;
        row.SetHighlight(baseValidRows.Contains(row) ? RowHighlight.Valid : RowHighlight.None);
    }

    private bool IsRowValidFor(CardData card, BattleRow row)
    {
        if (card == null || row == null) return false;
        if (card.IsUnitCard) return BattleRules.CanDeployUnit(card, row, CarrierMidFor(row), out _);
        return BattleRules.CanTargetRow(card, row, out _);
    }

    /// <summary>这条排属于哪一方,就取那一方的中军(§8.1.1 后军例外条件要按落位方判)</summary>
    private BattleRow CarrierMidOf(BattleRow row)
        => row == null ? null : row.Side == BattleSide.Player ? playerMid : enemyMid;

    /// <summary>
    /// 落位方自己的中军(§8.1.1 后军的例外条件要拿它比)。
    /// 前军是共享排、row.Side 恒为 Player,所以这里按"谁在落位"取 —— 和后军的取法区分开。
    /// </summary>
    private BattleRow CarrierMidFor(BattleRow row)
        => SidePayingFor(row) == BattleSide.Player ? playerMid : enemyMid;

    /// <summary>这条排"本来想落但落不下"—— 指针停上去要红闪,而不是毫无反应</summary>
    private bool IsRowCandidateFor(CardData card, BattleRow row)
    {
        if (card == null || row == null) return false;
        if (card.IsUnitCard) return true;      // 兵种牌:停在任何一条排上都算"想落在这儿"(敌我排都红闪)
        return card.targetType == TacticTargetType.EnemyRow || card.targetType == TacticTargetType.AllyRow;
    }

    private void SetEnemyHandHighlight(bool on)
    {
        if (!enemyHandColorCached)
        {
            if (enemyHandZone == null) enemyHandZone = FindRect(EnemyHandZoneName);
            enemyHandBackground = enemyHandZone != null ? enemyHandZone.GetComponent<Image>() : null;
            if (enemyHandBackground != null) enemyHandOriginalColor = enemyHandBackground.color;
            enemyHandColorCached = true;
        }

        if (enemyHandBackground == null) return;
        enemyHandBackground.color = on ? enemyHandZoneTint : enemyHandOriginalColor;
    }

    // ================================================================ 出牌

    /// <summary>
    /// 把指针底下拖着的这张手牌结算掉。
    /// Cancelled = 拖回手牌区(策划案§10.2 的"点击空白处取消"),不飘字;
    /// Failed 时 message 是拒绝的原因;成功时 message 是一句可以飘出来的话。
    /// </summary>
    public CardPlayResult TryResolveDrop(CardDisplay handCard, PointerEventData pointer, out string message)
    {
        message = null;
        if (handCard == null || handCard.Data == null || pointer == null) return CardPlayResult.Cancelled;

        var card = handCard.Data;

        // 拖回手牌区 = 取消出牌
        if (IsInsideHandArea(pointer.position)) return CardPlayResult.Cancelled;

        // 大营被打空(对局结束)之后谁也不能再出牌
        if (BattleSettlement.MatchOver)
        {
            message = "对局已经结束";
            return CardPlayResult.Failed;
        }

        // 轮流轮次:不是我方回合,一张牌都打不出去(拖起来可以,落地弹回去)
        var turn = TurnController.Instance;
        if (turn != null && turn.HasStarted && !turn.IsLocalTurn)
        {
            message = "现在是对方的回合";
            return CardPlayResult.Failed;
        }

        var cp = CommandPointController.Instance;
        if (cp != null && !cp.CanAfford(card.deploymentCost))
        {
            message = $"指挥点不足（{cp.Cp}/{card.deploymentCost}）";
            return CardPlayResult.Failed;
        }

        RaycastHits(pointer, handCard);

        return card.IsUnitCard
            ? TryDeployUnit(handCard, card, pointer, out message)
            : TryPlayTactic(handCard, card, pointer, out message);
    }

    private CardPlayResult TryDeployUnit(CardDisplay handCard, CardData card, PointerEventData pointer, out string message)
    {
        message = null;

        BattleRow row = null;
        FieldUnit hitUnit = null;
        for (int i = 0; i < hitBuffer.Count; i++)
        {
            var go = hitBuffer[i].gameObject;
            if (go == null) continue;

            var unit = go.GetComponentInParent<FieldUnit>();
            if (unit != null) { hitUnit = unit; row = unit.Row; break; }

            var hitRow = go.GetComponentInParent<BattleRow>();
            if (hitRow != null) { row = hitRow; break; }
        }

        if (row == null)
        {
            message = "只能部署到己方中军";
            return CardPlayResult.Failed;
        }

        // §8.1.1:中军容量未满才能进;后军要中军满员且驻有敌方袭扰骑兵
        if (!BattleRules.CanDeployUnit(card, row, playerMid, out message))
        {
            row.FlashInvalid();
            return CardPlayResult.Failed;
        }

        int slot = ResolveInsertIndex(row, pointer);
        if (!DeployUnit(card, row, slot, out var deployed, out message))
        {
            row.FlashInvalid();
            return CardPlayResult.Failed;
        }

        ConsumeHandCard(handCard);

        message = $"「{card.cardName}」→ {row.DisplayName}（{row.UnitCount}/{row.UnitCapacity}）";
        Debug.Log($"[Battlefield] 部署「{card.cardName}」→ {row.DisplayName}，该排单位 {row.UnitCount}/{row.UnitCapacity}");
        BoardChanged?.Invoke();

        ShowDeployEffect(card, deployed, row);
        return CardPlayResult.Deployed;
    }

    /// <summary>
    /// 真正落位(扣费 → 生成成员 → 结算部署增益 → 刷新袭扰标记)。
    /// 玩家拖牌(TryDeployUnit)和敌方 AI(EnemyAI 调它)共用这一条,保证 AI 不开挂、也不漏结算。
    /// </summary>
    public bool DeployUnit(CardData card, BattleRow row, int slotIndex, out FieldUnit deployed, out string message)
    {
        deployed = null;
        message = null;

        if (card == null || row == null) { message = "没有可部署的卡或排"; return false; }
        if (BattleSettlement.MatchOver) { message = "对局已经结束"; return false; }

        // §8.1.1 后军的例外条件要看"落位方自己的中军",所以这里按落位方取,不能写死我方中军
        var carrierMid = CarrierMidFor(row);
        if (!BattleRules.CanDeployUnit(card, row, carrierMid, out message)) return false;

        // 落位方的费用池:我方走 CommandPointController,敌方走 EnemyDeckController
        // (共享前军的 row.Side 恒为 Player,所以这里必须走 SidePayingFor,不能直接看 row.Side)
        if (SidePayingFor(row) == BattleSide.Player)
        {
            var cp = CommandPointController.Instance;
            if (cp != null && !cp.TrySpend(card.deploymentCost)) { message = "指挥点不足"; return false; }
        }
        else
        {
            var enemy = EnemyDeckController.Instance;
            if (enemy != null && !enemy.TrySpend(card.deploymentCost)) { message = "敌方指挥点不足"; return false; }
        }

        deployed = SpawnUnit(card, row, Mathf.Clamp(slotIndex, 0, row.Units.Count), SidePayingFor(row));
        if (deployed == null) { message = "落位失败（找不到卡牌预制体）"; return false; }

        row.RefreshRaiderFlag();
        Debug.Log($"[Battlefield] {BattleRules.SideName(deployed.Side)}部署「{card.cardName}」→ {row.DisplayName}" +
                  $"（{row.UnitCount}/{row.UnitCapacity}）");
        return true;
    }

    /// <summary>
    /// 直接往指定排摆一个单位,**不扣费、不判容量、不分敌我**(调试与自动化测试用)。
    /// 正式流程请走 DeployUnit —— 这个方法绕过了所有的规则校验,只保证"单位能出现在场上"这一个前提,
    /// 好让"攻击结算""疲劳扣血"这类要有人站在场上才能验的东西能单独验。
    /// </summary>
    public FieldUnit SpawnUnitForDebug(CardData card, BattleRow row, int slotIndex = 0)
        => SpawnUnitForDebug(card, row, row != null ? row.Side : BattleSide.Player, slotIndex);

    /// <summary>调试生成:阵营可以显式指定(共享前军上要摆敌方单位时用)</summary>
    public FieldUnit SpawnUnitForDebug(CardData card, BattleRow row, BattleSide side, int slotIndex = 0)
    {
        if (card == null || row == null) return null;

        var unit = SpawnUnit(card, row, Mathf.Clamp(slotIndex, 0, row.Units.Count), side);
        if (unit != null)
        {
            // 调试生成的不是"本回合部署"的:否则没有「闪击」的单位摆上去就动不了,验不了移动/攻击
            unit.GrantFullAp();
            row.RebuildLayout();
            RefreshRaidFlags();
            Debug.Log($"[Battlefield] 调试生成「{card.cardName}」({BattleRules.SideName(side)})→ {row.DisplayName}");
        }
        return unit;
    }

    /// <summary>
    /// 部署增益(策划案§4.3「支援类的价值主要来自部署时给友方提供的增益」)与「召唤」类部署效果。
    /// 增益要指定友方目标,现在是自动挑一个最合适的并飘字说明 —— 手动选目标还没做。
    /// </summary>
    private void ShowDeployEffect(CardData card, FieldUnit deployed, BattleRow row)
    {
        if (card == null || deployed == null) return;

        // 部署增益按**落位单位自己的阵营**算(不能用 row.Side:共享前军的 row.Side 恒为 Player)
        var caster = new CardEffectResolver.Caster(deployed.Side);
        string text = CardEffectResolver.ResolveOnDeploy(card, caster, deployed);
        if (string.IsNullOrEmpty(text)) return;

        Vector3 screen = ScreenCenterOf(deployed.transform.position);
        FloatingTipUI.Show(screen, text);
    }

    private CardPlayResult TryPlayTactic(CardDisplay handCard, CardData card, PointerEventData pointer, out string message)
    {
        message = null;

        // 无目标(抽卡过牌、随机目标):拖离手牌区就算释放
        if (!card.RequiresTarget)
            return ReleaseTactic(handCard, card, null, null, out message);

        BattleRow rowTarget = null;
        FieldUnit unitTarget = null;
        BattleRow firstRow = null;
        FieldUnit firstUnit = null;

        for (int i = 0; i < hitBuffer.Count; i++)
        {
            var go = hitBuffer[i].gameObject;
            if (go == null) continue;

            var unit = go.GetComponentInParent<FieldUnit>();
            if (unit != null)
            {
                if (firstUnit == null) firstUnit = unit;
                if (unitTarget == null && BattleRules.CanTargetUnit(card, unit, out _))
                {
                    unitTarget = unit;
                    rowTarget = unit.Row;
                    break;
                }
            }

            var row = go.GetComponentInParent<BattleRow>();
            if (row != null)
            {
                if (firstRow == null) firstRow = row;
                if (rowTarget == null && BattleRules.CanTargetRow(card, row, out _))
                {
                    rowTarget = row;
                    break;
                }
            }
        }

        if (unitTarget == null && rowTarget == null)
        {
            // 弃置敌牌类:目标是敌方手牌区
            if (card.targetType == TacticTargetType.EnemyHand && IsInsideEnemyHandZone(pointer.position))
                return ReleaseTactic(handCard, card, null, null, out message);

            message = $"需要指定目标：{BattleRules.TargetTypeName(card.targetType)}";

            // 命中了东西但类型不对 → 说清理由,顺便红闪一下那条排
            if (firstUnit != null && BattleRules.CanTargetUnit(card, firstUnit, out string unitReason) == false
                && !string.IsNullOrEmpty(unitReason) && unitReason != "这张牌不需要指定目标")
            {
                message = unitReason;
            }
            else if (firstRow != null && BattleRules.CanTargetRow(card, firstRow, out string rowReason) == false
                     && !string.IsNullOrEmpty(rowReason) && rowReason != "这张牌不能以整排为目标")
            {
                message = rowReason;
            }

            var flashRow = rowTarget ?? firstRow;
            if (flashRow == null && firstUnit != null) flashRow = firstUnit.Row;
            if (flashRow != null) flashRow.FlashInvalid();

            return CardPlayResult.Failed;
        }

        return ReleaseTactic(handCard, card, unitTarget, rowTarget, out message);
    }

    private CardPlayResult ReleaseTactic(CardDisplay handCard, CardData card, FieldUnit unitTarget, BattleRow rowTarget, out string message)
    {
        message = null;

        var caster = new CardEffectResolver.Caster(BattleSide.Player);

        // §7.2.1 顺序:先判效果能不能落地,再扣费、再让牌退场 —— 顺序反了就会出现"费扣了、牌没了、效果空放"
        if (!CardEffectResolver.Resolve(card, caster, unitTarget, rowTarget, out string result))
        {
            message = result;
            return CardPlayResult.Failed;
        }

        var cp = CommandPointController.Instance;
        if (cp != null && !cp.TrySpend(card.deploymentCost))
        {
            message = "指挥点不足";
            return CardPlayResult.Failed;
        }

        ConsumeHandCard(handCard);      // §7.2.1 第 4 步:策略卡结算后退场(本局不再可用)
        BattleSettlement.CountTacticPlayed(BattleSide.Player);

        string targetText = unitTarget != null ? unitTarget.DisplayName
                          : rowTarget != null ? rowTarget.DisplayName
                          : "无目标";
        message = result;
        Debug.Log($"[Battlefield] 释放策略卡「{card.cardName}」(部署费 {card.deploymentCost})→ {targetText}。{result}");

        // 结算可能打死人 / 拆掉建筑 / 打空大营,场面账要重算一遍
        RefreshRaidFlags();
        BoardChanged?.Invoke();

        Vector3 screen = unitTarget != null ? ScreenCenterOf(unitTarget.transform.position)
                       : new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);
        FloatingTipUI.Show(screen, result);

        return CardPlayResult.Released;
    }

    /// <summary>
    /// 敌方(或任何一方)释放策略卡:AI 走这条,与玩家点击走的是同一套结算
    /// (策划案§6.7:AI 不直接操作 UI,所有行动通过公共 API 完成,避免"AI 开挂")
    /// </summary>
    public bool PlayTactic(CardData card, BattleSide casterSide, FieldUnit unitTarget, BattleRow rowTarget, out string message)
    {
        message = null;
        if (card == null || card.cardType != CardType.Tactic) { message = "不是策略卡"; return false; }
        if (BattleSettlement.MatchOver) { message = "对局已经结束"; return false; }

        var caster = new CardEffectResolver.Caster(casterSide);

        if (!CardEffectResolver.Resolve(card, caster, unitTarget, rowTarget, out string result))
        {
            message = result;
            return false;
        }

        if (casterSide == BattleSide.Enemy)
        {
            var enemy = EnemyDeckController.Instance;
            if (enemy != null && !enemy.TrySpend(card.deploymentCost)) { message = "敌方指挥点不足"; return false; }
            enemy?.PlayCard(card);
        }
        else
        {
            var cp = CommandPointController.Instance;
            if (cp != null && !cp.TrySpend(card.deploymentCost)) { message = "指挥点不足"; return false; }
            FindObjectOfType<DeckController>()?.PlayCard(card);
        }

        BattleSettlement.CountTacticPlayed(casterSide);
        message = result;

        RefreshRaidFlags();
        BoardChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 单位移动(策划案§2.3:只能进相邻的前方排;§7.4:袭扰骑兵可撤回共享前军)。
    /// 消耗 1 AP + 行动费用 CP。玩家点击与 AI 都走这一条。
    /// </summary>
    public bool MoveUnit(FieldUnit unit, BattleRowType targetType, out string message)
    {
        message = null;
        if (unit == null) { message = "没有要移动的单位"; return false; }
        if (BattleSettlement.MatchOver) { message = "对局已经结束"; return false; }
        if (!IsSideAllowedToAct(unit.Side)) { message = "现在是对方的回合"; return false; }

        var from = unit.Row;
        var carrier = ResolveMoveRow(unit, targetType);
        if (carrier == null) { message = "战场上没有这条排"; return false; }

        if (!BattleRules.CanMoveTo(unit, from, targetType, carrier, out message, out bool isRetreat)) return false;

        // 容量判定放在扣 AP 之前:进不去的排不该吃掉行动点
        if (!isRetreat && carrier.IsUnitCapacityFull)
        {
            message = $"{carrier.DisplayName}已满，进不去";
            return false;
        }

        // 顺序说明:和攻击一样,先判合法/容量,再扣费,最后真正换排。
        // 换排只剩"元数据搬运",不会再失败 —— 所以这里不需要攻击那种回滚。
        if (!SpendAction(unit, out message)) return false;

        // 入排:贴到这条排链的末尾(§2.3 无格位模型下,位置只影响「守护」的相邻判定)
        int slot = carrier.Units.Count;
        from.RemoveUnit(unit);
        from.RebuildLayout();

        carrier.InsertSlot(unit, slot);
        carrier.AddUnit(unit, slot);
        unit.SetRow(carrier);

        if (isRetreat)
        {
            unit.ExitRaid();        // §7.4:撤回时袭扰状态与 ATK 加成一并清除
            Debug.Log($"[Battlefield] 「{unit.DisplayName}」撤回 {carrier.DisplayName},袭扰状态解除");
        }
        else if (targetType == BattleRowType.Mid && carrier.Side != unit.Side)
        {
            // §7.4:踏进敌方中军 = 袭扰。把当前回合数记进去,用来判"刚进来的这个回合不许撤回"
            var turn = TurnController.Instance;
            unit.EnterRaid(turn != null ? turn.RoundNumber : 1);
            Debug.Log($"[Battlefield] 骑兵「{unit.DisplayName}」进入 {carrier.DisplayName},进入袭扰状态");
        }

        carrier.RebuildLayout();
        RefreshRaidFlags();
        BoardChanged?.Invoke();

        message = isRetreat
            ? $"「{unit.DisplayName}」撤回 {carrier.DisplayName}"
            : $"「{unit.DisplayName}」移动到 {carrier.DisplayName}";
        return true;
    }

    /// <summary>
    /// 单位攻击(§7.1):消耗 1 AP + 行动费用 CP,伤害与反击在 BattleSettlement 里算。
    ///
    /// 顺序是「先判能不能打 → 先结算 → 再扣费」,扣费万一失败就把伤害原样回滚。
    /// 反过来写(先扣费再结算)会留下一个坑:结算一旦被拒,玩家就白付了 AP 和 CP,
    /// 表现成「只扣费用、没有伤害」—— 这个 bug 在真机上很难复现、极难定位,所以宁可在这一层多做一次回滚。
    /// </summary>
    public bool AttackUnit(FieldUnit attacker, FieldUnit target, out string message)
    {
        message = null;
        if (BattleSettlement.MatchOver) { message = "对局已经结束"; return false; }
        if (attacker != null && !IsSideAllowedToAct(attacker.Side)) { message = "现在是对方的回合"; return false; }

        if (!BattleRules.CanAttack(attacker, target, out message)) return false;

        int targetHpBefore = target.Hp;
        int attackerHpBefore = attacker.Hp;

        if (!BattleSettlement.Attack(attacker, target, out message))
        {
            message = message ?? "攻击失败";
            return false;
        }

        if (!SpendAction(attacker, out message))
        {
            // 付不起就当作没打:把两边血量原样还回去(建筑被毁/单位阵亡这种大改还是得重载场景,极罕见)
            target.SetHp(targetHpBefore);
            attacker.SetHp(attackerHpBefore);
            message = $"{message}（本次攻击已回滚）";
            Debug.LogWarning($"[Battlefield] 「{attacker.DisplayName}」攻击「{target.DisplayName}」后扣费失败," +
                             $"已回滚双方血量:{message}");
            return false;
        }

        RefreshRaidFlags();
        BoardChanged?.Invoke();
        return true;
    }

    /// <summary>这一方现在能不能行动(轮流回合:只有轮到自己时才能出牌、移动、攻击)</summary>
    public bool IsSideAllowedToAct(BattleSide side)
    {
        var turn = TurnController.Instance;
        if (turn == null || !turn.HasStarted) return true;      // 回合还没开始(纯逻辑测试)时不拦

        return turn.IsLocalTurn == (side == BattleSide.Player);
    }

    /// <summary>
    /// 扣一次行动的代价:1 AP + 该单位的行动费用 CP(§2.5)。任一项不够就整体不生效。
    /// 先检查再扣,所以「检查通过、扣款失败」只可能发生在外部同时改了费用池的情况下。
    /// </summary>
    private bool SpendAction(FieldUnit unit, out string message)
    {
        message = null;
        if (unit == null) { message = "没有可行动的单位"; return false; }
        if (unit.Ap <= 0) { message = "行动力已耗尽"; return false; }

        int cpCost = BattleRules.ActionCostOf(unit.Data);

        if (unit.Side == BattleSide.Player)
        {
            var cp = CommandPointController.Instance;
            if (cp != null && !cp.CanAfford(cpCost)) { message = $"指挥点不足（行动费用 {cpCost}）"; return false; }
        }
        else
        {
            var enemy = EnemyDeckController.Instance;
            if (enemy != null && !enemy.CanAfford(cpCost)) { message = $"敌方指挥点不足（行动费用 {cpCost}）"; return false; }
        }

        if (!unit.SpendActionPoint()) { message = "行动力已耗尽"; return false; }

        if (unit.Side == BattleSide.Player)
        {
            var cp = CommandPointController.Instance;
            if (cp != null && !cp.TrySpend(cpCost)) { message = "指挥点不足"; return false; }
        }
        else
        {
            var enemy = EnemyDeckController.Instance;
            if (enemy != null && !enemy.TrySpend(cpCost)) { message = "敌方指挥点不足"; return false; }
        }

        return true;
    }

    /// <summary>世界坐标(战场成员都是画布下的 UI 物体)转屏幕坐标,飘字定位用</summary>
    private static Vector3 ScreenCenterOf(Vector3 worldPosition)
    {
        var canvas = CanvasUtil.FindRootCanvas();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        return camera != null ? camera.WorldToScreenPoint(worldPosition)
                              : RectTransformUtility.WorldToScreenPoint(null, worldPosition);
    }

    // ================================================================ 落位

    /// <summary>
    /// 生成战场小卡并登记进排里。
    ///
    /// 两层结构:排 → **布局格子**(105×140,链上第几位就是第几个格子) → 卡面(150×200 的设计尺寸,
    /// 整体缩 0.7)。位置/间距全部交给排上的 HorizontalLayoutGroup(它按格子的 sizeDelta 排),
    /// 这里只给格子的尺寸和 sibling 顺序。
    /// </summary>
    private FieldUnit SpawnUnit(CardData card, BattleRow row, int siblingIndex, BattleSide side)
    {
        var prefab = ResolveCardPrefab();
        if (prefab == null || row == null) return null;

        var slot = CreateSlot(row, siblingIndex, "Unit_" + card.cardId);

        var view = Instantiate(prefab, slot);
        view.name = "Unit_" + card.cardId;
        view.Bind(card, CardViewMode.Field);

        // 战场小卡:悬停预览默认关掉(拖着牌从上面路过时容易误弹),手牌拖动组件也不能生效
        var hover = view.GetComponent<CardHover>();
        if (hover != null) hover.enabled = allowFieldHoverPreview;

        var drag = view.GetComponent<CardDragPlay>();
        if (drag != null) drag.enabled = false;

        var cardRect = (RectTransform)view.transform;
        ResizeSlot(slot, cardRect);      // 格子 = 卡面视觉尺寸(150×200 × 0.7 = 105×140)
        FitInSlot(cardRect);             // 卡面摆在格子正中,整体等比缩小

        // 成员挂在格子上:排的链顺序 = 格子顺序,命中卡面时 GetComponentInParent 也能找到它
        // 阵营由调用方给死,不从 row.Side 推 —— 前军是共享排(见 FieldUnit.Init 的说明)
        var unit = slot.gameObject.AddComponent<FieldUnit>();
        unit.Init(card, row, side, view);

        // 拖动移动的指针转发器:挂在兵牌自己身上,这样"按住 → 拖到某条排"一定落在这里
        // (建筑不挂 —— 建筑不能移动)
        slot.gameObject.AddComponent<FieldUnitDragProxy>();

        // 战斗数值落位时定档:重甲层数(§4.3)与军械库被毁后的 ATK -1 Debuff(§2.3)
        unit.SetHeavyArmor(BattleRules.HeavyArmorLayers(card));
        ApplyArsenalPenalty(unit);

        row.AddUnit(unit, siblingIndex);
        return unit;
    }

    /// <summary>生成一座建筑锚点(§2.3):它占容量、不可移动、不可被替换</summary>
    private FieldUnit SpawnBuilding(BuildingSpec spec, BattleRow row)
    {
        var prefab = ResolveBuildPrefab();
        if (prefab == null || spec == null || row == null) return null;

        var slot = CreateSlot(row, row.MemberCount, "Slot_" + spec.displayName);

        var go = Instantiate(prefab, slot);
        go.name = "Building_" + spec.displayName;

        var buildRect = (RectTransform)go.transform;
        ResizeSlot(slot, buildRect);
        FitInSlot(buildRect);

        var unit = slot.gameObject.AddComponent<FieldUnit>();
        unit.InitAsBuilding(spec.displayName, spec.hp, row, spec.side);
        row.AddUnit(unit, row.MemberCount);
        return unit;
    }

    /// <summary>
    /// 建一个布局格子。排上的 HorizontalLayoutGroup 按格子的 sizeDelta 排链,
    /// 内容装在格子里再整体缩放 —— 这样布局只认"格子"这一个尺寸,
    /// 卡面的字号/插画/角标一起等比缩小,不会被 rect 拉变形。
    /// </summary>
    private RectTransform CreateSlot(BattleRow row, int siblingIndex, string objectName)
    {
        var go = new GameObject(objectName, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(row.transform, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);    // 布局组随后会写成 (0,1)
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(105f, 140f);                   // 兜底值,紧接着会被 ResizeSlot 覆盖
        rt.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, row.transform.childCount - 1));
        return rt;
    }

    /// <summary>格子尺寸 = 内容的设计尺寸 × fieldCardScale(150×200 × 0.7 = 105×140)</summary>
    private void ResizeSlot(RectTransform slot, RectTransform content)
    {
        var design = content != null ? content.rect.size : Vector2.zero;
        if (design.x <= 0f || design.y <= 0f) design = new Vector2(150f, 200f);
        slot.sizeDelta = design * fieldCardScale;
    }

    /// <summary>把内容摆进格子正中:保持预制体的设计尺寸,整体等比缩放填满格子</summary>
    private void FitInSlot(RectTransform content)
    {
        if (content == null) return;
        content.anchorMin = content.anchorMax = new Vector2(0.5f, 0.5f);
        content.pivot = new Vector2(0.5f, 0.5f);
        content.anchoredPosition = Vector2.zero;
        content.localRotation = Quaternion.identity;
        content.localScale = Vector3.one * fieldCardScale;
    }

    /// <summary>
    /// 新成员插在链上的第几位(§2.3:绕排内既有成员左右贴放)。
    ///
    /// 按**指针在排里的横坐标**定位:停在某个成员中线的左边 → 插在它前面,右边 → 插在它后面;
    /// 停在链外的空白处就接链尾。所以"落在这条排的哪个位置"就是"插在链上哪个位置" ——
    /// 不必非要点中某个成员,想贴在大营左边就往它左半边放。
    /// 返回的既是 sibling 下标也是成员名单下标(排里只有"一个成员一个格子"这一种子物体)。
    /// </summary>
    private static int ResolveInsertIndex(BattleRow row, PointerEventData pointer)
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

    // ================================================================ 落点竖条

    /// <summary>
    /// 拖动兵种牌时,在"松手会插进去的那个缝"上亮一根竖条。
    /// 部署位置是按指针横坐标决定的,不给提示就等于闭着眼睛放 —— 有了它,
    /// 想贴在大营左边就把竖条对到大营左边,松手就是那个位置。
    /// </summary>
    private void ShowInsertMarker(BattleRow row, int index)
    {
        if (insertMarkerWidth <= 0f) { HideInsertMarker(); return; }

        var marker = GetInsertMarker(row);
        if (marker == null) { HideInsertMarker(); return; }

        if (activeInsertMarker != null && activeInsertMarker != marker)
            activeInsertMarker.gameObject.SetActive(false);

        var rt = (RectTransform)marker.transform;
        rt.anchoredPosition = new Vector2(InsertMarkerX(row, index), rt.anchoredPosition.y);
        marker.color = insertMarkerColor;
        marker.gameObject.SetActive(true);
        activeInsertMarker = marker;
    }

    private void HideInsertMarker()
    {
        if (activeInsertMarker != null) activeInsertMarker.gameObject.SetActive(false);
        activeInsertMarker = null;
    }

    /// <summary>竖条的横坐标 = 插入位置那个缝的中心(排里还没成员就标在排中间)</summary>
    private float InsertMarkerX(BattleRow row, int index)
    {
        var rowRect = row.transform as RectTransform;
        if (rowRect == null) return 0f;

        var members = row.Units;
        if (members.Count == 0) return rowRect.rect.width * 0.5f;

        var first = members[0] != null ? members[0].transform as RectTransform : null;
        float cell = first != null && first.rect.width > 0f ? first.rect.width : 105f;
        float halfCellAndGap = cell * 0.5f + memberGap * 0.5f;

        if (index <= 0) return MemberCenterX(members[0]) - halfCellAndGap;
        if (index >= members.Count) return MemberCenterX(members[members.Count - 1]) + halfCellAndGap;

        return (MemberCenterX(members[index - 1]) + MemberCenterX(members[index])) * 0.5f;
    }

    /// <summary>成员格子的中心横坐标(格子的锚点在排左上,所以 anchoredPosition.x 就是中心)</summary>
    private static float MemberCenterX(FieldUnit unit)
    {
        var rt = unit != null ? unit.transform as RectTransform : null;
        return rt != null ? rt.anchoredPosition.x : 0f;
    }

    /// <summary>每条排一根竖条,用到才建:和成员格子共用一套坐标(锚在排左上),高度 = 排高 - 4</summary>
    private Image GetInsertMarker(BattleRow row)
    {
        if (row == null) return null;
        if (insertMarkers.TryGetValue(row, out var cached) && cached != null) return cached;

        var rowRect = row.transform as RectTransform;
        if (rowRect == null) return null;

        var go = new GameObject("InsertMarker", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        var rt = (RectTransform)go.transform;
        rt.SetParent(row.transform, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(Mathf.Max(1f, insertMarkerWidth), Mathf.Max(1f, rowRect.sizeDelta.y - 4f));
        rt.anchoredPosition = new Vector2(0f, -rowRect.sizeDelta.y * 0.5f);

        var image = go.GetComponent<Image>();
        image.color = insertMarkerColor;
        image.raycastTarget = false;                                   // 别挡指针命中

        // 关键:它是排的子物体,但绝不能被排的布局组当成一个成员、更不能撑动行高
        go.GetComponent<LayoutElement>().ignoreLayout = true;

        go.SetActive(false);
        insertMarkers[row] = image;
        return image;
    }

    /// <summary>
    /// 打出去的手牌退场。两边都要动:
    /// · 数据层(DeckController 的手牌账)—— 不扣的话打出的牌还算在手牌里,摸牌会被误判成手牌已满;
    /// · 表现层(HandUI)—— 销毁卡牌、让扇形重排。
    /// 顺带广播 CardPlayed(策划案§3.3.2):提示条只报对方出的牌,自己出的会被过滤掉。
    /// </summary>
    private void ConsumeHandCard(CardDisplay handCard)
    {
        var data = handCard != null ? handCard.Data : null;
        if (data != null)
        {
            if (deckController == null) deckController = FindObjectOfType<DeckController>();
            if (deckController != null) deckController.PlayCard(data);
            else Debug.LogWarning("[BattlefieldManager] 找不到 DeckController,打出的牌还留在手牌账里。", this);

            var cp = CommandPointController.Instance;
            EventManager.Trigger(new CardPlayedEventArgs
            {
                Card = data,
                Player = cp != null ? cp.LocalPlayer : null,
            });
        }

        if (handUI == null) handUI = FindObjectOfType<HandUI>();
        if (handUI != null) handUI.RemoveCard(handCard);
        else Debug.LogWarning("[BattlefieldManager] 找不到 HandUI,打出去的手牌没能从手牌区移除。", this);
    }

    // ================================================================ 指针命中

    private void RaycastHits(PointerEventData pointer, CardDisplay ignore)
    {
        hitBuffer.Clear();
        if (pointer == null || EventSystem.current == null) return;

        EventSystem.current.RaycastAll(pointer, hitBuffer);

        // 拖着的这张牌一直跟着指针,射线第一个命中的就是它 —— 剔掉
        if (ignore == null) return;
        for (int i = hitBuffer.Count - 1; i >= 0; i--)
        {
            var go = hitBuffer[i].gameObject;
            if (go == null || go.transform.IsChildOf(ignore.transform) || go == ignore.gameObject)
                hitBuffer.RemoveAt(i);
        }
    }

    private bool IsInsideHandArea(Vector2 screenPosition)
    {
        if (handUI == null) handUI = FindObjectOfType<HandUI>();
        var rect = handUI != null ? handUI.HandRect : null;
        if (rect == null) return false;
        return RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, EventCameraFor(rect));
    }

    private bool IsInsideEnemyHandZone(Vector2 screenPosition)
    {
        if (enemyHandZone == null) enemyHandZone = FindRect(EnemyHandZoneName);
        if (enemyHandZone == null) return false;
        return RectTransformUtility.RectangleContainsScreenPoint(enemyHandZone, screenPosition, EventCameraFor(enemyHandZone));
    }

    // ================================================================ 调试

    [Header("调试")]
    [Tooltip("按一下切换「己方中军里驻有敌方袭扰骑兵」——\n" +
             "袭扰机制(§7.4)还没做,所以策划案§8.1.1 的「中军满员时可以改部署进后军」这条例外\n" +
             "平时永远不成立。想试后军部署就按这个键,再把中军塞满")]
    [SerializeField] private KeyCode debugRaiderToggleKey = KeyCode.F9;

    private void Update()
    {
        if (debugRaiderToggleKey != KeyCode.None && Input.GetKeyDown(debugRaiderToggleKey)) ToggleDebugRaider();
    }

    private void ToggleDebugRaider()
    {
        if (playerMid == null) return;

        playerMid.HasEnemyRaider = !playerMid.HasEnemyRaider;
        Debug.Log($"[Battlefield] 调试:己方中军「驻有敌方袭扰骑兵」= {playerMid.HasEnemyRaider}" +
                  "(中军单位容量满 + 这个为真,后军才允许部署)");

        BoardChanged?.Invoke();     // 手牌重新算一遍还能不能出
    }

    [ContextMenu("把 5 条排按名字重新认领一遍")]
    private void RebindRows()
    {
        playerBack = playerMid = playerFront = enemyMid = enemyBack = null;
        enemyHandZone = null;
        handUI = null;
        ResolveRefs();
    }
}
