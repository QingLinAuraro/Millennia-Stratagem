using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public enum CardViewMode { Hand, Field }   // 手牌大卡 / 战场小卡

/// <summary>
/// 卡面绑定:把一份 CardData 铺到已经摆好的卡面子物体上。
/// 只认脚本里拖好的这些引用,所有节点名都写死在 Card.prefab 里,不按名字找。
/// </summary>
///
/// 【挂载 & 调整】
///   挂在:Card.prefab 的根物体「Card」上(Assets/_Project/Prefabs/UI/Card.prefab)。
///     它是纯预制体资产:手牌由 HandUI 在手牌区 HandArea1 下 Instantiate,战场小卡由
///     BattlefieldManager 在排里的格子上 Instantiate,预览卡由 CardHoverPreview 实例化在 hover 下,
///     三处都用这同一个预制体。不要在场景里另外挂一份,也不要把本组件单独挪到子物体上。
///     根物体还自带一个嵌套 Canvas(子 Canvas)+ CanvasScaler(ScaleFactor 1)+ LayoutElement,
///     卡面渲染尺寸是「rect 的 150x200 × 根物体 localScale」,所以战场/预览都靠缩放改大小,别去改 rect。
///   引用:15 个引用全部不自动查找,必须手连,而且都要在 Card 自己的层级里(用 prefab 根上的原件)。
///     · borderImage 是唯一有运行时保护的一个:留空 → Bind 时 LogError 且不上色(卡框保持预制体原色);
///       若被拖成了卡牌外部的 Image,ApplyBorderColor 会拒绝上色并报错,免得把游戏背景整块染色。
///     · 其余 14 个留空就是裸引用:其中 11 个在 Bind 当场 NullReferenceException
///       (费用 3 个、卡名/朝代/关键词/效果/插画、兵种字、属性容器、攻击力整格),
///       生命值整格与 atk/hp 两个数字要等 ApplyProperty / RefreshBattleStats 才炸 ——
///       兵牌的 Bind 就会走到,所以实际上也是「第一张牌就报错」,牌面整块绑不上。
///     · illustrationImage 另有一个不会报错的坑:data.artwork 为 null 时它会保留上一张插画,
///       复用同一个实例先看 A 后看 B 会残留 A 的图。
///   常调:
///     · handDeploymentCostFont / handActionCostFont / fieldActionCostFont:三档字号。
///       调大 = 对应档位的数字更醒目;手牌太小看不清调前两个,战场小卡(整体缩 0.7)数字糊就调大第三个。
///       注意 SetActive(false) 的那一格不会设字号,所以战场布局下改手牌那一档看不出任何变化。
///     · Cost/DepCost 的 rect:它没有代码缓存,手牌模式下用预制体里设计好的锚点/位置/尺寸,
///       想挪部署费用格就改这里,不用动代码。
///     · Cost/ActCost 的 rect:首次 Bind 时按「当前值」惰性缓存成手牌基线(只有 anchorMin == anchorMax 未被拉伸时才缓存)。
///       之后 BattlefieldManager 用 CardViewMode.Field 绑定时会把它拉伸铺满整个费用区(战场只显示行动费用并放大)。
///       烘进预制体的基线不对,每次切回手牌都会还原成错的位置;要重设基线就先把这张卡 Bind 成手牌布局,
///       再用右键菜单「重置为手牌布局(清缓存)」重新记录。
///     · testData + 最后三个右键菜单:只在编辑器里看卡面用,不影响正式流程。
public class CardDisplay : MonoBehaviour
{
    [Header("费用区")]
    [Tooltip("部署费用文本,指 Card/Cost/DepCost。只在手牌与悬停预览显示;留空会在 Bind 时空引用")]
    [SerializeField] private TMP_Text deploymentCostText;   // → Cost/DepCost     费用区左半格
    [Tooltip("行动费用文本,指 Card/Cost/ActCost。手牌/预览是右下小格,战场会被拉伸铺满整格并放大")]
    [SerializeField] private TMP_Text actionCostText;       // → Cost/ActCost     费用区右下格
    [Tooltip("行动费用的底图,指 Card/Cost/CostIcon。只在手牌与预览显示;留空会在 Bind 时空引用")]
    [SerializeField] private GameObject actionCostIcon;     // → Cost/CostIcon    "K",费用区右上格

