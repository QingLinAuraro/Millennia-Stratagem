using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 战场成员的指针转发器:把"按住 / 拖动 / 松手"原样交给 UnitActionController。
///
/// 为什么要这么一层:拖动事件是由 EventSystem 派发给**指针按下的那个物体**(以及它的父级)上的处理者的。
/// 兵牌的拖动要落到 UnitActionController 上,而它挂在 BattleCanvas 上 —— 兵牌是 Canvas 的后代,
/// 层级上确实够得着,但这依赖"UI 的父子层级"这个和玩法无关的东西,换个挂法或换个预制体就会失效
/// (表现成"拖不动",而且不报错)。所以干脆把这一层挂在兵牌自己身上:
/// 事件一定先到这里,而这里一定能从同一个物体上拿到 FieldUnit,不必再靠射线去猜。
///
/// 【挂载 & 调整】
///   挂在:不用手挂。BattlefieldManager.SpawnUnit 生成兵牌格子时会 AddComponent 一个。
///         建筑(SpawnBuilding)不挂 —— 建筑不能移动,拖它没有任何意义。
///   引用:没有要手连的引用;找不到 UnitActionController 就什么都不做。
///   常调:
///     · 没有可调的项。要调拖动手感(阈值、标记颜色)请去 UnitActionController 上改。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(FieldUnit))]
public class FieldUnitDragProxy : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    private FieldUnit unit;

    private FieldUnit Unit
    {
        get
        {
            if (unit == null) unit = GetComponent<FieldUnit>();
            return unit;
        }
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        var controller = UnitActionController.Instance;
        if (controller != null) controller.OnUnitPointerDown(Unit, eventData);
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        var controller = UnitActionController.Instance;
        if (controller != null) controller.OnUnitBeginDrag(Unit, eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        var controller = UnitActionController.Instance;
        if (controller != null) controller.OnUnitDrag(Unit, eventData);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        var controller = UnitActionController.Instance;
        if (controller != null) controller.OnUnitEndDrag(Unit, eventData);
    }
}
