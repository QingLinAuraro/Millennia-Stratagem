# -*- coding: utf-8 -*-
"""
千秋策 · 朝代卡池表生成器（秦 / 汉）

锁定基准（只读，绝不修改）：
  《部署价值表》E3  cost_value = (3 - dcs/8) * dcs + 2 * acs / 3 - 1
  《基础公式》B20   card_value  = β * atk + α * hp + Σ词条等价费用
  《基础公式》E9:F12  β/α：步兵 1.1/0.9 · 骑兵 0.9/1.1 · 弓兵 1.2/0.8 · 支援(器械) 2/2
  《基础公式》D24:D61 词条/效果等价费用

已确认口径（本次锁定）：
  1) 血战 = 1.5（基础公式 D27 原值）
  2) 守护 = 1，且携带者部署时 +1HP（基础公式 E26）；卡面 HP 已含该 +1HP
  3) 闪击只适用于骑兵：骑兵携带 = 0，骑兵未携带 = -0.5；步兵/弓兵/器械不受该条影响
  4) 行动费用 acs 按策划案 §2.5 档位表（兵种固有）

产出工作表：
  汉·卡池 / 秦·卡池   —— 程序对接用平表（列名与 CardData.cs 字段对齐）
  遗漏词条定价         —— 只列不改：基础表未定价/半定价条目的整理与建议
  卡池总览             —— 两朝数量与费用分布、平衡偏差统计（引用公式活算）
"""
import shutil, os
from openpyxl import load_workbook
from openpyxl.styles import Font, PatternFill, Alignment, Border, Side
from openpyxl.formatting.rule import FormulaRule
from openpyxl.worksheet.datavalidation import DataValidation

SRC = r"Docs\数据表demo.xlsx"

# ============================================================ 一、锁定口径
BETA = {"步兵": (1.1, 0.9), "骑兵": (0.9, 1.1), "弓兵": (1.2, 0.8), "支援": (1.0, 1.0)}

# 词条/效果等价费用（基础公式 D24:D61）
ENTRY = {
    "闪击": 0.5,          # D24；按兵种特例处理，查表值仅备用
    "伏兵": 0.5,          # D25
    "守护": 1.0,          # D26
    "血战": 1.5,          # D27
    "连战": 1.0,          # D28
    "摸牌1": 1.0,         # D29
    "摸牌2": 3.0,         # D30
    "摸牌3": 6.0,         # D31
    "召唤(牌堆)": 0.5,    # D32
    "召唤(手牌)": 1.0,    # D33
    "建筑回血3": 1.0,     # D34
    "建筑回血5": 2.0,     # D35
    "建筑回血7": 3.0,     # D36
    "兵回血3": 2.0,       # D37
    "攻击后大营回血2": 1.5,  # D38
    "重甲1": 1.0,         # D39
    "重甲2": 2.5,         # D40
    "重甲3": 4.0,         # D41
    "压制": 1.0,          # D45
    "抵抗": 0.5,          # D46
    "等价·ATK对齐HP": 3.0,   # D50
    "等价·敌HP调1": 2.0,     # D51
    "等价·敌ATK调1": 2.0,    # D52
    "弃手牌1": 2.0,       # D53
    "直伤3": 2.0,         # D57
    "随机直伤3": 1.0,     # D58
    "消灭随机单位": 6.0,  # D60
    "整排伤害2": 3.0,     # D61
}

# —— buff 类费率（基础公式 D47/D48/D49）：不是独立词条，写进「效果」栏，按点数计价
BUFF_RATE = {
    "atk": 0.75,   # D47 每点 atk 记 0.75 费
    "hp": 0.50,    # D48 每点 hp 记 0.50 费
    "both": 0.60,  # D49 每点 atk 和 hp 均记 0.60 费
}

# 词条查表顺序（O 列 VLOOKUP 用）：**只允许基础公式 B26:D64 里存在的条目**
ENTRY_ORDER = [
    ("闪击", 0.5, "B27｜部署回合可移动或攻击。按兵种特例：骑兵携带=0，骑兵未携带=-0.5；步/弓/器不受该条影响（本次锁定）"),
    ("伏兵", 0.5, "B28｜在主动攻击前不可被攻击"),
    ("守护", 1.0, "B29｜单位存活时，同排相邻槽位的友方单位与建筑不能被选中；携带者部署时+1HP（E26），卡面HP已含"),
    ("血战", 1.5, "B30｜攻击后回复2HP（被反击不回）；数值以基础表为准（1.5）"),
    ("连战", 1.0, "B31｜同一回合可连续攻击两次，行动次数按2计"),
    ("摸牌1", 1.0, "B32｜抽取1张牌"),
    ("摸牌2", 3.0, "B33｜抽取2张牌"),
    ("摸牌3", 6.0, "B34｜抽取3张牌"),
    ("召唤(牌堆)", 0.5, "B35｜部署时添加一张卡牌进入牌堆"),
    ("召唤(手牌)", 1.0, "B36｜部署时将一张卡从牌堆加入手牌"),
    ("建筑回血3", 1.0, "B37｜为建筑回复3hp"),
    ("建筑回血5", 2.0, "B38｜为建筑回复5hp"),
    ("建筑回血7", 3.0, "B39｜为建筑回复7hp"),
    ("兵回血3", 2.0, "B40｜为兵回复3hp"),
    ("攻击后大营回血2", 1.5, "B41｜攻击后，为大营回复2hp"),
    ("重甲1", 1.0, "B42｜每层重甲在受到伤害时减少一点，仅重型单位可携带"),
    ("重甲2", 2.5, "B43｜同上，两层"),
    ("重甲3", 4.0, "B44｜同上，三层"),
    ("压制", 1.0, "B48｜限制敌方单位一回合行动，仅策略卡，仅可作用于兵牌"),
    ("抵抗", 0.5, "B49｜免疫压制，仅兵牌"),
    ("等价·ATK对齐HP", 3.0, "B53｜将指定单位的atk调整到与hp相同"),
    ("等价·敌HP调1", 2.0, "B54｜将对方任意单位的hp调整到1"),
    ("等价·敌ATK调1", 2.0, "B55｜将对方任意单位的atk调整到1"),
    ("弃手牌1", 2.0, "B56｜弃置敌方一张手牌"),
    ("直伤3", 2.0, "B60｜对指定单位造成3点伤害，仅策略卡、仅可作用于兵牌"),
    ("随机直伤3", 1.0, "B61｜对敌方随机单位造成3伤害（与 B60 差价即「指定」的价值）"),
    ("消灭随机单位", 6.0, "B60 区间的「消灭类」基准条目｜消灭一个敌方随机单位"),
    ("整排伤害2", 3.0, "B60 区间的「整排伤害」基准条目｜对指定一排单位造成2点伤害"),
]

# 行动费用 acs（策划案 §2.5 档位表，兵种+重量固有；与「行动次数」无关）
# 部署价值 cost_value 只取 dcs 与 acs 两个入参，跟攻击次数无关。
ACS = {
    ("步兵", "轻"): 1, ("步兵", "中"): 1, ("步兵", "重"): 2,
    ("骑兵", "轻"): 1, ("骑兵", "中"): 2, ("骑兵", "重"): 2,
    ("弓兵", "轻"): 1, ("弓兵", "中"): 1, ("弓兵", "重"): 2,
    ("支援", "轻"): 2, ("支援", "中"): 2, ("支援", "重"): 2,
}

def acs_of(kind, unit, weight, keywords):
    """行动费用 = 兵种 + 重量查表；连战等词条不影响行动费用（连战自身已按 1.0 费计入卡牌价值）"""
    if kind == "策略牌":
        return 0
    return ACS[(unit, weight)]

def ap_of(kind, unit, keywords):
    """行动次数（每回合可行动/攻击的次数）：骑兵2；连战2；其余一律1——器械每回合只能攻击一次"""
    if kind == "策略牌":
        return None
    if "连战" in keywords:
        return 2
    if unit == "骑兵":
        return 2
    return 1

def cost_value(dcs, acs):
    return round((3 - dcs / 8) * dcs + 2 * acs / 3 - 1, 3)

def blitz_term(kind, unit, keywords):
    if kind == "策略牌":
        return 0.0
    if unit == "骑兵":
        return 0.0 if "闪击" in keywords else -0.5
    return 0.0

def card_value(kind, unit, atk, hp, keywords, effects=None, dcs=None):
    """卡牌价值
    兵牌：β*atk + α*hp + Σ词条等价 + Σ效果等价 + 闪击调整（不套用 cost_value）
    策略牌：按《基础公式》B20「部署后产生的收益等于部署费用」，卡牌价值就是它的部署费用本身，
            既不套兵牌公式，也不等于「Σ效果价」——Σ效果价只是配平时的约束条件（必须恰等于费用）。
    """
    if kind == "策略牌":
        assert dcs is not None, "策略牌必须传入部署费用才能取卡牌价值"
        return float(dcs)
    b, a = BETA[unit]
    return round(b * (atk or 0) + a * (hp or 0) + blitz_term(kind, unit, keywords)
                 + sum(ENTRY[k] for k in keywords if k != "闪击")
                 + sum(eff_cost(e) for e in (effects or [])), 3)

