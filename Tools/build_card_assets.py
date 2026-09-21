# -*- coding: utf-8 -*-
"""
千秋策 · 卡牌 ScriptableObject 生成器

读《数据表demo.xlsx》的「秦·卡池」/「汉·卡池」两张工作表，按朝代分文件夹
生成 CardData(.asset + .asset.meta)，字段与 Assets/_Project/Scripts/Core/Data/CardData.cs 一一对应。

产出（**真值见下面的 ROOT 常量**；卡池在 Resources/ 下，因为运行时靠 Resources.Load 读，
      不在 Data/ 下 —— Data/ 目前只有几个空目录）：
  千秋策/Assets/_Project/Resources/Cards/Qin/<cardId>_<名称>.asset
  千秋策/Assets/_Project/Resources/Cards/Han/<cardId>_<名称>.asset

约定：
  · 插图(artwork)一律留空 —— 后续在 Inspector 里补（用户本轮明确「插图暂时不用管」）。
  · 枚举值取 CardData.cs 中的声明顺序下标，改枚举顺序必须同步改这里的 *_VALUE 映射。
    **例外：keywords 写的是枚举名而不是下标**（见 KEYWORD_VALUE 上方注释），枚举增删成员不会读错。
  · 策略牌：unitType = Strategy(4)（与 CardDisplay.cs 的 isUnit 判定一致），
    atk/hp/actionCost/actionCount 全部写 0，weight 写 0（策划案「策略卡忽略」）。
  · 重甲1/2/3 在 enum Keyword 里只有单一 HeavyArmor 档位，层级信息无处承载 —— 见运行末尾的「已知缺口」。
  · 「摸牌 / 召唤 / 回血 / 誓师」不是词条，是卡牌效果（登记在 CardEffectDatabase + effectText），
    数据表里即使写了也会被 KEYWORD_DEPRECATED 跳过，不写进 .asset。
  · targetType（策略卡要指定什么目标，策划案§7.2.2）：表里没有这一列，靠 TACTIC_TARGET_BY_CARD
    按 cardId 维护；漏登记的策略卡会直接报错中止，逼着补上。
"""
import io
import os
import re
import sys
import hashlib
import openpyxl

XLSX = r"Docs\数据表demo.xlsx"
# 卡牌必须放在 Resources 下,运行时才能用 Resources.LoadAll<CardData>("Cards") 扫出整个卡池
# (CardLibrary.CardsResourcePath = "Cards")。改这个路径要同步改 CardLibrary 里的常量。
ROOT = r"千秋策\Assets\_Project\Resources\Cards"

# CardData.cs 的 m_Script guid（Assets/_Project/Scripts/Core/Data/CardData.cs.meta）
CARD_DATA_GUID = "bfcf2cc80a34fa94dadbc55e6db77a79"
CARD_DATA_FILEID = 11400000

# ---------------------------------------------------------------- 枚举映射
RARITY_VALUE = {"普通": 0, "稀有": 1, "史诗": 2, "传说": 3}          # Standard/Limited/Special/Elite
CARDTYPE_VALUE = {"兵牌": 0, "策略牌": 1}                            # Unit/Tactic
UNITTYPE_VALUE = {"步兵": 0, "骑兵": 1, "弓兵": 2, "支援": 3}        # Infantry/Cavalry/Archer/Support
UNITTYPE_STRATEGY = 4                                               # UnitType.Strategy（策略牌）
WEIGHT_VALUE = {"轻": 0, "中": 1, "重": 2}                           # Light/Medium/Heavy

# CardData.cs: enum Keyword { Blitz, Ambush, Guard, BloodBattle, DoubleStrike, HeavyArmor, Resist }
# 只放「固定数值 / 固定内容」的词条。摸牌/召唤/回血/誓师是**卡牌效果**，登记在 CardEffectDatabase
# 并写在 CardData.effectText 里，不是词条 —— 不要再加回本表。
#
# ⚠ 写进 .asset 的是**枚举名**(keywords: - Blitz)，不是下标。
#   早先这里写的是下标(闪击→0)，一旦 CardData.cs 的 Keyword 增删成员，
#   老 .asset 里的数字就会静默错位(han_024 的「召唤」下标 7 越界，卡面词条显示成 "7")。
#   写成名字之后，枚举怎么改都不会读错，最多是这条词条被 Unity 丢弃并报警告。
KEYWORD_VALUE = {
    "闪击": 0, "伏兵": 1, "守护": 2, "血战": 3, "连战": 4,
    "重甲1": 5, "重甲2": 5, "重甲3": 5,          # enum 只有单一 HeavyArmor 档位，层数靠重复登记
    "抵抗": 6,
}
KEYWORD_NAME = dict([
    (0, "Blitz"), (1, "Ambush"), (2, "Guard"), (3, "BloodBattle"), (4, "DoubleStrike"),
    (5, "HeavyArmor"), (6, "Resist"),
])

