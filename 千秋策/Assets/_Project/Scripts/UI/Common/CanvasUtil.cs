using UnityEngine;

/// <summary>
/// 找画布的小工具:各处的 Instance 自举要把自己(或自己建的物体)挂到某个 Canvas 下面。
///
/// 为什么不直接用 FindObjectOfType&lt;Canvas&gt;():
/// Card.prefab 的根节点自带一个嵌套 Canvas(为了展示的卡能盖住别的东西),场上只要已经摆着卡,
/// FindObjectOfType 就可能先返回那张牌的 Canvas —— 自举出来的东西(飘字、HUD…)就挂到某张牌底下了,
/// 会跟着牌走、还可能被裁掉。这里优先取根画布(isRootCanvas),实在没有根画布才退回第一个画布。
/// </summary>
public static class CanvasUtil
{
    /// <summary>优先返回根画布(Battle 场景里就是 BattleCanvas);没有根画布时返回第一个画布,都没有则 null</summary>
    public static Canvas FindRootCanvas()
    {
        Canvas fallback = null;

        foreach (var canvas in Object.FindObjectsOfType<Canvas>())
        {
            if (canvas == null) continue;
            if (canvas.isRootCanvas) return canvas;
            if (fallback == null) fallback = canvas;
        }

        return fallback;
    }
}