# —— 效果名 → 等价费用（严格照抄基础公式 C 列的「效果」原文）
EFFECT_TEXT = {
    "部署回合可移动或攻击": 0.5,
    "在主动攻击前不可被攻击": 0.5,
    "单位存活时，同排相邻槽位的友方单位与建筑不能被选中（守护单位之间不互相保护）": 1.0,
    "攻击后回复2hp": 1.5,
    "同一回合可连续攻击两次": 1.0,
    "抽取1张牌": 1.0,
    "抽取2张牌": 3.0,
    "抽取3张牌": 6.0,
    "部署时添加一张卡牌进入牌堆": 0.5,
    "部署时将一张卡从牌堆加入手牌": 1.0,
    "为建筑回复3hp": 1.0,
    "为建筑回复5hp": 2.0,
    "为建筑回复7hp": 3.0,
    "为兵回复3hp": 2.0,
    "攻击后，为大营回复2hp": 1.5,
    "每层重甲在受到伤害时减少一点": 1.0,   # 重甲1（重甲2/3 另见词条表）
    "限制敌方单位一回合行动": 1.0,
    "免疫压制效果": 0.5,
    "将指定单位的atk调整到与hp相同": 3.0,
    "将对方任意单位的hp调整到1": 2.0,
    "将对方任意单位的atk调整到1": 2.0,
    "弃置敌方一张手牌": 2.0,
    "对指定单位造成3点伤害": 2.0,
    "对敌方随机单位造成3伤害": 1.0,
    "消灭一个敌方随机单位": 6.0,
    "对指定一排单位造成2点伤害": 3.0,
}
# 效果「中文写法」→ 基础表原文（卡面上写得通顺，计价仍按基础表原文）
EFFECT_ALIAS = {
    "为一个友方单位回复3点HP": "为兵回复3hp",
    "为一座己方建筑回复3点HP": "为建筑回复3hp",
    "为一座己方建筑回复5点HP": "为建筑回复5hp",
    "为一座己方建筑回复7点HP": "为建筑回复7hp",
    "对一名敌方兵牌造成3点伤害": "对指定单位造成3点伤害",
    "对敌方随机单位造成3点伤害": "对敌方随机单位造成3伤害",
    "对指定一排单位造成2点伤害": "对指定一排单位造成2点伤害",
    "弃置敌方1张手牌": "弃置敌方一张手牌",
    "限制敌方一个单位一回合行动": "限制敌方单位一回合行动",
    "将对方任意单位的hp调整到1": "将对方任意单位的hp调整到1",
    "将对方任意单位的atk调整到1": "将对方任意单位的atk调整到1",
    "将指定单位的atk调整到与hp相同": "将指定单位的atk调整到与hp相同",
    "部署时添加一张卡牌进入牌堆": "部署时添加一张卡牌进入牌堆",
    "部署时将一张卡从牌堆加入手牌": "部署时将一张卡从牌堆加入手牌",
    "免疫压制效果": "免疫压制效果",
}
for _alias, _base in EFFECT_ALIAS.items():
    assert _base in EFFECT_TEXT, f"效果别名「{_alias}」指向的基础表原文「{_base}」不存在"
    EFFECT_TEXT.setdefault(_alias, EFFECT_TEXT[_base])

# —— 效果 DSL：列表元素形如 (效果文本, 等价费用)，或 ("buff", atk点数, hp点数, 文本)
#    buff 按基础表费率计价：atk 0.75/点、hp 0.5/点、atk+hp 各 0.6/点
#    基础表 B50 限制：buff 仅限策略卡与支援卡的部署效果，且增加数值的费用不得超过 3 费
def eff_cost(e):
    if e[0] == "buff":
        _, na, nh, _t = e
        if na and nh:
            return round((na + nh) * BUFF_RATE["both"], 3)
        if na:
            return round(na * BUFF_RATE["atk"], 3)
        return round(nh * BUFF_RATE["hp"], 3)
    return round(EFFECT_TEXT[e[0]], 3)

def eff_text(e):
    return e[3] if e[0] == "buff" else e[0]

def effect_sum(c):
    """策略牌/带效果兵牌的「效果等价」= Σ词条价 + Σ效果价"""
    kws = c[9]
    effs = c[10] or []
    return round(sum(ENTRY[k] for k in kws) + sum(eff_cost(e) for e in effs), 3)

def eff_col(c):
    effs = c[10] or []
    return "；".join(eff_text(e) for e in effs) if effs else None

# 中文词条名 -> 程序 enum Keyword（CardData.cs）
# 注意：策略卡不带词条；兵牌词条只包含 CardData.cs 里已声明的 Keyword 枚举成员
ENUM_MAP = {
    "闪击": "Blitz", "伏兵": "Ambush", "守护": "Guard", "血战": "BloodBattle",
    "连战": "DoubleStrike", "重甲1": "HeavyArmor", "重甲2": "HeavyArmor", "重甲3": "HeavyArmor",
}

def ENUM_KW(kws):
    out = []
    for k in kws:
        e = ENUM_MAP.get(k)
        if e and e not in out:
            out.append(e)
    return ",".join(out) if out else None

# ============================================================ 二、卡池数据
# (cardId, 名称, 稀有度, 类型, 兵种, 重量, 费用, ATK, HP, [词条], [效果], 古文, 出处)
# 规则：
#   · [词条] 只能写「二、词条表」里有的词条（基础公式 B26:D64），词条列由脚本自动生成、不手填
#   · [效果] 写基础表里可查的效果；buff 类归入效果栏（一次性增减数值、无持续时间），按费率计价，不占词条列
#   · 策略牌「卡牌价值 = 部署费用」，因此每张策略牌的 Σ效果价 必须正好等于它的费用
#   · 纯词条兵牌不填效果（效果栏留空）
QIN = [
    # ---- 普通 8 ----
    ("qin_001", "什伍卒", "普通", "兵牌", "步兵", "轻", 1, 1, 2, [],
     None, "令民为什伍，而相牧司连坐。", "《史记·商君列传》"),
    ("qin_002", "戍卒", "普通", "兵牌", "步兵", "中", 2, 2, 3, [],
     None, "发闾左適戍渔阳九百人，屯大泽乡。", "《史记·陈涉世家》"),
    ("qin_003", "秦锐士", "普通", "兵牌", "步兵", "中", 3, 3, 5, [],
     None, "魏氏之武卒，不可以遇秦之锐士。", "《荀子·议兵》"),
    ("qin_004", "劲弩手", "普通", "兵牌", "弓兵", "轻", 2, 3, 2, [],
     None, "秦带甲百余万，车千乘，骑万匹。", "《史记·张仪列传》"),
    ("qin_005", "连弩台", "普通", "兵牌", "支援", "重", 3, 3, 4, [],
     [("buff", 0, 3, "部署当回合，指定一个友方单位本回合HP+3")],
     "备高临以连弩之车；鼓之舞之，三军奋击。", "《墨子·备高临》(化用)"),
    ("qin_006", "疾驰轻骑", "普通", "兵牌", "骑兵", "轻", 2, 2, 3, ["闪击"],
     None, "车骑之精，轻驰如风，掠野奔袭。", "《孙膑兵法·八阵》(化用)"),
    ("qin_007", "弩矢督", "普通", "策略牌", "—", "—", 1, None, None, [],
     [("对敌方随机单位造成3点伤害", 1.0)],
     "矢石之积，皆入于仓，以时给之。", "睡虎地秦墓竹简·仓律(化用)"),
    ("qin_008", "攒射", "普通", "策略牌", "—", "—", 2, None, None, [],
     [("对一名敌方兵牌造成3点伤害", 2.0)],
     "劲弩攒发，矢如飞蝗。", "《墨子·备城门》(化用)"),
    # ---- 稀有 8 ----
    ("qin_009", "斥候骑", "稀有", "兵牌", "骑兵", "轻", 3, 3, 4, ["闪击", "伏兵"],
     None, "斥候远窥，昼伏夜驰，以探敌情。", "《武经总要》(化用)"),
    ("qin_010", "轻车", "稀有", "兵牌", "骑兵", "中", 3, 3, 4, ["闪击", "连战"],
     None, "小戎俴收，五楘梁辀。", "《诗经·秦风·小戎》"),
    ("qin_011", "穿杨弩手", "稀有", "兵牌", "弓兵", "中", 3, 4, 3, [],
     None, "去柳叶百步而射之，百发百中。", "《战国策·西周策》(化用)"),
    ("qin_012", "铁鹰剑士", "稀有", "兵牌", "步兵", "重", 4, 5, 4, ["重甲1"],
     None, "王于兴师，修我甲兵，与子偕行。", "《诗经·秦风·无衣》"),
    ("qin_013", "陷阵士", "稀有", "兵牌", "步兵", "中", 4, 4, 4, ["血战"],
     None, "选锐冲之，分兵继之，急击勿疑。", "《吴子·料敌》"),
    ("qin_014", "飞羽轻骑", "稀有", "兵牌", "骑兵", "轻", 4, 6, 4, ["闪击"],
     None, "追亡逐北，伏尸百万，流血漂橹。", "贾谊《过秦论》(化用)"),
    ("qin_015", "军功爵", "稀有", "策略牌", "—", "—", 4, None, None, [],
     [("buff", 2, 3, "指定一个友方单位本回合ATK+2、HP+3"), ("抽取1张牌", 1.0)],
     "能得甲首一者，赏爵一级，益田一顷，益宅九亩。", "《商君书·境内》"),
    ("qin_016", "反间", "稀有", "策略牌", "—", "—", 2, None, None, [],
     [("弃置敌方1张手牌", 2.0)],
     "秦多与赵王宠臣郭开金，为反间。", "《史记·廉颇蔺相如列传》"),
    # ---- 史诗 5 ----
    ("qin_017", "连弩士", "史诗", "兵牌", "弓兵", "轻", 3, 4, 2, ["连战"],
     None, "自以连弩候大鱼出射之。", "《史记·秦始皇本纪》"),
    ("qin_018", "绝粮道", "史诗", "策略牌", "—", "—", 2, None, None, [],
     [("抽取1张牌", 1.0), ("对敌方随机单位造成3点伤害", 1.0)],
     "又分其兵，绝其粮道，赵军乏食而乱。", "《史记》(化用)"),
    ("qin_019", "锐士营", "史诗", "兵牌", "步兵", "重", 5, 5, 5, ["守护", "重甲1"],
     None, "秦性强，其地险，其政严，其赏罚信，其人不让，皆有斗心。", "《吴子·料敌》"),
    ("qin_020", "商君变法", "史诗", "策略牌", "—", "—", 4, None, None, [],
     [("抽取1张牌", 1.0), ("为一座己方建筑回复7点HP", 3.0)],
     "令既具，未布，恐民之不信，已乃立三丈之木于国都市南门。", "《史记·商君列传》"),
    ("qin_021", "移民实边", "史诗", "策略牌", "—", "—", 6, None, None, [],
     [("抽取3张牌", 6.0)],
     "明赏罚，劝耕战，民勇于公战，怯于私斗。", "《史记·商君列传》(化用)"),
    # ---- 传说 3 ----
    ("qin_022", "玄甲铁骑", "传说", "兵牌", "骑兵", "重", 6, 6, 6, ["闪击", "血战"],
     None, "岂曰无衣？与子同裳。王于兴师，修我甲兵。", "《诗经·秦风·无衣》"),
    ("qin_023", "函谷铁卫", "传说", "兵牌", "步兵", "重", 6, 6, 6, ["守护", "重甲1"],
     None, "深沟高垒，分兵固守，使敌欲战不得。", "《吴子·应变》(化用)"),
    ("qin_024", "蹶张神弩", "传说", "兵牌", "弓兵", "重", 6, 7, 7, [],
     None, "弩出于蹶张，射远命中，所当皆靡。", "《武经总要》(化用)"),
]

