using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public enum CardViewMode { Hand, Field }   // 手牌大卡 / 战场小卡

/// <summary>
/// 卡面绑定:把一份 CardData 铺到已经摆好的卡面子物体上。
/// 只认脚本里拖好的这些引用,节点名作为兜底(目前只有稀有度方框会按名字找一次)。
/// </summary>
///
/// 【挂载 & 调整】
///   挂在:**两份预制体的根物体**上,它们共用本脚本,只是引用的节点不同:
///     · Assets/_Project/Prefabs/UI/Card.prefab(根物体「Card」)—— 手牌大卡 + 悬停预览。
///       手牌由 HandUI 在 HandArea1 下 Instantiate,预览卡由 CardHoverPreview 实例化在 hover 下。
///     · Assets/_Project/Prefabs/UI/CardsInBattle.prefab(根物体「CardsInBattle」)—— 战场卡面。
///       由 BattlefieldManager 在排里的格子上 Instantiate(见 BattlefieldManager.SpawnUnit)。
///       这份刻意做成精简卡面:字号更大、没有卡名/关键词/效果/费用区/稀有度方框,
///       所以上面那批引用留空是正常的,本脚本对每个引用都做了留空保护。
///     不要在场景里另外挂一份,也不要把本组件单独挪到子物体上。
///     两份预制体的根物体都自带一个嵌套 Canvas(子 Canvas)+ CanvasScaler(ScaleFactor 1),
///     卡面渲染尺寸是「rect 的 150x200 × 根物体 localScale」,所以战场/预览都靠缩放改大小,别去改 rect。
///   引用:留空即跳过(不再空引用)。对照表:
///     · Card.prefab:14 个引用全连满,一个都别留空,否则对应那块卡面是空的。
///     · CardsInBattle.prefab:只连 actionCostText(Cost)、dynastyText(Dynasty)、
///       categoryText(category)、atkText/hpText、atkRoot/hpRoot、illustrationImage;
///       其余(部署费用 / K / 卡名 / 关键词 / 效果 / 稀有度方框)全部留空。
///     · rarityImage 是稀有度方框,有运行时保护:留空 → 按名字在卡牌层级里兜底找一次「special」,
///       还找不到才 LogError 且不上色;若被拖成了卡牌外部的 Image,ApplyRarity 会拒绝上色并报错,
///       免得把游戏背景整块染色。战场布局(Field)与没有方框的卡面直接跳过这一步。
///     · illustrationImage 另有一个不会报错的坑:data.artwork 为 null 时它会保留上一张插画,
///       复用同一个实例先看 A 后看 B 会残留 A 的图。
///   常调:
///     · **字号一律先改预制体**:每个文本节点(DepCost / ActCost / atk / hp / CardName / Dynasty / 词条 / 效果)
///       自己那段 TMP 上的 Font Size 就是最终显示的字号,代码默认一个字都不覆盖 ——
///       手牌、悬停预览、战场三处看到的字号 = 你在预制体里看到的字号。要改字号就点那个文本节点改。
///     · manageDeploymentCostFont / manageActionCostFont:**默认都不勾**,代码不碰费用字号(见上一条)。
///       勾上才按 handDeploymentCostFont / handActionCostFont 写死,只在「预制体字号改不动、必须靠代码压回小字」
///       这种应急情况才用;勾上之后手牌与预览会一起变,又和预制体不一致了,所以改完记得核对。
///     · fieldActionCostFont:只在 manageCostLayout 勾上(老路径:拿 Card.prefab 当战场卡)时才有意义,
///       现在战场走 CardsInBattle.prefab,它已经关掉 manageCostLayout,所以这一档对战场没有任何影响。
///     · Cost/DepCost 的 rect:它没有代码缓存,手牌模式下用预制体里设计好的锚点/位置/尺寸,
///       想挪部署费用格就改这里,不用动代码。
///     · Cost/ActCost 的 rect:首次 Bind 时按「当前值」惰性缓存成手牌基线(只有 anchorMin == anchorMax 未被拉伸时才缓存)。
///       之后绑定成 CardViewMode.Field 时(旧的手牌预制体当战场卡用的老路径)会把它拉伸铺满整个费用区。
///       这条老路径在 Card.prefab 上仍然保留(manageCostLayout 默认开),但 CardsInBattle 已经关掉它,
///       免得代码把预制体里摆好的费用格冲掉。
///       要重设基线就先把这张卡 Bind 成手牌布局,再用右键菜单「重置为手牌布局(清缓存)」重新记录。
///     · manageCostLayout:**费用区的布局是不是由代码接管**(只管 K 图标显隐 + ActCost 格的锚点/位置/尺寸)。
///       勾上(默认,Card.prefab 用):手牌↔战场来回切时 ActCost 格由代码在小格和整格之间切 —— 手牌需要它。
///       取消(CardsInBattle.prefab 用):布局一个像素都不动,完全按预制体显示,代码只往里填数字。
///     · testData + 最后三个右键菜单:只在编辑器里看卡面用,不影响正式流程。
///       「Bind 测试数据(战场)」在 CardsInBattle.prefab 上跑才看得出真实战场卡面。
/// </summary>
public class CardDisplay : MonoBehaviour
{
    [Header("费用区")]
    [Tooltip("部署费用文本,指 Card/Cost/DepCost。只在手牌与悬停预览显示;CardsInBattle 上留空")]
    [SerializeField] private TMP_Text deploymentCostText;   // → Cost/DepCost     费用区左半格
    [Tooltip("行动费用文本,指 Card/Cost/ActCost(手牌)或 CardsInBattle/Cost(战场——本身就是一颗大号数字)")]
    [SerializeField] private TMP_Text actionCostText;       // → Cost/ActCost     费用区右下格
    [Tooltip("行动费用的底图,指 Card/Cost/CostIcon。只在手牌与预览显示;CardsInBattle 上留空")]
    [SerializeField] private GameObject actionCostIcon;     // → Cost/CostIcon    "K",费用区右上格

