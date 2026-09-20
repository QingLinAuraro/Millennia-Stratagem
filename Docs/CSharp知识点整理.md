# 千秋策 · C# 知识点整理

> 按项目里**实际用到**的语法来整理，每条都给出真实代码出处（`文件:行号`），可以直接跳过去对着看。
> 语言版本：**C# 9.0 / netstandard2.1**（`Assembly-CSharp.csproj` 里的 `LangVersion`，由 Unity 2022.3 决定）。

## 目录

- [0. 语言版本基线](#0-语言版本基线)
- [1. 类型系统与基础语法](#1-类型系统与基础语法)
- [2. 类的成员写法](#2-类的成员写法)
- [3. 面向对象：封装 / 继承 / 多态](#3-面向对象封装--继承--多态)
- [4. 集合与索引器](#4-集合与索引器)
- [5. 字符串处理](#5-字符串处理)
- [6. 委托与事件](#6-委托与事件)
- [7. 泛型](#7-泛型)
- [8. 特性与反射](#8-特性与反射)
- [9. 迭代器与协程](#9-迭代器与协程)
- [10. Unity 场景下的 C# 用法](#10-unity-场景下的-c#-用法)
- [11. 项目**没有**用的语言特性](#11-项目没有用的语言特性)
- [12. 速查表](#12-速查表)

---

## 0. 语言版本基线

| 项 | 值 | 说明 |
|---|---|---|
| `LangVersion` | `9.0` | C# 9 及以前的语法都能用 |
| `TargetFramework` | `netstandard2.1` | Unity 的脚本程序集目标 |
| 可空引用类型 | 未开启 | 所以 `string?` 这类**引用类型**注解不可用；`Color?` 这种**值类型**可空是另一回事，见 §1.6 |
| 安全代码 | `AllowUnsafeBlocks = False` | 不能写 `unsafe` / 指针 |

**能用**：`var`、目标类型 `new()`、模式匹配、`switch` 表达式、`??=`、`?.`、`out var`、表达式体成员、`record`、`init`、局部函数。
**不能用**：文件作用域命名空间（`namespace X;`）、全局 `using`、`required`、`record struct`、字符串插值里的换行 —— 这些是 C# 10/11 的。

---

## 1. 类型系统与基础语法

### 1.1 `var` —— 隐式类型局部变量

编译器按右边**推断**类型，仍然是强类型，不是"万能变量"。

```csharp
// Core/Events/EventManger.cs:26
if (eventDict.TryGetValue(type, out var d))
    eventDict[type] = Delegate.Combine(d, listener);
```

项目里的习惯：**只在类型一眼可见时用 `var`**。右边是 `new XXX()` 或泛型方法返回时用，像 `var current = (int)field.GetValue(turn);` 这种带强制转换的也会用（`Core/Battle/BattleSelfTest.cs:860`）。

### 1.2 `const` 与 `static readonly` 的区别

```csharp
// Core/Models/PlayerState.cs —— 编译期常量
public const int CpGrowthCap = 12;
public const int CpMaxLimit = 24;

// Core/Events/EventManger.cs:21 —— 运行期只读
private static readonly Dictionary<GameEventType, Delegate> eventDict = new();
```

| | `const` | `static readonly` |
|---|---|---|
| 求值时机 | 编译期，值直接写进调用方 | 运行期，静态构造时 |
| 能放什么 | 只有基元类型 / `string` / `null` | 任意类型，可以 `new` 一个集合 |
| `const` 的坑 | 值被复制到调用方，改了要**全部重新编译** | 没这个问题 |

所以集合一律 `static readonly`，数字常量才 `const`。

### 1.3 `enum` —— 枚举

```csharp
// Core/Battle/TurnController.cs:44
public enum Side { Local, Enemy, Random }
```

不写数字时从 `0` 开始递增：`Local=0, Enemy=1, Random=2`。

> ⚠️ **实战踩过的坑**：Unity 场景文件里存的是**数字**（`Battle.unity` 里 `firstSide: 0`），而且**场景值会覆盖脚本里的默认值**。所以把默认值改成 `Side.Random` 之后，场景还是按 `0`（Local）跑 —— 必须同时改场景。

枚举值可以配合"位标志"做组合（项目暂未使用）：

```csharp
[Flags] public enum Layers { None = 0, Front = 1, Mid = 2, Back = 4 }
```

### 1.4 值类型 vs 引用类型

```csharp
// UI/Cards/FanLayout.cs:139 —— struct：值类型，赋值时整体复制
private struct CardHome
{
    public RectTransform Card;
    public Vector2 Position;
    public float Rotation;
}
```

- `struct`（值类型）：赋值 / 传参是**复制**。适合只有几个字段的小数据，能避免 GC。
- `class`（引用类型）：传的是**引用**，改一个变量会影响所有指向它的地方。
- 项目里 `CardHome` 只用来记一张手的原位（卡 + 位置 + 角度），存进 `List<CardHome>` 里，用 `struct` 正好。

### 1.5 元组（Tuple）

项目**没用** `System.Tuple` / `ValueTuple`，而是自己声明 `struct` 来表达"一条 AI 决策"：

```csharp
// Core/AI/EnemyAI.cs:75
public struct AIDecision
{
    public string reason;
    public int score;
    // ...
}
```

C# 7 之后的元组写法（项目未用，但很有用）：

```csharp
(int min, int max) Range() => (2, 5);
var (lo, hi) = Range();        // 解构
```

> 项目里刻意用 `struct` 而不是 `ValueTuple`，是因为 `ValueTuple` 在旧版 Unity 里要额外引 `System.ValueTuple`，而且命名 `struct` 读起来更清楚。

### 1.6 可空值类型 `?`

```csharp
// UI/Common/RuntimeText.cs:17 —— Color 是 struct，加 ? 才能传 null
public static TMP_Text Create(..., Color? color = null)
```

- `Color?` 是可空值类型（`Nullable<Color>`），只能用在 **struct** 上。
- 引用类型本来就能是 `null`，**不需要**也**不能**加 `?`（那是可空引用类型注解，本项目没开）。
- 判断和取值：`if (color.HasValue) ... color.Value ...`，或 `color ?? Color.white`（见下一节）。

### 1.7 三元运算符 `?:`

```csharp
// UI/Cards/CardDisplay.cs:236
square.color = RarityColors.TryGetValue(rarity, out var color) ? color : Color.white;

// Core/Battle/BattleRules.cs:596
public static string SideName(BattleSide side) => side == BattleSide.Player ? "我方" : "敌方";

// Core/Battle/TurnController.cs:111 —— 也可以嵌在表达式体属性里
public int CurrentSideTurnNumber => currentSide == Side.Local ? localTurns : enemyTurns;
```

注意三元的两边**类型要兼容**，否则编译器会去猜公共类型，容易出意外。

### 1.8 `switch` 语句

传统写法，项目里用在分支较多且每条要做不同事情的地方：

```csharp
// Core/Data/CardEffect.cs:131
public string Describe()
{
    switch (kind)
    {
        case CardEffectKind.Damage:
            return $"{ScopeName(scope)}造成 {amount} 点伤害";

        case CardEffectKind.Buff:
        {
            string span = durationRounds > 0 ? $"本回合" : "永久";
            return $"{ScopeName(scope)}{span} ATK{...}";
        }

        case CardEffectKind.RaiseCpMax:
            return amount >= 0 ? $"费用上限 +{amount}" : $"费用上限 {amount}";

        default:
            return "（无效果）";
    }
}
```

两个细节：
- `case` 里要**声明变量**时必须加一对 `{ }`（上面 `Buff` 那支），否则报"不能在 switch 段里声明同名变量"。
- 每个分支都以 `return` 结束，所以不需要 `break`（C# 不允许"贯穿到下一个 case"，漏写 `break` 且不返回会直接编译错误）。

### 1.9 `switch` 表达式（C# 8）

比 `switch` 语句更简洁，本身就是"求值得到一个结果"：

```csharp
// Core/Data/CardEffect.cs:155
private static string ScopeName(CardEffectScope scope) => scope switch
{
    CardEffectScope.Random   => "敌方随机单位",
    CardEffectScope.AllEnemy => "敌方全体",
    CardEffectScope.AllAlly  => "己方全体",
    CardEffectScope.Row      => "指定排",
    CardEffectScope.Caster   => "出牌方",
    CardEffectScope.Auto     => "自动挑选的",
    _                        => "目标",        // _ 是"其它全部"，必须放在最后
};
```

同样的写法在 `Core/AI/EnemyAI.cs:398`（`StateName`）、`:719`（`RowName`）、`Core/Battle/BattleRules.cs:581`（`TargetTypeName`）都有。

**要点**：`_` 兜底分支几乎总要写 —— 少了它，编译器要求你覆盖所有枚举值，新加一个枚举成员就会编译报错。这里是有意留的容错。

### 1.10 模式匹配

**类型模式 + 声明变量**（`is` 顺带完成转换，不用再强转）：

```csharp
// UI/Cards/FanLayout.cs:420
if (transform.GetChild(i) is RectTransform rt) cards.Add(rt);

// UI/Cards/EnemyHandUI.cs:289
if (handRoot is RectTransform area) area.anchorMin = area.anchorMax = new Vector2(0.5f, 1f);

// UI/Cards/CardHoverPreview.cs:222
baseSize = cardPrefab.transform is RectTransform prt ? prt.rect.size : baseSize;
```

**`is null` / `is not null`** —— 比 `== null` 更安全，因为**不会被重载的 `==` 运算符影响**：

```csharp
if (value == null) return string.Empty;      // Common/EnumExtensions.cs:13
```

**`out var` 声明模式** —— 判断成功的同时把结果取出来：

```csharp
// Core/Battle/BattleRules.cs:361
if (IsGuarded(target, out var guardian))

// Core/Events/EventManger.cs:26 同类写法
if (eventDict.TryGetValue(type, out var d))

// Core/Battle/BattlefieldManager.cs:772 —— 只要其中一个
if (!DeployUnit(card, row, slot, out var deployed, out message))
```

不关心某个返回值时用 `out _` 丢弃（`Core/Battle/BattleRules.cs:430`：`out _, out var forwardType`）。

### 1.11 `as` 与强制转换的区别

```csharp
// UI/Battle/BattleMessageUI.cs:243 —— as：失败给 null，不抛异常（引用类型专用）
if (go != null) messageRoot = go.transform as RectTransform;

// UI/Battle/BattleResultUI.cs:110 —— 强转：失败抛 InvalidCastException
var rootRect = (RectTransform)root.transform;

// Common/EnumExtensions.cs:23 —— as 的经典用法：拿到特性，没有就是 null
DescriptionAttribute attribute =
    Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute)) as DescriptionAttribute;
```

| | `as` | `(T)` |
|---|---|---|
| 失败结果 | `null` | 抛异常 |
| 适用 | 引用类型 / 可空值类型 | 所有类型 |
| 什么时候用 | "可能是，也可能不是" | "我确定它就是" |

### 1.12 可空运算符 `?.`、`??`、`??=`

**`?.` 空条件访问** —— 左边是空就整个表达式是空，不会抛 `NullReferenceException`：

```csharp
// Core/Battle/BattlefieldManager.cs:1165
return (EnemyDeckController.Instance?.DeckCount ?? 1) <= 0;

// Core/Events/EventManger.cs:42 —— 事件也可以直接 ?.Invoke，省掉判空
(d as Action<T>)?.Invoke(args);

// Common/EnumExtensions.cs:25
return attribute?.Description ?? name;
```

**`??` 空合并** —— 左边是空就取右边：

```csharp
// Core/Data/CardEffectDatabase.cs:38
if (table.TryGetValue(card.cardId ?? "", out var set)) return set;
```

**`??=` 空合并赋值（C# 8）** —— 左边是空才赋值：

```csharp
// Core/AI/EnemyAI.cs:656
BattleRules.CanAttack(unit, target, out string why);
why ??= "未知";
```

这一组是项目里最常用的语法糖，**`?.` + `??` 连用**基本替代了所有 `if (x != null)` 判空。

### 1.13 `out` / `ref` 参数

`out` 用于"函数返回多个值"，方法内**必须**给它赋值：

```csharp
// Core/Battle/BattlefieldManager.cs:1136
private bool SpendAction(FieldUnit unit, out string message)
{
    message = null;              // 所有 return 路径之前必须赋值
    if (unit == null) { message = "没有可行动的单位"; return false; }
    // ...
}
```

调用方用 `out var` 接（见 §1.10）。项目里没用 `ref`。

---

## 2. 类的成员写法

### 2.1 字段与命名约定

```csharp
// Core/Models/PlayerState.cs
private int cp;
public int cpMax => ...;

// Core/Battle/TurnController.cs
private Side currentSide = Side.Local;
private int roundNumber;        // 轮次:双方各走一次算一轮
```

项目约定：
- **`[SerializeField] private`** 给 Inspector 看的字段用小驼峰（`cardPrefab`、`startingCpMax`）。
- **`public` 对外暴露的**用大驼峰（`Cp`、`CpMax`）。
- 私有字段没有 `_` 前缀，直接小驼峰 —— 靠访问修饰符区分。

### 2.2 属性（Property）

```csharp
// Core/Battle/TurnController.cs:103-111
public Side CurrentSide => currentSide;
public bool IsLocalTurn => currentSide == Side.Local;
public int RoundNumber => roundNumber;
public int LocalTurnNumber => localTurns;

// UI/Cards/FanLayout.cs:146
public int Count => cards.Count;
public IReadOnlyList<RectTransform> Cards => cards;

// UI/Cards/CardHoverPreview.cs:82 —— 自动属性 + 私有 setter
public static CardHoverPreview Instance { get; private set; }
```

三种形态：

| 写法 | 意思 |
|---|---|
| `public int X => expr;` | 只读，每次访问都**重新计算**，不占存储 |
| `public int X { get; private set; }` | 自动属性，外部只读、内部可写 |
| `public int X { get; protected set; }` | 派生类也能写（`Core/Events/EventManger.cs:15`） |

> `Instance { get; private set; }` 是单例的简洁写法，比手写 `get { return instance; }` 短。

**带判空的表达式体属性**（很常见的防御式写法）：

```csharp
// UI/Battle/CommandPointController.cs:129
public int Cp => localPlayer != null ? localPlayer.cp : 0;
public int CpMax => localPlayer != null ? localPlayer.cpMax : 0;
```

### 2.3 表达式体成员（`=>`）

方法 / 属性 / 运算符只要**一条表达式**就能写完时，用 `=>` 省掉 `{ return ...; }`：

```csharp
// Core/Battle/BattleRules.cs:163
public static int RangeOf(UnitType type)
    => type == UnitType.Archer || type == UnitType.Support ? 2 : 1;

// Core/Battle/BattleRules.cs:596
public static string SideName(BattleSide side) => side == BattleSide.Player ? "我方" : "敌方";

// UI/Cards/CardDragPlay.cs:109 —— 只为了复用签名，转发给另一个方法
private void OnCommandPointsChanged(int cp, int cpMax) => RefreshPlayable();

// UI/Cards/FanLayout.cs:150
public float CardScale => Mathf.Max(0.01f, cardScale);
```

**方法的表达式体**和**属性**写法一样，区别只在于有没有参数。项目里还有个**多行表达式体**：

```csharp
// Core/Battle/BattlefieldManager.cs:308
private static List<BuildingSpec> DefaultBuildings() => new()
{
    new BuildingSpec { displayName = "大营",   side = BattleSide.Player, row = BattleRowType.Mid,  hp = 20 },
    new BuildingSpec { displayName = "军械库", side = BattleSide.Player, row = BattleRowType.Back, hp = 5 },
    // ...
};
```

### 2.4 静态成员与静态类

```csharp
// Core/Events/EventManger.cs:18 —— static class：不能实例化，只能放静态成员
public static class EventManager
{
    private static readonly Dictionary<GameEventType, Delegate> eventDict = new();
    public static void Subscribe<T>(...) { }
}
```

```csharp
// Common/EnumExtensions.cs:9
public static class EnumExtensions { ... }

// Core/GameBootstrap.cs —— 静态类 + 静态构造入口
public static class GameBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallBattleComponents() { ... }
}
```

项目里 `static` 用得**很克制**，只有两类：
1. 纯工具 / 全局注册表（`EventManager`、`EnumExtensions`、`BattleRules`、`CardEffectDatabase`）。
2. 单例的 `Instance` 属性。

因为静态状态**跨场景不会自动清**（比如 `BattleSettlement.CurrentRound`），退出战斗前要手动重置（`Core/Battle/BattleSettlement.cs:76`）。

### 2.5 只读与不可变

```csharp
// Core/Data/CardEffect.cs:234 —— public readonly：构造后不能换 List，但能往里加
public readonly List<CardEffect> effects = new();

// UI/Cards/HandUI.cs:58 —— readonly List：引用不可变，内容可变
private readonly List<CardDisplay> spawned = new();
```

注意 `readonly` 只锁**引用**，不锁内容：`spawned.Add(x)` 合法，`spawned = new()` 编译错误。

真正的"内容也不可变"要用 `IReadOnlyList<T>`：

```csharp
// UI/Cards/FanLayout.cs:147 —— 对外只给读接口，内部 List 照样改
private readonly List<RectTransform> cards = new();
public IReadOnlyList<RectTransform> Cards => cards;
```

这是**封装集合的正确姿势**：外面拿不到 `List`，就没法偷偷 `Add`。

---

## 3. 面向对象：封装 / 继承 / 多态

### 3.1 抽象类

```csharp
// Core/Events/EventManger.cs:13
public abstract class GameEventArgs
{
    public GameEventType EventType { get; protected set; }
}
```

- `abstract class` **不能** `new`，只能被继承。
- 它同时是泛型约束的锚点（§7.1）：`where T : GameEventArgs`。

### 3.2 访问修饰符

| 修饰符 | 谁能访问 | 项目里的例子 |
|---|---|---|
| `public` | 所有人 | `public int Cp => ...` |
| `private` | 只有本类 | `private Side currentSide` |
| `protected` | 本类 + 派生类 | `EventType { get; protected set; }` |
| `internal` | 同一程序集 | 项目没用（全在一个 `Assembly-CSharp` 里，没意义） |

默认值：类成员默认 `private`，类本身默认 `internal`。项目里**一律显式写出来**，不靠默认。

### 3.3 多态与 `virtual` / `override`

项目**几乎不用继承**，只在事件参数上开了一个抽象基类。原因是 Unity 的 `MonoBehaviour` 已经占掉了唯一的继承位（C# 单继承），策划案里的"单位/建筑/卡牌"差异都用**组合 + 枚举字段**表达，而不是继承树：

```csharp
// Core/Battle/FieldUnit.cs:146 —— 用 IsUnit 布尔 + 属性来表达"是兵还是建筑"
public int DamageReduction => IsUnit ? HeavyArmor : 0;
```

这是**有意为之**的取舍：组合比继承灵活，而且不会被 Unity 的序列化规则绊住。

### 3.4 构造函数与对象初始化器

```csharp
// Core/Data/CardEffect.cs:116-127 —— 对象初始化器
return new CardEffect
{
    kind = kind,
    amount = amount,
    scope = scope,
    summonCount = summonCount,
    durationRounds = durationRounds,
};
```

```csharp
// Core/Battle/BattlefieldManager.cs:309 —— 集合初始化器（注意结尾的逗号是合法的）
new BuildingSpec { displayName = "大营", side = BattleSide.Player, row = BattleRowType.Mid, hp = 20 },
```

**语义化工厂方法 + 私有构造**（项目里的核心手法，`CardEffect.cs:166` 起）：

```csharp
public static CardEffect Damage(int amount, CardEffectScope scope = CardEffectScope.Target,
                                CardEffectTargetSlot slot = CardEffectTargetSlot.Primary)
    => Create(CardEffectKind.Damage, amount, scope, slot);

public static CardEffect RefundCp(int amount)
    => Create(CardEffectKind.RefundCp, amount, CardEffectScope.Caster);
```

好处：登记卡牌效果时一行一张，**不用到处填 `kind` / `scope` / `slot` 这些字段**，写错了也编译不过。这是"用命名方法替代一堆参数"的典型做法。

### 3.5 默认参数与命名参数

```csharp
// Core/Models/PlayerState.cs
public void BeginTurn(int round, int growth = 1)

// Core/Data/CardEffect.cs:173 —— 多个默认参数
public static CardEffect Buff(int atk, int hp, CardEffectScope scope = CardEffectScope.Target,
                              CardEffectTargetSlot slot = CardEffectTargetSlot.Primary, int rounds = 0)
```

调用时**跳过中间参数**要用命名参数：

```csharp
CardEffect.Buff(2, 0, rounds: 1);      // 只给 scope/slot 用默认值
```

> ⚠️ 默认参数的值在**调用方**被烧进 IL（跟 `const` 一样），改了默认值必须重新编译调用方。

---

## 4. 集合与索引器

### 4.1 `List<T>`

```csharp
// Core/Models/Hand.cs:7
private readonly List<CardData> cards = new();

// Core/AI/EnemyAI.cs:643 —— 遍历
for (int i = 0; i < mine.Count; i++)
{
    var unit = mine[i];
    if (unit == null || !unit.IsUnit || !unit.IsAlive) continue;
    // ...
}
```

项目**统一用 `for` 而不是 `foreach`** 遍历 `List`：`List<T>` 的 `foreach` 在旧版 Mono 上会分配枚举器（GC 压力），而本项目是每帧跑的战斗逻辑。

### 4.2 `Dictionary<K, V>`

```csharp
// Core/Events/EventManger.cs:21
private static readonly Dictionary<GameEventType, Delegate> eventDict = new();

if (eventDict.TryGetValue(type, out var d))
    eventDict[type] = Delegate.Combine(d, listener);
else
    eventDict[type] = listener;
```

**`TryGetValue` 是标准姿势**：一次查找同时拿到"有没有"和"是什么"，比 `ContainsKey` + 索引器查两遍快。

```csharp
// UI/Cards/CardDisplay.cs:236 —— 字典的典型一行写法
square.color = RarityColors.TryGetValue(rarity, out var color) ? color : Color.white;
```

其它用到字典的地方：`BattlefieldManager.cs:182`（`Dictionary<BattleRow, Image>`）、`Core/Data/CardEffectDatabase.cs:30`。

### 4.3 `HashSet<T>` —— 只关心"在不在"

```csharp
// Core/Battle/BattlefieldManager.cs:174
private readonly HashSet<BattleRow> baseValidRows = new();

// UI/Cards/FanLayout.cs:131
private readonly HashSet<RectTransform> dimmedCards = new();
```

查找是 O(1)，而且**自动去重**。需要"这个物体是不是已经在集合里"时用它，别用 `List.Contains`（O(n)）。

### 4.4 索引器

```csharp
// Core/Battle/BattleRow.cs —— 让对象像数组一样用
public FieldUnit this[int index] => ...;
```

用 `row[0]` 代替 `row.GetUnit(0)` 更贴近"排里第几个单位"的直觉。`BattleRow` 还配了 `IndexOf`：

```csharp
// Core/Battle/BattleRow.cs:180
public int IndexOf(FieldUnit unit) => unit == null ? -1 : members.IndexOf(unit);
```

### 4.5 集合的封装（重要）

```csharp
// UI/Cards/FanLayout.cs:146
public int Count => cards.Count;
public IReadOnlyList<RectTransform> Cards => cards;
```

**不要**直接把 `List` 字段 `public` 出去。给 `IReadOnlyList<T>` 之后：
- 外面能 `for` 遍历、能 `.Count`；
- 但**不能** `Add` / `Clear` / 下标赋值 —— 集合只能由本类改。

---

## 5. 字符串处理

### 5.1 字符串插值 `$"..."`

项目里**最常用**的语法，几乎每句日志都用：

```csharp
// UI/Battle/CommandPointController.cs:231
Debug.Log($"[CP] 我方第 1 回合:CP 初始化为 {Cp}/{CpMax}(第 1 回合不走增长)");

// Core/Events/EventManger.cs:43
Debug.Log($"[EventManager] {args.EventType} triggered");
```

`{ }` 里可以是任意表达式；`{DateTime.Now:HH:mm}` 这种冒号后是**格式说明符**。

### 5.2 插值里的三元（注意括号）

```csharp
// Core/Data/CardEffect.cs:141
return $"{ScopeName(scope)}{span} ATK{(atkValue >= 0 ? "+" : "")}{atkValue}、HP{(hpValue >= 0 ? "+" : "")}{hpValue}";
```

三元运算符在插值里**必须加括号**：`{(x ? a : b)}`。不加会被 `:` 的格式说明符规则吃掉，直接编译错误。

### 5.3 `+` 拼接与性能

```csharp
// Core/Battle/BattlefieldManager.cs:1137
var sb = new System.Text.StringBuilder();
```

- 少量拼接用 `+` / 插值都行。
- **循环里**拼接不要用 `+`（每次生成新字符串 → GC），用 `StringBuilder`。项目在 `BattleSettlement.cs:410`、`BattlefieldManager.cs:1137` 都是这个原因。

### 5.4 其它常用方法

```csharp
why.Contains("守护")                        // Core/AI/EnemyAI.cs:658 —— 子串判断
(card.cardId ?? "")                         // 空字符串兜底
string.IsNullOrEmpty(costResult)            // 判空 + 判空串，比 == "" 安全
go.name.StartsWith("Hand")                  // 前缀判断
```

---

## 6. 委托与事件

这是项目里**最重要**的一块 —— 回合流转、CP 变化、手牌刷新全靠它。

### 6.1 `Action` / `Func` —— 内置委托

```csharp
// Core/Battle/TurnController.cs:101 —— Action<参数...>：无返回值
public event Action<bool, int> TurnStarted;

// UI/Battle/CommandPointController.cs:134
public event Action<int, int> CpChanged;

// UI/Battle/BattleHudButtons.cs:59 —— 无参数
public event Action SettingsClicked;
```

| 类型 | 签名 |
|---|---|
| `Action` | `void ()` |
| `Action<T1, T2>` | `void (T1, T2)` |
| `Func<T, TResult>` | `TResult (T)` |

项目一律用内置的 `Action` / `Func`，**不自定义 `delegate` 类型**，少一层绕。

### 6.2 `event` 关键字

```csharp
public event Action<bool, int> TurnStarted;
```

`event` 比裸的 `Action` 字段多了两条保护：
1. 外部**只能** `+=` / `-=`，不能 `=` 覆盖（`TurnStarted = null` 编译不过，防止把别人的订阅清掉）；
2. 外部**不能**直接 `Invoke`，只能由声明它的类触发。

### 6.3 订阅 / 退订必须成对

```csharp
// Core/AI/EnemyAI.cs:134
private void OnEnable() => HookTurn();
private void OnDisable() => UnhookTurn();

private void HookTurn()
{
    turnSource = TurnController.Instance;
    // ...
    turnSource.TurnStarted += OnTurnStarted;
    hooked = true;
}

private void UnhookTurn()
{
    if (!hooked) return;
    if (turnSource != null) turnSource.TurnStarted -= OnTurnStarted;
    turnSource = null;
    hooked = false;
}
```

**为什么重要**：委托会**持有目标对象的引用**。只订阅不退订，被销毁的对象不会被 GC 回收，而且下一局会**重复收到事件**。

项目里的标准模板：
- `OnEnable` 订阅、`OnDisable` 退订（`BattleResultUI.cs:66-68`、`HandUI.cs:88-93`、`CardDragPlay.cs:90-101`）；
- **加一个 `hooked` 布尔**防止重复订阅（`+=` 两次会**调用两次**，这是新手最常见的 bug）。

### 6.4 触发事件的写法

```csharp
// Core/Battle/TurnController.cs:217
TurnStarted?.Invoke(side == Side.Local, sideTurn);
```

`?.Invoke(...)` 是触发事件的**标准写法**：
- 等价于 `if (TurnStarted != null) TurnStarted(...)`；
- 但有微妙区别 —— `?.` 先读一次字段，多线程下更安全（虽然 Unity 主线程用不上）。

### 6.5 用 `Delegate` 做"万能事件表"

`EventManager` 是项目里最"高级"的一段 C#，值得单独看：

```csharp
// Core/Events/EventManger.cs
private static readonly Dictionary<GameEventType, Delegate> eventDict = new();

public static void Subscribe<T>(GameEventType type, Action<T> listener) where T : GameEventArgs
{
    if (eventDict.TryGetValue(type, out var d))
        eventDict[type] = Delegate.Combine(d, listener);    // 已有 → 追加
    else
        eventDict[type] = listener;                          // 还没有 → 新建
}

public static void Unsubscribe<T>(GameEventType type, Action<T> listener) where T : GameEventArgs
{
    if (eventDict.TryGetValue(type, out var d))
        eventDict[type] = Delegate.Remove(d, listener);
}

public static void Trigger<T>(T args) where T : GameEventArgs
{
    if (eventDict.TryGetValue(args.EventType, out var d))
        (d as Action<T>)?.Invoke(args);                      // 转回具体委托再调
}
```

知识点：
- **`Delegate` 是所有委托的基类**。存进 `Dictionary<,>` 只能声明成 `Delegate`，取出来要 `as Action<T>` 转回具体类型才能 `Invoke`。
- **`Delegate.Combine` / `Delegate.Remove`** 就是 `+=` / `-=` 的底层实现，手动调用是为了在**运行时**操作（`+=` 必须编译期知道是哪个字段）。
- **`Delegate.Remove` 移除最后一个订阅者后会变成 `null`**，所以 `Trigger` 里必须判空（用 `?.` 就顺带做了）。

这套设计让"一个字典装下所有事件类型"成为可能，代价是**失去了编译期类型检查**（`type` 和 `T` 对不上要运行期才发现）。

---

## 7. 泛型

### 7.1 泛型方法 + 约束

```csharp
// Core/Events/EventManger.cs:24
public static void Subscribe<T>(GameEventType type, Action<T> listener) where T : GameEventArgs
```

`where T : GameEventArgs` 是**约束**：保证 `T` 一定是 `GameEventArgs` 的派生类，所以方法体里能安全访问 `args.EventType`。

常见约束：

| 约束 | 含义 |
|---|---|
| `where T : class` | 必须是引用类型 |
| `where T : struct` | 必须是值类型 |
| `where T : new()` | 必须有无参构造（才能 `new T()`） |
| `where T : GameEventArgs` | 必须继承自它（或它本身） |
| `where T : IComparable<T>` | 必须实现某接口 |

### 7.2 泛型集合与 `GetComponent<T>`

```csharp
private readonly List<CardDisplay> spawned = new();
private readonly Dictionary<BattleRow, Image> insertMarkers = new();
private readonly HashSet<BattleRow> baseValidRows = new();

var tmp = go.GetComponent<TMP_Text>();          // Unity 的泛型方法
Object.FindObjectsOfType<TMP_Text>();
```

`GetComponent<T>()` 与 `GetComponent(typeof(T))` 是一回事，泛型版**不用强转**、稍微快点。

### 7.3 目标类型 `new()`（C# 9）

项目里到处都是，是 C# 9 最显眼的语法：

```csharp
// UI/Cards/HandUI.cs:58 —— 左边已经写了类型，右边不用再写
private readonly List<CardDisplay> spawned = new();

// Core/Events/EventManger.cs:21
private static readonly Dictionary<GameEventType, Delegate> eventDict = new();

// Core/Battle/BattlefieldManager.cs:308 —— 和方法返回值配合
private static List<BuildingSpec> DefaultBuildings() => new()
{
    new BuildingSpec { displayName = "大营", ... },
};
```

**`new()` 不是"动态类型"**，它只是让编译器从上下文推断类型，编译后和 `new List<CardDisplay>()` 完全一样。

> ⚠️ 老版本（C# 8 及以前）或别的项目里不能用，得写全 `new List<CardDisplay>()`。

### 7.4 泛型的坑：Unity 序列化

```csharp
[SerializeField] private List<BuildingSpec> buildings = new();   // ✅ Unity 能序列化具体类型
// [SerializeField] private T[] items;                            // ❌ 泛型字段 Unity 不序列化
```

Unity **不会序列化泛型类型参数**（`T`），只会序列化具体类型（`List<int>` 可以，`List<T>` 在泛型类里不行）。

---

## 8. 特性与反射

### 8.1 Unity 特性（用得最多）

```csharp
// UI/Battle/UnitActionController.cs:30, 51-71
[DisallowMultipleComponent]                     // 一个物体上只许挂一个
[RequireComponent(typeof(FieldUnit))]           // 自动补上依赖的组件
public class UnitActionController : MonoBehaviour
{
    [Header("开关")]                             // Inspector 里的小标题
    [Tooltip("关掉 = 玩家不能移动/攻击单位")]       // 鼠标悬停提示
    [SerializeField] private bool allowActions = true;   // 私有字段但序列化(Inspector 可见)

    [Header("拖动")]
    [SerializeField] private float dragThreshold = 12f;
    [SerializeField] private Color dragMarkerColor = new(1f, 0.85f, 0.35f, 0.35f);
}
```

| 特性 | 作用 |
|---|---|
| `[SerializeField]` | 让 **private** 字段出现在 Inspector 里并被存档 |
| `[Header("…")]` | Inspector 分组标题 |
| `[Tooltip("…")]` | 悬停说明 |
| `[DisallowMultipleComponent]` | 禁止重复挂载 |
| `[RequireComponent(typeof(X))]` | 挂本组件时自动补 `X` |
| `[CreateAssetMenu]` | 让类能"右键 → Create"成 `.asset` |
| `[RuntimeInitializeOnLoadMethod]` | **不用挂场景**，加载时自动执行（`GameBootstrap` 靠它） |
| `[CustomPropertyDrawer]` | 自定义 Inspector 绘制（`Editor/LocalizedEnumDrawer.cs`） |

**`[SerializeField] private` 是 Unity 的核心习惯**：对外完全私有，但对 Inspector / 存档开放。

### 8.2 元数据特性 + 反射

```csharp
// Core/Data/CardEffect.cs —— 给枚举成员挂中文名
[Description("费用上限+N")] RaiseCpMax,
[Description("回复N点费用")] RefundCp,
```

```csharp
// Common/EnumExtensions.cs —— 运行时把中文名读出来
public static string GetDescription(this Enum value)
{
    Type type = value.GetType();
    string name = Enum.GetName(type, value);
    if (name == null) return string.Empty;

    FieldInfo field = type.GetField(name);          // 反射取字段
    if (field == null) return name;

    DescriptionAttribute attribute =
        Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute)) as DescriptionAttribute;

    return attribute?.Description ?? name;          // 有特性用特性，没有就用枚举名
}
```

知识点：
- **特性本质是元数据**，挂在类型/字段/方法上，运行时通过 `System.Reflection` 读。
- `[Description]` 来自 `System.ComponentModel`（`using System.ComponentModel;`）。
- 这套「枚举 + `[Description]` + 扩展方法」的写法，让**下拉框直接显示中文**（`Editor/LocalizedEnumDrawer.cs`）。

### 8.3 反射访问私有字段

自检脚本里为了推进回合数，直接改了 `private` 字段：

```csharp
// Core/Battle/BattleSelfTest.cs:856
var field = typeof(TurnController).GetField("roundNumber",
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
if (field == null) return;

int current = (int)field.GetValue(turn);
field.SetValue(turn, current + 1);
```

- 私有成员默认**找不到**，必须显式给 `BindingFlags.NonPublic | BindingFlags.Instance`。
- 用途：**测试代码绕过封装**。产品代码里这么写要非常谨慎（编译期查不出问题，字段改名就静默失效 —— 所以这里 `field == null` 时直接 `return`）。

### 8.4 `nameof` 与 `typeof`

```csharp
typeof(TurnController)              // 取 Type 对象（编译期检查类型名）
typeof(DescriptionAttribute)
nameof(roundNumber)                 // 取字符串 "roundNumber"（不用手写字符串）
```

`nameof` 的好处：**重命名时跟着改**，写错直接编译报错。反射里给 `GetField(nameof(x))` 传参比手写字符串安全得多（项目里这处是历史写法，可以改进）。

### 8.5 扩展方法

```csharp
// Common/EnumExtensions.cs:11 —— 第一个参数带 this，就能像实例方法一样调
public static string GetDescription(this Enum value)

// 用法：
someEnum.GetDescription();
```

规则：
1. 必须在 **`static class`** 里；
2. 必须是 **`static` 方法**；
3. 第一个参数带 **`this`**，表示"给谁扩展"。

价值：**不给原类型加代码就能加方法**。`Enum` 是 BCL 类型改不了，靠扩展方法加上 `GetDescription()`。项目里另一个例子是 `UI/Cards/FanLayout.cs` 附近的若干工具方法。

---

## 9. 迭代器与协程

### 9.1 `IEnumerable<T>` 与 `yield return`

```csharp
// Core/AI/EnemyAI.cs:744 —— 遍历所有可攻击目标
private static IEnumerable<FieldUnit> AllAttackTargets()
{
    // ...
    yield return someUnit;
    // ...
}

// 调用方
foreach (var target in AllAttackTargets())
{
    if (target == null || !target.IsAlive) continue;
    // ...
}
```

`yield return` 让方法**变成状态机**：每次 `foreach` 取下一个值，方法从上次停的地方继续。**不会一次性生成整个列表**。

### 9.2 Unity 协程（把 `IEnumerator` 交给引擎）

```csharp
// Core/Battle/TurnController.cs:229 —— 等几秒
private IEnumerator AutoEndEnemyTurn()
{
    yield return new WaitForSeconds(enemyTurnSeconds);
    // 时间到了，结束回合
}

// 启动 / 停止
autoEndRoutine = StartCoroutine(AutoEndEnemyTurn());
StopCoroutine(autoEndRoutine);
```

```csharp
// UI/Battle/CommandPointController.cs:405 —— 等一帧，让所有 Start() 跑完
private IEnumerator DrawOpeningHandNextFrame()
{
    yield return null;      // 等所有 Start() 跑完(牌堆在 DeckController.Start() 里初始化)
    DrawOpeningHand();
}
```

```csharp
// Core/AI/EnemyAI.cs:212, 219 —— 提前结束
if (BattleSettlement.MatchOver) yield break;
yield return new WaitForSeconds(thinkDelay);
```

**`yield return` 的几种常用值**：

| 写法 | 含义 |
|---|---|
| `yield return null` | 等**下一帧** |
| `yield return new WaitForSeconds(t)` | 等 `t` 秒（受 `Time.timeScale` 影响） |
| `yield return new WaitForSecondsRealtime(t)` | 等 `t` 秒（不受缩放影响） |
| `yield return new WaitUntil(() => cond)` | 等到条件成立 |
| `yield break;` | **提前结束**整个协程（相当于 `return`） |

**关键点**：
- 协程的返回类型必须是 `IEnumerator`，用 `StartCoroutine` 启动。
- **`StartCoroutine` 只能由 `MonoBehaviour` 调用**，而且**物体/组件被禁用时会一起停**。
- 协程**不是线程**，全部跑在主线程，操作 UI 是安全的。
- **`StopCoroutine` 要传"启动时那个引用"**，所以项目里用字段存起来（`TurnController.cs:96` 的 `autoEndRoutine`），否则会重复启动、抢着过回合。

> 项目里踩过的坑（`TurnController.cs:230` 附近注释）：敌方 AI 接管回合时会调 `SetAutoEndEnemyTurn(false)` 把定时器关掉，否则**定时器和 AI 会抢着结束回合**。

---

## 10. Unity 场景下的 C# 用法

### 10.1 生命周期方法

项目里出现的全部生命周期方法及其顺序：

```csharp
Awake()      // 最早的初始化：自己内部赋值、拿单例（Core/Battle/TurnController.cs:114）
OnEnable()   // 每次启用：订阅事件（Core/AI/EnemyAI.cs:134）
Start()      // 第一帧前：依赖别的物体已就绪的初始化（Core/UI/.../CommandPointController.cs:169）
Update()     // 每帧（Core/Battle/TurnController.cs:129，处理调试按键）
LateUpdate() // 每帧最后（UI/Battle/UnitActionController.cs:661，拖动标记跟随，避免抖动）
FixedUpdate()// 固定步长物理（项目未用，没有 Rigidbody 物理）
OnDisable()  // 每次禁用：退订事件（Core/AI/EnemyAI.cs:136）
OnDestroy()  // 销毁：清理静态引用（Core/Battle/TurnController.cs:119）
OnValidate() // 仅编辑器：Inspector 改值时校验/夹紧（UI/Cards/FanLayout.cs:172）
```

**顺序**：`Awake` → `OnEnable` → `Start` →（每帧）`Update` → `LateUpdate` → `OnDisable` → `OnDestroy`

**这也是"订阅在 `OnEnable` 而不是 `Awake`"的原因**：物体禁用期间不该收到事件，`OnEnable`/`OnDisable` 天然配对。

### 10.2 单例模式

项目里**每个管理器都是单例**，两种写法：

**A. 简单版**（`Core/AI/EnemyAI.cs:132`、`UI/Battle/BattleResultUI.cs:64`）

```csharp
private static EnemyAI instance;
public static EnemyAI Instance { get; private set; }

private void Awake() => instance = this;      // 或 Instance = this;
```

**B. 懒加载 + 自动创建版**（`Core/Battle/TurnController.cs:50`）

```csharp
public static TurnController Instance
{
    get
    {
        if (instance != null) return instance;              // 1. 有就直接给

        instance = FindObjectOfType<TurnController>();      // 2. 场景里找
        if (instance != null) return instance;

        var go = new GameObject("TurnController");          // 3. 都没有 → 自己建
        var canvas = CanvasUtil.FindRootCanvas();
        if (canvas != null) go.transform.SetParent(canvas.transform, false);
        instance = go.AddComponent<TurnController>();
        return instance;
    }
}
```

B 版的好处：**代码里随便用，不怕场景没挂**（`GameBootstrap` 就是靠这个自动补齐组件的）。代价是"用到才建"，所以第一次访问的时机要自己想清楚。

**静态引用必须在 `OnDestroy` 清掉**：

```csharp
private void OnDestroy()
{
    if (instance == this) instance = null;    // 切场景后旧的已被销毁，但静态字段还指着它
}
```

不清的话，下次进战斗 `instance != null` 判断会**误判为真**（Unity 重载了 `==`，已销毁的对象和 `null` 比较返回 `true`，但字段本身不是 `null`），拿到一个"看起来存在其实是尸体"的对象。

### 10.3 `MonoBehaviour` 与"不能 `new`"

```csharp
var go = new GameObject("TurnController");
instance = go.AddComponent<TurnController>();     // ✅ 唯一正确姿势
// var tc = new TurnController();                 // ❌ 编译能过，但 Unity 会报错
```

`MonoBehaviour` 派生类**必须挂到 `GameObject` 上**，由引擎负责构造和调用生命周期。直接 `new` 出来的对象没有任何引擎关联，`Start`/`Update` 都不会跑。

**纯 C# 类**（不继承 `MonoBehaviour`）反而应该用 `new`：

```csharp
// Core/Models/PlayerState.cs、Core/Battle/BattleRules.cs 等全是纯 C# 类
var p = new PlayerState(true);
```

项目的分层很清楚：
- `Core/Models`、`Core/Battle/BattleRules.cs`、`Core/Data` → **纯 C# 逻辑**，能脱离 Unity 单测；
- `UI/`、各种 `*Controller` → `MonoBehaviour`，负责和引擎/UI 打交道。

### 10.4 `Debug.Log` 家族

```csharp
Debug.Log($"[CP] 我方第 1 回合:CP 初始化为 {Cp}/{CpMax}");              // 普通日志
Debug.LogWarning("[EnemyDeckController] 找不到 TurnController...", this); // 警告
Debug.LogError(...);                                                      // 错误
```

项目习惯：
- 前缀 `[模块名]`，方便在 Console 里过滤；
- 第二个参数传 `this`，点日志能**高亮对应物体**；
- `Debug.Log` 在**发布版里仍然会执行**（字符串拼接照样发生），高频路径上要用开关保护 —— 项目里都是 `if (debugLog)` 包起来（`UI/Battle/UnitActionController.cs:71`）。

### 10.5 再看一次"场景值覆盖脚本默认值"

以调试键为例，`Battle.unity` 里实际存的值（**不是脚本默认值**）：

| 字段 | 脚本默认 | 场景里存的值 |
|---|---|---|
| `debugRaiderToggleKey` | `KeyCode.F9` | `110`（N） |
| `debugEndTurnKey` | `KeyCode.T`（116） | `116`（T） |
| `debugRaiseCpMaxKey` | `KeyCode.Equals`（61） | `61`（=） |
| `debugPlayKey` | `KeyCode.M`（109） | `109`（M） |
| `debugDrawKey` | `KeyCode.D`（100） | `100`（D） |

所以 `BattlefieldManager.cs:1475` 那个 Tooltip 说"默认 F9"是**过时的**（场景早改成 N 了）；而 `BattleSelfTest` 场景里没挂、由 `GameBootstrap` 运行时补，用的才是脚本默认值 `F9` —— **两者不冲突，正是靠场景覆盖做到的**。

这正是 §12.2 里那条坑的实例：**改 `[SerializeField]` 的默认值，场景里已存的旧值不会跟着变**。

---

## 11. 项目**没有**用的语言特性

这一节同样重要 —— 知道"为什么不用"比"会用"更能说明问题。

### 11.1 LINQ（`using System.Linq`）—— 全项目零使用

```csharp
// ❌ 项目里没有这种写法
var alive = units.Where(u => u.IsAlive).OrderBy(u => u.Hp).ToList();

// ✅ 项目里的写法
var alive = new List<FieldUnit>();
for (int i = 0; i < units.Count; i++)
{
    if (units[i] != null && units[i].IsAlive) alive.Add(units[i]);
}
```

**为什么不用**：LINQ 的链式调用**每一步都会分配**（迭代器对象、闭包、临时集合）。战斗逻辑每帧都在跑（`BattleRules` 里的目标筛选、`EnemyAI` 的评分），在 Unity 里这是明确的 GC 压力来源。而且 IL2CPP 下 AOT 泛型实例化会增大包体和编译时间。

**代价**：代码更长、更啰嗦。这是**有意识的取舍**，不是不会写。

### 11.2 `async` / `await` —— 零使用

项目里所有"等一段时间"都用**协程**（§9.2），不用 `async Task`。

**为什么**：Unity 的 `async/await` 在 `MonoBehaviour` 被销毁后**不会自动停止**，`Task` 还会回到线程池线程上（此时访问 `transform` 会直接抛异常）；协程则随物体禁用/销毁自动停。回合制战斗里"等 2 秒再过回合"用协程语义更贴切。

### 11.3 元组简写、局部函数、`record`、`init`、模式匹配的进阶形态

C# 9 里这些东西**可用但项目没用**：`(int a, int b)` 元组简写、局部函数、`record`、`init` 访问器、`switch` 表达式里的属性模式 / 位置模式 / `when` 守卫。

**为什么**：项目风格偏**朴素直白**（全中文注释、`for` 循环、显式类型），可读性和团队协作优先。`record` / `init` 主要服务于不可变数据建模，而这个项目的"数据"几乎都是 Unity 序列化的 `[SerializeField]` 字段 + `.asset` 资源，用不上。

### 11.4 C# 10+ 语法（**编译器不支持**）

| 语法 | 版本 | 本项目 |
|---|---|---|
| 文件作用域命名空间 `namespace X;` | C# 10 | ❌ 编译不过 |
| 全局 `using` | C# 10 | ❌ 编译不过 |
| `required` 成员 | C# 11 | ❌ 编译不过 |
| `record struct` | C# 10 | ❌ 编译不过 |
| 插值字符串里的换行 | C# 11 | ❌ 编译不过 |

写的时候如果 IDE 提示"语言版本不支持"，原因就是 `LangVersion = 9.0` 这条线。

### 11.5 命名空间 —— 全项目**一个都没有**

项目所有脚本都写在**全局命名空间**里（`namespace` 关键字一次都没出现）。

好处：Unity 里拖引用、写调试脚本省事，不用 `using`。
代价：**类型名有冲突风险**。所以项目靠命名约定区分：

| 后缀 / 目录 | 类型 |
|---|---|
| `*Controller` | 管一个系统的 MonoBehaviour（`TurnController`、`CommandPointController`） |
| `*UI` | 纯表现层（`BattleResultUI`、`DeckUI`、`HandUI`） |
| `*Manager` | 全局单例（`BattlefieldManager`、`EventManager`） |
| `Battle*` | 战斗域的类型（`BattleRow`、`BattleRules`、`BattleSide`） |

> 如果将来要抽 DLL 或引入第三方库，**第一步就是补命名空间**，否则很容易撞名。

---

## 12. 速查表

### 12.1 常用语法糖对照

| 简写 | 等价于 |
|---|---|
| `x?.Y` | `x == null ? null : x.Y` |
| `x ?? y` | `x == null ? y : x` |
| `x ??= y` | `if (x == null) x = y;` |
| `a is T t` | `a is T` 且把 `a` 转成 `t` |
| `M(out var v)` | 声明 `v` 并作为 `out` 参数传入 |
| `$"{a}/{b}"` | `a.ToString() + "/" + b.ToString()` |
| `=> expr` | `{ return expr; }` |
| `new()` | `new 左边那个类型()` |
| `evt?.Invoke(x)` | `if (evt != null) evt(x);` |
| `T x = default;` | 值类型给零值、引用类型给 `null` |

### 12.2 项目里最容易写错的地方

| 坑 | 正确做法 |
|---|---|
| 事件只订不退 → 内存泄漏 + 重复触发 | `OnEnable` 订阅 / `OnDisable` 退订，加 `hooked` 布尔防重 |
| `MonoBehaviour` 用 `new` 创建 | `go.AddComponent<T>()` |
| 静态单例切场景后不清 | `OnDestroy` 里 `if (instance == this) instance = null;` |
| 循环里用 `+` 拼字符串 | `StringBuilder` |
| 循环里用 LINQ | `for` + 手动 `Add` |
| 插值里写三元不加括号 | `{(x ? a : b)}` |
| 泛型字段指望 Unity 序列化 | 换成具体类型 |
| 改了 `[SerializeField]` 默认值以为生效 | **场景里存的值会覆盖默认值**，得去 Inspector 改 |
| `switch` 表达式漏 `_` 分支 | 补 `_ => ...`，否则加枚举成员时编译报错 |
| 反射找私有字段没给 `BindingFlags.NonPublic` | 两个 flag 都要给，并判 `null` |

### 12.3 各知识点的代表文件

| 想复习 | 去看 |
|---|---|
| `switch` 表达式 / 表达式体 / 工厂方法 | `Core/Data/CardEffect.cs` |
| 委托 · 泛型 · `Delegate.Combine` | `Core/Events/EventManger.cs` |
| 特性 · 反射 · 扩展方法 | `Common/EnumExtensions.cs`、`Editor/LocalizedEnumDrawer.cs` |
| 协程 · 单例 · 事件订阅配对 | `Core/Battle/TurnController.cs`、`Core/AI/EnemyAI.cs` |
| 属性 · 封装 · CP 两层模型 | `Core/Models/PlayerState.cs` |
| 集合封装 · `IReadOnlyList` · `struct` | `UI/Cards/FanLayout.cs` |
| 纯 C# 逻辑层（可脱离 Unity 单测） | `Core/Battle/BattleRules.cs` |
| 反射访问私有字段 | `Core/Battle/BattleSelfTest.cs:856` |
| 不用命名空间的大项目怎么组织 | 全项目 `Assets/_Project/Scripts/` |

---

## 附：怎么验证某个语法确实能用

项目**没有**装 Unity Test Framework，验证方式是：

```powershell
# 在 千秋策/ 目录下编译整个脚本程序集
dotnet build Assembly-CSharp.csproj -v minimal -nologo
# 成功输出：0 个警告 / 0 个错误
```

> `Assembly-CSharp.csproj` 是 Unity 自动生成的（在 `.gitignore` 里），**不要提交**。它的 `LangVersion` 就是 Unity 允许你用的 C# 版本。

想验证**纯 C# 逻辑**（`Core/Models`、`Core/Battle/BattleRules.cs` 这类不碰 Unity 的代码），可以临时建一个 `dotnet` 控制台工程，用桩类替掉 `UnityEngine.Mathf` / `Debug`，就能脱离 Unity 直接跑 —— 这条路线在 CP 费用模型的验证里用过（`PlayerState` 只依赖 `Mathf.Clamp`，桩起来不到 15 行）。

进游戏后的运行时自检按 **F9**（`Core/Battle/BattleSelfTest.cs`）。
