# 千秋策（Millennia Stratagem）

秦汉对阵的卡牌对战游戏。Unity 2022.3.54f1c1 + uGUI + TextMeshPro + DOTween。

---

## 目录结构

```
千秋策（Millennia Stratagem）/
├─ README.md                  ← 你在这里
├─ Docs/                      策划案与数据表
│   ├─ 千秋策策划案.md          设计文档(卡片效果、费用曲线、卡池规则都以它为准)
│   └─ 数据表demo.xlsx          数值表(卡池表由脚本从策划案写回,别手改)
├─ Tools/                     数据表 / 卡牌资源的生成脚本(Python)
│   ├─ build_card_assets.py      xlsx → CardData .asset(96 张卡)
│   ├─ build_dynasty_sheets.py   生成秦/汉卡池工作表 + 回写策划案标记区
│   ├─ build_qin_sheet.py        秦朝卡组设计表
│   └─ _final_check.py           数据表自检
└─ 千秋策/                     ← Unity 工程本体
    ├─ Assets/
    │   ├─ _Project/           本项目自己的资源(下面详述)
    │   ├─ Plugins/            第三方:DOTween(别动)
    │   ├─ Resources/          DOTween 设置(别动)
    │   └─ TextMesh Pro/       TMP 包自带资源(别动)
    ├─ ProjectSettings/        工程设置(Build 场景列表里是 Battle.unity)
    └─ Assembly-CSharp.csproj  命令行编译用
```

### Assets/_Project

```
_Project/
├─ Art/                       美术
│   ├─ Cards/                   牌背(Cardback.png)
│   ├─ Buildings/               建筑图(build.png)
│   ├─ UI/ Icons/ Units/        面板按钮 / 费用攻击血量等图标 / 兵种立绘(空,待补)
│   └─ Effects/ Backgrounds/    特效贴图 / 战场背景(空,待补)
├─ Animations/                动画控制器(Battle.controller,目前是空壳)
├─ Audio/BGM/ SFX/            音乐 / 音效(空,待补)
├─ Data/                      ScriptableObject 数据
│   ├─ Cards/Han/ Qin/          汉、秦卡池各 24 张
│   ├─ Factions/                朝代配置(空,待补)
│   ├─ Effects/                 策略卡效果预设(空,待补)
│   └─ Config/                  平衡数值表,如 CP 上限 12/24(空,待补)
├─ Fonts/                     中文字体资源
├─ Localization/              多语言表(空,待补)
├─ Materials/ Shaders/        材质 / 着色器(空,待补)
├─ Prefabs/
│   ├─ UI/                      Card.prefab、CardBack.prefab
│   ├─ Battle/                  Build.prefab(战场建筑)
│   ├─ Units/                   兵种预制体(空,待补)
│   └─ VFX/                     攻击 / 受击特效(空,待补)
├─ Scenes/                     Battle.unity(战斗)、MainMenu.unity、Loading.unity
└─ Scripts/
    ├─ Core/                   纯逻辑 / 数据层
    │   ├─ AI/                   EnemyAI(敌方评分型 AI,策划案§6)
    │   ├─ Battle/               BattlefieldManager、BattleRow、FieldUnit、BattleRules、BuildingSpec、TurnController、
    │   │                        BattleSettlement(战斗结算)、CardEffectResolver(卡牌效果)、BattleSelfTest(F9 自检)
    │   ├─ Cards/                DeckController、EnemyDeckController(数据层,需要挂载来驱动)
    │   ├─ Data/                 CardData(卡牌 ScriptableObject 定义)、CardEffect / CardEffectDatabase(效果表)
    │   ├─ Events/               EventManger、CardDrawnEventArgs、CardPlayedEventArgs、FatigueEventArgs
    │   ├─ Models/               Deck、Hand、PlayerState
    │   ├─ Utils/                通用工具(空,待补)
    │   └─ GameBootstrap.cs      战斗场景的组件自举(没手挂的组件在这里补上)
    ├─ UI/                     表现层
    │   ├─ Battle/               CommandPointController、BattleMessageUI、BattleHudButtons、FloatingTipUI、
    │   │                        UnitActionController(点选/拖动操作单位)、FieldUnitDragProxy(把指针事件转给前者)、
    │   │                        BattleResultUI(胜负结算界面)
    │   ├─ Cards/                HandUI、EnemyHandUI、FanLayout、DeckUI、CardDisplay、CardDragPlay、CardHover、CardHoverPreview
    │   └─ Common/               RuntimeText(运行时拼 TMP 文本)
    └─ Editor/                 编辑器扩展(空,待补)
```

