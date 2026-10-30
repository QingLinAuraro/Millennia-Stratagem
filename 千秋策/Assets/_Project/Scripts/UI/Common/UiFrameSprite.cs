using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 运行时生成的**中空方框** Sprite(1 像素白边 + 九宫格边框),给"选中框 / 可攻击目标框 /
/// 可操作描边"共用 —— 这三处画的其实是同一种东西,只是颜色、粗细、挂的位置不同。
///
/// 【为什么代码生成】项目里没有现成的方框贴图,而这类提示框不该等美术排期。
///   16×16、边框 3 像素、九宫格 border = 3:拉伸到任意尺寸都是干净的 3 像素线,
///   不会像 1 像素图那样在非整数缩放下糊成半透明。
///   资源只生成一次(static 缓存),之后所有调用方拿的是同一个 Sprite。
///
/// 【注意】`Image.type` 必须设成 Sliced 才吃九宫格;设成 Simple 会把边框拉成整块色块。
/// </summary>
public static class UiFrameSprite
{
    private const int Size = 16;
    private const int Border = 3;

    private static Sprite frame;

    /// <summary>共享的中空方框 Sprite(第一次用到时生成)</summary>
    public static Sprite Frame
    {
        get
        {
            if (frame != null) return frame;

            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            var clear = new Color(0f, 0f, 0f, 0f);
            var solid = Color.white;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    bool onEdge = x < Border || y < Border || x >= Size - Border || y >= Size - Border;
                    texture.SetPixel(x, y, onEdge ? solid : clear);
                }
            }
            texture.Apply();

            frame = Sprite.Create(texture, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f, 0,
                                  SpriteMeshType.FullRect, new Vector4(Border, Border, Border, Border));
            return frame;
        }
    }

    /// <summary>
    /// 在 <paramref name="parent"/> 上铺一圈描边(铺满 + 四周外扩 <paramref name="padding"/>),
    /// 返回那个 Image;父级不合法返回 null。描边**不吃射线**,只是个提示,不能挡住底下的卡/排。
    /// </summary>
    public static Image CreateBorder(RectTransform parent, string objectName, Color color, float padding)
    {
        if (parent == null) return null;

        var go = new GameObject(objectName, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        var image = go.GetComponent<Image>();

        image.sprite = Frame;
        image.type = Image.Type.Sliced;
        image.color = color;
        image.raycastTarget = false;

        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(-padding, -padding);
        rt.offsetMax = new Vector2(padding, padding);
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        return image;
    }
}
