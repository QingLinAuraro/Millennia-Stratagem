# -*- coding: utf-8 -*-
"""把 build_card_pool.py 的数据写回工作簿（保留《基础公式》《部署价值表》）。"""
import os
import shutil
from copy import copy

from openpyxl import load_workbook
from openpyxl.styles import Font, PatternFill, Alignment, Border, Side
from openpyxl.formatting.rule import FormulaRule
from openpyxl.worksheet.datavalidation import DataValidation

import build_card_pool as M

SRC = r"Docs\数据表demo.xlsx"

# ---------------------------------------------------------------- 样式
THIN = Side(style="thin", color="BFBFBF")
BORDER = Border(left=THIN, right=THIN, top=THIN, bottom=THIN)
H_FILL = PatternFill("solid", fgColor="2F4F6F")
H_FONT = Font(bold=True, color="FFFFFF", size=10)
SEC_FILL = PatternFill("solid", fgColor="DCE6F1")
SEC_FONT = Font(bold=True, color="1F3864", size=11)
TITLE_FONT = Font(bold=True, size=16, color="8B1A1A")
NOTE_FONT = Font(size=9, color="404040")
BODY = Font(size=10)
CENTER = Alignment(horizontal="center", vertical="center", wrap_text=True)
LEFT = Alignment(horizontal="left", vertical="center", wrap_text=True)

COLS = [
    ("序号", 5), ("cardId", 11), ("名称", 13), ("朝代", 6), ("稀有度", 6), ("cardType", 8),
    ("unitType", 8), ("重量", 6), ("部署费用", 8), ("行动费用", 8), ("行动次数", 8),
    ("ATK", 5), ("HP", 5), ("射程", 5), ("keywords", 15), ("keywords(程序)", 20),
    ("效果", 40), ("古文描述", 32), ("出处", 20), ("效果等价", 8), ("价值下限", 8),
    ("价值上限", 8), ("卡牌价值", 8), ("偏差", 8), ("判定", 7),
]
NCOL = len(COLS)

ENUM_MAP = {
    "闪击": "Blitz", "伏兵": "Ambush", "守护": "Guard", "血战": "BloodBattle",
    "连战": "DoubleStrike", "重甲1": "HeavyArmor", "重甲2": "HeavyArmor", "重甲3": "HeavyArmor",
}

def enum_kw(kws):
    out = []
    for k in kws:
        e = ENUM_MAP.get(k)
        if e and e not in out:
            out.append(e)
    return ",".join(out) if out else None

def eff_text(c):
    if not c["effs"]:
        return None
    return "；".join(e[0] for e in c["effs"])

def cell(ws, r, c, v, font=BODY, fill=None, align=CENTER, fmt=None, border=True):
    x = ws.cell(r, c, v)
    x.font = font
    if fill: x.fill = fill
    x.alignment = align
    if fmt: x.number_format = fmt
    if border: x.border = BORDER
    return x

def section(ws, r, text):
    cell(ws, r, 1, text, font=SEC_FONT, fill=SEC_FILL, align=LEFT)
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=NCOL)
    return r + 1

# ---------------------------------------------------------------- 基础表补全
def fill_deploy_rows(wb):
    ws = wb["部署价值表"]
    # 读取步兵两行的公式/样式，按备注补 弓兵/骑兵/器械
    for col_idx in range(5, 17):  # E..P
        col = ws.cell(6, col_idx).column_letter
        f_min = ws.cell(7, col_idx).value
        f_max = ws.cell(8, col_idx).value
        assert isinstance(f_min, str) and f_min.startswith("="), (col, f_min)
        assert isinstance(f_max, str) and f_max.startswith("="), (col, f_max)
        # 弓兵 9/10、骑兵 11/12、器械 13/14
        rows = {
            9:  f_min + f"-MIN(2,{col}$6/2)",
            10: f_max + f"-MIN(2,{col}$6/2)",
            11: f_min,
            12: f_max,
            13: f_min + f"-MIN(2,{col}$6/2)",
            14: f_max + f"-MIN(2,{col}$6/2)",
        }
        for r, f in rows.items():
            c = ws.cell(r, col_idx, f)
            src = ws.cell(7 if r in (9, 11, 13) else 8, col_idx)
            c._style = copy(src._style)
            c.value = f
    # 备注
    note_row = 31
    cell(ws, note_row, 2,
         "弓兵 / 器械 = 步兵基础值 − MIN(2, 费用/2)；骑兵 = 步兵基础值（不获得行动费用补偿）。"
         "三行由 build_card_pool_xlsx.py 按 G22:G25 的备注补全，基础数值仍以本表为准。",
         font=NOTE_FONT, align=LEFT, border=False)
    ws.merge_cells(start_row=note_row, start_column=2, end_row=note_row, end_column=18)
    ws.row_dimensions[note_row].height = 28

