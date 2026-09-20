using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 卡组构筑界面:左边是**卡池**(每种卡显示一张,角标写"已放 N"),右边是**当前卡组**。
///
/// 【操作方式】
///   · 点左边卡池里的一张卡 → 往卡组里加一张(同名上限内可以加到 4 张,见 DeckPreset.MaxCopiesOf)
///   · 右边卡组里每张卡带 - / +,点 - 拿掉一张,点 + 再加一张
///   · 顶部实时显示「张数 / 30」和稀有度配额完成度,不满足规则的地方标红
///
/// 【为什么卡池按"种类"排,而不是一格一张卡】
///   30 张的卡组里普通卡可以同名 4 张,卡池要是把 4 张同名牌平铺出来,玩家得自己数"这张我还要几张",
///   而且卡池总格子数会变成"30 多张"这种很长的滚动列表。按种类排 + 角标显示"已放 N / 上限 M",
///   加几张一眼就看得出来,点一下加一张也最直接。
///
/// 【挂载 & 调整】
///   挂在:主菜单里那个卡组构筑面板的根物体上(物体名随便,但要和 MainMenuController.deckPanel 连上)。
///         面板内部的所有东西(两栏、滚动、栅格、卡池格、卡组行)都是运行时建的 ——
///         你只需要准备一个**全屏的根物体 + 一张底图**,剩下的交给这里。
///   引用:· cardPrefab:拖 Assets/_Project/Prefabs/UI/Card.prefab。留空会退化成纯文字卡片
///           (能用但很丑),正式界面一定要连上。
///         · library:留空自动取 CardLibrary.Current(它扫 Resources/Cards)。
///         · decks:可选参战卡组列表,一般由 MainMenuController 推过来,不用手连。
///   常调:· cardCellSize:卡池里每格多大。卡面设计尺寸 150×200,格子比它大一圈留出角标位置。
///         · columns:卡池每行几张。
///         · poolScale / deckScale:两侧卡面的缩放,独立调。
/// </summary>
[DisallowMultipleComponent]
public class DeckEditorPanel : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("卡面预制体(Assets/_Project/Prefabs/UI/Card.prefab)。留空退化成纯文字卡片")]
    [SerializeField] private GameObject cardPrefab;
    [Tooltip("卡池。留空自动取 CardLibrary.Current(它扫 Resources/Cards)")]
    [SerializeField] private CardLibrary library;

    [Header("卡池栅格")]
    [Tooltip("卡池每格尺寸(px)。卡面设计尺寸 150×200,格子留大一点放角标")]
    [SerializeField] private Vector2 cardCellSize = new Vector2(150f, 200f);
    [Tooltip("卡池每行几张")]
    [SerializeField] private int columns = 6;
    [Tooltip("卡池格间距")]
    [SerializeField] private Vector2 cardCellSpacing = new Vector2(12f, 12f);
    [Tooltip("卡池卡面缩放(1 = 原始设计尺寸)。格子小于卡面时调小这个")]
    [SerializeField] private float poolScale = 0.78f;

    [Header("卡组栏")]
    [Tooltip("卡组栏里每行的高度")]
    [SerializeField] private float deckRowHeight = 40f;
    [Tooltip("卡组栏字号")]
    [SerializeField] private float deckRowFontSize = 22f;

    [Header("配色")]
    [SerializeField] private Color panelColor = new Color(0.10f, 0.09f, 0.08f, 0.98f);
    [SerializeField] private Color poolCellColor = new Color(0.18f, 0.16f, 0.14f, 0.9f);
    [SerializeField] private Color deckRowColor = new Color(0.20f, 0.18f, 0.15f, 0.9f);
    [SerializeField] private Color textColor = new Color(0.95f, 0.93f, 0.88f);
    [SerializeField] private Color dimColor = new Color(0.62f, 0.60f, 0.56f);
    [SerializeField] private Color okColor = new Color(0.55f, 0.85f, 0.55f);
    [SerializeField] private Color badColor = new Color(0.92f, 0.42f, 0.38f);

    // ---- 运行时 ----

    private List<DeckPreset> decks = new List<DeckPreset>();
    private int deckIndex;

    /// <summary>正在编辑的这份构筑(内存副本)。保存时写回 PlayerPrefs,由编辑器菜单落到资产</summary>
    private readonly List<CardData> working = new List<CardData>();

    private RectTransform poolContent;
    private RectTransform deckContent;
    private TMP_Text statusText;
    private TMP_Text deckNameText;

    // 卡池里每种卡那一格:卡 → 角标文字 + 该卡当前放进卡组的张数
    private readonly Dictionary<CardData, TMP_Text> poolBadges = new Dictionary<CardData, TMP_Text>();
    private readonly Dictionary<CardData, GameObject> poolCells = new Dictionary<CardData, GameObject>();

    private bool built;

    public bool IsOpen => gameObject.activeSelf;

    /// <summary>
    /// 场景里那一份构筑面板。**存在这里而不是靠 FindObjectOfType 现找**:
    /// 面板默认是收起的(未激活),而 FindObjectOfType / GameObject.Find 都**找不到未激活的物体** ——
    /// 主菜单在 Start 里现找就会拿到 null,点「卡组构筑」报"找不到 DeckEditorPanel"。
    /// Awake 是**在物体被 SetActive(false) 之前**执行的,所以这里一定注册得上。
    /// </summary>
    public static DeckEditorPanel Instance { get; private set; }

    // ================================================================ 生命周期

    /// <summary>
    /// 面板故意要在 Awake 里收起来(主菜单点「卡组构筑」才展开)。
    /// 这个组件是场景里手挂的、下面一个子物体都没有,所以 childCount 一定是 0;
    /// 加这个判断只是为了将来万一有人手工往面板里摆了点东西时不要被这里关掉。
    /// </summary>
    private void Awake()
    {
        Instance = this;   // 必须在 SetActive(false) 之前 —— 之后注册的话别人还是找不到

        if (transform.childCount == 0) gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>面板展开时:建界面(只建一次)→ 建卡池(只建一次)→ 刷新</summary>
    private void OnEnable()
    {
        if (!built) Build();
        if (poolCells.Count == 0) BuildPool();
        RefreshAll();
    }

    // ================================================================ 打开 / 关闭

    /// <summary>打开构筑界面。decks 是可选卡组(为空就只编辑临时的一套)</summary>
    public void Open(IEnumerable<DeckPreset> availableDecks = null)
    {
        if (availableDecks != null)
        {
            decks = new List<DeckPreset>(availableDecks);
            if (decks.Count > 0) LoadIntoWorking(decks[0]);
        }

        // 先展开再搭建。反过来的话所有布局组件(RectMask2D / ScrollRect / GridLayoutGroup)
        // 都是在未激活状态下建的,内容尺寸可能算不出来,卡池会挤成一团。
        if (!gameObject.activeSelf) gameObject.SetActive(true);   // SetActive 会触发 OnEnable,里面负责建
        else if (!built) Build();

        transform.SetAsLastSibling();
        RefreshAll();
    }

    public void Close()
    {
        gameObject.SetActive(false);
    }

    /// <summary>把某套卡组读进正在编辑的副本</summary>
    private void LoadIntoWorking(DeckPreset preset)
    {
        working.Clear();
        if (preset != null && preset.cards != null) working.AddRange(preset.cards);
        if (deckNameText != null) deckNameText.text = preset != null ? preset.deckName : "临时卡组";
    }

    // ================================================================ 加 / 减

    /// <summary>往卡组里加一张。返回是否加成功(超额/超上限会失败)</summary>
    public bool TryAdd(CardData card)
    {
        if (card == null) return false;

        if (working.Count >= DeckPreset.DeckSize)
        {
            SetStatus($"卡组已满({DeckPreset.DeckSize} 张),先拿掉几张再加", badColor);
            return false;
        }

        int have = CountIn(card);
        int limit = DeckPreset.MaxCopiesOf(card.rarity);
        if (have >= limit)
        {
            SetStatus($"「{card.cardName}」是{DeckPreset.RarityName(card.rarity)}卡,同名最多 {limit} 张(§5.2)", badColor);
            return false;
        }

        working.Add(card);
        RefreshAll();
        return true;
    }

    /// <summary>从卡组里拿掉一张</summary>
    public bool TryRemove(CardData card)
    {
        if (card == null) return false;
        if (!working.Remove(card)) return false;
        RefreshAll();
        return true;
    }

    private int CountIn(CardData card)
    {
        int n = 0;
        for (int i = 0; i < working.Count; i++)
            if (working[i] == card) n++;
        return n;
    }

    // ================================================================ 建界面

    private void Build()
    {
        built = true;
        if (library == null) library = CardLibrary.Current;

        var root = (RectTransform)transform;
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero;
        root.offsetMax = Vector2.zero;

        // 面板底图(吃掉点击,免得点穿到主菜单按钮)
        var bg = gameObject.GetComponent<Image>();
        if (bg == null) bg = gameObject.AddComponent<Image>();
        bg.color = panelColor;

        // ---- 顶栏:标题 + 卡组名 + 状态 ----
        var title = RuntimeText.Create(root, "EditorTitle", "卡组构筑", 40f,
                                       TextAlignmentOptions.Left, textColor);
        Place((RectTransform)title.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
              new Vector2(48f, -28f), new Vector2(420f, 52f), new Vector2(0f, 1f));

        deckNameText = RuntimeText.Create(root, "DeckName", "—", 28f,
                                          TextAlignmentOptions.Left, dimColor);
        Place((RectTransform)deckNameText.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
              new Vector2(48f, -84f), new Vector2(420f, 40f), new Vector2(0f, 1f));

        statusText = RuntimeText.Create(root, "EditorStatus", "", 24f,
                                        TextAlignmentOptions.Left, textColor);
        Place((RectTransform)statusText.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
              new Vector2(48f, -124f), new Vector2(900f, 40f), new Vector2(0f, 1f));

        // ---- 左栏:卡池 ----
        var poolRoot = NewRect(root, "PoolRoot",
                               new Vector2(0f, 0f), new Vector2(0.68f, 1f),
                               new Vector2(48f, 120f), new Vector2(-12f, -180f));
        poolContent = BuildScrollGrid(poolRoot, "PoolScroll", "PoolContent", out _);

        // ---- 右栏:当前卡组 ----
        var deckRoot = NewRect(root, "DeckRoot",
                               new Vector2(0.68f, 0f), new Vector2(1f, 1f),
                               new Vector2(12f, 120f), new Vector2(-48f, -180f));
        deckContent = BuildScrollList(deckRoot, "DeckScroll", "DeckContent");

        // ---- 底栏按钮 ----
        // 从右往左排:pivot 是 (1, 0.5),anchoredPos.x 是**右边缘**的位置
        CreateButton(root, "BtnSaveDeck", "保存卡组", new Vector2(-360f, 48f), SaveDeck);
        CreateButton(root, "BtnSetActive", "设为出战", new Vector2(-540f, 48f), SaveAndSetActive);
        CreateButton(root, "BtnResetDeck", "重置", new Vector2(-180f, 48f), ResetToPreset);
        CreateButton(root, "BtnCloseDeck", "返回", new Vector2(-20f, 48f), Close);
    }

    private RectTransform BuildScrollGrid(RectTransform parent, string scrollName, string contentName,
                                          out ScrollRect scroll)
    {
        var scrollGo = NewRect(parent, scrollName, Vector2.zero, Vector2.one,
                               Vector2.zero, Vector2.zero).gameObject;
        scroll = scrollGo.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 40f;

        var viewport = NewRect((RectTransform)scrollGo.transform, "Viewport",
                               Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        viewport.gameObject.AddComponent<RectMask2D>();
        scroll.viewport = viewport;

        var content = NewRect(viewport, contentName, new Vector2(0f, 1f), new Vector2(1f, 1f),
                              Vector2.zero, Vector2.zero);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;

        var grid = content.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = cardCellSize;
        grid.spacing = cardCellSpacing;
        grid.padding = new RectOffset(8, 8, 8, 8);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = Mathf.Max(1, columns);
        grid.childAlignment = TextAnchor.UpperLeft;

        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.content = content;
        return content;
    }

    private RectTransform BuildScrollList(RectTransform parent, string scrollName, string contentName)
    {
        var scrollGo = NewRect(parent, scrollName, Vector2.zero, Vector2.one,
                               Vector2.zero, Vector2.zero).gameObject;
        var scroll = scrollGo.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 40f;

        var viewport = NewRect((RectTransform)scrollGo.transform, "Viewport",
                               Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        viewport.gameObject.AddComponent<RectMask2D>();
        scroll.viewport = viewport;

        var content = NewRect(viewport, contentName, new Vector2(0f, 1f), new Vector2(1f, 1f),
                              Vector2.zero, Vector2.zero);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;

        var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 6f;
        vlg.padding = new RectOffset(6, 6, 6, 6);
        vlg.childForceExpandHeight = false;
        vlg.childForceExpandWidth = true;

        // childControlWidth/Height 必须为 true —— 否则布局系统**不去读**子物体上的
        // LayoutElement.preferredHeight,行高会退化成子物体自己的 sizeDelta。
        // 而 CreateDeckRow 是用 NewRect(..., offsetMin=zero, offsetMax=zero) 建的行,
        // sizeDelta 正好是 0 —— 结果就是几十行全叠在同一个位置(踩过的坑)。
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;

        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.content = content;
        return content;
    }

    // ================================================================ 刷新

    private void RefreshAll()
    {
        RefreshPoolBadges();
        RefreshDeckList();
        RefreshStatus();
    }

    /// <summary>只刷角标:卡池那一格本身不用重建,改一下"已放 N / 上限 M"就行</summary>
    private void RefreshPoolBadges()
    {
        foreach (var kv in poolBadges)
        {
            var card = kv.Key;
            if (kv.Value == null) continue;

            int have = CountIn(card);
            int limit = DeckPreset.MaxCopiesOf(card.rarity);

            if (have == 0)
            {
                kv.Value.text = $"上限 {limit}";
                kv.Value.color = dimColor;
            }
            else
            {
                kv.Value.text = $"已放 {have} / {limit}";
                kv.Value.color = have >= limit ? badColor : okColor;
            }

            // 加满了就把那一格压暗,一眼看出"这张不能再加了"
            if (poolCells.TryGetValue(card, out var cell) && cell != null)
            {
                var group = cell.GetComponent<CanvasGroup>();
                if (group == null && have >= limit) group = cell.AddComponent<CanvasGroup>();
                if (group != null) group.alpha = have >= limit ? 0.45f : 1f;
            }
        }
    }

    /// <summary>重建右栏(卡组内容每次变动都重建 —— 几十行而已,不值得做增量)</summary>
    private void RefreshDeckList()
    {
        if (deckContent == null) return;

        for (int i = deckContent.childCount - 1; i >= 0; i--)
            Destroy(deckContent.GetChild(i).gameObject);

        if (working.Count == 0)
        {
            var empty = RuntimeText.Create(deckContent, "Empty", "卡组是空的\n点左边的卡加进来", 22f,
                                           TextAlignmentOptions.Center, dimColor);
            var le = empty.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 80f;
            return;
        }

        // 按 cardId 归并:同一张卡显示成一行「卡名 ×N - +」
        var order = new List<CardData>();
        var counts = new Dictionary<CardData, int>();
        for (int i = 0; i < working.Count; i++)
        {
            var c = working[i];
            if (c == null) continue;
            if (!counts.ContainsKey(c)) { counts[c] = 0; order.Add(c); }
            counts[c]++;
        }

        for (int i = 0; i < order.Count; i++)
            CreateDeckRow(order[i], counts[order[i]]);
    }

    private void CreateDeckRow(CardData card, int count)
    {
        var row = NewRect(deckContent, "Row_" + card.cardId, Vector2.zero, Vector2.one,
                          Vector2.zero, Vector2.zero);
        row.gameObject.AddComponent<Image>().color = deckRowColor;
        var le = row.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = deckRowHeight;
        le.minHeight = deckRowHeight;

        var label = RuntimeText.Create(row, "Label",
            $"{card.cardName}  ×{count}   ({card.deploymentCost} 费)", deckRowFontSize,
            TextAlignmentOptions.Left, textColor);
        var labelRect = (RectTransform)label.transform;
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(1f, 1f);
        labelRect.offsetMin = new Vector2(12f, 0f);
        labelRect.offsetMax = new Vector2(-96f, 0f);
        label.raycastTarget = false;

        // 同名加满时把卡名标红(和卡池角标一个口径)
        if (count >= DeckPreset.MaxCopiesOf(card.rarity)) label.color = badColor;

        // 按钮上的字用 ASCII 的 "-" / "+":➖(U+2796) ➕(U+2795) 属于 Emoji 符号区,
        // 中文字体一般没有这两个字形,借来的 TMP 字体渲染出来是个方框。
        CreateSmallButton(row, "Minus", "-", new Vector2(-64f, 0f), () => TryRemove(card));
        CreateSmallButton(row, "Plus", "+", new Vector2(-20f, 0f), () => TryAdd(card));
    }

    private void RefreshStatus()
    {
        if (statusText == null) return;

        var counts = new Dictionary<Rarity, int>();
        int primary = 0, secondary = 0, legendsFromSecondary = 0;
        string primaryDynasty = decks.Count > 0 && decks[Mathf.Clamp(deckIndex, 0, decks.Count - 1)] != null
            ? decks[Mathf.Clamp(deckIndex, 0, decks.Count - 1)].primaryDynasty
            : "汉";

        for (int i = 0; i < working.Count; i++)
        {
            var c = working[i];
            if (c == null) continue;
            counts.TryGetValue(c.rarity, out int n);
            counts[c.rarity] = n + 1;

            if (c.dynasty == primaryDynasty) primary++;
            else
            {
                secondary++;
                if (c.rarity == Rarity.Legend || c.rarity == Rarity.Hero) legendsFromSecondary++;
            }
        }

        var sb = new StringBuilder();
        sb.Append($"共 {working.Count} / {DeckPreset.DeckSize} 张");

        var order = new[] { Rarity.Ordinary, Rarity.Rare, Rarity.Epic, Rarity.Legend };
        var want = new[] { 12, 8, 4, 2 };
        for (int i = 0; i < order.Length; i++)
        {
            counts.TryGetValue(order[i], out int got);
            sb.Append($"   {DeckPreset.RarityName(order[i])} {got}/{want[i]}");
        }

        sb.Append($"   主{primaryDynasty} {primary}/{DeckPreset.PrimaryDynastyMin}");
        sb.Append($" · 次朝 {secondary}/{DeckPreset.SecondaryDynastyMax}");
        if (legendsFromSecondary > 0) sb.Append("  [!] 次朝代混入了传说卡");

        statusText.text = sb.ToString();

        // 差得远就标红,方便一眼判断能不能保存
        bool sizeOk = working.Count == DeckPreset.DeckSize;
        bool dynastyOk = primary >= DeckPreset.PrimaryDynastyMin
                         && secondary <= DeckPreset.SecondaryDynastyMax
                         && legendsFromSecondary == 0;
        statusText.color = sizeOk && dynastyOk ? okColor : textColor;
    }

    private void SetStatus(string message, Color color)
    {
        if (statusText == null) return;
        statusText.text = message;
        statusText.color = color;
    }

    // ================================================================ 卡池格子

    private void BuildPool()
    {
        foreach (var kv in poolCells)
            if (kv.Value != null) Destroy(kv.Value);
        poolCells.Clear();
        poolBadges.Clear();

        if (library == null)
        {
            Debug.LogError("[构筑] 拿不到卡池(CardLibrary.Current 是空的)," +
                           "检查卡牌资产是不是在 Assets/_Project/Resources/Cards 下面。", this);
            return;
        }

        for (int i = 0; i < library.Count; i++)
        {
            var card = library.cards[i];
            if (card == null) continue;
            CreatePoolCell(card);
        }
    }

    private void CreatePoolCell(CardData card)
    {
        var cell = NewRect(poolContent, "Pool_" + card.cardId, Vector2.zero, Vector2.one,
                           Vector2.zero, Vector2.zero);
        cell.gameObject.AddComponent<Image>().color = poolCellColor;

        if (cardPrefab != null)
        {
            var view = Instantiate(cardPrefab, cell);
            var viewRect = (RectTransform)view.transform;
            viewRect.anchorMin = viewRect.anchorMax = new Vector2(0.5f, 0.5f);
            viewRect.pivot = new Vector2(0.5f, 0.5f);
            viewRect.anchoredPosition = Vector2.zero;
            viewRect.localScale = Vector3.one * poolScale;

            var display = view.GetComponent<CardDisplay>();
            if (display != null) display.Bind(card, CardViewMode.Hand);

            // 卡池里的卡不吃悬停预览(预览会弹出整张卡盖住界面),也不吃拖动
            var hover = view.GetComponent<CardHover>();
            if (hover != null) hover.enabled = false;
            var dragPlay = view.GetComponent<CardDragPlay>();
            if (dragPlay != null) dragPlay.enabled = false;
        }
        else
        {
            // 兜底:没有卡面预制体时用一行文字,至少能用
            var fallback = RuntimeText.Create(cell, "Fallback",
                $"{card.cardName}\n{card.deploymentCost} 费  {card.atk}/{card.hp}", 18f,
                TextAlignmentOptions.Center, textColor);
            var fbRect = (RectTransform)fallback.transform;
            fbRect.anchorMin = Vector2.zero;
            fbRect.anchorMax = Vector2.one;
            fbRect.offsetMin = Vector2.zero;
            fbRect.offsetMax = Vector2.zero;
        }

        // 角标:已放 N / 上限 M
        var badge = RuntimeText.Create(cell, "Badge", "", 18f,
                                       TextAlignmentOptions.Center, dimColor);
        var badgeRect = (RectTransform)badge.transform;
        badgeRect.anchorMin = new Vector2(0f, 1f);
        badgeRect.anchorMax = new Vector2(1f, 1f);
        badgeRect.pivot = new Vector2(0.5f, 1f);
        badgeRect.anchoredPosition = new Vector2(0f, -2f);
        badgeRect.sizeDelta = new Vector2(0f, 24f);
        badge.raycastTarget = false;

        // 点整格 = 加一张(角标和卡面都不吃射线,免得挡住点击)
        var button = cell.gameObject.AddComponent<Button>();
        var hit = cell.gameObject.GetComponent<Image>();
        button.targetGraphic = hit;
        var captured = card;
        button.onClick.AddListener(() => TryAdd(captured));

        poolCells[card] = cell.gameObject;
        poolBadges[card] = badge;
    }

    // ================================================================ 保存

    private void SaveDeck()
    {
        if (working.Count != DeckPreset.DeckSize)
        {
            SetStatus($"卡组要正好 {DeckPreset.DeckSize} 张才能保存,现在是 {working.Count} 张", badColor);
            return;
        }

        var ids = new List<string>();
        for (int i = 0; i < working.Count; i++)
            if (working[i] != null) ids.Add(working[i].cardId);

        DeckStorage.SaveCustomDeck(ids);
        SetStatus($"已保存({working.Count} 张)。编辑器里用菜单「千秋策/卡组/把保存的构筑落成资产」写成正式卡组", okColor);
        Debug.Log($"[构筑] 已保存卡组:{string.Join(",", ids)}");
    }

    private void ResetToPreset()
    {
        if (decks.Count == 0) { working.Clear(); RefreshAll(); return; }
        deckIndex = 0;
        LoadIntoWorking(decks[0]);
        RefreshAll();
    }

    /// <summary>
    /// 「设为出战」:存下来,并且把它定成下一局的出战卡组。
    ///
    /// 为什么要单独一个按钮:光点「保存卡组」只写进 PlayerPrefs,主菜单的
    /// ResolvePlayerDeck 会**优先用资产卡组**,所以存了也不生效 —— 玩家会觉得"我改了但白改"。
    /// 这个按钮把"存"和"选"两件事一次做完。
    /// </summary>
    private void SaveAndSetActive()
    {
        if (!SaveAsActiveIsLegal()) return;
        SaveDeck();

        var preset = BuildWorkingPreset();
        if (preset == null) return;

        var menu = FindObjectOfType<MainMenuController>();
        if (menu == null)
        {
            SetStatus("已保存,但场景里找不到 MainMenuController,没法设为出战", badColor);
            return;
        }

        // 先塞进主菜单的卡组列表,否则 SetActiveDeck 里 IndexOf 找不到、直接 return
        menu.RegisterRuntimeDeck(preset);
        menu.SetActiveDeck(preset);

        SetStatus($"已保存并设为出战:{preset.deckName}", okColor);
    }

    /// <summary>
    /// 「设为出战」的额外校验。比「保存卡组」严 —— 保存只是留个草稿,
    /// 设为出战会直接被下一局拿去打,所以得走策划案§5 的全套规则。
    /// </summary>
    private bool SaveAsActiveIsLegal()
    {
        var preset = BuildWorkingPreset();
        if (preset == null) return false;

        var problems = new List<string>();
        if (DeckPreset.ValidateCards(preset.cards, preset.quota, preset.primaryDynasty, problems))
            return true;

        SetStatus("这套还不能出战:" + string.Join(";", problems), badColor);
        return false;
    }

    /// <summary>把当前工作列表包成一个临时 DeckPreset(照着 decks 里第一套的风格取名字和主朝代)</summary>
    private DeckPreset BuildWorkingPreset()
    {
        if (working.Count == 0)
        {
            SetStatus("卡组是空的,先往右边加卡", badColor);
            return null;
        }

        var basis = decks.Count > 0 ? decks[0] : null;
        string primary = basis != null ? basis.primaryDynasty : "汉";
        string deckName = basis != null ? basis.deckName + "(自定)" : "自定卡组";

        var preset = ScriptableObject.CreateInstance<DeckPreset>();
        preset.deckName = deckName;
        preset.primaryDynasty = primary;
        preset.description = "在卡组构筑界面里改的";
        preset.cards = new List<CardData>(working);
        if (basis != null) preset.quota = basis.quota;
        return preset;
    }

    // ================================================================ 小工具

    private static RectTransform NewRect(RectTransform parent, string name,
                                         Vector2 anchorMin, Vector2 anchorMax,
                                         Vector2 offsetMin, Vector2 offsetMax)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        return rt;
    }

    private static void Place(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax,
                              Vector2 anchoredPos, Vector2 size, Vector2 pivot)
    {
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
    }

    private void CreateButton(RectTransform parent, string name, string label,
                              Vector2 anchoredPos, UnityEngine.Events.UnityAction onClick)
    {
        var rt = NewRect(parent, name, new Vector2(1f, 0f), new Vector2(1f, 0f),
                         anchoredPos, new Vector2(150f, 56f));
        rt.pivot = new Vector2(1f, 0.5f);

        var image = rt.gameObject.AddComponent<Image>();
        image.color = new Color(0.32f, 0.24f, 0.16f, 0.98f);

        var button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(onClick);

        var text = RuntimeText.Create(rt, "Label", label, 24f,
                                      TextAlignmentOptions.Center, new Color(0.98f, 0.94f, 0.8f));
        var textRect = (RectTransform)text.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        text.raycastTarget = false;
    }

    private void CreateSmallButton(RectTransform parent, string name, string label,
                                   Vector2 anchoredPos, UnityEngine.Events.UnityAction onClick)
    {
        var rt = NewRect(parent, name, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                         anchoredPos, new Vector2(36f, 32f));
        rt.pivot = new Vector2(1f, 0.5f);

        var image = rt.gameObject.AddComponent<Image>();
        image.color = new Color(0.36f, 0.28f, 0.20f, 1f);

        var button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(onClick);

        var text = RuntimeText.Create(rt, "Label", label, 18f,
                                      TextAlignmentOptions.Center, textColor);
        var textRect = (RectTransform)text.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        text.raycastTarget = false;
    }
}
