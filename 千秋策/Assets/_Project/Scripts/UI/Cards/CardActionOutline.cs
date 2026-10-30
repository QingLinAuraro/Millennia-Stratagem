using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 给卡面套一圈「**这张现在能操作**」的描边(默认淡绿)。手牌与战场卡共用一套:
///
///   · 手牌:能打出去就亮(`CardDragPlay.RefreshPlayable` 里 blockReason == null 时打开)。
///     出不了(点数不够 / 中军已满)时手牌本来就会变灰,描边要一起收掉。
///   · 战场:轮到我方、这个兵还有行动力(AP &gt; 0、没被压制)才亮 —— 也就是 `FieldUnit.CanActNow`。
///     **敌方兵永远不亮**:能操作是"我方视角"的概念,敌人能不能动跟我没关系。
///
/// 【为什么战场卡自己每帧核对】"能不能动"会被三条互不相干的路径改掉:回合切换(AP 重置)、
///   花 AP(移动/攻击)、被压制(压制清 AP)。靠事件逐个挂钩一定会漏一条,漏了就是
///   "兵还能动,绿框却没了";而每帧只比较几个 bool/整数、值没变就不碰 UI,代价可以忽略。
///
/// 【挂法】运行时由 `FieldUnit.Init`(战场卡)与 `CardDragPlay`(手牌)自动加上,不用往预制体里拖。
/// 描边是卡面的子物体、铺满并外扩 `padding` 像素,跟着卡面一起缩放,所以手牌大卡与战场小卡的线宽视觉一致。
/// </summary>
public class CardActionOutline : MonoBehaviour
{
    [Header("可操作描边")]
    [Tooltip("「现在能操作」的描边颜色。默认淡绿:和红(可攻击目标)、金(选中/可落点)区分开")]
    [SerializeField] private Color outlineColor = new Color(0.55f, 0.93f, 0.68f, 0.85f);

    [Tooltip("描边相对卡面往外扩多少像素(卡面本身尺寸不变)")]
    [SerializeField] private float padding = 2f;

    /// <summary>绑定了单位 = 战场卡(自己按状态核对);null = 手牌(由 CardDragPlay 驱动)</summary>
    private FieldUnit unit;

    private Image border;
    private bool shown;
    private bool suppressed;        // 选中时让位给金色选中框

    private TurnController turn;

    /// <summary>绑定战场单位之后,每帧按 CanActNow 自己核对</summary>
    public void BindUnit(FieldUnit owner)
    {
        unit = owner;
        if (unit != null) LateUpdate();     // 挂上的这一帧就先算一次,别等到下一帧才亮
    }

    /// <summary>选中(金框)期间把描边藏起来,两个框叠在一起会显得很吵</summary>
    public void SetSelected(bool selected)
    {
        suppressed = selected;
        if (selected) SetOn(false);
    }

    /// <summary>外部驱动(手牌 / 外部逻辑)。值没变就不碰 UI —— 这个方法可能每帧被调</summary>
    public void SetOn(bool value)
    {
        if (value == shown) return;
        shown = value;

        if (value) EnsureBorder();
        if (border != null) border.gameObject.SetActive(value);
    }

    private void LateUpdate()
    {
        if (unit == null) return;

        bool turnOk = true;
        if (turn == null) turn = TurnController.Instance;
        if (turn != null && turn.HasStarted) turnOk = turn.IsLocalTurn;

        // "能操作" = 轮到我方 + 这个兵还能动 + **付得起**(AP 与行动费用 CP 都够)。
        // 只判 CanActNow 是不够的:指挥点不足时兵"有 AP 但动不了",绿框会骗人。
        bool affordable = false;
        var board = BattlefieldManager.Instance;
        if (board != null) affordable = board.CanAffordAction(unit, out _);

        SetOn(!suppressed
              && turnOk
              && unit.Side == BattleSide.Player        // 只标"我方能操作"的兵
              && unit.IsUnit                           // 建筑不能动
              && unit.IsAlive
              && unit.CanActNow
              && affordable);
    }

    private void EnsureBorder()
    {
        if (border != null && border.transform.parent != null) return;

        border = UiFrameSprite.CreateBorder(transform as RectTransform, "ActionOutline", outlineColor, padding);
        if (border == null) return;

        border.transform.SetAsLastSibling();     // 描边盖在卡面上,别被插画压住
        border.gameObject.SetActive(shown);
    }
}
