using UnityEngine;

/// <summary>
/// 战斗结算:攻击、伤害、死亡、建筑被毁的 Debuff、大营血量归零判负、牌库耗尽的疲劳伤害。
/// 全照策划案§7.1 / §7.3 / §7.4 / §2.3 / §5.4.1 抄,不碰 UI 输入,由出牌流程、单位操作和 AI 共同调用。
///
/// 为什么是 static 而不是 MonoBehaviour:
///   结算全是"纯规则"—— 给它攻击者和目标就能算出结果,不需要逐帧更新,也不需要 Inspector。
///   它只在运行时抓一次 BattlefieldManager(FieldUnit 与 5 条排的账都在那儿),抓不到就退回
///   FindObjectOfType / 直接建一个,和别的单例一个套路。
///
/// 谁调它:
///   · BattlefieldManager:兵牌落位(登记重甲层数、军械库 Debuff)、策略卡效果结算时;
///   · UnitActionController:玩家点击单位后的移动/攻击;
///   · EnemyAI:敌方回合的移动/攻击;
///   · DeckController / EnemyDeckController:牌堆摸空 → TriggerFatigue;
///   · TurnController:每次进入新回合时 BeginTurnFor / EndTurnFor(重置 AP、清临时 buff、走袭扰自损)。
///
/// 【挂载 & 调整】
///   挂在:不是组件,不用挂。第一次用到时 Board 自己会去找 BattlefieldManager。
///   常调:
///     · MatchOver / Result:对局是否已经结束、谁赢(大营被打空 / 投降都会把它置上),
///       出牌与行动在结算前都会先看这个,结束之后谁也动不了。
///     · FatigueDamageFor(次数):疲劳伤害 = 次数 × 2(2→4→6→8…,§5.4.1)。
///       想改成别的曲线就改这一个方法,两边牌堆共用。
///     · ArsenalAtkPenalty:军械库被毁后给那一方全场兵牌(含之后上场的)-N ATK,可以叠加。
/// </summary>
public static class BattleSettlement
{
    /// <summary>对局已经结束(大营被打空或投降);结束之后所有结算与操作都不再生效</summary>
    public static bool MatchOver { get; private set; }

    /// <summary>谁赢了(仅在 MatchOver 为真时有意义)</summary>
    public static BattleSide Winner { get; private set; }

    /// <summary>结束原因(结算面板上显示的那句话)</summary>
    public static string ResultText { get; private set; }

    /// <summary>当前回合是第几轮(双方各走一次算一轮)。临时 buff 按轮次过期,由 TurnController 推进</summary>
    public static int CurrentRound { get; private set; } = 1;

    /// <summary>我方打出的总伤害(结算面板的「造成伤害」)</summary>
    public static int PlayerDamageDealt { get; private set; }

    /// <summary>我方摧毁的敌方建筑数(结算面板的「摧毁建筑」)</summary>
    public static int PlayerBuildingsDestroyed { get; private set; }

    /// <summary>我方使用过的策略卡数(结算面板的「使用策略卡」)</summary>
    public static int PlayerTacticsUsed { get; private set; }

    private static BattlefieldManager board;

    /// <summary>对局结束时通知一次(结算面板 / 停掉 AI 听这个)</summary>
    public static event System.Action<BattleSide, string> MatchEnded;

    /// <summary>战场账(单位在哪条排、某方有哪些建筑)。找不到就现建一个</summary>
    public static BattlefieldManager Board
    {
        get
        {
            if (board == null) board = Object.FindObjectOfType<BattlefieldManager>();
            if (board == null) board = BattlefieldManager.Instance;
            return board;
        }
    }

    /// <summary>换个战场(换场景/重开一局时用;测试里也能直接塞一个假的)</summary>
    public static void SetBoard(BattlefieldManager manager) => board = manager;

    /// <summary>重开一局:把结算状态清零(不重载场景时用)</summary>
    public static void ResetMatch()
    {
        MatchOver = false;
        Winner = BattleSide.Player;
        ResultText = null;
        CurrentRound = 1;
        PlayerDamageDealt = 0;
        PlayerBuildingsDestroyed = 0;
        PlayerTacticsUsed = 0;
    }

