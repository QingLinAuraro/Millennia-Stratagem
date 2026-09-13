using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 敌方手牌表现层:只显示牌背,永远不显示卡面(情报隔离 —— 对手手里是什么牌,我方看不到)。
///
/// 和己方手牌(HandUI + FanLayout)的差别只有三点:
///   1) 生成的是牌背:优先用 Inspector 上指定的 cardBackPrefab,没指定就拿
///      Art/Cards/Cardback.png 现拼一张 150x200 的牌背;
///   2) 手牌区锚在屏幕顶边(锚点 0.5,1),扇形往下挂 —— 和己方"底边往上凸"正好上下相反;
///   3) 牌背不接鼠标(raycastTarget / blocksRaycasts 全关),免得挡住
///      "指定敌方手牌"类策略卡的落点,也别让指针在屏幕顶上被吃掉。
/// 其余逻辑和己方手牌一致:牌背张数跟着 EnemyDeckController 的手牌账走
/// (摸到一张就补一张牌背,打出去一张就收一张),上限同样是 7 张,靠扇形自动收窄。
///
/// 场景里没挂这个组件时,第一次访问 Instance 会自动补到 HandArea2 上(和 BattlefieldManager 那套自举一致)。
/// 运行时补出来的 FanLayout 会用下面这套"顶边往下挂"的参数;想自己调就把组件挂到场景里再改 Inspector。
/// </summary>
///
/// 【挂载 & 调整】
///   挂在:Battle.unity 里挂在 BattleCanvas/HandArea2 上(和 HandArea2 上那份 FanLayout 同一个物体;场景里这一份的
///         handRoot / fanLayout / deck 都是手连的)。
///         漏挂也能自救:Instance 先 FindObjectOfType<EnemyHandUI>(),再找名为 HandArea2 的物体 AddComponent,
///         连 HandArea2 都没有才在第一个 Canvas 下新建空物体挂上 —— 所以忘挂不报错,但自举出来的那一份
///         Inspector 引用全是空的,只剩「按名字找 HandArea2 / EnemyDeckController」的兜底。
///   引用:
///         · handRoot:留空 → Awake 按名字找 HandArea2,找不到只打 LogWarning;Sync 里 handRoot == null 直接 return,
///           牌背一张都不摆(数据层照常摸牌,只是屏幕上什么都没有)。
///         · fanLayout:留空 → Awake 先试 handRoot.GetComponent<FanLayout>(),还没有就 AddComponent 一个,
///           并【按下面那组敌方参数配好】(只有 created = true 才配)。场景里已经连好了 HandArea2 那份,created = false,
///           所以 forceFanPreset 不勾时下面这组扇形数值根本不会写进 FanLayout —— 想改敌方扇形请直接改 FanLayout 组件,
///           或用本组件的右键菜单「按敌方手牌参数刷新扇形」,再或者勾 forceFanPreset。
///         · deck:留空 → Awake / OnEnable 里都取 EnemyDeckController.Instance(它自带自举)。
///           拿不到 → Sync 里 want = 0,牌背会被全部销毁;而且监听不到 HandChanged,牌背数量再也不跟敌方手牌账动。
///         · cardBackPrefab:留空 → 运行时现拼牌背(150x200 的 Image)。拖了就用它 —— 此时 cardBackSize /
///           cardBackTint / keepCardBackAspect 三个参数全部不生效,大小以预制体自己的 rect 为准。
///         · cardBackSprite:留空 → 只在编辑器里按 Assets/_Project/Art/Cards/Cardback.png 认领(#if UNITY_EDITOR);
///           打包后认领不到就是拿纯色块顶着 + LogWarning,所以出包前要么把这张图拖上,要么拖 cardBackPrefab。
///         另外:牌背生成之后一律把身上所有 Graphic 的 raycastTarget 关掉,再补一个 blocksRaycasts / interactable
///         都关的 CanvasGroup —— 免得挡住「指定敌方手牌」的落点,也别让指针在屏幕顶上被吃掉。
///   常调:(下面这些是预设值,只写进 FanLayout —— 见上面 fanLayout 那条;平时真正生效的是 HandArea2 那份 FanLayout,
///         它现在的数值和这里一致,baseOffset 两边都是 240)
///     · baseOffset(→ FanLayout.baseY):整手牌从手牌区底边往上抬多少像素。调大 = 牌背整体更靠上、屏幕里露出更少;
///       调小 = 露出更多(太小会压到战场 / CP 面板上)。手牌区高 140、牌背高 200,现在 240 的实际效果是
///       两端露出 100px、中间露出 140px;要和己方(80px / 120px)完全对称得填 260。
///     · arcDepth:填负值(现在 -40)= 中间那张比两端再往下沉 40px,也就是比两端多露 40px;填 0 = 排成一条直线;
///       填正值会变成往上凸(己方那种),牌背顶边会顶出屏幕外。
///     · maxTilt:两端倾角,填负值两端才往外撇(现在 -15);调 0 = 全部不旋转,负得越多两端歪得越厉害。
///     · stepPerCard / maxSpread:相邻间距与整手横向总宽上限。手牌上限 7 张,现在 7 张时 step 取 72
///       (640/6≈106、可用宽 650/6≈108 都没夹住它),整排约 582px,800 宽的手牌区放得下;想更紧凑就调小 stepPerCard。
///     · cardScale:牌背整体缩放(→ FanLayout.cardScale,只改 localScale)。调小 = 牌背更小,配合 baseOffset 调小
///       可以把整张牌背收进顶部那条 140px 的带子里;它同时会让 FanLayout 按缩放后的视觉宽度重算重叠。
///     · cardBackSize / cardBackTint / keepCardBackAspect:只在现拼牌背(cardBackPrefab 留空)时才生效。
///       cardBackTint 现在是不透明白,想给敌方牌背整体压暗或染蓝就调它;keepCardBackAspect 勾上才按原图
///       739x1033(≈0.72)的比例,不勾会被拉成 cardBackSize 的 150x200(0.75)。
///     · lockAnchorToTop:把手牌区的锚点钉成 (0.5,1)(只改 anchor,位置和尺寸不动)。场景里 HandArea2 本来就是
///       (0.5,1)+ 位置 -70,所以关掉不会立刻变样 —— 关掉之后别人在场景里把锚点改成别的,本脚本也不会纠正回来。
///     · forceFanPreset:勾上 = 每次 Awake 都拿上面这组数值去刷 FanLayout,会盖掉在 FanLayout 上手调的值。
///       只有想把敌方扇形参数「集中在本组件上管」时才勾。
public class EnemyHandUI : MonoBehaviour
{
    /// <summary>敌方手牌区(屏幕顶边居中,800x140)</summary>
    public const string HandAreaObjectName = "HandArea2";

