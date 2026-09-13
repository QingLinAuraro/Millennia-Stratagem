# -*- coding: utf-8 -*-
"""
在 数据表demo.xlsx 的第三张表「赳赳老秦」中设计秦朝卡组。
只写入第三个工作表；其他工作表单元格内容保持原样。
数值口径完全来自既有工作表：
  部署价值 cost_value = (3 - dcs/8)*dcs + 2*acs/3 - 1        （部署价值表 E3）
  卡牌价值 card_value = beta*atk + alpha*hp + Σ词条等价费用   （基础公式 B20）
  beta/alpha 取 基础公式!E9:F12；词条等价费用取 基础公式!D24:D32
"""
import shutil
from openpyxl import load_workbook
from openpyxl.styles import Font, PatternFill, Alignment, Border, Side
from openpyxl.formatting.rule import FormulaRule
from openpyxl.utils import get_column_letter

SRC = r"Docs\数据表demo.xlsx"
SHEET = "赳赳老秦"

# ---------------------------------------------------------------- 数值口径
BETA = {"步兵": (1.1, 0.9), "骑兵": (0.9, 1.1), "弓兵": (1.2, 0.8), "支援": (2.0, 2.0)}
ENTRY = {  # 词条 / 效果 -> 等价费用
    "伏兵": 0.5, "守护": 1.0, "血战": 2.0, "连战": 1.0,
    "摸牌1": 1.0, "摸牌2": 3.0, "摸牌3": 6.0,
    "召唤(手牌)": 1.0, "召唤(牌堆)": 0.5,
    "回血1": 1.0, "回血2": 2.0, "回血3": 3.0, "回血5": 5.0, "回血7": 7.0,
    "重甲1": 1.0, "重甲2": 2.0,
    "誓师": 2.0,  # 支援类通用：部署当回合，己方所有已部署单位本回合 ATK+1（表外新增）
    # 直伤按“1 点 = 1 价值”（与回血对称）：1 费可造成 2 点伤害或回复 2 点 HP；多费按需放大
    "伤害1": 1.0, "伤害2": 2.0, "伤害3": 3.0, "伤害4": 4.0, "伤害5": 5.0, "伤害7": 7.0,
    "弃手牌1": 1.0, "临时ATK+3": 1.5,
}

def cost_value(dcs, acs):
    return round((3 - dcs / 8) * dcs + 2 * acs / 3 - 1, 3)

def card_value(kind, unit, atk, hp, entries, has_blitz):
    """kind: 兵牌/策略牌; unit: 步兵/骑兵/弓兵/支援"""
    if kind == "策略牌":
        s = 0.0
    else:
        # 闪击口径：骑兵携带=0；其他兵种携带=+0.5；未携带=-0.5
        s = 0.0 if has_blitz and unit == "骑兵" else (0.5 if has_blitz else -0.5)
    for e in entries:
        if e and e != "闪击":
            s += ENTRY[e]
    if kind == "策略牌":
        return round(s, 3)
    b, a = BETA[unit]
    return round(b * atk + a * hp + s, 3)