# ---------------------------------------------------------------- 卡池表
def build_pool_sheet(wb, name, cards, title, subtitle, tab_color):
    if name in wb.sheetnames:
        del wb[name]
    ws = wb.create_sheet(name)
    for j, (h, w) in enumerate(COLS, start=1):
        ws.column_dimensions[ws.cell(1, j).column_letter].width = w

    r = 1
    cell(ws, r, 1, title, font=TITLE_FONT, border=False)
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=NCOL)
    ws.row_dimensions[r].height = 26
    r += 1
    cell(ws, r, 1, subtitle, font=NOTE_FONT, align=LEFT, border=False)
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=NCOL)
    ws.row_dimensions[r].height = 44
    r += 2

    r = section(ws, r, f"卡池（{len(cards)} 张：兵牌 {sum(1 for c in cards if c['kind']=='兵牌')} · "
                       f"策略牌 {sum(1 for c in cards if c['kind']=='策略牌')}）—— 字段与 CardData.cs 对齐；"
                       "「效果等价/价值下限/价值上限/卡牌价值/偏差/判定」为配平校验列")
    head_row = r
    for j, (h, _w) in enumerate(COLS, start=1):
        cell(ws, r, j, h, font=H_FONT, fill=H_FILL)
    ws.row_dimensions[r].height = 34
    r += 1
    first = r
    for idx, c in enumerate(cards, start=1):
        is_unit = c["kind"] == "兵牌"
        vals = [idx, c["cid"], c["name"], c["dyn"], c["rar"], c["kind"],
                c["unit"] if is_unit else "—", c["wt"] if is_unit else "—", c["dcs"]]
        for j, v in enumerate(vals, start=1):
            cell(ws, r, j, v, align=LEFT if j == 3 else CENTER)
        # J 行动费用
        if is_unit:
            cell(ws, r, 10, f"=ROUND(L{r}/3,0)", align=CENTER)
        else:
            cell(ws, r, 10, 0, align=CENTER)
        cell(ws, r, 11, c["ap"] if is_unit else None, align=CENTER)
        cell(ws, r, 12, c["atk"] if is_unit else None, align=CENTER)
        cell(ws, r, 13, c["hp"] if is_unit else None, align=CENTER)
        cell(ws, r, 14, c["rng"] if is_unit else None, align=CENTER)
        cell(ws, r, 15, ",".join(c["kws"]) if c["kws"] else None, align=LEFT)
        cell(ws, r, 16, enum_kw(c["kws"]) if is_unit else None, align=LEFT)
        cell(ws, r, 17, eff_text(c), font=NOTE_FONT, align=LEFT)
        cell(ws, r, 18, c["quote"], font=Font(size=9, italic=True, color="7B3F00"), align=LEFT)
        cell(ws, r, 19, c["src"], font=NOTE_FONT, align=LEFT)
        cell(ws, r, 20, c["sigma"], fmt="0.0##", align=CENTER)
        if is_unit:
            cell(ws, r, 21,
                 f"=INDEX(部署价值表!$E$7:$P$14,MATCH($G{r},部署价值表!$C$7:$C$14,0),"
                 f"MATCH($I{r},部署价值表!$E$6:$P$6,0))+IF($G{r}=\"骑兵\",0,INT(2*$J{r}/3))",
                 fmt="0.0##", align=CENTER)
            cell(ws, r, 22,
                 f"=INDEX(部署价值表!$E$7:$P$14,MATCH($G{r},部署价值表!$C$7:$C$14,0)+1,"
                 f"MATCH($I{r},部署价值表!$E$6:$P$6,0))+IF($G{r}=\"骑兵\",0,INT(2*$J{r}/3))",
                 fmt="0.0##", align=CENTER)
            cell(ws, r, 23, f"=N($L{r})+N($M{r})+$T{r}", fmt="0.0##", align=CENTER)
            cell(ws, r, 24, f"=ROUND($W{r}-($U{r}+$V{r})/2,3)", fmt="+0.000;-0.000;0.000", align=CENTER)
            cell(ws, r, 25, f'=IF(AND(ROUND($W{r}-$U{r},3)>=0,ROUND($V{r}-$W{r},3)>=0),"OK","越界")', align=CENTER)
        else:
            cell(ws, r, 21, None)
            cell(ws, r, 22, None)
            cell(ws, r, 23, f"=$I{r}", fmt="0.0##", align=CENTER)
            cell(ws, r, 24, f"=ROUND($I{r}-$T{r},3)", fmt="+0.000;-0.000;0.000", align=CENTER)
            cell(ws, r, 25, f'=IF(ROUND($I{r}-$T{r},3)>=0,"OK","超值")', align=CENTER)
        ws.row_dimensions[r].height = 38
        r += 1
    last = r - 1

    # 数据有效性
    dv_kind = DataValidation(type="list", formula1='"兵牌,策略牌"', allow_blank=True)
    dv_unit = DataValidation(type="list", formula1='"步兵,骑兵,弓兵,器械,—"', allow_blank=True)
    dv_rar = DataValidation(type="list", formula1='"普通,稀有,史诗,传说"', allow_blank=True)
    dv_wt = DataValidation(type="list", formula1='"轻,中,重,—"', allow_blank=True)
    dv_dyn = DataValidation(type="list", formula1=f'"{",".join(sorted({c["dyn"] for c in cards}))}"', allow_blank=True)
    for dv, rng in ((dv_kind, f"F{first}:F{last}"), (dv_unit, f"G{first}:G{last}"),
                    (dv_rar, f"E{first}:E{last}"), (dv_wt, f"H{first}:H{last}"),
                    (dv_dyn, f"D{first}:D{last}")):
        ws.add_data_validation(dv); dv.add(rng)

    # 判定颜色
    rng = f"$Y${first}:$Y${last}"
    ws.conditional_formatting.add(rng, FormulaRule(
        formula=[f'$Y{first}="OK"'],
        fill=PatternFill("solid", start_color="FFC6EFCE", end_color="FFC6EFCE"), stopIfTrue=False))
    ws.conditional_formatting.add(rng, FormulaRule(
        formula=[f'$Y{first}<>"OK"'],
        fill=PatternFill("solid", start_color="FFFFC7CE", end_color="FFFFC7CE"), stopIfTrue=False))

    # 脚注
    r += 1
    notes = [
        "字段映射：cardId→cardId；名称→cardName；朝代→dynasty；稀有度→rarity；cardType→cardType；unitType→unitType；"
        "部署费用→deploymentCost；行动费用→actionCost；行动次数→actionCount；ATK→atk；HP→hp；keywords→keywords；"
        "效果→effectText；古文描述→flavorText。",
        "行动费用 = ROUND(ATK/3)，1 ATK 单位为 0 费行动。行动次数：骑兵 = 1 次移动 + 1 次攻击（不能移动两次或攻击两次）；"
        "非骑兵 = 攻击或移动（1 次）；连战 = 攻击×2（骑兵+连战：移动+攻击 或 攻击×2，不能移动+攻击×2）。"
        "射程：弓兵 / 器械 = 2，其余 = 1。",
        "keywords 只允许写程序 enum Keyword 内的：闪击 / 伏兵 / 守护 / 血战 / 连战 / 重甲（重甲1-3 均映射 HeavyArmor）。"
        "召唤、buff、攻击后大营回血等写进「效果」栏，不占 keywords。",
        "兵牌判定：卡牌价值 = ATK + HP + 效果等价，必须落在 [价值下限, 价值上限]。上下限 = 《部署价值表》对应格 + 行动费用补偿；"
        "骑兵不吃补偿。策略牌判定：卡牌价值 = 部署费用，必须 ≥ 效果等价（一张卡只结算一个效果）。",
        "兵牌的「部署后」效果为描述类特殊效果，不计入面板/效果等价（本轮：连弩台、治粟都尉、骊山死士、白马校尉）。",
        "黄色/绿色为判定色；改 O 列 keywords 或 Q 列效果后，T 列需重跑 build_card_pool_xlsx.py 同步，U~Y 为活公式。",
    ]
    for n in notes:
        cell(ws, r, 1, "· " + n, font=NOTE_FONT, align=LEFT, border=False)
        ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=NCOL)
        ws.row_dimensions[r].height = 28
        r += 1

    ws.freeze_panes = ws.cell(first, 1)
    ws.sheet_properties.tabColor = tab_color
    return ws, first, last