    // ================================================================ 疲劳(§5.4.1)

    /// <summary>第 count 次疲劳的伤害:2→4→6→8…(次数从 1 开始,双方各自独立计数)</summary>
    public static int FatigueDamageFor(int count) => Mathf.Max(0, count) * 2;

    /// <summary>
    /// 牌堆摸空还继续抽 → 疲劳结算:扣的是**摸不到牌那一方自己的大营**(§5.4.1)。
    /// 这笔伤害不是攻击:不触发反击、不受守护转移、不吃重甲减免、与军械库 Debuff 无关,直扣大营 HP。
    /// </summary>
    public static void TriggerFatigue(BattleSide side, int count)
    {
        int damage = FatigueDamageFor(count);
        Debug.Log($"[结算] {BattleRules.SideName(side)}第 {count} 次疲劳(牌堆已空):大营直扣 {damage} 点");

        EventManager.Trigger(new FatigueEventArgs
        {
            Side = side,
            Count = count,
            Damage = damage,
        });

        DealCampDamage(side, damage, $"牌库耗尽（第 {count} 次疲劳）");
    }

    // ================================================================ 攻击(§7.1)

    /// <summary>
    /// 一次完整攻击(§7.1.2 精确顺序):基础伤害 → 减伤 → 扣血 → 死亡判定 → 反击判定 → 血战回血。
    /// 非法攻击(不在射程、被守护)什么都不做并返回 false,reason 说明原因。
    /// </summary>
    public static bool Attack(FieldUnit attacker, FieldUnit target, out string reason)
    {
        reason = null;
        if (MatchOver) { reason = "对局已经结束"; return false; }
        if (!BattleRules.CanAttack(attacker, target, out reason)) return false;

        int damage = Mathf.Max(0, attacker.Atk);
        int dealt = DealDamage(target, damage, attacker, isAttack: true);
        Debug.Log($"[结算] 「{attacker.DisplayName}」攻击「{target.DisplayName}」:造成 {dealt} 点伤害" +
                  $"（剩余 HP {target.Hp}/{target.MaxHp}）");

        // 攻得动却一点血都没掉 = 一定有地方不对(重甲最多把伤害减到 0,不该悄无声息)。这种要吼出来
        if (dealt <= 0 && damage > 0 && target.InvincibleRounds <= 0)
        {
            Debug.LogError($"[结算] 数值异常:「{attacker.DisplayName}」(ATK {damage})攻击「{target.DisplayName}」" +
                           $"造成 0 点伤害 —— 目标 HP {target.Hp}/{target.MaxHp},重甲 {target.DamageReduction} 层" +
                           $",无敌 {target.InvincibleRounds} 回合。检查伤害结算链。");
        }

        if (attacker.Side == BattleSide.Player) PlayerDamageDealt += dealt;

        bool targetDead = !target.IsAlive;
        if (targetDead) KillUnit(target);

        // §7.1.2 第 7 步:同类互攻才反击(近战↔近战、远程↔远程);目标已经死了就不反击
        if (!targetDead && BattleRules.Counters(attacker, target) && target.Atk > 0)
        {
            int counter = DealDamage(attacker, target.Atk, target, isAttack: true);
            Debug.Log($"[结算] 「{target.DisplayName}」反击「{attacker.DisplayName}」:造成 {counter} 点伤害" +
                      $"（剩余 HP {attacker.Hp}/{attacker.MaxHp}）");

            if (!attacker.IsAlive) KillUnit(attacker);
        }

        // §4.3 血战:攻击后回复 2 HP(攻击者还活着才算)
        if (attacker.IsAlive && BattleRules.HasKeyword(attacker.Data, Keyword.BloodBattle))
        {
            int hpBefore = attacker.Hp;
            int healed = attacker.ApplyHpDelta(2);
            if (healed > 0)
            {
                attacker.ShowNumber(healed, healing: true, hpBefore, attacker.Hp);
                Debug.Log($"[结算] 「{attacker.DisplayName}」触发「血战」,回复 {healed} 点 HP");
            }
        }

        return true;
    }

