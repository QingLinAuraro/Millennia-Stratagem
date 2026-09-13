using UnityEngine;

/// <summary>
/// 战场场景的"装配工":进 Play 之后自动把还缺的战斗组件补上。
///
/// 为什么需要它:战斗逻辑的核心件(BattlefieldManager / TurnController / CommandPointController)
/// 已经手挂在 Battle.unity 里了,但"AI 对手""单位点选操作""胜负结算界面"这几个是后加的 —— 每次加一件都要
/// 去场景里手动挂一次,漏了就会出现"敌方不动""点单位没反应""打完没结算"这种症状,又很难查。
/// 这里用 RuntimeInitializeOnLoadMethod 在场景加载后统一补齐:**已经手挂过的不会重复建**(先找再建)。
///
/// 注意:这里建出来的组件用的是 Inspector 默认值,想调参(思考延迟、阈值、字号)还是要把它手挂到场景里。
/// 别把已有的场景物体删了 —— 删了也不会崩,但参数就没法在 Inspector 里调了。
/// </summary>
public static class GameBootstrap
{
    /// <summary>只在战斗场景里装配。以后有别的玩法场景,在数组里加名字就行</summary>
    private static readonly string[] BattleScenes = { "Battle" };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallBattleComponents()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (System.Array.IndexOf(BattleScenes, scene.name) < 0) return;

        // 场景里连战场管理器都没有,说明这不是真正的战斗场景(或者场景被改坏了),不要硬塞组件
        if (Object.FindObjectOfType<BattlefieldManager>() == null) return;

        // 顺序无所谓(每个组件都是先找后建),但先确保回合控制器在 —— 敌方 AI 要在 OnEnable 里订阅它
        _ = TurnController.Instance;        // 回合流转(场景里大概率已挂,拿不到会现建)
        _ = EnemyAI.Instance;               // 敌方 AI(策划案§6)
        _ = UnitActionController.Instance;  // 我方单位点选操作(移动 / 攻击)
        _ = BattleResultUI.Instance;        // 胜负结算界面
        _ = BattleSelfTest.Instance;        // 战斗流程自检(F9)

        Debug.Log("[Bootstrap] 战斗组件已就位:EnemyAI / UnitActionController / BattleResultUI / BattleSelfTest");
    }
}
