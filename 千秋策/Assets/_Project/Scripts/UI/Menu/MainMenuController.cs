using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 主菜单的按钮接线与"开始游戏"流程。
///
/// 【流程】(策划案 §主流程)
///   主菜单 ──开始游戏──> [选定出战卡组 + AI 随机构筑一套] ──> Loading(异步加载)──> Battle
///   主菜单 ──卡组构筑──> 打开 DeckEditorPanel
///
/// 【为什么"开始游戏"要先去 Loading 场景,而不是直接 LoadScene】
///   Battle 场景要实例化几十个卡牌 / 单位 / 建筑预制体,直接 LoadScene 会**卡住几百毫秒到几秒**
///   —— 玩家点完按钮画面直接冻结,像是崩了。走 Loading 场景用 LoadSceneAsync 把加载摊到多帧上,
///   中间还能转个进度条。另外 BuildingManager / TurnController 的初始化也在这段时间里完成。
///
/// 【挂载 & 调整】
///   挂在:MainMenu.unity 的 Canvas 上(或 Canvas 下任意一个物体,只要场景里只有一份)。
///   引用:· deckPanel:同场景的 DeckEditorPanel。留空会自动 FindObjectOfType 找。
///         · 六个按钮**不手连** —— 按物体名(BtnStart / BtnBuildng / ...)在 Start 里自动找。
///           改名的话这里会 LogError 报出来是哪个没找到,不会静默失效。
///   常调:· 场景名常量见下面三个 const,改了要同步改 Build Settings 里的场景名。
///         · 卡组列表来自 Resources/Decks,新增卡组资产会自动出现在列表里。
/// </summary>
[DisallowMultipleComponent]
public class MainMenuController : MonoBehaviour
{
    // ---- 场景名。改这里必须同步改 ProjectSettings/EditorBuildSettings.asset 里的场景名 ----
    public const string MainMenuScene = "MainMenu";
    public const string LoadingScene = "Loading";
    public const string BattleScene = "Battle";

    /// <summary>卡组资产放在 Resources 下的哪个目录(相对 Resources,不带扩展名)</summary>
    private const string DeckResourceFolder = "Decks";

    /// <summary>玩家选中的卡组名存在 PlayerPrefs 里</summary>
    private const string ActiveDeckKey = "QSZ.ActiveDeck";

    /// <summary>
    /// 存在 ActiveDeckKey 里的是「卡组名」而不是资产名,因为构筑界面里改出来的那套
    /// 是运行时 ScriptableObject.CreateInstance 建的,**没有资产名**(name 会是空串)。
    /// 为了两种来源能共用同一个键,统一都用 deckName 标识。
    /// </summary>
    private const string ActiveDeckPrefix = "deck:";

    /// <summary>没有卡组资产时的兜底:直接用随机构筑的一套,免得主菜单点开始没反应</summary>
    [Header("选项")]
    [Tooltip("一套卡组都没有时,用随机构筑的结果兜底(勾掉则禁止开始游戏并报错)")]
    [SerializeField] private bool fallbackToRandomDeck = true;
    [Tooltip("进入战斗前先把选中的卡组打进日志,方便确认带进去的是哪套")]
    [SerializeField] private bool logSelectedDeck = true;

    [Header("引用")]
    [Tooltip("卡组构筑面板。留空自动在场景里找")]
    [SerializeField] private DeckEditorPanel deckPanel;

    // 按钮物体名(和 MainMenu.unity 里的层级一致)
    private const string BtnStart = "BtnStart";
    private const string BtnBuildng = "BtnBuildng";
    private const string BtnQuit = "BtnQuit";
    private const string BtnCollection = "BtnCollection";
    private const string BtnRoles = "BtnRoles";
    private const string BtnSettings = "BtnSettings";

    /// <summary>卡组构筑面板的物体名。改物体名要同步改这里</summary>
    private const string DeckPanelObjectName = "DeckEditorPanel";

    private readonly List<DeckPreset> decks = new List<DeckPreset>();
    private int activeIndex;

    // ================================================================ 生命周期

    private void Awake()
    {
        if (deckPanel == null) deckPanel = DeckEditorPanel.Instance;
    }