    [Header("顶部")]
    [Tooltip("卡名文本,指 Card/Top/CardName。留空会在 Bind 时空引用")]
    [SerializeField] private TMP_Text nameText;         // → Top/CardName
    [Tooltip("朝代文本,指 Card/Top/Dynasty。留空会在 Bind 时空引用")]
    [SerializeField] private TMP_Text dynastyText;      // → Top/Dynasty

    [Header("描述区")]
    [Tooltip("关键词文本,指 Card/description/entry。Bind 时按 keywords 拼成「闪击，守护」这种中文串")]
    [SerializeField] private TMP_Text keywordsText;     // → description/entry
    [Tooltip("效果文本,指 Card/description/effects。直接取 CardData.effectText,留空会空引用")]
    [SerializeField] private TMP_Text effectsText;      // → description/effects

    [Header("图像")]
    [Tooltip("插画,指 Card/background/illustration。只换 sprite 不动尺寸;artwork 为空时保留上一张的插画不置空")]
    [SerializeField] private Image illustrationImage;   // → illustration

    [Header("属性区")]
    [Tooltip("属性区容器,指 Card/Property。代码里被强制常显(策略卡也要显示兵种字),关掉它不会生效")]
    [SerializeField] private GameObject propertyRoot;   // → Property        属性区容器(始终保留,策略卡也显示兵种字)
    [Tooltip("兵种字文本,指 Card/Property/Category/categoryword。兵牌显示「步/骑/弓/器」,策略卡显示「策」")]
    [SerializeField] private TMP_Text categoryText;     // → Property/Category/categoryword
    [Tooltip("攻击力整格,指 Card/Property/actbackground。策略卡会被隐藏;留空会空引用")]
    [SerializeField] private GameObject atkRoot;        // → Property/actbackground  攻击力整格
    [Tooltip("生命值整格,指 Card/Property/hpbackground。策略卡会被隐藏;留空会空引用")]
    [SerializeField] private GameObject hpRoot;         // → Property/hpbackground   生命值整格
    [Tooltip("攻击力数字,指 Card/Property/actbackground/atk。战斗中由 RefreshBattleStats 刷新当前值")]
    [SerializeField] private TMP_Text atkText;          // → Property/actbackground/atk
    [Tooltip("生命值数字,指 Card/Property/hpbackground/hp。战斗中由 RefreshBattleStats 刷新当前值")]
    [SerializeField] private TMP_Text hpText;           // → Property/hpbackground/hp

    [Header("稀有度边框")]
    [Tooltip("要按稀有度上色的底图,指 Card/background。必须拖卡牌自己的图;拖成卡牌外部(比如场景 Background)会被拒绝上色并报错")]
    [SerializeField] private Image borderImage;         // → Border

    [Header("战场数值颜色")]
    [Tooltip("战场小卡上「被增益过」的攻/血数字颜色(当前值高于卡面时用)")]
    [SerializeField] private Color buffedStatColor = new Color(0.5f, 1f, 0.55f);
    [Tooltip("战场小卡上「受过伤」的血量颜色(当前 HP 低于上限时用)")]
    [SerializeField] private Color damagedStatColor = new Color(1f, 0.5f, 0.45f);

    [Header("费用字号")]
    [Tooltip("手牌布局下部署费用的字号。调大 = 费用数字更醒目;太小会看不清")]
    [SerializeField] private float handDeploymentCostFont = 10f;   // 手牌:部署费用
    [Tooltip("手牌布局下行动费用的字号。手牌那一格很小,一般别超过部署费用那一档")]
    [SerializeField] private float handActionCostFont = 8f;        // 手牌:行动费用
    [Tooltip("战场布局下行动费用的字号。战场卡整体缩 0.7、行动费用独占整格,所以可以比手牌大;糊了就调大它")]
    [SerializeField] private float fieldActionCostFont = 12f;      // 战场:行动费用(放大)

    public CardData Data { get; private set; }

    // 手牌布局基线:首次Bind时惰性缓存(编辑模式没有Awake,只能在这里记)
    private bool handLayoutCached;
    private Vector2 handActionCostPos;
    private Vector2 handActionCostSize;

