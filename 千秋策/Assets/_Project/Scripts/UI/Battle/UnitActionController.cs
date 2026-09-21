using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 我方单位的操作入口,**只有"拖动"这一个动作手势**:
///   · **拖动**自己的兵牌 → 落在一条排上 = 移动过去(合法的排亮绿、不合法的亮红);
///   · **拖动**自己的兵牌 → 落在一个敌人身上 = 攻击那个敌人(能打的目标套红框)。
/// 为什么不再用"点选 + 点目标":点(Update 里判按下)和拖动(EventSystem 判位移)是两套独立的输入,
/// 同一次按压里状态会互相覆盖 —— 长按的时候点已经触发了、拖动又开始了,于是出现"点了没反应"
/// 或者"拖到一半动作已经发出去了"。统一成拖动之后,一次按压只会有一个结局。
/// 单击仍然保留,**但只做选中**(亮金框 + 显示能打谁),不发出任何消耗 AP 的动作。
///
/// 单位能做什么全部由 BattleRules 判定,真正结算走 BattlefieldManager.MoveUnit / AttackUnit(和 AI 同一条路径):
///   · 攻击 = 消耗 1 AP + 该单位的行动费用 CP(§2.5),被守护的目标点不了(§4.3);
///   · 移动 = 只能进相邻的前方排;袭扰骑兵是唯一能撤回前军的单位(§7.4)。
/// 数字键 1/2/3 仍然保留了同一套移动(键盘党/无鼠标调试用),和拖动走的是同一个 MoveUnit。
///
/// 【挂载 & 调整】
///   挂在:Battle.unity 里的 BattleCanvas 上(和 TurnController / CommandPointController 同一个物体)。
///         没挂也能跑:Instance 会先全场景找,找不到就在第一个 Canvas 下现建一个 —— 那样 Inspector 上的值都是默认值。
///   引用:没有要手连的引用。战场成员从 BattlefieldManager.Instance 拿,选中框/红框/拖动标记都是运行时建的。
///   常调:
///     · enabled:整个组件就是"玩家能不能操作单位"的开关,关掉 = 只能出牌、不能移动/攻击(想临时锁操作就关它)。
///     · dragThreshold:超过多少像素才算"拖动"。调小 = 手一抖就变拖动(容易误发动作);调大 = 要拖得更明确。
///     · debugLog:把每次拖动/移动/攻击的判定过程打到 Console,调"拖不动"这类问题时打开。
///     · highlightPadding:选中框相对卡面放大多少。
/// </summary>
[DisallowMultipleComponent]
public class UnitActionController : MonoBehaviour
{
    private static UnitActionController instance;

    public static UnitActionController Instance
    {
        get
        {
            if (instance != null) return instance;
            instance = FindObjectOfType<UnitActionController>();
            if (instance != null) return instance;

            var go = new GameObject("UnitActionController");
            var canvas = CanvasUtil.FindRootCanvas();
            if (canvas != null) go.transform.SetParent(canvas.transform, false);
            instance = go.AddComponent<UnitActionController>();
            return instance;
        }
    }

    [Header("开关")]
    [Tooltip("关掉 = 玩家不能移动/攻击单位(只能出牌)。临时锁操作、做动画时用")]
    [SerializeField] private bool allowActions = true;

    [Header("拖动")]
    [Tooltip("指针移动超过多少像素才算「拖动」(以内算点击选中)。太小容易误移动,太大拖起来费劲")]
    [SerializeField] private float dragThreshold = 12f;
    [Tooltip("拖动时跟着指针的那个半透明方块的颜色")]
    [SerializeField] private Color dragMarkerColor = new(1f, 0.85f, 0.35f, 0.35f);

    [Header("选中框")]
    [Tooltip("选中框相对卡面放大多少(1 = 正好贴住卡面)")]
    [SerializeField] private float highlightPadding = 1.12f;

    [Header("调试")]
    [Tooltip("把每次点选/拖动的判定过程打到 Console(调「点不动」「拖不动」这类问题时打开)")]
    [SerializeField] private bool debugLog = false;

    private FieldUnit selected;
    private RectTransform highlight;

    // ---- 拖动状态 ----
    private FieldUnit dragUnit;          // 正在被拖的单位(拖动真正开始后才有值)

    // 这次拖动有没有向 BattlefieldManager 登记过(登记了就一定要还回去,见 ClearDragVisuals / OnDisable)
    private bool dragCounted;
    private FieldUnit pressedUnit;       // 按下的单位(可能只是点一下,还没到拖动阈值)
    private BattleRow hoveredRow;        // 指针现在停在哪条排上
    private FieldUnit hoveredTarget;     // 指针现在停在哪个"打得到的敌人"上(拖动时,攻击优先于移动)
    private Vector2 pressPosition;
    private RectTransform dragMarker;    // 跟着指针的半透明方块

    // 攻击指向箭头:拖动时指针压在能打的敌人身上,就把"卡牌跟手"换成一根指向目标的箭头。
    // 理由:攻击不涉及同时移动,让卡牌跟着手走会让人以为这一下是"把卡挪过去";
    // 箭头只表达一件事 —— 松手打它。表里的顺序完全是"我方在上、敌方在下"决定的。
    private RectTransform attackArrowHead;
    private RectTransform attackArrowShaft;
    private bool attackArrowVisible;

    // 攻击指向箭头的贴图(全部代码生成,不依赖美术资源)
    private static Sprite arrowHeadSprite;
    private static Sprite arrowShaftSprite;

    /// <summary>选中单位时给"打得到的目标"套的红框(复用同一个池子,不每次新建)</summary>
    private readonly List<RectTransform> targetFrames = new();

    /// <summary>拖动时"能落到哪条排"的绿底 —— 记下来,结束时还回去</summary>
    private readonly List<BattleRow> validDropRows = new();

    private const string TargetFrameName = "AttackableTargetFrame";

    /// <summary>攻击指向箭头的箭头尖(画在目标那一侧)</summary>
    private const string AttackArrowHeadName = "AttackArrowHead";

    /// <summary>攻击指向箭头的箭杆(画在攻击方和目标之间)</summary>
    private const string AttackArrowShaftName = "AttackArrowShaft";

    /// <summary>拖动时卡牌跟手标记的不透明度(攻击指向时几乎透明)</summary>
    private const float DragMarkerAlpha = 0.35f;

    /// <summary>攻击指向时卡牌标记的不透明度。**不能设成 0** —— 0 会让 Graphic 不再参与渲染,
    /// 松手时再淡回来会有一帧闪断。0.12 视觉上等于看不见,但渲染管线一直算着它。</summary>
    private const float DragMarkerAlphaWhileAttacking = 0.12f;