    private void Start()
    {
        LoadDecks();

        WireButton(BtnStart, OnStartGame);
        WireButton(BtnBuildng, OnOpenDeckBuilder);
        WireButton(BtnQuit, OnQuit);

        // 这三个按你的要求"能用就行",只接一个提示,不做实现
        WireButton(BtnCollection, () => NotYet("图鉴"));
        WireButton(BtnRoles, () => NotYet("规则说明"));
        WireButton(BtnSettings, () => NotYet("设置"));
    }

    // ================================================================ 卡组列表

    private void LoadDecks()
    {
        decks.Clear();

        var loaded = Resources.LoadAll<DeckPreset>(DeckResourceFolder);
        for (int i = 0; i < loaded.Length; i++)
        {
            if (loaded[i] == null) continue;
            decks.Add(loaded[i]);
        }

        if (decks.Count == 0)
        {
            Debug.LogWarning($"[主菜单] Resources/{DeckResourceFolder} 下一套卡组都没有。" +
                             "在编辑器里打开工程会自动生成两套默认卡组(DeckAssetGenerator)," +
                             "或者用菜单「千秋策/卡组/重新生成两套默认卡组」。");
            return;
        }

        // 按资产名排序,保证每次启动列表顺序一致(Resources.LoadAll 的顺序不保证)
        decks.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        // 恢复上次选中的那套(按 deckName 匹配,资产卡组和构筑界面改出来的那套共用这个口径)
        string saved = PlayerPrefs.GetString(ActiveDeckKey, "");
        activeIndex = 0;
        if (!string.IsNullOrEmpty(saved))
        {
            if (saved.StartsWith(ActiveDeckPrefix)) saved = saved.Substring(ActiveDeckPrefix.Length);
            for (int i = 0; i < decks.Count; i++)
            {
                if (decks[i] != null && decks[i].deckName == saved) { activeIndex = i; break; }
            }
        }

        Debug.Log($"[主菜单] 载入 {decks.Count} 套卡组,当前出战:「{decks[activeIndex].deckName}」");
    }

    /// <summary>当前选为出战的那套(可能为 null)</summary>
    public DeckPreset ActiveDeck => activeIndex >= 0 && activeIndex < decks.Count ? decks[activeIndex] : null;

    /// <summary>
    /// 把一套**运行时**建出来的卡组塞进列表(构筑界面点「设为出战」时调)。
    /// 同名就替换,免得反复点攒出一堆重复项。
    /// </summary>
    public void RegisterRuntimeDeck(DeckPreset preset)
    {
        if (preset == null) return;

        for (int i = 0; i < decks.Count; i++)
        {
            if (decks[i] != null && decks[i].deckName == preset.deckName)
            {
                decks[i] = preset;      // 替换(旧的是 CreateInstance 建的,不 Destroy 也无妨,量极小)
                return;
            }
        }

        decks.Add(preset);
    }

    /// <summary>切换出战卡组(构筑界面里点"设为出战"会调它)</summary>
    public void SetActiveDeck(DeckPreset preset)
    {
        int idx = decks.IndexOf(preset);
        if (idx < 0) return;

        activeIndex = idx;
        PlayerPrefs.SetString(ActiveDeckKey, ActiveDeckPrefix + preset.deckName);
        PlayerPrefs.Save();
        Debug.Log($"[主菜单] 出战卡组改为「{preset.deckName}」");
    }

    // ================================================================ 开始游戏

    private void OnStartGame()
    {
        var playerDeck = ResolvePlayerDeck();
        if (playerDeck == null) return;

        // AI 每局随机构筑一套(见 DeckRandomizer 的说明)
        var aiDeck = DeckRandomizer.BuildPreset();
        if (aiDeck == null)
        {
            Debug.LogError("[主菜单] AI 卡组随机构筑失败,没法开始对局。看上面 [AI 构筑] 的报错。");
            return;
        }

        BattleContext.SetDecks(playerDeck, aiDeck, opponentIsRandom: true);

        if (logSelectedDeck)
        {
            Debug.Log($"[主菜单] 开始对局 —— 我方「{playerDeck.deckName}」{playerDeck.cards.Count} 张," +
                      $"敌方「{aiDeck.deckName}」{aiDeck.cards.Count} 张(主朝代 {aiDeck.primaryDynasty})");
        }

        SceneManager.LoadScene(LoadingScene);
    }