    /// <summary>牌背图。运行时拼牌背时按这个路径认领(只在编辑器里有效,打包要用 cardBackPrefab 或场景引用)</summary>
    public const string CardBackSpritePath = "Assets/_Project/Art/Cards/Cardback.png";

    private static EnemyHandUI instance;

    /// <summary>场景里没挂就自己补一个(优先补在敌方手牌区上)</summary>
    public static EnemyHandUI Instance
    {
        get
        {
            if (instance != null) return instance;

            instance = FindObjectOfType<EnemyHandUI>();
            if (instance != null) return instance;

            var area = GameObject.Find(HandAreaObjectName);
            if (area != null) return instance = area.AddComponent<EnemyHandUI>();

            var go = new GameObject("EnemyHandUI");
            var canvas = CanvasUtil.FindRootCanvas();
            if (canvas != null) go.transform.SetParent(canvas.transform, false);
            return instance = go.AddComponent<EnemyHandUI>();
        }
    }

    [Header("引用(留空则运行时按名字找 HandArea2)")]
    [Tooltip("敌方手牌区。牌背都挂在这下面")]
    [SerializeField] private Transform handRoot;
    [Tooltip("留空则运行时补一个(并按下面的敌方参数配好)")]
    [SerializeField] private FanLayout fanLayout;
    [Tooltip("留空则运行时找 EnemyDeckController")]
    [SerializeField] private EnemyDeckController deck;

