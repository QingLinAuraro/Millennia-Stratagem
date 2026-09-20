using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 出牌流程:玩家拖放与 AI 共用这一套,保证"AI 不开挂、也不漏结算"。
///
/// 职责:
///   · TryResolveDrop :指针松手时结算拖着的这张手牌(取消 / 失败 / 部署 / 释放)
///   · TryDeployUnit  :兵种牌落点判定 → 交给 UnitDeployer 落位
///   · TryPlayTactic / ReleaseTactic / PlayTactic :策略卡的目标判定与释放
///   · ConsumeHandCard:打出的牌退场(数据层 + 表现层 + 广播 CardPlayed)
///
/// 顺序铁律(§7.2.1):**先判效果能不能落地 → 再扣费 → 再让牌退场**。
/// 反过来写就会出现「费扣了、牌没了、效果空放」。
/// 费用类效果(过费/回费)还要更靠后 —— 必须等这张卡自己的费用扣完再结算,
/// 否则回费卡会把自己的卡费也退回来(见 PlayTactic 里的说明)。
/// </summary>
public class CardPlayDirector
{
    /// <summary>手的牌账在 DeckController 里(打出牌要从那儿扣掉)</summary>
    private DeckController deckController;

    private readonly BattlefieldManager m;

    public CardPlayDirector(BattlefieldManager manager)
    {
        m = manager;
    }

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

        m.RaycastHits(pointer, handCard);

        return card.IsUnitCard
            ? TryDeployUnit(handCard, card, pointer, out message)
            : TryPlayTactic(handCard, card, pointer, out message);
    }

    private CardPlayResult TryDeployUnit(CardDisplay handCard, CardData card, PointerEventData pointer, out string message)
    {
        message = null;

        BattleRow row = null;
        FieldUnit hitUnit = null;
        for (int i = 0; i < m.hitBuffer.Count; i++)
        {
            var go = m.hitBuffer[i].gameObject;
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
        if (!BattleRules.CanDeployUnit(card, row, m.playerMid, out message))
        {
            row.FlashInvalid();
            return CardPlayResult.Failed;
        }

        int slot = BattlefieldManager.ResolveInsertIndex(row, pointer);
        if (!m.deployer.DeployUnit(card, row, slot, out var deployed, out message))
        {
            row.FlashInvalid();
            return CardPlayResult.Failed;
        }

        ConsumeHandCard(handCard);

        message = $"「{card.cardName}」→ {row.DisplayName}（{row.UnitCount}/{row.UnitCapacity}）";
        Debug.Log($"[Battlefield] 部署「{card.cardName}」→ {row.DisplayName}，该排单位 {row.UnitCount}/{row.UnitCapacity}");
        m.RaiseBoardChanged();

        ShowDeployEffect(card, deployed, row);
        return CardPlayResult.Deployed;
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

        Vector3 screen = FieldHighlighter.ScreenCenterOf(deployed.transform.position);
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

        for (int i = 0; i < m.hitBuffer.Count; i++)
        {
            var go = m.hitBuffer[i].gameObject;
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
            if (card.targetType == TacticTargetType.EnemyHand && m.IsInsideEnemyHandZone(pointer.position))
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
        m.RefreshRaidFlags();
        m.RaiseBoardChanged();

        Vector3 screen = unitTarget != null ? FieldHighlighter.ScreenCenterOf(unitTarget.transform.position)
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
            Object.FindObjectOfType<DeckController>()?.PlayCard(card);
        }

        BattleSettlement.CountTacticPlayed(casterSide);

        // 费用类效果(过费 / 回费)必须等卡费扣完再结算 —— 回费先补再扣等于把卡费也退了。
        // 没登记这两条效果的卡,这里什么都不做,所以别的策略牌不会影响费用。
        string costResult = CardEffectResolver.ResolvePlayCostEffects(card, caster);

        message = string.IsNullOrEmpty(costResult) ? result : $"{result}；{costResult}";

        m.RefreshRaidFlags();
        m.RaiseBoardChanged();
        return true;
    }

    /// <summary>
    /// 打出去的手牌退场。两边都要动:
    /// · 数据层(DeckController 的手牌账)—— 不扣的话打出的牌还算在手牌里,摸牌会被误判成手牌已满;
    /// · 表现层(HandUI)—— 销毁卡牌、让扇形重排。
    /// 顺带广播 CardPlayed(策划案§3.3.2):提示条只报对方出的牌,自己出的会被过滤掉。
    /// </summary>
    public void ConsumeHandCard(CardDisplay handCard)
    {
        var data = handCard != null ? handCard.Data : null;
        if (data != null)
        {
            if (deckController == null) deckController = Object.FindObjectOfType<DeckController>();
            if (deckController != null) deckController.PlayCard(data);
            else Debug.LogWarning("[BattlefieldManager] 找不到 DeckController,打出的牌还留在手牌账里。", m);

            var cp = CommandPointController.Instance;
            EventManager.Trigger(new CardPlayedEventArgs
            {
                Card = data,
                Player = cp != null ? cp.LocalPlayer : null,
            });
        }

        if (m.handUI == null) m.handUI = Object.FindObjectOfType<HandUI>();
        if (m.handUI != null) m.handUI.RemoveCard(handCard);
        else Debug.LogWarning("[BattlefieldManager] 找不到 HandUI,打出去的手牌没能从手牌区移除。", m);
    }

    // ================================================================ 指针命中

    public void RaycastHits(PointerEventData pointer, CardDisplay ignore)
    {
        m.hitBuffer.Clear();
        if (pointer == null || EventSystem.current == null) return;

        EventSystem.current.RaycastAll(pointer, m.hitBuffer);

        // 拖着的这张牌一直跟着指针,射线第一个命中的就是它 —— 剔掉
        if (ignore == null) return;
        for (int i = m.hitBuffer.Count - 1; i >= 0; i--)
        {
            var go = m.hitBuffer[i].gameObject;
            if (go == null || go.transform.IsChildOf(ignore.transform) || go == ignore.gameObject)
                m.hitBuffer.RemoveAt(i);
        }
    }

    public bool IsInsideHandArea(Vector2 screenPosition)
    {
        if (m.handUI == null) m.handUI = Object.FindObjectOfType<HandUI>();
        var rect = m.handUI != null ? m.handUI.HandRect : null;
        if (rect == null) return false;
        return RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, PrefabResolver.EventCameraFor(rect));
    }

    public bool IsInsideEnemyHandZone(Vector2 screenPosition)
    {
        if (m.enemyHandZone == null) m.enemyHandZone = PrefabResolver.FindRect(BattlefieldManager.EnemyHandZoneName);
        if (m.enemyHandZone == null) return false;
        return RectTransformUtility.RectangleContainsScreenPoint(m.enemyHandZone, screenPosition, PrefabResolver.EventCameraFor(m.enemyHandZone));
    }
}
