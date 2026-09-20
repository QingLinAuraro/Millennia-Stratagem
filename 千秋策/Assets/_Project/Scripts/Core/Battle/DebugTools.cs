using UnityEngine;

/// <summary>
/// 调试功能:只在调试期用的东西,和战斗规则无关。
///
/// 现在只有一个:切换「己方中军驻有敌方袭扰骑兵」。
/// 袭扰机制(§7.4)已经改成"骑兵进中军时挂上的实时 buff、离开就掉",
/// 正常流程下由 BattleRow.RefreshRaiderFlag 自己算 —— 这个开关是给"手上还没有骑兵,
/// 但想验 §8.1.1 的「中军满员时可以改部署进后军」"这种情况用的手动模拟。
///
/// 按键由 BattlefieldManager 上的 debugRaiderToggleKey 定义(Inspector 可改),
/// 触发入口在 BattlefieldManager.Update。
/// </summary>
public static class DebugTools
{
    public static void ToggleDebugRaider(BattlefieldManager m)
    {
        if (m == null || m.playerMid == null) return;

        m.playerMid.HasEnemyRaider = !m.playerMid.HasEnemyRaider;
        Debug.Log($"[Battlefield] 调试:己方中军「驻有敌方袭扰骑兵」= {m.playerMid.HasEnemyRaider}" +
                  "(中军单位容量满 + 这个为真,后军才允许部署)");

        m.RaiseBoardChanged();     // 手牌重新算一遍还能不能出
    }
}