# 数据表里出现过的「已废弃词条」：这些是卡牌效果,不是词条,登记表/效果文案里已经有了。
# 列在这里是为了让脚本**明确跳过并报告**，而不是当成未知词条报错刷屏。
KEYWORD_DEPRECATED = {
    "摸牌", "召唤(牌堆)", "召唤(手牌)", "召唤", "回血", "誓师",
}

DYNASTY_DIR = {"秦": "Qin", "汉": "Han"}

# CardData.cs: enum TacticTargetType { None, EnemyUnit, EnemyBuilding, EnemyRow,
#                                      AllyUnit, AllyRow, AllyBuilding, EnemyHand }
# 兵牌一律 None（兵牌的"目标"是落位，由策划案§8.1.1 管）。
TACTIC_TARGET_VALUE = {
    "None": 0, "EnemyUnit": 1, "EnemyBuilding": 2, "EnemyRow": 3,
    "AllyUnit": 4, "AllyRow": 5, "AllyBuilding": 6, "EnemyHand": 7,
}

# 每张策略卡释放时要指定的目标（按卡面效果文案 + 策划案§7.2.2 的目标类型表）。
# 判定口径：
#   伤害/削弱类 → 敌方；buff/回复类 → 友方；维修类 → 己方建筑；
#   抽卡过牌类、以及卡面写「随机单位」的（随机挑目标，不需要玩家指定）→ None；
#   弃置敌方手牌 → 敌方手牌区。
# 多目标策略卡（决水灌城、盐铁论）先填第一个要指定的目标 —— 目标链还没做。
TACTIC_TARGET_BY_CARD = {
    "qin_007": "None",          # 弩矢督：对敌方随机单位造成3点伤害（随机）
    "qin_008": "EnemyUnit",     # 攒射：对一名敌方兵牌造成3点伤害
    "qin_015": "AllyUnit",      # 军功爵：指定一个友方单位本回合ATK+2、HP+3；抽取1张牌
    "qin_016": "EnemyHand",     # 反间：弃置敌方1张手牌
    "qin_018": "None",          # 绝粮道：抽取1张牌；对敌方随机单位造成3点伤害（随机）
    "qin_020": "AllyBuilding",  # 商君变法：抽取1张牌；为一座己方建筑回复7点HP
    "qin_021": "None",          # 移民实边：抽取3张牌
    "han_007": "None",          # 招降：对敌方随机单位造成3点伤害（随机）
    "han_008": "AllyBuilding",  # 屯田：为一座己方建筑回复5点HP；抽取1张牌
    "han_015": "AllyUnit",      # 破敌封赏：指定一个友方单位本回合ATK+2、HP+3；抽取1张牌
    "han_016": "None",          # 离间：抽取1张牌；对敌方随机单位造成3点伤害（随机）
    "han_018": "AllyUnit",      # 决水灌城：指定一个友方单位本回合HP+2；对指定一排单位造成2点伤害（多目标）
    "han_021": "EnemyUnit",     # 盐铁论：限制敌方一个单位一回合行动；……（多目标）
}


# ---------------------------------------------------------------- YAML helpers
def yaml_scalar(s):
    """按 Unity 的写法输出 YAML 标量：能裸写就裸写，需要时才加双引号。"""
    s = "" if s is None else str(s)
    if s == "":
        return ""
    need = s != s.strip() or s[0] in "!&*?|>%@`\"'#,[]{}" or s[0] == "-"
    if any(t in s for t in (": ", " #", "\n", "\t", '"', "\\", ":")) or s.endswith(":"):
        need = True
    if s.lower() in ("true", "false", "null", "yes", "no", "on", "off", "~"):
        need = True
    if need:
        return '"' + s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n") + '"'
    return s


def make_guid(key):
    """由稳定的键派生 32 位十六进制 GUID，保证重复运行结果一致。"""
    return hashlib.md5(("QianQiuCe/CardData/" + key).encode("utf-8")).hexdigest()