HAN = [
    # ---- 普通 8 ----
    ("han_001", "汉弩手", "普通", "兵牌", "弓兵", "轻", 2, 3, 2, [],
     None, "汉弩一石十二斤，射程六百步。", "《汉书·匈奴传》(化用)"),
    ("han_002", "材官骑士", "普通", "兵牌", "骑兵", "轻", 1, 2, 1, ["闪击"],
     None, "一岁为卫士，一岁为材官骑士。", "《汉书·食货志》"),
    ("han_003", "边郡戍卒", "普通", "兵牌", "步兵", "中", 2, 2, 3, [],
     None, "烽火通于甘泉、长安。", "《汉书·匈奴传》"),
    ("han_004", "材官", "普通", "兵牌", "步兵", "中", 3, 3, 5, [],
     None, "材官、骑士，各有员数。", "《汉书·高帝纪》"),
    ("han_005", "治粟都尉", "普通", "兵牌", "支援", "重", 3, 3, 4, [],
     [("buff", 0, 3, "部署当回合，指定一个友方单位本回合HP+3")],
     "桑弘羊为治粟都尉，领大农，尽代仅斡天下盐铁。", "《史记·平准书》(化用)"),
    ("han_006", "游徼骑", "普通", "兵牌", "骑兵", "中", 3, 3, 4, ["闪击", "连战"],
     None, "繇役轻骑，往来如风。", "《汉书》(化用)"),
    ("han_007", "招降", "普通", "策略牌", "—", "—", 1, None, None, [],
     [("对敌方随机单位造成3点伤害", 1.0)],
     "招降纳叛，来者不拒。", "《汉书》(化用)"),
    ("han_008", "屯田", "普通", "策略牌", "—", "—", 3, None, None, [],
     [("为一座己方建筑回复5点HP", 2.0), ("抽取1张牌", 1.0)],
     "募民徙塞下，皆便田作。", "《汉书·晁错传》(化用)"),
    # ---- 稀有 8 ----
    ("han_009", "轻车骑", "稀有", "兵牌", "骑兵", "轻", 3, 3, 4, ["闪击", "伏兵"],
     None, "轻车锐骑，昼夜兼行。", "《汉书·卫青传》(化用)"),
    ("han_010", "材官蹶张", "稀有", "兵牌", "弓兵", "中", 3, 4, 3, [],
     None, "材官蹶张，弓弩并发。", "《汉书·申屠嘉传》(化用)"),
    ("han_011", "屯田卒", "稀有", "兵牌", "步兵", "中", 3, 4, 2, ["守护"],
     None, "且耕且守，边备以充。", "《汉书·赵充国传》(化用)"),
    ("han_012", "玄甲校士", "稀有", "兵牌", "步兵", "重", 4, 5, 4, ["重甲1"],
     None, "玄甲曜日，望之如墨。", "《汉书》(化用)"),
    ("han_013", "死士营", "稀有", "兵牌", "步兵", "中", 4, 4, 4, ["血战"],
     None, "陷阵之志，有死无生。", "《后汉书·耿弇传》(化用)"),
    ("han_014", "骁骑", "稀有", "兵牌", "骑兵", "轻", 4, 5, 5, ["闪击"],
     None, "骁骑十万，旌旗蔽野。", "《汉书·匈奴传》(化用)"),
    ("han_015", "破敌封赏", "稀有", "策略牌", "—", "—", 4, None, None, [],
     [("buff", 2, 3, "指定一个友方单位本回合ATK+2、HP+3"), ("抽取1张牌", 1.0)],
     "斩首捕虏，赐爵有差。", "《汉书·景帝纪》(化用)"),
    ("han_016", "离间", "稀有", "策略牌", "—", "—", 2, None, None, [],
     [("抽取1张牌", 1.0), ("对敌方随机单位造成3点伤害", 1.0)],
     "陈平多阴谋，离间楚君臣。", "《汉书·陈平传》(化用)"),
    # ---- 史诗 5 ----
    ("han_017", "射声士", "史诗", "兵牌", "弓兵", "轻", 3, 4, 2, ["连战"],
     None, "射声校尉，闻声而中。", "《汉书·百官公卿表》(化用)"),
    ("han_018", "决水灌城", "史诗", "策略牌", "—", "—", 4, None, None, [],
     [("buff", 0, 2, "指定一个友方单位本回合HP+2"), ("对指定一排单位造成2点伤害", 3.0)],
     "决荥阳之水以灌城，城坏。", "《史记·高祖本纪》(化用)"),
    ("han_019", "虎贲营", "史诗", "兵牌", "步兵", "重", 5, 4, 5, ["守护", "重甲2"],
     None, "虎贲之士，百人有余。", "《汉书·王莽传》(化用)"),
    ("han_020", "白马校尉", "史诗", "兵牌", "骑兵", "轻", 5, 5, 6, ["闪击", "伏兵"],
     None, "白马义从，所向披靡。", "《后汉书·公孙瓒传》(化用)"),
    ("han_021", "盐铁论", "史诗", "策略牌", "—", "—", 5, None, None, [],
     [("限制敌方一个单位一回合行动", 1.0), ("为一座己方建筑回复5点HP", 2.0), ("为一个友方单位回复3点HP", 2.0)],
     "笼天下盐铁，以佐赋税。", "《盐铁论》(化用)"),
    # ---- 传说 3 ----
    ("han_022", "骠骑精骑", "传说", "兵牌", "骑兵", "重", 6, 6, 6, ["闪击", "血战"],
     None, "匈奴未灭，无以家为。", "《汉书·霍去病传》(化用)"),
    ("han_023", "羽林壁垒", "传说", "兵牌", "步兵", "重", 6, 6, 4, ["守护", "重甲2"],
     None, "壁垒森严，胡骑不敢南牧。", "《汉书》(化用)"),
    ("han_024", "汉家大黄弩", "传说", "兵牌", "弓兵", "重", 6, 7, 6, ["召唤(牌堆)"],
     None, "大黄参连弩，射及数百步。", "《汉书·李广传》(化用)"),
]

# ============================================================ 三、校验
print("=" * 104)
print(f"{'id':<9}{'名称':<12}{'稀有':<5}{'兵种':<5}{'费':>3}{'acs':>4}{'AP':>3}{'ATK':>5}{'HP':>4}  {'Σ词条':>6}{'部署价值':>9}{'卡牌价值':>9}{'差值':>8}")

def tolerance(kind, dcs):
    """平衡判定阈值：兵牌 0.35（1费兵牌放宽到 0.6）"""
    if kind == "策略牌":
        return 0.0   # 策略牌价值 = 部署费用，必须严格相等
    return 0.6 if dcs <= 1 else 0.35