    [Header("牌背")]
    [Tooltip("留空 = 运行时用 cardBackSprite 现拼一张牌背")]
    [SerializeField] private GameObject cardBackPrefab;
    [Tooltip("牌背图。留空则按 CardBackSpritePath 自动认领")]
    [SerializeField] private Sprite cardBackSprite;
    [Tooltip("现拼牌背的尺寸,和己方卡牌一样是 150x200")]
    [SerializeField] private Vector2 cardBackSize = new Vector2(150f, 200f);
    [Tooltip("牌背染色。只在现拼牌背(cardBackPrefab 留空)时生效;不透明白 = 原图颜色,想给敌方牌背压暗或染蓝就调它")]
    [SerializeField] private Color cardBackTint = Color.white;
    [Tooltip("按原图比例显示。牌背图 739x1033(≈0.72),不勾会被拉成 0.75")]
    [SerializeField] private bool keepCardBackAspect = true;

    [Header("手牌区")]
    [Tooltip("把手牌区的锚点钉成 (0.5,1) —— 屏幕顶边居中。只改锚点,位置和尺寸不动")]
    [SerializeField] private bool lockAnchorToTop = true;

    [Header("扇形(敌方手牌从顶边往下挂)")]
    [Tooltip("相邻牌背最多分开多少像素。调大 = 排得更宽、露出更多;7 张时整排约 582px,800 宽的手牌区放得下。\n" +
             "注意:这组扇形数值只在 FanLayout 是运行时补出来、或勾了 forceFanPreset 时才写进 FanLayout")]
    [SerializeField] private float stepPerCard = 72f;
    [Tooltip("整手牌横向总宽上限(px)。7 张时是 640/6≈106,大于 stepPerCard 的 72,所以现在夹不到它;\n" +
             "想压窄整排请先把它调到 432(7 张 × 72)以下,或者直接调 stepPerCard")]
    [SerializeField] private float maxSpread = 640f;
    [Tooltip("中间那张比两侧再往下沉多少像素(填负值)。往下挂的扇形里,沉得越低 = 露出得越多,\n" +
             "所以 -40 就是中间那张比两端多露 40px —— 和己方手牌「中间抬得更高」正好是镜像")]
    [SerializeField] private float arcDepth = -40f;
    [Tooltip("最外侧牌背的倾角。往下挂的扇形要填负值,两端下摆才是往外撇的")]
    [SerializeField] private float maxTilt = -15f;
    [Tooltip("整手牌从手牌区底边往上抬多少像素(场景现值 240:两端的牌露得少、中间那张露得多)。\n" +
             "调大 = 整手牌更靠上、露出的更少;调小 = 更往下压、露出更多。\n" +
             "注意:场景里 FanLayout 已连、forceFanPreset 关着,这个值当前不生效 —— 真正生效的是 FanLayout.baseY")]
    [SerializeField] private float baseOffset = 240f;
    [Tooltip("牌背轴心。往下挂要用顶边中心,旋转和缩放才绕顶边")]
    [SerializeField] private Vector2 cardPivot = new Vector2(0.5f, 1f);
    [Tooltip("牌背整体缩放(写进 FanLayout.cardScale,只改 localScale)。调小 = 牌背更小,\n" +
             "配合 baseOffset 调小能把整张牌背收进顶部那条 140px 的带子里")]
    [SerializeField] private float cardScale = 1f;
    [Tooltip("勾上 = 不管手牌区上原有的参数,一律按这里的数值刷一遍")]
    [SerializeField] private bool forceFanPreset = false;

    /// <summary>当前摆着的牌背(顺序 = 敌方摸牌顺序)</summary>
    private readonly List<RectTransform> backs = new();

    public int CardBackCount => backs.Count;

    /// <summary>敌方手牌区的 RectTransform(策略卡"指定敌方手牌"的高亮框还是用这个区)</summary>
    public RectTransform HandArea => handRoot as RectTransform;

    // ================================================================ 生命周期

    private void Awake()
    {
        instance = this;

        ResolveRefs();
        ApplyHandAreaAnchor();
        EnsureFan();
    }

    private void OnEnable()
    {
        if (deck == null) deck = EnemyDeckController.Instance;

        if (deck != null)
        {
            deck.HandChanged -= Sync;       // 先退订,免得重复挂上
            deck.HandChanged += Sync;
        }

        Sync();
    }

    private void OnDisable()
    {
        if (deck != null) deck.HandChanged -= Sync;
    }

