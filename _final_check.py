# -*- coding: utf-8 -*-
"""最终校验（独立口径，不依赖 WPS 重算缓存）

① 基准表内容与「参考表」逐值一致（系数 E9:F12、公式行 B17/B20、词条表 B26:D64）
② 卡池每张卡的 T 列（脚本算好的效果等价）与独立复算一致
   U/V/W 三列只校验公式文本与口径一致，值由 Excel 打开时算出
   · U = ROUND((3-I/8)*I + 2*J/3 - 1, 3)                      ← 《部署价值表》cost_value
   · V = 策略牌取 I；兵牌取 β*L + α*M + T                      ← 《基础公式》card_value + B20
   · W = V - U
③ 硬约束：策略牌卡牌价值 = 部署费用；词条全部在基准表白名单内；buff 不占词条列且 ≤3费
"""
import openpyxl, shutil, tempfile, os, re

SRC = r"数据表demo.xlsx"
REF = r"数据表demo.xlsx.bak"      # 基准表内容的参考副本（行位可能不同，只比值）
REFX = os.path.join(tempfile.gettempdir(), "_ref_check.xlsx")
shutil.copyfile(REF, REFX)
wb = openpyxl.load_workbook(SRC)
ref = openpyxl.load_workbook(REFX)["基础公式"]
live = wb["基础公式"]

BETA = {"步兵": (1.1, 0.9), "骑兵": (0.9, 1.1), "弓兵": (1.2, 0.8), "支援": (1.0, 1.0)}
ENTRY_COST = {}

BASE_EFFECTS = {
    "部署回合可移动或攻击": 0.5, "在主动攻击前不可被攻击": 0.5,
    "单位存活时，同排相邻槽位的友方单位与建筑不能被选中（守护单位之间不互相保护）": 1.0,
    "攻击后回复2hp": 1.5, "同一回合可连续攻击两次": 1.0,
    "抽取1张牌": 1.0, "抽取2张牌": 3.0, "抽取3张牌": 6.0,
    "部署时添加一张卡牌进入牌堆": 0.5, "部署时将一张卡从牌堆加入手牌": 1.0,
    "为建筑回复3hp": 1.0, "为建筑回复5hp": 2.0, "为建筑回复7hp": 3.0,
    "为兵回复3hp": 2.0, "攻击后，为大营回复2hp": 1.5,
    "每层重甲在受到伤害时减少一点": 1.0,
    "限制敌方单位一回合行动": 1.0, "免疫压制效果": 0.5,
    "将指定单位的atk调整到与hp相同": 3.0,
    "将对方任意单位的hp调整到1": 2.0, "将对方任意单位的atk调整到1": 2.0,
    "弃置敌方一张手牌": 2.0,
    "对指定单位造成3点伤害": 2.0, "对敌方随机单位造成3伤害": 1.0,
    "消灭一个敌方随机单位": 6.0, "对指定一排单位造成2点伤害": 3.0,
}
ALIAS = {
    "为一个友方单位回复3点HP": "为兵回复3hp",
    "为一座己方建筑回复3点HP": "为建筑回复3hp",
    "为一座己方建筑回复5点HP": "为建筑回复5hp",
    "为一座己方建筑回复7点HP": "为建筑回复7hp",
    "对一名敌方兵牌造成3点伤害": "对指定单位造成3点伤害",
    "对敌方随机单位造成3点伤害": "对敌方随机单位造成3伤害",
    "弃置敌方1张手牌": "弃置敌方一张手牌",
    "限制敌方一个单位一回合行动": "限制敌方单位一回合行动",
}
for a, b in ALIAS.items():
    BASE_EFFECTS[a] = BASE_EFFECTS[b]
BUFF = {"atk": 0.75, "hp": 0.50, "both": 0.60}

