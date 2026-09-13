using UnityEngine;

/// <summary>
/// 一名玩家的对局状态。
///
/// 现在只有指挥点(CP)池 —— 策划案§2.4:回合开始的补给阶段「CP 上限 +1 并补满」;
/// §4.5:第 1 回合 1 CP,第 3 回合 3 CP,第 7 回合后 7~12 CP。
/// 出牌(部署费用)和单位行动(行动费用)共用这一个池子(§2.5)。
///
/// 疲劳计数在 DeckController 里(它是牌堆的事),回合流程还没做,
/// 目前由 CommandPointController 代替回合状态机调用 BeginTurn()。
/// </summary>
public class PlayerState
{
    /// <summary>
    /// 自然增长的封顶:每回合 +1 涨到这里就不再涨了(策划案§2.4 补给阶段)。
    /// 超过这个数的唯一途径是"提升 CP 上限"的卡牌,见 IncreaseMax。
    /// </summary>
    public const int CpGrowthCap = 12;

    /// <summary>
    /// CP 上限的硬顶。不管是自然增长还是卡牌抬上来的,谁都越不过 24 ——
    /// 后期那些"费用上限 +N"的牌(IncreaseMax)顶到这个数就停。
    /// </summary>
    public const int CpMaxLimit = 24;

    public bool isLocal;    // 是否本地玩家(区分敌我手牌显示)

    /// <summary>CP 上限(每回合 +1,自然增长到 CpGrowthCap 为止;日后粮草被毁会 -2,§2.3)</summary>
    public int cpMax;

    /// <summary>本回合还剩多少 CP</summary>
    public int cp;

    public PlayerState(bool isLocal) { this.isLocal = isLocal; }

    public bool CanAfford(int cost) => cp >= cost;

    /// <summary>
    /// 扣费。不够就一分都不扣并返回 false —— 这样调用方可以先判定后执行,
    /// 不会出现"钱扣了牌没打出去"。
    /// </summary>
    public bool TrySpend(int cost)
    {
        if (cost <= 0) return true;
        if (cp < cost) return false;
        cp -= cost;
        return true;
    }

    /// <summary>开局补给:第 1 回合的 CP(策划案§4.5 = 1)</summary>
    public void StartMatch(int initialCpMax)
    {
        cpMax = Mathf.Clamp(initialCpMax, 0, CpMaxLimit);
        cp = cpMax;
    }

    /// <summary>
    /// 回合开始的补给阶段:上限 +growth 并补满(策划案§2.4)。
    /// 自然增长封顶 CpGrowthCap:到了 12 就不再涨(卡牌抬上来的部分也不会被这里顶掉)。
    /// </summary>
    public void BeginTurn(int growth = 1)
    {
        if (cpMax < CpGrowthCap)
            cpMax = Mathf.Min(CpGrowthCap, cpMax + Mathf.Max(0, growth));

        cp = cpMax;
    }

    /// <summary>
    /// CP 上限 +N(后期"费用上限"类卡牌的入口)。受硬顶 CpMaxLimit 限制,
    /// 返回实际涨了多少(顶到 24 之后就是 0,调用方据此决定要不要提示"已达上限")。
    /// </summary>
    public int IncreaseMax(int amount)
    {
        if (amount <= 0) return 0;

        int before = cpMax;
        cpMax = Mathf.Clamp(cpMax + amount, 0, CpMaxLimit);
        if (cp > cpMax) cp = cpMax;     // 上限被别的东西压过时,手头的点数跟着收

        return cpMax - before;
    }

    /// <summary>CP 上限 -N(粮草被摧毁的 Debuff,§2.3);当前值跟着夹一下</summary>
    public void ReduceMax(int amount)
    {
        cpMax = Mathf.Max(0, cpMax - Mathf.Max(0, amount));
        cp = Mathf.Min(cp, cpMax);
    }

    /// <summary>自然增长是否已经到顶(12),HUD 提示用</summary>
    public bool IsGrowthCapped => cpMax >= CpGrowthCap;

    /// <summary>CP 上限是否已经到硬顶(24)</summary>
    public bool IsAtMaxLimit => cpMax >= CpMaxLimit;
}
