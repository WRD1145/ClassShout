#!/usr/bin/env python3
"""在无系统字体的 Linux 图形会话启动真实发布物，防止默认字体回归。"""
import argparse
import os
from pathlib import Path
import resource
import subprocess
import tempfile
import time
from xml.sax.saxutils import escape


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--classroom", type=Path)
    parser.add_argument("--teacher", type=Path)
    parser.add_argument("--output", type=Path, default=Path("artifacts/font-startup"))
    options = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    configuration = os.environ.get("CONFIGURATION", "Release")
    apps = [("教室端", options.classroom, "ClassShout.Classroom")]
    if options.teacher or not options.classroom:
        apps.append(("教师端", options.teacher, "ClassShout.Teacher.Desktop"))
    # 预期失败应只产生测试日志，不留下系统 core 文件。
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    with tempfile.TemporaryDirectory(prefix="classshout-no-fonts-") as state:
        state = Path(state)
        (state / "fonts").mkdir()
        (state / "cache").mkdir()
        config = state / "fonts.conf"
        config.write_text(
            f"<fontconfig><dir>{escape(str(state / 'fonts'))}</dir>"
            f"<cachedir>{escape(str(state / 'cache'))}</cachedir></fontconfig>",
            encoding="utf-8",
        )
        environment = os.environ.copy()
        environment.update(FONTCONFIG_FILE=str(config), FONTCONFIG_PATH=str(state))
        fonts = subprocess.run(["fc-list"], env=environment, check=True, capture_output=True)
        if fonts.stdout.strip():
            raise RuntimeError("字体隔离失败，仍能枚举系统字体。")
        print("[通过] 已隔离系统字体，fc-list 返回空列表。", flush=True)
        failures = 0
        for label, executable, assembly in apps:
            executable = (executable or root / "src" / assembly / "bin" / configuration / "net10.0" / assembly).resolve()
            environment["CLASSSHOUT_DATA_DIR"] = str(state / assembly)
            log_path = output / f"{assembly}.log"
            with log_path.open("w", encoding="utf-8") as log:
                process = subprocess.Popen([str(executable)], env=environment, stdout=log, stderr=log)
                try:
                    deadline = time.monotonic() + 8
                    while process.poll() is None and time.monotonic() < deadline:
                        time.sleep(0.1)
                    if process.poll() is not None:
                        failures += 1
                        print(f"[失败] {label}启动退出：{process.returncode}。日志：{log_path}", flush=True)
                    else:
                        print(f"[通过] {label}在无系统字体环境启动并持续运行 8 秒。", flush=True)
                finally:
                    if process.poll() is None:
                        process.terminate()
                        try:
                            process.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait()
        return int(failures > 0)


if __name__ == "__main__":
    raise SystemExit(main())