def validate(cards, label):
    rows = []
    worst_u = worst_s = 0.0
    worst_ratio, worst_cid, worst_tol = -1.0, "-", 0.0
    for c in cards:
        cid, name, rar, kind, unit, wt, dcs, atk, hp, kws, effs, quote, src = c
        acs = acs_of(kind, unit, wt, kws)
        ap = ap_of(kind, unit, kws)
        sigma = effect_sum(c)
        pv = cost_value(dcs, acs)
        # 策略牌：部署后产生的收益 = 部署费用（基础公式 B20），因此卡牌价值直接等于费用
        cv = card_value(kind, unit, atk, hp, kws, effs, dcs)
        d = round(cv - pv, 3)
        rows.append((cid, name, rar, kind, unit, wt, dcs, acs, ap, atk, hp, kws, eff_col(c), quote, src,
                     sigma, pv, cv, d, effs or []))
        if kind == "策略牌":
            # 校验：策略牌的效果价必须正好等于它的费用
            gap = round(sigma - dcs, 3)
            worst_s = max(worst_s, abs(gap))
            flag = "" if abs(gap) < 1e-9 else f"  <== 效果价与费用差 {gap:+.3f}"
        else:
            if abs(d) / tolerance(kind, dcs) > worst_ratio:
                worst_ratio, worst_u, worst_cid = abs(d) / tolerance(kind, dcs), abs(d), cid
                worst_tol = tolerance(kind, dcs)
            flag = "" if abs(d) <= tolerance(kind, dcs) else "  <== 越界"
        print(f"{cid:<9}{name:<12}{rar:<5}{unit:<5}{dcs:>3}{acs:>4}{str(ap or '-'):>3}"
              f"{(atk or 0):>5}{(hp or 0):>4}  {sigma:>6.2f}{pv:>9.3f}{cv:>9.3f}{d:>+8.3f}{flag}")
    print(f"-- {label}：兵牌最差 {worst_cid} |差值| {worst_u:.3f}（该卡门槛 {worst_tol:.2f}）"
          f"｜越界兵牌 {sum(1 for x in rows if x[3] == '兵牌' and abs(x[18]) > tolerance('兵牌', x[6]))} 张"
          f"｜策略牌「效果价 − 费用」最大偏差 {worst_s:.3f}（必须为 0）")
    print("=" * 104)
    return rows

QROWS = validate(QIN, "秦")
HROWS = validate(HAN, "汉")

# 卡池里实际用到的词条集合：T 列只对这些词条生成查表项，避免公式过长
USED_ENTRIES = {k for _, _, _, _, _, _, _, _, _, kws, _, _, _ in (QIN + HAN) for k in kws}
assert USED_ENTRIES <= {n for n, _, _ in ENTRY_ORDER}, \
    f"有词条未登记等价费用：{sorted(USED_ENTRIES - {n for n, _, _ in ENTRY_ORDER})}"

# 硬规则校验：策略牌只能用效果、不能用词条；buff 只能出现在效果栏且增加数值不超过 3 费
for c in QIN + HAN:
    cid, _n, _r, kind, unit, _wt, dcs, _a, _h, kws, effs, _q, _s = c
    if kind == "策略牌":
        assert not kws, f"{cid} 是策略牌，词条列必须留空（策略牌没有闪击/连击等兵种词条）"
        assert effs, f"{cid} 是策略牌，必须有效果"
    for e in (effs or []):
        if e[0] == "buff":
            assert unit in ("—", "支援"), f"{cid} 携带 buff，但基础表 B50 限定「仅限策略卡和支援卡」"
            assert eff_cost(e) <= 3.0 + 1e-9, f"{cid} 的 buff 费用 {eff_cost(e)} 超过基础表 B50 的 3 费上限"
        else:
            assert e[0] in EFFECT_TEXT, f"{cid} 的效果「{e[0]}」不在基础表内"
            assert abs(e[1] - EFFECT_TEXT[e[0]]) < 1e-9, \
                f"{cid} 的效果「{e[0]}」单价 {e[1]} 与基础表 {EFFECT_TEXT[e[0]]} 不一致"

# ---- 面板求解：在锁定基准下找「既有合理攻防比、又贴合成费用预算」的 ATK/HP
def solve(dcs, acs, unit, kws, effs=None, tol=0.7):
    """返回 (预算, [(差值, atk, hp), ...])，按 |差值| 排序，且剔除失衡攻防比。"""
    pv = cost_value(dcs, acs)
    cands = []
    for atk in range(1, 10):
        for hp in range(1, 10):
            d = round(card_value("兵牌", unit, atk, hp, kws, effs) - pv, 3)
            if abs(d) > tol:
                continue
            if atk * 3 < hp or hp * 3 < atk:      # 不许出现 1/8、9/1 这类失衡面板
                continue
            cands.append((abs(d), d, atk, hp))
    cands.sort(key=lambda t: (t[0], -min(t[2], t[3]), t[2] + t[3]))
    return pv, [(d, a, h) for _, d, a, h in cands]

print("面板求解（每个费用点上的可用面板；越靠前越推荐）")
print("-" * 104)
bad = 0
for label, cards in (("秦", QIN), ("汉", HAN)):
    for c in cards:
        cid, name, rar, kind, unit, wt, dcs, atk, hp, kws, effs, _q, _s = c
        if kind != "兵牌":
            continue
        acs = acs_of(kind, unit, wt, kws)
        pv = cost_value(dcs, acs)
        d = round(card_value(kind, unit, atk, hp, kws, effs, dcs) - pv, 3)
        pv, cands = solve(dcs, acs, unit, kws, effs)
        ok = abs(d) <= 0.7
        if not ok:
            bad += 1
        top = "  ".join(f"{a}/{h}({dd:+.2f})" for dd, a, h in cands[:4]) or "无可行面板"
        print(f"{'OK ' if ok else '!! '}{label} {cid} {name:<10} {kind[:2]} {unit}{wt} {dcs}费{acs}acs "
              f"现值{atk}/{hp}({d:+.2f}) 预算{pv:>7.3f} → {top}")
print(f"-- 待改面板 {bad} 张")
print("-" * 104)

# ============================================================ 四、写入工作簿
if not os.path.exists(SRC + ".bak"):
    shutil.copyfile(SRC, SRC + ".bak")
wb = load_workbook(SRC)

CARD_COLS = [
    ("序号", 5), ("cardId", 11), ("名称", 12), ("朝代", 7), ("稀有度", 7), ("cardType", 9),
    ("unitType", 8), ("重量", 6), ("部署费用", 9), ("行动费用", 9), ("行动次数", 9),
    ("ATK", 5), ("HP", 5), ("射程", 5), ("keywords", 18), ("keywords(程序)", 20), ("效果", 40),
    ("古文描述", 32), ("出处", 20), ("效果等价", 9), ("部署价值", 9), ("卡牌价值", 9), ("差值", 9),
]
NCOL = len(CARD_COLS)          # 23 列 = A..W

THIN = Side(style="thin", color="BFBFBF")
BORDER = Border(left=THIN, right=THIN, top=THIN, bottom=THIN)
H_FILL = PatternFill("solid", fgColor="2F4F6F")
H_FONT = Font(bold=True, color="FFFFFF", size=10)
SEC_FILL = PatternFill("solid", fgColor="DCE6F1")
SEC_FONT = Font(bold=True, color="1F3864", size=11)
TITLE_FONT = Font(bold=True, size=16, color="8B1A1A")
NOTE_FONT = Font(size=9, color="404040")
CENTER = Alignment(horizontal="center", vertical="center")
LEFT = Alignment(horizontal="left", vertical="center", wrap_text=True)
LEFTC = Alignment(horizontal="left", vertical="center", wrap_text=True)

def cell(ws, r, c, v, font=None, fill=None, align=None, fmt=None, border=True):
    x = ws.cell(r, c, v)
    if font: x.font = font
    if fill: x.fill = fill
    if align: x.alignment = align
    if fmt: x.number_format = fmt
    if border: x.border = BORDER
    return x

def section(ws, r, text):
    cell(ws, r, 1, text, font=SEC_FONT, fill=SEC_FILL, align=LEFT)
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=NCOL)
    return r + 1

