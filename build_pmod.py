# -*- coding: utf-8 -*-
"""打包 SpawnRate.pmod（zip + 根 mod.json）。

硬护栏（照技能文档）：
  · mod.json 必须在【根】且【唯一】（第 0 个条目）
  · Runtime/ 下只允许 ModAssembly.dll（多一个可执行文件 → 整包被拒）
  · resources 列表必须与实际包内文件一致
  · 编译产物必须与 Runtime/ModAssembly.dll 一致（防止"改了代码却打进旧 DLL"的假修复）
"""
import hashlib
import json
import os
import shutil
import sys
import zipfile

# --- 本机路径适配 ---
import os as _os
import sys as _sys

_MROOT = _os.path.dirname(_os.path.abspath(__file__))
while not _os.path.isfile(_os.path.join(_MROOT, "_modenv.py")):
    _p = _os.path.dirname(_MROOT)
    if _p == _MROOT:
        break
    _MROOT = _p
if _MROOT not in _sys.path:
    _sys.path.insert(0, _MROOT)
# --- 适配块结束 ---

BASE = _os.path.dirname(_os.path.abspath(__file__))
RUNTIME = os.path.join(BASE, "Runtime")
BINSRC = os.path.join(BASE, "runtime_src", "bin", "Release", "JTYSpawnRate.dll")
OUT_DIR = os.path.join(BASE, "dist")
OUT = os.path.join(OUT_DIR, "SpawnRate.pmod")


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    if not os.path.isfile(os.path.join(BASE, "mod.json")):
        print("缺少 mod.json")
        return 1
    os.makedirs(RUNTIME, exist_ok=True)
    runtime_dll = os.path.join(RUNTIME, "ModAssembly.dll")
    if not os.path.isfile(BINSRC):
        print("缺少编译产物：", BINSRC, "—— 先跑 build_and_install.py")
        return 1
    if (not os.path.isfile(runtime_dll)) or md5(BINSRC) != md5(runtime_dll):
        shutil.copyfile(BINSRC, runtime_dll)
        print("[护栏0] 已用最新编译产物覆盖 Runtime/ModAssembly.dll ->", md5(runtime_dll))

    man = json.load(open(os.path.join(BASE, "mod.json"), encoding="utf-8"))

    # 护栏 1：Runtime/ 只许有 ModAssembly.dll
    names = sorted(os.listdir(RUNTIME))
    bad = [n for n in names if n != "ModAssembly.dll"]
    if bad:
        print("Runtime/ 下有非法文件（会被整包拒收）:", bad)
        return 2

    # Resources/*.png（可选）
    res_dir = os.path.join(BASE, "Resources")
    art = []
    if os.path.isdir(res_dir):
        art = sorted("Resources/" + n for n in os.listdir(res_dir) if n.lower().endswith(".png"))

    # 护栏 2：resources 必须与实际内容一致
    actual = sorted(["Runtime/ModAssembly.dll"] + art, key=str.lower)
    declared = sorted(man.get("resources", []), key=str.lower)
    if actual != declared:
        print("resources 不一致\n  实际:", actual, "\n  声明:", declared)
        return 3

    # 护栏 3：runtime 三件套
    if man.get("runtimeAssembly") != "Runtime/ModAssembly.dll":
        print("runtimeAssembly 必须恰好是字面量 Runtime/ModAssembly.dll")
        return 4
    if not isinstance(man.get("runtimeApiVersion"), int) or man.get("runtimeApiVersion") < 1:
        print("runtimeApiVersion 非法")
        return 5

    os.makedirs(OUT_DIR, exist_ok=True)
    with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(os.path.join(BASE, "mod.json"), "mod.json")
        z.write(runtime_dll, "Runtime/ModAssembly.dll")
        for _rel in art:
            z.write(os.path.join(BASE, _rel.replace("/", os.sep)), _rel)

    with zipfile.ZipFile(OUT) as z:
        nl = z.namelist()
    print("OK ->", OUT)
    print("  大小:", os.path.getsize(OUT), "字节")
    print("  内容:", nl)
    print("  包内 DLL md5:", md5(runtime_dll), "（应与 bin/Release 一致）")
    if nl.count("mod.json") != 1 or nl[0] != "mod.json":
        print("  ⚠️ mod.json 不在根或不止一个")
        return 6
    return 0


if __name__ == "__main__":
    sys.exit(main())
