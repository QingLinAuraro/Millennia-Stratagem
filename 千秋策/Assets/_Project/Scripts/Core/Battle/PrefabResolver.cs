using UnityEngine;

/// <summary>
/// 预制体 / 场景引用解析:所有"Inspector 没拖就自己找"的兜底逻辑都收在这里。
///
/// 原本这些方法都在 BattlefieldManager 里,和战场逻辑混在一起。拆出来的好处是
/// "引用从哪来"这件事只有一个地方定义 —— 想查"为什么建筑没摆出来",看这一个文件就够。
///
/// 职责:
///   · ResolveRow     :按场景物体名认领一条排(没有 BattleRow 组件就当场补一个并配好身份)
///   · FindRect       :按名字找 RectTransform
///   · ResolveBuildPrefab      :建筑预制体(Inspector → 编辑器按路径认领)
///   · ResolveFieldCardPrefab  :战场卡面(Inspector → HandUI → 按路径 → 退回手牌卡面)
///   · EventCameraFor :这个 UI 元素该用哪个摄像机做事件坐标换算
///
/// 全部是"读场景 + 写回 Inspector 字段"的纯解析工作,不含任何战斗规则。
/// </summary>
public class PrefabResolver
{
    /// <summary>编辑器里自动认领建筑预制体用的路径(Inspector 上没拖时才用)</summary>
    private const string BuildPrefabPath = "Assets/_Project/Prefabs/Battle/Build.prefab";

    /// <summary>编辑器里自动认领战场卡面用的路径(Inspector 上没拖时才用)</summary>
    private const string FieldCardPrefabPath = "Assets/_Project/Prefabs/UI/CardsInBattle.prefab";

    private readonly BattlefieldManager m;

    public PrefabResolver(BattlefieldManager manager)
    {
        m = manager;
    }

    /// <summary>
    /// 按名字认领一条排。场景里本来就有 BattleRow(自己配好了身份)就用它;
    /// 没有就补一个并按名字配好阵营/位置/容量。
    /// </summary>
    public BattleRow ResolveRow(BattleRow current, string objectName, BattleSide side, BattleRowType rowType, int capacity)
    {
        bool created = false;

        if (current == null)
        {
            var rt = FindRect(objectName);
            if (rt == null)
            {
                Debug.LogError($"[BattlefieldManager] 场景里找不到排「{objectName}」,这条排落不了牌。", m);
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

    /// <summary>给 5 条排（后军/中军/前军）绑定对应的 UI 锚点</summary>
    public static RectTransform FindRect(string objectName)
    {
        var go = GameObject.Find(objectName);
        return go != null ? go.transform as RectTransform : null;
    }

    /// <summary>
    /// 拿到建筑预制体。优先用 Inspector 里拖的那个;没拖就在编辑器里按路径认领一份 ——
    /// 场景被 Unity 从内存里另存、把引用冲掉时,不用手动再拖一次也能跑起来。
    /// </summary>
    public GameObject ResolveBuildPrefab()
    {
        if (m.buildPrefab != null) return m.buildPrefab;

#if UNITY_EDITOR
        m.buildPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BuildPrefabPath);
        if (m.buildPrefab != null)
            Debug.Log($"[BattlefieldManager] buildPrefab 没赋值,已按路径自动认领 {BuildPrefabPath}", m);
#endif
        return m.buildPrefab;
    }

    /// <summary>
    /// 拿到战场卡面的预制体。优先级:Inspector 拖的 fieldCardPrefab → HandUI 上的战场卡引用 →
    /// 编辑器里按路径自动认领 CardsInBattle.prefab → 最后退回手牌用的 Card.prefab(老行为,能跑但字小)。
    /// </summary>
    public CardDisplay ResolveFieldCardPrefab()
    {
        if (m.fieldCardPrefab == null)
        {
            if (m.handUI == null) m.handUI = Object.FindObjectOfType<HandUI>();
            if (m.handUI != null) m.fieldCardPrefab = m.handUI.BattleCardPrefab;
        }

        if (m.fieldCardPrefab == null)
        {
#if UNITY_EDITOR
            m.fieldCardPrefab = UnityEditor.AssetDatabase
                .LoadAssetAtPath<CardDisplay>(FieldCardPrefabPath);
            if (m.fieldCardPrefab != null)
                Debug.Log($"[BattlefieldManager] fieldCardPrefab 没赋值,已按路径自动认领 {FieldCardPrefabPath}", m);
#endif
        }

        if (m.fieldCardPrefab == null)
        {
            // 兜底:老的手牌卡面也能当战场卡用(只是字号小、还占着费用区和描述区)
            if (m.handUI == null) m.handUI = Object.FindObjectOfType<HandUI>();
            m.fieldCardPrefab = m.handUI != null ? m.handUI.CardPrefab : null;

            if (m.fieldCardPrefab != null)
                Debug.LogWarning(
                    $"[BattlefieldManager] 找不到战场卡面 {FieldCardPrefabPath},已退回手牌用的 Card.prefab:" +
                    "战场上的字会比设计的小。请确认 CardsInBattle.prefab 还在,或在 Inspector 上指定 fieldCardPrefab。", m);
        }

        if (m.fieldCardPrefab == null)
            Debug.LogError("[BattlefieldManager] 拿不到任何战场卡面预制体,部署不了单位。", m);

        return m.fieldCardPrefab;
    }

    /// <summary>根据 UI 元素所在的 Canvas 类型，决定事件系统应该用哪个摄像机</summary>
    public static Camera EventCameraFor(RectTransform rect)
    {
        if (rect == null) return null;
        var canvas = rect.GetComponentInParent<Canvas>();
        if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
        return canvas.worldCamera;
    }
}