# ---------------------------------------------------------------- 卡池数据
# (序号, cardId, 名称, 稀有度, 类型, 兵种, 重量, 费用, 行动力, ATK, HP, 词条1, 词条2, 词条3, 定位, 效果, 古文, 出处)
CARDS = [
    # ---- 普通（8）----
    ("qin_001", "什伍卒", "普通", "兵牌", "步兵", "轻", 1, 1, 1, 2, None, None, None,
     "1费铺场", "无特殊效果。什伍相保，同伍连坐，退无可退。",
     "令民为什伍，而相牧司连坐。", "《史记·商君列传》"),
    ("qin_002", "戍卒", "普通", "兵牌", "步兵", "中", 2, 1, 2, 4, None, None, None,
     "2费中坚", "无特殊效果。戍边之卒，久历行伍。",
     "发闾左適戍渔阳九百人，屯大泽乡。", "《史记·陈涉世家》"),
    ("qin_003", "秦锐士", "普通", "兵牌", "步兵", "中", 3, 1, 4, 4, None, None, None,
     "3费核心", "无特殊效果。锐士之选，披坚执锐，进退有节。",
     "魏氏之武卒，不可以遇秦之锐士。", "《荀子·议兵》"),
    ("qin_004", "劲弩手", "普通", "兵牌", "弓兵", "轻", 2, 1, 2, 4, None, None, None,
     "2费后排（射程2）", "弓兵类自带射程2；攻击时不受反击，被攻击时也不反击。",
     "秦带甲百余万，车千乘，骑万匹。", "《史记·张仪列传》"),
    ("qin_005", "连弩台", "普通", "兵牌", "支援", "重", 3, 1, 1, 2, "誓师", None, None,
     "3费增益支援", "誓师（部署当回合：己方所有已部署单位本回合 ATK+1）。器械（支援）定位＝部署增益型，自带射程2、面板较低，价值在增益；受反击时不反击。",
     "备高临以连弩之车；鼓之舞之，三军奋击。", "《墨子·备高临》(化用)"),
    ("qin_006", "疾驰轻骑", "普通", "兵牌", "骑兵", "轻", 2, 2, 3, 3, "闪击", None, None,
     "2费机动", "闪击（部署当回合即可移动或攻击）；骑兵行动力2。",
     "车骑之精，轻驰如风，掠野奔袭。", "《孙膑兵法·八阵》(化用)"),
    ("qin_007", "输粟", "普通", "策略牌", "—", "—", 1, 0, None, None, "回血2", None, None,
     "1费修缮/续航", "为一座己方建筑或一个友方单位回复2点HP。",
     "入禾仓，万石一积。", "睡虎地秦墓竹简·仓律"),
    ("qin_008", "攒射", "普通", "策略牌", "—", "—", 1, 0, None, None, "伤害2", None, None,
     "1费直伤", "对一名敌方单位或建筑造成2点伤害。",
     "劲弩攒发，矢如飞蝗。", "《墨子·备城门》(化用)"),
    # ---- 稀有（8）----
    ("qin_009", "斥候骑", "稀有", "兵牌", "骑兵", "轻", 2, 2, 2, 3, "闪击", "伏兵", None,
     "2费伏击", "闪击（部署当回合即可行动）＋伏兵（主动攻击前不可被攻击）；骑兵行动力2。",
     "斥候远窥，昼伏夜驰，以探敌情。", "《武经总要》(化用)"),
    ("qin_010", "轻车", "稀有", "兵牌", "骑兵", "轻", 3, 2, 4, 4, "闪击", None, None,
     "3费突击", "闪击（部署当回合即可移动或攻击）；骑兵行动力2。",
     "小戎俴收，五楘梁辀。", "《诗经·秦风·小戎》"),
    ("qin_011", "穿杨弩手", "稀有", "兵牌", "弓兵", "轻", 3, 1, 4, 4, None, None, None,
     "3费神射（射程2）", "弓兵自带射程2；攻击不受反击。矢出穿杨，百不失一。",
     "去柳叶百步而射之，百发百中。", "《战国策·西周策》(化用)"),
    ("qin_012", "铁鹰剑士", "稀有", "兵牌", "步兵", "重", 4, 1, 5, 4, "重甲1", None, None,
     "4费重装前排", "重甲1（每次受到伤害减1）；仅重型单位可携带。",
     "王于兴师，修我甲兵，与子偕行。", "《诗经·秦风·无衣》"),
    ("qin_013", "陷阵士", "稀有", "兵牌", "步兵", "中", 4, 1, 4, 4, "血战", None, None,
     "4费换血续航", "血战（主动攻击后回复2HP；被反击时不回复）。",
     "选锐冲之，分兵继之，急击勿疑。", "《吴子·料敌》"),
    ("qin_014", "飞羽轻骑", "稀有", "兵牌", "骑兵", "轻", 4, 2, 5, 5, "闪击", None, None,
     "4费骁骑", "闪击；骑兵行动力2。铁骑纵横，乘胜逐北。",
     "追亡逐北，伏尸百万，流血漂橹。", "贾谊《过秦论》(化用)"),
    ("qin_015", "军功爵", "稀有", "策略牌", "—", "—", 2, 0, None, None, "临时ATK+3", "回血3", None,
     "2费增益", "指定一个友方单位本回合ATK+3，并为其回复3点HP。",
     "能得甲首一者，赏爵一级，益田一顷，益宅九亩。", "《商君书·境内》"),
    ("qin_016", "反间", "稀有", "策略牌", "—", "—", 2, 0, None, None, "弃手牌1", "摸牌2", None,
     "2费干扰/过牌", "随机弃置敌方1张手牌，然后你抽2张牌。",
     "秦多与赵王宠臣郭开金，为反间。", "《史记·廉颇蔺相如列传》"),
    # ---- 史诗（5）----
    ("qin_017", "连弩士", "史诗", "兵牌", "弓兵", "中", 3, 2, 4, 4, "连战", None, None,
     "3费连击", "连战（同一回合可连续攻击两次，行动力记为2）；射程2。",
     "自以连弩候大鱼出射之。", "《史记·秦始皇本纪》"),
    ("qin_018", "绝粮道", "史诗", "策略牌", "—", "—", 3, 0, None, None, "伤害7", None, None,
     "3费拆建筑/解场", "对目标建筑造成7点伤害；若目标为兵牌，改为造成4点伤害。",
     "又分其兵，绝其粮道，赵军乏食而乱。", "《史记》(化用)"),
    ("qin_019", "锐士营", "史诗", "兵牌", "步兵", "重", 5, 1, 5, 5, "守护", "重甲1", None,
     "5费站场核心", "守护（同排相邻槽位的友方单位与建筑不可被选中；守护单位之间不互相保护；部署时+3HP已计入词条价值）＋重甲1。",
     "秦性强，其地险，其政严，其赏罚信，其人不让，皆有斗心。", "《吴子·料敌》"),
    ("qin_020", "商君变法", "史诗", "策略牌", "—", "—", 4, 0, None, None, "摸牌3", "回血3", None,
     "4费强力过牌", "抽3张牌，并为一座己方建筑回复3点HP。",
     "令既具，未布，恐民之不信，已乃立三丈之木于国都市南门。", "《史记·商君列传》"),
    ("qin_021", "移民实边", "史诗", "策略牌", "—", "—", 5, 0, None, None, "摸牌3", "回血5", None,
     "5费经济/续航", "抽3张牌，并为一座己方建筑或一个友方单位回复5点HP。",
     "明赏罚，劝耕战，民勇于公战，怯于私斗。", "《史记·商君列传》(化用)"),
    # ---- 传说（3）----
    ("qin_022", "玄甲铁骑", "传说", "兵牌", "骑兵", "中", 6, 2, 6, 6, "闪击", "血战", None,
     "6费终结", "闪击＋血战（主动攻击后回复2HP，被反击时不回复）；骑兵行动力2。玄甲锐骑，追亡逐北，所向无前。",
     "岂曰无衣？与子同裳。王于兴师，修我甲兵。", "《诗经·秦风·无衣》"),
    ("qin_023", "函谷铁卫", "传说", "兵牌", "步兵", "重", 6, 1, 5, 6, "守护", "重甲2", None,
     "6费守护核心", "守护＋重甲2（每次受到伤害减2）。坚壁不出，以逸待劳。",
     "深沟高垒，分兵固守，使敌欲战不得。", "《吴子·应变》(化用)"),
    ("qin_024", "蹶张神弩", "传说", "兵牌", "弓兵", "重", 6, 1, 6, 8, None, None, None,
     "6费神射", "弓兵自带射程2；攻击不受反击。蹶张之弩，势若崩雷，一矢中的。",
     "弩出于蹶张，射远命中，所当皆靡。", "《武经总要》(化用)"),
]

