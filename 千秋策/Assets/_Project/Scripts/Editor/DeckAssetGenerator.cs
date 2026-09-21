using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 默认卡组的生成与校验(只在编辑器里跑,不进运行时)。
///
/// 【为什么不手写 .asset 的 YAML】
///   卡组资产里有 <c>List&lt;CardData&gt;</c> 这种"资产引用列表",手写 YAML 只要字段名或
///   fileID 写错一个,Unity 会**静默**读成空卡组 —— 不报错、不警告,进战斗才发现牌堆是空的。
///   走 AssetDatabase + SerializedObject 让 Unity 自己序列化,格式不可能错。
///
/// 【为什么会自动跑】
///   挂在 <c>[InitializeOnLoad]</c> 上,域重载(打开工程 / 改完脚本编译完)时检查一次:
///   两套默认卡组不存在就自动生成。所以你 clone 下来打开工程就有卡组了,不用手动点菜单。
///   已经存在就什么都不做 —— 你在 Inspector 里手调过的内容不会被覆盖。
///
/// 【挂载 & 调整】
///   挂在:不是组件,是编辑器静态类。文件必须在 Assets 下的任意 Editor 文件夹里。
///   菜单:· 千秋策/卡组/重新生成两套默认卡组   —— 删掉再建,**会覆盖手改内容**
///         · 千秋策/卡组/校验全部卡组           —— 对着所有卡组资产跑一遍规则自检
///   常调:· 改卡组构成就改下面的 HanDeck / QinDeck 两张表。
///         · 卡池换代(改了 cardId)之后,这里的 id 会找不到卡 —— 校验菜单会报出来。
/// </summary>
public static class DeckAssetGenerator
{
    /// <summary>
    /// 卡组资产放哪儿。
    ///
    /// **必须在 Resources 下面**:主菜单是用 Resources.LoadAll&lt;DeckPreset&gt;("Decks") 拿卡组列表的
    /// (见 MainMenuController.DeckResourceFolder),放在 Data/ 下面运行时根本扫不到 ——
    /// 表现就是"卡组明明生成出来了,主菜单却说一套都没有,然后兜底成随机构筑"。
    /// 这和卡牌资产必须放 Resources/Cards 是同一个道理(见 CardLibrary)。
    /// </summary>
    private const string DeckFolder = "Assets/_Project/Resources/Decks";

    private const string HanDeckPath = DeckFolder + "/Default_Han.asset";
    private const string QinDeckPath = DeckFolder + "/Default_Qin.asset";

    // ================================================================ 两张卡组表
    //
    // 格式:"cardId 重复次数"
    //
    // 构成规则(策划案§5,由 DeckPreset.ValidateCards 强制):
    //   30 张 = 12 普通 + 8 稀有 + 4 史诗 + 2 传说 + 4 弹性位(可填普通/稀有/史诗)
    //   主朝代 >= 25 张;次朝代 <= 5 张且不得编入传说
    //   同名上限:普通 4 / 稀有 3 / 史诗 2 / 传说 1
    //
    // 【为什么普通卡要靠重复凑,而不是"每种放一张"】
    //   每个朝代只有 8 种普通卡,但普通位要 12 张 —— 不重复根本凑不满。
    //   所以这里刻意让几张主力普通卡吃满 4 张上限(汉弩手 ×4、材官骑士 ×4),
    //   这是构筑的常态:靠重复把某张牌摸到的概率顶上去。构筑界面也支持玩家这么干。
    //
    // 汉·闪击冲阵:材官骑士+骁骑双闪击抢节奏,轻车骑伏兵偷袭,游徼骑与射声士连战滚雪球
    private static readonly string[] HanDeck =
    {
        // ---- 普通 12 张 ----
        "han_001", "han_001", "han_001", "han_001",   // 汉弩手 ×4   弓 3/2 远程(吃满上限)
        "han_002", "han_002", "han_002", "han_002",   // 材官骑士 ×4 骑 2/1 闪击(吃满上限)
        "han_003", "han_003",                         // 边郡戍卒 ×2 步 2/3
        "han_004",                                     // 材官 ×1      步 3/5
        "han_006",                                     // 游徼骑 ×1    骑 3/4 闪击/连战

        // ---- 稀有 8 张 ----
        "han_009",                                     // 轻车骑 ×1    骑 3/4 闪击/伏兵
        "han_010", "han_010",                         // 材官蹶张 ×2 弓 4/3
        "han_012", "han_012",                         // 玄甲校士 ×2 步 5/4 重甲
        "han_014", "han_014", "han_014",              // 骁骑 ×3      骑 5/5 闪击(吃满上限)

        // ---- 史诗 4 张 ----
        "han_017", "han_017",                         // 射声士 ×2    弓 4/2 连战
        "han_019",                                     // 虎贲营 ×1    步 4/5 守护/重甲
        "han_020",                                     // 白马校尉 ×1 骑 5/6 闪击/伏兵

        // ---- 传说 2 张 ----
        "han_022",                                     // 骠骑精骑     骑 6/6 闪击/血战
        "han_023",                                     // 羽林壁垒     步 6/4 守护/重甲

        // ---- 弹性位 4 张(次朝代 1/5)----
        "qin_001", "qin_001",                         // 什伍卒 ×2    步 1/2
        "qin_002", "qin_002",                         // 戍卒 ×2      步 2/3
    };

