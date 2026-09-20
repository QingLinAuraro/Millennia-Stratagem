# Docs

| 文件 | 说明 |
|---|---|
| `千秋策策划案.md` | 设计文档。卡片效果、费用曲线、卡池规则、部署价值表都以它为准;里面标了标记区的卡池表由 `Tools/build_dynasty_sheets.py` 回写,**不要手改那部分**。 |
| `数据表demo.xlsx` | 数值表(卡池表、基础公式、部署价值表、「赳赳老秦」设计表)。卡牌资源的唯一数据源。 |
| `数据表demo.xlsx.bak` | 基准副本,`Tools/_final_check.py` 用它比对效果文本是否被改动,不要删。 |
| `CSharp知识点整理.md` | 项目用到的 C# 语法速查(委托/事件、泛型、协程、特性反射、Unity 特性等),每条附代码出处。新人上手或复习用。 |

改数值的流程:

1. 改 `数据表demo.xlsx`(需要重新设计卡池时先跑 `Tools/build_qin_sheet.py` / `build_dynasty_sheets.py`)。
2. 跑 `Tools/build_card_assets.py` 重新生成卡牌 `.asset`。
3. 跑 `Tools/_final_check.py` 自检。
4. 回 Unity 让它 Reimport,进 `Assets/_Project/Scenes/Battle.unity` 试。