# 校验
print("=" * 96)
print(f"{'id':<9}{'名称':<12}{'稀有':<5}{'兵种':<5}{'费':>3}{'AP':>3}{'ATK':>4}{'HP':>4}  {'Σ词条':>6}{'部署价值':>9}{'卡牌价值':>9}{'差值':>7}")
worst = 0.0
for c in CARDS:
    cid, name, rar, kind, unit, wt, dcs, acs, atk, hp, e1, e2, e3 = c[:13]
    entries = [e for e in (e1, e2, e3) if e]
    has_blitz = "闪击" in entries
    sig = 0.0 if kind == "策略牌" else (0.0 if (has_blitz and unit == "骑兵") else (0.5 if has_blitz else -0.5))
    sigma = sig + sum(ENTRY[e] for e in entries if e != "闪击")
    cv = card_value(kind, unit, atk or 0, hp or 0, entries, has_blitz)
    pv = cost_value(dcs, acs)
    d = round(cv - pv, 3)
    worst = max(worst, abs(d))
    print(f"{cid:<9}{name:<12}{rar:<5}{unit:<5}{dcs:>3}{acs:>3}{(atk or 0):>4}{(hp or 0):>4}  {sigma:>6.2f}{pv:>9.3f}{cv:>9.3f}{d:>+7.3f}")
