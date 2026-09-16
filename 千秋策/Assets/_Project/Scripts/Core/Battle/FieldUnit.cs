using DG.Tweening;
using TMPro;
using UnityEngine;

/// <summary>
/// 作战区里的一个成员:一张已部署的兵牌,或者一个建筑锚点(大营/军械库/粮草)。
/// 由 BattlefieldManager 部署时挂在"布局格子"上 —— 挂格子而不是挂卡面,
/// 是因为排上的 HorizontalLayoutGroup 排的是格子,链上顺序 = 格子顺序(§2.3)。
///
/// 卡面数据(CardData)是只读的,所以战斗中的一切变化都存在这里:
///   ATK / HP / HP 上限、本回合剩余 AP、重甲层数、临时 buff(带失效轮次)、
///   袭扰状态(§7.4)、无敌回合(§7.3)、压制回合(§4.3 压制)、部署当回合标记(§2.5)。
/// 伤害与死亡的结算不在这里,在 BattleSettlement(§7.1);这里只提供"改状态"的接口。
/// 改完 ATK/HP 会立刻刷战场小卡上的数字(兵牌走 CardDisplay.RefreshBattleStats,建筑走 hpText)。
///
/// 【挂载 & 调整】
///   挂在:场景和预制体里都没有这个组件 —— 是 BattlefieldManager 在落位时 AddComponent 挂到「布局格子」上的:
///         兵牌走 SpawnUnit(格子名 Unit_卡id),建筑走 SpawnBuilding(格子名 Slot_大营 之类),
///         都是 Instantiate 之后立刻 AddComponent + Init / InitAsBuilding + row.AddUnit 登记进排。
///         挂格子而不是挂卡面,是因为排上的 HorizontalLayoutGroup 排的是格子、链上顺序 = 格子顺序。
///   引用:没有要手连的引用;唯一算「接线」的是 hpTextObjectName,它按名字去子物体里找 TMP 文本,
///         必须和预制体里的名字一致(Build.prefab 里叫「Text (TMP)」)。找不到时:建筑会退而取第一个子 TMP 文本,
///         兵牌没有这层兜底;而且这个查找只在 InitAsBuilding / Flush 时才发生 —— 名字写错就是建筑血量数字不刷新,
///         不会报错。另外注意:战场成员身上的 FieldUnit 是运行时 AddComponent 新建的,取的是代码里的初始值,
///         所以在 Inspector 上改这几个字段对 BattlefieldManager 生成出来的成员没有影响(想调值得改这里声明的初始值);
///         Data / Side / Row / Atk / Hp 也都是运行时由 Init 填的,预制体上看不到值。
///   常调:
///     · isBuilding:勾上 = 建筑锚点(不可移动、不可被部署替换、血量走 hpText 刷);落位时 InitAsBuilding 会置 true、
///       Init 不会把它置回 false,而新建的组件一定是 false —— 所以这个勾只在「自己往预制体上挂组件」时有意义,平时不用动。
///     · targetHighlightScale:拖着策略卡、指针停在这个合法目标上时放大到几倍,1 = 不放大;调大 = 更醒目但可能盖住旁边的卡,
///       调到 1 以下反而缩小;只影响表现,不动任何数值,松手/停到别处会补间回原始缩放。
///     · hpTextObjectName:建筑血量角标那个 TMP 文本的名字;只有在 Build.prefab 里改过子物体名字、或者换成另一套建筑预制体时,
///       才需要跟着改(改这里不如直接对齐预制体上的名字,因为运行时新建组件取的还是代码里的初始值)。
///   战斗状态怎么清:每回合开始时由 BattleSettlement.BeginTurnFor 统一调 ResetForNewTurn(重置 AP、过期临时 buff),
///     回合结束时调 EndTurnCleanup(清"本回合部署"标记)。不接回合流程的话,单位会一直带着上一回合的 AP。
/// </summary>
[DisallowMultipleComponent]
public class FieldUnit : MonoBehaviour
{
    [Header("建筑锚点")]
    [Tooltip("大营/军械库/粮草:不可移动、不可被部署替换(策划案§2.3)")]
    [SerializeField] private bool isBuilding;

