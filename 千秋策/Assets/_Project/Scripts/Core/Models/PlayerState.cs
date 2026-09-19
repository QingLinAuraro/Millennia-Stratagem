using UnityEngine;

// 玩家状态
public class PlayerState
{
    // 随回合增长上限
    public const int CpGrowthCap = 12;
    // 最大上限
    public const int CpMaxLimit = 24;
    // 是否本地玩家(区分敌我手牌显示)
    public bool isLocal;    
    // 随回合变化的费用上限部分
    public int cpFromRounds;
    // 起始费用
    public int startingCpFromRounds { get; private set; }
    // 卡牌效果影响的cp
    public int cpBonus;
    // 回合总cp
    public int cpMax => Mathf.Clamp(cpFromRounds + cpBonus, 0, CpMaxLimit);
    // 当前剩余cp
    public int cp;
    // 玩家状态，确定为自己
    public PlayerState(bool isLocal) { this.isLocal = isLocal; }
    // 是否能支付费用
    public bool CanAfford(int cost)
    {
        return cp >= cost;
    }
    public bool TrySpend(int cost)
    {
        if (cost <= 0) return true;
        if (cp < cost) return false;
        cp -= cost;
        return true;
    }
    public void StartMatch(int initialCpMax)
    {
        startingCpFromRounds = Mathf.Clamp(initialCpMax, 0, CpMaxLimit);

        cpFromRounds = startingCpFromRounds;
        cpBonus = 0;
        cp = cpMax;
    }
    /// <summary>
    /// 进入新回合:回合数决定的那部分上限**按回合数重算**(不是累加),然后补满当前 CP。
    ///
    /// cpFromRounds = clamp(起始值 + (round - 1) × growth, 0, CpGrowthCap):
    ///   growth 是**每回合的增长值**(增量,不是倍率),第 1 回合是起始值、不走增长。
    ///   growth = 1、起始 1 时:1,2,3,…,11,**第 12 回合起恒为 12**。
    ///   growth = 2、起始 1 时:1,3,5,…,11,第 7 回合到 12 封顶。
    ///   growth ≤ 0 视为不增长(Mathf.Max 兜住),上限就停在起始值上,不会往回扣。
    /// (以前是"每次调用 += growth",同一回合被调两次就白涨一格,而且超出 CpGrowthCap 的回合完全停涨。)
    /// </summary>
    public void BeginTurn(int round, int growth = 1)
    {
        int gained = (round - 1) * Mathf.Max(0, growth);
        cpFromRounds = Mathf.Clamp(startingCpFromRounds + gained, 0, CpGrowthCap);
        cp = cpMax;
    }

    /// <summary>
    /// CP 上限增减(卡牌效果 / 粮草被焚 / 调试键的唯一入口)。只动**卡牌层** cpBonus,
    /// 回合数算出来的 cpFromRounds 一律不碰 —— 所以卡牌减上限顶多把 cpBonus 压到
    /// -cpFromRounds(cpMax 归 0),压不掉自然增长那部分;硬顶仍是 CpMaxLimit(24)。
    ///
    /// delta 可正可负,+N 和 -N 走同一条路。返回夹过之后的 cpMax(不是增量)。
    /// </summary>
    public int ChangeBonus(int delta)
    {
        cpBonus = Mathf.Clamp(cpBonus + delta, -cpFromRounds, CpMaxLimit - cpFromRounds);
        if (cp > cpMax)
        {
            cp = cpMax;
        }
        return cpMax;
    }

    public bool IsGrowthCapped => cpFromRounds >= CpGrowthCap;
    public bool IsAtMaxLimit => cpMax >= CpMaxLimit;
}