    /// <summary>箭头/箭杆的颜色(亮金,和选中框一个色系)</summary>
    private static readonly Color AttackArrowColor = new(1f, 0.85f, 0.35f, 0.95f);

    /// <summary>箭头尖的长度方向尺寸(指向目标的那个三角)</summary>
    private static readonly Vector2 AttackArrowHeadSize = new(44f, 30f);

    /// <summary>箭头尖离目标中心多远(留出来免得盖住目标卡面)</summary>
    private const float AttackArrowHeadGap = 52f;

    /// <summary>箭杆粗细</summary>
    private const float AttackArrowShaftThickness = 6f;

    /// <summary>起点离箭头尖太近就不画箭杆了,只留一个三角(否则会是一条看不清的小横线)</summary>
    private const float AttackArrowShaftMinLength = 30f;

    /// <summary>攻击目标框的颜色(暗红,和选中框的金色区分开)</summary>
    private static readonly Color TargetFrameColor = new(0.95f, 0.28f, 0.24f, 0.85f);

    /// <summary>指针压在这个目标上时的颜色(亮红:松手就打它)</summary>
    private static readonly Color TargetFrameHoverColor = new(1f, 0.85f, 0.25f, 1f);

    /// <summary>当前选中的单位(null = 没选中)</summary>
    public FieldUnit Selected => selected;

    /// <summary>现在是不是正在拖一个单位</summary>
    public bool IsDraggingUnit => dragUnit != null;

    private void Awake()
    {
        // 只记第一份:GameBootstrap 可能建了一份,场景里又摆了一份 —— 后来的那份让位,
        // 否则 Instance 会指到一个谁也没在用、随时可能被销毁的对象上
        if (instance == null || instance == this) instance = this;
    }

    private void OnDestroy()
    {
        // 只清"自己那一份":场景重载时旧实例的 OnDestroy 可能晚于新实例的 Awake,
        // 无条件置 null 会把新实例的引用一起抹掉,之后 Instance 就只剩一份临时对象了
        if (instance == this) instance = null;
    }

    private void OnDisable()
    {
        // 组件被关掉/场景销毁时别把高亮和标记留在场上。
        // 注意:重载战斗场景时会走到这里,此时**别人**可能已经被销毁了 ——
        // 所以下面只碰"自己建的东西",排上的颜色交给排自己(它要没了也就没颜色可言了)。
        selected = null;
        dragUnit = null;
        pressedUnit = null;
        hoveredRow = null;
        hoveredTarget = null;
        validDropRows.Clear();

        // 拖动中途被禁用:OnUnitEndDrag 不会再来了,把登记还回去,否则计数永远归不了零,
        // 那道"拖动结束必须收干净高亮"的闸门就再也不会生效。
        // 只还计数,不去碰排的颜色 —— 这时 BattlefieldManager 可能已经被销毁了。
        if (dragCounted)
        {
            dragCounted = false;
            BattlefieldManager.Instance?.ForgetDragHighlight();
        }

        if (highlight != null) highlight.gameObject.SetActive(false);
        if (dragMarker != null) dragMarker.gameObject.SetActive(false);

        for (int i = 0; i < targetFrames.Count; i++)
            if (targetFrames[i] != null) targetFrames[i].gameObject.SetActive(false);
    }

    private void Update()
    {
        if (!allowActions)
        {
            if (selected != null) ClearSelection();
            return;
        }

        // 选中的单位随时可能被打死/被销毁(反击、疲劳、对方回合的结算),每帧先确认它还在
        if (selected != null && !IsUsable(selected)) { ClearSelection(); return; }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            CancelDrag();
            ClearSelection();
            return;
        }

        if (!Input.GetMouseButtonDown(0)) return;

