using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 一条排属于哪一方(策划案§2.3:己方后军/中军、共享前军、敌方中军/后军)
/// </summary>
public enum BattleSide { Player, Enemy }

/// <summary>排在战场里的位置(哪一侧、哪一排)</summary>
public enum BattleRowType { Back, Mid, Front }

/// <summary>拖动出牌时这条排的高亮状态</summary>
public enum RowHighlight
{
    None,      // 不高亮(恢复排自己的颜色)
    Valid,     // 这张牌可以落在这里
    Invalid,   // 指针停在上面但落不下去(红闪)
}

/// <summary>
/// 战场的一条排 = 一个容量容器 + 一条相邻链(策划案§2.3)。
///
/// 排上的 HorizontalLayoutGroup 负责把链上的成员横向排开(成员之间留固定空隙)、
/// RowsContainer 上的 VerticalLayoutGroup 负责把 5 条排竖着摞起来 ——
/// 所以本脚本**不碰子物体的位置**,只负责:容量账、成员链名单、行高与间距、
/// 落牌合法性、拖动时的高亮/红闪。
///
/// 容量只算"单位容量",建筑锚点(大营/军械库/粮草)的位置已经在 unitCapacity 里扣掉了:
/// 中军 5 容量 - 大营 = 4;后军 3 容量 - 军械库 - 粮草 = 1;前军无建筑 = 4。
///
/// 【挂载 & 调整】
///   挂在:场景和预制体里都没有这个组件 —— 是 BattlefieldManager.ResolveRefs 在运行时按名字认领 5 条排时,
///         发现排上还没有 BattleRow 就当场 AddComponent 补上,并紧接着 Configure(阵营/位置/容量)配好。
///         所以平时不用手挂,只有想让 Inspector 里的手填值生效时才自己在编辑器里加上它。
///   引用:没有必须手连的引用,全是自己的字段。底色直接取同物体上的 Image(首次用到才缓存),
///         那个物体没有 Image 就只剩容量账,高亮和红闪都不生效(不报错)。
///         手动挂上组件时注意:BattlefieldManager 只在自己新建组件那一次调用 Configure,已存在的组件一律不动,
///         所以 side/rowType/unitCapacity 必须自己填对,或者右键用「按物体名字自动配置」菜单按 PlayerMid/EnemyBack 这类名字配。
///   常调:
///     · unitCapacity:这条排能放几个兵牌(建筑不占这个数);调大 = 能堆更多单位、排更满,调小 = 很快满员、手牌跟着变灰。按名字自动配置时会用 BattleRules 的常量覆盖它(中军 4、后军 1、前军 4),手填的值想保住就别再点那个菜单。
///     · side / rowType:这条排属于谁、在哪个位置,决定 DisplayName,也决定兵种牌和策略卡能不能落在这条排上;运行时新建的组件由 BattlefieldManager 写好,手挂时才要自己填 —— 填错会直接改规则:side 填成 Enemy,兵种牌在这条排上永远报「只能部署到己方排」;rowType 填成 Front,永远报「前军只能靠移动进入」。
///     · flashDuration:非法落点红闪一次持续多久;调大 = 红得更久更显眼、连着点会把反馈拖黏,调小 = 一闪而过,填 0 就完全看不见红闪;策划案§10.3 取 0.2s 左右(当前 0.25)。
///     · validTint / invalidTint:合法、非法落点盖在排底色上的颜色;调大 alpha = 提示更抢眼但会把排里的卡面糊住,调小 = 更含蓄。invalidTint 同时是红闪的起始色(FlashInvalid 是从它补间回原色的)。
/// </summary>
[DisallowMultipleComponent]
public class BattleRow : MonoBehaviour
{
    [Header("身份")]
    [Tooltip("这条排属于哪一方,决定谁能在上面落牌。手挂这个组件时才要自己填:运行时由 BattlefieldManager 按物体名字自动写好(Player* → 己方,Enemy* → 敌方)")]
    [SerializeField] private BattleSide side = BattleSide.Player;
    [Tooltip("这条排在战场上的位置(后军/中军/前军),决定容量和能不能落牌:前军不能直接部署。手挂这个组件时才要自己填,运行时由 BattlefieldManager 按名字自动写好")]
    [SerializeField] private BattleRowType rowType = BattleRowType.Mid;

    [Header("容量(单位容量,建筑锚点已扣除)")]
    [Tooltip("中军 = 5 - 大营 = 4;后军 = 3 - 军械库 - 粮草 = 1;前军 = 4")]
    [SerializeField] private int unitCapacity = 4;