    [Header("建筑血量")]
    [Tooltip("建筑的血数字写在子物体的这个 TMP 文本上(Build.prefab 里叫「Text (TMP)」)")]
    [SerializeField] private string hpTextObjectName = "Text (TMP)";

    [Header("被指定为目标时")]
    [Tooltip("指针拖着策略卡停在这个成员上时放大多少(1 = 不放大)")]
    [SerializeField] private float targetHighlightScale = 1.12f;

    [Header("数值飘字")]
    [Tooltip("受击/回血时在头顶飘一个数字。关掉就只在日志里看结算")]
    [SerializeField] private bool showFloatingNumbers = true;

    private Vector3 baseScale = Vector3.one;
    private bool targetHighlighted;
    private TMP_Text hpText;
    private bool hpTextLookupDone;
    private bool hpTextWarned;
    private CardDisplay display;

    /// <summary>受击抖动(同时只允许一个,连击时不叠着抖)</summary>
    private Sequence hitSequence;
    private bool hitShaking;

    public CardData Data { get; private set; }
    public BattleSide Side { get; private set; }
    public BattleRow Row { get; private set; }
    public bool IsBuilding => isBuilding;
    public int Atk { get; private set; }
    public int Hp { get; private set; }
    public int MaxHp { get; private set; }

    /// <summary>建筑的名字(大营/军械库/粮草)。兵牌没有这个,名字走卡面</summary>
    public string BuildingName { get; private set; }

    public string DisplayName => Data != null ? Data.cardName
                            : !string.IsNullOrEmpty(BuildingName) ? BuildingName
                            : name;

    // ================================================================ 战斗状态(§2.5 / §4.3 / §7.3 / §7.4)

    /// <summary>本回合还剩几点行动(AP)。攻击或移动各扣 1,§2.5</summary>
    public int Ap { get; private set; }

    /// <summary>本回合的 AP 上限 = 行动次数(骑兵 2、连战 2、其余 1)</summary>
    public int ApMax { get; private set; }

    /// <summary>压制剩余回合数(§4.3 压制):> 0 时不能行动</summary>
    public int SuppressTurns { get; private set; }

    /// <summary>无敌剩余「轮」数(§7.3 维修后的护盾):> 0 时免疫一切伤害且不能被选为目标</summary>
    public int InvincibleRounds { get; private set; }

    /// <summary>是不是刚部署上来(§2.5:当回合不能行动,除非带「闪击」)</summary>
    public bool DeployedThisTurn { get; private set; }

    /// <summary>
    /// 袭扰状态(§7.4):骑兵从共享前军进入敌方中军后为真。
    /// 此时只能攻击敌方后军建筑、被攻击不反击、每回合开始 ATK+1 并自损 2。
    /// </summary>
    public bool IsRaiding { get; private set; }

    /// <summary>袭扰累计的 ATK 加成(撤回时一并清除,§7.4)</summary>
    public int RaidAtkBonus { get; private set; }

    /// <summary>进入袭扰的那个回合(0 = 不在袭扰)。还在"刚进来"这个回合的骑兵不许撤回</summary>
    public int RaidStartedRound { get; private set; }

    /// <summary>重甲层数(§4.3:每层伤害 -1)。Keyword 只有单一 HeavyArmor 档位,层数由 BattlefieldManager 落位时读卡面文案标出来</summary>
    public int HeavyArmor { get; private set; }

    /// <summary>本回合临时加的 ATK / HP(失效时从当前值上扣回去)</summary>
    private int tempAtk, tempHp;

    /// <summary>临时 buff 到哪一轮失效(0 = 没有)</summary>
    private int tempBuffExpireRound;

    // ================================================================ 查询

    /// <summary>兵牌(不是建筑)</summary>
    public bool IsUnit => !isBuilding;

    /// <summary>还活着</summary>
    public bool IsAlive => Hp > 0;

