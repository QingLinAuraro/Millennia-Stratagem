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
///   常调:· 改卡组构成就改 ComposeDeck 里的稀有度方案(DeckQuota.TargetOf 那边也有一份,两边要一致)。
///         · 卡池换代(加了新卡/改了 id)不用动这里 —— 它是按稀有度从卡池里挑的,不认 cardId。
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

    // ================================================================ 构筑方案
    //
    // 【为什么不再写死 cardId 列表】
    //   以前这里是两张 "han_001 ×4" 的表。卡池统一改 id 之后(现在是 qin_u_01 / han_t_03 这种),
    //   整张表全部落空 —— 生成出来是空卡组,而 Unity 不会因此报错,
    //   只在日志里说一句"这些 id 找不到卡",很容易被当成"卡组已经有了"忽略过去。
    //   改成"按稀有度从卡池里挑"之后,id 怎么变都不影响:卡池扫得到,卡组就建得出来。
    //
    // 构成规则(§5.1/§5.2,由 DeckPreset.ValidateCards 强制):
    //   40 张 = 39 张可构筑 + 1 张固定大营
    //   稀有度只有**上限**:普通 ≤39 / 稀有 ≤12 / 史诗 ≤8 / 传说 ≤4(大营不计入)
    //   同名上限:普通 4 / 稀有 3 / 史诗 2 / 传说 1
    //   主朝代 ≥ 29 张;次朝代 ≤ 5 张且不得编入传说
    //
    // 铺法(和 DeckQuota.TargetOf 一致):把前三档吃满,剩下的全给普通
    //   4 传说 + 8 史诗 + 12 稀有 + 15 普通 = 39
    private static readonly Rarity[] TierOrder =
        { Rarity.Legend, Rarity.Epic, Rarity.Rare, Rarity.Ordinary };

    /// <summary>按稀有度方案从卡池里挑 39 张 + 1 张大营。挑不满会把原因写进 problems</summary>
    private static List<CardData> ComposeDeck(CardLibrary library, string dynasty, DeckQuota quota,
                                              List<string> problems)
    {
        var result = new List<CardData>();
        int budget = DeckPreset.DeckSize - 1;

        for (int t = 0; t < TierOrder.Length; t++)
        {
            var rarity = TierOrder[t];
            if (budget <= 0) break;

            // 普通档是**兜底档**:它必须把剩下的坑全吃掉,否则前面的档凑不满
            // (比如传说只有 3 种,少一张)时总张数就永远差一张,卡组直接判非法。
            int want = rarity == Rarity.Ordinary
                ? Mathf.Min(quota.MaxOf(rarity), budget)
                : Mathf.Min(Mathf.Min(quota.TargetOf(rarity), quota.MaxOf(rarity)), budget);
            if (want <= 0) continue;

            // 按卡池顺序摊成候选(每种卡最多同名上限张),取前 want 张 —— 确定性:
            // 同样的卡池每次生成出同一套牌,不会一按菜单就换一套。
            int cap = DeckPreset.MaxCopiesOf(rarity);
            var candidates = new List<CardData>();
            var pool = library.Filter(rarity, dynasty, null);
            for (int i = 0; i < pool.Count; i++)
            {
                // 大营卡的 rarity 也是「普通」,必须排掉,否则会被当普通卡抓进牌堆
                if (pool[i] == null || pool[i].cardType == CardType.Building) continue;
                for (int k = 0; k < cap; k++) candidates.Add(pool[i]);
            }

            int take = Mathf.Min(want, candidates.Count);
            for (int i = 0; i < take; i++) result.Add(candidates[i]);
            budget -= take;

            if (take < want)
                Debug.LogWarning($"[卡组] {dynasty} 的{DeckPreset.RarityName(rarity)}卡只够 {take} 张" +
                                 $"(计划 {want} 张),空出来的位置让给下一档。");
        }

        if (budget > 0)
            problems.Add($"{dynasty} 卡池凑不满 {DeckPreset.DeckSize} 张(还差 {budget} 张)," +
                         "检查卡池是不是缺卡");

        // 大营卡:固定 1 张,占最后一个位置,不计入稀有度
        var camps = library.Filter(null, dynasty, CardType.Building);
        if (camps.Count > 0) result.Add(camps[0]);
        else problems.Add($"{dynasty} 没有大营卡(cardType = Building),卡组必然不合法");

        return result;
    }

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
                  "步骑弓混杂的中军流:材官骑士闪击抢节奏,游徼骑与射声士连战滚雪球。");
        BuildDeck(QinDeckPath, "秦·重甲弓弩", "秦",
                  "铁鹰剑士与函谷铁卫架住前排,连弩士和蹶张神弩在后排持续输出。");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (verbose)
        {
            ValidateAllDecks();
            Debug.Log($"[卡组] 已重新生成两套默认卡组 → {DeckFolder}");
        }
    }

    /// <summary>把一套卡组落成 DeckPreset 资产(内容由 ComposeDeck 按稀有度方案从卡池里挑)</summary>
    private static void BuildDeck(string assetPath, string deckName, string primaryDynasty,
                                  string description)
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
        var composeProblems = new List<string>();

        if (library != null && library.Count > 0)
            preset.cards = ComposeDeck(library, primaryDynasty, preset.quota, composeProblems);
        else
            composeProblems.Add("CardLibrary.Current 是空的,扫不到卡牌资产(是不是不在 Resources 下面?)");

        AssetDatabase.CreateAsset(preset, assetPath);

        // 自检:生成出来就该是合法的,不合法立刻报出来(不合法 = 卡池缺卡,或者规则改了没同步)
        // ⚠ IsValid 会 Clear 传进去的列表,所以先拷一份,别把 ComposeDeck 的原因冲掉
        var problems = new List<string>(composeProblems);
        var ruleProblems = new List<string>();
        if (!preset.IsValid(ruleProblems)) problems.AddRange(ruleProblems);

        if (problems.Count > 0)
            Debug.LogError($"[卡组] 「{deckName}」生成出来不合法:\n  · {string.Join("\n  · ", problems)}");
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