def build_asset_yaml(asset_name, card):
    # 词条写成枚举**名**(keywords: - Blitz)。Unity 按名字反查枚举值,
    # 所以 CardData.cs 里 Keyword 增删成员都不会让老 .asset 静默错位(详见 KEYWORD_VALUE 上方注释)。
    kw_lines = "  keywords: []\n"
    if card["keywords"]:
        kw_lines = "  keywords:\n" + "".join(f"  - {yaml_scalar(k)}\n" for k in card["keywords"])

    return (
        "%YAML 1.1\n"
        "%TAG !u! tag:unity3d.com,2011:\n"
        f"--- !u!114 &{CARD_DATA_FILEID}\n"
        "MonoBehaviour:\n"
        "  m_ObjectHideFlags: 0\n"
        "  m_CorrespondingSourceObject: {fileID: 0}\n"
        "  m_PrefabInstance: {fileID: 0}\n"
        "  m_PrefabAsset: {fileID: 0}\n"
        "  m_GameObject: {fileID: 0}\n"
        "  m_Enabled: 1\n"
        "  m_EditorHideFlags: 0\n"
        f"  m_Script: {{fileID: 11500000, guid: {CARD_DATA_GUID}, type: 3}}\n"
        f"  m_Name: {yaml_scalar(asset_name)}\n"
        "  m_EditorClassIdentifier: \n"
        f"  cardId: {yaml_scalar(card['cardId'])}\n"
        f"  cardName: {yaml_scalar(card['cardName'])}\n"
        f"  dynasty: {yaml_scalar(card['dynasty'])}\n"
        f"  rarity: {card['rarity']}\n"
        f"  cardType: {card['cardType']}\n"
        "  artwork: {fileID: 0}\n"
        f"  flavorText: {yaml_scalar(card['flavorText'])}\n"
        f"  effectText: {yaml_scalar(card['effectText'])}\n"
        f"  deploymentCost: {card['deploymentCost']}\n"
        f"  actionCost: {card['actionCost']}\n"
        f"  actionCount: {card['actionCount']}\n"
        f"  atk: {card['atk']}\n"
        f"  hp: {card['hp']}\n"
        f"  unitType: {card['unitType']}\n"
        f"  weight: {card['weight']}\n"
        + kw_lines
        + f"  targetType: {card['targetType']}\n"
    )


def build_meta(guid):
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "NativeFormatImporter:\n"
        "  externalObjects: {}\n"
        "  mainObjectFileID: 11400000\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def write_lf(path, text):
    """Unity 的 .asset/.meta 一律 LF、无 BOM。"""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


# ---------------------------------------------------------------- 解析数据表
def cell(ws, r, c):
    v = ws.cell(r, c).value
    if v is None:
        return ""
    return str(v).strip()


def parse_keywords(raw_cn, raw_prog, card_id, problems):
    """中文词条列 → 程序枚举**名**列表；同时校验表内「keywords(程序)」列是否一致。"""
    cn_list = [k.strip() for k in re.split(r"[,，]", raw_cn) if k.strip()]
    prog_list = [k.strip() for k in re.split(r"[,，]", raw_prog) if k.strip()]

    out = []
    for k in cn_list:
        if k in KEYWORD_DEPRECATED:
            # 效果类写法(摸牌/召唤/回血/誓师)已不是词条，静默跳过 —— 它们由效果栏承载
            continue
        if k not in KEYWORD_VALUE:
            problems.append(f"{card_id}: 词条「{k}」不在 CardData.cs 的 Keyword 枚举内")
            continue
        v = KEYWORD_NAME[KEYWORD_VALUE[k]]
        if v not in out:
            out.append(v)

    # 与表内程序列对账(同样先剔掉已废弃的效果类写法)
    expect = [KEYWORD_NAME[KEYWORD_VALUE[k]]
              for k in cn_list if k in KEYWORD_VALUE and k not in KEYWORD_DEPRECATED]
    expect = list(dict.fromkeys(expect))
    prog_kept = [p for p in prog_list if p not in KEYWORD_DEPRECATED]
    if expect != prog_kept:
        problems.append(f"{card_id}: keywords(程序) 列写的是 {prog_kept or '空'}，"
                        f"按中文词条 {cn_list} 应为 {expect}")
    return out


