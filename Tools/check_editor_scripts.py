"""对 Assets 下的 Editor 脚本做一次类型检查。

【为什么需要这个脚本】
  `dotnet restore Assembly-CSharp-Editor.csproj` 在这个工程上会**静默失败**
  (退出码 1,没有任何错误信息),所以 `dotnet build` 那条路对 Editor 程序集走不通 ——
  结果就是 Editor 脚本改错了也编不出来,只能等 Unity 报错,而 Unity 报错要人去翻控制台。

  这里绕开 restore:直接从 Assembly-CSharp-Editor.csproj 里抽 HintPath(Unity 自己生成的
  引用表,278 个),连同 Library/ScriptAssemblies 下已编好的 Assembly-CSharp.dll,
  一起喂给 Roslyn 的 csc 做纯类型检查。

【用法】
  python Tools/check_editor_scripts.py
  类型检查通过 → 退出码 0;有编译错误 → 退出码非 0 并打印错误。

【注意】
  只查 Editor 脚本。运行时脚本请用 `dotnet build Assembly-CSharp.csproj`(在 千秋策/ 下)。
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
        if os.path.exists(os.path.join(cand, "Assembly-CSharp-Editor.csproj")):
            return cand
    raise SystemExit("找不到 Assembly-CSharp-Editor.csproj")


PROJ = find_project_dir()
CSPROJ = os.path.join(PROJ, "Assembly-CSharp-Editor.csproj")

# ---- 1) 从 csproj 抽引用 ----
text = open(CSPROJ, encoding="utf-8").read()
refs = []
missing = []
for p in re.findall(r"<HintPath>(.+?)</HintPath>", text):
    p = p.strip()
    if os.path.exists(p):
        refs.append(p)
    else:
        missing.append(p)

# ---- 2) 主程序集(DeckPreset / CardLibrary / CardData 都在里面)----
main_dll = os.path.join(PROJ, "Library", "ScriptAssemblies", "Assembly-CSharp.dll")
if os.path.exists(main_dll):
    refs.append(main_dll)
else:
    print("警告:没找到 Assembly-CSharp.dll —— 先在 Unity 里编译一次,否则会漏掉对主程序集的解析")

# ---- 3) 待检查的源文件 ----
editor_dir = os.path.join(PROJ, "Assets", "_Project", "Scripts", "Editor")
sources = sorted(glob.glob(os.path.join(editor_dir, "**", "*.cs"), recursive=True))
if not sources:
    raise SystemExit("%s 下没有 .cs" % editor_dir)

print("引用 %d 个%s" % (len(refs), ("(其中 %d 个 HintPath 不存在,已跳过)" % len(missing)) if missing else ""))
print("源文件 %d 个:" % len(sources))
for s in sources:
    print("  · %s" % os.path.basename(s))

# ---- 4) 找 csc ----
csc = None
cands = glob.glob(os.path.expanduser(
    "~/.nuget/packages/microsoft.net.compilers*/**/csc.exe"), recursive=True)
if cands:
    csc = sorted(cands)[-1]
    runner = None
else:
    sdks = sorted(glob.glob(r"C:\Program Files\dotnet\sdk\*"), reverse=True)
    for s in sdks:
        cand = os.path.join(s, "Roslyn", "bincore", "csc.dll")
        if os.path.exists(cand):
            csc = cand
            runner = "dotnet"
            break
if not csc:
    raise SystemExit("找不到 csc")

print("csc: %s" % csc)
print()

# ---- 5) 类型检查 ----
out_dll = os.path.join(os.environ.get("TEMP", "."), "EditorTypeCheck.dll")
argv = ["-nologo", "-target:library", "-langversion:9.0", "-nostdlib+", "-out:" + out_dll]
argv += ["-r:" + r for r in refs]
argv += sources

cmd = (["dotnet", csc] if runner == "dotnet" else [csc]) + argv
res = subprocess.run(cmd, capture_output=True)
out = res.stdout.decode("utf-8", "replace") + res.stderr.decode("utf-8", "replace")
print(out.strip())

if res.returncode == 0:
    print("\n类型检查通过 ✓")
else:
    n_err = out.count("error CS")
    print("\n类型检查失败 ✗  (%d 个编译错误)" % n_err)

sys.exit(res.returncode)