print("=" * 96)
print("最大偏差绝对值 =", round(worst, 3))

# 卡组构筑示例
DECK = [
    ("什伍卒", "普通", 1, 4, "普通配额"), ("戍卒", "普通", 2, 3, "普通配额"),
    ("劲弩手", "普通", 2, 2, "普通配额"), ("秦锐士", "普通", 3, 2, "普通配额"),
    ("疾驰轻骑", "普通", 2, 1, "普通配额"),
    ("攒射", "普通", 1, 1, "任意配额"), ("输粟", "普通", 1, 1, "任意配额"),
    ("斥候骑", "稀有", 2, 2, "稀有配额"), ("轻车", "稀有", 3, 3, "稀有配额"),
    ("铁鹰剑士", "稀有", 4, 2, "稀有配额"), ("穿杨弩手", "稀有", 3, 1, "稀有配额"),
    ("军功爵", "稀有", 2, 1, "任意配额"),
    ("连弩士", "史诗", 3, 2, "史诗配额"), ("锐士营", "史诗", 5, 2, "史诗配额"),
    ("商君变法", "史诗", 4, 1, "任意配额"),
    ("玄甲铁骑", "传说", 6, 1, "传说配额"), ("蹶张神弩", "传说", 6, 1, "传说配额"),
]
tot = sum(d[3] for d in DECK)
print("卡组总数 =", tot, "总CP =", sum(d[2] * d[3] for d in DECK))
for lo, hi in ((1, 2), (3, 4), (5, 9)):
    print(f"  {lo}~{hi}费:", sum(d[3] for d in DECK if lo <= d[2] <= hi))
for rar in ("普通", "稀有", "史诗", "传说"):
    print(f"  {rar}:", sum(d[3] for d in DECK if d[1] == rar))

# ---------------------------------------------------------------- 写入工作簿
import os
if not os.path.exists(SRC + ".bak"):        # 仅首次运行保留原始备份
    shutil.copyfile(SRC, SRC + ".bak")
wb = load_workbook(SRC)
ws = wb[SHEET]

# 每次全量重排；先清除上一版遗留的合并区，避免行数变化后与旧合并区冲突（MergedCell 只读）。
for _mr in list(ws.merged_cells.ranges):
    ws.unmerge_cells(str(_mr))

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

widths = {"A": 5, "B": 11, "C": 15, "D": 8, "E": 8, "F": 7, "G": 7, "H": 6, "I": 7,
          "J": 5, "K": 5, "L": 11, "M": 11, "N": 11, "O": 9, "P": 10, "Q": 10, "R": 8,
          "S": 15, "T": 46, "U": 46, "V": 26}
for col, w in widths.items():
    ws.column_dimensions[col].width = w

r = 1
ws.cell(r, 1, "秦朝卡池 · 赳赳老秦（Qin Card Set）").font = TITLE_FONT
ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=22)
ws.row_dimensions[r].height = 26
r += 1
ws.cell(r, 1, "设计依据：《千秋策》策划案 §4.3 词条 / §4.5 费用曲线 / §5 卡组规则 ＋ 数据表《基础公式》《部署价值表》。"
              "本表只新增内容，未改动其他工作表的任何单元格。").font = NOTE_FONT
ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=22)
r += 2