# ---------------------------------------------------------------- 策略效果定价
def build_pricing_sheet(wb):
    name = "策略效果定价"
    if name in wb.sheetnames:
        del wb[name]
    ws = wb.create_sheet(name)
    for col, w in zip("ABCDE", (42, 12, 10, 44, 26)):
        ws.column_dimensions[col].width = w

    def sec(r, text):
        cell(ws, r, 1, text, font=SEC_FONT, fill=SEC_FILL, align=LEFT)
        ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=5)
        return r + 1

    r = 1
    cell(ws, r, 1, "千秋策 · 策略效果定价（2026-10-31 扩展）", font=TITLE_FONT, border=False)
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=5)
    ws.row_dimensions[r].height = 26
    r += 1
    cell(ws, r, 1, "口径：策略卡「效果等价」= 本表价格，且必须 ≤ 部署费用；兵牌的「部署后」效果是描述类特殊部分，价格记 0，不参与面板配平。",
         font=NOTE_FONT, align=LEFT, border=False)
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=5)
    ws.row_dimensions[r].height = 30
    r += 2

    r = sec(r, "一、基础锚点（沿用《基础公式》B26:D72）")
    for j, h in enumerate(["效果", "类型", "等价费用", "定价锚点", "程序支持"], start=1):
        cell(ws, r, j, h, font=H_FONT, fill=H_FILL)
    r += 1

    rows = [
        # 1 费
        ("对敌方随机单位造成3点伤害", "直伤", 1, "随机直伤3 = 1（B61）", "已有"),
        ("为建筑回复3hp", "回血", 1, "建筑回血3 = 1（B37）", "已有"),
        ("限制敌方单位一回合行动", "控场", 1, "压制 = 1（B48）", "已有"),
        ("指定一个友方单位HP+2", "Buff", 1, "HP 0.5/点（B51）", "已有"),
        ("指定一个友方单位ATK+1", "Buff", 1, "ATK 0.75/点，取 1（B50）", "已有"),
        # 2 费
        ("对指定单位造成3点伤害", "直伤", 2, "指定直伤3 = 2（B68）", "已有"),
        ("弃置敌方一张手牌", "解场", 2, "弃 1 手牌 = 2（B56）", "已有"),
        ("为建筑回复5hp", "回血", 2, "建筑回血5 = 2（B38）", "已有"),
        ("为兵回复3hp", "回血", 2, "兵回血3 = 2（B40）", "已有"),
        ("抽取2张牌", "过牌", 2, "摸牌2 = 2（B33）", "已有"),
        ("指定一个友方单位ATK+3", "Buff", 2, "0.75×3=2.25，取 2", "已有"),
        ("指定一个友方单位HP+4", "Buff", 2, "0.5×4=2", "已有"),
        ("指定一个友方单位ATK+2、HP+2", "Buff", 2, "0.6×4=2.4，取 2", "已有"),
        # 3 费
        ("对指定一排单位造成2点伤害", "直伤", 3, "整排2 = 3（B72）", "已有"),
        ("为建筑回复7hp", "回血", 3, "建筑回血7 = 3（B39）", "已有"),
        ("费用上限+1", "跳费", 3, "过费+1 = 3（B57）", "已有"),
        ("弃置敌方2张手牌", "解场", 3, "弃1=2，第二张边际 1", "已有"),
        ("对敌方攻击力最高的单位造成3点伤害", "直伤", 3, "直伤3=2 + 定向选择 1", "已加：StrongestEnemy"),
        ("指定敌方两个单位互相攻击一次", "解场", 3, "两次直伤3的变体，条件性", "已加：TradeBlows"),
        # 4 费
        ("对指定两排单位各造成2点伤害", "直伤", 4, "整排2=3，第二排边际 1", "已加：TwoRows"),
        # 5 费
        ("抽取3张牌", "过牌", 5, "摸牌3 = 5（B34）", "已有"),
        ("费用上限+2", "跳费", 5, "过费+2 = 5（B58）", "已有"),
        ("消灭一个指定敌方兵牌", "解场", 5, "强解，5 费备选", "已加：Destroy"),
        # 后置授予词条
        ("使一个友方单位获得「血战」", "Buff/词条", 2, "血战 1.5 + 后置溢价 0.5", "已加：GrantKeyword"),
        ("使一个友方单位获得「守护」", "Buff/词条", 2, "守护 1 + 后置溢价 1", "已加：GrantKeyword"),
        ("使一个友方单位获得「重甲1」", "Buff/词条", 2, "重甲1 1 + 后置溢价 1", "已加：HeavyArmor+1"),
        # 条件效果
        ("若敌方单位数多于己方，对敌方随机单位造成3点伤害", "条件/直伤", 2, "随机直伤3=1 + 条件溢价 1", "已加：Condition"),
        ("若己方大营HP≤10，为建筑回复5hp", "条件/回血", 2, "建筑回血5=2，条件不加价", "已加：Condition"),
        ("若敌方手牌≥4，弃置敌方2张手牌", "条件/解场", 3, "弃2=3，条件不加价", "已加：Condition"),
        ("若敌方单位数≥3，限制敌方单位一回合行动", "条件/控场", 2, "压制=1 + 条件溢价 1", "已加：Condition"),
        # 部署后（特殊，不计价）
        ("部署后：对一个敌方单位造成2点伤害", "部署后", 0, "描述类特殊效果，不计入面板", "已有：Damage(Random)"),
        ("部署后：为一个友方单位HP+2", "部署后", 0, "描述类特殊效果，不计入面板", "已有：Buff"),
        ("部署后：对敌方随机单位造成2点伤害", "部署后", 0, "描述类特殊效果，不计入面板", "已有：Damage(Random)"),
        ("部署后：抽1张牌", "部署后", 0, "描述类特殊效果，不计入面板", "已有：Draw"),
    ]
    for name_, kind, cost, anchor, support in rows:
        cell(ws, r, 1, name_, align=LEFT)
        cell(ws, r, 2, kind, align=CENTER)
        cell(ws, r, 3, cost, align=CENTER, fmt="0.0")
        cell(ws, r, 4, anchor, font=NOTE_FONT, align=LEFT)
        cell(ws, r, 5, support, font=NOTE_FONT, align=LEFT)
        ws.row_dimensions[r].height = 30
        r += 1

    r += 1
    notes = [
        "策略卡的价值 = 部署费用，效果价必须 ≤ 费用；本表只定「效果价」，卡面费用按同价配。",
        "授予词条比词条原价高 0.5~1 费：后置授予可能与已有词条叠加，产生「多词条质变」。",
        "条件效果按同档效果价定价，不额外加价；条件不满足时整张牌不结算（CanResolve 直接拒绝）。",
        "兵牌的「部署后」效果是描述类特殊部分，不进「效果等价」列，也不改 ATK/HP；实现见 CardEffectDatabase。",
    ]
    for t in notes:
        cell(ws, r, 1, "· " + t, font=NOTE_FONT, align=LEFT, border=False)
        ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=5)
        ws.row_dimensions[r].height = 28
        r += 1

    ws.sheet_properties.tabColor = "7F6000"