    // 秦·重甲弓弩:铁鹰剑士架前排,连弩士与穿杨弩手后排输出,陷阵士血战换命
    //
    // 注意这张表和上面那张**不是对称的**:秦当主朝代时,主朝代要 >= 25 张,
    // 那么次朝代最多只能借 5 张 —— 所以这里只从汉借了 2 张(治粟都尉 han_005、
    // 招降 han_007),秦 28 + 汉 2 = 30。
    // 别为了"两边看起来对称"往这里多塞汉卡,一塞就超过 §5.3.2 的 5 张上限。
    private static readonly string[] QinDeck =
    {
        // ---- 普通 12 张 ----
        "qin_004", "qin_004", "qin_004", "qin_004",   // 劲弩手 ×4   弓 3/2(吃满上限)
        "qin_006", "qin_006", "qin_006", "qin_006",   // 疾驰轻骑 ×4 骑 闪击(吃满上限)
        "qin_001", "qin_001",                         // 什伍卒 ×2   步 1/2
        "qin_002",                                     // 戍卒 ×1      步 2/3
        "qin_003",                                     // 秦锐士 ×1    步 3/5

        // ---- 稀有 8 张 ----
        "qin_009",                                     // 斥候骑 ×1
        "qin_011", "qin_011",                         // 穿杨弩手 ×2
        "qin_012", "qin_012", "qin_012",              // 铁鹰剑士 ×3 重甲(吃满上限)
        "qin_013", "qin_013",                         // 陷阵士 ×2    血战

        // ---- 史诗 4 张 ----
        "qin_017", "qin_017",                         // 连弩士 ×2    连战
        "qin_018",                                     // 绝粮道 ×1    策略
        "qin_019",                                     // 锐士营 ×1    守护/重甲

        // ---- 传说 2 张 ----
        "qin_022",                                     // 玄甲铁骑     骑 6/6 闪击/血战
        "qin_023",                                     // 函谷铁卫     步 6/6 守护/重甲

        // ---- 弹性位 4 张(秦系 26 张 + 汉系 4 张)----
        "qin_020", "qin_020",                         // 商君变法 ×2  策略
        "han_005",                                     // 治粟都尉 ×1  器 3/4
        "han_007",                                     // 招降 ×1      策略
    };

    // ================================================================ 自动生成

    /// <summary>
    /// 域重载(打开工程 / 改完脚本编译完)后检查一次:两套默认卡组不存在就自动生成。
    ///
    /// 【为什么要 delayCall 而不是直接跑】
    ///   InitializeOnLoadMethod 是在程序集刚加载完的那一刻执行的,那时 AssetDatabase 还没
    ///   "醒"过来(EditorApplication.isUpdating 为真),这时调 AssetDatabase.CreateAsset /
    ///   Refresh 可能什么都不做,而且不报错 —— 表现就是"脚本明明跑了,资产没建出来"。
    ///   delayCall 把真正的活儿推迟到编辑器空闲的下一帧,那时资源库肯定就绪了。
    /// </summary>
    [InitializeOnLoadMethod]
    private static void EnsureDefaultDecks()
    {
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                // 还在编译/导入,再等一帧
                EnsureDefaultDecks();
                return;
            }

            bool hanMissing = !File.Exists(HanDeckPath);
            bool qinMissing = !File.Exists(QinDeckPath);
            Debug.Log($"[卡组] 启动自检:汉套{(hanMissing ? "缺失" : "已存在")}、" +
                      $"秦套{(qinMissing ? "缺失" : "已存在")}({DeckFolder})");
            if (!hanMissing && !qinMissing) return;