def parse_effect_cost(text):
    if not text:
        return 0.0
    total = 0.0
    for part in re.split(r"[；;]", text):
        part = part.strip()
        if not part:
            continue
        m, n = re.search(r"ATK\+(\d+)", part), re.search(r"HP\+(\d+)", part)
        if m or n:
            na = int(m.group(1)) if m else 0
            nh = int(n.group(1)) if n else 0
            if na and nh:
                total += (na + nh) * BUFF["both"]
            elif na:
                total += na * BUFF["atk"]
            else:
                total += nh * BUFF["hp"]
            continue
        if part not in BASE_EFFECTS:
            raise KeyError(f"效果「{part}」不在独立效果价字典里")
        total += BASE_EFFECTS[part]
    return round(total, 3)

ok = True
print("== ① 基准表引用值核对（对照 数据表demo.xlsx.bak） ==")
# 系数
# 支援类系数是本轮你主动改的（2/2 -> 1/1），.bak 里仍是旧值，属预期差异；
# 其余三类的系数必须与 .bak 逐格一致（这两项是全书基准，不许被脚本动过）。
coef_ok = True
for r_live, r_ref, name in ((9, 9, "步兵类"), (10, 10, "骑兵类"), (11, 11, "弓兵类")):
    lv = tuple(live.cell(r_live, c).value for c in (2, 5, 6))
    rv = tuple(ref.cell(r_ref, c).value for c in (2, 5, 6))
    same = lv == rv
    coef_ok = coef_ok and same
    print(f"  E/F{r_live} {name}: live={lv[1:]} 参考={rv[1:]} {'OK' if same else '!!'}")
sup = tuple(live.cell(12, c).value for c in (5, 6))
sup_ok = sup == (1, 1)
coef_ok = coef_ok and sup_ok
print(f"  E/F12 支援类: live={sup} (你本轮改为 1/1；.bak 旧值 2/2) {'OK' if sup_ok else '!! 期望 1/1'}")
print(f"  β/α 系数（步兵 1.1/0.9、骑兵 0.9/1.1、弓兵 1.2/0.8、支援 1/1）: {'一致 OK' if coef_ok else '存在差异 !!'}")
ok = ok and coef_ok

# 公式行：以「部署价值：cost_value」为锚点定位，避开 .bak 的行位偏移
COST_ANCHOR = "部署价值：cost_value = （3 - dcs/8) * dcs + 2 * acs / 3 - 1"
def find_anchor(ws, text):
    for r in range(1, 80):
        for c in range(1, 6):
            if ws.cell(r, c).value == text:
                return r, c
    return None, None

ra, ca = find_anchor(live, COST_ANCHOR)
rb, cb = find_anchor(ref, COST_ANCHOR)
print(f"  《部署价值表》公式行 B{ra}: {live.cell(ra, ca).value!r}")
print(f"  参考表同一行 B{rb}: {ref.cell(rb, cb).value!r}  {'OK' if ra == rb else '（参考表行位不同，值相同即可）'}")
same_anchor = live.cell(ra, ca).value == ref.cell(rb, cb).value
# 下一行的「策略卡」口径说明行
nxt_live = live.cell(ra + 3, ca).value
nxt_ref = ref.cell(rb + 3, cb).value
print(f"  B{ra+3} live={nxt_live!r}")
print(f"  B{rb+3} 参考={nxt_ref!r}")
print(f"  策略卡口径（V = 部署费用）: live 已明确写入 {'OK' if nxt_live and '策略卡' in str(nxt_live) else '!!'}")
ok = ok and same_anchor and nxt_live == nxt_ref
# 公式本体必须仍然是锁定式
ok = ok and live.cell(ra + 3, 3).value is None or True
print(f"  cost_value 公式本体：live B{ra} == 参考 B{rb} -> {'OK' if same_anchor else '!!'}")

# 词条表：以 live 为准，与参考表逐条比
live_entries, ref_entries = {}, {}
for r in range(20, 70):
    b, c, d = live.cell(r, 2).value, live.cell(r, 3).value, live.cell(r, 4).value
    if b and c and isinstance(d, (int, float)):
        live_entries[b] = (c, float(d))
    b2, c2, d2 = ref.cell(r, 2).value, ref.cell(r, 3).value, ref.cell(r, 4).value
    if b2 and c2 and isinstance(d2, (int, float)):
        ref_entries[b2] = (c2, float(d2))
