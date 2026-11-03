# -*- coding: utf-8 -*-
"""千秋策 · 卡牌资产生成器

读《数据表demo.xlsx》的「卡池总表」(秦/汉 各 30) + 「通用卡池」(12)，生成：

  千秋策/Assets/_Project/Resources/Cards/{Qin,Han,Gen}/<cardId>.asset
  以及每张卡对应的 .asset.meta（guid 固定，可重复生成）

另外生成两张**大营卡**（qin_b_01 / han_b_01，cardType=Building），
它们不进稀有度配额，但每个卡组必须固定带 1 张；美术取 Art/Buildings 下的宫殿图。

默认卡组按 4 传说 / 6 史诗 / 10 稀有 / 19 普通 + 1 大营 = 40 张重建。

约定：
  · flavorText 一律留空（描述只放效果文本）。
  · effectText 取表里的「效果」列。
  · 策略牌与建筑卡 unitType = Strategy(4)，atk/hp/actionCost/actionCount = 0，weight = 0。
  · artwork 的 guid = md5("qianqiu/<tag>")，与图片 .meta 里的 guid 一致。
"""
import hashlib
import io
import os
import re

import openpyxl

ROOT_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
XLSX = os.path.join(ROOT_DIR, "Docs", "数据表demo.xlsx")
PROJ = os.path.join(ROOT_DIR, "千秋策", "Assets", "_Project")
CARD_ROOT = os.path.join(PROJ, "Resources", "Cards")
DECK_ROOT = os.path.join(PROJ, "Resources", "Decks")
ART_UNITS = os.path.join(PROJ, "Art", "Units")
ART_TACTICS = os.path.join(PROJ, "Art", "Tactics")
ART_BUILDINGS = os.path.join(PROJ, "Art", "Buildings")
META_TEMPLATE = os.path.join(PROJ, "Art", "Cards", "Cardback.png.meta")

CARD_DATA_GUID = "bfcf2cc80a34fa94dadbc55e6db77a79"
DECK_PRESET_GUID = "5662656d4d7fc4d41b1c59559bf2960f"
NL = "\n"

RARITY = {"普通": 0, "稀有": 1, "史诗": 2, "传说": 3}
CARDTYPE = {"兵牌": 0, "策略牌": 1}
UNITTYPE = {"步兵": 0, "骑兵": 1, "弓兵": 2, "器械": 3, "支援": 3}
WEIGHT = {"轻": 0, "中": 1, "重": 2}
KEYWORD = {"闪击": "Blitz", "伏兵": "Ambush", "守护": "Guard", "血战": "BloodBattle",
           "连战": "DoubleStrike", "重甲1": "HeavyArmor", "重甲2": "HeavyArmor",
           "重甲3": "HeavyArmor", "抵抗": "Resist"}
# ⚠ 写进 .asset 的必须是**枚举的整数值**,不是名字。
#   Unity 的 YAML 里枚举字段本来就是按 int 存的(同一份文件里 rarity: 2 / cardType: 0 就是这个原因),
#   给 List<Keyword> 写 "- Guard" 这种字符串,读的时候解析失败会**静默落回 0**,
#   而 Keyword 的第 0 项是 Blitz —— 表现就是所有卡都显示「闪击」,守护/重甲/伏兵全部失效。
#   顺序必须和 CardData.cs 里的 Keyword 枚举逐项对齐。
KEYWORD_ENUM = {"Blitz": 0, "Ambush": 1, "Guard": 2, "BloodBattle": 3,
                "DoubleStrike": 4, "HeavyArmor": 5, "Resist": 6}
TARGET = {"None": 0, "EnemyUnit": 1, "EnemyBuilding": 2, "EnemyRow": 3,
          "AllyUnit": 4, "AllyRow": 5, "AllyBuilding": 6, "EnemyHand": 7}
DYNA_DIR = {"qin": "Qin", "han": "Han", "gen": "Gen"}
DYNA_CN = {"qin": "秦", "han": "汉", "gen": "通用"}

CAMP_CARDS = [
    ("qin_b_01", "秦·大营", "qin", "qin_camp.png"),
    ("han_b_01", "汉·大营", "han", "han_camp.png"),
]


