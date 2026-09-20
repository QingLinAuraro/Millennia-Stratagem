using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 牌堆 + 手牌的数据层，并把牌堆 UI 的刷新接起来。
///
/// 抽牌入口是公开的 DrawOne() / DrawCards(n)：我方回合开始时由 CommandPointController 调它们
/// （本脚本自己不订阅回合事件），「摸牌N」策略卡也直接调这两个方法，把 drawOnKeyD 关掉就不再吃键盘。
///
/// 【挂载 & 调整】
///   挂在:BattleCanvas/Deck(场景 Assets/_Project/Scenes/Battle.unity,Deck 是 BattleCanvas 的直接子物体,
///         上面只有本脚本 + 一个 RectTransform;不会自举,忘了挂就是全场景没有我方牌堆账)。
///         回合流转由 TurnController 统一驱动,但本脚本不自己订阅它的 TurnStarted ——
///         我方的「开局抽 5 张」和「每回合抽 1 张」都是 CommandPointController(它订阅了 TurnStarted)回头调
///         这里的 DrawCards() / DrawOne();所以 CommandPointController 一掉线,本脚本就只剩按调试键这一个抽牌入口。
///   引用:deckList 必须手连(场景里现在拖了 12 张)。留空或 0 张:Start() 打 LogError,牌堆开局就是 0 张,
///         之后每次抽牌都走疲劳 —— 数据上不崩,但对局直接跑歪。敌方 EnemyDeckController 的构筑留空时会反过来
///         借这里的 deckList(FindObjectOfType<DeckController>()),所以这里的卡组别留空,否则两边一起空。
///         deckUI 必须手连(场景里连的是 cards1 上的 DeckUI)。代码不会自动找:留空不报警告,
///         但牌堆剩余张数在表现层永远不刷新(数据照常少),看着就像抽牌没生效。
///   常调:· deckList:卡组张数。现在 12 张,以后换 30 张;张数越少越容易空牌堆 —— 一空就走 TriggerFatigue,
///           把疲劳次数 +1 并打日志(2/4/6/8 递增的那笔自伤还没接到大营 HP,现在只有计数和日志),
///           想验策划案§5.4.1 的疲劳就故意只留 1~2 张。
///         · deckUI:换牌堆 HUD、重搭场景后要重新拖。牌背「越来越薄」的视觉和数量都在 DeckUI 上,这里只管递计数。
///         · drawOnKeyD:调试开关,默认开。正式靠回合流程摸牌后关掉,免得手一抖按到 D 多摸一张。
///         · debugDrawKey:调试抽牌键,默认 D。和抬费用(=)、过回合(N)、敌方出牌(M)的调试键撞了可以换。
/// </summary>
public class DeckController : MonoBehaviour
{
    [Header("构筑(暂时拖12张,以后换成30张卡组)")]
    [Tooltip("我方卡组,至少 1 张。留空或 0 张:Start() 报错,牌堆开局 0 张,之后每次抽牌都算疲劳。\n" +
             "敌方 EnemyDeckController 的构筑留空时会借这一套,所以这里别空着")]
    [SerializeField] private List<CardData> deckList;
    [Tooltip("己方牌堆 HUD(cards1 上的 DeckUI)。必须手连:留空不会自动找也不报警告,\n" +
             "牌堆剩余张数在表现层永远不刷新(数据照常减少)")]
    [SerializeField] private DeckUI deckUI;

    [Header("调试")]
    [Tooltip("仅调试用:按 debugDrawKey 摸一张。正式接回合流程后关掉")]
    [SerializeField] private bool drawOnKeyD = true;
    [Tooltip("调试抽牌键,配合 drawOnKeyD 用,默认 D。\n" +
             "和抬费用(=)/ 过回合(N)/ 敌方出牌(M)的调试键撞了可以换一个")]
    [SerializeField] private KeyCode debugDrawKey = KeyCode.D;

    private readonly Deck deck = new();
    private readonly Hand hand = new();
    private readonly PlayerState localPlayer = new PlayerState(isLocal: true);

