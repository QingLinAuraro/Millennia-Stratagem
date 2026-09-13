using UnityEngine;
using TMPro;

/// <summary>
/// 牌堆表现层:剩余张数文字 + 一叠牌背。
///
/// 牌背显示规则:
///   剩余 >= backs.Length → 全部显示(满堆的样子)
///   剩余 &lt;  backs.Length → 剩几张就亮几张,逐渐变薄
///   剩余 == 0            → 全部隐藏,牌堆视觉上清空
/// 所以 backs 的数量就是"满堆时最多显示几个牌背"。
/// </summary>
///
/// 【挂载 & 调整】
///   挂在:Battle.unity 里挂了两份,分别在 BattleCanvas/cards1(我方)和 BattleCanvas/cards2(敌方)上。
///         它只负责显示、不订阅任何事件:张数由 DeckController.RefreshDeckUI() / EnemyDeckController.RefreshDeckUI()
///         主动调 Refresh(count) 推过来 —— 所以谁驱动牌堆账,谁就必须记得调它,这里的数字不会自己动。
///   引用:
///         · countText:必须手连(cards1 / cards2 连的都是自己下面那个 CountNum)。本组件【没有任何按名字查找】——
///           只有 EnemyDeckController 那边才会按名字找 CountNum 再 Bind。留空既不报错也不警告,
///           只是数字永远停在 Inspector 里那个值,看着像牌堆没少。
///         · backs:牌背物体数组,可以留空或空数组 —— 那是合法用法:「只显示剩余张数、不做越来越薄」(cards2 现在就是空的)。
///           场景里 cards1 拖了自己下面那 5 个牌背物体。空数组时 Start 的 ValidateBacks 直接 return,不报错。
///           某个元素是 None → Start 报 LogError(文案说「抽牌时会在这里报空引用」,实际 Refresh 里是
///           if (backs[i] == null) continue 跳过,不会崩 —— 但那一层牌背永远不出现);
///           拖成牌堆容器外面的物体(比如对手牌堆的牌背)→ Start 报 LogError 并提示牌堆变少会把那个物体一起隐藏
///           (判定是 backs[i].transform.IsChildOf(transform),只在 Start 跑一次,之后再改父物体不会被复查)。
///         · 会被 EnemyDeckController.Bind 覆盖:只有当它的 deckUI 字段为空时,才走「Find(cards2) →
///           GetComponent/AddComponent<DeckUI> → Bind(找到的 CountNum 文字)」这条兜底。Bind 是无条件覆盖 ——
///           countText 换成它找到的那个,而 backs 因为调用时没传第二个参数会被清成 null / 空数组。
///           所以给 cards2 手连了牌背也没用,得先把 EnemyDeckController.deckUI 连上(场景里已经连了)。
///   常调:
///     · backs 的元素个数:满堆时最多亮几个牌背(cards1 现在是 5)。调多 = 牌堆看着更厚,但要抽掉更多张才会明显变薄;
///       调少 = 一开始就薄,「抽空」的视觉更早出现。
///     · backs 里放哪些物体、怎么叠:代码只按数组下标决定「前 N 个显示」,不看位置、层级和大小 ——
///       那几个牌背物体自己的位置和 sibling 顺序才决定厚度错位的样子。想换牌背样式或错位量就改这些子物体,不用动代码。
///     · hideCountWhenEmpty:勾上 = 牌堆 0 张时连数字一起 SetActive(false)。默认不勾(0 也照常显示一个 0,
///       方便调试确认真的空了);它只影响 countText 那个物体,牌背在 count = 0 时本来就全部隐藏。
///     · 文字样式(字体 / 字号 / 颜色 / 对齐):直接在 CountNum 上改,这里只写 text,不碰样式。
///     · 显示格式:这里就是 count.ToString(),没有补零、后缀或「剩余 N」文案;要改显示格式得改代码或另加一层文字。
///     · 两份实例的字段互相独立(cards1 / cards2 各调各的),改一份不会串到另一份。
public class DeckUI : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("剩余张数的文字(cards1 / cards2 下面那个 CountNum)。不自动查找,留空不报错、也不警告,\n" +
             "但数字永远不刷新 —— 看着像牌堆没少")]
    [SerializeField] private TMP_Text countText;
    [Tooltip("牌背物体。拖自己牌堆下面的那几个,顺序随意,数量 = 满堆时显示的张数")]
    [SerializeField] private GameObject[] backs;

    [Header("选项")]
    [Tooltip("牌堆为 0 时连数字一起隐藏")]
    [SerializeField] private bool hideCountWhenEmpty = false;

    private void Start()
    {
        // 启动自检:牌背拖成别的物体(比如对手牌堆的牌背)时,牌堆变少会误隐藏那个物体
        ValidateBacks();
    }

    /// <summary>
    /// 运行时接线:敌方牌堆(cards2)场景里没挂这个组件,由 EnemyDeckController 补上再 Bind。
    /// backs 传 null / 空 = 这堆牌只显示剩余张数,不做"越来越薄"的视觉。
    /// </summary>
    public void Bind(TMP_Text count, GameObject[] pileBacks = null)
    {
        countText = count;
        backs = pileBacks;
    }

    public void Refresh(int count)
    {
        count = Mathf.Max(0, count);

        if (countText != null)
        {
            countText.text = count.ToString();
            countText.gameObject.SetActive(!(hideCountWhenEmpty && count == 0));
        }

        if (backs == null || backs.Length == 0) return;

        int visible = Mathf.Clamp(count, 0, backs.Length);
        for (int i = 0; i < backs.Length; i++)
        {
            if (backs[i] == null) continue;
            bool on = i < visible;
            if (backs[i].activeSelf != on) backs[i].SetActive(on);
        }
    }

    private void ValidateBacks()
    {
        if (backs == null || backs.Length == 0)
        {
            // 只显示张数也是合法用法(敌方牌堆现在就只更新数字),不当成问题
            return;
        }

        for (int i = 0; i < backs.Length; i++)
        {
            if (backs[i] == null)
            {
                Debug.LogError($"[DeckUI] backs[{i}] 是 None,这一层牌背永远不会显示(不会报空引用:Refresh 里遇到 null 直接跳过)。", this);
                continue;
            }

            if (!backs[i].transform.IsChildOf(transform))
            {
                var parent = backs[i].transform.parent;
                Debug.LogError(
                    $"[DeckUI] backs[{i}]「{backs[i].name}」不在牌堆容器「{name}」下面," +
                    $"它在「{(parent != null ? parent.name : "无父物体")}」下面。" +
                    "牌堆变少时会把那个物体一起隐藏,请重新拖成自己牌堆的牌背。",
                    backs[i]);
            }
        }
    }
}