---

## 怎么跑

1. Unity Hub 用 2022.3.54f1c1 打开 `千秋策/` 目录。
2. 打开 `Assets/_Project/Scenes/Battle.unity`,Play。
3. 不开 Unity 也能做编译检查(在 `千秋策/` 下执行):

```powershell
dotnet build Assembly-CSharp.csproj -t:Rebuild -v minimal -nologo
```

成功标志:`已成功生成。 0 个警告 0 个错误`。

调试键(战斗场景):

| 键 | 作用 |
|---|---|
| `T` | 结束当前回合(TurnController) |
| `=` | 我方 CP 上限 +1(CommandPointController) |
| `M` | 敌方随机打出一张策略牌(默认关着;开着会跟 AI 抢出牌) |
| `N` | 切换前军的「敌方袭扰骑兵」标记(§8.1.1 后军部署的调试开关) |
| `F9` | 跑一遍战斗流程自检(BattleSelfTest,会在 Console 打 ✅/❌) |
| `1` / `2` / `3` | 把选中的我方单位移动到 后军 / 中军 / 前军(拖动的等价入口) |
| 鼠标拖动 | **唯一的动作手势**:拖我方兵牌 → 落在一条排上 = 移动(绿的可落、红的不可),落在一个套红框的敌人身上 = 攻击 |
| 鼠标点击 | 只做**选中**(亮金框 + 标出能打谁),不发出任何动作;点空白处取消选中 |

---

## 战斗流程现状

- **回合**:`TurnController` 是唯一权威。点「结束回合」(场景物体 `NextRound`)或按 `T` 结束我方回合;轮到敌方时 `EnemyAI` 接管,自己走完并交回回合。
- **费用**:第 1 回合 CP 初始化为 1(不走增长),之后每到自己回合 +1 并补满;自然增长上限 12;打出策略卡会让该方 CP 上限 +1,硬上限 24。
- **手牌**:开局各抽 5;每到自己回合(第 2 回合起)再抽 1。敌方手牌只显示牌背。
- **出牌**:拖动落点由 `BattlefieldManager.TryResolveDrop` 统一裁决;不是我方回合时会弹回并飘字「现在是对方的回合」。
- **单位行动**:**只有拖动这一个动作手势** —— **拖**我方兵牌 → 落在一条排上 = 移动(拖动开始时所有可落的排亮绿、指针停在不该停的排上亮红),落在**一个套红框的敌人身上** = 攻击(松手时那个框会变亮黄)。走的是策划案§10.1「长按拖动到目标」的原案。单击**只做选中**(亮金框 + 把能打的目标套红框),不发出任何消耗 AP 的动作。红的才是现在打得到的 —— 射程不够、被守护挡住的目标不亮框,「为什么打不到」一眼能看出来。数字键 1/2/3 是移动的等价入口。射程、守护、重甲、反击全部由 `BattleRules` + `BattleSettlement` 判定。
  - 为什么不做"点选 + 点目标":单击走 `Update()` 里的 `Input.GetMouseButtonDown`,拖动走 EventSystem 的 `IBeginDragHandler`,是**两套独立输入**。同一个按压里两边都会判,长按时"点已经触发了、拖动又刚启动",状态互相覆盖,表现为点了没反应、或者拖到一半动作已经发出去了。统一成拖动之后,一次按压只有一个结局。
  - 拖动落点**攻击优先于移动**:指针底下是"打得到的敌人"就打它,否则按排处理;落到别处什么都不做(不会误发动作)。