    /// <summary>
    /// 制造伤害(不动反击):重甲每层 -1(§4.3),无敌建筑免疫(§7.3)。
    /// 返回实际扣掉的血量。isAttack = true 时算「攻击」,用于结算面板的伤害统计。
    /// </summary>
    public static int DealDamage(FieldUnit target, int amount, FieldUnit source = null, bool isAttack = false)
    {
        if (target == null || amount <= 0 || !target.IsAlive) return 0;

        if (target.InvincibleRounds > 0)
        {
            Debug.Log($"[结算] 「{target.DisplayName}」处于无敌状态,免疫 {amount} 点伤害（§7.3）");
            return 0;
        }

        int reduction = target.DamageReduction;
        int final = Mathf.Max(0, amount - reduction);   // §4.3:重甲只减到 0,不会变成治疗
        if (reduction > 0)
            Debug.Log($"[结算] 「{target.DisplayName}」重甲 {reduction} 层:{amount} → {final}");

        int hpBefore = target.Hp;
        int lost = -target.ApplyHpDelta(-final);        // 返回的是负数,取反 = 实际掉的血
        if (lost > 0) target.ShowNumber(lost, healing: false, hpBefore, target.Hp);

        return lost;
    }

    /// <summary>
    /// 直接伤害(不走重甲、不留 0 点):策略卡的固定伤害走这里。
    /// 目前和 DealDamage 的差别只在"不吃重甲"这一点上,§4.3 的重甲只对「受到的伤害」生效,
    /// 策略卡伤害同样算受到伤害 —— 所以这里保留它也吃重甲,只是把日志说清楚。
    /// </summary>
    public static int DealSpellDamage(FieldUnit target, int amount)
        => DealDamage(target, amount, null, isAttack: false);

    /// <summary>回血(建筑修缮、兵牌治疗)。到上限就不再涨,返回实际回了多少</summary>
    public static int Heal(FieldUnit target, int amount)
    {
        if (target == null || amount <= 0 || !target.IsAlive) return 0;

        int hpBefore = target.Hp;
        int healed = target.ApplyHpDelta(amount);
        if (healed > 0) target.ShowNumber(healed, healing: true, hpBefore, target.Hp);
        return healed;
    }

    // ================================================================ 死亡与建筑被毁(§7.1.4 / §2.3)

    /// <summary>
    /// 单位死亡:移出战场、直接退场(本局不再回到牌堆,§5.4),不进入任何区域。
    /// 建筑被打空走这里会改成「被毁但不移除」—— 见 OnBuildingDestroyed。
    /// </summary>
    public static void KillUnit(FieldUnit unit)
    {
        if (unit == null || unit.IsAlive) return;

        if (unit.IsBuilding) { OnBuildingDestroyed(unit); return; }

        var row = unit.Row;
        if (row != null)
        {
            row.RemoveUnit(unit);
            row.HasEnemyRaider = row.ContainsRaider();      // 袭扰标记跟着走(§8.1.1 靠它)
        }

        Debug.Log($"[结算] 「{unit.DisplayName}」阵亡,移出战场(直接退场,不回牌堆)");
        Object.Destroy(unit.gameObject);
    }

