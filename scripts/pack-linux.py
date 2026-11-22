#!/usr/bin/env python3
"""Linux 原生打包：自包含目录、tar.gz、更新包、AppImage、deb、rpm。"""
import argparse
import hashlib
import importlib.util
import os
import platform
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("update_package", ROOT / "scripts/create-update-package.py")
update_package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(update_package)


def run(*args, **kwargs):
    subprocess.run([str(arg) for arg in args], check=True, **kwargs)


def write(path, text, executable=False):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    path.chmod(0o755 if executable else 0o644)


def build(app, version, output, tool, architecture, reuse=None):
    runtime = f"linux-{architecture}"
    native_arch = "aarch64" if architecture == "arm64" else "x86_64"
    deb_arch = "arm64" if architecture == "arm64" else "amd64"
    assembly = "ClassShout.Classroom" if app == "classroom" else "ClassShout.Teacher.Desktop"
    title = "ClassShout 教室端" if app == "classroom" else "ClassShout 教师端"
    product = "ClassShout.Classroom" if app == "classroom" else "ClassShout.Teacher"
    package = f"classshout-{app}"
    assets = output / "release"
    assets.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="classshout-package-", dir=output) as state:
        state = Path(state)
        payload = state / "payload"
        if reuse:
            shutil.copytree(reuse, payload, ignore=shutil.ignore_patterns("logs", "使用说明.txt", "run.sh"))
        else:
            run("dotnet", "publish", ROOT / f"src/{assembly}/{assembly}.csproj", "-c", "Release", "-f", "net10.0",
                "-r", runtime, "--self-contained=true", f"-p:Version={version}", "-p:DebugType=none", "-o", payload)
        if not (payload / assembly).is_file():
            raise RuntimeError("发布物缺少程序入口。")
        update_package.validate_entrypoint(payload / assembly, runtime)
        (payload / assembly).chmod(0o755)
        shutil.copy(ROOT / "LICENSE", payload / "ClassShout.LICENSE.txt")
        update_package.create(payload, assets / f"{product}-{runtime}-update.zip", app, version, runtime)
        write(payload / "run.sh", f'#!/bin/sh\ncd -- "$(dirname -- "$0")" || exit 1\nexec "./{assembly}" "$@"\n', True)
        with tarfile.open(assets / f"{product}-{runtime}.tar.gz", "w:gz") as archive:
            archive.add(payload, arcname=product)
        desktop = (f"[Desktop Entry]\nType=Application\nName={product}\nName[zh_CN]={title}\n"
                   f"Comment=Classroom announcements\nExec={package}\nIcon={package}\nTerminal=false\nCategories=Education;\nStartupWMClass={assembly}\n")
        icon = ROOT / f"assets/{app}-512.png"
        appdir = state / f"{product}.AppDir"
        shutil.copytree(payload, appdir / "usr/lib" / package)
        write(appdir / "AppRun", f'#!/bin/sh\nhere="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"\nexec "$here/usr/lib/{package}/{assembly}" "$@"\n', True)
        write(appdir / f"{package}.desktop", desktop.replace(f"Exec={package}", "Exec=AppRun"))
        shutil.copy(icon, appdir / f"{package}.png")
        environment = os.environ.copy()
        environment.update(ARCH=native_arch, APPIMAGE_EXTRACT_AND_RUN="1", VERSION=version)
        run(tool, "--no-appstream", appdir, assets / f"{product}-{runtime}.AppImage", env=environment)
        (assets / f"{product}-{runtime}.AppImage").chmod(0o755)
        # deb/rpm 不允许应用自行覆盖系统目录，标记交给更新页识别。
        tree = state / "system"
        install = tree / "opt" / package
        shutil.copytree(payload, install)
        write(tree / f"usr/bin/{package}", f'#!/bin/sh\nexec /opt/{package}/{assembly} "$@"\n', True)
        write(tree / f"usr/share/applications/{package}.desktop", desktop)
        image = tree / f"usr/share/icons/hicolor/512x512/apps/{package}.png"
        image.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(icon, image)
        write(install / "classshout-package-manager", "deb\n")
        write(tree / "DEBIAN/control", f"Package: {package}\nVersion: {version}\nArchitecture: {deb_arch}\nMaintainer: ClassShout <noreply@github.com>\nSection: education\nPriority: optional\nDepends: libc6 (>= 2.31), libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6, libicu70 | libicu72 | libicu74 | libicu76 | libicu78\nRecommends: alsa-utils, espeak-ng\nDescription: {product} classroom announcements\n Self-contained .NET desktop application.\n")
        run("dpkg-deb", "--root-owner-group", "--build", tree, assets / f"{product}-{runtime}.deb")
        shutil.rmtree(tree / "DEBIAN")
        write(install / "classshout-package-manager", "rpm\n")
        rpm = state / "rpm"
        rpm.mkdir()
        specification = state / "package.spec"
        write(specification, f"""Name: {package}
Version: {version}
Release: 1
Summary: {product} classroom announcements
License: MIT
URL: https://github.com/WRD1145/ClassShout
AutoReqProv: no
Requires: glibc, libgcc, libstdc++, zlib, fontconfig, libX11, libICE, libSM, libicu
Recommends: alsa-utils, espeak-ng
%description
Self-contained .NET desktop application for classroom announcements.
%install
mkdir -p "%{{buildroot}}"
cp -a "{tree}/." "%{{buildroot}}/"
%files
/opt/{package}
/usr/bin/{package}
/usr/share/applications/{package}.desktop
/usr/share/icons/hicolor/512x512/apps/{package}.png
""")
        run("rpmbuild", "-bb", "--target", native_arch, "--define", f"_topdir {rpm}", "--define", "debug_package %{nil}",
            "--define", "__os_install_post %{nil}", specification)
        packages = list(rpm.rglob("*.rpm"))
        if len(packages) != 1:
            raise RuntimeError("RPM 产物数量不正确。")
        shutil.copy(packages[0], assets / f"{product}-{runtime}.rpm")
    print(f"Linux 五种产物已生成：{product}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default=ET.parse(ROOT / "Directory.Build.props").findtext(".//Version"))
    parser.add_argument("--architecture", choices=["x64", "arm64"], default="arm64" if platform.machine().lower() in ("aarch64", "arm64") else "x64")
    parser.add_argument("--output", type=Path, default=ROOT / "dist/linux-native")
    parser.add_argument("--appimagetool", type=Path, required=True)
    parser.add_argument("--app", choices=["classroom"], default="classroom")
    parser.add_argument("--reuse-publish", type=Path, help="仅验收单个应用时复用干净发布目录")
    options = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", options.version):
        raise ValueError("Linux 包必须使用四段版本。")
    options.output = options.output.resolve()
    options.output.mkdir(parents=True, exist_ok=True)
    for app in [options.app]:
        build(app, options.version, options.output, options.appimagetool.resolve(), options.architecture, options.reuse_publish)
    assets = options.output / "release"
    write(assets / "SHA256SUMS.txt", "".join(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n"
          for path in sorted(assets.iterdir()) if path.name != "SHA256SUMS.txt"))


if __name__ == "__main__":
    main()