ws.cell(r, 1, "一、数值口径（公式全部引用既有表，可随原表联动）").font = SEC_FONT
ws.cell(r, 1).fill = SEC_FILL
ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=22)
r += 1
notes = [
    "1) 部署价值 cost_value =（3 − dcs/8）× dcs ＋ 2 × acs / 3 − 1   —— 与《部署价值表》E3 一致；dcs=部署费用，acs=行动费用。",
    "2) 卡牌价值 card_value = β × ATK ＋ α × HP ＋ Σ词条等价费用   —— 与《基础公式》B20 一致。",
    "3) β / α 按兵种取《基础公式》E9:F12（步兵1.1/0.9、骑兵0.9/1.1、弓兵1.2/0.8、支援2/2）；策略卡无 ATK/HP，其价值＝Σ词条等价费用。",
    "4) acs 口径：步兵/弓兵/器械(支援)＝1；骑兵＝2（策划案 §2.5 骑兵2AP）；连战单位＝2（可连续攻击两次）；策略卡＝0。",
    "5) 闪击口径（《基础公式》E24）：骑兵携带＝0；其他兵种携带＝+0.5；未携带＝−0.5（策略卡不适用该条）。",
    "6) 平衡判定：|卡牌价值 − 部署价值| ≤ 0.35（兵牌）；≤ 0.50（策略卡，因词条等价费用为 1/2/3/5/6/7 等离散档位）。",
    "7) 卡面兵种标识：步兵=「步」、骑兵=「骑」、弓兵=「弓」、支援类=器械「器」、策略牌=「策」；弓兵与器械自带射程2，不计入词条。",
]
for n in notes:
    ws.cell(r, 1, n).font = NOTE_FONT
    ws.cell(r, 1).alignment = LEFT
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=22)
    ws.row_dimensions[r].height = 14
    r += 1
r += 1

# 二、兵种系数表
coef_row = r + 1
ws.cell(r, 1, "二、兵种系数表（公式引用《基础公式》E9:F12）").font = SEC_FONT
ws.cell(r, 1).fill = SEC_FILL
ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=22)
r += 1
for j, h in enumerate(["兵种", "β（ATK系数）", "α（HP系数）", "来源"], start=1):
    c = ws.cell(r, j, h); c.font = H_FONT; c.fill = H_FILL; c.alignment = CENTER; c.border = BORDER
r += 1
for i, (u, src) in enumerate([("步兵", "基础公式 B9/E9/F9"), ("骑兵", "基础公式 B10/E10/F10"),
                              ("弓兵", "基础公式 B11/E11/F11"), ("支援", "基础公式 B12/E12/F12")]):
    ws.cell(r, 1, u).alignment = CENTER
    ws.cell(r, 2, f"='基础公式'!E{9 + i}").number_format = "0.0"
    ws.cell(r, 3, f"='基础公式'!F{9 + i}").number_format = "0.0"
    ws.cell(r, 4, src).font = NOTE_FONT
    for j in range(1, 5):
        ws.cell(r, j).border = BORDER
    r += 1
r += 1

# 三、词条等价费用表
ENTRY_ROWS = [
    ("闪击", 0.5, "基础公式 D24；骑兵携带=0，未携带=−0.5（见卡池 O 列公式，不计入本行查表）"),
    ("伏兵", 0.5, "基础公式 D25；主动攻击前不可被攻击"),
    ("守护", 1.0, "基础公式 D26；部署时+3HP，已由该等价费用计入"),
    ("血战", 2.0, "表内调整（原 1.5）：主动攻击后回复 2HP，被反击时不回复——避免「造成伤害的回血 < 反击伤害」导致攻出去反而亏、畏战不前。持续回血故高于一次性回血档（回血2=2），上调至 2.0"),
    ("连战", 1.0, "基础公式 D28；同一回合连续攻击两次（行动力记为2）"),
    ("摸牌1", 1.0, "基础公式 D29；抽1张牌"),
    ("摸牌2", 3.0, "基础公式 D29；抽2张牌"),
    ("摸牌3", 6.0, "基础公式 D29；抽3张牌"),
    ("召唤(手牌)", 1.0, "基础公式 D30；加入手牌1张"),
    ("召唤(牌堆)", 0.5, "基础公式 D30；加入牌堆1张"),
    ("回血1", 1.0, "基础公式 D31；回1点"),
    ("回血2", 2.0, "基础公式 D31；回2点"),
    ("回血3", 3.0, "基础公式 D31；仅策略卡可用的档位"),
    ("回血5", 5.0, "基础公式 D31；仅策略卡可用的档位"),
    ("回血7", 7.0, "基础公式 D31；仅策略卡可用的档位"),
    ("重甲1", 1.0, "基础公式 D32；每层受击减1伤害，仅重型单位"),
    ("重甲2", 2.0, "基础公式 D32；仅重型单位"),
    ("伤害1", 1.0, "表外补充定价：直接伤害 1 点 = 1 价值（与回血对称：1费可造成2点伤害或回复2点HP）"),
    ("伤害2", 2.0, "表外补充定价：同上"),
    ("伤害3", 3.0, "表外补充定价：同上"),
    ("伤害4", 4.0, "表外补充定价：同上"),
    ("伤害5", 5.0, "表外补充定价：同上"),
    ("伤害7", 7.0, "表外补充定价：同上"),
    ("弃手牌1", 1.0, "表外补充定价：弃置敌方1张手牌 = 1 价值"),
    ("临时ATK+3", 1.5, "表外补充定价：本回合临时 +1ATK = 0.5 价值"),
    ("誓师", 2.0, "表内新增：支援（器械）类通用「部署增益」标价——部署当回合，己方所有已部署单位本回合 ATK+1；支援类因此面板偏低"),
]
entry_hdr = r + 1
ws.cell(r, 1, "三、词条等价费用表（《基础公式》B23:E32；标注「表外」者为本表补充定价，可随时调整）").font = SEC_FONT
ws.cell(r, 1).fill = SEC_FILL
ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=22)
r += 1
for j, h in enumerate(["词条/效果", "等价费用", "来源/备注"], start=1):
    c = ws.cell(r, j, h); c.font = H_FONT; c.fill = H_FILL; c.alignment = CENTER; c.border = BORDER