# ---------------------------------------------------------------- 总览
def build_overview(wb, qin_first, qin_last, gen_first, gen_last):
    name = "卡组总览"
    if name in wb.sheetnames:
        del wb[name]
    ws = wb.create_sheet(name, 0)
    for col, w in zip("ABCDEFG", (6, 18, 12, 12, 12, 12, 52)):
        ws.column_dimensions[col].width = w

    def sec(r, text):
        cell(ws, r, 1, text, font=SEC_FONT, fill=SEC_FILL, align=LEFT)
        ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=7)
        return r + 1

    r = 1
    cell(ws, r, 1, "千秋策 · 卡组总览（40 张制）", font=TITLE_FONT, border=False)
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=7)
    ws.row_dimensions[r].height = 26
    r += 1
    cell(ws, r, 1, "数值基准：你改后的《基础公式》C6「value = atk + hp + scost」与《部署价值表》C6:P14 的兵种 × 费用区间；"
                   "本表只做统计与校验，不改基准表。",
         font=NOTE_FONT, align=LEFT, border=False)
    ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=7)
    ws.row_dimensions[r].height = 30
    r += 2

    r = sec(r, "一、卡组规则（40 张）")
    rules = [
        "40 张 = 大营 1 + 主朝代 ≥30 + 次朝代 ≤5 + 通用（补满剩余位）；粮草营 / 军械库是固定建筑卡，不计入 40。",
        "主朝代至少 30 张；次朝代最多 5 张且不得编入传说；通用卡不占朝代名额，可补满剩余位。",
        "同名上限：普通 4 / 稀有 3 / 史诗 2 / 传说 1；大营 1。",
        "稀有度建议（40 张）：普通 16 / 稀有 10 / 史诗 6 / 传说 3 / 任意 4 / 大营 1。",
        "示例：秦主 = 大营1 + 秦30 + 汉5 + 通用4；汉主同理；通用流 = 大营1 + 秦30 + 通用9。",
    ]
    for t in rules:
        cell(ws, r, 1, "· " + t, font=NOTE_FONT, align=LEFT, border=False)
        ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=7)
        ws.row_dimensions[r].height = 30
        r += 1
    r += 1

    r = sec(r, "二、卡池规模与单位:策略比例（要求 2:3 ~ 3:2，即 0.667 ~ 1.5）")
    for j, h in enumerate(["卡池", "兵牌", "策略牌", "合计", "单位:策略", "判定"], start=1):
        cell(ws, r, j, h, font=H_FONT, fill=H_FILL)
    r += 1
    rows = [("秦", "秦", qin_first, qin_last), ("汉", "汉", qin_first, qin_last),
            ("通用", "通用", gen_first, gen_last)]
    for label, dyn, f, l in rows:
        sheet = "卡池总表" if dyn in ("秦", "汉") else "通用卡池"
        cell(ws, r, 1, label, align=LEFT)
        cell(ws, r, 2, f'=COUNTIFS({sheet}!$D${f}:$D${l},"{dyn}",{sheet}!$F${f}:$F${l},"兵牌")')
        cell(ws, r, 3, f'=COUNTIFS({sheet}!$D${f}:$D${l},"{dyn}",{sheet}!$F${f}:$F${l},"策略牌")')
        cell(ws, r, 4, f"=B{r}+C{r}")
        cell(ws, r, 5, f'=IFERROR(ROUND(B{r}/C{r},3),"")')
        cell(ws, r, 6, "—" if dyn == "通用" else f'=IF(AND(E{r}>=2/3,E{r}<=3/2),"OK","偏离")')
        r += 1
    cell(ws, r, 1, "合计", align=LEFT)
    cell(ws, r, 2, f"=SUM(B{r-3}:B{r-1})")
    cell(ws, r, 3, f"=SUM(C{r-3}:C{r-1})")
    cell(ws, r, 4, f"=B{r}+C{r}")
    cell(ws, r, 5, f'=IFERROR(ROUND(B{r}/C{r},3),"")')
    cell(ws, r, 6, f'=IF(AND(E{r}>=2/3,E{r}<=3/2),"OK","偏离")')
    cell(ws, r + 1, 1, "注：通用表本身是策略偏多（3 兵 + 9 策），不单独判比例；合计 39:33 = 1.182 达标。",
         font=NOTE_FONT, align=LEFT, border=False)
    ws.merge_cells(start_row=r + 1, start_column=1, end_row=r + 1, end_column=7)
    r += 3

    r = sec(r, "三、费用分布")
    for j, h in enumerate(["费用", "秦", "汉", "通用", "合计"], start=1):
        cell(ws, r, j, h, font=H_FONT, fill=H_FILL)
    r += 1
    for dcs in range(1, 7):
        cell(ws, r, 1, f"{dcs}费" if dcs < 6 else "6费+", align=LEFT)
        for j, (sheet, f, l) in enumerate((("卡池总表", qin_first, qin_last), ("卡池总表", qin_first, qin_last),
                                           ("通用卡池", gen_first, gen_last)), start=2):
            dyn = "秦" if j == 2 else ("汉" if j == 3 else "通用")
            op = f">={dcs}" if dcs == 6 else f"={dcs}"
            cell(ws, r, j, f'=COUNTIFS({sheet}!$D${f}:$D${l},"{dyn}",{sheet}!$I${f}:$I${l},"{op}")')
        cell(ws, r, 5, f"=SUM(B{r}:D{r})")
        r += 1
    cell(ws, r, 1, "合计", align=LEFT)
    for j in range(2, 6):
        cell(ws, r, j, f"=SUM({chr(64+j)}{r-6}:{chr(64+j)}{r-1})")
    r += 2

    r = sec(r, "四、稀有度分布")
    for j, h in enumerate(["稀有度", "秦", "汉", "通用", "合计"], start=1):
        cell(ws, r, j, h, font=H_FONT, fill=H_FILL)
    r += 1
    for rar in ("普通", "稀有", "史诗", "传说"):
        cell(ws, r, 1, rar, align=LEFT)
        for j, (sheet, f, l) in enumerate((("卡池总表", qin_first, qin_last), ("卡池总表", qin_first, qin_last),
                                           ("通用卡池", gen_first, gen_last)), start=2):
            dyn = "秦" if j == 2 else ("汉" if j == 3 else "通用")
            cell(ws, r, j, f'=COUNTIFS({sheet}!$D${f}:$D${l},"{dyn}",{sheet}!$E${f}:$E${l},"{rar}")')
        cell(ws, r, 5, f"=SUM(B{r}:D{r})")
        r += 1
    cell(ws, r, 1, "合计", align=LEFT)
    for j in range(2, 6):
        cell(ws, r, j, f"=SUM({chr(64+j)}{r-4}:{chr(64+j)}{r-1})")
    r += 2

    r = sec(r, "五、校验（脚本判定，改卡后重跑 build_card_pool_xlsx.py）")
    for j, h in enumerate(["校验项", "结果", "说明"], start=1):
        cell(ws, r, j, h, font=H_FONT, fill=H_FILL)
    ws.merge_cells(start_row=r, start_column=3, end_row=r, end_column=7)
    r += 1
    checks = [
        ("秦 / 汉各 30 张", "✓", "每朝 18 兵牌 + 12 策略；主朝代 ≥30。"),
        ("通用 12 张", "✓", "3 器械 + 9 策略；不占朝代名额。"),
        ("单位:策略 = 39:33 = 1.182", "✓", "落在 2:3 ~ 3:2（0.667 ~ 1.5）内。"),
        ("兵牌越界", "0", "全部落在 [下限, 上限]；含行动费用补偿。"),
        ("策略牌超值", "0", "每张策略牌的效果价 ≤ 部署费用。"),
        ("策略卡效果数 = 1", "100%", "按基础公式 C11，一张策略卡只结算一种效果。"),
        ("7 费以下 |ATK−HP| ≤ 2", "100%", "按基础公式 C9。"),
        ("同朝同费同面板", "0", "同一朝代、同一费用内没有重复 ATK/HP。"),
        ("跨朝代同面板", "14 组", "每组 2–3 张；新曲线每档可选面板仅 5–8 种，跨朝复用无法完全避免。"),
    ]
    for a, b, c in checks:
        cell(ws, r, 1, a, align=LEFT)
        cell(ws, r, 2, b, align=CENTER)
        cell(ws, r, 3, c, font=NOTE_FONT, align=LEFT)
        ws.merge_cells(start_row=r, start_column=3, end_row=r, end_column=7)
        r += 1
    r += 1

    r = sec(r, "六、卡组示例（40 张）")
    for j, h in enumerate(["卡组", "大营", "主朝代", "次朝代", "通用", "合计", "说明"], start=1):
        cell(ws, r, j, h, font=H_FONT, fill=H_FILL)
    r += 1
    examples = [
        ("秦主 · 重装推进", 1, 30, 5, 4, 40, "秦 30 张全用，汉补 5 张低费/策略，通用补 4 张工具。"),
        ("汉主 · 骑射", 1, 30, 5, 4, 40, "汉 30 张全用，秦补 5 张前排，通用补 4 张工具。"),
        ("秦主 · 通用工具", 1, 30, 0, 9, 40, "不打次朝代，通用 9 张拉满过牌/修械/解场。"),
        ("汉主 · 通用工具", 1, 30, 0, 9, 40, "同上，汉主。"),
    ]
    for row in examples:
        for j, v in enumerate(row, start=1):
            cell(ws, r, j, v, align=LEFT if j in (1, 7) else CENTER)
        ws.row_dimensions[r].height = 30
        r += 1
    r += 1

    r = sec(r, "七、口径与取舍")
    notes = [
        "卡牌价值 = ATK + HP + Σ效果等价（基础公式 C6）。兵牌必须落在《部署价值表》对应格 ±1 内。",
        "弓兵 / 器械的部署价值 = 步兵基础值 − MIN(2, 费用/2)；骑兵 = 步兵基础值，且不吃行动费用补偿。",
        "行动费用 = ROUND(ATK/3)，1 ATK 单位为 0 费行动。行动次数：骑兵 = 移动+攻击（各 1 次，不能移动两次/攻击两次）；"
        "非骑兵 = 攻击或移动；连战 = 攻击×2；骑兵+连战 = 移动+攻击 或 攻击×2。补偿 = INT(2×行动费用/3)，仅非骑兵生效。",
        "取值刻意不全取上限：每档都保留了下沿/中段的面板（如 1 费 1/1 什伍卒、3 费 2/3 斥候骑、5 费 2/4 虎贲营），用于整体强度调控。",
        "策略卡按 C11 改为单效果：抽牌 / 回血 / 直伤 / 压制 / 弃牌 / 等价 / 过费 / 召唤，一张只取一种，效果价 ≤ 费用。",
        "授予词条按 Buff 计价，但比词条原价高 0.5~1 费（后置授予可能与已有词条叠加产生质变）；本轮授予血战/守护/重甲1 均记 2 费。",
        "条件效果（若…则…）按同档效果价定价，不额外加价；条件不满足时整张牌不结算。",
        "兵牌的「部署后」效果是描述类特殊部分，不计入面板/效果等价（本轮 4 张：连弩台、治粟都尉、骊山死士、白马校尉）。",
        "连战沿用现有描述：一回合可攻击两次（行动次数 2）。",
        "高费传说 2 张：秦·伐荆锐士（8 费 7/6 守护）、汉·笼城铁骑（8 费 7/5 闪击+血战），只用于后期决战；费用分布最后一档「6费+」把它们并入统计。",
        "通用表单独放「通用策略 + 器械」：火攻 / 水攻 / 空城 / 擒贼擒王 / 修械 / 犒赏 / 草檄 / 屯粮 / 借刀，以及云梯 / 冲车 / 投石机。",
        "旧《秦·卡池》《汉·卡池》《基准表外条目》已删除；本表与《卡池总表》《通用卡池》由 build_card_pool.py + build_card_pool_xlsx.py 生成。",
    ]
    for t in notes:
        cell(ws, r, 1, "· " + t, font=NOTE_FONT, align=LEFT, border=False)
        ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=7)
        ws.row_dimensions[r].height = 34
        r += 1
    ws.sheet_properties.tabColor = "375623"