    [Header("顶部")]
    [Tooltip("卡名文本,指 Card/Top/CardName。CardsInBattle 上没有卡名,留空")]
    [SerializeField] private TMP_Text nameText;         // → Top/CardName
    [Tooltip("朝代文本,指 Card/Top/Dynasty(战场是 CardsInBattle/top/Dynasty)")]
    [SerializeField] private TMP_Text dynastyText;      // → Top/Dynasty

    [Header("描述区")]
    [Tooltip("关键词文本,指 Card/description/entry。Bind 时按 keywords 拼成「闪击，守护」这种中文串")]
    [SerializeField] private TMP_Text keywordsText;     // → description/entry
    [Tooltip("效果文本,指 Card/description/effects。CardsInBattle 上没有效果文案,留空")]
    [SerializeField] private TMP_Text effectsText;      // → description/effects

    [Header("图像")]
    [Tooltip("插画,指 Card/background/illustration(战场是 CardsInBattle/Image)。只换 sprite 不动尺寸;artwork 为空时保留上一张的插画不置空")]
    [SerializeField] private Image illustrationImage;   // → illustration

    [Header("属性区")]
    [Tooltip("属性区容器,指 Card/Property。代码里被强制常显(策略卡也要显示兵种字),关掉它不会生效")]
    [SerializeField] private GameObject propertyRoot;   // → Property        属性区容器(始终保留,策略卡也显示兵种字)
    [Tooltip("兵种字文本,指 Card/Property/Category/categoryword(战场是 CardsInBattle/category)。兵牌显示「步/骑/弓/器」,策略卡显示「策」")]
    [SerializeField] private TMP_Text categoryText;     // → Property/Category/categoryword
    [Tooltip("攻击力整格,指 Card/Property/actbackground(战场是 CardsInBattle/atk)。策略卡会被隐藏;留空则跳过")]
    [SerializeField] private GameObject atkRoot;        // → Property/actbackground  攻击力整格
    [Tooltip("生命值整格,指 Card/Property/hpbackground(战场是 CardsInBattle/hp)。策略卡会被隐藏;留空则跳过")]
    [SerializeField] private GameObject hpRoot;         // → Property/hpbackground   生命值整格
    [Tooltip("攻击力数字,指 Card/Property/actbackground/atk(战场是 CardsInBattle/atk/atknum)。战斗中由 RefreshBattleStats 刷新当前值")]
    [SerializeField] private TMP_Text atkText;          // → Property/actbackground/atk
    [Tooltip("生命值数字,指 Card/Property/hpbackground/hp(战场是 CardsInBattle/hp/hpnum)。战斗中由 RefreshBattleStats 刷新当前值")]
    [SerializeField] private TMP_Text hpText;           // → Property/hpbackground/hp