    /// <summary>疲劳触发次数。每名玩家各自独立计数,永不重置(策划案§5.4.1)</summary>
    private int fatigueCount;

    public int DeckCount => deck.Count;
    public int HandCount => hand.Count;
    public int FatigueCount => fatigueCount;
    public Deck Pile => deck;

    /// <summary>本地玩家的构筑。敌方(EnemyDeckController)没单独配卡组时会借这套来测试对局</summary>
    public IReadOnlyList<CardData> DeckList => deckList;

    /// <summary>本地玩家的手牌账(只读,给调试/表现层看)</summary>
    public IReadOnlyList<CardData> HandCards => hand.Cards;

    private void Start()
    {
        // 优先用主菜单选定的卡组(BattleContext 是跨场景的中转,见它的说明)。
        // 场景里手拖的 deckList 现在是**兜底** —— 直接从 Battle 场景启动(调试)时才会用到,
        // 那条路没有主菜单,带不进来卡组。
        var source = deckList;
        var fromMenu = BattleContext.PlayerDeck;
        if (fromMenu != null && fromMenu.cards != null && fromMenu.cards.Count > 0)
        {
            source = fromMenu.cards;
            Debug.Log($"[DeckController] 用主菜单选定的卡组「{fromMenu.deckName}」{source.Count} 张");
        }
        else if (source == null || source.Count == 0)
        {
            Debug.LogError("[DeckController] 既没有主菜单带来的卡组(BattleContext 为空)," +
                           "场景里的 deckList 也是空的 —— 牌堆一开局就是 0 张。", this);
        }
        else if (source.Count != DeckPreset.DeckSize)
        {
            Debug.LogWarning($"[DeckController] 用的是场景里手拖的兜底卡组,{source.Count} 张" +
                             $"(规则是 {DeckPreset.DeckSize} 张)。从主菜单进战斗才是正常流程。", this);
        }

        deck.Init(source ?? new List<CardData>());
        RefreshDeckUI();
    }

    private void Update()
    {
        if (drawOnKeyD && Input.GetKeyDown(debugDrawKey)) DrawOne();
    }

    /// <summary>
    /// 打出一张手牌:数据层把它从手牌里拿掉。
    ///
    /// 表现层的卡牌销毁是 HandUI 的事,但**手牌账**在数据层 —— 不在这里扣掉,
    /// 打出去的牌会一直算在手牌里,手牌上限 7 提前顶满,摸牌会被误判成"手牌已满"
    /// 而直接退场(策划案§5.4:无弃牌堆,打出的牌直接退场)。
    /// </summary>
    public bool PlayCard(CardData card)
    {
        if (card == null) return false;

        bool removed = hand.Remove(card);
        if (!removed) Debug.LogWarning($"[DeckController] 打出的「{card.cardName}」不在手牌账里,手牌数没变。", this);
        return removed;
    }

    /// <summary>摸 1 张。</summary>
    public void DrawOne() => DrawCards(1);

    /// <summary>
    /// 摸 count 张。回合开始摸牌、「摸牌N」策略卡都走这里。
    /// 每一张单独结算:牌堆空 → 各自触发一次疲劳;手牌满 → 那一张直接退场。
    /// </summary>
    public void DrawCards(int count)
    {
        for (int i = 0; i < count; i++) DrawSingle();
    }

    private void DrawSingle()
    {
        var card = deck.Draw();

        // 牌堆先刷新:不管摸到的牌是进手牌还是直接退场,牌堆都实实在在少了一张。
        // (原来这行在手牌满的 return 之后,于是手牌满时 UI 不更新,
        //  下次成功摸牌时计数会一次跳 2 —— 这就是"计数错误"的来源)
        RefreshDeckUI();

        if (card == null)
        {
            TriggerFatigue();
            return;
        }

        if (!hand.TryAdd(card))
        {
            // 策划案§5.4:手牌上限 7,超出的牌直接退场(没有弃牌堆)
            Debug.Log($"[DeckController] 手牌已满({Hand.MaxSize}张),摸到的「{card.cardName}」直接退场");
            return;
        }

        EventManager.Trigger(new CardDrawnEventArgs { Card = card, Player = localPlayer });
    }

