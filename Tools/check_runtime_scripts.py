"""对 Assets/_Project/Scripts 下的**运行时**脚本做一次类型检查。

与 check_editor_scripts.py 同一套办法(绕开静默失败的 dotnet restore,
直接抽 csproj 的 HintPath 喂给 Roslyn csc),区别只是:
  · 用 Assembly-CSharp.csproj 的引用表
  · 不定义 UNITY_EDITOR(运行时脚本不该依赖编辑器专用 API)
  · 不引用 Assembly-CSharp.dll 自己(避免和待检查源码里的类型重名)

用法: python Tools/check_runtime_scripts.py
"""
import os
import re
import sys
import glob
import io
import subprocess

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def find_project_dir():
    for cand in (os.getcwd(), REPO, os.path.join(REPO, "千秋策")):
        if os.path.exists(os.path.join(cand, "Assembly-CSharp.csproj")):
            return cand
    raise SystemExit("找不到 Assembly-CSharp.csproj")


PROJ = find_project_dir()
CSPROJ = os.path.join(PROJ, "Assembly-CSharp.csproj")

text = open(CSPROJ, encoding="utf-8").read()
refs, missing = [], []
for p in re.findall(r"<HintPath>(.+?)</HintPath>", text):
    p = p.strip()
    if os.path.exists(p):
        # 别把主程序集自己引进来,否则会与待检查源码里的同名类型冲突
        if os.path.basename(p) == "Assembly-CSharp.dll":
            continue
        refs.append(p)
    else:
        missing.append(p)

scripts_dir = os.path.join(PROJ, "Assets", "_Project", "Scripts")
sources = sorted(p for p in glob.glob(os.path.join(scripts_dir, "**", "*.cs"), recursive=True)
                 if os.sep + "Editor" + os.sep not in p)
if not sources:
    raise SystemExit("%s 下没有运行时 .cs" % scripts_dir)

# Unity 生成的 csproj 只用 HintPath 列引擎与 Assets/Plugins 下的 DLL,包程序集
# (UnityEngine.UI / TextMeshPro)不在里面 —— 不补会报一百多个 CS0246「未能找到类型
# Image / TMP_Text / Tween」。
# DOTween 要引**两个**:DOTween.dll 提供核心类型(Sequence/Tween),
# Assembly-CSharp-firstpass.dll 提供 UI 扩展方法(DOFade/DOAnchorPos/DOColor 所在的
# DOTween.Modules —— 本工程没有独立的 Modules.dll,它被编进了 firstpass)。
# 少任何一个都会报错:只引 firstpass 报「未能找到类型 Sequence」,
# 只引 DOTween.dll 报「CanvasGroup 不包含 DOFade 的定义」。
# firstpass 不依赖运行时程序集,不会成环。
for extra in (
    os.path.join(PROJ, "Library", "ScriptAssemblies", "UnityEngine.UI.dll"),
    os.path.join(PROJ, "Library", "ScriptAssemblies", "Unity.TextMeshPro.dll"),
    os.path.join(PROJ, "Library", "ScriptAssemblies", "Assembly-CSharp-firstpass.dll"),
    os.path.join(PROJ, "Assets", "Plugins", "Demigiant", "DOTween", "DOTween.dll"),
):
    if os.path.exists(extra):
        refs.append(extra)
    else:
        print("警告:缺引用 %s" % extra)

print("工程: %s" % PROJ)
print("引用 %d 个%s" % (len(refs), ("(其中 %d 个 HintPath 不存在,已跳过)" % len(missing)) if missing else ""))
print("运行时源文件 %d 个" % len(sources))

csc, runner = None, None
cands = glob.glob(os.path.expanduser("~/.nuget/packages/microsoft.net.compilers*/**/csc.exe"), recursive=True)
if cands:
    csc = sorted(cands)[-1]
else:
    for s in sorted(glob.glob(r"C:\Program Files\dotnet\sdk\*"), reverse=True):
        cand = os.path.join(s, "Roslyn", "bincore", "csc.dll")
        if os.path.exists(cand):
            csc, runner = cand, "dotnet"
            break
if not csc:
    raise SystemExit("找不到 csc")
print("csc: %s" % csc)
print()

out_dll = os.path.join(os.environ.get("TEMP", "."), "RuntimeTypeCheck.dll")
argv = ["-nologo", "-target:library", "-langversion:9.0", "-nostdlib+", "-out:" + out_dll]
# 只定义玩家平台宏 —— 运行时脚本不该靠 UNITY_EDITOR 分支活着
argv += ["-define:UNITY_2022_3_OR_NEWER", "-define:UNITY_STANDALONE_WIN"]
argv += ["-r:" + r for r in refs]
argv += sources

# 参数太多(219 个引用 + 58 个源文件)会把命令行顶爆:CreateProcess 直接报
# WinError 206「文件名或扩展名太长」。所以走 csc 的响应文件 @file。
# 注意:响应文件里**每行都要自己加引号** —— Unity 装在 "C:\Program Files\..." 下,
# 不引的话 csc 会按空格把路径切成好几段,报一堆 CS2001「未能找到源文件」。
def quote(a):
    return '"' + a.replace('"', '\\"') + '"' if (" " in a or "（" in a) else a


rsp = os.path.join(os.environ.get("TEMP", "."), "RuntimeTypeCheck.rsp")
with open(rsp, "w", encoding="utf-8") as f:
    f.write("\n".join(quote(a) for a in argv))

cmd = (["dotnet", csc] if runner == "dotnet" else [csc]) + ["@" + rsp]
res = subprocess.run(cmd, capture_output=True)
out = res.stdout.decode("utf-8", "replace") + res.stderr.decode("utf-8", "replace")
print(out.strip() or "(无输出)")

if res.returncode == 0:
    print("\n类型检查通过 ✓")
else:
    print("\n类型检查失败 ✗  (%d 个编译错误)" % out.count("error CS"))

sys.exit(res.returncode)