- **同排不算移动**:把兵牌拖回它现在所在的那条排会被拒(提示「已经在这条排上」),**不扣行动力也不扣 CP** —— 判定在 `BattleRules.CanMoveTo` 里(`from` 与落点排是同一个引用就判不合法),玩家、AI 走的是同一条,AI 也不会为原地不动浪费行动。
- **只能向前**:后军→中军→前军→敌方中军(只有骑兵能进,进敌方中军 = 袭扰)。**除袭扰骑兵撤回前军外,任何单位都不允许朝己方后排走**。判据只有一个、写在 `BattleRules.CanMoveTo` 里:**落点必须等于 `TryForwardRow(unit, from)` 指出的那一条排**(`Back` 单独给一句更好懂的理由)。`CanMoveTo` 的判序也不能乱:同排 → **袭扰撤回** → 方向闸门 → 容量 → 前军占位。
  - **方向按"单位自己的阵营"算,不能按"排的 Side"算**。共享前军在全场只有一条(物理上就是 `playerFront`),它的 `row.Side` **恒为 Player**。这个坑踩了两次,两次都让"一步合法前进"被判成"向后走":
    - 错法①:拿"序号变大才算前进"去判方向。序号当时还是**绝对编号**,而 `targetType`(`Mid`/`Back`)**不带阵营** —— 敌方从自己的中军(3)回共享前军时,它的 `Mid` 被算成序号 2 < 3。
    - 错法②:`TryForwardRow` 内部用 `from.Side` 推方向。站在敌方中军时,它算出"往前走一步 = **敌方后军**",于是"敌方中军 → 共享前军"被拒。**修法:`TryForwardRow(unit, from, …)` 收单位、按 `unit.Side` 算方向。**
    - 现象:「我前军还有兵的时候 AI 不攻击,反而一直试着移动然后一直被拒」「我的前军单位死了之后 AI 满费不移动」。日志会打 `[规则] 拦下一次非前进的移动:… 按它自己的阵营,往前走一步是 X` —— 这句话直接告诉你正确答案是什么。
  - **`RowRank(side, type)` 是"按视角编号"的相对排名,不是棋盘的绝对位置**。后军 = 0 → 中军 = 1 → 共享前军 = 2 → 对面中军 = 3 → 对面后军 = 4,**两边各算各的**:同一个共享前军,在我方视角是第 2 位,在敌方视角**也是**第 2 位。`ForwardDistance` = 两个排各用各的视角做减法。
    - **踩过的大坑(比方向那两个更致命)**:以前它叫 `RowOrderIndex`,是"绝对编号"——共享前军恒为 2、**敌方中军是 3**。于是从敌方视角看,前军跑到他中军**后面**去了:`ForwardDistance(敌方中军, 共享前军) = 2 - 3 = -1`,被 `CanAttack` 判成「目标在身后」。后果是**我方兵站在前军时,敌方近战永远打不到** —— 前军变成一堵**单向的墙**。现象:「我在前军有单位、AI 在中军有单位、满费用但不攻击」,以及 `超出射程 32 条`(32 = 全场上所有「敌方 → 我方前军」的组合数,一个不落全被拒)。
    - 这个 bug 藏得久,是因为**玩家的攻击一直是对的**(己方视角那半本来就是对的),而且 AI 之前因为方向闸门根本走不到前军,没人撞上它。
    - 自检里有一张**排距矩阵 25 格**全表断言(`5e` 段),纵轴攻击者所在排、横轴目标所在排 —— 以后再动这块,改错一格就 ❌。
  - **要判"方向"用 `TryForwardRow`(明文查表),要判"距离"用 `RowRank`/`ForwardDistance`**,两者都**不要**自己写"序号 ± 1"。`TryForwardRow` 现在不做任何算术:后军 → 中军(自己)、中军 → 前军、前军 → **对面中军**(按单位阵营)。中军那一格还要额外确认 `from.Side == unit.Side`(站在**对面**中军上的单位没有"再往前走"这回事,那是向后撤,只有袭扰骑兵能走)。
  - 方向闸门必须**排在袭扰撤回分支之后** —— 袭扰撤回是唯一允许反向的分支,顺序写反 = 骑兵再也撤不回来。
  - 袭扰骑兵的撤回是**唯一**的向后移动,而且四个条件缺一不可:目标是共享前军、它真的站在敌方中军、**不是刚冲进去的那个回合**、前军没被敌方占着。
  - **「中军」这个叫法有歧义,移动落点必须问 `BattlefieldManager.ResolveMoveRow`**:站在共享前军上时,单位的「中军」是**对面的中军**(§7.4 袭扰),而回自己中军是"往后走"、根本不合法。这个解析**只能有一份** —— 曾经 UI(`RowCarrierFor`)、AI(`MoveCarrier`)、`BattlefieldManager.FindRow` 各算一份,其中两份算的是"自己的中军",于是玩家把骑兵从前军往前拖会被判不合法、AI 却走得过去,判定还会用错排的容量与占位。现在三处统一调 `ResolveMoveRow`。AI 日志也会把落点排的真实名字打出来(`（现在落在 敌方中军）`),免得把"从共享前军冲进你中军"看成"退回自己中军"。
  - 袭扰骑兵的撤回是**唯一**的向后移动,而且四个条件缺一不可:目标是共享前军、它真的站在敌方中军、**不是刚冲进去的那个回合**、前军没被敌方占着。
