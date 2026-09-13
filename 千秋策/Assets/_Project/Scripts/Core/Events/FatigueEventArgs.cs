/// <summary>
/// 牌库耗尽(疲劳)的结算参数(策划案§5.4.1)。
///
/// 触发时机:任何一次**实际尝试抽牌**(抽牌阶段抽 1 张、或抽牌类策略卡)而牌堆已无牌可抽的瞬间,
/// 逐次触发;一次抽多张就逐张结算(空牌堆打「摸牌3」= 2+4+6 = 12 点自伤)。
/// 结算对象:抽不到牌的那名玩家**自己的大营**;双方各自独立计数,永不重置。
/// 这笔伤害不是攻击:不触发反击、不受守护转移、不吃重甲减免、与军械库 Debuff 无关。
///
/// 谁听它:大营头顶的红色伤害数字已经由 BattleSettlement 直接飘了;
/// 「牌库已空」的提示与 HUD 常驻的「疲劳:每次扣 X 点」可以订阅这个事件。
/// </summary>
public class FatigueEventArgs : GameEventArgs
{
    public FatigueEventArgs() { EventType = GameEventType.Fatigue; }

    /// <summary>哪一方挨了这次疲劳</summary>
    public BattleSide Side;

    /// <summary>这是这一方的第几次疲劳(从 1 开始,只增不减)</summary>
    public int Count;

    /// <summary>这次扣了多少(第 N 次 = N × 2)</summary>
    public int Damage;
}
