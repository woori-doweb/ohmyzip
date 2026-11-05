"""Build an ohMypic release candidate.

1. dotnet publish (Release, win-x64, framework-dependent, no debug symbols) -> releases/ohMypic-<version>/
   Version, AssemblyVersion, FileVersion and InformationalVersion are all set to <version>.
2. Add README.txt (version filled in).
3. Write SHA256SUMS.txt, releases/ohMypic-<version>-win-x64.zip and releases/ohMypic-<version>-win-x64.zip.sha256.
   The zip holds one top folder, ohMypic-<version>/ (the auto-updater relies on this layout).
Fails instead of overwriting when the release folder, zip or sha256 already exists.
<version> must be three numbers, major.minor.patch (for example 0.1.3).
Uses dotnet from PATH, else C:\\Program Files\\dotnet\\dotnet.exe.
Run: python -X utf8 ohmypic/packaging/package.py <version>
"""
import hashlib
import pathlib
import re
import shutil
import subprocess
import sys
import zipfile

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent
DOTNET = shutil.which("dotnet") or r"C:\Program Files\dotnet\dotnet.exe"
VERSION_RE = re.compile(r"\d+\.\d+\.\d+")
NAME = "ohMypic"


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def main(version):
    if not VERSION_RE.fullmatch(version):
        raise SystemExit(f"not a version: {version!r} (example: 0.1.3)")
    out = ROOT / "releases" / f"{NAME}-{version}"
    pkg = ROOT / "releases" / f"{NAME}-{version}-win-x64.zip"
    pkg_sha = pkg.with_name(pkg.name + ".sha256")
    for p in (out, pkg, pkg_sha):
        if p.exists():
            raise SystemExit(f"already exists: {p}")
    cmd = [DOTNET, "publish", str(ROOT / "src" / "OhMyPic.App"), "-c", "Release", "-r", "win-x64", "--self-contained", "false",
           "-o", str(out), "-nologo", "-p:DebugType=None", "-p:DebugSymbols=false",
           f"-p:Version={version}", f"-p:AssemblyVersion={version}.0", f"-p:FileVersion={version}.0",
           f"-p:InformationalVersion={version}"]
    print("$", " ".join(cmd), flush=True)
    rc = subprocess.run(cmd).returncode
    if rc != 0:
        raise SystemExit(f"publish rc={rc}")
    readme = (HERE / "README.txt").read_text(encoding="utf-8").replace("{VERSION}", version)
    (out / "README.txt").write_text(readme, encoding="utf-8")
    leftovers = sorted(p.name for p in out.rglob("*.pdb"))
    if leftovers:
        raise SystemExit(f"debug symbols left: {leftovers}")
    # Include subfolders (the ko/ satellite assembly).
    files = sorted((p for p in out.rglob("*") if p.is_file()), key=lambda p: p.relative_to(out).as_posix())
    (out / "SHA256SUMS.txt").write_text("".join(f"{sha(p)} *{p.relative_to(out).as_posix()}\n" for p in files), encoding="utf-8")
    files.append(out / "SHA256SUMS.txt")
    with zipfile.ZipFile(pkg, "w", zipfile.ZIP_DEFLATED) as z:
        for p in sorted(files, key=lambda p: p.relative_to(out).as_posix()):
            z.write(p, f"{NAME}-{version}/{p.relative_to(out).as_posix()}")
    line = f"{sha(pkg)} *{pkg.name}\n"
    pkg_sha.write_bytes(line.encode("ascii"))
    print((out / "SHA256SUMS.txt").read_text(encoding="utf-8"), end="")
    print(line, end="")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        raise SystemExit(2)
    main(sys.argv[1])
