using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 加载界面:异步加载战斗场景 + 进度条。
///
/// 【为什么要 "异步" 而不是直接 LoadScene】
///   Battle 场景要实例化几十个卡牌/单位/建筑预制体,同步 LoadScene 会把主线程卡住
///   几百毫秒到几秒 —— 点完按钮画面直接冻住,像是崩了。LoadSceneAsync 把加载摊到多帧,
///   中间能刷进度条,玩家至少知道在动。
///
/// 【为什么还要 "最短显示时长"】
///   场景小的时候加载可能只要 50ms,进度条会"闪一下"就没了,反而显得更卡。
///   所以即使加载完了,也至少把界面留够 minDisplaySeconds 再切场景。
///
/// 【进度条怎么驱动】
///   只改 fillImage.fillAmount(0~1)。**它必须是一个 Image Type = Filled 的图**,
///   否则 fillAmount 完全不起作用(Simple 类型下这个值被忽略,进度条永远满或永远空)。
///   Loading.unity 里 Fill 已经配好了(Image Type = Filled, Fill Method = Horizontal)。
///
/// 【挂载 & 调整】
///   挂在:Loading.unity 的 Canvas 上。
///   引用:· fillImage:进度条填充图(Canvas/LoadingPanel/ProgressBarBg/Fill),必须手连
///         · statusText / tipText:文字,留空也能跑(只是没有文字)
///   常调:· minDisplaySeconds:加载再快也至少显示这么久。调大 = 加载界面停留更久
///         · simulateSlowLoadSeconds:纯调试用,假装加载很慢,方便看进度条动起来。0 = 关
///         · tips:随机提示语,留空则不显示
/// </summary>
[DisallowMultipleComponent]
public class LoadingScreen : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("进度条填充图(LoadingPanel/ProgressBarBg/Fill)。必须是 Image Type = Filled,否则 fillAmount 无效")]
    [SerializeField] private Image fillImage;
    [Tooltip("状态文字,比如「加载中… 45%」。可留空")]
    [SerializeField] private TMPro.TMP_Text statusText;
    [Tooltip("随机提示语。可留空")]
    [SerializeField] private TMPro.TMP_Text tipText;

    [Header("选项")]
    [Tooltip("最短显示时长(秒)。加载再快也至少停留这么久,避免进度条闪一下就没了")]
    [SerializeField] private float minDisplaySeconds = 1.2f;
    [Tooltip("调试用:假装加载耗时这么多秒(0 = 关)。想看进度条匀速动起来就调大")]
    [SerializeField] private float simulateSlowLoadSeconds = 0f;
    [Tooltip("进度条是否平滑跟随(勾上更顺眼,但到达 100% 会稍晚一点点)")]
    [SerializeField] private bool smoothFill = true;
    [Tooltip("平滑速度,越大越跟得紧")]
    [SerializeField] private float smoothSpeed = 3f;

    [Header("文案")]
    [Tooltip("状态文字格式。{0} 会被替换成 0~100 的整数")]
    [SerializeField] private string statusFormat = "加载中… {0}%";
    [Tooltip("随机提示语,进入加载界面时随机挑一条。留空则不显示")]
    [SerializeField] private string[] tips =
    {
        "闪击:部署当回合即可行动。",
        "连战:一回合内可以行动两次。",
        "重甲:受到的伤害减少。",
        "守护:优先替相邻单位承受攻击。",
        "伏兵:进入战场时触发额外效果。",
        "大营是胜负关键 —— 打掉对方大营即胜。",
        "费用每回合增长,高费牌留到中后期。",
        "卡组 30 张:12 普通 / 8 稀有 / 4 史诗 / 2 传说 / 4 任意。",
    };

    /// <summary>加载完要进哪个场景。由 MainMenuController 决定,但默认就是 Battle</summary>
    private string targetScene = MainMenuController.BattleScene;

    private float shownAt;
    private float displayed /* 0~1 的当前显示值 */;

    // 按名字自动找的物体名(和 Loading.unity 的层级一致)。改名要同步改这里
    private const string FillObjectName = "Fill";
    private const string StatusTextObjectName = "StatusText";
    private const string TipTextObjectName = "TipText";

    private void Awake()
    {
        // 引用全按名字自动找 —— 在 Inspector 里什么都不用拖。
        // 找不到会 LogError 报出是哪个物体,不会静默失效。
        if (fillImage == null) fillImage = FindComponent<Image>(FillObjectName);
        if (statusText == null) statusText = FindComponent<TMPro.TMP_Text>(StatusTextObjectName);
        if (tipText == null) tipText = FindComponent<TMPro.TMP_Text>(TipTextObjectName);
    }

    /// <summary>在场景里按名字找组件(包括未激活的物体 —— GameObject.Find 找不到未激活的)</summary>
    private T FindComponent<T>(string objectName) where T : Component
    {
        var go = FindDeep(objectName);
        return go != null ? go.GetComponent<T>() : null;
    }

    private GameObject FindDeep(string objectName)
    {
        var scene = gameObject.scene;
        if (!scene.IsValid()) return GameObject.Find(objectName);

        var roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            var hit = FindInChildren(roots[i].transform, objectName);
            if (hit != null) return hit;
        }
        return null;
    }

    private static GameObject FindInChildren(Transform parent, string objectName)
    {
        if (parent.name == objectName) return parent.gameObject;
        for (int i = 0; i < parent.childCount; i++)
        {
            var hit = FindInChildren(parent.GetChild(i), objectName);
            if (hit != null) return hit;
        }
        return null;
    }

    private void Start()
    {
        shownAt = Time.unscaledTime;

        if (fillImage == null)
        {
            Debug.LogError($"[加载] 场景里找不到进度条填充图「{FillObjectName}」—— 进度条不会动。" +
                           "它在 Loading.unity 里应该是 Canvas/LoadingPanel/ProgressBarBg/Fill。", this);
        }
        else if (fillImage.type != Image.Type.Filled)
        {
            Debug.LogError($"[加载] fillImage 的 Image Type 是 {fillImage.type},不是 Filled —— " +
                           "这个类型下 fillAmount 会被忽略,进度条不会动。", fillImage);
        }

        if (statusText != null) statusText.text = string.Format(statusFormat, 0);

        if (tipText != null)
        {
            if (tips != null && tips.Length > 0)
                tipText.text = tips[Random.Range(0, tips.Length)];
            else
                tipText.gameObject.SetActive(false);
        }

        // 从哪进来的?—— MainMenuController 会先 SetDecks,这里只负责确认带没带上
        if (!BattleContext.HasBattleSetup)
        {
            Debug.LogWarning("[加载] 没有找到出战卡组(BattleContext 是空的)。" +
                             "大概率是直接从 Loading 场景启动的 —— 从 MainMenu 点开始游戏才有卡组。");
        }

        StartCoroutine(LoadRoutine());
    }

    private IEnumerator LoadRoutine()
    {
        // 先让界面画出来一帧,不然加载开始就把这一帧吃掉了,玩家看不到 Loading 界面
        yield return null;

        var op = SceneManager.LoadSceneAsync(targetScene, LoadSceneMode.Single);
        if (op == null)
        {
            Debug.LogError($"[加载] 加载场景「{targetScene}」失败 —— 它在 Build Settings 里吗?");
            yield break;
        }

        // 别让 Unity 自己切:我们要等最短显示时长
        op.allowSceneActivation = false;

        float startTime = Time.unscaledTime;

        while (true)
        {
            float elapsed = Time.unscaledTime - startTime;

            // ---- 算目标进度 ----
            // LoadSceneAsync 的 progress 到 0.9 就停住(剩下 0.1 是激活场景那一瞬间),
            // 所以按 0.9 归一化,让进度条能走到 100%。
            float realProgress = Mathf.Clamp01(op.progress / 0.9f);

            // 调试用的假加载:按时间走,但不超过真实进度太多
            if (simulateSlowLoadSeconds > 0f)
            {
                float fake = Mathf.Clamp01(elapsed / simulateSlowLoadSeconds);
                realProgress = Mathf.Min(realProgress, fake);
            }

            // 最短显示时长也当成一道进度闸门,免得进度条冲到 100% 然后干等
            if (elapsed < minDisplaySeconds)
                realProgress = Mathf.Min(realProgress, elapsed / minDisplaySeconds);

            // ---- 平滑跟随 ----
            if (smoothFill) displayed = Mathf.MoveTowards(displayed, realProgress, smoothSpeed * Time.unscaledDeltaTime);
            else displayed = realProgress;

            PushProgress(displayed);

            // ---- 可以走了吗 ----
            bool loadDone = op.progress >= 0.9f;
            bool minTimeDone = elapsed >= minDisplaySeconds;
            bool slowDone = simulateSlowLoadSeconds <= 0f || elapsed >= simulateSlowLoadSeconds;

            if (loadDone && minTimeDone && slowDone)
            {
                // 收尾:让进度条走到满再切,不然会停在 90% 就跳走
                if (displayed < 1f) { displayed = Mathf.Min(1f, displayed + smoothSpeed * Time.unscaledDeltaTime); PushProgress(displayed); yield return null; continue; }
                break;
            }

            yield return null;
        }

        PushProgress(1f);

        // 给一帧让满格的进度条画出来
        yield return null;

        op.allowSceneActivation = true;
    }

    private void PushProgress(float value)
    {
        value = Mathf.Clamp01(value);

        if (fillImage != null) fillImage.fillAmount = value;
        if (statusText != null) statusText.text = string.Format(statusFormat, Mathf.RoundToInt(value * 100f));
    }
}