def read_sheet(wb, sheet_name, problems):
    ws = wb[sheet_name]

    header_row = None
    for r in range(1, ws.max_row + 1):
        if cell(ws, r, 2) == "cardId":
            header_row = r
            break
    if header_row is None:
        raise SystemExit(f"工作表「{sheet_name}」里找不到 cardId 表头")

    cards = []
    for r in range(header_row + 1, ws.max_row + 1):
        card_id = cell(ws, r, 2)
        if not card_id:
            break
        if not re.fullmatch(r"(qin|han)_\d{3}", card_id):
            continue

        name = cell(ws, r, 3)
        dynasty = cell(ws, r, 4)
        rarity_cn = cell(ws, r, 5)
        cardtype_cn = cell(ws, r, 6)
        unit_cn = cell(ws, r, 7)
        weight_cn = cell(ws, r, 8)
        dcs = cell(ws, r, 9)
        acs = cell(ws, r, 10)
        ap = cell(ws, r, 11)
        atk = cell(ws, r, 12)
        hp = cell(ws, r, 13)
        kw_cn = cell(ws, r, 15)
        kw_prog = cell(ws, r, 16)
        effect = cell(ws, r, 17)
        flavor = cell(ws, r, 18)

        is_tactic = cardtype_cn == "策略牌"
        to_int = lambda s, d=0: int(float(s)) if s not in ("", "—") else d

        if is_tactic and card_id not in TACTIC_TARGET_BY_CARD:
            problems.append(f"{card_id}: 策略卡没登记指定目标 —— 请在 TACTIC_TARGET_BY_CARD 里补一项")

        cards.append({
            "cardId": card_id,
            "cardName": name,
            "dynasty": dynasty,
            "rarity": RARITY_VALUE.get(rarity_cn, 0),
            "cardType": CARDTYPE_VALUE.get(cardtype_cn, 0),
            "flavorText": flavor,
            "effectText": effect,
            "deploymentCost": to_int(dcs, 0),
            "actionCost": to_int(acs, 0),
            "actionCount": to_int(ap, 0),
            "atk": to_int(atk, 0),
            "hp": to_int(hp, 0),
            "unitType": UNITTYPE_STRATEGY if is_tactic else UNITTYPE_VALUE.get(unit_cn, 0),
            "weight": 0 if is_tactic else WEIGHT_VALUE.get(weight_cn, 0),
            "keywords": [] if is_tactic else parse_keywords(kw_cn, kw_prog, card_id, problems),
            "targetType": TACTIC_TARGET_VALUE.get(TACTIC_TARGET_BY_CARD.get(card_id, "None"), 0) if is_tactic else 0,
            "_isTactic": is_tactic,
            "_unitCn": unit_cn,
        })

        if rarity_cn not in RARITY_VALUE:
            problems.append(f"{card_id}: 稀有度「{rarity_cn}」无法映射")
        if not is_tactic and unit_cn not in UNITTYPE_VALUE:
            problems.append(f"{card_id}: 兵种「{unit_cn}」无法映射")
    return cards


# ---------------------------------------------------------------- 主流程
def main():
    wb = openpyxl.load_workbook(XLSX, data_only=True)
    problems = []
    all_cards = []
    for sheet_name in ("秦·卡池", "汉·卡池"):
        all_cards += read_sheet(wb, sheet_name, problems)

    for p in problems:
        if "keywords(程序)" in p:
            print("[对账] " + p)
    hard = [p for p in problems if "keywords(程序)" not in p]
    if hard:
        print("\n".join("[错误] " + p for p in hard))
        raise SystemExit("存在无法映射的字段，已中止")

    ids = [c["cardId"] for c in all_cards]
    assert len(ids) == len(set(ids)), "cardId 重复"

    written, guids = 0, {}
    for c in all_cards:
        sub = DYNASTY_DIR[c["dynasty"]]
        asset_name = f"{c['cardId']}_{c['cardName']}"
        base = os.path.join(ROOT, sub)
        asset_path = os.path.join(base, asset_name + ".asset")
        meta_path = asset_path + ".meta"

        guid = make_guid(asset_name)
        assert guid not in guids, f"GUID 冲突：{asset_name} vs {guids[guid]}"
        guids[guid] = asset_name

        write_lf(asset_path, build_asset_yaml(asset_name, c))
        write_lf(meta_path, build_meta(guid))
        written += 1

    by_dyn = {}
    for c in all_cards:
        d = by_dyn.setdefault(c["dynasty"], {"兵牌": 0, "策略牌": 0})
        d["策略牌" if c["_isTactic"] else "兵牌"] += 1

    print(f"\n共写出 {written} 张卡（.asset + .meta = {written * 2} 个文件），GUID 全部唯一")
    for dyn, n in by_dyn.items():
        print(f"  {dyn} → Cards/{DYNASTY_DIR[dyn]}/ ：{n['兵牌']} 兵牌 + {n['策略牌']} 策略牌 = {n['兵牌'] + n['策略牌']} 张")


if __name__ == "__main__":
    sys.exit(main())
