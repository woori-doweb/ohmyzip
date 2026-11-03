"""ohmyZip 배포 후보를 만든다.

1. dotnet publish (Release, win-x64, framework-dependent, 디버그 심볼 없음) → releases/ohmyZip-<버전>/
2. README.txt(버전 채움)와 THIRD-PARTY-NOTICES.txt 를 넣는다.
3. SHA256SUMS.txt 와 releases/ohmyZip-<버전>-win-x64.zip 을 만든다.
기존 releases/ohmyZip-<버전>/ 이 있으면 덮어쓰지 않고 실패한다.
실행: python -X utf8 ohmyzip/packaging/package.py <버전>
"""
import hashlib
import pathlib
import shutil
import subprocess
import sys
import zipfile

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent
NUGET = pathlib.Path.home() / ".nuget" / "packages"
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"

NOTICES = [
    ("SharpCompress 1.0.0", "https://github.com/adamhathcock/sharpcompress (commit b6cc95af73950c914e0c6ce7ea3511528ede1121)",
     HERE / "licenses" / "SharpCompress-LICENSE.txt"),
    ("System.Security.Cryptography.ProtectedData 9.0.10", "https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/9.0.10",
     NUGET / "system.security.cryptography.protecteddata" / "9.0.10" / "LICENSE.TXT"),
    ("System.Security.Cryptography.ProtectedData 9.0.10 — 패키지에 포함된 제3자 고지", "",
     NUGET / "system.security.cryptography.protecteddata" / "9.0.10" / "THIRD-PARTY-NOTICES.TXT"),
]


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def main(version):
    out = ROOT / "releases" / f"ohmyZip-{version}"
    if out.exists():
        raise SystemExit(f"이미 있음: {out}")
    cmd = [DOTNET, "publish", str(ROOT / "src" / "OhMyZip.App"), "-c", "Release", "-r", "win-x64", "--self-contained", "false",
           "-o", str(out), "-nologo", "-p:DebugType=None", "-p:DebugSymbols=false", f"-p:InformationalVersion={version}"]
    print("$", " ".join(cmd), flush=True)
    rc = subprocess.run(cmd).returncode
    if rc != 0:
        raise SystemExit(f"publish rc={rc}")
    readme = (HERE / "README.txt").read_text(encoding="utf-8").replace("{VERSION}", version)
    (out / "README.txt").write_text(readme, encoding="utf-8")
    parts = ["ohmyZip 이 사용하는 제3자 구성요소와 그 라이선스 전문입니다.\n"]
    for name, src, path in NOTICES:
        parts.append("=" * 78 + f"\n{name}\n{src}\n" + "=" * 78 + "\n" + path.read_text(encoding="utf-8-sig").strip() + "\n")
    (out / "THIRD-PARTY-NOTICES.txt").write_text("\n".join(parts), encoding="utf-8")
    leftovers = sorted(p.name for p in out.glob("*.pdb"))
    if leftovers:
        raise SystemExit(f"디버그 심볼이 남음: {leftovers}")
    files = sorted(p for p in out.iterdir() if p.is_file())
    (out / "SHA256SUMS.txt").write_text("".join(f"{sha(p)} *{p.name}\n" for p in files), encoding="utf-8")
    pkg = ROOT / "releases" / f"ohmyZip-{version}-win-x64.zip"
    with zipfile.ZipFile(pkg, "w", zipfile.ZIP_DEFLATED) as z:
        for p in sorted(out.iterdir()):
            z.write(p, f"ohmyZip-{version}/{p.name}")
    print((out / "SHA256SUMS.txt").read_text(encoding="utf-8"), end="")
    print(f"{sha(pkg)} *{pkg.name}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        raise SystemExit(2)
    main(sys.argv[1])
