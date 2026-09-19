using System;
using System.Collections.Generic;
using UnityEngine;

public enum GameEventType
{
    CardDrawn,        // 抽牌(手牌区监听)
    CardPlayed,       // 出牌:部署兵牌 / 释放策略牌(提示条、以后的结算都听这个)
    Fatigue,          // 牌库耗尽:抽不到牌的一方自扣大营
}

// 事件参数类基类
public abstract class GameEventArgs
{
    public GameEventType EventType { get; protected set; }
}

public static class EventManager
{
    // 每个事件类型，当前有哪些方法在监听它
    private static readonly Dictionary<GameEventType, Delegate> eventDict = new();

    // 调用的效果：如果这个事件类型已经有人订阅了，就把新监听者追加到后面；如果还没有人订阅，就新开一条
    public static void Subscribe<T>(GameEventType type, Action<T> listener) where T : GameEventArgs
    {
        if (eventDict.TryGetValue(type, out var d))
            eventDict[type] = Delegate.Combine(d, listener);
        else
            eventDict[type] = listener;
    }

    // 取消订阅
    public static void Unsubscribe<T>(GameEventType type, Action<T> listener) where T : GameEventArgs
    {
        if (eventDict.TryGetValue(type, out var d))
            eventDict[type] = Delegate.Remove(d, listener);
    }

    public static void Trigger<T>(T args) where T : GameEventArgs
    {
        if (eventDict.TryGetValue(args.EventType, out var d))
            (d as Action<T>)?.Invoke(args);
        Debug.Log($"[EventManager] {args.EventType} triggered");
    }
}