    private void Start()
    {
        // 敌方开局那 5 张可能在 OnEnable 之前就进账了(自举顺序不定),这里再对一次
        Sync();
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    // ================================================================ 牌背数量对齐手牌账

    /// <summary>把手牌区的牌背数量对齐敌方手牌账:多了销毁,少了补一张</summary>
    public void Sync()
    {
        if (handRoot == null) return;

        int want = deck != null ? deck.HandCount : 0;

        while (backs.Count > want)
        {
            int last = backs.Count - 1;
            var rt = backs[last];
            backs.RemoveAt(last);

            if (fanLayout != null) fanLayout.Remove(rt);
            if (rt != null) Destroy(rt.gameObject);
        }

        while (backs.Count < want)
        {
            var rt = SpawnCardBack(backs.Count);
            if (rt == null) break;

            backs.Add(rt);
            if (fanLayout != null) fanLayout.Add(rt);
        }
    }

    /// <summary>清空所有牌背(重开一局用;牌背只是表现,不动敌方手牌账)</summary>
    public void ClearBacks()
    {
        for (int i = 0; i < backs.Count; i++)
        {
            if (backs[i] == null) continue;
            if (fanLayout != null) fanLayout.Remove(backs[i]);
            Destroy(backs[i].gameObject);
        }

        backs.Clear();
    }

    // ================================================================ 生成牌背

    private RectTransform SpawnCardBack(int index)
    {
        GameObject go;

        if (cardBackPrefab != null)
        {
            go = Instantiate(cardBackPrefab, handRoot);
        }
        else
        {
            var sprite = ResolveCardBackSprite();
            if (sprite == null)
            {
                Debug.LogWarning($"[EnemyHandUI] 找不到牌背图({CardBackSpritePath}),先用纯色块顶着。", this);
            }

            go = new GameObject("CardBack", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(handRoot, false);

            var image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.color = cardBackTint;
            image.preserveAspect = sprite != null && keepCardBackAspect;
            image.raycastTarget = false;

            ((RectTransform)go.transform).sizeDelta = cardBackSize;
        }

        go.name = $"CardBack_{index + 1}";

        // 牌背一律不接鼠标:挡住"指定敌方手牌"的落点就麻烦了
        foreach (var graphic in go.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;

        var group = go.GetComponent<CanvasGroup>();
        if (group == null) group = go.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;

        return (RectTransform)go.transform;
    }

    private Sprite ResolveCardBackSprite()
    {
        if (cardBackSprite != null) return cardBackSprite;

#if UNITY_EDITOR
        cardBackSprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(CardBackSpritePath);
#endif

        return cardBackSprite;
    }

    // ================================================================ 手牌区 / 扇形

    /// <summary>手牌区锚到屏幕顶边居中(0.5,1)。只钉锚点,位置和尺寸保持场景里的值</summary>
    private void ApplyHandAreaAnchor()
    {
        if (!lockAnchorToTop) return;
        if (handRoot is RectTransform area) area.anchorMin = area.anchorMax = new Vector2(0.5f, 1f);
    }

    /// <summary>
    /// 手牌区上要有一个 FanLayout。场景里没有就补一个,并按"顶边往下挂"配好参数;
    /// 已有的话默认尊重 Inspector 里的值(forceFanPreset 勾上才覆盖)。
    /// </summary>
    private void EnsureFan()
    {
        if (handRoot == null) return;

        if (fanLayout == null) fanLayout = handRoot.GetComponent<FanLayout>();

        bool created = false;
        if (fanLayout == null)
        {
            fanLayout = handRoot.gameObject.AddComponent<FanLayout>();
            created = true;
        }

        if (created || forceFanPreset) ApplyFanPreset();
    }

    /// <summary>把下面这套"顶边往下挂"的扇形参数刷到 FanLayout 上(Inspector 右键也能调)</summary>
    [ContextMenu("按敌方手牌参数刷新扇形")]
    public void ApplyFanPreset()
    {
        if (fanLayout == null) return;
        fanLayout.ApplyPreset(stepPerCard, maxSpread, arcDepth, maxTilt, baseOffset, cardPivot, cardScale);
    }

    private void ResolveRefs()
    {
        if (handRoot == null)
        {
            var go = GameObject.Find(HandAreaObjectName);
            if (go != null) handRoot = go.transform;
            else Debug.LogWarning($"[EnemyHandUI] 场景里找不到敌方手牌区「{HandAreaObjectName}」,手牌没地方摆。", this);
        }

        if (deck == null) deck = EnemyDeckController.Instance;
    }
}
