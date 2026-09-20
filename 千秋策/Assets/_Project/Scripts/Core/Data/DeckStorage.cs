using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 玩家自己构筑的卡组的存盘。存在 PlayerPrefs 里,只记 **cardId 字符串**。
///
/// 【为什么存 cardId 而不是直接存卡组资产】
///   运行时改 ScriptableObject 资产在打包后是写不进磁盘的(PlayerPrefs 能写,资产不能)。
///   所以运行时的"保存卡组"落到 PlayerPrefs;想在工程里留成正式资产,用编辑器菜单
///   「千秋策/卡组/把保存的构筑落成资产」把它转成 .asset(见 EditorDeckTools)。
///   两边用同一套 cardId 序列,互相能对上。
///
/// 【为什么不用 JsonUtility + 文件】
///   PlayerPrefs 在 Windows 上是注册表、在别的平台是各自的标准位置,不用管路径、
///   不用管打包后可写目录、也不用手写 try/catch 兜 IO 异常 —— 这点数据量它完全够用。
///
/// 【挂载 & 调整】
///   挂在:不是组件,纯静态工具。
///   引用:没有。
///   常调:· 不用调。换 key 会让老存档失效(读不到 = 玩家要重新构筑一次)。
/// </summary>
public static class DeckStorage
{
    /// <summary>玩家自己存的卡组。加前缀避免和别的 PlayerPrefs 项撞名</summary>
    private const string CustomDeckKey = "QSZ.CustomDeck";

    /// <summary>cardId 之间的分隔符。cardId 本身是 han_001 这种,不会含逗号</summary>
    private const char Separator = ',';

    /// <summary>有没有存过自定义卡组</summary>
    public static bool HasCustomDeck => !string.IsNullOrEmpty(PlayerPrefs.GetString(CustomDeckKey, ""));

    /// <summary>存一套卡组(cardId 列表)</summary>
    public static void SaveCustomDeck(IEnumerable<string> cardIds)
    {
        if (cardIds == null) { ClearCustomDeck(); return; }

        var list = new List<string>();
        foreach (var id in cardIds)
            if (!string.IsNullOrEmpty(id)) list.Add(id);

        if (list.Count == 0) { ClearCustomDeck(); return; }

        PlayerPrefs.SetString(CustomDeckKey, string.Join(Separator.ToString(), list));
        PlayerPrefs.Save();
    }

    /// <summary>读出存的 cardId 列表(没存过就是空表)</summary>
    public static List<string> LoadCustomDeckIds()
    {
        var result = new List<string>();
        string raw = PlayerPrefs.GetString(CustomDeckKey, "");
        if (string.IsNullOrEmpty(raw)) return result;

        var parts = raw.Split(Separator);
        for (int i = 0; i < parts.Length; i++)
            if (!string.IsNullOrEmpty(parts[i])) result.Add(parts[i]);
        return result;
    }

    /// <summary>
    /// 把存的 cardId 还原成卡牌列表。找不到的 id **跳过并记一条警告** ——
    /// 卡池改动(删卡/改 id)之后老存档必然会对不上,静默丢弃会让玩家以为"卡组莫名少了几张"。
    /// </summary>
    public static List<CardData> LoadCustomDeck(CardLibrary library = null)
    {
        var result = new List<CardData>();
        var ids = LoadCustomDeckIds();
        if (ids.Count == 0) return result;

        library ??= CardLibrary.Current;

        for (int i = 0; i < ids.Count; i++)
        {
            var card = library != null ? library.Find(ids[i]) : null;
            if (card != null) result.Add(card);
            else Debug.LogWarning($"[卡组存档] 找不到 cardId「{ids[i]}」,这一张被跳过了(卡池改过?)。");
        }
        return result;
    }

    public static void ClearCustomDeck()
    {
        PlayerPrefs.DeleteKey(CustomDeckKey);
        PlayerPrefs.Save();
    }
}
