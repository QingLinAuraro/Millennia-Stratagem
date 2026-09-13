using TMPro;
using UnityEngine;

/// <summary>
/// 运行时补一个文本的小工具。
///
/// 场景里的占位图(cost1 = 己方 CP 面板、nextround = 结束回合按钮)没有文字子物体,
/// 又不想为了几个字去改场景 —— 这里直接建一个 TextMeshProUGUI,字体借用场景里已有的
/// 中文 TMP 字体(没有就退回 TMP 默认字体)。运行时建出来的那一份只在 Play 期间存在,
/// 关掉 Play 就没了。
/// </summary>
public static class RuntimeText
{
    /// <summary>在 parent 里建一个铺满它的文本</summary>
    public static TMP_Text Create(RectTransform parent, string objectName, string text, float fontSize,
                                  TextAlignmentOptions alignment = TextAlignmentOptions.Center,
                                  Color? color = null)
    {
        var go = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var tmp = go.GetComponent<TMP_Text>();
        var font = ResolveFont();
        if (font != null) tmp.font = font;
        else Debug.LogWarning($"[RuntimeText] 场景里找不到 TMP 字体资源,「{objectName}」可能显示不出来。");

        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.alignment = alignment;
        tmp.color = color ?? Color.white;
        tmp.raycastTarget = false;      // 别挡住底下的按钮/卡牌
        return tmp;
    }

    /// <summary>
    /// 借用场景里已有的字体资源。优先挑"画得出中文字形"的那个 ——
    /// 场景里可能混着英文默认字体,拿它飘中文会全是方框。
    /// </summary>
    public static TMP_FontAsset ResolveFont()
    {
        var texts = Object.FindObjectsOfType<TMP_Text>();
        TMP_FontAsset first = null;

        for (int i = 0; i < texts.Length; i++)
        {
            var font = texts[i] != null ? texts[i].font : null;
            if (font == null) continue;
            if (first == null) first = font;
            if (font.HasCharacter(CjkSample)) return font;      // 这个字体有中文字形,就用它
        }

        if (first != null) return first;
        return TMP_Settings.defaultFontAsset;
    }

    // 拿一个一定会出现在提示文案里的字,判断字体有没有中文字形
    private const char CjkSample = '策';

    /// <summary>Overlay 画布返回 null(射线/坐标转换就该传 null),其余返回画布相机</summary>
    public static Camera EventCameraFor(RectTransform rect)
    {
        if (rect == null) return null;
        var canvas = rect.GetComponentInParent<Canvas>();
        if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
        return canvas.worldCamera;
    }
}
