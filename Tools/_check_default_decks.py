import sys, io, os, re, glob, collections
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

# 路径一律以脚本自身位置为基准,不许用裸相对路径 ——
# 裸相对路径只能在仓库根目录下跑,换个工作目录(比如从桌面上 `python 千秋策/Tools/...`)
# 就直接 FileNotFoundError 崩掉。这里沿用同目录 check_editor_scripts.py 的写法。
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def find_project_dir():
    for cand in (os.getcwd(), REPO, os.path.join(REPO, "千秋策")):
        if os.path.exists(os.path.join(cand, "Assets", "_Project", "Scripts")):
            return cand
    raise SystemExit("找不到 Unity 工程目录(应有 Assets/_Project/Scripts)")


PROJ = find_project_dir()
SRC = os.path.join(PROJ, "Assets", "_Project", "Scripts", "Editor", "DeckAssetGenerator.cs")
src = open(SRC, encoding="utf-8").read()

def grab(name):
    m = re.search(name + r"\s*=\s*\{(.*?)\n    \};", src, re.S)
    if not m:
        raise SystemExit("找不到数组 " + name)
    body = re.sub(r"//[^\n]*", "", m.group(1))
    return re.findall(r'"(han_\d+|qin_\d+)"', body)

# 读卡池真实数据
pool = {}
for p in glob.glob(os.path.join(PROJ, "Assets", "_Project", "Resources", "Cards", "**", "*.asset"),
                   recursive=True):
    t = open(p, encoding="utf-8").read()
    cid = re.search(r"cardId:\s*(\S+)", t).group(1)
    rar = int(re.search(r"rarity:\s*(\d+)", t).group(1))
    dyn = re.search(r'dynasty:\s*"?([^"\r\n]+)"?', t).group(1).strip()
    nm = re.search(r"cardName:\s*(\S+)", t).group(1)
    pool[cid] = (rar, dyn, nm)

# 卡组资产里实际存的 primaryDynasty（**不要硬编码"汉"/"秦"**）。
# 这个检查是补一次真实事故：卡牌与卡组的朝代字段都曾被写成字面量 "\u6C49"（6 个字符，
# 不是「汉」）。两边同时坏掉时，DeckPreset 的 c.dynasty == primaryDynasty 正好互相抵消，
# 计数照常通过 —— 但卡面上会直接显示"\u6C49"，而且只要有一边被重新生成就会立刻错位。
# 所以这里必须直接读文件、直接比对，不能靠计数间接推断。
_DECKS = os.path.join(PROJ, "Assets", "_Project", "Resources", "Decks")
DECK_FILE = {"汉": os.path.join(_DECKS, "Default_Han.asset"),
             "秦": os.path.join(_DECKS, "Default_Qin.asset")}


def read_primary_dynasty(dyn):
    """读卡组资产里的 primaryDynasty 原值（保留字面量转义，便于判断）。"""
    d = open(DECK_FILE[dyn], encoding="utf-8").read()
    m = re.search(r"primaryDynasty:\s*(.+)", d)
    return m.group(1).strip() if m else ""

RAR = {0: "普通", 1: "稀有", 2: "史诗", 3: "传说", 4: "英雄"}
QUOTA = {"普通": 12, "稀有": 8, "史诗": 4, "传说": 2}
MAXCOPY = {0: 4, 1: 3, 2: 2, 3: 1, 4: 1}

allok = True
for label, arr, primary in (("汉套", grab("HanDeck"), "汉"), ("秦套", grab("QinDeck"), "秦")):
    print("=" * 52)
    print("%s (主朝代 %s)" % (label, primary))
    print("=" * 52)
    ok = True

    def chk(cond, text):
        global ok
        print("  [%s] %s" % ("✓" if cond else "✗", text))
        if not cond:
            ok = False

    chk(len(arr) == 30, "总张数 %d / 30" % len(arr))

    byrar, bydyn = collections.Counter(), collections.Counter()
    unknown = []
    for cid in arr:
        if cid not in pool:
            unknown.append(cid)
            continue
        r, d, n = pool[cid]
        byrar[RAR[r]] += 1
        bydyn[d] += 1
    if unknown:
        chk(False, "卡池里找不到: %s" % unknown)

    for k, want in QUOTA.items():
        got = byrar[k]
        chk(got >= want if k != "传说" else got == want, "%s %d (配额 %d)" % (k, got, want))

    flex = byrar["普通"] + byrar["稀有"] + byrar["史诗"] - 12 - 8 - 4
    chk(0 <= flex <= 4, "弹性位占用 %d / 4" % flex)

    chk(bydyn[primary] >= 25, "主朝代 %s %d / 25" % (primary, bydyn[primary]))
    sec = sum(v for k, v in bydyn.items() if k != primary)
    chk(sec <= 5, "次朝代 %d / 5" % sec)
    sec_leg = sum(1 for cid in arr if cid in pool and pool[cid][1] != primary and pool[cid][0] in (3, 4))
    chk(sec_leg == 0, "次朝代传说 %d (须 0)" % sec_leg)

    over = [(c, n, MAXCOPY[pool[c][0]]) for c, n in collections.Counter(arr).items()
            if c in pool and n > MAXCOPY[pool[c][0]]]
    chk(not over, "同名超限: %s" % (over if over else "无"))

    # ---- 朝代字段的写法检查（见 DECK_FILE 上方的说明）----
    raw_deck = read_primary_dynasty(primary)
    chk(raw_deck.strip('"') == primary,
        "卡组 primaryDynasty 取值正确（%s）" % (raw_deck or "缺失"))

    literal = sorted(c for c in arr
                     if c in pool and "\\u" in pool[c][1])
    chk(not literal, "卡牌 dynasty 无字面量转义: %s" % (literal if literal else "无"))

    mismatch = sorted(c for c in arr
                      if c in pool and pool[c][1] != primary and "\\u" not in pool[c][1]
                      and pool[c][1] not in ("秦", "汉"))
    chk(not mismatch, "卡牌 dynasty 取值合法: %s" % (mismatch if mismatch else "无"))

    print("  朝代分布: %s" % dict(bydyn))
    print("  稀有度分布: %s" % dict(byrar))
    print("  → %s\n" % ("这张表合法" if ok else "这张表不合法,要改"))
    allok = allok and ok

print("=" * 52)
print("总体: %s" % ("两张表都满足 §5 全部规则" if allok else "还有问题"))
sys.exit(0 if allok else 1)