    /// <summary>
    /// 建筑被毁(§2.3):不移除、留在链上占位(维修卡可以把它修回来),
    /// 但不可被指定为目标,并且立刻向它的主人施加 Debuff。
    /// </summary>
    private static void OnBuildingDestroyed(FieldUnit building)
    {
        var owner = building.Side;

        if (building.IsCamp)
        {
            // §2.2 / §2.7:大营 HP 归零 → 立即判负,这是唯一的胜负条件
            EndMatch(owner == BattleSide.Player ? BattleSide.Enemy : BattleSide.Player,
                     $"{(owner == BattleSide.Player ? "我方" : "敌方")}大营被攻破");
            return;
        }

        building.MarkWrecked();
        Debug.Log($"[结算] {BattleRules.SideName(owner)}的「{building.BuildingName}」被摧毁(§2.3 Debuff 生效)");

        if (owner == BattleSide.Enemy) PlayerBuildingsDestroyed++;

        if (building.BuildingName == BattleRules.ArsenalName)
        {
            // 军械库:敌方全场兵牌和后续上场兵牌 ATK -1(军械尽失,武备废弛)
            var targetSide = owner == BattleSide.Player ? BattleSide.Enemy : BattleSide.Player;
            var b = Board;
            if (b != null) b.AddArsenalPenalty(targetSide);

            Debug.Log($"[结算] 军械库被毁:{BattleRules.SideName(targetSide)}全场兵牌 ATK -1");
        }
        else if (building.BuildingName == BattleRules.GranaryName)
        {
            // 粮草:敌方 CP 上限 -2(粮草被焚,补给断绝)。受伤的是**建筑主人的对面**那一方,
            // 双方对称,所以走同一段代码。
            var victim = owner == BattleSide.Player ? BattleSide.Enemy : BattleSide.Player;

            GetPlayerState(victim)?.ReduceMax(2);
            if (victim == BattleSide.Player) RefreshLocalHud();
            else EnemyDeckController.Instance?.NotifyCpChanged();

            Debug.Log($"[结算] 粮草被焚:{BattleRules.SideName(victim)}CP 上限 -2");
        }
    }

    /// <summary>
    /// 大营直接掉血(疲劳 / 以后的大营直伤效果)。HP 归零 → 立即判负(§2.2)。
    /// 返回实际扣了多少。
    /// </summary>
    public static int DealCampDamage(BattleSide side, int amount, string cause)
    {
        if (MatchOver || amount <= 0) return 0;

        var camp = FindCamp(side);
        if (camp == null)
        {
            Debug.LogWarning($"[结算] 找不到{BattleRules.SideName(side)}的大营,{amount} 点伤害落空了。");
            return 0;
        }

        int campHpBefore = camp.Hp;
        int lost = -camp.ApplyHpDelta(-amount);
        if (lost > 0) camp.ShowNumber(lost, healing: false, campHpBefore, camp.Hp);

        Debug.Log($"[结算] {BattleRules.SideName(side)}大营受到 {lost} 点伤害（{cause}）,剩余 {camp.Hp}/{camp.MaxHp}");

        if (!camp.IsAlive)
            EndMatch(side == BattleSide.Player ? BattleSide.Enemy : BattleSide.Player,
                     $"{BattleRules.SideName(side)}大营被打空");

        return lost;
    }

    /// <summary>找某一方的大营</summary>
    public static FieldUnit FindCamp(BattleSide side)
    {
        var b = Board;
        if (b == null) return null;
        return b.FindBuilding(side, BattleRules.CampName);
    }

    // ================================================================ 回合维护(§2.4 / §7.4 / §7.3)

    /// <summary>轮次推进(双方各走一次算一轮)。临时 buff 的失效轮次按它算</summary>
    public static void SetRound(int round) => CurrentRound = Mathf.Max(1, round);

    /// <summary>
    /// 某一方的回合开始时的维护:
    ///   · 过期的临时 buff 从当前值上扣回去;
    ///   · 重置 AP(刚部署且没有「闪击」的按 0 算,§2.5);
    ///   · 袭扰骑兵:ATK +1 并受到 2 点伤害(§7.4,从进入后的下一个回合开始);
    ///   · 无敌护盾走一格(§7.3:1 个敌方回合 + 1 个己方回合)。
    /// </summary>
    public static void BeginTurnFor(BattleSide side)
    {
        if (MatchOver) return;

        var units = AllUnitsSnapshot();
        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit == null || !unit.IsAlive) continue;

            if (unit.Side != side) continue;

            unit.ExpireTempBuffIfDue(CurrentRound);