    [Header("稀有度小方框")]
    [Tooltip("卡牌底部那颗按稀有度上色的小方框,指 Card/Property/special(白/蓝/紫/金/红)。\n" +
             "只在 Card.prefab 上有:战场用的 CardsInBattle 不带方框,这一格留空。\n" +
             "留空时会按名字在卡牌层级里兜底找一次 special;还是找不到才报错。\n" +
             "必须拖卡牌自己的图;拖成卡牌外部(比如场景 Background)会被拒绝上色并报错")]
    [SerializeField] private Image rarityImage;         // → Property/special  稀有度小方框

    [Header("战场数值颜色")]
    [Tooltip("战场小卡上「被增益过」的攻/血数字颜色(当前值高于卡面时用)")]
    [SerializeField] private Color buffedStatColor = new Color(0.5f, 1f, 0.55f);
    [Tooltip("战场小卡上「受过伤」的血量颜色(当前 HP 低于上限时用)")]
    [SerializeField] private Color damagedStatColor = new Color(1f, 0.5f, 0.45f);

    [Header("费用字号")]
    [Tooltip("勾上 = 手牌的部署费用按下面这一档字号写死。\n" +
             "取消(推荐)= 完全用 Card.prefab 里 DepCost 自己那段 TMP 的字号,代码不碰 —— " +
             "这样手牌和悬停预览的字号和你在预制体里看到的一模一样。\n" +
             "留这一档只是为了「预制体字号改不动了、必须靠代码压回小字」这种应急情况。")]
    [SerializeField] private bool manageDeploymentCostFont;
    [Tooltip("只在上面勾上时生效:手牌布局下部署费用的字号")]
    [SerializeField] private float handDeploymentCostFont = 16f;   // 手牌:部署费用(兜底档,默认跟预制体一致)
    [Tooltip("勾上 = 手牌的行动费用按下面这一档字号写死。\n" +
             "取消(推荐)= 完全用 Card.prefab 里 ActCost 自己那段 TMP 的字号,代码不碰。")]
    [SerializeField] private bool manageActionCostFont;
    [Tooltip("只在上面勾上时生效:手牌布局下行动费用的字号")]
    [SerializeField] private float handActionCostFont = 16f;       // 手牌:行动费用(兜底档,默认跟预制体一致)
    [Tooltip("只在 manageCostLayout 勾上时生效:战场布局下行动费用的字号")]
    [SerializeField] private float fieldActionCostFont = 12f;      // 战场:行动费用(老路径兜底档)

    [Header("布局接管")]
    [Tooltip("勾上 = 费用区的 K 图标显隐、行动费用格的锚点/位置/尺寸由代码按模式写 —— 手牌 Card.prefab 需要这个" +
             "(它把 ActCost 从手牌的小格切到战场的整格)。字号不在这里管,见上面两档开关。\n" +
             "取消 = 连布局也不动,完全按预制体显示。CardsInBattle.prefab 必须取消。")]
    [SerializeField] private bool manageCostLayout = true;