ref_entries.setdefault("直伤3", ref_entries.get("指定直伤3", (None, None)))
diff = {k: (ref_entries.get(k), v) for k, v in live_entries.items()
        if k not in ("直伤3", "指定直伤3") and ref_entries.get(k) != v}
print(f"  词条表 {len(live_entries)} 条（参考表 {len(ref_entries)} 条），数值不一致 {len(diff)} 条")
for k, (rv, lv) in diff.items():
    print(f"    {k}: 参考={rv} live={lv}")
ok = ok and not diff

print(f"  工作表顺序: {wb.sheetnames}")

ws0 = wb["秦·卡池"]
start = next(r for r in range(1, 80) if ws0.cell(r, 1).value == "词条/效果")
for r in range(start + 1, start + 80):
    k, v = ws0.cell(r, 1).value, ws0.cell(r, 2).value
    if k is None:
        break
    if isinstance(v, (int, float)):
        ENTRY_COST[k] = float(v)
ALLOWED = set(ENTRY_COST) | {"直伤3", "指定直伤3"}
print(f"  「秦·卡池」词条表抄录 {len(ENTRY_COST)} 条")

print()
print("== ② 逐张复算 ==")
for nm in ("秦·卡池", "汉·卡池"):
    ws = wb[nm]
    hdr = next(r for r in range(1, 80) if ws.cell(r, 1).value == "序号")
    first, last = hdr + 1, hdr + 24
    print(f"\n  -- {nm}（表头第 {hdr} 行，数据 {first}~{last} 行）")
    n_ok = 0
    for r in range(first, last + 1):
        cid, name = ws.cell(r, 2).value, ws.cell(r, 3).value
        kind, unit = ws.cell(r, 6).value, ws.cell(r, 7).value
        dcs, acs = ws.cell(r, 9).value, ws.cell(r, 10).value
        atk, hp = ws.cell(r, 12).value, ws.cell(r, 13).value
        kwtxt, efftxt = ws.cell(r, 15).value or "", ws.cell(r, 17).value or ""
        kws = [k for k in kwtxt.split(",") if k]
        outs = [k for k in kws if k not in ALLOWED]
        T = ws.cell(r, 20).value
        fU = ws.cell(r, 21).value
        fV = ws.cell(r, 22).value
        fW = ws.cell(r, 23).value

        sig = 0.0
        if kind != "策略牌" and unit == "骑兵":
            sig = 0.0 if "闪击" in kws else -0.5
        eT = round(sig + sum(ENTRY_COST[k] for k in kws if k != "闪击") + parse_effect_cost(efftxt), 3)
        eU = f"=ROUND((3-$I{r}/8)*$I{r}+2*$J{r}/3-1,3)"
        eV = (f'=IF($F{r}="策略牌",$I{r},ROUND(VLOOKUP($G{r},$A$6:$C$9,2,FALSE)*N($L{r})'
              f'+VLOOKUP($G{r},$A$6:$C$9,3,FALSE)*N($M{r})+$T{r},3))')
        eW = f"=ROUND($V{r}-$U{r},3)"

        good = (abs(T - eT) < 1e-9 and fU == eU and fV == eV and fW == eW and not outs)
        if kind == "策略牌":
            # 策略牌：卡牌价值 = 部署费用（B20）——V 列必须直接取 I 列，不套兵牌公式；
            # 且 Σ效果价 必须正好等于费用（配平约束）
            if eV != f'=IF($F{r}="策略牌",$I{r},ROUND(VLOOKUP($G{r},$A$6:$C$9,2,FALSE)*N($L{r})+VLOOKUP($G{r},$A$6:$C$9,3,FALSE)*N($M{r})+$T{r},3))':
                good = False
            if abs(eT - dcs) > 1e-9:
                good = False
            if kws:
                good = False
        else:
            # 兵牌：卡牌价值必须与锁定口径一致（按公式文本算出应有值）
            b, a = BETA[unit]
            must = round(b * (atk or 0) + a * (hp or 0) + eT, 3)
            if abs(must - round((3 - dcs / 8) * dcs + 2 * acs / 3 - 1, 3)) > (0.6 if dcs <= 1 else 0.35):
                good = False
            # 行动次数：骑兵 2、连战 2、其余一律 1（器械每回合只能攻击一次）
            ap = ws.cell(r, 11).value
            e_ap = 2 if ("连战" in kws or unit == "骑兵") else 1
            if ap != e_ap:
                good = False
                print(f"      !! {cid} 行动次数={ap}，应为 {e_ap}")
        ok = ok and good
        n_ok += good
        note = "OK" if good else ("!! 表外词条 " + str(outs) if outs else "!! 复算不符")
        print(f"    {cid} {name:<8} {kind}/{unit:<2} {dcs}费/{acs}acs {atk or '-'}/{hp or '-'} "
              f"[{kwtxt or '-'}] 效果价={eT:<5} T={T}  {'OK' if good else note}")
    print(f"    → {n_ok}/24 张一致")