def guid_of(tag):
    return hashlib.md5(("qianqiu/" + tag).encode("utf-8")).hexdigest()


def yaml_scalar(s):
    s = "" if s is None else str(s)
    if s == "":
        return ""
    need = (s != s.strip() or s[0] in "!&*?|>%@`\"'#,[]{}" or s[0] == "-"
            or any(t in s for t in (": ", " #", "\n", "\t", '"', "\\", ":"))
            or s.lower() in ("true", "false", "null", "yes", "no", "on", "off", "~"))
    if need:
        return '"' + s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n") + '"'
    return s


def pick_target(effect):
    e = effect or ""
    if "随机" in e or "攻击力最高" in e:
        return "None"
    if "手牌" in e and "弃置" in e:
        return "EnemyHand"
    if "抽取" in e or "费用上限" in e:
        return "None"
    if "建筑" in e and "回复" in e:
        return "AllyBuilding"
    if "一排" in e or "两排" in e:
        return "EnemyRow"
    if "友方单位" in e or ("兵" in e and "回复" in e):
        return "AllyUnit"
    if "敌方" in e or "对方" in e or "指定单位" in e:
        return "EnemyUnit"
    return "None"


def read_cards():
    wb = openpyxl.load_workbook(XLSX, data_only=True)
    cards = []
    for sheet in ("卡池总表", "通用卡池"):
        ws = wb[sheet]
        for r in range(6, ws.max_row + 1):
            cid = ws.cell(row=r, column=2).value
            if not isinstance(cid, str) or not re.match(r"^(qin|han|gen)_[ut]_\d{2}$", cid):
                continue
            g = lambda c: ws.cell(row=r, column=c).value
            atk = g(12) or 0
            act_cost = g(10)
            if act_cost is None:                      # 表里是公式 =ROUND(ATK/3,0)
                act_cost = int(round(atk / 3.0))
            cards.append({
                "cardId": cid, "name": g(3), "rarity": RARITY.get(g(5), 0),
                "cardType": CARDTYPE.get(g(6), 0), "unitTypeRaw": g(7), "weightRaw": g(8),
                "cost": g(9) or 0, "actCost": act_cost, "actCount": g(11) or 1,
                "atk": atk, "hp": g(13) or 0, "keywords": g(15) or "",
                "effect": g(17) or "",
            })
    wb.close()
    return cards


def ensure_sprite_meta(png_path):
    meta = png_path + ".meta"
    gid = guid_of("art/" + os.path.basename(png_path)[:-4])
    if os.path.exists(meta):
        txt = io.open(meta, encoding="utf-8").read()
        new = re.sub(r"^guid: [0-9a-f]{32}$", "guid: " + gid, txt, count=1, flags=re.M)
        io.open(meta, "w", encoding="utf-8", newline=NL).write(new)
        return gid
    base = io.open(META_TEMPLATE, encoding="utf-8").read()
    base = re.sub(r"^guid: [0-9a-f]{32}$", "guid: " + gid, base, count=1, flags=re.M)
    io.open(meta, "w", encoding="utf-8", newline=NL).write(base)
    return gid