    [Header("拖动高亮")]
    [Tooltip("拖动时「这张牌能落在这条排」的底色。alpha 调大 = 更抢眼,但排里卡面会被糊住;调小 = 更含蓄")]
    [SerializeField] private Color validTint = new Color(0.35f, 1f, 0.55f, 0.45f);
    [Tooltip("指针停在这条排上但落不下去时的底色(也是 FlashInvalid 红闪的起始色,红闪结束后会补间回排原本的颜色)")]
    [SerializeField] private Color invalidTint = new Color(1f, 0.28f, 0.25f, 0.45f);
    [Tooltip("非法落点红闪一次多久(策划案§10.3:0.2s 左右)")]
    [SerializeField] private float flashDuration = 0.25f;

    private readonly List<FieldUnit> members = new();
    private Image background;
    private Color originalColor = Color.white;
    private bool cachedColor;

    /// <summary>
    /// ⚠ 共享前军的这个值是**被迫填的假值**(恒为 Player) —— 前军全场只有一条,它不属于任何一方。
    /// 判敌我一概用 <see cref="Owner"/>,不要用它。
    /// 保留它是为了兼容尚未改造完的旧调用点,新代码别再依赖它判阵营。
    /// </summary>
    public BattleSide Side => side;
    public BattleRowType RowType => rowType;
    public int UnitCapacity => unitCapacity;

    /// <summary>
    /// 这条排现在归属谁(§2.3 + 前军的动态归属规则)。
    ///
    /// · 后军 / 中军:归属由排类型决定,恒定不变 —— 己方两条归我方,敌方两条归敌方。
    /// · 共享前军:全场只有一条,**归属由场面决定**:
    ///     · 排里没有活着的兵牌 → 无归属(<c>null</c>),双方都能进;
    ///     · 有兵牌站上去 → 归那一方,对面进不来(§2.3「只容得下一方的单位」);
    ///     · 兵牌全部阵亡 → **立刻**回到无归属,谁都能再来抢。
    ///
    /// 为什么是**派生属性**而不是一个字段:前军最多 4 个成员,每次遍历的成本可以忽略;
    /// 反过来若存成字段,就必须保证每一条进出路径都记得更新它,漏一处就是一个静默的错误状态。
    /// 项目里的 <see cref="HasEnemyRaider"/> 正是那种「字段 + 集中重算」的做法,它的注释里专门写了「别手写」。
    /// 派生值不会和场面不一致,所以这里选它。
    ///
    /// 典型用法(判断 side 能不能进这条排):
    /// <c>if (row.Owner == null || row.Owner == side) { /* 可以进 */ }</c>
    ///
    /// ⚠ 别拿它替换 <c>BattleRules.OnlySideOccupies</c>:那个方法遍历检测的是「混住」,
    ///   对**敌方中军里的袭扰骑兵**会返回 false,而本属性对中军取的是静态归属、恒为该方 ——
    ///   两者语义不同,必须并存。
    /// </summary>
    public BattleSide? Owner => RowType == BattleRowType.Front ? OccupantSide() : side;

