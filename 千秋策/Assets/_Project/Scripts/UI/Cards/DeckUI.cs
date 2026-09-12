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
public class DeckUI : MonoBehaviour
{
    [Header("引用")]
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
            Debug.LogWarning("[DeckUI] backs 是空的,牌堆不会有任何视觉变化(计数文字仍会更新)。", this);
            return;
        }

        for (int i = 0; i < backs.Length; i++)
        {
            if (backs[i] == null)
            {
                Debug.LogError($"[DeckUI] backs[{i}] 是 None,抽牌时会在这里报空引用。", this);
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