    /// <summary>
    /// 牌堆已空还继续摸 → 疲劳:2/4/6/8/10...递增,扣的是摸牌方自己的大营(策划案§5.4.1)。
    /// 空牌堆摸「摸牌3」= 2+4+6 = 12 点自伤。
    ///
    /// 伤害落到大营 HP 上是在 BattleSettlement.TriggerFatigue 里做的(它还要负责"大营被打空就判负");
    /// 这里只管计数与播报 —— 计数双方各自独立,永不重置。
    /// </summary>
    private void TriggerFatigue()
    {
        fatigueCount++;
        int damage = BattleSettlement.FatigueDamageFor(fatigueCount);

        Debug.Log($"[DeckController] 牌堆已空,第 {fatigueCount} 次疲劳,我方大营受到 {damage} 点伤害");
        FloatingTipUI.Show(DeckTipPosition(), $"牌库已空！大营 -{damage}", warning: true);

        BattleSettlement.TriggerFatigue(BattleSide.Player, fatigueCount);
    }

    /// <summary>疲劳飘字的位置:没有牌堆 HUD 就飘在屏幕下方中间(手牌上方)</summary>
    private Vector2 DeckTipPosition()
    {
        var rect = deckUI != null ? deckUI.transform as RectTransform : null;
        var canvas = CanvasUtil.FindRootCanvas();

        if (rect != null && canvas != null)
        {
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            return RectTransformUtility.WorldToScreenPoint(cam, rect.position);
        }

        return new Vector2(Screen.width * 0.5f, Screen.height * 0.35f);
    }

    /// <summary>牌堆张数变了,刷新 HUD(召唤(牌堆)这类往牌堆里加牌的效果也会调它)</summary>
    public void RefreshDeckCount() => RefreshDeckUI();

    /// <summary>
    /// 往我方牌堆里加一张牌(「召唤(牌堆)」效果,策划案§4.3)。
    /// 加到牌堆顶 = 下一张就摸到,这是"召唤"类卡的本意。
    /// </summary>
    public void AddToDeck(CardData card, int count = 1)
    {
        if (card == null || count <= 0) return;

        for (int i = 0; i < count; i++) deck.Add(card);
        RefreshDeckUI();
        Debug.Log($"[DeckController] 我方牌堆加入「{card.cardName}」×{count},现有 {deck.Count} 张");
    }

    /// <summary>
    /// 随机弃掉一张手牌(策划案§5.4:被弃的牌**直接退场**,不回牌堆、没有弃牌堆)。
    /// 「反间」这类弃牌效果走这里;表现层的手牌由 HandUI 自己收走。
    /// </summary>
    public bool DiscardRandom()
    {
        var cards = hand.Cards;
        if (cards.Count == 0) return false;

        var card = cards[UnityEngine.Random.Range(0, cards.Count)];
        return Discard(card);
    }

    /// <summary>弃掉指定的那张手牌(手牌账 + 表现层一起收)</summary>
    public bool Discard(CardData card)
    {
        if (card == null) return false;
        if (!hand.Remove(card))
        {
            Debug.LogWarning($"[DeckController] 要弃置的「{card.cardName}」不在手牌账里。", this);
            return false;
        }

        Debug.Log($"[DeckController] 我方手牌「{card.cardName}」被弃置,直接退场（§5.4：本局不再回到牌堆）");

        // 表现层:手牌区里那张同名卡也收掉(打出的牌走 BattlefieldManager.ConsumeHandCard,弃牌走这里)
        var hud = FindObjectOfType<HandUI>();
        if (hud != null)
        {
            var spawned = hud.Spawned;
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] != null && spawned[i].Data == card) { hud.RemoveCard(spawned[i]); break; }
            }
        }

        return true;
    }

    private void RefreshDeckUI()
    {
        if (deckUI != null) deckUI.Refresh(deck.Count);
    }
}