    /// <summary>排里的兵牌数(建筑锚点不算单位容量,§2.3:大营占容量但不占那 4 个单位容量)</summary>
    public int UnitCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < members.Count; i++)
                if (members[i] != null && !members[i].IsBuilding) n++;
            return n;
        }
    }

    /// <summary>链条上的成员总数(兵牌 + 建筑)。链上顺序 = members 顺序 = 子物体顺序</summary>
    public int MemberCount => members.Count;

    public bool IsUnitCapacityFull => UnitCount >= unitCapacity;

    /// <summary>整条链(含建筑锚点),链上顺序就是列表顺序</summary>
    public IReadOnlyList<FieldUnit> Units => members;
    public RowHighlight Highlight { get; private set; } = RowHighlight.None;

    /// <summary>
    /// 这条排里驻有敌方袭扰骑兵(策划案§7.4)。
    /// 谁做袭扰谁在骑兵进入/撤出时改它 —— 后军能不能部署就靠它(§8.1.1)。
    /// 骑兵进出这条排之后由 BattleSettlement 调 RefreshRaiderFlag 重新算一遍,别手写这个属性。
    /// </summary>
    public bool HasEnemyRaider { get; set; }

    /// <summary>按链上成员重算「驻有敌方袭扰骑兵」(§7.4):排里有没有一个正在袭扰的成员</summary>
    public bool RefreshRaiderFlag()
    {
        HasEnemyRaider = ContainsRaider();
        return HasEnemyRaider;
    }

    /// <summary>链上有没有袭扰中的成员(骑兵、IsRaiding)</summary>
    public bool ContainsRaider()
    {
        for (int i = 0; i < members.Count; i++)
        {
            var unit = members[i];
            if (unit != null && unit.IsAlive && unit.IsRaiding) return true;
        }
        return false;
    }

    /// <summary>
    /// 共享前军现在被谁占着:链上**第一个活着的兵牌**属于哪一方,就归哪一方;没人就是 null(无归属)。
    ///
    /// 只有共享前军会走到这里(见 <see cref="Owner"/>)—— 其余四条排的归属由排类型定死,不需要看场面。
    ///
    /// 三个边界按策划案定死了,改之前先确认:
    ///   · **建筑不算**:前军本来就没有建筑锚点,而且建筑不会移动、不会造成"混住";
    ///   · **阵亡的不算**:兵牌全部阵亡后立刻回到无归属(清掉就能再抢),不是等到回合结束;
    ///   · **不存在混占**:规则上排里只容得下一方的单位(进之前就被 BattleRules 拦了),
    ///     所以取第一个活着的兵牌的阵营就够了,不用处理"两边都有人"的情况。
    /// </summary>
    private BattleSide? OccupantSide()
    {
        for (int i = 0; i < members.Count; i++)
        {
            var unit = members[i];
            if (unit == null || !unit.IsAlive || unit.IsBuilding) continue;
            return unit.Side;
        }
        return null;
    }

    /// <summary>
    /// 排的中文名,飘字和日志用。
    ///
    /// 共享前军**不属于任何一方**,所以名字里不冠"己方 / 敌方" —— 叫成"己方前军"会让人误以为
    /// 那条排是我方私有的,而它其实是双方抢的那一条(现在归谁看 <see cref="Owner"/>)。
    /// 飘字里出现「共享前军还驻着敌方单位」才说得通。
    /// </summary>
    public string DisplayName =>
        rowType == BattleRowType.Front
            ? "共享前军"
            : (side == BattleSide.Player ? "己方" : "敌方") +
              (rowType == BattleRowType.Back ? "后军" : rowType == BattleRowType.Mid ? "中军" : "前军");

    private void Awake() => CacheBackground();

    /// <summary>运行时新建了这个组件时用(按物体名字认出身份)</summary>
    public void Configure(BattleSide side, BattleRowType rowType, int unitCapacity)
    {
        this.side = side;
        this.rowType = rowType;
        this.unitCapacity = Mathf.Max(1, unitCapacity);
    }

    /// <summary>按物体名字(PlayerMid/EnemyBack……)自动配好身份和容量,懒得手填时右键用</summary>
    [ContextMenu("按物体名字自动配置")]
    private void ConfigureFromName()
    {
        string n = name;
        bool enemy = n.StartsWith("Enemy");
        if (!enemy && !n.StartsWith("Player")) { Debug.LogWarning($"[BattleRow] 名字「{n}」认不出是哪条排。", this); return; }

        var type = n.EndsWith("Back") ? BattleRowType.Back
                 : n.EndsWith("Mid") ? BattleRowType.Mid
                 : BattleRowType.Front;
        int capacity = type == BattleRowType.Back ? BattleRules.BackUnitCapacity
                     : type == BattleRowType.Mid ? BattleRules.MidUnitCapacity
                     : BattleRules.FrontUnitCapacity;

        Configure(enemy ? BattleSide.Enemy : BattleSide.Player, type, capacity);
    }

    private void CacheBackground()
    {
        if (cachedColor) return;
        background = GetComponent<Image>();
        if (background != null) originalColor = background.color;
        cachedColor = true;
    }

    /// <summary>
    /// 重缓存底色。**目前全仓库没有任何调用点** —— 保留它是为了这个真实存在的坑：
    ///
    /// CacheBackground() 是懒加载(第一次 SetHighlight/FlashInvalid 才去读 background.color)。
    /// 只要在"组件挂上之后、第一次高亮之前"有谁改过底色(改 Inspector、换主题、按排类型上色),
    /// 那么第一次读到什么 originalColor 就永远是什么,SetHighlight(None) 会把那个中间色当成
    /// 原色还原 —— 排会一直挂着错颜色,而且**不会有任何报错**,只能靠肉眼发现。
    /// 真遇到这种情况,在"确认当前不高亮"的时刻调一次这个方法即可。
    ///
    /// **只在没有高亮时才能调**:排正亮着的时候去读 background.color,读到的就是高亮色本身,
    /// 那等于亲手把高亮色写成原色,反而制造出"永久常亮"。
    /// </summary>
    internal void RefreshBackgroundCache()
    {
        if (Highlight != RowHighlight.None) return;      // 亮着的时候不碰缓存

        cachedColor = false;
        CacheBackground();
    }

    // ===== 成员名单(§2.3 的相邻链) =====

    /// <summary>把新成员(兵牌或建筑)登记进这条排。index = 插在链上的第几位(0 = 最前)</summary>
    public void AddUnit(FieldUnit unit, int index)
    {
        if (unit == null) return;
        members.Insert(Mathf.Clamp(index, 0, members.Count), unit);
    }

    /// <summary>
    /// 成员离场(被击毁 / 撤走)。只从名单里摘掉 —— 销毁实体由调用方负责,
    /// 记住成员挂在"布局格子"上:Destroy(unit.gameObject) 连格子带卡面一起走,
    /// 排上的布局组自己会把剩下的成员重新排好。
    /// </summary>
    public bool RemoveUnit(FieldUnit unit) => unit != null && members.Remove(unit);

    public int IndexOf(FieldUnit unit) => unit == null ? -1 : members.IndexOf(unit);

    /// <summary>
    /// 把一个成员连它的「布局格子」一起搬到这条排的链上第 index 位(移动单位时用,§2.3 排距 1)。
    ///
    /// 为什么不能只改 members 名单:成员挂在格子上,格子是排的子物体,
    /// 排上的 HorizontalLayoutGroup 是按**子物体顺序**排的 —— 不搬格子,单位在数据上换了排、
    /// 画面上还留在原来那条排里。所以这里连 Transform 一起搬。
    /// </summary>
    public void InsertSlot(FieldUnit unit, int index)
    {
        if (unit == null) return;

        var rt = unit.transform as RectTransform;
        if (rt == null) return;

        rt.SetParent(transform, false);
        rt.SetSiblingIndex(Mathf.Clamp(index, 0, transform.childCount - 1));
        LayoutRebuilder.MarkLayoutForRebuild((RectTransform)transform);
    }

    /// <summary>成员数或链上顺序变了,让布局组重排一次(名单与格子顺序要一致)</summary>
    public void RebuildLayout()
    {
        LayoutRebuilder.MarkLayoutForRebuild((RectTransform)transform);
    }

    // ===== 排布(行高固定 + 链上定距) =====

    /// <summary>
    /// 把这条排的排布钉死:高度固定成 rowHeight,链上成员之间留 memberGap 的空隙
    /// (那段空隙是留给 buff 图标的)。只改布局参数,子物体的位置还是布局组算。
    ///
    /// 高度这条是关键:排上的 HorizontalLayoutGroup 会按成员的偏好高度撑排,
    /// 排里一塞卡牌整排就变高、把 5 条排的位置全挤乱 —— 所以这里直接写死
    /// sizeDelta.y,并让 RowsContainer 别再按内容撑各排的高度(见 ApplyRowLayout)。
    ///
    /// 成员的尺寸由"布局格子"自己的 sizeDelta 决定(childControl* 关掉):
    /// 格子固定 105×140,卡面装在格子里整体缩 0.7 —— 布局只认格子,
    /// 卡面的字号/插画一起等比缩小,不会被 rect 拉变形。
    /// </summary>
    public void LockLayout(float rowHeight, float memberGap)
    {
        var rt = (RectTransform)transform;
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, rowHeight);

        var layout = GetComponent<HorizontalLayoutGroup>();
        if (layout == null) return;

        layout.childControlWidth = false;      // 成员多宽由格子自己说了算
        layout.childControlHeight = false;     // 别按偏好高度改成员高度(格子没有偏好高度,会算成 0)
        layout.childForceExpandWidth = false;  // 不摊开:链上成员按固定间距挨着排
        layout.childForceExpandHeight = false;
        layout.spacing = memberGap;
        layout.childAlignment = TextAnchor.MiddleCenter;
    }

    // ===== 高亮 / 红闪 =====

    /// <summary>拖动出牌时给这条排上色。None = 恢复排自己的颜色</summary>
    public void SetHighlight(RowHighlight state)
    {
        CacheBackground();

        // 状态没变就别重写颜色 —— 拖动时这个方法每帧都会被调,
        // 每次都写一遍 background.color 会让 UI 每帧重建顶点,白费性能。
        if (state == Highlight) return;

        Highlight = state;
        if (background == null) return;

        background.DOKill();       // 别让上一次红闪的补间把新颜色又拉回去
        switch (state)
        {
            case RowHighlight.Valid: background.color = validTint; break;
            case RowHighlight.Invalid: background.color = invalidTint; break;
            default: background.color = originalColor; break;
        }
    }

    /// <summary>非法落点:红闪一下再回到原来的颜色(策划案§10.3)</summary>
    public void FlashInvalid()
    {
        CacheBackground();
        Highlight = RowHighlight.Invalid;
        if (background == null) return;

        background.DOKill();
        background.color = invalidTint;
        background.DOColor(originalColor, flashDuration).SetEase(Ease.OutQuad)
                  .OnComplete(() => Highlight = RowHighlight.None);
    }
}