def build_pool(wb, sheet, rows, title, dynasty, tab):
    if sheet in wb.sheetnames:
        del wb[sheet]
    ws = wb.create_sheet(sheet)
    for j, (h, w) in enumerate(CARD_COLS, start=1):
        ws.column_dimensions[ws.cell(1, j).column_letter].width = w

    r = 1
    cell(ws, r, 1, title, font=TITLE_FONT, border=False)
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=NCOL)
    ws.row_dimensions[r].height = 26
    r += 1
    cell(ws, r, 1, "数值基准：《部署价值表》E3 cost_value=(3-dcs/8)*dcs+2*acs/3-1 ；《基础公式》B20 card_value=β*atk+α*hp+Σ词条等价费用。"
                   "策略牌按 B20 后半句「部署后产生的收益等于部署费用」，卡牌价值直接等于部署费用。本表不修改任何基准表单元格。",
         font=NOTE_FONT, align=LEFT, border=False)
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=NCOL)
    r += 2

    # 一、系数表
    r = section(ws, r, "一、兵种系数表（引用《基础公式》E9:F12）")
    for j, h in enumerate(["cardType / unitType", "β（ATK）", "α（HP）"], start=1):
        cell(ws, r, j, h, font=H_FONT, fill=H_FILL, align=CENTER)
    r += 1
    coef_first = r
    for i, (u, key) in enumerate([("步兵", "步兵"), ("骑兵", "骑兵"), ("弓兵", "弓兵"), ("支援", "支援")]):
        cell(ws, r, 1, u, align=CENTER)
        cell(ws, r, 2, f"='基础公式'!E{9 + i}", align=CENTER, fmt="0.0")
        cell(ws, r, 3, f"='基础公式'!F{9 + i}", align=CENTER, fmt="0.0")
        r += 1
    coef_last = r - 1
    COEF = f"$A${coef_first}:$C${coef_last}"
    r += 1

    # 二、词条价格表
    r = section(ws, r, "二、词条/效果等价费用表（逐条抄录《基础公式》B26:D64；本表不含任何基准表以外的条目）")
    for j, h in enumerate(["词条/效果", "等价费用", "来源/口径"], start=1):
        cell(ws, r, j, h, font=H_FONT, fill=H_FILL, align=CENTER)
    r += 1
    entry_first = r
    for name, val, note in ENTRY_ORDER:
        cell(ws, r, 1, name, align=CENTER)
        cell(ws, r, 2, val, align=CENTER, fmt="0.00")
        cell(ws, r, 3, note, font=NOTE_FONT, align=LEFTC)
        r += 1
    entry_last = r - 1
    LOOKUP = f"$A${entry_first}:$B${entry_last}"
    r += 1
    for name, rate, desc in (("buff·增加atk", BUFF_RATE["atk"], "B50｜每点 atk 记 0.75 费；仅限策略卡和支援卡的部署效果，增加数值的费用不得超过 3 费"),
                             ("buff·增加hp", BUFF_RATE["hp"], "B51｜每点 hp 记 0.5 费"),
                             ("buff·增加atk和hp", BUFF_RATE["both"], "B52｜每点 atk 和 hp 均记 0.6 费"),
                             ("buff 类用法", None, "buff 不是独立词条：写进「效果」栏，一次性增减数值、不设持续时间，不占 keywords 列")):
        cell(ws, r, 1, name, align=CENTER)
        cell(ws, r, 2, rate if rate is not None else "—", align=CENTER, fmt="0.00")
        cell(ws, r, 3, desc, font=NOTE_FONT, align=LEFTC)
        r += 1
    r += 1

    # 三、卡池平表
    r = section(ws, r, f"三、{dynasty}卡池（{len(rows)} 张）—— 列名与 CardData.cs 字段对齐；"
                       "「效果等价」列为脚本算好的等价费用（不参与 Excel 重算，改词条/效果后重跑生成脚本），"
                       "「部署价值 / 卡牌价值 / 差值」为活公式，改 I/J/L/M 会自动重算")
    for j, (h, w) in enumerate(CARD_COLS, start=1):
        cell(ws, r, j, h, font=H_FONT, fill=H_FILL, align=CENTER)
    ws.row_dimensions[r].height = 30
    r += 1
    data_first = r
    for idx, (cid, name, rar, kind, unit, wt, dcs, acs, ap, atk, hp, kws, eff, quote, src,
              sigma, pv, cv, d, effs_of) in enumerate(rows, start=1):
        kws_v = ",".join(kws) if kws else None
        rng = 2 if unit in ("弓兵", "支援") and kind == "兵牌" else (1 if kind == "兵牌" else None)
        vals = [idx, cid, name, dynasty, rar, kind, unit, wt, dcs, acs, ap, atk, hp, rng]
        for j, v in enumerate(vals, start=1):
            cell(ws, r, j, v, align=CENTER)
        cell(ws, r, 3, name, align=LEFT)
        cell(ws, r, 15, kws_v, align=LEFT)
        cell(ws, r, 16, ENUM_KW(kws), align=LEFT)
        cell(ws, r, 17, eff, font=NOTE_FONT, align=LEFTC)
        cell(ws, r, 18, quote, font=Font(size=9, italic=True, color="7B3F00"), align=LEFTC)
        cell(ws, r, 19, src, font=NOTE_FONT, align=LEFTC)
        # T 效果等价（脚本写死，Excel 侧不重算）
        #   策略牌 = Σ效果价（必须正好等于部署费用）；兵牌 = 闪击兵种特例 + Σ词条价 + Σ效果价
        #   之所以不让 Excel 重算：引擎对中文整串命中不稳定（直伤3 会误命中 随机直伤3 之类）；
        #   改 O 列词条或效果后重跑本脚本即可刷新 T 列。U~W 仍是活公式。
        sig = 0.0 if kind == "策略牌" else blitz_term(kind, unit, kws)
        total = sig + sum(ENTRY[k] for k in kws if k != "闪击") + sum(eff_cost(e) for e in effs_of)
        cell(ws, r, 20, round(total, 3), align=CENTER, fmt="0.000")
        cell(ws, r, 21, f"=ROUND((3-$I{r}/8)*$I{r}+2*$J{r}/3-1,3)", align=CENTER, fmt="0.000")
        # 卡牌价值：策略牌 = 部署费用（基础公式 B20「部署后产生的收益等于部署费用」）
        cell(ws, r, 22, f'=IF($F{r}="策略牌",$I{r},ROUND(VLOOKUP($G{r},{COEF},2,FALSE)*N($L{r})'
                        f'+VLOOKUP($G{r},{COEF},3,FALSE)*N($M{r})+$T{r},3))', align=CENTER, fmt="0.000")
        cell(ws, r, 23, f"=ROUND($V{r}-$U{r},3)", align=CENTER, fmt="+0.000;-0.000;0.000")
        r += 1
    data_last = r - 1

    # 数据有效性：程序侧枚举直接下拉
    dv = DataValidation(type="list", formula1='"兵牌,策略牌"', allow_blank=True)
    ws.add_data_validation(dv); dv.add(f"F{data_first}:F{data_last}")
    dv2 = DataValidation(type="list", formula1='"步兵,骑兵,弓兵,支援,—"', allow_blank=True)
    ws.add_data_validation(dv2); dv2.add(f"G{data_first}:G{data_last}")
    dv3 = DataValidation(type="list", formula1='"普通,稀有,史诗,传说"', allow_blank=True)
    ws.add_data_validation(dv3); dv3.add(f"E{data_first}:E{data_last}")
    dv4 = DataValidation(type="list", formula1='"轻,中,重,—"', allow_blank=True)
    ws.add_data_validation(dv4); dv4.add(f"H{data_first}:H{data_last}")

    rng = f"$W${data_first}:$W${data_last}"
    ws.conditional_formatting.add(rng, FormulaRule(
        formula=[f'AND($F{data_first}="兵牌",ABS($W{data_first})<=0.35)'],
        fill=PatternFill("solid", start_color="FFC6EFCE", end_color="FFC6EFCE"), stopIfTrue=False))
    ws.conditional_formatting.add(rng, FormulaRule(
        formula=[f'AND($F{data_first}="兵牌",ABS($W{data_first})>0.35)'],
        fill=PatternFill("solid", start_color="FFFFC7CE", end_color="FFFFC7CE"), stopIfTrue=False))
    ws.conditional_formatting.add(rng, FormulaRule(
        formula=[f'AND($F{data_first}="策略牌",ABS($W{data_first})<=1)'],
        fill=PatternFill("solid", start_color="FFC6EFCE", end_color="FFC6EFCE"), stopIfTrue=False))
    ws.conditional_formatting.add(rng, FormulaRule(
        formula=[f'AND($F{data_first}="策略牌",ABS($W{data_first})>1)'],
        fill=PatternFill("solid", start_color="FFFFEB9C", end_color="FFFFEB9C"), stopIfTrue=False))

    r += 1
    notes = [
        "字段口径（与 CardData.cs 一一对应）：cardId / 名称→cardName / 朝代→dynasty / 稀有度→rarity / cardType→cardType / "
        "unitType→unitType / 部署费用→deploymentCost / 行动费用→actionCost / 行动次数→actionCount / ATK→atk / HP→hp / keywords→keywords / 效果→effectText / 古文描述→flavorText。",
        "行动次数：非骑兵=1，骑兵=2，连战单位=2（可「攻击+攻击」），策略牌留空——由兵种/词条派生，不由策划手填。",
        "行动费用：按策划案 §2.5 兵种固有档位——步兵轻/中=1、步兵重=2；骑兵轻=1、骑兵中/重=2；弓兵轻/中=1、弓兵重=2；器械(支援)=2；策略牌=0。",
        "射程：弓兵与器械(支援)自带 2 排，其余 1 排；策略牌留空。射程与骑兵2AP 均属兵种自带，不计入词条等价费用。",
        "keywords 列用英文逗号分隔，只写程序 enum Keyword 内的词条；keywords(程序) 列为对应枚举名。示例：闪击,血战 → Blitz,BloodBattle。",
        "效果列只填「策略卡」与「有部署时/行动后效果」的兵牌；纯词条卡留空，效果由 keywords 与程序侧词条逻辑实现。",
        "血战=1.5、守护=1（携带者部署时+1HP，卡面HP已含）、闪击只适用于骑兵（携带0 / 未携带-0.5），均取自基础表。",
        "平衡判定：兵牌 |差值| ≤ 0.35（绿）；策略牌 |差值| ≤ 1.0（绿），1.0~2.0（黄）为词条离散档位导致的正常余量。",
    ]
    for n in notes:
        cell(ws, r, 1, "· " + n, font=NOTE_FONT, align=LEFT, border=False)
        ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=NCOL)
        ws.row_dimensions[r].height = 14
        r += 1
    ws.freeze_panes = ws.cell(data_first, 1)
    ws.sheet_properties.tabColor = tab
    return ws, data_first, data_last

def eval_rows(cards):
    """把卡池定义转成 build_pool 需要的行元组（19 项，末项是原始效果列表）"""
    out = []
    for c in cards:
        _cid, _n, _r, kind, unit, wt, dcs, atk, hp, kws, effs, _q, _s = c
        acs = acs_of(kind, unit, wt, kws)
        pv = cost_value(dcs, acs)
        sig = 0.0 if kind == "策略牌" else blitz_term(kind, unit, kws)
        sigma = round(sig + sum(ENTRY[k] for k in kws if k != "闪击") + sum(eff_cost(e) for e in (effs or [])), 3)
        cv = card_value(kind, unit, atk, hp, kws, effs, dcs)
        out.append((c[0], c[1], c[2], kind, unit, wt, dcs, acs, ap_of(kind, unit, kws),
                    atk, hp, kws, eff_col(c), c[11], c[12],
                    sigma, pv, cv, round(cv - pv, 3), effs or []))
    return out