    /// <summary>建筑已经被摧毁(留在链上占位,但不可被指定为目标;维修卡能把它修回来,§2.3 / §7.3)</summary>
    public bool IsWrecked { get; private set; }

    /// <summary>大营(§2.2:HP 归零立即判负)</summary>
    public bool IsCamp => isBuilding && BuildingName == BattleRules.CampName;

    public bool IsAliveAndWell => IsAlive && !IsBuilding;

    /// <summary>还能行动吗(有 AP、没被压制、不是刚部署、不是无敌中的建筑之类)</summary>
    public bool CanActNow => IsUnit && IsAlive && Ap > 0 && SuppressTurns <= 0;

    /// <summary>当前有没有本回合的临时增益(表现层可以据此画 buff 图标)</summary>
    public bool HasTempBuff => tempAtk != 0 || tempHp != 0;

    public int TempAtk => tempAtk;
    public int TempHp => tempHp;

    /// <summary>承受伤害的减伤层数(只有兵牌吃重甲,建筑不吃)</summary>
    public int DamageReduction => IsUnit ? HeavyArmor : 0;

    // ================================================================ 落位

    /// <summary>
    /// 部署进排里时调用(data = 卡面,row = 落在哪条排,view = 战场小卡的表现层)。
    /// side 必须**由调用方显式给出**:前军是双方共享的那一条,它的 row.Side 恒为 Player ——
    /// 按 row.Side 推阵营的话,敌方单位一旦走进共享前军就会变成"我方",直接不能互相攻击。
    /// </summary>
    public void Init(CardData data, BattleRow row, BattleSide side, CardDisplay cardView = null)
    {
        Data = data;
        display = cardView;
        Row = row;
        Side = side;
        Atk = data != null ? data.atk : 0;
        Hp = data != null ? data.hp : 0;
        MaxHp = Hp;
        baseScale = transform.localScale;

        Ap = 0;
        ApMax = data != null ? BattleRules.ActionPointsOf(data) : 1;
        DeployedThisTurn = true;

        // 悬停预览要知道"这张牌现在在场上是谁":预览显示的是当前攻/血,不是卡面数值
        // (战场卡的悬停组件就挂在卡面根上,由 BattlefieldManager 决定开不开)
        var hover = cardView != null ? cardView.GetComponent<CardHover>() : null;
        if (hover != null) hover.Bind(this);

        Flush();
    }

    /// <summary>
    /// 建筑锚点(不可移动、不可被替换)。
    /// hp 是**起始血量**(大营 20 / 军械库 5 / 粮草 5),同时作为「修缮的上限」 ——
    /// 建筑不会像兵牌那样被加临时上限,所以两个值在建筑上是一回事:打空成废墟,修回来就是这个数。
    /// </summary>
    public void InitAsBuilding(string displayName, int hp, BattleRow row, BattleSide side)
    {
        Data = null;
        display = null;
        Row = row;
        Side = side;
        isBuilding = true;
        BuildingName = displayName;
        Atk = 0;
        Hp = Mathf.Max(1, hp);
        MaxHp = Hp;
        baseScale = transform.localScale;

        Ap = 0;
        ApMax = 0;
        Flush();
    }

    /// <summary>换排。**只改位置,不改阵营** —— 阵营是落位时定死的,不会因为走进前军就变</summary>
    public void SetRow(BattleRow row)
    {
        Row = row;
    }

    // ================================================================ 数值变更(只由 BattleSettlement / CardEffectResolver 调)

    /// <summary>改血量(建筑挨打、修缮都走这里),顺手刷角标/卡面上的数字</summary>
    public void SetHp(int value)
    {
        Hp = Mathf.Max(0, value);
        if (Hp > MaxHp) MaxHp = Hp;
        Flush();
    }

