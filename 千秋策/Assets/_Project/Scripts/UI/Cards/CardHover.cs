using UnityEngine;
using UnityEngine.EventSystems;
using DG.Tweening;

/// <summary>
/// 卡牌悬停:上浮 + 放大 + 回正,并把这张卡临时提到最上层。
/// 归位位置由 FanLayout 通过 SetHome 喂进来,这里只负责动画。
///
/// 注意:提到最上层只在悬停期间有效,移开时必须让 FanLayout 把图层顺序恢复成
/// 抽牌顺序 —— 否则看完一张牌它就一直压在最上面,把后面那张牌左边露出来的
/// 费用和卡名盖掉。
/// </summary>
public class CardHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [Header("悬停")]
    [Tooltip("悬停时往上抬多少像素")]
    [SerializeField] private float hoverLift = 90f;
    [Tooltip("悬停时放大到多少倍(1 = 不放大)")]
    [SerializeField] private float hoverScale = 1.12f;
    [SerializeField] private float duration = 0.12f;

    public bool IsHovered { get; private set; }

    private RectTransform rt;
    private Vector2 homePos;
    private float homeRot;
    private Tween tween;
    private FanLayout fan;

    private void Awake() { rt = (RectTransform)transform; }

    /// <summary>FanLayout 在 Add 的时候调用,悬停结束后靠它恢复图层顺序</summary>
    public void Bind(FanLayout owner) => fan = owner;

    /// <summary>FanLayout 每次排完扇形调用:平时直接归位,悬停中的卡只更新数据不动它</summary>
    public void SetHome(Vector2 pos, float rot)
    {
        homePos = pos;
        homeRot = rot;

        if (IsHovered) return;

        rt.anchoredPosition = pos;
        rt.localRotation = Quaternion.Euler(0f, 0f, rot);
        rt.localScale = Vector3.one;
    }

    public void OnPointerEnter(PointerEventData e)
    {
        if (IsHovered) return;
        IsHovered = true;

        // 悬停期间临时提到最上层,方便完整看清这张牌;
        // 移开时由 OnPointerExit 交回给 FanLayout 恢复成抽牌顺序
        transform.SetAsLastSibling();

        tween?.Kill();
        tween = DOTween.Sequence()
            .Join(rt.DOAnchorPos(new Vector2(homePos.x, homePos.y + hoverLift), duration).SetEase(Ease.OutQuad))
            .Join(rt.DOLocalRotate(Vector3.zero, duration).SetEase(Ease.OutQuad))
            .Join(rt.DOScale(hoverScale, duration).SetEase(Ease.OutQuad));
    }

    public void OnPointerExit(PointerEventData e)
    {
        if (!IsHovered) return;
        IsHovered = false;

        // 立刻把图层顺序恢复成抽牌顺序:后摸的牌重新压回这张之上,
        // 这样每张牌左边露出来的费用和卡名不会被"刚看过的那张"盖住
        if (fan != null) fan.RestoreRenderOrder();

        tween?.Kill();
        tween = DOTween.Sequence()
            .Join(rt.DOAnchorPos(homePos, duration).SetEase(Ease.OutQuad))
            .Join(rt.DOLocalRotate(new Vector3(0f, 0f, homeRot), duration).SetEase(Ease.OutQuad))
            .Join(rt.DOScale(Vector3.one, duration).SetEase(Ease.OutQuad));
    }

    private void OnDisable()
    {
        tween?.Kill();
        IsHovered = false;
    }
}