ws_q, q_first, q_last = build_pool(
    wb, "秦·卡池", eval_rows(QIN),
    "秦朝卡池 · 赳赳老秦（按新列结构重建）", "秦", "8B1A1A")

ws_h, h_first, h_last = build_pool(
    wb, "汉·卡池", eval_rows(HAN),
    "汉朝卡池 · 大汉雄风（新建）", "汉", "1F3864")

# ============================================================ 五、基准表外条目（已全部拆除，只报告不改表）
# 说明：按「我基础表里没有的词条和数值，不允许存在」执行——以下条目一律不进任何卡牌，
#       只登记「曾经用过什么、现在为什么不能用、要补什么才能用」，等你决定是否补进《基础公式》。
REMOVED = [
    ("誓师", "支援(器械)部署增益",
     "部署当回合，己方所有已部署单位本回合 ATK+1",
     "基础表无「誓师」这一条；支援/器械类目前只有 buff 类费率可用",
     "本次两表都没有支援(器械)卡，所以这个形态暂时用不到；若你想保留「全军增益」，需要先定义「群体 buff 每点多少钱」——"
     "基础表只给了单体每点费率。"),
    ("临时ATK+3", "buff·单体加攻",
     "指定一个友方单位本回合 ATK+3",
     "基础表 B50 已给费率（每点 0.75 费），但旧表把它当成一个固定价词条写进了 keywords 列",
     "已改为效果栏写法，费率照 B50，不再占词条列。"),
    ("临时HP+3", "buff·单体加血",
     "为一个友方单位本回合 HP+3",
     "同上，B51 费率「每点 hp 记 0.5 费」",
     "本次未使用；若需要，直接按 0.5/点写进效果栏即可。"),
    ("指定直伤5", "直伤·指定5点",
     "对一名敌方兵牌造成5点伤害",
     "B60 只有「指定3点=2」一个锚点，5 点档没有基准价；旧表按 1点=1 外推成 4.0，属表外取值",
     "已撤掉：秦·绝粮道 改为「指定3点伤害(2.0) + 压制(1.0)」= 3 费，两个分量都在基础表里有价，正好等于费用。"),
    ("整排伤害3", "直伤·整排3点",
     "对指定一排所有单位造成3点伤害",
     "B64 只有「整排2点=3」一个锚点，3 点档属外推（旧表取 5.0）",
     "已撤掉：汉·决水灌城 改为「指定3点伤害(2.0) + 随机3点伤害(1.0)」= 3 费，两个分量都有基准价。"),
    ("攻击后大营回血2", "加血类·兵种卡",
     "攻击后，为大营回复2hp",
     "B41 确实有这一条（1.5 费），但它写在「加血类」里、备注明写「仅限兵种卡」，是兵牌的效果而不是可挂载词条",
     "已撤掉词条写法：汉·屯田卒 改为带「守护」的 3 费 4/2 中甲步兵（|差值| 0.292）。"
     "若你想让这张卡走「攻击后回血」路线，我按 B41 = 1.5 费重新配面板即可。"),
    ("重甲3", "重甲第三层",
     "每层重甲在受到伤害时减少一点（三层）",
     "B44 有这一条（4.0 费）",
     "基准价有、但本次没用上：5 费重型步兵的预算 12.208，带重甲3 需要面板降到约 3/3 才配得上，"
     "攻防比失衡（脚本会剔除）。→ 汉·虎贲营 改用 4/5 + 守护 + 重甲2（12.133，差值 −0.075），"
     "既保留「重甲营」的定位，也和 秦·锐士营（5/5 守护+重甲1）、汉·玄甲校士（4 费 重甲1）、汉·羽林壁垒（6 费 守护+重甲2）拉开档位。"
     "重甲3 留给后续朝代的 6 费以上重型单位。"),
    ("反制", "埋伏型解场牌（陷阱）",
     "（基础表 B45 只写了「满足触发条件时自动使用，不可主动使用，不消耗费用，起解场作用」，"
     "既没有触发条件也没有具体效果，等价费用一栏为空）",
     "定位已明确：反制是一类**陷阱/埋伏牌**——不能主动打出，只在触发条件满足时自动发动，不消耗费用，"
     "作用是在对方攻势落点上做一次突袭式解场（出奇制胜）。基准表里仍缺「触发条件」与「触发后效果」两项，"
     "因此无法定价，本次两表未使用。",
     "设计意图已记录，**暂不需要定价**。已确认的示例：「对方攻击己方大营的第一个单位，攻击后立即死亡」"
     "——触发条件 = 敌方单位攻击己方大营；触发效果 = 该攻击者攻击后立即死亡（等价于一次定向消灭）。"
     "将来若要落表，需要补：①触发条件的枚举（如：敌方攻击己方大营 / 敌方部署 ≥N 费单位 / 己方单位被消灭时）；"
     "②触发效果的基准锚点（本例「攻击后立即死亡」可参照 B63「消灭一个敌方随机单位」= 6 费，"
     "但因为是条件触发、不可主动选择时机，实际等价费用应低于 6；具体档位由你定，我不擅自填）。"),
]

if "遗漏词条定价" in wb.sheetnames:
    del wb["遗漏词条定价"]
if "基准表外条目" in wb.sheetnames:
    del wb["基准表外条目"]
wm = wb.create_sheet("基准表外条目", wb.sheetnames.index("汉·卡池") + 1)
for col, w in zip("ABCDE", (5, 18, 46, 46, 62)):
    wm.column_dimensions[col].width = w
r = 1
cell(wm, r, 1, "基准表外条目登记（只报告，不写入《基础公式》）", font=TITLE_FONT, border=False)
wm.merge_cells(start_row=r, start_column=1, end_row=r, end_column=5)
wm.row_dimensions[r].height = 26
r += 1
cell(wm, r, 1, "规则：两个卡池只允许使用《基础公式》B26:D64 里登记过的词条与效果。本表登记「曾经出现在旧表里、但基准表查不到」的条目，"
               "以及基准表自身留白/自相矛盾、需要你拍板的地方。任何基准表改动都等你确认后另行落表。",
     font=NOTE_FONT, align=LEFT, border=False)
wm.merge_cells(start_row=r, start_column=1, end_row=r, end_column=5)
r += 2

cell(wm, r, 1, "一、已从卡池中拆除的表外条目", font=SEC_FONT, fill=SEC_FILL, align=LEFT)
wm.merge_cells(start_row=r, start_column=1, end_row=r, end_column=5)
r += 1
for j, h in enumerate(["序号", "条目", "原用法", "为什么不能用 / 基准表状态", "现在的替代方案（已落表）"], start=1):
    cell(wm, r, j, h, font=H_FONT, fill=H_FILL, align=CENTER)
r += 1
for i, (name, kind, used, why, now) in enumerate(REMOVED, start=1):
    cell(wm, r, 1, i, align=CENTER)
    cell(wm, r, 2, name, align=CENTER)
    cell(wm, r, 3, f"{kind}｜{used}", font=NOTE_FONT, align=LEFTC)
    cell(wm, r, 4, why, font=NOTE_FONT, align=LEFTC)
    cell(wm, r, 5, now, font=NOTE_FONT, align=LEFTC)
    wm.row_dimensions[r].height = 58
    r += 1
r += 1

cell(wm, r, 1, "二、基准表里「有锚点但本次没用上」的档位（可能对后续朝代有用）", font=SEC_FONT, fill=SEC_FILL, align=LEFT)
wm.merge_cells(start_row=r, start_column=1, end_row=r, end_column=5)
r += 1
for j, h in enumerate(["序号", "条目", "基准价", "基准表位置", "说明"], start=1):
    cell(wm, r, j, h, font=H_FONT, fill=H_FILL, align=CENTER)
r += 1
UNUSED = [
    ("摸牌1 / 摸牌2 / 摸牌3", "1 / 3 / 6", "B32:B34", "抽牌档。本次只用到 摸牌1（汉·屯田、汉·离间）和 摸牌3（秦·移民实边）；摸牌2 未使用"),
    ("建筑回血3", "1", "B37", "本次只用到 建筑回血5（汉·屯田、汉·盐铁论）与 建筑回血7（秦·商君变法）"),
    ("兵回血3", "2", "B40", "本次用到：秦·输粟（为友方单位回3血）、汉·盐铁论"),
    ("攻击后大营回血2", "1.5", "B41", "仅限兵种卡。本次未使用（屯田卒 改走守护路线）"),
    ("重甲3", "4.0", "B44", "见上表：5 费重型步兵预算容纳不下，留给后续朝代的 6 费以上重型单位"),
    ("抵抗", "0.5", "B49", "免疫压制。本次兵牌未携带（重甲3 未使用，所以连带未出现）"),
    ("等价·ATK对齐HP", "3", "B53", "固定 3 费策略卡。本次未使用"),
    ("等价·敌HP调1", "2", "B54", "固定 2 费策略卡。本次未使用"),
    ("等价·敌ATK调1", "2", "B55", "固定 2 费策略卡。本次未使用"),
    ("召唤(牌堆)", "0.5", "B35", "词条。本次用到：汉·汉家大黄弩（部署时添加一张卡牌进入牌堆）"),
    ("召唤(手牌)", "1", "B36", "部署时从牌堆取一张入手。本次未使用"),
    ("消灭随机单位", "6", "B63", "消灭一个敌方随机单位。本次未使用（6 费策略卡位被 秦·移民实边 的抽3张占用）"),
    ("整排伤害2", "3", "B64", "本次用到：汉·飞石"),
]
for i, (name, price, loc, note) in enumerate(UNUSED, start=1):
    cell(wm, r, 1, i, align=CENTER)
    cell(wm, r, 2, name, align=LEFT)
    cell(wm, r, 3, price, align=CENTER)
    cell(wm, r, 4, loc, align=CENTER)
    cell(wm, r, 5, note, font=NOTE_FONT, align=LEFTC)
    wm.row_dimensions[r].height = 30
    r += 1