- **前军只容一方**:前军里有敌方兵牌时进不去(双方一样),要先把他们清掉;袭扰骑兵撤回前军也受这条限制。见下面"共享前军"那条。
- **共享前军(改这块之前先读这一条)**:前军是双方**同一条排**,场景里它就是 `playerFront`,所以它的 `row.Side` **恒为 Player**。两条由此而来的规则与坑:
  - **前军只容得下一方的单位**。里面站着敌人的兵牌就进不去(要先清掉),袭扰骑兵**撤回**前军同样受这条限制。所以前军是「抢」来的:谁先站上去谁占着,对面只能隔着打 —— 想够到对方中军的大营,必须先把前军拿下来。判定在 `BattleRules.OnlySideOccupies`(空排也算可用,建筑不参与),`CanMoveTo` 里对 `Front` 统一检查。部署本来就进不了前军(`CanDeployUnit` 直接返回「前军只能靠移动进入」),所以这条只影响移动。
  - 任何"按 `row.Side` 判断敌我"的代码在共享前军上都是错的。已经处理的四处:①`BattlefieldManager.FindRow` 查 `Front` 时直接返回共享排(否则敌方一往前走就报"战场上没有这条排");②单位的 `Side` 由落位时显式传入,**不从 `row.Side` 推**,`SetRow` 也不再改阵营(否则敌方兵走进前军会变成"我方",互相打不了);③`CardEffectResolver.UnitsOf` 按**单位自己的 Side** 筛,不按排的 Side(否则 AI 和策略卡都看不到站在前军里的敌人);④部署扣费按"落位方"走 `SidePayingFor`,不是按 `row.Side`(否则敌方往共享前军落位会扣我方的 CP)。
- **结算**:伤害 → 减伤 → 死亡 → 反击 → 血战回血(§7.1.2);建筑被打空变成"废墟"留在链上,可被维修卡修复(§7.3);大营 HP 归零立即判负。
- **扣费顺序**:攻击/移动都是「先判能不能打 → 先结算 → 再扣 AP 与行动费用 CP」,扣费失败会把伤害原样回滚 —— 不会出现"只扣费用、没有伤害"。受击会飘 `-3（5→2）` 并抖一下。
- **疲劳**:牌堆抽空后每次摸牌按 2/4/6/8…递增扣**自己**大营的 HP(§5.4.1),双方各自计数、永不重置。
- **AI**:`EnemyAI` 按**优先级分层**打一整个回合,姿态分抢血/防守/均衡。原则如下,踩过坑的都记在这里:
  - **优先级:部署 > 策略 > 移动 > 攻击 > 空过,而且尽可能把费用花光**。`TakeTurn` 因此是分层的(不是"所有候选放一起按分数排序"—— 那样一个高分攻击会插在两次部署中间,既不按优先级也把 CP 花得七零八落)。每层自己挑最优解,上一层做不动了(没钱/没牌/没人能动)才轮到下一层。层与层之间**不互相压低分数**。
  - **行动层不卡阈值**。以前是"分数低于 `actionThreshold` 就不做、直接结束回合",这正是"有费、有单位能行动,AI 却什么都不做"的原因 —— 一次磨血攻击的分值很容易低于阈值,于是被整轮滤掉。现在回合内只要**合法且付得起**就做,分数只决定先后。`actionThreshold` 只留给 `PeekBestAction()` / 自检用。
  - **任何一次攻击都是正分**。攻击排在"空过"前面,所以"打不动"(被重甲吃光还挨反击)也给 +1 垫底而不是 −50,抢血姿态的减半也有 `Mathf.Max(1, …)` 兜底。拦住某次攻击靠的是 `CanAttack` 判不合法,不是给负分。
  - **不许用攻击去压低部署**。曾经有"场上有能打的攻击 → 出牌 −150",那和"部署优先"是反的,已删。真正要解决的是"部署把 CP 吃光、轮到行动时没费可用",这由分层顺序解决(部署层做不动了才轮到移动层、攻击层)。
  - **移动分要算上「这一步能不能把打不到变成打得到」**(`BreakthroughGain`)—— 否则 AI 会在中军堆满兵牌却一步不往前挪。
  - **袭扰的进退不许白嫖**。袭扰的代价(每回合 ATK+1、自损 2)要到**下一个自己的回合**才结算,所以"刚冲进敌方中军的那个回合不许撤回"—— `FieldUnit.RaidStartedRound` 记下进入的回合,`BattleRules.CanMoveTo` 比对 `TurnController.RoundNumber`。没有这一条,骑兵可以进去一趟、下回合立刻撤回,一点代价都不付。
  - **走位不许把出手机会扔掉**。`ScoreMove` 里比较"原地能打多少"和"挪过去能打多少":原地就有好机会、挪过去反而变差(差 20 分以上)时,扣掉原地那次攻击的分。骑兵的"有仗打时进敌方中军 −20"只是这条的一个特例 —— 骑兵射程 2,从前军能打,进了敌方中军反而打不着。
  - **往回缩要重罚**。前军是抢来的(§2.3 只容一方),站上去就是占住了进攻的桥头;主动退下来基本是白让节奏,前军里还有玩家单位时更是把阵地直接送出去。所以「前军→中军」和「中军→后军」各 −60(前军里还有敌人再 −60),只留防守姿态这一个例外。
  - **`BreakthroughGain` 也算"够不够得到大营"**。近战射程只有 1 排,从敌方中军是够不到玩家大营的,**必须先站上前军**;所以只要前军这个位置能威胁大营,这一步的分数就给足,AI 才会往上顶。
  - `PeekBestAction()` 可以只看不做地问一句"AI 现在想干什么"(自检和调参用)。