r += 1
entry_first = r
for name, val, note in ENTRY_ROWS:
    ws.cell(r, 1, name).alignment = CENTER
    ws.cell(r, 2, val).number_format = "0.0"
    ws.cell(r, 2).alignment = CENTER
    ws.cell(r, 3, note).font = NOTE_FONT
    ws.cell(r, 3).alignment = LEFTC
    for j in range(1, 4):
        ws.cell(r, j).border = BORDER
    r += 1
entry_last = r - 1
LOOKUP = f"$A${entry_first}:$B${entry_last}"
COEF = f"$A${coef_row + 1}:$C${coef_row + 4}"   # 数据行：coef_row+1 .. coef_row+4
r += 1

# 四、卡池表
ws.cell(r, 1, "四、秦朝卡池（24 张设计；O~R 列为公式校验，随数值自动更新）").font = SEC_FONT
ws.cell(r, 1).fill = SEC_FILL
ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=22)
r += 1
hdr = ["序号", "cardId", "名称", "稀有度", "类型", "兵种", "重量", "费用", "行动力", "ATK", "HP",
       "词条1", "词条2", "词条3", "词条等价", "部署价值", "卡牌价值", "差值", "定位", "效果", "古文描述", "出处"]
for j, h in enumerate(hdr, start=1):
    c = ws.cell(r, j, h); c.font = H_FONT; c.fill = H_FILL; c.alignment = CENTER; c.border = BORDER
ws.row_dimensions[r].height = 28
r += 1
card_first = r
for idx, c in enumerate(CARDS, start=1):
    (cid, name, rar, kind, unit, wt, dcs, acs, atk, hp, e1, e2, e3, role, eff, quote, src) = c
    vals = [idx, cid, name, rar, kind, unit, wt, dcs, acs, atk, hp, e1, e2, e3]
    for j, v in enumerate(vals, start=1):
        cell = ws.cell(r, j, v)
        cell.border = BORDER
        cell.alignment = CENTER if j not in (3, 12, 13, 14) else CENTER
    ws.cell(r, 3).alignment = LEFT
    for j in (8, 9, 10, 11):
        ws.cell(r, j).number_format = "0"
    # O 词条等价
    f_sig = f'IF($E{r}="策略牌",0,IF(COUNTIF($L{r}:$N{r},"闪击")>0,IF($F{r}="骑兵",0,0.5),-0.5))'
    f_look = "".join(
        f'+IF(${col}{r}="闪击",0,IFERROR(VLOOKUP(${col}{r},{LOOKUP},2,FALSE),0))'
        for col in ("L", "M", "N"))
    ws.cell(r, 15, f"={f_sig}{f_look}").number_format = "0.00"
    # P 部署价值
    ws.cell(r, 16, f"=ROUND((3-$H{r}/8)*$H{r}+2*$I{r}/3-1,3)").number_format = "0.000"
    # Q 卡牌价值
    ws.cell(r, 17, f'=IF($E{r}="策略牌",$O{r},'
                   f'ROUND(VLOOKUP($F{r},{COEF},2,FALSE)*$J{r}+VLOOKUP($F{r},{COEF},3,FALSE)*$K{r}+$O{r},3))'
            ).number_format = "0.000"
    # R 差值
    ws.cell(r, 18, f"=ROUND($Q{r}-$P{r},3)").number_format = "+0.000;-0.000;0.000"
    for j in (15, 16, 17, 18):
        ws.cell(r, j).alignment = CENTER
        ws.cell(r, j).border = BORDER
    ws.cell(r, 19, role).font = NOTE_FONT
    ws.cell(r, 19).alignment = LEFTC
    ws.cell(r, 20, eff).font = NOTE_FONT
    ws.cell(r, 20).alignment = LEFTC
    ws.cell(r, 21, quote).font = Font(size=9, italic=True, color="7B3F00")
    ws.cell(r, 21).alignment = LEFTC
    ws.cell(r, 22, src).font = NOTE_FONT
    ws.cell(r, 22).alignment = LEFTC
    for j in (19, 20, 21, 22):
        ws.cell(r, j).border = BORDER
    ws.row_dimensions[r].height = 30
    r += 1