r += 1

cell(wm, r, 1, "三、基准表内部留白与冲突（只报告，未修改任何基准表单元格）", font=SEC_FONT, fill=SEC_FILL, align=LEFT)
wm.merge_cells(start_row=r, start_column=1, end_row=r, end_column=5)
r += 1
conflicts = [
    "反制（B45）：有名字、有效果描述、无触发条件、等价费用一栏为空 —— 基础表里唯一无法计价的条目。"
    "定位已明确为「埋伏型解场牌／陷阱」（不可主动使用、触发即发动、不耗费用、出奇制胜），"
    "已记录的示例是「对方攻击己方大营的第一个单位，攻击后立即死亡」。"
    "仍缺「触发条件」与「触发后效果」两项，故无法定价、暂不落表；若将来补档，档位由你定，脚本不擅自填价。",
    "守护部署加成：B29 备注写「部署时+1hp」，策划案 §4.3 写 +3HP。→ 本次按你确认取 +1HP（卡面 HP 已含）。",
    "血战等价费用：B30 = 1.5，策划案 §4.3 写 2.0。→ 本次按你确认取 1.5。",
    "闪击适用范围：B27 备注「骑兵类携带时等价为0，未携带算入-0.5」，没写步/弓/器怎么办。→ 本次按你确认：只对骑兵适用，步/弓/器不受影响。",
    "直伤类（B60~B64）：「指定3点=2」「随机3点=1」「整排2点=3」「消灭随机=6」四个档位彼此不成线性，"
    "1/2/4/5/7 点与整排 3 点都无锚点，因此这些档位本次一律不用。",
    "弃牌类（B56）：弃 1 张手牌固定 2 费，单独无法撑满 2 费以上的策略卡，因此必然要搭配第二个效果使用（秦·军功爵、汉·离间 都是「弃牌 + 另一效果」）。",
    "buff 类（B50~B52）只给了「每点」费率，没有给「群体增益」的费率上限；"
    "备注只限定「仅限策略卡和支援卡的部署效果，增加数值的费用不得超过 3 费」。→ 本次全部按单点费率计价，"
    "单张 buff 费用最高 2.0 费（汉·破敌封赏 HP+4、秦·军功爵 ATK+2），未触 3 费上限。",
    "★支援(器械)类：已按你更新后的系数（E12:F12 = 1/1）重做。旧系数 2/2 时 3 费器械「面板+最小效果」最低 8.5，"
    "而门槛上限是 8.558 —— 只剩 0.25 余量、比基础表最小的效果价 0.5 还小，支援卡根本无法成立；改成 1/1 后预算与面板同量纲，"
    "3 费 3/4 +「本回合HP+3」（B51 每点 hp 记 0.5 费 = 1.5 费）正好落在预算上（8.5 vs 8.208，差值 +0.292）。"
    "秦·连弩台、汉·治粟都尉 即此配置。注意：支援(器械)现按 2 次行动计（与骑兵同级——器械架设后可连续输出）。",
    "费用曲线：策略牌的「效果价 = 费用」是硬约束，而基础表的效果价档位只有 0.5 / 1 / 1.5 / 2 / 2.5 / 3 / 4 / 6，"
    "因此不是每个费用点都能配出策略卡（5 费只有「整排2点(3) + 兵回血3(2)」一类组合，6 费只有「摸牌3」「消灭随机」）。"
    "1 费档本次用「随机直伤3（B61 = 1 费）」做成了 秦·弩矢督 与 汉·招降。",
    "B47~B59 之间有留白行（47/57/58/59 附近的空行），未来若补新条目，补进去后本脚本会自动可用（改 ENTRY / EFFECT_TEXT 即可）。",
]
for n in conflicts:
    cell(wm, r, 1, "· " + n, font=NOTE_FONT, align=LEFT, border=False)
    wm.merge_cells(start_row=r, start_column=1, end_row=r, end_column=5)
    wm.row_dimensions[r].height = 14
    r += 1
wm.sheet_properties.tabColor = "BF8F00"

# ============================================================ 六、卡池总览
if "卡池总览" in wb.sheetnames:
    del wb["卡池总览"]
wo = wb.create_sheet("卡池总览", 0)

def stats(cards, kind_filter=None):
    return [c for c in cards if kind_filter is None or c[3] == kind_filter]

def report(cards):
    """返回 (兵牌最差|差值|, 该卡门槛, 该卡 cardId, 策略牌「效果价−费用」最大偏差, 策略牌总数, 各费用张数)

    兵牌的「最差」按各自门槛归一化挑选，这样 1 费卡的放宽档（0.6）不会掩盖真正的越界卡。
    策略牌按新口径「卡牌价值 = 部署费用」，唯一要守的硬约束是 Σ效果价 必须正好等于费用。"""
    ds = []
    for c in cards:
        acs = acs_of(c[3], c[4], c[5], c[9])
        if c[3] == "策略牌":
            cv = c[6]                      # 卡牌价值 = 部署费用
            sigma = c[6]                   # 占位，下面用列表推导单独算
        else:
            cv = card_value(c[3], c[4], c[7], c[8], c[9], c[10], c[6])
        pv = cost_value(c[6], acs)
        ds.append((c[3], round(cv - pv, 3), cv, pv, c[0], c[6]))
    unit = sorted(((abs(d) / tolerance("兵牌", dcs), abs(d), cid, tolerance("兵牌", dcs))
                   for k, d, _, _, cid, dcs in ds if k == "兵牌"), reverse=True)
    u, uw, ut = (unit[0][1], unit[0][2], unit[0][3]) if unit else (0, "-", 0.35)
    strat = [c for c in cards if c[3] == "策略牌"]
    gap = max([abs(effect_sum(c) - c[6]) for c in strat], default=0)
    return round(u, 3), round(ut, 2), uw, round(gap, 3), len(strat), [c[6] for c in strat]

qu, qut, quw, qgap, qn, qcosts = report(QIN)
hu, hut, huw, hgap, hn, hcosts = report(HAN)

for col, w in zip("ABCDEFG", (5, 16, 10, 10, 10, 10, 46)):
    wo.column_dimensions[col].width = w
r = 1
cell(wo, r, 1, "千秋策 · 朝代卡池总览", font=TITLE_FONT, border=False)
wo.merge_cells(start_row=r, start_column=1, end_row=r, end_column=7)
wo.row_dimensions[r].height = 26
r += 1
cell(wo, r, 1, "数值基准：《部署价值表》E3 ＋《基础公式》B20（含「策略卡：部署后产生的收益等于部署费用」）。本次未修改任何基准表单元格；"
               "两个卡池只使用《基础公式》B26:D64 里登记过的词条与效果，buff 类写进效果栏、按费率计价。",
     font=NOTE_FONT, align=LEFT, border=False)
wo.merge_cells(start_row=r, start_column=1, end_row=r, end_column=7)
r += 2

r = (lambda rr: (cell(wo, rr, 1, "一、卡池规模与平衡偏差", font=SEC_FONT, fill=SEC_FILL, align=LEFT),
                 wo.merge_cells(start_row=rr, start_column=1, end_row=rr, end_column=7), rr + 1)[2])(r)
for j, h in enumerate(["", "总张数", "兵牌", "策略牌", "普通", "稀有", "史诗/传说"], start=1):
    cell(wo, r, j, h, font=H_FONT, fill=H_FILL, align=CENTER)
r += 1
for label, cards in (("秦·赳赳老秦", QIN), ("汉·大汉雄风", HAN)):
    cell(wo, r, 1, label, align=LEFT)
    cell(wo, r, 2, len(cards), align=CENTER)
    cell(wo, r, 3, sum(1 for c in cards if c[3] == "兵牌"), align=CENTER)
    cell(wo, r, 4, sum(1 for c in cards if c[3] == "策略牌"), align=CENTER)
    cell(wo, r, 5, sum(1 for c in cards if c[2] == "普通"), align=CENTER)
    cell(wo, r, 6, sum(1 for c in cards if c[2] == "稀有"), align=CENTER)
    cell(wo, r, 7, f'{sum(1 for c in cards if c[2] == "史诗")} / {sum(1 for c in cards if c[2] == "传说")}', align=CENTER)
    r += 1