    /// <summary>
    /// 受击结算用:直接扣 HP,并保证 HP 不超过上限。
    /// 返回值只是「实际扣了多少」,减伤与死亡判定在 BattleSettlement(§7.1.2)。
    /// </summary>
    public int ApplyHpDelta(int delta)
    {
        if (delta == 0) return 0;

        int before = Hp;
        Hp = Mathf.Min(MaxHp, Hp + delta);
        if (Hp < 0) Hp = 0;
        Flush();
        return Hp - before;
    }

    /// <summary>ATK 的增减(军械库被毁的 -1 Debuff、袭扰的每回合 +1、临时增益都走这里)</summary>
    public void ApplyAtkDelta(int delta)
    {
        if (delta == 0) return;
        Atk = Mathf.Max(0, Atk + delta);
        Flush();
    }

    /// <summary>HP 上限的增减(临时 buff 加 HP 时上限也跟着涨,失效时一起收回去)</summary>
    public void ApplyMaxHpDelta(int delta)
    {
        MaxHp = Mathf.Max(1, MaxHp + delta);
        if (Hp > MaxHp) Hp = MaxHp;
        Flush();
    }

    /// <summary>
    /// 加一条临时增益(策划案§4.3 buff 类;卡面写「本回合」= 1 轮)。
    /// 同时给 ATK 与 HP 上限加值并回满这部分 HP —— 和「ATK+2、HP+3」的读法一致:
    /// 那 3 点 HP 是加上限,不是治疗。
    /// </summary>
    public void AddTempBuff(int atk, int hp, int rounds, int currentRound)
    {
        if (atk == 0 && hp == 0) return;

        tempAtk += atk;
        if (atk != 0) ApplyAtkDelta(atk);

        if (hp != 0)
        {
            tempHp += hp;
            ApplyMaxHpDelta(hp);
            if (hp > 0) ApplyHpDelta(hp);
        }

        tempBuffExpireRound = Mathf.Max(tempBuffExpireRound, currentRound + Mathf.Max(1, rounds));
        Flush();
    }

    /// <summary>
    /// 检查临时增益是否过期(每次进入新回合时由 BattleSettlement 调)。
    /// 到点就从当前值上扣回加过的那部分,恢复卡面基础数值。
    /// </summary>
    public bool ExpireTempBuffIfDue(int currentRound)
    {
        if (tempBuffExpireRound <= 0 || currentRound < tempBuffExpireRound) return false;

        if (tempAtk != 0) ApplyAtkDelta(-tempAtk);
        if (tempHp != 0)
        {
            ApplyHpDelta(-tempHp);
            ApplyMaxHpDelta(-tempHp);
        }

        tempAtk = 0;
        tempHp = 0;
        tempBuffExpireRound = 0;
        return true;
    }

    /// <summary>重甲层数(落位时由 BattlefieldManager 按卡面关键词标出来)</summary>
    public void SetHeavyArmor(int layers) => HeavyArmor = Mathf.Max(0, layers);

    /// <summary>压制:接下来 N 个自己的回合不能行动(§4.3 压制)。同时把当前 AP 清空</summary>
    public void ApplySuppress(int turns)
    {
        SuppressTurns = Mathf.Max(SuppressTurns, Mathf.Max(1, turns));
        Ap = 0;
        Flush();
    }

    /// <summary>无敌护盾(§7.3 维修后 1 轮):期间免疫伤害、不能被指定为目标</summary>
    public void GrantInvincible(int rounds) => InvincibleRounds = Mathf.Max(InvincibleRounds, Mathf.Max(1, rounds));

    /// <summary>走完一轮,护盾层数 -1</summary>
    public void ConsumeInvincibleRound()
    {
        if (InvincibleRounds > 0) InvincibleRounds--;
    }

    // ================================================================ 回合与行动(§2.5)

    /// <summary>进入新回合:重置 AP。刚部署且没有「闪击」的当回合按 0 算(§2.5)</summary>
    public void ResetForNewTurn()
    {
        if (!IsUnit) { Ap = 0; ApMax = 0; return; }

        ApMax = Data != null ? BattleRules.ActionPointsOf(Data) : 1;
        Ap = (DeployedThisTurn && !BattleRules.HasKeyword(Data, Keyword.Blitz)) ? 0 : ApMax;

        if (SuppressTurns > 0) Ap = 0;      // 被压制:这个回合直接没有 AP
    }