    public CardData Data { get; private set; }

    // 手牌布局基线:首次Bind时惰性缓存(编辑模式没有Awake,只能在这里记)
    private bool handLayoutCached;
    private Vector2 handActionCostPos;
    private Vector2 handActionCostSize;

    // 稀有度小方框的颜色:白 → 蓝 → 紫 → 金 → 红(红是给后续稀有度留的档位)
    private static readonly Dictionary<Rarity, Color> RarityColors = new()
    {
        { Rarity.Ordinary, new Color(0.92f, 0.92f, 0.92f) },   // 普通:白
        { Rarity.Rare,     new Color(0.35f, 0.55f, 1.00f) },   // 稀有:蓝
        { Rarity.Epic,     new Color(0.65f, 0.35f, 1.00f) },   // 史诗:紫
        { Rarity.Legend,   new Color(1.00f, 0.80f, 0.25f) },   // 传说:金
        { Rarity.Hero,     new Color(0.95f, 0.25f, 0.25f) },   // 英雄:红
    };

    private static readonly Dictionary<UnitType, string> TypeNames = new()
    {
        { UnitType.Infantry, "步" },
        { UnitType.Cavalry,  "骑" },
        { UnitType.Archer,   "弓" },
        { UnitType.Support,  "器" },
        { UnitType.Strategy, "策" },
        { UnitType.Counter,  "反" }
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

        if (deploymentCostText != null) deploymentCostText.text = data.deploymentCost.ToString();
        if (actionCostText != null) actionCostText.text = data.actionCost.ToString();

        // ===== 公共部分 =====
        if (nameText != null) nameText.text = data.cardName;
        if (dynastyText != null) dynastyText.text = data.dynasty;
        if (effectsText != null) effectsText.text = data.effectText;
        ApplyIllustration(data);
        ApplyRarity(data.rarity, mode);

        // ===== 属性区:策略卡只去掉攻击力/生命值,兵种字(Category)保留 =====
        ApplyProperty(isUnit);
        ApplyKeywords(data);
    }

    // ===== 插画 =====
    // artwork 为空时保留上一张的插画(同一个实例复用时会残留前一张的图,这是刻意的兜底)
    private void ApplyIllustration(CardData data)
    {
        if (illustrationImage == null || data == null || data.artwork == null) return;
        illustrationImage.sprite = data.artwork;
    }

    // ===== 关键词 =====
    private void ApplyKeywords(CardData data)
    {
        if (keywordsText == null || data == null) return;

        var sb = new StringBuilder();
        for (int i = 0; i < data.keywords.Count; i++)
        {
            if (i > 0) sb.Append("，");
            sb.Append(KeywordToChinese(data.keywords[i]));
        }
        keywordsText.text = sb.ToString();
    }

    // ===== 稀有度小方框 =====
    /// <summary>
    /// 按稀有度给卡牌底部那颗小方框上色(白/蓝/紫/金/红)。
    /// 战场卡面用的是 CardsInBattle,上面不带稀有度方框,所以 Field 布局直接跳过。
    ///
    /// 这里挡一道:rarityImage 要是被拖成了卡牌外部的 Image(典型事故是拖成了
    /// 场景里的 Background),上色就会直接改掉整块游戏背景的颜色。
    /// </summary>
    private void ApplyRarity(Rarity rarity, CardViewMode mode)
    {
        // 稀有度只在手牌与悬停预览里显示;战场卡面(CardsInBattle)本来就没有这颗方框
        if (mode != CardViewMode.Hand) return;

        var square = ResolveRarityImage();
        if (square == null) return;

        if (!square.transform.IsChildOf(transform))
        {
            Debug.LogError(
                $"[CardDisplay] rarityImage 指向了卡牌外部的「{square.name}」," +
                "已跳过上色以免污染游戏背景。请把它改回卡牌自己的 Card/Property/special。",
                this);
            return;
        }

        square.color = RarityColors.TryGetValue(rarity, out var color) ? color : Color.white;
    }