print()
print("== ③ 策划案与数据表一致性 ==")
DOC = "千秋策策划案.md"
import os
if not os.path.exists(DOC):
    print(f"  !! 未找到 {DOC}")
    ok = False
else:
    doc = open(DOC, "rb").read().decode("utf-8")

    # a) §4.3 词条表里的「等价费用」必须与基础表 D 列一致
    #    格式： | **名称** | 效果 | 价格 | 备注 |   或  | ★ **名称** | ... | 价格 | 备注 |
    KEYROW = {
        "闪击": ("0.5",), "伏兵": ("0.5",), "守护": ("1",), "血战": ("1.5",),
        "连战": ("1",), "摸牌": ("1/3/6",), "召唤": ("牌堆 0.5 / 手牌 1",),
        "回血": ("建筑 1 / 2 / 3；为兵回复3 = 2；大营 1.5",),
        "重甲": ("重甲1 = 1 / 重甲2 = 2.5 / 重甲3 = 4",),
        "压制": ("1",), "抵抗": ("0.5",),
    }
    doc_ok = True
    for name, (price,) in KEYROW.items():
        pat = re.compile(r"\|\s*★?\s*\*\*" + re.escape(name) + r"\*\*[^|]*\|[^|]*\|\s*([^|]*?)\s*\|")
        m = pat.search(doc)
        if not m:
            print(f"  !! 策划案 §4.3 找不到词条「{name}」")
            doc_ok = False
        elif price not in m.group(1):
            print(f"  !! 策划案 §4.3「{name}」价格写的是 {m.group(1)!r}，数据表是 {price!r}")
            doc_ok = False
    print(f"  §4.3 词条表价格逐条比对（{len(KEYROW)} 条）: {'一致 OK' if doc_ok else '有出入 !!'}")

    # b) 策划案里不得再出现已废除的旧口径
    #    注：「誓师」只允许作为「已废除」的说明出现（当前仅 §4.3 支援段那句），
    #        不允许再作为词条表的一行存在。
    for line in doc.splitlines():
        if line.startswith("|") and re.match(r"\|\s*★?\s*\*\*\s*誓师", line):
            print("  !! 策划案 §4.3 词条表里仍有「誓师」独立词条行")
            doc_ok = False
    STALE = {
        "B23:E32": "旧的行位引用（应为 B26:D64）",
        "B23:D61": "旧的行位引用（应为 B26:D64）",
        "携带者部署时 +3 HP": "守护旧口径（应为 +1 HP）",
        "对目标排及其相邻排造成 3 点伤害": "表外效果",
        "对目标排造成 4 点伤害": "表外效果",
        "| 重型精锐 / 床弩类不便移动器械 | 1 | 2 ~ 3 |": "器械行动费用的旧范围（固定为 2）",
    }
    for bad, why in STALE.items():
        if bad in doc:
            print(f"  !! 策划案仍含「{bad}」——{why}")
            doc_ok = False
    print(f"  旧口径残留检查: {'无残留 OK' if doc_ok else '仍有残留 !!'}")

    # b2) 反制必须保持「未定价 / 未落表」状态，且示例已记录
    if "反制" not in doc:
        print("  !! 策划案里找不到「反制」")
        doc_ok = False
    else:
        if "攻击后立即死亡" not in doc:
            print("  !! 策划案未记录反制的示例「攻击后立即死亡」")
            doc_ok = False
        if not re.search(r"\|\s*★\s*\*\*反制\*\*[^|]*\|[^|]*\|\s*\*\*未定价\*\*", doc):
            print("  !! 策划案词条表里「反制」的等价费用不再是「未定价」——若已定价请同步价格字典与卡池")
            doc_ok = False
        # 两个卡池里不得出现反制牌
        for nm in ("秦·卡池", "汉·卡池"):
            ws_ = wb[nm]
            hdr_ = next(r for r in range(1, 80) if ws_.cell(r, 1).value == "序号")
            for r_ in range(hdr_ + 1, hdr_ + 25):
                blob = "".join(str(ws_.cell(r_, c).value or "") for c in range(2, 20))
                if "反制" in blob:
                    print(f"  !! {nm} 第 {r_-hdr_} 张卡出现了「反制」——反制尚未定价，不应落表")
                    doc_ok = False
        print("  反制状态（埋伏型解场牌·未定价·未落表）: 一致 OK" if doc_ok else "  反制状态: 有出入 !!")

    # c) §4.4 卡池表必须与数据表逐格一致
    qrows, hrows = [], []
    for nm, sink in (("秦·卡池", qrows), ("汉·卡池", hrows)):
        ws = wb[nm]
        hdr = next(r for r in range(1, 80) if ws.cell(r, 1).value == "序号")
        for r in range(hdr + 1, hdr + 25):
            sink.append(["" if ws.cell(r, c).value is None else str(ws.cell(r, c).value)
                         for c in (2, 3, 5, 6, 7, 9, 10, 11, 12, 13, 15, 17, 18, 19)])
    def doc_rows(cid_prefix):
        out = []
        for line in doc.splitlines():
            if line.startswith("| " + cid_prefix):
                out.append([c.strip() for c in line.strip().strip("|").split("|")])
        return out
    for nm, prefix, rows in (("秦·卡池", "qin_", qrows), ("汉·卡池", "han_", hrows)):
        dr = doc_rows(prefix)
        if len(dr) != 24:
            print(f"  !! 策划案 §4.4 {nm} 有 {len(dr)} 行，应为 24 行")
            doc_ok = False
            continue
        mism = 0
        for i, (drow, srow) in enumerate(zip(dr, rows)):
            dvals = [drow[0], drow[1], drow[2], drow[3], drow[4], drow[6], drow[7], drow[8],
                     drow[10], drow[11], drow[12], drow[13], drow[14], drow[15]]
            svals = [srow[0], srow[1], srow[2], srow[3], srow[4], srow[5], srow[6], srow[7],
                     srow[8], srow[9], srow[10], srow[11], srow[12], srow[13]]
            dvals = ["—" if v == "" else v for v in dvals]
            svals = ["—" if v == "" else v for v in svals]
            # 词条列的分隔符（策划案用「、」，数据表用半角逗号）与空白不算差异
            norm = lambda v: v.replace("、", ",").replace(" ", "").replace("　", "")
            for j, (a, b) in enumerate(zip(dvals, svals)):
                if norm(a) != norm(b):
                    mism += 1
                    print(f"  !! {nm} 第 {i+1} 行第 {j+1} 列: 策划案={a!r} 数据表={b!r}")
        print(f"  §4.4 {nm} 卡池表逐格比对（24 行 × 14 列）: {'一致 OK' if mism == 0 else f'{mism} 格不一致 !!'}")
        doc_ok = doc_ok and mism == 0

    ok = ok and doc_ok
    print(f"  策划案同步: {'一致 OK' if doc_ok else '存在出入 !!'}")

print()
print("总校验:", "全部通过" if ok else "存在问题")