- **横幡/结算**:敌方打出策略卡时 `BattleMessageUI` 显示那张卡 3 秒;大营被打空或投降 → `BattleResultUI` 弹出战报(再来一局 / 返回主菜单)。
- **自检**:进 Play 按 `F9`,`BattleSelfTest` 会把开局布置、部署、移动、攻击反击、攻击扣费与缺费回滚、同排不扣费、建筑受击与血量角标、AI 是否会进攻、疲劳、胜负判定在真场景里跑一遍并逐条打日志。

## 还没做(已知缺口)

单位与结算:多目标战术卡的**指定目标 UI**(决水灌城/盐铁论的第 2、3 个目标目前由结算层自动挑)、支援类部署增益的手动选目标(目前自动挑)、60 秒回合超时、单位移动/攻击的动画与特效。
对手:AI 只做简单难度(普通难度的"预判下回合斩杀线"未实现);AI 不会主动把策略卡留给特定目标。
界面:设置面板、投降的二次确认弹窗、MainMenu 还没登记进 Build Settings(结算界面的「返回主菜单」会失败并飘字提示)。
工程:单元测试 —— 项目脚本全在预定义程序集 `Assembly-CSharp`,而 Unity Test Framework 要求测试放在 asmdef 里,
      两者互相引用不到;要接 NUnit 得先把脚本搬进 asmdef(会动到所有脚本的程序集归属),暂时用 `BattleSelfTest` 代替。
      另外 `Assembly-CSharp.csproj` 的文件清单是手写的,新增 `.cs` 之后要把它补进 `<Compile Include>`(或让 Unity 重新生成)。

---

## 约定

- C# 不使用 namespace,一个文件一个类型,注释全中文,需要引号时用「」。
- `.cs` 与 Unity 的 YAML 资源都用 LF 换行,见 `.gitattributes`。
- **移动 Unity 资源时必须连 `.meta` 一起移动** —— 场景/预制体是按 guid 引用资源的,只搬文件会让引用失效。
- 场景和预制体是 YAML 文本,可以用文本方式改,但改完要提醒在 Unity 里 Reload 再保存(否则会被编辑器里那份内存状态覆盖)。
- 数值以 `Docs/千秋策策划案.md` + `Docs/数据表demo.xlsx` 为准;卡牌 `.asset` 由 `Tools/build_card_assets.py` 从 xlsx 生成,不要手改单张卡。
