# _Project 资源说明

本工程自己的资源全在这个目录下,`Assets/` 根下的 `Plugins`(DOTween)、`Resources`(DOTween 设置)、`TextMesh Pro`(TMP 包自带)属于第三方,不要动。

完整目录树、战斗流程、已知缺口见仓库根目录的 `README.md`。

## 找东西的快速索引

| 想改什么 | 去哪个文件 |
|---|---|
| 战斗流程 / 回合流转 | `Scripts/Core/Battle/TurnController.cs` |
| 部署规则 / 落点判定 / 排容量 | `Scripts/Core/Battle/BattlefieldManager.cs`、`BattleRules.cs` |
| 单条排 / 单张小卡 | `Scripts/Core/Battle/BattleRow.cs`、`FieldUnit.cs` |
| 我方牌堆手牌 / 抽牌 / 疲劳 | `Scripts/Core/Cards/DeckController.cs` |
| 敌方牌堆手牌 / CP | `Scripts/Core/Cards/EnemyDeckController.cs` |
| 费用(CP)池与 HUD | `Scripts/UI/Battle/CommandPointController.cs` |
| 玩家数据(CP 上限、回合补给) | `Scripts/Core/Models/PlayerState.cs` |
| 手牌布局(扇形排布) | `Scripts/UI/Cards/FanLayout.cs` |
| 卡面显示 | `Scripts/UI/Cards/CardDisplay.cs` |
| 拖动出牌 | `Scripts/UI/Cards/CardDragPlay.cs` |
| 卡牌数据格式 | `Scripts/Core/Data/CardData.cs` |
| 卡池数值 | `Data/Cards/{Han,Qin}/`,由 `Tools/build_card_assets.py` 生成 |
| 卡牌 / 建筑预制体 | `Prefabs/UI/`、`Prefabs/Battle/` |
| 自举时找画布 | `Scripts/UI/Common/CanvasUtil.cs`(统一取根画布;别再用 `FindObjectOfType<Canvas>()` —— 卡牌预制体自带嵌套 Canvas,场上一有卡就可能挑到牌上那个) |

## 挂载关系(战斗场景 Battle.unity)

| 物体 | 挂的脚本 |
|---|---|
| `BattleCanvas` | CommandPointController、EnemyDeckController、TurnController、BattleHudButtons |
| `BattleCanvas/Deck` | DeckController |
| `BattleCanvas/HandArea1` | FanLayout(我方扇形,参数独立) |
| `BattleCanvas/HandArea2` | FanLayout(敌方扇形)、EnemyHandUI |
| `BattleCanvas/cards1`、`cards2` | DeckUI(牌堆计数) |
| `BattleCanvas/ShowMessage` | BattleMessageUI |
| `BattleArea/RowsContainer` | BattlefieldManager |
| `HandUI`(根物体) | HandUI |
| `Card.prefab` 根物体 `Card` | CardDisplay、CardHover、CardDragPlay |

运行时才挂的:BattleRow / FieldUnit(由 BattlefieldManager 建)、CardHoverPreview(由 CardHover 加)、FloatingTipUI(自举创建)。

每个脚本类头的 XML 注释里都有一段「【挂载 & 调整】」,写明它挂在哪、哪些引用必须手连、常调的参数是什么。