            if (unit.IsUnit)
            {
                unit.ResetForNewTurn();

                if (unit.IsRaiding)
                {
                    unit.TickRaidRaidUpkeep();
                    Debug.Log($"[结算] 袭扰骑兵「{unit.DisplayName}」:ATK +1(现 {unit.Atk}),受到 2 点自损(剩 {unit.Hp} HP)");
                    if (!unit.IsAlive) KillUnit(unit);
                }
            }
            else if (unit.InvincibleRounds > 0)
            {
                unit.ConsumeInvincibleRound();
            }
        }
    }

    /// <summary>某一方的回合结束:清「本回合」标记、压制倒计时走一格(§2.4 第 4 步)</summary>
    public static void EndTurnFor(BattleSide side)
    {
        var units = AllUnitsSnapshot();
        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit != null && unit.IsAlive && unit.Side == side) unit.EndTurnCleanup();
        }
    }

    /// <summary>场上所有成员的快照(结算里会有人死,不能直接遍历排的活表)</summary>
    public static System.Collections.Generic.List<FieldUnit> AllUnitsSnapshot()
    {
        var list = new System.Collections.Generic.List<FieldUnit>();
        var b = Board;
        if (b == null) return list;

        var rows = b.Rows;
        for (int i = 0; i < rows.Count; i++) list.AddRange(BattleRules.Snapshot(rows[i]));
        return list;
    }

    // ================================================================ 胜负(§8.2)

    /// <summary>结束对局。大营被打空、投降、以后的时间到都会走这里,只生效一次</summary>
    public static void EndMatch(BattleSide winner, string reason)
    {
        if (MatchOver) return;

        MatchOver = true;
        Winner = winner;
        ResultText = BuildResultText(winner, reason);

        Debug.Log($"[结算] 对局结束:{(winner == BattleSide.Player ? "我方胜" : "敌方胜")} —— {reason}");

        var turn = TurnController.Instance;
        if (turn != null) turn.StopTurnAdvance();

        MatchEnded?.Invoke(winner, reason);
    }

    /// <summary>
    /// 结算面板上那段战报:胜负 + 原因 + 我方的统计。
    /// 单出成对外可读的拼接方法,是为了让结算界面(BattleResultUI)不用自己拼字符串、
    /// 以后加统计项也只改这一处。
    /// </summary>
    public static string BuildResultText(BattleSide winner, string reason = null)
    {
        bool playerWon = winner == BattleSide.Player;
        string head = playerWon ? "我方获胜" : "敌方获胜";
        string detail = string.IsNullOrEmpty(reason) ? ResultText : reason;

        var sb = new System.Text.StringBuilder();
        sb.Append(head);
        if (!string.IsNullOrEmpty(detail)) sb.Append($" · {detail}");
        sb.Append('\n');
        sb.Append($"共 {CurrentRound} 轮 · 我方造成伤害 {PlayerDamageDealt} · 拆毁建筑 {PlayerBuildingsDestroyed} · 打出策略牌 {PlayerTacticsUsed}");

        var myCamp = FindCamp(BattleSide.Player);
        var hisCamp = FindCamp(BattleSide.Enemy);
        if (myCamp != null && hisCamp != null)
            sb.Append($"\n我方大营 {myCamp.Hp}/{myCamp.MaxHp} · 敌方大营 {hisCamp.Hp}/{hisCamp.MaxHp}");

        return sb.ToString();
    }

    // ================================================================ 统计

    /// <summary>我方造成的伤害累加(攻击与策略卡都走这里,结算面板的「造成伤害」)</summary>
    public static void AddPlayerDamage(int amount)
    {
        if (amount > 0) PlayerDamageDealt += amount;
    }

    public static void CountTacticPlayed(BattleSide side)
    {
        if (side == BattleSide.Player) PlayerTacticsUsed++;
    }

    // ================================================================ 杂项

    /// <summary>玩家的 PlayerState(谁的都行:isLocal 区分)</summary>
    public static PlayerState GetPlayerState(BattleSide side)
    {
        if (side == BattleSide.Player)
        {
            var cp = CommandPointController.Instance;
            return cp != null ? cp.LocalPlayer : null;
        }

        var enemy = EnemyDeckController.Instance;
        return enemy != null ? enemy.Player : null;
    }

    /// <summary>CP 上限变了之后把 HUD 刷一遍(粮草被焚走这条路)</summary>
    private static void RefreshLocalHud()
    {
        var cp = CommandPointController.Instance;
        if (cp != null) cp.RefreshCpMaxDisplay();
    }
}
