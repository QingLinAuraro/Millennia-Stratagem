using UnityEngine;

/// <summary>
/// 跨场景的出战信息:「这一局用哪套卡组打谁」。主菜单写入,战斗场景读取。
///
/// 【为什么需要它】
///   卡组原来是手拖在 Battle.unity 的 DeckController 上的 12 张引用 —— 场景里写死的东西,
///   主菜单没法改。现在主菜单选定一套 DeckPreset 之后写进这里,战斗场景读它来洗牌堆,
///   DeckController 上那份手拖列表就退化成"没有出战信息时的兜底"。
///
/// 【生命周期】
///   静态字段,进程内一直有效 —— 切场景不会掉(场景物体才会掉,静态字段不会)。
///   没有它也能直接进 Battle 开始游戏(会回落到场景里手拖的那套),方便单独调试战斗场景。
///   想清掉(打完回主菜单重新选)调 Clear()。
///
/// 注意:**不要**把它做成 MonoBehaviour 或者 ScriptableObject。
///   做成组件就得 DontDestroyOnLoad,做成资产就会把上一局的运行时状态写进磁盘 ——
///   它只是"这一局"的运行时上下文,静态字段是正解。
/// </summary>
public static class BattleContext
{
    /// <summary>本局玩家的出战卡组。null = 没选过(战斗场景回落到自己 Inspector 上的构筑)</summary>
    public static DeckPreset PlayerDeck { get; private set; }

    /// <summary>本局对手的卡组。null = 没定过(战斗场景回落到"借我方那套")</summary>
    public static DeckPreset OpponentDeck { get; private set; }

    /// <summary>本局是"随机对手"还是"指定对手"(结算界面显示 / 以后做天梯用)</summary>
    public static bool OpponentIsRandom { get; private set; }

    /// <summary>已经开始过至少一局(用来判断"再来一局"要不要保留选择)</summary>
    public static bool HasBattleSetup => PlayerDeck != null || OpponentDeck != null;

    /// <summary>主菜单点「开始游戏」时调:定下这一局的双方卡组</summary>
    public static void SetDecks(DeckPreset playerDeck, DeckPreset opponentDeck, bool opponentIsRandom = false)
    {
        PlayerDeck = playerDeck;
        OpponentDeck = opponentDeck;
        OpponentIsRandom = opponentIsRandom;

        Debug.Log($"[出战] 我方「{(playerDeck != null ? playerDeck.deckName : "未选(用场景默认)")}」 vs " +
                  $"敌方「{(opponentDeck != null ? opponentDeck.deckName : "未定(借我方那套)")}」" +
                  (opponentIsRandom ? "(随机)" : ""));
    }

    /// <summary>回主菜单重新选卡组时清掉,免得下一局还带着上一套</summary>
    public static void Clear()
    {
        PlayerDeck = null;
        OpponentDeck = null;
        OpponentIsRandom = false;
    }
}
