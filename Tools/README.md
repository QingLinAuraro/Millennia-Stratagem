# Tools

数据表与卡牌资源的生成脚本(Python 3,依赖 openpyxl)。

**所有脚本都要在仓库根目录 `千秋策（Millennia Stratagem）/` 下执行**,脚本里的路径都是相对根目录写的(如 `千秋策\Assets\_Project\...`、`Docs\数据表demo.xlsx`)。

| 脚本 | 作用 |
|---|---|
| `build_card_assets.py` | 读 `Docs/数据表demo.xlsx` 的「秦·卡池」「汉·卡池」两张工作表,生成卡牌 `CardData` 资源到 `千秋策/Assets/_Project/Data/Cards/{Qin,Han}/`,文件名形如 `<cardId>_<名称>.asset`。字段与 `Scripts/Core/Data/CardData.cs` 一一对应。 |
| `build_dynasty_sheets.py` | 生成/刷新 xlsx 里的朝代卡池总览表,并把两张卡池表回写进 `Docs/千秋策策划案.md` 的标记区(策划案里的卡池表是派生物,不要手改)。 |
| `build_qin_sheet.py` | 在 xlsx 的「赳赳老秦」表里设计秦朝卡组。 |
| `_final_check.py` | 数据表自检:比对 `数据表demo.xlsx` 与基准副本 `数据表demo.xlsx.bak` 的效果文本一致性等。 |

## 注意

- 卡牌 `.asset` 是生成物,**不要手工改单张卡**:改 xlsx 后重新跑 `build_card_assets.py`。
- `build_card_assets.py` 会写 Unity 的 `.asset` 与 `.asset.meta`;脚本里写死了 `CardData.cs` 的脚本 guid。如果以后给 `CardData.cs` 换文件路径(不是重命名类),guid 不变、脚本不用改;但如果重新生成了 `.meta`,记得同步脚本里的 guid 常量。
- 跑完生成脚本后回到 Unity,让它 Reimport(切到 Unity 窗口即可)。