    private static readonly Dictionary<Rarity, Color> RarityColors = new()
    {
        { Rarity.Standard, new Color(0.92f, 0.92f, 0.92f) },
        { Rarity.Limited,  new Color(0.35f, 0.55f, 1.00f) },
        { Rarity.Special,  new Color(0.65f, 0.35f, 1.00f) },
        { Rarity.Elite,    new Color(1.00f, 0.80f, 0.25f) },
    };

    private static readonly Dictionary<UnitType, string> TypeNames = new()
    {
        { UnitType.Infantry, "步" },
        { UnitType.Cavalry,  "骑" },
        { UnitType.Archer,   "弓" },
        { UnitType.Support,  "器" },
        { UnitType.Strategy, "策" }
    };

    public void Bind(CardData data, CardViewMode mode = CardViewMode.Hand)
    {
        Data = data;
        if (data == null) return;

        CacheStatColor();       // 攻/血两格的原色(战斗中上过色要还原回它)

        // 策略卡(cardType=Tactic 或 unitType=Strategy)不算兵牌:无攻击/生命,也无行动费用
        bool isUnit = data.cardType == CardType.Unit && data.unitType != UnitType.Strategy;

        // ===== 费用区 =====
        // 手牌/悬停展示:部署费用 + K + 行动费用全部显示(策略卡无行动费用)
        // 战场:隐藏部署费用与 K,行动费用放大并独占整个费用区
        ApplyCostLayout(mode, isUnit);

        deploymentCostText.text = data.deploymentCost.ToString();
        actionCostText.text = data.actionCost.ToString();

        // ===== 公共部分 =====
        nameText.text = data.cardName;
        dynastyText.text = data.dynasty;
        effectsText.text = data.effectText;
        ApplyBorderColor(data.rarity);
        if (data.artwork != null) illustrationImage.sprite = data.artwork;

        // ===== 属性区:策略卡只去掉攻击力/生命值,兵种字(Category)保留 =====
        ApplyProperty(isUnit);

        var sb = new StringBuilder();
        for (int i = 0; i < data.keywords.Count; i++)
        {
            if (i > 0) sb.Append("，");
            sb.Append(KeywordToChinese(data.keywords[i]));
        }
        keywordsText.text = sb.ToString();
    }

    // ===== 稀有度边框 =====
    /// <summary>
    /// 按稀有度给卡牌自己的底图上色。
    /// 这里挡一道:borderImage 要是被拖成了卡牌外部的 Image(典型事故是拖成了
    /// 场景里的 Background),上色就会直接改掉整块游戏背景的颜色。
    /// </summary>
    private void ApplyBorderColor(Rarity rarity)
    {
        if (borderImage == null)
        {
            Debug.LogError("[CardDisplay] borderImage 没有赋值。", this);
            return;
        }

        if (!borderImage.transform.IsChildOf(transform))
        {
            Debug.LogError(
                $"[CardDisplay] borderImage 指向了卡牌外部的「{borderImage.name}」," +
                "已跳过上色以免污染游戏背景。请把它改回卡牌自己的 Card/background。",
                this);
            return;
        }

        borderImage.color = RarityColors[rarity];
    }

