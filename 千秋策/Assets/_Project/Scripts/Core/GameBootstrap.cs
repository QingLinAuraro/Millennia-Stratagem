using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 战场场景的"装配工":进 Play 之后自动把还缺的战斗组件补上。
///
/// 为什么需要它:战斗逻辑的核心件(BattlefieldManager / TurnController / CommandPointController)
/// 已经手挂在 Battle.unity 里了,但"AI 对手""单位点选操作""胜负结算界面"这几个是后加的 —— 每次加一件都要
/// 去场景里手动挂一次,漏了就会出现"敌方不动""点单位没反应""打完没结算"这种症状,又很难查。
/// 这里统一补齐:**已经手挂过的不会重复建**(先找再建)。
///
/// 【时机:为什么用 sceneLoaded 而不是 AfterSceneLoad】—— 这里踩过一个坑,写下来免得改回去
///   AfterSceneLoad 这个回调**跑在场景自己的 Awake 之前**(实测:它执行时 Battle.unity 还只是个
///   "备份场景",随后才 Loaded scene,然后是 BattlefieldManager.Awake)。
///   于是"先找后建"里那个"找"必然找不到任何场景组件 —— 全是现建的。而 TurnController.Awake /
///   BattlefieldManager.Awake 里都是**无条件** instance = this,场景里手挂的那份一 Awake 就把
///   静态 Instance 顶掉了。结果:
///     · EnemyAI 订阅的是被顶掉的幽灵 TurnController → 敌方回合永远没人操作;
///     · BattleResultUI 建在旧 Canvas 下,场景一加载连人带订阅一起销毁 → 打完不弹结算。
///   两个症状都是这一个时序错出来的。
///   sceneLoaded 跑在场景 Awake/OnEnable 之后、Start 之前,那时场景组件已经各就各位,
///   "先找后建"才真的能"找到"。
///
/// 注意:这里建出来的组件用的是 Inspector 默认值,想调参(思考延迟、阈值、字号)还是要把它手挂到场景里。
/// 别把已有的场景物体删了 —— 删了也不会崩,但参数就没法在 Inspector 里调了。
/// </summary>
public static class GameBootstrap
{
    /// <summary>只在战斗场景里装配。以后有别的玩法场景,在数组里加名字就行</summary>
    private static readonly string[] BattleScenes = { "Battle" };

    /// <summary>
    /// 登记场景加载回调。SubsystemRegistration 只跑一次(整局游戏的第一帧之前),
    /// 此时挂 sceneLoaded 才能保证后面每一次进场都被接住。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Register()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;   // 关掉"域重载"再进 Play 时防止重复登记
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (System.Array.IndexOf(BattleScenes, scene.name) < 0) return;

        InstallBattleComponents();
    }

    private static void InstallBattleComponents()
    {
        // 场景里连战场管理器都没有,说明这不是真正的战斗场景(或者场景被改坏了),不要硬塞组件
        if (Object.FindObjectOfType<BattlefieldManager>() == null)
        {
            Debug.LogWarning("[Bootstrap] 场景里找不到 BattlefieldManager,放弃装配战斗组件。");
            return;
        }

        // 顺序无所谓(每个组件都是先找后建)。这时候场景手挂的 TurnController 已经 Awake 过了,
        // 下面这行拿到的是它本人,不会是新建的临时货。
        var turn = TurnController.Instance;   // 回合流转(场景里已挂)

        // 先手提示必须在 TurnController 开第 1 回合**之前**就位 —— 它订阅 FirstSideDecided,
        // 晚一步建出来就收不到广播、提示不会弹。GameBootstrap 跑在场景 Awake 之后、Start 之前,
        // 而回合是 Start 里延迟一帧才开的,所以这里建正好赶得上。
        _ = BattleIntroUI.Instance;           // 开局先手提示

        _ = EnemyAI.Instance;                 // 敌方 AI(策划案§6)——它要在 OnEnable 里订阅 turn
        _ = UnitActionController.Instance;    // 我方单位点选操作(移动 / 攻击)
        var resultUI = BattleResultUI.Instance;   // 胜负结算界面
        _ = BattleSelfTest.Instance;          // 战斗流程自检(F9)

        Debug.Log($"[Bootstrap] 战斗组件已就位:EnemyAI / UnitActionController / BattleResultUI / BattleSelfTest" +
                  $"(TurnController={(turn != null ? "有" : "缺")}," +
                  $" 结算UI 在「{(resultUI != null ? resultUI.gameObject.name : "null")}」)");
    }
}
