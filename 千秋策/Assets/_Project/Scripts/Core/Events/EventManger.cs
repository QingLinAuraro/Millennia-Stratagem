using System;
using System.Collections.Generic;
using UnityEngine;

public enum GameEventType
{
    CardDrawn,        // 抽牌(手牌区监听)
    CardPlayed,       // 出牌:部署兵牌 / 释放策略牌(提示条、以后的结算都听这个)
    Fatigue,          // 牌库耗尽:抽不到牌的一方自扣大营(策划案§5.4.1)
    // 以后按策划案§3.3.2往里加:UnitDied / 单位被击伤 等(回合流转不走这里,走 TurnController.TurnStarted)
}

public abstract class GameEventArgs
{
    public GameEventType EventType { get; protected set; }
}

public static class EventManager
{
    private static readonly Dictionary<GameEventType, Delegate> eventDict = new();

    public static void Subscribe<T>(GameEventType type, Action<T> listener) where T : GameEventArgs
    {
        if (eventDict.TryGetValue(type, out var d))
            eventDict[type] = Delegate.Combine(d, listener);
        else
            eventDict[type] = listener;
    }

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