    // ===== 费用区布局 =====
    private void ApplyCostLayout(CardViewMode mode, bool isUnit)
    {
        CacheHandLayout();

        bool hand = mode == CardViewMode.Hand;
        bool showDeployment = hand;                 // 部署费用只在手牌显示
        bool showIcon = hand;                       // "K" 只在手牌显示
        bool showAction = hand ? isUnit : true;     // 手牌:策略卡不显示行动费用;战场:只显示行动费用

        deploymentCostText.gameObject.SetActive(showDeployment);
        actionCostIcon.SetActive(showIcon);
        actionCostText.gameObject.SetActive(showAction);


        if (showDeployment)
            deploymentCostText.fontSize = handDeploymentCostFont;

        if (!showAction) return;

        // 只有行动费用会随模式换位置:手牌还原原始小格,战场拉伸独占整个费用区
        var rt = actionCostText.rectTransform;
        if (hand)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = handActionCostPos;
            rt.sizeDelta = handActionCostSize;
        }
        else
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
        }

        actionCostText.fontSize = hand ? handActionCostFont : fieldActionCostFont;
    }


    // 记录脚本在编辑模式下被设计好的 ActCost 位置与尺寸,返回手牌布局时还原
    private void CacheHandLayout()
    {
        if (handLayoutCached) return;
        var rt = actionCostText.rectTransform;
        if (rt.anchorMin != rt.anchorMax) return;   // 已被拉伸(说明缓存时已是战场布局),不缓存
        handActionCostPos = rt.anchoredPosition;
        handActionCostSize = rt.sizeDelta;
        handLayoutCached = true;
    }

    // ===== 属性区 =====
    private void ApplyProperty(bool isUnit)
    {
        propertyRoot.SetActive(true);       // 容器常显:兵种字对策略卡同样有意义
        atkRoot.SetActive(isUnit);          // 攻击力:策略卡隐藏
        hpRoot.SetActive(isUnit);           // 生命值:策略卡隐藏

        categoryText.text = TypeNames.TryGetValue(Data.unitType, out var typeName)
            ? typeName
            : Data.unitType.ToString();

        if (!isUnit) return;
        atkText.text = Data.atk.ToString();
        hpText.text = Data.hp.ToString();
    }

    // 战场用:战斗中数值变化后刷新
    public void RefreshBattleStats(int currentATK, int currentHP)
    {
        if (atkText != null) atkText.text = currentATK.ToString();
        if (hpText != null) hpText.text = currentHP.ToString();
    }

    /// <summary>
    /// 战场用:按当前值刷新攻/血两格,并把「受过伤 / 被 buff 过」标出来。
    /// maxHP 比卡面高就是吃过增益(绿字),当前 HP 低于上限就是带伤(红字);
    /// 都恢复正常时回到卡面原本的颜色。策略卡没有这两格,直接跳过。
    /// </summary>
    public void RefreshStats(int currentATK, int currentHP, int maxHP)
    {
        if (Data == null || IsStrategyCard) return;

        RefreshBattleStats(currentATK, currentHP);

        bool buffed = maxHP > Data.hp;
        bool damaged = currentHP < maxHP;

        if (atkText != null)
        {
            bool atkBuffed = currentATK > Data.atk;
            atkText.color = atkBuffed ? buffedStatColor : (currentATK < Data.atk ? damagedStatColor : baseStatColor);
        }

        if (hpText != null)
            hpText.color = damaged ? damagedStatColor : (buffed ? buffedStatColor : baseStatColor);
    }

    /// <summary>这张卡在场上算不算策略卡(策略卡不显示攻/血)</summary>
    public bool IsStrategyCard => Data != null && (Data.cardType != CardType.Unit || Data.unitType == UnitType.Strategy);

    /// <summary>攻/血两格在预制体里的原色(第一次绑定时记下来,数值恢复正常后要还原)</summary>
    private Color baseStatColor = Color.white;

    private void CacheStatColor()
    {
        if (statColorCached) return;
        if (hpText != null) baseStatColor = hpText.color;
        else if (atkText != null) baseStatColor = atkText.color;
        statColorCached = true;
    }

    private bool statColorCached;

    private static string KeywordToChinese(Keyword k) => k switch
    {
        Keyword.Blitz        => "闪击",
        Keyword.Ambush       => "伏兵",
        Keyword.Guard        => "守护",
        Keyword.BloodBattle  => "血战",
        Keyword.DoubleStrike => "连战",
        Keyword.HeavyArmor   => "重甲",
        Keyword.DrawCards    => "摸牌",
        Keyword.Summon       => "召唤",
        Keyword.Heal         => "回血",
        Keyword.Oath         => "誓师",
        _ => k.ToString(),
    };

    [Header("编辑器测试")]
    [SerializeField] private CardData testData;

    [ContextMenu("Bind 测试数据(手牌)")]
    private void BindTest_Hand() { if (testData != null) Bind(testData); }

    [ContextMenu("Bind 测试数据(战场)")]
    private void BindTest_Field() { if (testData != null) Bind(testData, CardViewMode.Field); }

    [ContextMenu("重置为手牌布局(清缓存)")]
    private void ResetToHandLayout()
    {
        handLayoutCached = false;   // 丢弃缓存,把当前场景里的设计值重新记为基线
        if (Data != null) Bind(Data, CardViewMode.Hand);
    }
}