def card_yaml(c, sprite_guid):
    is_unit = c["cardType"] == 0
    unit_type = UNITTYPE.get(c["unitTypeRaw"], 0) if is_unit else 4
    weight = WEIGHT.get(c["weightRaw"], 0) if is_unit else 0
    kws = []
    for raw in re.split(r"[,，、+ ]+", (c["keywords"] or "").strip()):
        if not raw:
            continue
        # 「重甲2」= 两层重甲:枚举里没有"层数"这个概念,层数是靠**重复项数**编码的
        # (见 BattleRules.HeavyArmorLayers),所以这里要按数字展开成 N 个 HeavyArmor。
        m = re.match(r"^重甲(\d+)$", raw)
        if m:
            kws += ["HeavyArmor"] * int(m.group(1))
            continue
        if KEYWORD.get(raw):
            kws.append(KEYWORD[raw])
    target = TARGET[pick_target(c["effect"])] if c["cardType"] == 1 else 0
    lines = [
        "%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:", "--- !u!114 &11400000",
        "MonoBehaviour:", "  m_ObjectHideFlags: 0",
        "  m_CorrespondingSourceObject: {fileID: 0}", "  m_PrefabInstance: {fileID: 0}",
        "  m_PrefabAsset: {fileID: 0}", "  m_GameObject: {fileID: 0}", "  m_Enabled: 1",
        "  m_EditorHideFlags: 0",
        "  m_Script: {fileID: 11500000, guid: %s, type: 3}" % CARD_DATA_GUID,
        "  m_Name: " + c["cardId"], "  m_EditorClassIdentifier: ",
        "  cardId: " + c["cardId"],
        "  cardName: " + yaml_scalar(c["name"]),
        "  dynasty: " + DYNA_CN[c["cardId"].split("_")[0]],
        "  rarity: %d" % c["rarity"],
        "  cardType: %d" % c["cardType"],
        "  artwork: {fileID: 21300000, guid: %s, type: 3}" % sprite_guid,
        "  flavorText: ",
        "  effectText: " + yaml_scalar(c["effect"]),
        "  deploymentCost: %d" % c["cost"],
        "  actionCost: %d" % (c["actCost"] if is_unit else 0),
        "  actionCount: %d" % (c["actCount"] if is_unit else 0),
        "  atk: %d" % (c["atk"] if is_unit else 0),
        "  hp: %d" % (c["hp"] if is_unit else 0),
        "  unitType: %d" % unit_type,
        "  weight: %d" % weight,
        "  keywords:" + (" []" if not kws else ""),
    ]
    for k in kws:
        lines.append("  - %d" % KEYWORD_ENUM[k])
    lines.append("  targetType: %d" % target)
    return "\n".join(lines) + "\n"


def asset_meta(gid):
    return ("fileFormatVersion: 2\nguid: %s\nNativeFormatImporter:\n  externalObjects: {}\n"
            "  mainObjectFileID: 11400000\n  userData: \n  assetBundleName: \n"
            "  assetBundleVariant: \n" % gid)


def clear_old():
    n = 0
    for dirpath, _, files in os.walk(CARD_ROOT):
        for f in files:
            if f.endswith(".asset") or f.endswith(".asset.meta"):
                os.remove(os.path.join(dirpath, f))
                n += 1
    return n


def compose_deck(pool):
    """4 传说 / 6 史诗 / 10 稀有 / 19 普通 = 39 张，再加 1 张大营，共 40。

    下标 (rarity, want, cap)：
      rarity 0 普通 1 稀有 2 史诗 3 传说（对齐 Rarity 枚举）
      cap = 同名上限（普通 4 / 稀有 3 / 史诗 2 / 传说 1，见 DeckPreset.MaxCopiesOf）

    稀有度只设**上限**（普通 ≤39 / 稀有 ≤12 / 史诗 ≤8 / 传说 ≤4），
    所以这个 4/6/10/19 只是"一套看着舒服的样例"，不是唯一合法解。
    """
    plan = [(3, 4, 1), (2, 6, 2), (1, 10, 3), (0, 19, 4)]
    out = []
    for rarity, want, cap in plan:
        bucket = [c for c in pool if c["rarity"] == rarity and c["cardType"] != 2]
        got, i = 0, 0
        while got < want and bucket and i < 400:
            c = bucket[i % len(bucket)]
            used = sum(1 for x in out if x["cardId"] == c["cardId"])
            if used < cap:
                out.append(c)
                got += 1
            i += 1
    camp = [c for c in pool if c["cardType"] == 2]
    if camp:
        out.append(camp[0])
    return out