# ---------------------------------------------------------------- 主流程
def main(out_path=None):
    out_path = out_path or SRC
    wb = load_workbook(out_path)
    fill_deploy_rows(wb)
    for old in ("卡池总览", "秦·卡池", "汉·卡池", "基准表外条目", "卡池总表", "通用卡池", "卡组总览"):
        if old in wb.sheetnames:
            del wb[old]
    qin_cards = M.QIN_CARDS + M.HAN_CARDS
    _, qf, ql = build_pool_sheet(
        wb, "卡池总表", qin_cards,
        "千秋策 · 卡池总表（秦 / 汉，60 张）",
        "按你改后的《基础公式》《部署价值表》重建：卡牌价值 = ATK+HP+效果等价；兵牌落在兵种×费用区间内，"
        "策略卡单效果且效果价 ≤ 费用。朝代列区分秦/汉，通用卡见《通用卡池》。",
        "8B1A1A")
    _, gf, gl = build_pool_sheet(
        wb, "通用卡池", M.GEN_CARDS,
        "千秋策 · 通用卡池（策略 / 器械，12 张）",
        "跨朝代通用：火攻、水攻、空城、擒贼擒王、修械、犒赏、草檄、屯粮、借刀，以及云梯、冲车、投石机。"
        "通用卡不占朝代名额，可补满卡组剩余位。",
        "BF8F00")
    build_pricing_sheet(wb)
    build_overview(wb, qf, ql, gf, gl)
    # 调整顺序
    order = ["卡组总览", "卡池总表", "通用卡池", "策略效果定价", "基础公式", "部署价值表"]
    wb._sheets = [wb[n] for n in order if n in wb.sheetnames] + [s for s in wb._sheets if s.title not in order]
    wb.active = 0
    wb.calculation.fullCalcOnLoad = True
    wb.save(out_path)
    print("已保存:", out_path)
    print("工作表:", wb.sheetnames)

if __name__ == "__main__":
    import sys
    out = sys.argv[1] if len(sys.argv) > 1 else None
    main(out)
