# -*- coding: utf-8 -*-
"""SpawnRate（波次进度栏）：编译 → 打包 → 装机（一条龙）。

★ 本机踩坑（照抄 TimeStop/build_and_install.py 的结论）：
  `dotnet build` 可能卡死在 NuGet restore（无输出、DLL 不更新）。必须同时做三件事：
    1) 先 `taskkill /F /IM dotnet.exe` 清残留（否则旧进程锁 obj/ 卡死）；
    2) TEMP/TMP/DOTNET_CLI_HOME/NUGET_PACKAGES 全部重定向到工作区内；
    3) 显式 `-p:GodotRefDir=...`（csproj 里被清空的默认值会让引用解析不到，报一屏 CS0246）。

用法：python build_and_install.py [--install]
      不带 --install 只编译；带则继续打包 + 装机 + 清解包缓存。
"""
import os
import shutil
import subprocess
import sys

# --- 本机路径适配（自动注入；换机器只改 mods/_modenv.py）---
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
from _modenv import (  # noqa: E402
    MODS_ROOT, WORKSPACE, REF_DIR, GAME, UNPACK, UD, MODS_DIR, CACHE,
    PY, DOTNET, DOTNET_ROOT, require_ref_dir, ensure_mods, describe,
)
# --- 适配块结束 ---

BASE = _os.path.dirname(_os.path.abspath(__file__))
SRC = os.path.join(BASE, "runtime_src")
LOG = os.path.join(WORKSPACE, ".cache", "build_logs", "spawnrate_build_last.log")

HOME = os.path.join(WORKSPACE, ".cache", "dotnet_home")
TMPD = os.path.join(HOME, "tmp")
NUGET = os.path.join(WORKSPACE, ".cache", "nuget")

# 本 Mod 的唯一程序集身份名（必须与 csproj 的 <AssemblyName> 一致）
ASSEMBLY = "JTYSpawnRate"
PKG_NAME = "SpawnRate"
MOD_ID = "spawnrate"

lines = []


def log(s=""):
    lines.append(str(s))


def run_compile():
    for d in (HOME, TMPD, NUGET):
        os.makedirs(d, exist_ok=True)
    env = dict(os.environ)
    env["DOTNET_ROOT"] = DOTNET_ROOT
    env["DOTNET_CLI_HOME"] = HOME
    env["TEMP"] = TMPD
    env["TMP"] = TMPD
    env["TMPDIR"] = TMPD
    env["NUGET_PACKAGES"] = NUGET
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    env["MSBUILDDISABLENODEREUSE"] = "1"

    _csproj = next((f for f in sorted(os.listdir(SRC)) if f.endswith(".csproj")), None)
    if not _csproj:
        log("[compile] %s 下找不到 .csproj" % SRC)
        return False
    cmd = [DOTNET, "build", os.path.join(SRC, _csproj),
           "-c", "Release",
           "-p:GodotRefDir=" + require_ref_dir(),
           "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false", "-v:q", "-nologo"]
    p = subprocess.run(cmd, cwd=SRC, env=env, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=420)
    log("[compile] RC=%d" % p.returncode)
    if p.stdout:
        log(p.stdout.strip())
    if p.stderr:
        log(p.stderr.strip())
    return p.returncode == 0


def main():
    try:
        subprocess.run(["taskkill", "/F", "/IM", "dotnet.exe"], capture_output=True, timeout=30)
    except Exception:
        pass

    if not run_compile():
        log("编译失败，终止。")
        return 1
    log("[compile] OK -> runtime_src/bin/Release/%s.dll" % ASSEMBLY)

    if "--install" not in sys.argv:
        return 0

    p = subprocess.run([PY, os.path.join(BASE, "build_pmod.py")], capture_output=True,
                       text=True, encoding="utf-8", errors="replace", timeout=180)
    log("[package] RC=%d" % p.returncode)
    log((p.stdout or "").strip())

    dist = os.path.join(BASE, "dist", PKG_NAME + ".pmod")
    if p.returncode != 0 or not os.path.isfile(dist):
        log("打包失败，终止。")
        return 1

    ensure_mods()
    dst = os.path.join(MODS_DIR, PKG_NAME + ".pmod")
    shutil.copyfile(dist, dst)
    log("[install] %s (%d B)" % (dst, os.path.getsize(dst)))

    # 清上一轮解包缓存（只清本 Mod 自己的目录；⚠️ 先确认游戏没在跑）
    if os.path.isdir(CACHE):
        for name in os.listdir(CACHE):
            if name.lower() == MOD_ID:
                src = os.path.join(CACHE, name)
                try:
                    shutil.rmtree(src)
                    log("[cache] removed %s" % src)
                except Exception as ex:
                    log("[cache] remove 失败（已忽略）：%r" % ex)
    return 0


if __name__ == "__main__":
    rc = 0
    try:
        rc = main()
    except Exception as e:
        import traceback
        log("EXC: %r" % e)
        log(traceback.format_exc())
        rc = 1
    os.makedirs(os.path.dirname(LOG), exist_ok=True)
    with open(LOG, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    try:
        sys.stdout.reconfigure(encoding=sys.stdout.encoding or "utf-8", errors="replace")
    except Exception:
        pass
    try:
        print("\n".join(lines))
    except Exception:
        print("\n".join(lines).encode("utf-8", "replace").decode("utf-8", "replace"))
    sys.exit(rc)
