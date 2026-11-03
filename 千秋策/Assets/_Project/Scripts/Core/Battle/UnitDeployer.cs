using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 单位 / 建筑生成与部署:所有 Instantiate 都收在这里。
///
/// 职责:
///   · DeployUnit        :真正落位(判容量 → 扣费 → 生成成员 → 补军械库惩罚)
///   · SpawnUnitForDebug :调试/自检用,绕过规则与费用
///   · SpawnUnit         :生成战场小卡并登记进排里(两层结构:排 → 格子 → 卡面)
///   · SpawnBuilding     :生成一座建筑锚点
///   · CreateSlot / ResizeSlot / FitInSlot :格子的建、量、摆
///
/// 出牌流程本身(判定落点、消耗手牌、飘字)在 CardPlayDirector,
/// 这里只负责"把东西真的放到场上"和与之配套的格子几何。
/// </summary>
public class UnitDeployer
{
    private readonly BattlefieldManager m;

    public UnitDeployer(BattlefieldManager manager)
    {
        m = manager;
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
        var carrierMid = m.CarrierMidFor(row);
        if (!BattleRules.CanDeployUnit(card, row, carrierMid, out message)) return false;

        // 落位方的费用池:我方走 CommandPointController,敌方走 EnemyDeckController
        // (共享前军的 row.Side 恒为 Player,所以这里必须走 SidePayingFor,不能直接看 row.Side)
        if (BattlefieldManager.SidePayingFor(row.Side) == BattleSide.Player)
        {
            var cp = CommandPointController.Instance;
            if (cp != null && !cp.TrySpend(card.deploymentCost)) { message = "指挥点不足"; return false; }
        }
        else
        {
            var enemy = EnemyDeckController.Instance;
            if (enemy != null && !enemy.TrySpend(card.deploymentCost)) { message = "敌方指挥点不足"; return false; }
        }

        deployed = SpawnUnit(card, row, Mathf.Clamp(slotIndex, 0, row.Units.Count), BattlefieldManager.SidePayingFor(row.Side));
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
            m.RefreshRaidFlags();
            Debug.Log($"[Battlefield] 调试生成「{card.cardName}」({BattleRules.SideName(side)})→ {row.DisplayName}");
        }
        return unit;
    }

    /// <summary>
    /// 生成战场小卡并登记进排里。
    ///
    /// 两层结构:排 → **布局格子**(105×140,链上第几位就是第几个格子) → 卡面(150×200 的设计尺寸,
    /// 整体缩 0.7)。位置/间距全部交给排上的 HorizontalLayoutGroup(它按格子的 sizeDelta 排),
    /// 这里只给格子的尺寸和 sibling 顺序。
    /// </summary>
    private FieldUnit SpawnUnit(CardData card, BattleRow row, int siblingIndex, BattleSide side)
    {
        var prefab = m.prefabs.ResolveFieldCardPrefab();
        if (prefab == null || row == null) return null;

        var slot = CreateSlot(row, siblingIndex, "Unit_" + card.cardId);

        var view = Object.Instantiate(prefab, slot);
        view.name = "Unit_" + card.cardId;
        view.Bind(card, CardViewMode.Field);

        // 战场卡:能悬停看完整信息(卡面只有行动费用/攻血/兵种/朝代,效果文案在预览里)。
        // CardHover 的战场布局由 FieldUnit.Init → Bind(FieldUnit) 喂进去,预览显示的是场上当前值
        var hover = view.GetComponent<CardHover>();
        if (hover != null) hover.enabled = m.fieldHoverPreview;

        // 拖动出牌组件在战场卡上绝不能生效(按住战场卡会变成把它当手牌打出去)
        var drag = view.GetComponent<CardDragPlay>();
        if (drag != null) drag.enabled = false;

        var cardRect = (RectTransform)view.transform;
        ResizeSlot(slot);                // 格子 = 兵牌设计尺寸 × 缩放(150×200 × 0.7 = 105×140)
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
        m.buildingManager.ApplyArsenalPenalty(unit);

        // 词条栏(卡面右上角外侧的小方块 + 长悬停面板)。
        // 必须在 SetHeavyArmor 之后绑:它要按最终的层数画「重甲 ×N」;
        // 绑的时机也是"卡面已经 Bind 过、单位数值已经定档"之后,读到的都是最终值。
        var keywordBadge = view.gameObject.AddComponent<FieldKeywordBadge>();
        keywordBadge.Bind(unit);

        row.AddUnit(unit, siblingIndex);
        return unit;
    }

    /// <summary>
    /// 生成一座建筑锚点(§2.3):它占容量、不可移动、不可被替换。
    /// </summary>
    /// <param name="overrideArtwork">
    /// 临时替换的立绘(大营用:图片跟着卡组里那张大营卡走,见 BuildingManager.ResolveCampArtwork)。
    /// 传 null 就用 spec 自己连的那张;spec 也没连就保留预制体的通用图。
    /// </param>
    public FieldUnit SpawnBuilding(BuildingSpec spec, BattleRow row, Sprite overrideArtwork = null)
    {
        var prefab = m.prefabs.ResolveBuildPrefab();
        if (prefab == null || spec == null || row == null) return null;

        // 调用 CreateSlot 方法，在排上创建一个名为 "Slot_大营" 之类的空物体
        var slot = CreateSlot(row, row.MemberCount, "Slot_" + spec.displayName);

        // 把 Build.prefab 的实例创建出来，并设为刚才格子的子物体。Instantiate 是 Unity 运行时动态生成物体的核心 API。
        var go = Object.Instantiate(prefab, slot);
        go.name = "Building_" + spec.displayName;

        ApplyBuildingArtwork(go, overrideArtwork != null ? overrideArtwork : spec.artwork);

        // 把刚实例化的物体的 Transform 转换成 RectTransform（UI 物体必须是 RectTransform 才能参与布局）
        var buildRect = (RectTransform)go.transform;

        // 建筑占的格子**强制**和兵牌一样大(同一个常量算出来,见 ResizeSlot)
        ResizeSlot(slot);
        FitInSlot(buildRect);

        var unit = slot.gameObject.AddComponent<FieldUnit>();
        unit.InitAsBuilding(spec.displayName, spec.hp, row, spec.side);
        row.AddUnit(unit, row.MemberCount);
        return unit;
    }

    /// <summary>
    /// 把立绘换到预制体根上那颗 Image(角标是子物体上的另一颗 Image,不能一起改)。
    /// 同时把颜色刷成纯白 —— 预制体根图原先带一点偏色,m_Sprite 一换就会把整张图染色。
    /// </summary>
    private static void ApplyBuildingArtwork(GameObject go, Sprite artwork)
    {
        if (go == null || artwork == null) return;

        // ⚠ 必须拿根上的:GetComponentInChildren 会先命中根,这里其实一样,
        //   但角标那颗在子物体上,用 GetComponentInChildren 以后改层级会踩雷,直接 GetComponent 最稳
        var image = go.GetComponent<Image>();
        if (image == null) return;

        image.sprite = artwork;
        image.color = Color.white;
        image.type = Image.Type.Simple;
        image.preserveAspect = false;      // 铺满卡位,与兵牌同尺寸
    }

    /// <summary>战场卡的设计尺寸,兵牌与建筑共用。格子 = 它 × fieldCardScale(= 105×140)</summary>
    private static readonly Vector2 FieldCardDesignSize = new Vector2(150f, 200f);

    /// <summary>建一个布局格子。排上的 HorizontalLayoutGroup 按格子的 sizeDelta 排链,内容装在格子里再整体缩放</summary>
    private RectTransform CreateSlot(BattleRow row, int siblingIndex, string objectName)
    {
        var go = new GameObject(objectName, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(row.transform, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);    // 布局组随后会写成 (0,1)
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = FieldCardDesignSize * m.fieldCardScale;    // 兵牌与建筑同一个尺寸
        rt.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, row.transform.childCount - 1));
        return rt;
    }

    /// <summary>把格子尺寸统一成"兵牌的设计尺寸 × 缩放"。兵牌、建筑走同一条,不量各自的预制体</summary>
    private void ResizeSlot(RectTransform slot)
    {
        slot.sizeDelta = FieldCardDesignSize * m.fieldCardScale;
    }

    /// <summary>把内容摆进格子正中:保持预制体的设计尺寸,整体等比缩放填满格子</summary>
    private void FitInSlot(RectTransform content)
    {
        if (content == null) return;
        content.anchorMin = content.anchorMax = new Vector2(0.5f, 0.5f);
        content.pivot = new Vector2(0.5f, 0.5f);
        content.anchoredPosition = Vector2.zero;
        content.localRotation = Quaternion.identity;
        content.localScale = Vector3.one * m.fieldCardScale;
    }
}