    /// <summary>稀有度方框的引用。没手连就按名字在卡牌层级里兜底找一次(找不到才报错)</summary>
    private Image ResolveRarityImage()
    {
        if (rarityImage != null) return rarityImage;

        var all = GetComponentsInChildren<Image>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].gameObject.name == RaritySquareName)
            {
                rarityImage = all[i];
                return rarityImage;
            }
        }

        if (!warnedMissingRarityImage)
        {
            warnedMissingRarityImage = true;
            Debug.LogError(
                $"[CardDisplay] 找不到稀有度小方框(rarityImage 没赋值,卡牌层级里也没有名为「{RaritySquareName}」的 Image)," +
                "稀有度不会显示。", this);
        }
        return null;
    }

    private const string RaritySquareName = "special";
    private bool warnedMissingRarityImage;

    // ===== 费用区布局 =====
    private void ApplyCostLayout(CardViewMode mode, bool isUnit)
    {
        CacheHandLayout();

        bool hand = mode == CardViewMode.Hand;
        bool showDeployment = hand;                 // 部署费用只在手牌显示
        bool showIcon = hand;                       // "K" 只在手牌显示
        bool showAction = hand ? isUnit : true;     // 手牌:策略卡不显示行动费用;战场:只显示行动费用

        // 战场卡面(CardsInBattle)整块费用区都没有这些引用:手牌布局下直接跳过,
        // 免得换了个卡面就当场空引用
        if (deploymentCostText == null && actionCostText == null && actionCostIcon == null)
        {
            if (hand)
                Debug.LogWarning(
                    "[CardDisplay] 卡面上没有费用区(部署费用 / 行动费用 / K 三个引用全空)," +
                    "手牌布局下不会显示费用。战场卡面(CardsInBattle)是正常情况。", this);
            return;
        }

        if (deploymentCostText != null)
        {
            deploymentCostText.gameObject.SetActive(showDeployment);
            // 字号默认不碰:DepCost 那段 TMP 自己在预制体里配好了字号(手牌 16),代码再写一次就会
            // 出现「预制体里调好了、场景里却是另一个字号」的不一致。只有勾了开关才按代码的档位写。
            if (showDeployment && manageDeploymentCostFont)
                deploymentCostText.fontSize = handDeploymentCostFont;
        }

        if (actionCostIcon != null) actionCostIcon.SetActive(showIcon);

        if (actionCostText == null || !showAction)
        {
            if (actionCostText != null) actionCostText.gameObject.SetActive(false);
            return;
        }

        actionCostText.gameObject.SetActive(true);

        // 只有行动费用会随模式换位置,而且只在「代码接管布局」的卡面(Card.prefab)上做。
        // CardsInBattle 关掉了 manageCostLayout:它的费用格/朝代格是自己在预制体里摆好的,
        // 这里一个像素都不能动,否则代码会把它拉满整条顶栏,跟预制体里看到的完全不一样。
        if (manageCostLayout)
        {
            var rt = actionCostText.rectTransform;
            if (hand)
            {
                // 还原手牌基线;基线本身是「没被拉伸」的状态,所以万一缓存漏了也不会把拉伸格写回去
                rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = handActionCostPos;
                rt.sizeDelta = handActionCostSize;
            }
            else
            {
                // 拉伸铺满整个费用区(行尾坐标全 0 不是随便写的:Stretch 的 anchoredPosition 必须归零)
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = Vector2.zero;
            }
        }

        // 字号同理:默认由 ActCost 自己在预制体里配(手牌 16),不勾开关就不覆盖
        if (manageCostLayout && manageActionCostFont)
            actionCostText.fontSize = hand ? handActionCostFont : fieldActionCostFont;
    }


    // 记录脚本在编辑模式下被设计好的 ActCost 位置与尺寸,返回手牌布局时还原
    private void CacheHandLayout()
    {
        if (handLayoutCached || actionCostText == null || !manageCostLayout) return;
        var rt = actionCostText.rectTransform;
        if (rt.anchorMin != rt.anchorMax) return;   // 已被拉伸(说明缓存时已是战场布局),不缓存
        handActionCostPos = rt.anchoredPosition;
        handActionCostSize = rt.sizeDelta;
        handLayoutCached = true;
    }

    // ===== 属性区 =====
    private void ApplyProperty(bool isUnit)
    {
        // 全部留空保护:CardsInBattle 这类精简卡面上没有部分格子
        if (propertyRoot != null) propertyRoot.SetActive(true);   // 容器常显:兵种字对策略卡同样有意义
        if (atkRoot != null) atkRoot.SetActive(isUnit);           // 攻击力:策略卡隐藏
        if (hpRoot != null) hpRoot.SetActive(isUnit);             // 生命值:策略卡隐藏

        if (categoryText != null)
            categoryText.text = TypeNames.TryGetValue(Data.unitType, out var typeName)
                ? typeName
                : Data.unitType.ToString();

        if (!isUnit) return;
        if (atkText != null) atkText.text = Data.atk.ToString();
        if (hpText != null) hpText.text = Data.hp.ToString();
    }

    // 战场用:战斗中数值变化后刷新
    public void RefreshBattleStats(int currentATK, int currentHP)
    {
        if (atkText != null) atkText.text = currentATK.ToString();
        if (hpText != null) hpText.text = currentHP.ToString();
    }

    /// <summary>
    /// 悬停预览用:把战场单位的**当前**攻/血刷到这张只读预览卡上
    /// (预览卡用的是手牌布局,卡面上只有卡面数值,不改的话战场吃了 buff / 挨了打都看不出来)。
    /// 增益/受伤的颜色按卡面数值比较:ATK 高于卡面 = 绿,低于 = 红;当前 HP 低于上限 = 红,上限高于卡面 = 绿。
    /// maxHP 传 0 表示不比较,直接按卡面原色显示。
    /// </summary>
    public void ShowPreviewStats(int currentATK, int currentHP, int baseAtk, int maxHP)
    {
        if (Data == null || IsStrategyCard) return;

        CacheStatColor();
        RefreshBattleStats(currentATK, currentHP);

        if (atkText != null)
        {
            int reference = baseAtk > 0 ? baseAtk : Data.atk;
            if (currentATK > reference) atkText.color = buffedStatColor;
            else if (currentATK < reference) atkText.color = damagedStatColor;
            else atkText.color = baseStatColor;
        }

        if (hpText != null)
        {
            if (maxHP <= 0) hpText.color = baseStatColor;
            else if (currentHP < maxHP) hpText.color = damagedStatColor;
            else if (maxHP > Data.hp) hpText.color = buffedStatColor;
            else hpText.color = baseStatColor;
        }
    }

    /// <summary>
    /// 战场用:按当前值刷新攻/血两格,并把「受过伤 / 被 buff 过」标出来。
    /// maxHP 比卡面高就是吃过增益(绿字),当前 HP 低于上限就是带伤(红字);
    /// 都恢复正常时回到卡面原本的颜色。策略卡没有这两格,直接跳过。
    /// </summary>
    public void RefreshStats(int currentATK, int currentHP, int maxHP)
    {
        if (Data == null || IsStrategyCard) return;
        if (atkText == null && hpText == null) return;      // 精简卡面:没有攻/血两格

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

    /// <summary>
    /// 词条在卡面上显示成什么。统一走 [Description] 上写的中文(EnumExtensions.GetDescription),
    /// 这样加词条时只改 CardData.cs 的 Keyword 枚举,不用回来补一张对照表。
    /// </summary>
    private static string KeywordToChinese(Keyword k) => k.GetDescription();

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