    /// <summary>回合结束:清「本回合部署」标记(§2.4 第 4 步),并让压制倒计时走一格</summary>
    public void EndTurnCleanup()
    {
        DeployedThisTurn = false;
        if (SuppressTurns > 0) SuppressTurns--;
    }

    /// <summary>花掉 1 点 AP(移动/攻击各一次)。成功返回 true,AP 不够返回 false</summary>
    public bool SpendActionPoint(int count = 1)
    {
        if (Ap < count) return false;
        Ap -= count;
        Flush();
        return true;
    }

    /// <summary>把这回合的 AP 直接给满(闪击单位部署当回合立即行动时用)</summary>
    public void GrantFullAp()
    {
        Ap = ApMax;
        DeployedThisTurn = false;
        Flush();
    }

    // ================================================================ 袭扰(§7.4)

    /// <summary>
    /// 进入袭扰状态(骑兵踏进敌方中军)。
    /// 记下进入的回合数:袭扰的代价(每回合自损 2、ATK+1)要到**下一个自己的回合**才结算,
    /// 所以必须有"刚进来"这个信息 —— 否则骑兵可以进去一趟、下回合立刻撤回,一点代价都不付,
    /// 在前军和敌方中军之间来回弹(见 BattleRules.CanMoveTo 的撤回条件)。
    /// </summary>
    public void EnterRaid(int round)
    {
        IsRaiding = true;
        RaidStartedRound = Mathf.Max(1, round);
        Flush();
    }

    /// <summary>
    /// 袭扰的每回合结算(§7.4):进入敌方中军后的下一个回合开始,ATK+1 并受到 2 点伤害。
    /// 由 BattleSettlement 在自己的回合开始时调。
    /// </summary>
    public void TickRaidRaidUpkeep()
    {
        if (!IsRaiding) return;

        RaidAtkBonus++;
        ApplyAtkDelta(1);
        ApplyHpDelta(-2);       // 孤军断粮:固定 2 点,不吃重甲、不触发反击(它不是攻击)
        Flush();
    }

    /// <summary>撤回己方前军:袭扰状态与累计 ATK 加成一并清除(§7.4)</summary>
    public void ExitRaid()
    {
        if (!IsRaiding) return;

        if (RaidAtkBonus != 0) ApplyAtkDelta(-RaidAtkBonus);
        RaidAtkBonus = 0;
        IsRaiding = false;
        RaidStartedRound = 0;
        Flush();
    }

    // ================================================================ 表现

    /// <summary>
    /// 建筑被摧毁(§2.3):留在链上占位(维修卡可以修回来),但已经不能被打、也不能被指定为目标。
    /// 表现上压暗 + 关掉射线,免得玩家还去点它。
    /// </summary>
    public void MarkWrecked()
    {
        IsWrecked = true;

        var graphics = GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
        for (int i = 0; i < graphics.Length; i++)
        {
            if (graphics[i] == null) continue;
            var c = graphics[i].color;
            graphics[i].color = new Color(c.r * 0.45f, c.g * 0.45f, c.b * 0.45f, c.a);
            graphics[i].raycastTarget = false;
        }

        Flush();
    }

    /// <summary>被摧毁后又修回来:解除废墟状态(§7.3 维修卡;原色由维修时的重建逻辑负责)</summary>
    public void ClearWrecked()
    {
        IsWrecked = false;
        Flush();
    }

    /// <summary>把当前 ATK/HP 刷到卡面或建筑角标上</summary>
    public void Flush()
    {
        if (isBuilding)
        {
            RefreshHpText();
            WarnIfHpTextMissing();
        }
        if (display != null) display.RefreshStats(Atk, Hp, MaxHp);
    }