            GenerateDefaultDecks(verbose: false);
        };
    }

    [MenuItem("千秋策/卡组/重新生成两套默认卡组")]
    private static void GenerateDefaultDecksMenu()
    {
        if (!EditorUtility.DisplayDialog("重新生成默认卡组",
                $"会删掉并重建:\n{HanDeckPath}\n{QinDeckPath}\n\n" +
                "如果你在 Inspector 里手调过这两套卡组,那些改动会丢失。继续?",
                "重建", "取消"))
            return;

        GenerateDefaultDecks(verbose: true);
    }

    private static void GenerateDefaultDecks(bool verbose)
    {
        Directory.CreateDirectory(DeckFolder);
        AssetDatabase.Refresh();

        BuildDeck(HanDeckPath, "汉·兵种混杂", "汉",
                  "步骑弓混杂的中军流:材官骑士闪击抢节奏,游徼骑与射声士连战滚雪球。", HanDeck);
        BuildDeck(QinDeckPath, "秦·重甲弓弩", "秦",
                  "铁鹰剑士与函谷铁卫架住前排,连弩士和蹶张神弩在后排持续输出。", QinDeck);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (verbose)
        {
            ValidateAllDecks();
            Debug.Log($"[卡组] 已重新生成两套默认卡组 → {DeckFolder}");
        }
    }

    /// <summary>把一张 "cardId 列表" 落成一个 DeckPreset 资产</summary>
    private static void BuildDeck(string assetPath, string deckName, string primaryDynasty,
                                  string description, string[] cardIds)
    {
        // 先删再建:保证 card 列表是干净的(不删的话旧的会残留在列表里)
        if (File.Exists(assetPath)) AssetDatabase.DeleteAsset(assetPath);

        var preset = ScriptableObject.CreateInstance<DeckPreset>();
        preset.deckName = deckName;
        preset.primaryDynasty = primaryDynasty;
        preset.description = description;
        preset.cards = new List<CardData>();
        preset.quota = new DeckQuota();

        var library = CardLibrary.Current;
        var missing = new List<string>();

        for (int i = 0; i < cardIds.Length; i++)
        {
            var card = library != null ? library.Find(cardIds[i]) : null;
            if (card == null) { missing.Add(cardIds[i]); continue; }
            preset.cards.Add(card);
        }

        if (missing.Count > 0)
            Debug.LogError($"[卡组] 「{deckName}」有 {missing.Count} 个 cardId 在卡池里找不到:" +
                           $"{string.Join("、", missing)}。卡池是不是改过 id?这几种卡不会进卡组。");

        AssetDatabase.CreateAsset(preset, assetPath);

        // 自检:生成出来就该是合法的,不合法立刻报出来(不合法 = 上面那张表写错了)
        var problems = new List<string>();
        if (!preset.IsValid(problems))
            Debug.LogError($"[卡组] 「{deckName}」生成出来不合法,检查 DeckAssetGenerator 里的表:\n" +
                           $"  · {string.Join("\n  · ", problems)}");
        else
            Debug.Log($"[卡组] 「{deckName}」{preset.cards.Count} 张 ✓  {preset.Describe()}");
    }

    // ================================================================ 校验

    [MenuItem("千秋策/卡组/校验全部卡组")]
    public static void ValidateAllDecks()
    {
        Directory.CreateDirectory(DeckFolder);
        AssetDatabase.Refresh();

        var guids = AssetDatabase.FindAssets("t:DeckPreset", new[] { DeckFolder });
        if (guids.Length == 0)
        {
            Debug.LogWarning($"[卡组] {DeckFolder} 下一套卡组都没有。菜单「千秋策/卡组/重新生成两套默认卡组」可以建。");
            return;
        }

        int bad = 0;
        var sb = new StringBuilder();
        sb.Append($"[卡组] 共 {guids.Length} 套:\n");

        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            var preset = AssetDatabase.LoadAssetAtPath<DeckPreset>(path);
            if (preset == null) continue;

            var problems = new List<string>();
            bool ok = preset.IsValid(problems);

            sb.Append(ok ? "  ✓ " : "  ✗ ");
            sb.Append($"{preset.deckName} ({Path.GetFileName(path)})  {preset.Describe()}");
            if (!ok)
            {
                bad++;
                sb.Append("\n");
                for (int j = 0; j < problems.Count; j++) sb.Append($"      · {problems[j]}\n");
            }
            sb.Append("\n");
        }

        if (bad == 0) Debug.Log(sb.ToString());
        else Debug.LogError(sb.ToString());
    }
}
