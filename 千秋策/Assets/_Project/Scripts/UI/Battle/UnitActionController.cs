using System.Collections.Generic;
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
///     · infoFontSize / infoBottomOffset:说明文字的字号与位置(相对屏幕底部)。
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

    [Header("说明文字")]
    [Tooltip("选中单位后屏幕下方那行说明的字号")]
    [SerializeField] private float infoFontSize = 26f;
    [Tooltip("说明文字离屏幕底边多高(像素)")]
    [SerializeField] private float infoBottomOffset = 150f;
    [Tooltip("选中框相对卡面放大多少(1 = 正好贴住卡面)")]
    [SerializeField] private float highlightPadding = 1.12f;

    [Header("调试")]
    [Tooltip("把每次点选/拖动的判定过程打到 Console(调「点不动」「拖不动」这类问题时打开)")]
    [SerializeField] private bool debugLog = false;

    private FieldUnit selected;
    private RectTransform highlight;
    private TMPro.TMP_Text infoText;

    // ---- 拖动状态 ----
    private FieldUnit dragUnit;          // 正在被拖的单位(拖动真正开始后才有值)
    private FieldUnit pressedUnit;       // 按下的单位(可能只是点一下,还没到拖动阈值)
    private BattleRow hoveredRow;        // 指针现在停在哪条排上
    private FieldUnit hoveredTarget;     // 指针现在停在哪个"打得到的敌人"上(拖动时,攻击优先于移动)
    private Vector2 pressPosition;
    private RectTransform dragMarker;    // 跟着指针的半透明方块

    /// <summary>选中单位时给"打得到的目标"套的红框(复用同一个池子,不每次新建)</summary>
    private readonly List<RectTransform> targetFrames = new();

    /// <summary>拖动时"能落到哪条排"的绿底 —— 记下来,结束时还回去</summary>
    private readonly List<BattleRow> validDropRows = new();

    private const string TargetFrameName = "AttackableTargetFrame";

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
        if (BattleSettlement.MatchOver) { SetInfo("对局已经结束"); return; }
        if (turn != null && turn.HasStarted && !turn.IsLocalTurn) { SetInfo("现在是对方的回合"); return; }

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
            SetInfo($"已选中「{unit.DisplayName}」：拖动它到一条排 = 移动，拖到套红框的敌人身上 = 攻击");
            return;
        }

        // 点到敌人:不在这里攻击(拖动才是动作),只说清楚该怎么打
        SetInfo(selected != null
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
        RefreshInfo();
        if (debugLog) Debug.Log($"[操作] 选中「{unit.DisplayName}」");
    }

    private void ClearSelection()
    {
        selected = null;
        // 选中框是挂在单位身上的:单位被销毁时它会跟着一起没,所以这里也要能容忍"已销毁"
        if (highlight != null) highlight.gameObject.SetActive(false);
        HideTargetFrames();
        RefreshInfo();
    }

    // ================================================================ 拖动移动(§2.3)
    //
    // 入口有三个,都从 FieldUnitDragProxy 转过来(它挂在兵牌自己身上,见那个文件的说明):
    // 把"哪个单位"直接传进来,不用再靠射线去猜指针底下是什么 —— 拖动的第一步就不会出错。

    /// <summary>按下一个兵牌</summary>
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

        if (pressedUnit == null || unit == null || unit != pressedUnit || !IsUsable(unit))
        { CancelDrag(); return; }

        float moved = eventData != null ? Vector2.Distance(eventData.position, pressPosition) : dragThreshold;
        if (moved < dragThreshold) { CancelDrag(); return; }

        dragUnit = unit;
        selected = unit;                       // 拖动中保持选中,松手后还看得见金框
        MoveHighlight(unit);
        RefreshTargetFrames(unit);             // 红框一直留着:拖到敌人身上就是打它
        ShowDragMarker(unit);
        HighlightMoveTargets(unit);
        RefreshInfo();

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

        // 指针底下是不是一个"现在打得到"的敌人?
        var target = AttackTargetUnderPointer(dragUnit, eventData.position);
        if (target != hoveredTarget)
        {
            SetTargetFrameEmphasis(hoveredTarget, false);
            hoveredTarget = target;
            SetTargetFrameEmphasis(hoveredTarget, true);
        }

        if (hoveredTarget != null)
        {
            // 攻击优先:指针在敌人身上时不再给排上色,免得看不出来这一下会打谁
            RestoreRowHighlight(hoveredRow);
            hoveredRow = null;
            SetInfo($"松手攻击「{hoveredTarget.DisplayName}」");
            return;
        }

        var row = PickRowUnderPointer(eventData.position);
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
        if (!IsUsable(acting)) { SetInfo("这个单位已经不在场上了"); RefreshInfo(); return; }

        // ---- 结局一:落在敌人身上 → 攻击 ----
        if (target != null) { TryAttack(acting, target); return; }

        if (row == null)
        {
            SetInfo("松手的位置不在任何一条排上，没有移动");
            RefreshInfo();
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
            SetInfo(sameRow ? $"「{acting.DisplayName}」已经在这条排上" : why ?? "不能移到这条排");
            RefreshInfo();
            return;
        }

        MoveSelectedTo(acting, row);
    }

    /// <summary>
    /// 指针底下那个"拖动中的单位现在打得到"的敌人(用红框状态判,和界面上看到的完全一致)。
    /// 找不到返回 null —— 那就说明这一下不是攻击,该按移动处理。
    /// </summary>
    private FieldUnit AttackTargetUnderPointer(FieldUnit attacker, Vector2 screenPosition)
    {
        if (!IsUsable(attacker)) return null;

        var unit = PickUnitUnderPointer(screenPosition);
        if (unit == null || unit.Side == attacker.Side || !unit.IsAlive) return null;
        if (!BattleRules.CanAttack(attacker, unit, out _)) return null;

        return unit;
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
            SetInfo(message);
            RefreshInfo();
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
        MoveDragMarker(Input.mousePosition);
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
            SetInfo("目标不在任何一条排上");
            return;
        }

        if (!BattleRules.CanAttack(attacker, target, out string reason))
        {
            SetInfo(reason);
            FloatingTipUI.Show(ScreenPositionOf(target), reason, warning: true);
            target.Row.FlashInvalid();
            return;
        }

        if (!board.AttackUnit(attacker, target, out string message))
        {
            SetInfo(message);
            FloatingTipUI.Show(ScreenPositionOf(target), message, warning: true);
            return;
        }

        AfterAction();
    }

    /// <summary>单位行动之后:AP 用完就自动取消选中,否则保持选中(连战 / 骑兵能连着走两步)</summary>
    private void AfterAction()
    {
        if (selected == null) { RefreshInfo(); return; }

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
        RefreshInfo();
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
            SetInfo(message);
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

    private void RefreshInfo()
    {
        if (selected == null || !selected.IsAlive)
        {
            SetInfo(allowActions
                ? "拖动我方兵牌：落到一条排上 = 移动（绿的可落、红的不可），落到套红框的敌人身上 = 攻击 ｜ 点一下 = 选中查看 ｜ Esc = 取消"
                : "");
            return;
        }

        string hint = DescribeActions(selected);
        SetInfo($"已选中「{selected.DisplayName}」 {selected.Atk}/{selected.Hp} · 行动 {selected.Ap}/{selected.ApMax} · {hint}");
    }

    private static string DescribeActions(FieldUnit unit)
    {
        if (unit.Ap <= 0) return "行动力已耗尽（每回合开始补满）";
        if (unit.SuppressTurns > 0) return $"被压制 {unit.SuppressTurns} 回合";
        if (unit.DeployedThisTurn && !BattleRules.HasKeyword(unit.Data, Keyword.Blitz)) return "刚部署，本回合不能行动（带「闪击」的除外）";

        var parts = new List<string>();
        int range = BattleRules.RangeOf(unit);
        parts.Add(unit.IsRaiding
            ? $"袭扰中：只能打敌方后军建筑（射程 {range}）"
            : $"可打 {range} 排内的敌方目标（红的才能打）");
        parts.Add(unit.IsRaiding ? "拖动到前军 = 撤回" : "拖动到相邻前方的排 = 移动");
        parts.Add("每次行动扣 1 AP + 行动费用 CP");

        return string.Join("，", parts);
    }

    private void SetInfo(string text)
    {
        if (string.IsNullOrEmpty(text)) { if (infoText != null) infoText.text = ""; return; }

        if (infoText == null)
        {
            var canvas = CanvasUtil.FindRootCanvas();
            if (canvas == null) return;

            infoText = RuntimeText.Create((RectTransform)canvas.transform, "UnitActionInfo", text, infoFontSize,
                                          TMPro.TextAlignmentOptions.Center,
                                          new Color(0.98f, 0.95f, 0.82f));

            var rt = (RectTransform)infoText.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(1400f, 60f);
            rt.anchoredPosition = new Vector2(0f, infoBottomOffset);
        }

        infoText.text = text;
    }

    private static Vector3 ScreenCenterOf(Vector3 worldPosition)
    {
        var canvas = CanvasUtil.FindRootCanvas();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        return camera != null ? camera.WorldToScreenPoint(worldPosition)
                              : RectTransformUtility.WorldToScreenPoint(null, worldPosition);
    }
}