r += 1
cell(wo, r, 1, "校验项", font=H_FONT, fill=H_FILL, align=CENTER)
cell(wo, r, 2, "兵牌最差一类（按门槛归一化）", font=H_FONT, fill=H_FILL, align=CENTER)
cell(wo, r, 3, "兵牌越界张数", font=H_FONT, fill=H_FILL, align=CENTER)
cell(wo, r, 4, "策略牌「Σ效果价 − 费用」最大偏差", font=H_FONT, fill=H_FILL, align=CENTER)
cell(wo, r, 5, "判定口径", font=H_FONT, fill=H_FILL, align=CENTER)
wo.merge_cells(start_row=r, start_column=5, end_row=r, end_column=7)
r += 1
for label, u, ut, uw, gap, cards in (("秦", qu, qut, quw, qgap, QIN), ("汉", hu, hut, huw, hgap, HAN)):
    over = sum(1 for c in cards if c[3] == "兵牌"
               and abs(card_value(c[3], c[4], c[7], c[8], c[9], c[10], c[6]) - cost_value(c[6], acs_of(c[3], c[4], c[5], c[9]))) > tolerance("兵牌", c[6]))
    cell(wo, r, 1, f"{label}表", font=Font(bold=True), align=LEFT)
    cell(wo, r, 2, f"{u:.3f}（{uw}，门槛 {ut:.2f}）", align=CENTER)
    cell(wo, r, 3, over, align=CENTER)
    cell(wo, r, 4, gap, align=CENTER, fmt="0.000")
    cell(wo, r, 5, "兵牌：|卡牌价值−部署价值|≤0.35（1费放宽到0.6）；策略牌：卡牌价值=部署费用，Σ效果价必须正好等于费用", font=NOTE_FONT, align=LEFTC)
    wo.merge_cells(start_row=r, start_column=5, end_row=r, end_column=7)
    r += 1
r += 1

r = (lambda rr: (cell(wo, rr, 1, "二、费用分布（策划案 §4.5 建议：1~2费 10~12 张 / 3~4费 12~14 张 / 5费+ 4~6 张）", font=SEC_FONT, fill=SEC_FILL, align=LEFT),
                 wo.merge_cells(start_row=rr, start_column=1, end_row=rr, end_column=7), rr + 1)[2])(r)
for j, h in enumerate(["", "1费", "2费", "3费", "4费", "5费", "6费+"], start=1):
    cell(wo, r, j, h, font=H_FONT, fill=H_FILL, align=CENTER)
r += 1
for label, cards, n in (("秦", QIN, "秦·卡池"), ("汉", HAN, "汉·卡池")):
    cell(wo, r, 1, label, align=LEFT)
    for j, dcs in enumerate((1, 2, 3, 4, 5, 6), start=2):
        if dcs == 6:
            cell(wo, r, j, f'=COUNTIF({n}!$I$5:$I$200,">=6")', align=CENTER)
        else:
            cell(wo, r, j, f'=COUNTIF({n}!$I$5:$I$200,{dcs})', align=CENTER)
    r += 1
r += 1
for label, costs in (("秦", qcosts), ("汉", hcosts)):
    cell(wo, r, 1, f"{label}策略牌费用", align=LEFT)
    cell(wo, r, 2, "、".join(str(x) for x in sorted(costs)), font=NOTE_FONT, align=LEFTC)
    wo.merge_cells(start_row=r, start_column=2, end_row=r, end_column=7)
    r += 1
r += 1

r = (lambda rr: (cell(wo, rr, 1, "三、本次相对旧《赳赳老秦》表的改动（旧表保留在原地未动，可对照）", font=SEC_FONT, fill=SEC_FILL, align=LEFT),
                 wo.merge_cells(start_row=rr, start_column=1, end_row=rr, end_column=7), rr + 1)[2])(r)
changes = [
    "列结构：去掉「定位」列（对程序无用）；新增「行动费用」「射程」「keywords(程序)」列，与 CardData.cs 字段一一对齐；"
    "「词条等价」改名为「效果等价」（同时容纳词条价与效果价）。",
    "词条白名单：两个卡池只使用《基础公式》B26:D64 登记过的条目；原先自造的 誓师、临时ATK+3、指定直伤5、整排伤害3、攻击后大营回血2 等表外条目已全部拆除。",
    "buff 类：不再作为独立词条，改写入「效果」栏，按基础表 B50/B51/B52 的费率计价（atk 0.75/点、hp 0.5/点、atk+hp 各 0.6/点），一次性增减数值、无持续时间。",
    "策略牌价值：按《基础公式》B20「策略卡：部署后产生的收益等于部署费用」，卡牌价值 V 直接等于部署费用 I——"
    "既不套兵牌的 β·atk+α·hp 公式，也不等于 Σ效果价。因此策略卡一行写的是「部署价值 = 卡牌价值 = 部署费用」；"
    "W 列的负值只是 cost_value 把费用换算成面板的参考值，对策略卡没有意义。Σ效果价 是配平时的硬约束：必须恰等于费用。",
    "策略卡不再带词条：抽牌/回血/直伤/压制/弃牌/等价类/召唤/buff 全部是「效果」，keywords 列留空。",
    "汉·汉家大黄弩（部署召唤，词条列写「召唤(牌堆)」）。",
    "兵牌效果栏：除两张支援卡（秦·连弩台、汉·治粟都尉，按 B50「仅限策略卡和支援卡的部署效果」写 buff）外，"
    "其余兵牌的增益一律做成词条（守护/血战/连战/重甲/闪击/伏兵/召唤），不带效果、效果价 0。",
    "闪击：按你确认只对骑兵适用（骑兵携带=0 / 骑兵未携带 −0.5），步/弓/器不受该条影响。",
    "血战按基础表 1.5（不是策划案的 2.0）；守护携带者部署时 +1HP 已含在卡面 HP 里。",
    "行动费用按策划案 §2.5 档位表（步兵轻中=1/重=2；骑兵轻=1/中重=2；弓兵轻中=1/重=2；器械=2）。"
    "行动次数：骑兵为 2、携带连战者为 2、其余一律为 1——器械每回合只能攻击一次。行动次数只影响每回合能打几下，不进入部署价值公式（部署价值只取部署费用 dcs 与行动费用 acs）。射程：弓兵与器械为 2、其余为 1。",
    "支援(器械)类：本次两表各 1 张（秦·连弩台、汉·治粟都尉，3 费·重·3/4·2acs·2AP·射程2）——"
    "按你更新后的 E12:F12 = 1/1 系数、且行动次数 = 1（每回合一次攻击）重做；旧系数 2/2 时该类别无法成立，详见《基准表外条目》「三、」。",
    "「效果等价」列由脚本算好后写死（不参与 Excel 重算）；「部署价值 / 卡牌价值 / 差值」是活公式，改 I/J/L/M 会自动重算，"
    "改了 O 列词条或效果栏后重跑 build_dynasty_sheets.py 让「效果等价」列同步。",
]
for n in changes:
    cell(wo, r, 1, "· " + n, font=NOTE_FONT, align=LEFT, border=False)
    wo.merge_cells(start_row=r, start_column=1, end_row=r, end_column=7)
    wo.row_dimensions[r].height = 14
    r += 1
wo.sheet_properties.tabColor = "375623"

wb.save(SRC)
print("已保存:", SRC)
print("工作表顺序:", wb.sheetnames)

# ============================================================ 六、同步策划案 §4.4 卡池表
# 策划案里的卡池表是「以数据表为准」的派生物，因此由脚本直接把两个卡池写进标记区，
# 避免手改的表格与数据表再次脱节。
DOC = "Docs\千秋策策划案.md"
POOL_BEGIN, POOL_END = "<!-- POOL:START -->", "<!-- POOL:END -->"

def _cell(v):
    """markdown 表格单元格：转义竖线、空值写 —"""
    if v is None or v == "":
        return "—"
    return str(v).replace("|", "\\|").replace("\n", " ")

def pool_table(title, rows):
    out = [f"#### {title}", "",
           "| cardId | 名称 | 稀有度 | 类型 | 兵种 | 重量 | 部署费用 | 行动费用 | 行动次数 | 射程 | ATK | HP | 词条 | 效果 | 古文描述 | 出处 |",
           "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for x in rows:
        (_cid, name, rar, kind, unit, wt, dcs, acs, ap, atk, hp, kws, efftxt,
         quote, src, _sigma, _pv, _cv, _d, _effs) = x
        out.append("| " + " | ".join(_cell(v) for v in (
            x[0], name, rar, kind, unit, wt, dcs, acs, ap,
            "2" if (unit in ("弓兵", "支援") and kind == "兵牌") else ("1" if kind == "兵牌" else None),
            atk, hp, "、".join(kws) if kws else "—", efftxt, quote, src)) + " |")
    return out

if os.path.exists(DOC):
    with open(DOC, "rb") as fh:
        doc = fh.read().decode("utf-8")
    if POOL_BEGIN in doc and POOL_END in doc:
        head, rest = doc.split(POOL_BEGIN, 1)
        _, tail = rest.split(POOL_END, 1)
        body = pool_table("秦朝卡池（Qin，24 张）", eval_rows(QIN)) + [""] \
             + pool_table("汉朝卡池（Han，24 张）", eval_rows(HAN))
        # 该 markdown 全文用 CRLF，这里必须跟着用 CRLF，否则会混入裸 LF
        doc = head + POOL_BEGIN + "\r\n" + "\r\n".join(body) + "\r\n" + POOL_END + tail
        with open(DOC, "wb") as fh:
            fh.write(doc.encode("utf-8"))
        print(f"已同步 {DOC} §4.4 卡池表（48 张）")
    else:
        print(f"!! {DOC} 里找不到 {POOL_BEGIN}/{POOL_END} 标记区，跳过同步")
else:
    print(f"!! 未找到 {DOC}，跳过同步")