    /// <summary>拿到出战卡组。优先资产,其次玩家自己存的,最后看要不要兜底</summary>
    private DeckPreset ResolvePlayerDeck()
    {
        var preset = ActiveDeck;
        if (preset != null && preset.cards != null && preset.cards.Count > 0)
            return preset;

        // 玩家在构筑界面存过的自定义卡组,优先于随机构筑
        if (DeckStorage.HasCustomDeck)
        {
            var custom = DeckStorage.LoadCustomDeck();
            var problems = new List<string>();
            if (custom.Count > 0 && DeckPreset.ValidateCards(custom, new DeckQuota(), "汉", problems))
            {
                var made = ScriptableObject.CreateInstance<DeckPreset>();
                made.deckName = "自定义卡组";
                made.cards = custom;
                return made;
            }
            Debug.LogWarning($"[主菜单] 存的卡组不合法,先不用它:\n  · {string.Join("\n  · ", problems)}");
        }

        if (!fallbackToRandomDeck)
        {
            Debug.LogError("[主菜单] 没有可用的出战卡组,开始游戏被拦下了。" +
                           "先用「卡组构筑」存一套,或勾上 fallbackToRandomDeck。");
            return null;
        }

        Debug.LogWarning("[主菜单] 没有可用的出战卡组,临时随机构筑一套顶上。");
        return DeckRandomizer.BuildPreset();
    }

    // ================================================================ 其它按钮

    private void OnOpenDeckBuilder()
    {
        // 三层兜底。注意面板默认是**未激活**的,所以最后那层必须用能找未激活物体的遍历,
        // 不能用 FindObjectOfType / GameObject.Find(它们对未激活物体返回 null)。
        if (deckPanel == null) deckPanel = DeckEditorPanel.Instance;
        if (deckPanel == null)
        {
            var go = FindDeep(DeckPanelObjectName);
            if (go != null) deckPanel = go.GetComponent<DeckEditorPanel>();
        }

        if (deckPanel == null)
        {
            Debug.LogError($"[主菜单] 找不到 DeckEditorPanel —— 场景里没有一个挂着 DeckEditorPanel 组件的物体" +
                           $"(按名字找过「{DeckPanelObjectName}」)。检查 MainMenu 的层级。", this);
            return;
        }
        deckPanel.Open(decks);
    }

    private void OnQuit()
    {
#if UNITY_EDITOR
        // 编辑器里 Application.Quit 只是停止 Play,先在控制台说一声,免得以为按钮没反应
        Debug.Log("[主菜单] 退出游戏(编辑器里表现为停止运行)");
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void NotYet(string what)
    {
        Debug.Log($"[主菜单]「{what}」还没实现(按当前需求不需要)。");
    }

    // ================================================================ 接线

    /// <summary>
    /// 按物体名找按钮并挂上回调。**找不到会 LogError 报出物体名** ——
    /// 静默失效的话,点按钮没反应会以为是逻辑问题,实际是改名字改断了。
    /// </summary>
    private void WireButton(string objectName, UnityEngine.Events.UnityAction action)
    {
        var go = FindDeep(objectName);
        if (go == null)
        {
            Debug.LogError($"[主菜单] 没找到按钮物体「{objectName}」。它在 MainMenu.unity 里被改名或删掉了?" +
                           $"按钮接线靠物体名,改名要同步改 MainMenuController 里的常量。", this);
            return;
        }

        var button = go.GetComponent<Button>();
        if (button == null)
        {
            Debug.LogError($"[主菜单]「{objectName}」上没有 Button 组件,接不上点击。", go);
            return;
        }

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(action);
    }

    /// <summary>在场景里按名字找物体(包括未激活的 —— GameObject.Find 找不到未激活的)</summary>
    private GameObject FindDeep(string name)
    {
        // 先试当前的,够快
        var found = GameObject.Find(name);
        if (found != null) return found;

        // 再遍历全场景(能命中未激活的)
        var scene = gameObject.scene;
        if (!scene.IsValid()) return null;

        var roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            var hit = FindInChildren(roots[i].transform, name);
            if (hit != null) return hit;
        }
        return null;
    }

    private static GameObject FindInChildren(Transform parent, string name)
    {
        if (parent.name == name) return parent.gameObject;
        for (int i = 0; i < parent.childCount; i++)
        {
            var hit = FindInChildren(parent.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }
}
