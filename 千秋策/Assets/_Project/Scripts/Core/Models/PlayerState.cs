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
    /// cpFromRounds = clamp(起始值 + (round - 1) × growth, 0, CpGrowthCap):
    ///   growth 是**每回合的增长值**(增量,不是倍率),第 1 回合是起始值、不走增长。
    /// </summary>
    public void BeginTurn(int round, int growth = 1)
    {
        int gained = (round - 1) * Mathf.Max(0, growth);
        cpFromRounds = Mathf.Clamp(startingCpFromRounds + gained, 0, CpGrowthCap);
        cp = cpMax;
    }

    public int ChangeBonus(int delta)
    {
        cpBonus = Mathf.Clamp(cpBonus + delta, -cpFromRounds, CpMaxLimit - cpFromRounds);
        if (cp > cpMax)
        {
            cp = cpMax;
        }
        return cpMax;
    }

    /// <summary>
    /// 回费(「回复 x 点费用」)。只补**当前费用**,上限一分不涨 —— 走的是「已经花掉的补回来」,
    /// 不是「凭空突破上限」。所以返回值是**实际补了多少**,回费卡在满费时打出去就是 0。
    ///
    /// 上限是 cpMax(自然增长 + 卡牌加成),这里不碰 cpFromRounds,也不碰 cpBonus:
    /// 回费永远无法让这一回合的费用超过上限,也就不可能靠回费堆出超额的牌序。
    /// </summary>
    public int RefundCp(int amount)
    {
        if (amount <= 0) return 0;

        int before = cp;
        cp = Mathf.Clamp(cp + amount, 0, cpMax);
        return cp - before;
    }

    public bool IsGrowthCapped => cpFromRounds >= CpGrowthCap;
    public bool IsAtMaxLimit => cpMax >= CpMaxLimit;
}