        HandleClick(Input.mousePosition);
    }

    // ================================================================ 空引用防护
    //
    // 单位被 Destroy 之后,留在字段里的引用**不是 C# 的 null** —— 它是个"已销毁"的包装对象,
    // 一旦去碰它的 transform / gameObject 就抛 MissingReferenceException。
    // 所以所有拿旧引用去访问 transform 的地方都要先过这一关(不能只写 != null)。

    /// <summary>这个单位现在还能用吗(存在、没被销毁、还活着)。碰它的 transform 之前都要先过这一关</summary>
    private static bool IsUsable(FieldUnit unit) => unit != null && unit.IsAlive;

    /// <summary>安全取单位的 RectTransform,拿不到返回 null</summary>
    private static RectTransform RectOf(FieldUnit unit)
    {
        if (!IsUsable(unit)) return null;
        return unit.transform as RectTransform;
    }

    /// <summary>安全取单位的屏幕坐标(定位飘字用),拿不到就退回屏幕中心</summary>
    private static Vector3 ScreenPositionOf(FieldUnit unit)
    {
        var rect = RectOf(unit);
        if (rect == null) return new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);
        return ScreenCenterOf(rect.position);
    }

    // ================================================================ 点选
    //
    // 单击**只做选中**:亮金框 + 把"现在打得到谁"标出来,不发出任何消耗 AP 的动作。
    // 动作一律由拖动完成(落在排上 = 移动,落在敌人身上 = 攻击)—— 这样一次按压只有一个结局,
    // 不会出现"点也在判、拖也在判,长按时两边互相覆盖"的卡顿。

    private void HandleClick(Vector2 screenPosition)
    {
        var board = BattlefieldManager.Instance;
        if (board == null) return;

        var turn = TurnController.Instance;
        if (BattleSettlement.MatchOver) { ShowTip("对局已经结束", screenPosition); return; }
        if (turn != null && turn.HasStarted && !turn.IsLocalTurn) { ShowTip("现在是对方的回合", screenPosition); return; }

        var unit = PickUnitUnderPointer(screenPosition);

        // 点空白 = 取消选中
        if (unit == null) { ClearSelection(); return; }

        if (debugLog) Debug.Log($"[操作] 点到「{unit.DisplayName}」({BattleRules.SideName(unit.Side)}," +
                                $"{unit.Row?.DisplayName},AP {unit.Ap}/{unit.ApMax})");

        // 点自己的兵牌 = 选中 / 再点一下取消
        if (unit.Side == BattleSide.Player && unit.IsUnit)
        {
            if (selected == unit) { ClearSelection(); return; }
            Select(unit);
            ShowTip($"已选中「{unit.DisplayName}」：拖动它到一条排 = 移动，拖到套红框的敌人身上 = 攻击");
            return;
        }

        // 点到敌人:不在这里攻击(拖动才是动作),只说清楚该怎么打
        ShowTip(selected != null
            ? $"要攻击「{unit.DisplayName}」，请把「{selected.DisplayName}」拖到它身上"
            : $"先按住自己的兵牌选中，再把它拖到「{unit.DisplayName}」身上攻击");
    }

    /// <summary>用 UI 射线找出指针底下的战场成员(卡面 → 格子 → FieldUnit)</summary>
    private FieldUnit PickUnitUnderPointer(Vector2 screenPosition)
    {
        var results = RaycastAll(screenPosition);
        for (int i = 0; i < results.Count; i++)
        {
            var go = results[i].gameObject;
            if (go == null) continue;

            var unit = go.GetComponentInParent<FieldUnit>();
            if (unit != null && unit.IsAlive) return unit;
        }

        return null;
    }

    /// <summary>用 UI 射线找出指针底下是哪条排(排的底色 Image 是可命中的,手牌拖动也靠它)</summary>
    private BattleRow PickRowUnderPointer(Vector2 screenPosition)
    {
        var results = RaycastAll(screenPosition);
        for (int i = 0; i < results.Count; i++)
        {
            var go = results[i].gameObject;
            if (go == null) continue;

            var row = go.GetComponentInParent<BattleRow>();
            if (row != null) return row;
        }

        return null;
    }

    private static readonly List<RaycastResult> raycastBuffer = new();

    private static List<RaycastResult> RaycastAll(Vector2 screenPosition)
    {
        raycastBuffer.Clear();

        var eventSystem = EventSystem.current;
        if (eventSystem == null) return raycastBuffer;

        var data = new PointerEventData(eventSystem) { position = screenPosition };
        eventSystem.RaycastAll(data, raycastBuffer);
        return raycastBuffer;
    }

    private void Select(FieldUnit unit)
    {
        if (!IsUsable(unit)) return;

        selected = unit;
        MoveHighlight(unit);
        RefreshTargetFrames(unit);
        if (debugLog) Debug.Log($"[操作] 选中「{unit.DisplayName}」");
    }

    private void ClearSelection()
    {
        selected = null;
        // 选中框是挂在单位身上的:单位被销毁时它会跟着一起没,所以这里也要能容忍"已销毁"
        if (highlight != null) highlight.gameObject.SetActive(false);
        HideTargetFrames();
    }

    // ================================================================ 拖动移动(§2.3)
    //
    // 入口有三个,都从 FieldUnitDragProxy 转过来(它挂在兵牌自己身上,见那个文件的说明):
    // 把"哪个单位"直接传进来,不用再靠射线去猜指针底下是什么 —— 拖动的第一步就不会出错。

    /// <summary>
    /// 按下一个兵牌。**这里记下的东西只用来算"位移够不够判定成拖动",不是拖动的必要条件** ——
    /// 战场卡面自己带 Canvas + GraphicRaycaster,而 CardHover 挂在卡面上、实现了 IPointerDownHandler,
    /// EventSystem 找处理者时"沿父链取第一个",按下会停在卡面的 CardHover 上,到不了兵牌格子上的
    /// FieldUnitDragProxy。所以这个方法在战场上**通常根本不会被调用**(详见 FieldUnitDragProxy 的说明),
    /// 拖动判定一律以 OnUnitBeginDrag 为准。
    /// </summary>
    public void OnUnitPointerDown(FieldUnit unit, PointerEventData eventData)
    {
        if (!allowActions || unit == null) return;

        pressPosition = eventData != null ? eventData.position : (Vector2)Input.mousePosition;

        // 只有我方兵牌能拖(敌方单位、建筑都不可拖)
        if (unit.Side != BattleSide.Player || !unit.IsUnit) { pressedUnit = null; return; }

        pressedUnit = unit;
        if (debugLog) Debug.Log($"[操作] 按住「{unit.DisplayName}」(AP {unit.Ap}/{unit.ApMax})");
    }

    /// <summary>拖动开始:超过阈值才算拖动。**没超过就当点击**(只选中,不发动作)</summary>
    public void OnUnitBeginDrag(FieldUnit unit, PointerEventData eventData)
    {
        if (!allowActions) return;
        if (unit == null || !IsUsable(unit)) return;

        // 只接管我方兵牌(敌方单位、建筑都不可拖)
        if (unit.Side != BattleSide.Player || !unit.IsUnit) return;

        // "位移够不够判定成拖动"用**拖动开始这一刻**的指针位置算,不再依赖 PointerDown 记下的 pressPosition。
        // 原因:按下事件不一定能到兵牌格子上(卡面自己有个 IPointerDownHandler 时会先接走),
        // 那条路一旦断,pressPosition 就一直是零值,拖动会被误判成"没动过"而直接取消 —— 表现成"拖不起来"。
        // 指针已经越过阈值 EventSystem 才会调到这里,所以"够不够拖动"这件事其实已经由它保证了。
        if (eventData != null && pressPosition != Vector2.zero)
        {
            float moved = Vector2.Distance(eventData.position, pressPosition);
            if (moved < dragThreshold) { pressedUnit = null; return; }
        }

        pressedUnit = unit;
        dragUnit = unit;
        selected = unit;                       // 拖动中保持选中,松手后还看得见金框
        MoveHighlight(unit);
        RefreshTargetFrames(unit);             // 红框一直留着:拖到敌人身上就是打它
        ShowDragMarker(unit);
        HighlightMoveTargets(unit);
        // 向战场登记"现在有东西被拖着" —— 拖完由 ClearDragVisuals 还回去。
        // 这是硬保证:万一哪条路径忘了清高亮,计数归零时 BattlefieldManager 会强制收干净。
        BattlefieldManager.Instance?.BeginDragHighlight();
        dragCounted = true;

        if (debugLog) Debug.Log($"[操作] 开始拖动「{unit.DisplayName}」");
    }

    /// <summary>
    /// 拖动中:指针底下是"能打的目标"就单独亮那个目标(攻击),否则给排上色(移动)。
    /// 一次拖动只会有一个结局:落在敌人身上 = 攻击,落在排上 = 移动。
    /// </summary>
    public void OnUnitDrag(FieldUnit unit, PointerEventData eventData)
    {
        if (dragUnit == null || eventData == null) return;

        // 拖到一半单位没了(反击/疲劳结算):直接把拖动收尾,别继续拿旧引用做判定
        if (!IsUsable(dragUnit)) { CancelDrag(); return; }

        MoveDragMarker(eventData.position);

        // 指针底下是不是一个"现在打得到"的敌人?(排只取一次射线,给攻击判定和落点高亮共用)
        var hoverRow = PickRowUnderPointer(eventData.position);
        var target = AttackTargetUnderPointer(dragUnit, eventData.position, hoverRow);

        // ★ 这一段只在"指针刚进/刚出某个目标"这一帧执行。
        //   原来飘字写在下面 if (hoveredTarget != null) 里面,而那个分支**每帧**都进 ——
        //   FloatingTipUI.Show 每次都 new 一个 GameObject,等于悬停在敌人身上时每秒创建 60 个飘字对象,
        //   刷屏的同时还在持续分配。放到这个"状态变化"分支里才是正确的位置。
        if (target != hoveredTarget)
        {
            SetTargetFrameEmphasis(hoveredTarget, false);
            hoveredTarget = target;
            SetTargetFrameEmphasis(hoveredTarget, true);

            if (hoveredTarget != null) ShowTip($"松手攻击「{hoveredTarget.DisplayName}」");
        }

        if (hoveredTarget != null)
        {
            // 攻击优先:指针在敌人身上时不再给排上色,免得看不出来这一下会打谁
            RestoreRowHighlight(hoveredRow);
            hoveredRow = null;

            // 攻击指向:卡牌跟手收掉,改画一根指向目标的箭头
            SetDragMarkerMode(attacking: true);
            return;
        }

        // 离开目标(或在排之间移动):把箭头收掉,卡牌跟手恢复
        SetDragMarkerMode(attacking: false);

        var row = hoverRow;
        if (row != hoveredRow)
        {
            RestoreRowHighlight(hoveredRow);
            hoveredRow = row;
        }

        if (hoveredRow == null) return;

        hoveredRow.SetHighlight(CanMoveTo(dragUnit, hoveredRow) ? RowHighlight.Valid : RowHighlight.Invalid);
    }

    /// <summary>松手:落在敌人身上 = 攻击,落在排上 = 移动,落在别处 = 什么都不做</summary>
    public void OnUnitEndDrag(FieldUnit unit, PointerEventData eventData)
    {
        var acting = dragUnit;
        var row = eventData != null ? PickRowUnderPointer(eventData.position) : null;
        var target = acting != null && eventData != null ? AttackTargetUnderPointer(acting, eventData.position) : null;

        ClearDragVisuals();
        dragUnit = null;
        pressedUnit = null;

        if (acting == null) return;
        if (!IsUsable(acting)) { ShowTip("这个单位已经不在场上了"); return; }

        // ---- 结局一:落在敌人身上 → 攻击 ----
        if (target != null) { TryAttack(acting, target); return; }

        if (row == null)
        {
            ShowTip("松手的位置不在任何一条排上，没有移动");
            return;
        }

        // ---- 结局二:落在排上 → 移动 ----
        if (!CanMoveTo(acting, row))
        {
            BattleRules.CanMoveTo(acting, acting.Row, row.RowType, RowCarrierFor(acting, row.RowType),
                                  out string why, out bool wasRetreat);
            // 「已经在这条排上」不算失败,别红闪也别飘警告 —— 就是原地放下而已
            bool sameRow = acting.Row == row && !wasRetreat;

            row.SetHighlight(RowHighlight.None);
            if (!sameRow)
            {
                row.FlashInvalid();
                FloatingTipUI.Show(ScreenPositionOf(acting), why ?? "不能移到这条排", warning: true);
            }
            ShowTip(sameRow ? $"「{acting.DisplayName}」已经在这条排上" : why ?? "不能移到这条排");
            return;
        }

        MoveSelectedTo(acting, row);
    }

    /// <summary>
    /// 指针底下那个"拖动中的单位现在打得到"的敌人(用红框状态判,和界面上看到的完全一致)。
    /// 找不到返回 null —— 那就说明这一下不是攻击,该按移动处理。
    ///
    /// ⚠ 直接射线没命中具体某张卡时,还要**按排补判一次**(见下面那个重载)。
    ///   满员的排里卡是紧挨着挤满的,两张卡之间只剩一条很窄的缝;指针落在缝里、
    ///   或者落在卡与格子的边缘上时,射线打不到任何 FieldUnit,于是这一下被当成"移动",
    ///   接着被"目标排已满"拒掉 —— 表现就是"敌人那排满了就打不到它"。
    ///   攻击判定的单位是**敌方排**,不是"指针必须严丝合缝压在某张卡上"。
    /// </summary>
    private FieldUnit AttackTargetUnderPointer(FieldUnit attacker, Vector2 screenPosition)
        => AttackTargetUnderPointer(attacker, screenPosition, PickRowUnderPointer(screenPosition));

    private FieldUnit AttackTargetUnderPointer(FieldUnit attacker, Vector2 screenPosition, BattleRow pointerRow)
    {
        if (!IsUsable(attacker)) return null;

        var unit = PickUnitUnderPointer(screenPosition);
        if (unit != null)
            return unit.Side != attacker.Side && unit.IsAlive && BattleRules.CanAttack(attacker, unit, out _)
                ? unit
                : null;

        // 补判:指针确实压在这条排上,只是没严丝合缝压在某张卡上(卡与卡之间的缝就是这种)。
        // **只认指针所在的这一条排** —— 不跨排找替身,否则"拖到敌方后军"会突然打到中军的某个单位,
        // 打谁完全猜不到,比打不着更糟。
        return NearestAttackableIn(attacker, pointerRow, screenPosition);
    }

    /// <summary>
    /// 这条排上"离指针最近的那个能打的敌人"。卡与卡之间的缝会走到这里 ——
    /// 挑离指针最近的一个是**确定**的,不会出现"点这一下打到了旁边那张"的随机感。
    /// 一条排上没有一个能打的(空排、守护挡着、射程不够)就返回 null,那一格仍然按移动处理。
    /// </summary>
    private static FieldUnit NearestAttackableIn(FieldUnit attacker, BattleRow row, Vector2 screenPosition)
    {
        if (row == null || !IsUsable(attacker)) return null;

        FieldUnit best = null;
        float bestDistance = float.MaxValue;

        var units = row.Units;      // 直接用排自己的成员表:不额外分配、顺序就是链上的顺序
        for (int i = 0; i < units.Count; i++)
        {
            var candidate = units[i];
            if (candidate == null || !candidate.IsAlive) continue;
            if (candidate.Side == attacker.Side) continue;
            if (!BattleRules.CanAttack(attacker, candidate, out _)) continue;

            float distance = Vector2.Distance(ScreenPosition2DOf(candidate), screenPosition);
            if (distance < bestDistance) { bestDistance = distance; best = candidate; }
        }

        return best;
    }

    /// <summary>取单位的屏幕坐标(二维,用来比"离指针多远")。拿不到就返回屏幕外,让它排到最后</summary>
    private static Vector2 ScreenPosition2DOf(FieldUnit unit)
    {
        var rect = RectOf(unit);
        if (rect == null) return new Vector2(-9999f, -9999f);

        var canvas = rect.GetComponentInParent<Canvas>();
        var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay
            ? null
            : canvas.worldCamera;

        return RectTransformUtility.WorldToScreenPoint(camera, rect.position);
    }

    /// <summary>把单位挪到这条排(和数字键走同一条 MoveUnit)</summary>
    private void MoveSelectedTo(FieldUnit unit, BattleRow row)
    {
        var board = BattlefieldManager.Instance;
        if (board == null) return;
        if (!IsUsable(unit)) return;

        // 排的归属分敌我,而 MoveUnit 要的是"排类型";由这里统一换算,免得拖到敌方中军时算错
        if (row.Side != unit.Side && row.RowType != BattleRowType.Front && !unit.IsRaiding)
        {
            string why = "只能移动到己方的排（袭扰骑兵才能进敌方中军）";
            row.FlashInvalid();
            FloatingTipUI.Show(ScreenPositionOf(unit), why, warning: true);
            return;
        }

        if (!board.MoveUnit(unit, row.RowType, out string message))
        {
            row.FlashInvalid();
            FloatingTipUI.Show(ScreenPositionOf(unit), message, warning: true);
            ShowTip(message);
            return;
        }

        FloatingTipUI.Show(ScreenPositionOf(unit), message);
        AfterAction();
    }

    /// <summary>这个单位现在能不能落到这条排上(移动目标算的是排"类型",不是具体哪条排)</summary>
    private bool CanMoveTo(FieldUnit unit, BattleRow row)
    {
        if (!IsUsable(unit) || row == null) return false;

        // unit.Row 就是"现在在哪条排";sideCarrier 是"会落进哪条排"
        // —— BattleRules.CanMoveTo 里两者相同就判不合法(没有位移不该收行动力与 CP)
        var carrier = RowCarrierFor(unit, row.RowType);
        if (!BattleRules.CanMoveTo(unit, unit.Row, row.RowType, carrier, out _, out bool isRetreat)) return false;

        // 容量:满员的排进不去(撤回不占新位置,不受这条限制)
        return isRetreat || !row.IsUnitCapacityFull;
    }

    /// <summary>
    /// 排类型对应的"那条排"。
    /// 直接问 BattlefieldManager.ResolveMoveRow —— 这里**曾经自己算了一份**,而且算错了:
    /// 站在共享前军时"中军"指的是**对面的中军**(§7.4 袭扰),那份实现却返回自己的中军,
    /// 于是玩家把骑兵从共享前军往前拖会被判成"不合法"(拖不过去),AI 却走得过去。
    /// </summary>
    private static BattleRow RowCarrierFor(FieldUnit unit, BattleRowType type)
    {
        var board = BattlefieldManager.Instance as BattlefieldManager;
        return board != null ? board.ResolveMoveRowTo(unit, type) : null;
    }

    /// <summary>拖动开始时,把所有"能落到"的排标成绿色(§10.2 合法落点高亮)</summary>
    private void HighlightMoveTargets(FieldUnit unit)
    {
        validDropRows.Clear();

        var board = BattlefieldManager.Instance;
        if (board == null) return;

        var rows = board.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null) continue;
            if (!CanMoveTo(unit, row)) continue;

            validDropRows.Add(row);
            row.SetHighlight(RowHighlight.Valid);
        }
    }

    private void RestoreRowHighlight(BattleRow row)
    {
        if (row == null) return;
        row.SetHighlight(validDropRows.Contains(row) ? RowHighlight.Valid : RowHighlight.None);
    }

    /// <summary>拖动结束/取消:把绿底、指针那条排的颜色、目标框的强调色、拖动标记全部清掉</summary>
    private void ClearDragVisuals()
    {
        for (int i = 0; i < validDropRows.Count; i++)
            if (IsRowAlive(validDropRows[i])) validDropRows[i].SetHighlight(RowHighlight.None);

        validDropRows.Clear();

        if (IsRowAlive(hoveredRow)) hoveredRow.SetHighlight(RowHighlight.None);
        hoveredRow = null;

        // 指针压过的那个目标只是个高亮,松手前把它放回原来的暗红
        SetTargetFrameEmphasis(hoveredTarget, false);
        hoveredTarget = null;

        if (dragMarker != null) dragMarker.gameObject.SetActive(false);

        // 箭头也要收,否则松手之后会留在场上
        HideAttackArrow();

        // 拖动登记还回去:计数归零时 BattlefieldManager 会强制把所有排的高亮收干净
        if (dragCounted)
        {
            dragCounted = false;
            BattlefieldManager.Instance?.EndDragHighlight();
        }
    }

    /// <summary>这条排还在场上吗(场景销毁时排会先走,留下的引用不能碰)</summary>
    private static bool IsRowAlive(BattleRow row)
    {
        if (row == null) return false;
        return row.transform != null;      // 已销毁的物体在这里返回 null,但不会抛异常
    }

    /// <summary>取消拖动(从头到尾没到阈值、或者按了 Esc、组件被关掉)</summary>
    private void CancelDrag()
    {
        ClearDragVisuals();
        dragUnit = null;
        pressedUnit = null;
    }

    // ---- 拖动标记:跟着指针的半透明方块 ----

    private void ShowDragMarker(FieldUnit unit)
    {
        // 单位可能在两次拖动之间被销毁:标记是挂在画布上的独立物体,父级也可能没了 —— 两种都重建
        if (dragMarker == null || dragMarker.parent == null) dragMarker = CreateDragMarker();
        if (dragMarker == null) return;

        var source = RectOf(unit);
        if (source != null) dragMarker.sizeDelta = new Vector2(source.rect.width, source.rect.height);

        dragMarker.gameObject.SetActive(true);
        if (dragMarker.parent != null) dragMarker.SetAsLastSibling();
        SetDragMarkerAlpha(DragMarkerAlpha);
        MoveDragMarker(Input.mousePosition);
    }

    /// <summary>
    /// 切换"卡牌跟手"与"攻击指向"两种模式。
    /// attacking = true:卡牌标记淡到几乎看不见,画箭头指向目标;
    /// attacking = false:箭头收掉,卡牌标记恢复。
    /// 用淡入淡出而不是直接开关,是为了拖动中来回划过敌人时不会闪。
    /// </summary>
    private void SetDragMarkerMode(bool attacking)
    {
        SetDragMarkerAlpha(attacking ? DragMarkerAlphaWhileAttacking : DragMarkerAlpha);
        if (attacking) ShowAttackArrow();
        else HideAttackArrow();
    }

    private void SetDragMarkerAlpha(float alpha)
    {
        if (dragMarker == null) return;
        var image = dragMarker.GetComponent<Image>();
        if (image == null) return;

        var color = image.color;
        if (Mathf.Approximately(color.a, alpha)) return;      // 每帧都会调,值没变就别起补间
        DOTween.Kill(image);
        image.DOColor(new Color(color.r, color.g, color.b, alpha), 0.12f).SetUpdate(true);
    }

    // ---- 攻击指向箭头 ----

    /// <summary>
    /// 拖动时指针压在能打的敌人身上:从攻击方画一根指向目标的箭头,代替"卡牌跟手"。
    /// 每次指针移动都重算(起点是攻击方当前位置,单位可能刚被移动过,不能缓存)。
    ///
    /// 【坐标】全程在**箭头父级(根画布)的局部坐标**里算,不要用 RectTransform.position。
    ///   BattleCanvas 是 ScreenSpaceOverlay + CanvasScaler(参考分辨率 1920×1080),这种画布下
    ///   `position` 是屏幕像素,而 sizeDelta/anchoredPosition 是画布单位,两者差一个 scaleFactor。
    ///   在 1080p 全屏时 scaleFactor = 1,两者数值恰好相等 —— 所以只在开发者那块屏幕上看着是对的;
    ///   换 2560×1440(1.333)箭头会整体偏移、间距与长度按比例放大,分辨率再怪一点会飘出画布。
    ///   同文件的 MoveDragMarker() 一直用的是 ScreenPointToLocalPointInRectangle,这里沿用同一套。
    /// </summary>
    private void ShowAttackArrow()
    {
        var from = RectOf(dragUnit);
        var to = RectOf(hoveredTarget);
        if (from == null || to == null) { HideAttackArrow(); return; }

        if (!EnsureAttackArrow()) return;

        var parent = attackArrowHead.parent as RectTransform;
        if (parent == null || parent != attackArrowShaft.parent) { HideAttackArrow(); return; }

        // 把两端的**屏幕坐标**换到父级局部坐标(和 MoveDragMarker 同一套换算)
        var cam = EventCameraFor(parent);
        var fromScreen = (Vector2)ScreenCenterOf(from.position);
        var toScreen = (Vector2)ScreenCenterOf(to.position);
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, fromScreen, cam, out var fromLocal) ||
            !RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, toScreen, cam, out var toLocal))
        {
            HideAttackArrow();
            return;
        }

        // 两点相差不到 1 画布单位就没法定义方向(也说明两者重叠了),直接不画
        var delta = toLocal - fromLocal;
        if (delta.sqrMagnitude < 1f) { HideAttackArrow(); return; }

        attackArrowHead.SetAsLastSibling();
        attackArrowShaft.SetAsLastSibling();

        // 【方向】从攻击方指向目标,角度由**实际坐标**算出来,不写死"朝上/朝下"。
        //   之前我按排的添加顺序推断屏幕上是"我方在上、敌方在下",直接把三角画成朝下,结果反了 ——
        //   排的上下是由 RowsContainer 的布局决定的,靠猜不靠谱。现在方向永远是"攻击方 → 目标"。
        var direction = delta.normalized;
        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;

        // 箭头贴在目标那一侧(离目标中心 AttackArrowHeadGap 个画布单位),别把目标卡面糊住
        var headLocal = toLocal - direction * AttackArrowHeadGap;
        attackArrowHead.localPosition = headLocal;
        // 贴图默认尖端朝"上"(+Y),所以要把 +Y 转到 direction:角度再减 90°
        attackArrowHead.localRotation = Quaternion.Euler(0f, 0f, angle - 90f);

        // 箭杆从攻击方一直拉到箭头根部,不会戳穿三角
        var headBase = headLocal + direction * (AttackArrowHeadSize.y * 0.5f - AttackArrowShaftThickness * 0.5f);
        float length = Vector2.Distance(fromLocal, headBase);
        if (length <= AttackArrowShaftMinLength)
        {
            attackArrowShaft.gameObject.SetActive(false);
        }
        else
        {
            attackArrowShaft.gameObject.SetActive(true);
            attackArrowShaft.localPosition = (fromLocal + headBase) * 0.5f;
            // 一根横条,绕 Z 轴转到两点连线的角度就是箭杆
            attackArrowShaft.localRotation = Quaternion.Euler(0f, 0f, angle);
            attackArrowShaft.sizeDelta = new Vector2(length, AttackArrowShaftThickness);
        }

        attackArrowVisible = true;
    }

    private void HideAttackArrow()
    {
        if (!attackArrowVisible) return;
        attackArrowVisible = false;

        if (attackArrowHead != null) attackArrowHead.gameObject.SetActive(false);
        if (attackArrowShaft != null) attackArrowShaft.gameObject.SetActive(false);
    }

    /// <summary>箭头和箭杆都建好并显示出来了吗(建不出来返回 false,调用方就别继续算了)</summary>
    private bool EnsureAttackArrow()
    {
        if (attackArrowHead == null || attackArrowHead.parent == null) attackArrowHead = CreateAttackArrowPart(
            AttackArrowHeadName, EnsureArrowHeadSprite(), AttackArrowHeadSize);
        if (attackArrowShaft == null || attackArrowShaft.parent == null) attackArrowShaft = CreateAttackArrowPart(
            AttackArrowShaftName, EnsureArrowShaftSprite(), new Vector2(1f, AttackArrowShaftThickness));

        if (attackArrowHead == null || attackArrowShaft == null) return false;

        attackArrowHead.gameObject.SetActive(true);
        // 箭杆的显隐由 ShowAttackArrow 按实际长度决定,这里不无条件打开
        return true;
    }

    private RectTransform CreateAttackArrowPart(string objectName, Sprite sprite, Vector2 size)
    {
        var go = new GameObject(objectName, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        var image = go.GetComponent<Image>();

        image.sprite = sprite;
        image.color = AttackArrowColor;
        image.raycastTarget = false;      // 只是个提示,绝不能挡住底下排/单位的射线

        var host = VisualHost();
        if (host == null) { Destroy(go); return null; }

        rt.SetParent(host, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        go.SetActive(false);
        return rt;
    }

    /// <summary>
    /// 箭头尖的三角:**尖端朝上(+Y)**,底边在下。
    /// ShowAttackArrow 会把 +Y 旋转到"攻击方 → 目标"的方向,所以这里是中性朝向,不带上下假设。
    /// 资源只生成一次,之后复用。
    /// </summary>
    private static Sprite EnsureArrowHeadSprite()
    {
        if (arrowHeadSprite != null) return arrowHeadSprite;

        const int w = 32, h = 24;
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var clear = new Color(0f, 0f, 0f, 0f);

        for (int y = 0; y < h; y++)
        {
            // y 越大越靠上。**尖端必须在最上(y = h-1)** —— ShowAttackArrow 是按
            // "贴图尖端朝 +Y"来算旋转的,这里画反了箭头就会指向目标的反方向。
            // t: 1 = 最上(尖端,宽 0),0 = 最下(底边,最宽)
            float t = (float)y / (h - 1);
            float halfWidth = Mathf.Max(0.5f, (1f - t) * (w * 0.5f));
            for (int x = 0; x < w; x++)
            {
                float distanceFromCenter = Mathf.Abs(x - (w - 1) * 0.5f);
                float alpha = Mathf.Clamp01(halfWidth - distanceFromCenter + 0.5f);
                texture.SetPixel(x, y, alpha <= 0f ? clear : new Color(1f, 1f, 1f, alpha));
            }
        }
        texture.Apply();

        arrowHeadSprite = Sprite.Create(texture, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        return arrowHeadSprite;
    }

    /// <summary>箭杆用的纯白 1×1 Sprite,拉伸成任意长度的细条</summary>
    private static Sprite EnsureArrowShaftSprite()
    {
        if (arrowShaftSprite != null) return arrowShaftSprite;

        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, Color.white);
        texture.Apply();

        arrowShaftSprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 100f);
        return arrowShaftSprite;
    }

    private void MoveDragMarker(Vector2 screenPosition)
    {
        if (dragMarker == null || !dragMarker.gameObject.activeSelf) return;

        var parent = dragMarker.parent as RectTransform;
        if (parent == null) return;

        var cam = EventCameraFor(parent);
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenPosition, cam, out var local))
            dragMarker.anchoredPosition = local;
    }

    private RectTransform CreateDragMarker()
    {
        var go = new GameObject("UnitDragMarker", typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        var image = go.GetComponent<Image>();

        image.sprite = CreateFrameSprite();
        image.type = Image.Type.Sliced;
        image.color = dragMarkerColor;
        image.raycastTarget = false;      // 别挡住下面的排,不然拖到自己身上就判定不出来了

        var host = VisualHost();
        if (host == null) { Destroy(go); return null; }     // 画布没了就别建,建了也没处挂
        rt.SetParent(host, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        go.SetActive(false);
        return rt;
    }

    private static Camera EventCameraFor(RectTransform rect)
    {
        if (rect == null) return null;
        var canvas = rect.GetComponentInParent<Canvas>();
        if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
        return canvas.worldCamera;
    }

    // ================================================================ 攻击

    private void TryAttack(FieldUnit attacker, FieldUnit target)
    {
        var board = BattlefieldManager.Instance;
        if (board == null) return;
        if (!IsUsable(attacker) || target == null || !target.IsAlive) return;

        if (target.Row == null)
        {
            ShowTip("目标不在任何一条排上");
            return;
        }

        if (!BattleRules.CanAttack(attacker, target, out string reason))
        {
            ShowTip(reason);
            FloatingTipUI.Show(ScreenPositionOf(target), reason, warning: true);
            target.Row.FlashInvalid();
            return;
        }

        if (!board.AttackUnit(attacker, target, out string message))
        {
            ShowTip(message);
            FloatingTipUI.Show(ScreenPositionOf(target), message, warning: true);
            return;
        }

        AfterAction();
    }

    /// <summary>单位行动之后:AP 用完就自动取消选中,否则保持选中(连战 / 骑兵能连着走两步)</summary>
    private void AfterAction()
    {
        if (selected == null) return;

        // 攻击/反击可能把选中的自己打死了:必须清掉选中,否则后面每帧都在碰已销毁的对象
        if (!IsUsable(selected))
        {
            ClearSelection();
            return;
        }

        if (selected.Ap <= 0)
        {
            ClearSelection();
            return;
        }

        // 还有 AP:位置/血量都变了,可攻击目标要重算
        RefreshTargetFrames(selected);
    }

    // ================================================================ 键盘快捷移动(拖动之外的等价入口)

    private void LateUpdate()
    {
        // 数字键 1/2/3 = 选中单位向 后军 / 中军 / 前军 移动(§2.3 只能向前;袭扰骑兵 3 = 撤回)
        // 和鼠标拖动走的是同一个 MoveUnit,只是给键盘党/调试留一个不用拖的入口
        if (!allowActions || selected == null || IsDraggingUnit) return;
        if (!IsUsable(selected)) { ClearSelection(); return; }

        BattleRowType type;
        if (Input.GetKeyDown(KeyCode.Alpha1)) type = BattleRowType.Back;
        else if (Input.GetKeyDown(KeyCode.Alpha2)) type = BattleRowType.Mid;
        else if (Input.GetKeyDown(KeyCode.Alpha3)) type = BattleRowType.Front;
        else return;

        var board = BattlefieldManager.Instance;
        if (board == null) return;

        if (!board.MoveUnit(selected, type, out string message))
        {
            ShowTip(message);
            // 「已经在这条排上」只是原地没动,不该飘红警告
            if (selected.Row == null || selected.Row.RowType != type)
                FloatingTipUI.Show(ScreenPositionOf(selected), message, warning: true);
            return;
        }

        FloatingTipUI.Show(ScreenPositionOf(selected), message);
        AfterAction();
    }

    // ================================================================ 可攻击目标高亮

    /// <summary>
    /// 给"这个单位现在打得到的目标"套红框。
    /// 判定完全走 BattleRules.CanAttack(射程、守护、无敌、压制都在里面),
    /// 所以被守护挡住的单位不会亮框 —— 「为什么点不动」一眼就能看出来。
    /// </summary>
    private void RefreshTargetFrames(FieldUnit attacker)
    {
        HideTargetFrames();
        if (!IsUsable(attacker) || attacker.Ap <= 0) return;

        var board = BattlefieldManager.Instance;
        if (board == null) return;

        int used = 0;
        var rows = board.Rows;

        for (int r = 0; r < rows.Count; r++)
        {
            var members = BattleRules.Snapshot(rows[r]);
            for (int i = 0; i < members.Count; i++)
            {
                var candidate = members[i];
                if (candidate == null || !candidate.IsAlive) continue;
                if (candidate.Side == attacker.Side) continue;
                if (!BattleRules.CanAttack(attacker, candidate, out _)) continue;

                FrameFor(used++, candidate);
            }
        }

        if (debugLog) Debug.Log($"[操作] 「{attacker.DisplayName}」可攻击目标 {used} 个");
    }

    /// <summary>取(或新建)第 index 个目标框,套到 unit 上</summary>
    private void FrameFor(int index, FieldUnit unit)
    {
        var targetRect = RectOf(unit);
        if (targetRect == null) return;

        // 目标框是挂在目标身上的:目标被销毁时框会跟着没,池子里的引用就成了"已销毁"对象,必须重建
        while (targetFrames.Count <= index) targetFrames.Add(null);
        if (targetFrames[index] == null) targetFrames[index] = CreateTargetFrame();

        var frame = targetFrames[index];
        if (frame == null) return;

        frame.SetParent(targetRect, false);
        frame.anchorMin = frame.anchorMax = new Vector2(0.5f, 0.5f);
        frame.pivot = new Vector2(0.5f, 0.5f);
        frame.anchoredPosition = Vector2.zero;
        frame.sizeDelta = new Vector2(targetRect.rect.width, targetRect.rect.height) * highlightPadding;
        frame.gameObject.SetActive(true);
        frame.SetAsLastSibling();
    }

    private RectTransform CreateTargetFrame()
    {
        var go = new GameObject(TargetFrameName, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        var image = go.GetComponent<Image>();

        image.sprite = CreateFrameSprite();
        image.type = Image.Type.Sliced;
        image.color = TargetFrameColor;
        image.raycastTarget = false;      // 只是个提示框,不能挡点击

        var host = VisualHost();
        if (host == null) { Destroy(go); return null; }
        rt.SetParent(host, false);
        go.SetActive(false);
        return rt;
    }

    private void HideTargetFrames()
    {
        for (int i = 0; i < targetFrames.Count; i++)
            if (targetFrames[i] != null) targetFrames[i].gameObject.SetActive(false);
    }

    /// <summary>
    /// 把某个目标框临时"强调"一下(拖动时指针压在上面)。只是把颜色提亮/放回原色,
    /// 不改激活状态 —— 激活与否由 RefreshTargetFrames 统一管,两边都写会打架。
    /// </summary>
    private void SetTargetFrameEmphasis(FieldUnit unit, bool on)
    {
        if (!IsUsable(unit)) return;

        var rect = RectOf(unit);
        if (rect == null) return;

        for (int i = 0; i < targetFrames.Count; i++)
        {
            var frame = targetFrames[i];
            if (frame == null || !frame.gameObject.activeSelf) continue;
            if (frame.parent != rect) continue;

            var image = frame.GetComponent<Image>();
            if (image == null) continue;

            image.color = on ? TargetFrameHoverColor : TargetFrameColor;
            if (on) frame.SetAsLastSibling();
            return;
        }
    }

    // ================================================================ 表现(选中框 + 说明)

    private void MoveHighlight(FieldUnit unit)
    {
        var target = RectOf(unit);
        if (target == null) return;

        // 选中框平时挂在画布上,选中时改挂到单位身上 —— 单位销毁时框会一起没,所以引用失效就重建
        if (highlight == null) highlight = CreateHighlight();
        if (highlight == null) return;

        highlight.SetParent(target, false);
        highlight.anchorMin = new Vector2(0.5f, 0.5f);
        highlight.anchorMax = new Vector2(0.5f, 0.5f);
        highlight.pivot = new Vector2(0.5f, 0.5f);
        highlight.anchoredPosition = Vector2.zero;
        highlight.sizeDelta = new Vector2(target.rect.width, target.rect.height) * highlightPadding;
        highlight.gameObject.SetActive(true);
        highlight.SetAsLastSibling();
    }

    private RectTransform CreateHighlight()
    {
        var go = new GameObject("SelectedUnitFrame", typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        var image = go.GetComponent<Image>();

        // 用一个纯色描边代替九宫格图:美术资源还没有,代码里画一个亮金色边框
        image.sprite = CreateFrameSprite();
        image.type = Image.Type.Sliced;
        image.color = new Color(1f, 0.85f, 0.35f, 0.95f);
        image.raycastTarget = false;

        var host = VisualHost();
        if (host == null) { Destroy(go); return null; }
        rt.SetParent(host, false);
        go.SetActive(false);
        return rt;
    }

    /// <summary>
    /// 临时 UI(选中框 / 目标框 / 拖动标记)平时挂在根画布上。
    /// 画布被销毁时(重载战斗场景)这里会返回 null —— 必须在"建"的时候就挡住,
    /// 不能等 SetParent 拿到一个已销毁的 RectTransform 再抛 MissingReferenceException。
    /// </summary>
    private RectTransform VisualHost()
    {
        var canvas = CanvasUtil.FindRootCanvas();
        if (canvas != null) return canvas.transform as RectTransform;
        return this != null ? transform as RectTransform : null;
    }

    /// <summary>画一个中空的方框 Sprite(1 像素白框 + 九宫格边框),避免依赖美术资源</summary>
    private static Sprite CreateFrameSprite()
    {
        const int size = 16;
        const int border = 3;

        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var clear = new Color(0f, 0f, 0f, 0f);
        var solid = Color.white;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool onEdge = x < border || y < border || x >= size - border || y >= size - border;
                texture.SetPixel(x, y, onEdge ? solid : clear);
            }
        }
        texture.Apply();

        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, new Vector4(border, border, border, border));
    }

    /// <summary>
    /// 冒一句飘字(原来这里是一条常驻在屏幕底部、横跨 1400 像素的说明条)。
    ///
    /// 【为什么改掉常驻条】它建在画布底边居中,正好压在手牌区上,把卡牌挡住了;
    ///   而且没选中单位时它显示一长串操作教程,战斗里一直在那儿占着地方。
    ///   改成飘字之后:说完就散,不占版面,也和项目里其它反馈(见 FloatingTipUI 的说明、
    ///   以及本文件里已有的那些 FloatingTipUI.Show 调用)口径一致 —— 策划案§10.3 本来
    ///   就要求"变灰 / 抖动 / 飘字",不许用常驻弹窗打断操作。
    ///
    /// position 留空 = 用鼠标当前位置(指针操作触发的反馈基本都在指针那儿)。
    /// </summary>
    private static void ShowTip(string message, Vector2? position = null)
    {
        if (string.IsNullOrEmpty(message)) return;
        FloatingTipUI.Show(position ?? (Vector2)Input.mousePosition, message);
    }


    private static Vector3 ScreenCenterOf(Vector3 worldPosition)
    {
        var canvas = CanvasUtil.FindRootCanvas();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        return camera != null ? camera.WorldToScreenPoint(worldPosition)
                              : RectTransformUtility.WorldToScreenPoint(null, worldPosition);
    }
}