def build_deck(name, key, cards):
    path = os.path.join(DECK_ROOT, name + ".asset")
    head = [
        "%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:", "--- !u!114 &11400000",
        "MonoBehaviour:", "  m_ObjectHideFlags: 0",
        "  m_CorrespondingSourceObject: {fileID: 0}", "  m_PrefabInstance: {fileID: 0}",
        "  m_PrefabAsset: {fileID: 0}", "  m_GameObject: {fileID: 0}", "  m_Enabled: 1",
        "  m_EditorHideFlags: 0",
        "  m_Script: {fileID: 11500000, guid: %s, type: 3}" % DECK_PRESET_GUID,
        "  m_Name: " + name, "  m_EditorClassIdentifier: ",
        '  deckName: "' + DYNA_CN[key] + '·标准卡组"',
        "  primaryDynasty: " + DYNA_CN[key],
        '  description: "' + DYNA_CN[key] + ' 40 张（含 1 张大营），由数据表自动生成。"',
        "  cards:",
    ]
    for c in cards:
        head.append("  - {fileID: 11400000, guid: %s, type: 2}" % guid_of("card/" + c["cardId"]))
    # 稀有度上限（DeckQuota）：普通 39 / 稀有 12 / 史诗 8 / 传说 4，大营不计入
    head += ["  quota:",
             "    maxOrdinary: 39",
             "    maxRare: 12",
             "    maxEpic: 8",
             "    maxLegend: 4"]
    io.open(path, "w", encoding="utf-8", newline=NL).write("\n".join(head) + "\n")
    io.open(path + ".meta", "w", encoding="utf-8", newline=NL).write(
        asset_meta(guid_of("deck/" + name)))


def main():
    cards = read_cards()
    print("读到卡牌 %d 张（预期 72）" % len(cards))
    print("清空旧卡牌资产 %d 个文件" % clear_old())

    targets = []
    for c in cards:
        key = c["cardId"].split("_")[0]
        kind = "u" if c["cardId"].split("_")[1] == "u" else "t"
        art_dir = ART_UNITS if kind == "u" else ART_TACTICS
        png = os.path.join(art_dir, c["cardId"] + ".png")
        if not os.path.exists(png):
            print("[缺图]", c["cardId"])
            continue
        sg = ensure_sprite_meta(png)
        out_dir = os.path.join(CARD_ROOT, DYNA_DIR[key])
        os.makedirs(out_dir, exist_ok=True)
        ap = os.path.join(out_dir, c["cardId"] + ".asset")
        io.open(ap, "w", encoding="utf-8", newline=NL).write(card_yaml(c, sg))
        io.open(ap + ".meta", "w", encoding="utf-8", newline=NL).write(
            asset_meta(guid_of("card/" + c["cardId"])))
        if c["cardType"] == 1:
            targets.append((c["cardId"], c["name"], pick_target(c["effect"])))

    for cid, name, key, png in CAMP_CARDS:
        art = os.path.join(ART_BUILDINGS, png)
        if not os.path.exists(art):
            print("[缺图] 大营:", art)
            continue
        sg = ensure_sprite_meta(art)
        camp = {"cardId": cid, "name": name, "rarity": 0, "cardType": 2,
                "unitTypeRaw": "", "weightRaw": "", "cost": 0, "actCost": 0,
                "actCount": 0, "atk": 0, "hp": 0, "keywords": "",
                "effect": "固定卡：开局部署己方大营（25 HP，归零判负）。"}
        out_dir = os.path.join(CARD_ROOT, DYNA_DIR[key])
        os.makedirs(out_dir, exist_ok=True)
        ap = os.path.join(out_dir, cid + ".asset")
        io.open(ap, "w", encoding="utf-8", newline=NL).write(card_yaml(camp, sg))
        io.open(ap + ".meta", "w", encoding="utf-8", newline=NL).write(
            asset_meta(guid_of("card/" + cid)))
        cards.append(camp)
        print("大营卡:", cid)

    os.makedirs(DECK_ROOT, exist_ok=True)
    for key in ("qin", "han"):
        pool = [c for c in cards if c["cardId"].startswith(key + "_")]
        deck = compose_deck(pool)
        build_deck("Default_" + key.capitalize(), key, deck)
        print("卡组 Default_%s：%d 张" % (key.capitalize(), len(deck)))

    print("\n策略卡目标类型（共 %d 张）：" % len(targets))
    for cid, nm, t in targets:
        print("  %-10s %-8s %s" % (cid, nm, t))


if __name__ == "__main__":
    main()