card_last = r - 1

# 差值条件格式
rng = f"$R${card_first}:$R${card_last}"
CF_OK = PatternFill(patternType="solid", start_color="FFC6EFCE", end_color="FFC6EFCE")
CF_MID = PatternFill(patternType="solid", start_color="FFFFEB9C", end_color="FFFFEB9C")
CF_BAD = PatternFill(patternType="solid", start_color="FFFFC7CE", end_color="FFFFC7CE")
ws.conditional_formatting.add(rng, FormulaRule(formula=[f"ABS($R{card_first})<=0.35"], fill=CF_OK, stopIfTrue=False))
ws.conditional_formatting.add(rng, FormulaRule(formula=[f"AND(ABS($R{card_first})>0.35,ABS($R{card_first})<=0.5)"], fill=CF_MID, stopIfTrue=False))
ws.conditional_formatting.add(rng, FormulaRule(formula=[f"ABS($R{card_first})>0.5"], fill=CF_BAD, stopIfTrue=False))
r += 1

# 五、卡组构筑示例
ws.cell(r, 1, "五、卡组构筑示例（30 张 ＝ 12普通 + 8稀有 + 4史诗 + 2传说 + 4任意；同名上限：普通4/稀有3/史诗2/传说1）").font = SEC_FONT
ws.cell(r, 1).fill = SEC_FILL
ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=22)
r += 1
for j, h in enumerate(["卡名", "稀有度", "费用", "数量", "小计CP", "配额归属"], start=1):
    c = ws.cell(r, j, h); c.font = H_FONT; c.fill = H_FILL; c.alignment = CENTER; c.border = BORDER
r += 1
deck_first = r
for name, rar, cp, cnt, quota in DECK:
    ws.cell(r, 1, name).alignment = LEFT
    ws.cell(r, 2, rar).alignment = CENTER
    ws.cell(r, 3, cp).alignment = CENTER
    ws.cell(r, 4, cnt).alignment = CENTER
    ws.cell(r, 5, f"=C{r}*D{r}").alignment = CENTER
    ws.cell(r, 6, quota).font = NOTE_FONT
    for j in range(1, 7):
        ws.cell(r, j).border = BORDER
    r += 1
deck_last = r - 1
ws.cell(r, 1, "合计").font = Font(bold=True)
ws.cell(r, 4, f"=SUM(D{deck_first}:D{deck_last})").font = Font(bold=True)
ws.cell(r, 4).alignment = CENTER
ws.cell(r, 5, f"=SUM(E{deck_first}:E{deck_last})").font = Font(bold=True)
ws.cell(r, 5).alignment = CENTER
for j in range(1, 7):
    ws.cell(r, j).border = BORDER
r += 1
curve = [("1~2费", sum(d[3] for d in DECK if d[2] in (1, 2)), "策划案建议 10~12 张"),
         ("3~4费", sum(d[3] for d in DECK if d[2] in (3, 4)), "策划案建议 12~14 张"),
         ("5费+", sum(d[3] for d in DECK if d[2] >= 5), "策划案建议 4~6 张")]