    /// <summary>
    /// 建筑角标上现在显示的血量文本(null = 没找到那个 TMP 文本)。
    /// 给自检/调试对照用:数值在代码里是对的、但角标不跟着变时,用它一眼看出是哪一层断了。
    /// </summary>
    public string DisplayedHpText => isBuilding ? (hpText != null ? hpText.text : null) : null;

    /// <summary>角标文本找不到时只提醒一次 —— 那意味着建筑血量数字不会刷新(名字写错或预制体换了)</summary>
    private void WarnIfHpTextMissing()
    {
        if (hpText != null || hpTextWarned) return;
        hpTextWarned = true;

        Debug.LogWarning($"[FieldUnit] 「{DisplayName}」身上找不到名为「{hpTextObjectName}」的血量文本，" +
                         "建筑血量数字不会刷新。检查 Build.prefab 里那个 TMP 文本的名字（或改了名的子物体）。", this);
    }

    /// <summary>
    /// 在世界坐标处飘一个数值(受击红、回血绿)。给 BattleSettlement 用。
    /// hpBeforeHpAfter 传进来时,飘字会写成「-3（5→2）」—— 加上底下这一下抖动,
    /// 「到底有没有结算」不用去翻 Console 也看得出来。
    /// </summary>
    public void ShowNumber(int amount, bool healing, int hpBefore = -1, int hpAfter = -1)
    {
        if (amount == 0) return;

        if (hpBefore >= 0 && hpAfter >= 0) HitShake();

        if (!showFloatingNumbers) return;

        // 战场成员都是画布下的 UI 物体(格子),所以世界坐标直接就是屏幕坐标;
        // 画布是 WorldSpace/ScreenSpace-Camera 时再按它自己的相机换算一次。
        var canvas = CanvasUtil.FindRootCanvas();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

        Vector3 screen = camera != null
            ? camera.WorldToScreenPoint(transform.position)
            : RectTransformUtility.WorldToScreenPoint(null, transform.position);

        string text = healing ? $"+{amount}" : $"-{amount}";
        if (hpBefore >= 0 && hpAfter >= 0) text += $"（{hpBefore}→{hpAfter}）";

        FloatingTipUI.Show(screen, text, warning: !healing);
    }

    /// <summary>挨打时抖一下:让「这一下确实结算了」有肉眼可见的反馈</summary>
    private void HitShake()
    {
        if (hitShaking) return;          // 反击/连击连着来时别叠着抖

        hitShaking = true;
        hitSequence?.Kill();
        hitSequence = DOTween.Sequence()
            .Append(transform.DOPunchPosition(new Vector3(8f, 0f, 0f), 0.18f, 12, 0.9f))
            .OnComplete(() => hitShaking = false);
    }

    /// <summary>
    /// 建筑角标上的血量数字。只在建筑上找 —— 兵牌卡面里也有好几段 TMP 文本,
    /// 乱抓会把卡名/攻击力当血量写坏。
    /// </summary>
    private void RefreshHpText()
    {
        // 只在第一次(或还没找到时)查一次子物体;找不到就交给 WarnIfHpTextMissing 提醒一次
        if (hpText == null && !hpTextLookupDone)
        {
            hpTextLookupDone = true;

            var texts = GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
                if (texts[i] != null && texts[i].name == hpTextObjectName) { hpText = texts[i]; break; }

            if (hpText == null) hpText = GetComponentInChildren<TMP_Text>(true);
        }

        if (hpText != null) hpText.text = Hp.ToString();
    }

    /// <summary>拖动策略卡时,把指针底下的合法目标稍微放大一点(§10.2 的"目标高亮")</summary>
    public void SetTargetHighlight(bool on)
    {
        if (targetHighlighted == on) return;
        targetHighlighted = on;

        transform.DOKill();
        transform.DOScale(baseScale * (on ? targetHighlightScale : 1f), 0.12f).SetEase(Ease.OutQuad);
    }

    private void OnDisable()
    {
        // 被击毁/排被清空:把缩放还给预制体的值,免得下次复用带着高亮
        transform.DOKill();
        transform.localScale = baseScale;
    }
}
