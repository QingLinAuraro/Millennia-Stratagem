using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 拖动高亮 / 落点提示:
///   · 拖动开始时把合法落点排整条点亮(§10.2「合法部署行高亮」)
///   · 拖动过程中只改指针底下那一条排 / 那一个单位(非法就红闪)
///   · 兵种牌再补一根竖条,标出"松手会插在链上哪个缝"
///   · 松手 / 取消时把上过的色全部还回去
///
/// 高亮的判据本身来自 BattleRules,这里只管"上色、撤色、缓存还原"这套表现层账。
/// 拖动期间的高亮缓存(baseValidRows)也放在这里 —— 它天然是"一次拖动"的生命周期。
/// </summary>
public class FieldHighlighter
{
    // 拖动期间的高亮缓存:拖动开始时算一次"哪些排本来就合法",指针移动时只改指针底下那一条
    private readonly HashSet<BattleRow> baseValidRows = new();
    private BattleRow hoveredRow;
    private FieldUnit hoveredUnit;
    private Image enemyHandBackground;
    private Color enemyHandOriginalColor;
    private bool enemyHandColorCached;

    // 落点竖条:每条排一根,用到才建(它是排的子物体但 ignoreLayout,不参与排的布局)
    private readonly Dictionary<BattleRow, Image> markersByRow = new();
    private Image activeMarker;

    private readonly BattlefieldManager m;

    public FieldHighlighter(BattlefieldManager manager)
    {
        m = manager;
    }

    // ================================================================ 排 / 单位高亮

    /// <summary>
    /// 这张兵种牌现在有没有地方能落(§8.1.1:中军没满就能进;中军满了且驻有敌方袭扰骑兵时能进后军)。
    /// 没有的话 reason 说明原因,手牌据此变灰 + 点击飘字。
    /// </summary>
    public bool CanDeployUnitAnywhere(CardData card, out string reason)
    {
        reason = "中军已满，无法部署";
        if (card == null || !card.IsUnitCard) { reason = "只有兵种牌才能部署"; return false; }

        if (m.playerMid != null && BattleRules.CanDeployUnit(card, m.playerMid, m.playerMid, out reason)) return true;
        if (m.playerBack != null && BattleRules.CanDeployUnit(card, m.playerBack, m.playerMid, out string backReason)) return true;

        // 中军的原因更有代表性(玩家最先想知道的就是中军为什么进不去)
        if (m.playerMid != null) BattleRules.CanDeployUnit(card, m.playerMid, m.playerMid, out reason);
        return false;
    }

    /// <summary>拖动开始:§10.2「合法部署行高亮」</summary>
    public void HighlightDropTargets(CardData card)
    {
        ClearHighlights();
        if (card == null) return;

        for (int i = 0; i < m.rows.Count; i++)
        {
            var row = m.rows[i];
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

        m.RaycastHits(pointer, null);
        BattleRow row = null;
        FieldUnit unit = null;
        for (int i = 0; i < m.hitBuffer.Count; i++)
        {
            var go = m.hitBuffer[i].gameObject;
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
        if (card.IsUnitCard && rowValid) ShowInsertMarker(hoveredRow, BattlefieldManager.ResolveInsertIndex(hoveredRow, pointer));
        else HideInsertMarker();
    }

    /// <summary>松手/取消:把拖动期间上的色全部还回去</summary>
    public void ClearHighlights()
    {
        for (int i = 0; i < m.rows.Count; i++)
            if (m.rows[i] != null) m.rows[i].SetHighlight(RowHighlight.None);

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
        => row == null ? null : row.Side == BattleSide.Player ? m.playerMid : m.enemyMid;

    /// <summary>
    /// 落位方自己的中军(§8.1.1 后军的例外条件要拿它比)。
    /// 前军是共享排、row.Side 恒为 Player,所以这里按"谁在落位"取 —— 和后军的取法区分开。
    /// </summary>
    private BattleRow CarrierMidFor(BattleRow row)
        => BattlefieldManager.SidePayingFor(row.Side) == BattleSide.Player ? m.playerMid : m.enemyMid;

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
            if (m.enemyHandZone == null) m.enemyHandZone = PrefabResolver.FindRect(BattlefieldManager.EnemyHandZoneName);
            enemyHandBackground = m.enemyHandZone != null ? m.enemyHandZone.GetComponent<Image>() : null;
            if (enemyHandBackground != null) enemyHandOriginalColor = enemyHandBackground.color;
            enemyHandColorCached = true;
        }

        if (enemyHandBackground == null) return;
        enemyHandBackground.color = on ? m.enemyHandZoneTint : enemyHandOriginalColor;
    }

    // ================================================================ 落点竖条

    /// <summary>
    /// 拖动兵种牌时,在"松手会插进去的那个缝"上亮一根竖条。
    /// 部署位置是按指针横坐标决定的,不给提示就等于闭着眼睛放 —— 有了它,
    /// 想贴在大营左边就把竖条对到大营左边,松手就是那个位置。
    /// </summary>
    private void ShowInsertMarker(BattleRow row, int index)
    {
        if (m.insertMarkerWidth <= 0f) { HideInsertMarker(); return; }

        var marker = GetInsertMarker(row);
        if (marker == null) { HideInsertMarker(); return; }

        if (activeMarker != null && activeMarker != marker)
            activeMarker.gameObject.SetActive(false);

        var rt = (RectTransform)marker.transform;
        rt.anchoredPosition = new Vector2(InsertMarkerX(row, index), rt.anchoredPosition.y);
        marker.color = m.insertMarkerColor;
        marker.gameObject.SetActive(true);
        activeMarker = marker;
    }

    private void HideInsertMarker()
    {
        if (activeMarker != null) activeMarker.gameObject.SetActive(false);
        activeMarker = null;
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
        float halfCellAndGap = cell * 0.5f + m.memberGap * 0.5f;

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

    /// <summary>世界坐标(战场成员都是画布下的 UI 物体)转屏幕坐标,飘字定位用</summary>
    public static Vector3 ScreenCenterOf(Vector3 worldPosition)
    {
        var canvas = CanvasUtil.FindRootCanvas();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        return camera != null ? camera.WorldToScreenPoint(worldPosition)
                              : RectTransformUtility.WorldToScreenPoint(null, worldPosition);
    }

    /// <summary>每条排一根竖条,用到才建:和成员格子共用一套坐标(锚在排左上),高度 = 排高 - 4</summary>
    private Image GetInsertMarker(BattleRow row)
    {
        if (row == null) return null;
        if (markersByRow.TryGetValue(row, out var cached) && cached != null) return cached;

        var rowRect = row.transform as RectTransform;
        if (rowRect == null) return null;

        var go = new GameObject("InsertMarker", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        var rt = (RectTransform)go.transform;
        rt.SetParent(row.transform, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(Mathf.Max(1f, m.insertMarkerWidth), Mathf.Max(1f, rowRect.sizeDelta.y - 4f));
        rt.anchoredPosition = new Vector2(0f, -rowRect.sizeDelta.y * 0.5f);

        var image = go.GetComponent<Image>();
        image.color = m.insertMarkerColor;
        image.raycastTarget = false;                                   // 别挡指针命中

        // 关键:它是排的子物体,但绝不能被排的布局组当成一个成员、更不能撑动行高
        go.GetComponent<LayoutElement>().ignoreLayout = true;

        go.SetActive(false);
        markersByRow[row] = image;
        return image;
    }
}