for label, cnt, tip in curve:
    ws.cell(r, 1, label).alignment = CENTER
    ws.cell(r, 4, cnt).alignment = CENTER
    ws.cell(r, 6, tip).font = NOTE_FONT
    for j in range(1, 7):
        ws.cell(r, j).border = BORDER
    r += 1
r += 1

# 固定建筑卡
ws.cell(r, 1, "固定建筑卡（通用，不占 30 张配额；策划案 §2.3）").font = SEC_FONT
ws.cell(r, 1).fill = SEC_FILL
ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=22)
r += 1
for j, h in enumerate(["建筑", "HP", "被摧毁后（Debuff）"], start=1):
    c = ws.cell(r, j, h); c.font = H_FONT; c.fill = H_FILL; c.alignment = CENTER; c.border = BORDER
r += 1
for b, hpb, deb in [("大营", 20, "HP 归零 → 立即判负（唯一胜负条件）"),
                    ("军械库", 5, "敌方全场兵牌与后续上场兵牌 ATK −1"),
                    ("粮草", 5, "敌方 CP 上限 −2")]:
    ws.cell(r, 1, b).alignment = CENTER
    ws.cell(r, 2, hpb).alignment = CENTER
    ws.cell(r, 3, deb).font = NOTE_FONT
    for j in range(1, 4):
        ws.cell(r, j).border = BORDER
    r += 1
r += 1

# 六、校验与备注
ws.cell(r, 1, "六、校验与备注").font = SEC_FONT
ws.cell(r, 1).fill = SEC_FILL
ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=22)
r += 1
final = [
    "· 兵牌 17 张 / 策略牌 7 张；稀有度：普通 8 · 稀有 8 · 史诗 5 · 传说 3。每个稀有度都比「一张 30 张卡组该档位的最大可入组数」多出至少 1 种选择：传说可入 2 → 提供 3 供选。同名上限（普4/稀3/史2/传1）内可组满 30 张。",
    "· 全部 24 张的 |差值| ≤ 0.35（兵牌）与 ≤ 0.50（策略卡），见 R 列条件格式：绿色达标、黄色可接受、红色需调整。",
    "· 卡面以「兵种/特色部队」命名，不出现具体将领姓名（如以 玄甲铁骑/函谷铁卫/蹶张神弩 代替具名名将）；策略卡可为具体谋略或关键历史事件（如 商君变法）。",
    "· 直伤/回血档位为 1 点 = 1 价值，与回血对称：1 费即可造成 2 点伤害（攒射）或回复 2 点（输粟）；更高费用的伤害/回复视强度取 3/5/7 等档（绝粮道、移民实边等）。",
    "· 若把骑兵的行动费用 acs 改记为 1，则骑兵预算整体下降 0.667，需把 I 列改为 1 后重看 R 列（公式会自动重算）。",
    "· 守护「部署时+3HP」的收益已包含在词条等价费用中，卡面 HP 不再额外加算，避免重复计价。",
    "· 血战词条等价费用已上调至 2.0（主动攻击后回复 2HP、被反击时不回复）；带血战的 陷阵士(4/4)、玄甲铁骑(6/6) 已按 2.0 重校（见第三节血战行）。",
    "· 守护口径（排位制）：仅保护同排相邻槽位的友方单位与建筑，守护单位之间不互相保护（见策划案 §4.3）。",
    "· 「伤害」「弃手牌」「临时ATK」三类为本表补充定价（原《基础公式》未列），如与原设计口径不同，只改第三节数值即可全表联动。",
    "· 连战单位（连弩士）行动力记为 2：既可移动+攻击，也可连续攻击两次，符合《基础公式》连战词条说明。",
    "· 本卡池为单一秦朝卡包，主朝代占比 100%，满足策划案 §5.3.1「主朝代卡 ≥25 张」；如需混入次要朝代，最多 5 张且不得含传说卡。",
]
for n in final:
    ws.cell(r, 1, n).font = NOTE_FONT
    ws.cell(r, 1).alignment = LEFT
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=22)
    ws.row_dimensions[r].height = 14
    r += 1

ws.sheet_properties.tabColor = "8B1A1A"
wb.save(SRC)
print("已写入:", SHEET, "行数:", ws.max_row)
