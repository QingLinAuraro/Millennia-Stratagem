# Tests（暂时别往这里放 .cs）

Unity 的测试代码不能直接丢进 `Assets/` 下的普通目录:

- 测试要引用 `NUnit` / `UnityEngine.TestRunner`,而本工程当前**没有 asmdef**,所有 `.cs` 都会编进 `Assembly-CSharp.dll` —— 那个程序集引用不到 NUnit,一旦放入测试代码就会编译报错,连正常开发都受影响。

所以正确顺序是:

1. 在 `Assets/_Project/Scripts/` 下新建 `QianQiuCe.Tests.asmdef`:
   - `"references": ["UnityEngine.TestRunner", "UnityEditor.TestRunner", "Assembly-CSharp"]`
   - `"includePlatforms": ["Editor"]`(只在编辑器里跑)
   - `"precompiledReferences": ["nunit.framework.dll"]`
   - `"defineConstraints": ["UNITY_INCLUDE_TESTS"]`
2. 再往本目录放测试文件,并在 Unity 的 Test Runner 窗口里跑。

值得优先补测试的纯逻辑:`PlayerState`(CP 增长/上限 12 与 24)、`DeckController`/`EnemyDeckController` 的抽牌与洗牌、`BattleRules` 的部署合法性判定、`FanLayout` 的扇形坐标换算。
