"""탐색기 명령 처리기 ShellCommand.dll(x64, C 런타임 정적 연결)을 빌드한다.

사용: python -X utf8 ohmyzip/src/ShellCommand/build.py
결과: ohmyzip/src/ShellCommand/bin/x64/ShellCommand.dll (중간 파일은 obj/x64/)
vswhere 로 x64 C++ 도구가 있는 Visual Studio 를 찾고, vcvars64.bat 환경에서 cl·link 를 인자 배열로 실행한다.
실패하면 0 이 아닌 종료 코드로 끝난다.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
SOURCE = HERE / "ShellCommand.cpp"
DEF_FILE = HERE / "ShellCommand.def"
OBJ_DIR = HERE / "obj" / "x64"
BIN_DIR = HERE / "bin" / "x64"
DLL = BIN_DIR / "ShellCommand.dll"
VSWHERE = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
ENV_MARKER = "===SHELLCOMMAND-ENV==="

COMPILE_FLAGS = [
    "/nologo", "/c", "/utf-8", "/std:c++17", "/permissive-", "/W4", "/WX",
    "/MT", "/O2", "/GS", "/guard:cf", "/sdl", "/Zi", "/EHs-c-", "/GR-",
    "/DUNICODE", "/D_UNICODE", "/DWIN32_LEAN_AND_MEAN", "/D_WIN32_WINNT=0x0A00", "/DWINVER=0x0A00",
]
LINK_LIBS = ["kernel32.lib", "user32.lib", "advapi32.lib", "ole32.lib", "shell32.lib", "uuid.lib"]


def fail(message: str) -> None:
    print(f"build.py: {message}", file=sys.stderr)
    sys.exit(1)


def find_vcvars() -> Path:
    if not VSWHERE.is_file():
        fail(f"vswhere 없음: {VSWHERE}")
    args = [str(VSWHERE), "-latest", "-products", "*",
            "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
            "-property", "installationPath", "-utf8"]
    result = subprocess.run(args, capture_output=True, text=True, encoding="utf-8")
    install = result.stdout.strip().splitlines()
    if result.returncode != 0 or not install:
        fail(f"x64 C++ 도구가 있는 Visual Studio 를 찾지 못함 (vswhere 종료 {result.returncode})")
    vcvars = Path(install[0]) / "VC" / "Auxiliary" / "Build" / "vcvars64.bat"
    if not vcvars.is_file():
        fail(f"vcvars64.bat 없음: {vcvars}")
    return vcvars


def msvc_environment(vcvars: Path) -> dict[str, str]:
    # vcvars64.bat 를 부른 cmd 의 환경 변수를 표지 뒤에서 읽는다.
    args = ["cmd.exe", "/d", "/c", "call", str(vcvars), "&&", "echo", ENV_MARKER, "&&", "set"]
    result = subprocess.run(args, capture_output=True, text=True, encoding="mbcs", errors="replace")
    if result.returncode != 0 or ENV_MARKER not in result.stdout:
        print(result.stdout, result.stderr, sep="\n", file=sys.stderr)
        fail(f"vcvars64.bat 실패 (종료 {result.returncode})")
    env: dict[str, str] = {}
    for line in result.stdout.split(ENV_MARKER, 1)[1].splitlines():
        name, sep, value = line.partition("=")
        if sep and name:
            env[name] = value
    return env


def tool(name: str, env: dict[str, str]) -> str:
    # CreateProcess 는 부모의 PATH 로 찾으므로 vcvars 환경의 PATH 에서 전체 경로를 구한다.
    search = next((value for key, value in env.items() if key.upper() == "PATH"), "")
    found = shutil.which(name, path=search)
    if found is None:
        fail(f"{name} 을 vcvars 환경의 PATH 에서 찾지 못함")
    return found


def run(args: list[str], env: dict[str, str]) -> None:
    print("> " + subprocess.list2cmdline(args), flush=True)
    result = subprocess.run(args, env=env, cwd=HERE)
    if result.returncode != 0:
        fail(f"{Path(args[0]).name} 실패 (종료 {result.returncode})")


def main() -> None:
    vcvars = find_vcvars()
    print(f"vcvars: {vcvars}", flush=True)
    env = msvc_environment(vcvars)
    OBJ_DIR.mkdir(parents=True, exist_ok=True)
    BIN_DIR.mkdir(parents=True, exist_ok=True)
    obj = OBJ_DIR / "ShellCommand.obj"
    run([tool("cl.exe", env), *COMPILE_FLAGS, f"/Fo{obj}", f"/Fd{OBJ_DIR / 'vc.pdb'}", str(SOURCE)], env)
    run([tool("link.exe", env), "/nologo", "/DLL", "/MACHINE:X64", "/WX", "/DEBUG", "/OPT:REF", "/OPT:ICF",
         "/DYNAMICBASE", "/NXCOMPAT", "/HIGHENTROPYVA", "/guard:cf",
         f"/DEF:{DEF_FILE}", f"/OUT:{DLL}", f"/PDB:{OBJ_DIR / 'ShellCommand.pdb'}",
         f"/IMPLIB:{OBJ_DIR / 'ShellCommand.lib'}", str(obj), *LINK_LIBS], env)
    if not DLL.is_file():
        fail(f"결과 없음: {DLL}")
    print(f"built {DLL} ({DLL.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
