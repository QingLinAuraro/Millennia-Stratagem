using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public enum CardViewMode { Hand, Field }   // 手牌大卡 / 战场小卡

public class CardDisplay : MonoBehaviour
{
    [Header("费用区")]
    [SerializeField] private TMP_Text deploymentCostText;   // → Cost/DepCost     费用区左半格
    [SerializeField] private TMP_Text actionCostText;       // → Cost/ActCost     费用区右下格
    [SerializeField] private GameObject actionCostIcon;     // → Cost/CostIcon    "K",费用区右上格

    [Header("顶部")]
    [SerializeField] private TMP_Text nameText;         // → Top/CardName
    [SerializeField] private TMP_Text dynastyText;      // → Top/Dynasty

    [Header("描述区")]
    [SerializeField] private TMP_Text keywordsText;     // → description/entry
    [SerializeField] private TMP_Text effectsText;      // → description/effects

    [Header("图像")]
    [SerializeField] private Image illustrationImage;   // → illustration

    [Header("属性区")]
    [SerializeField] private GameObject propertyRoot;   // → Property        属性区容器(始终保留,策略卡也显示兵种字)
    [SerializeField] private TMP_Text categoryText;     // → Property/Category/categoryword
    [SerializeField] private GameObject atkRoot;        // → Property/actbackground  攻击力整格
    [SerializeField] private GameObject hpRoot;         // → Property/hpbackground   生命值整格
    [SerializeField] private TMP_Text atkText;          // → Property/actbackground/atk
    [SerializeField] private TMP_Text hpText;           // → Property/hpbackground/hp

    [Header("稀有度边框")]
    [SerializeField] private Image borderImage;         // → Border

    [Header("费用字号")]
    [SerializeField] private float handDeploymentCostFont = 10f;   // 手牌:部署费用
    [SerializeField] private float handActionCostFont = 8f;        // 手牌:行动费用
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
        atkText.text = currentATK.ToString();
        hpText.text = currentHP.ToString();
    }

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
