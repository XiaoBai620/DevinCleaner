"""Exercise real updater processes using isolated, synthetic single-file hosts."""

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
PREFIX = "devincleaner-update-smoke-"
OWNED = {".download-owner", "DevinCleaner.exe", "helper.exe", "install.json", "ready", "complete", "commit", "cancel"}
EPOCH_TICKS = 504911232000000000


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


class ProcessTracker:
    """Retain handles only after checking image path and creation time."""

    def __init__(self, results, root):
        self.results, self.root, self.handles = results, root, {}
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.user = ctypes.WinDLL("user32", use_last_error=True)
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        self.kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        self.kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
        self.user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        self.user.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
        self.user.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
        self.user.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]

    def refresh(self):
        stage = staging_directory(self.results)
        for record in self.results.glob("process-*.json"):
            info = read_json(record)
            pid = info["id"]
            if pid in self.handles:
                continue
            expected = Path(info["path"]).resolve()
            if not expected.is_relative_to(self.root) and not (stage and expected == stage / "helper.exe"):
                raise RuntimeError("A smoke process record points outside its isolated directories.")
            handle = self.kernel.OpenProcess(0x100000 | 0x1000 | 0x0001, False, pid)
            if not handle:
                continue
            image = ctypes.create_unicode_buffer(32768)
            length = wintypes.DWORD(len(image))
            times = [wintypes.FILETIME() for _ in range(4)]
            valid = self.kernel.QueryFullProcessImageNameW(handle, 0, image, ctypes.byref(length))
            valid = valid and self.kernel.GetProcessTimes(handle, *(ctypes.byref(t) for t in times))
            ticks = (times[0].dwHighDateTime << 32) + times[0].dwLowDateTime + EPOCH_TICKS
            if not valid or Path(image.value).resolve() != expected or ticks != info["startTicks"]:
                self.kernel.CloseHandle(handle)
                continue
            self.handles[pid] = (handle, info["role"])

    def close_expected_error(self):
        helper_ids = {pid for pid, (handle, role) in self.handles.items()
                      if role == "--apply-update" and self.kernel.WaitForSingleObject(handle, 0) == 258}
        callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        @callback_type
        def callback(window, _):
            pid = wintypes.DWORD()
            self.user.GetWindowThreadProcessId(window, ctypes.byref(pid))
            if pid.value in helper_ids:
                name, title = ctypes.create_unicode_buffer(256), ctypes.create_unicode_buffer(256)
                self.user.GetClassNameW(window, name, len(name))
                self.user.GetWindowTextW(window, title, len(title))
                if name.value == "#32770" and title.value == "DevinCleaner 更新":
                    self.user.PostMessageW(window, 0x0010, 0, 0)  # WM_CLOSE, scoped to this exact helper.
            return True

        self.user.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
        self.user.EnumWindows(callback, 0)

    def all_exited(self):
        return all(self.kernel.WaitForSingleObject(h, 0) == 0 for h, _ in self.handles.values())

    def cleanup(self):
        self.refresh()
        for handle, _ in self.handles.values():
            if self.kernel.WaitForSingleObject(handle, 0) == 258:
                if not self.kernel.TerminateProcess(handle, 94) or self.kernel.WaitForSingleObject(handle, 5000) != 0:
                    raise RuntimeError("An authenticated smoke process could not be stopped.")
        for handle, _ in self.handles.values():
            self.kernel.CloseHandle(handle)
        self.handles.clear()


def local_application_data():
    buffer = ctypes.create_unicode_buffer(32768)
    shell = ctypes.WinDLL("shell32")
    shell.SHGetFolderPathW.argtypes = [wintypes.HWND, ctypes.c_int, wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR]
    if shell.SHGetFolderPathW(None, 28, None, 0, buffer):
        raise RuntimeError("Cannot locate the local application data directory.")
    return Path(buffer.value).resolve()


def staging_directory(results):
    record = results / "prepared.json"
    if not record.exists():
        return None
    stage = Path(read_json(record)["directory"])
    expected_parent = local_application_data() / "DevinCleaner" / "Updates"
    if not stage.is_absolute() or stage.parent != expected_parent or not re.fullmatch(r"update-[0-9a-f]{32}", stage.name):
        raise RuntimeError("Refusing a staging directory outside the recorded updater location.")
    return stage


def cleanup_staging(results):
    stage = staging_directory(results)
    if stage is None or not stage.exists():
        return
    if stage.is_symlink() or stage.stat().st_file_attributes & 0x400:
        raise RuntimeError("Refusing linked smoke staging directory.")
    marker = stage / ".download-owner"
    if marker.read_text(encoding="utf-8") != "DevinCleaner update staging v1\n" + stage.name:
        raise RuntimeError("Smoke staging ownership changed; material retained.")
    entries = list(stage.iterdir())
    for item in entries:
        info = item.stat(follow_symlinks=False)
        if item.name not in OWNED or not item.is_file() or info.st_file_attributes & 0x400 or info.st_nlink != 1:
            raise RuntimeError("Unknown or linked smoke staging entry; material retained.")
    for item in entries:
        if item != marker:
            item.unlink()
    marker.unlink()
    stage.rmdir()
    if stage.exists():
        raise RuntimeError("Smoke staging cleanup did not finish.")


def publish(root, name, version):
    output = root / ("build-" + name)
    command = ["dotnet", "publish", str(ROOT / "tools/UpdateSmoke/UpdateSmoke.csproj"), "-c", "Release",
               "-o", str(output), "-p:Version=" + version,
               "-p:BaseOutputPath=" + str(root / ("bin-" + name)) + os.sep,
               "-p:BaseIntermediateOutputPath=" + str(root / ("obj-" + name)) + os.sep,
               "-p:MSBuildProjectExtensionsPath=" + str(root / ("obj-" + name)) + os.sep]
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)
    if result.returncode:
        raise RuntimeError("Smoke host publish failed:\n" + result.stdout + result.stderr)
    return output / "DevinCleaner.exe"


def run_case(root, name, old, new, version, timeout):
    case = root / name
    case.mkdir()
    results = case / "results"
    results.mkdir()
    (results / ".smoke-owner").write_text(root.name, encoding="ascii")
    target = case / "DevinCleaner.exe"
    shutil.copy2(old, target)
    old_hash, new_hash = digest(old), digest(new)
    env = dict(os.environ, DEVINCLEANER_UPDATE_SMOKE_RESULTS=str(results),
               DEVINCLEANER_UPDATE_SMOKE_FAIL_STARTUP="1" if name == "rollback" else "0")
    tracker = ProcessTracker(results, root)
    parent = subprocess.Popen([str(target), "--smoke-start", str(new), version], cwd=case, env=env,
                              creationflags=subprocess.CREATE_NO_WINDOW)
    started = time.monotonic()
    try:
        while time.monotonic() - started < timeout:
            tracker.refresh()
            if (results / "failure.json").exists():
                raise RuntimeError(read_json(results / "failure.json")["error"])
            if name == "rollback":
                tracker.close_expected_error()
            ready = results / ("complete.json" if name == "success" else "new-started.json")
            process_records = [read_json(p) for p in results.glob("process-*.json")]
            helper_records = [p for p in process_records if p["role"] == "--apply-update"]
            helper_exited = bool(helper_records) and all((results / f"exit-{p['id']}.json").exists() for p in helper_records)
            recorded_exited = all((results / f"exit-{p['id']}.json").exists() for p in process_records)
            if ready.exists() and parent.poll() == 0 and helper_exited and recorded_exited and tracker.all_exited():
                expected = new_hash if name == "success" else old_hash
                if digest(target) != expected:
                    raise RuntimeError("Updated/rolled-back executable does not match its original bytes.")
                if list(case.glob(".devincleaner-update-*")):
                    raise RuntimeError("Update left temporary siblings beside the executable.")
                stage = staging_directory(results)
                if name == "success" and (stage is None or stage.exists()):
                    raise RuntimeError("Successful update did not clean its staging directory.")
                if name == "rollback" and (results / "shown.json").exists():
                    raise RuntimeError("The failure host unexpectedly confirmed startup.")
                print(f"{name}: PASS (real helper, verified executable bytes, {time.monotonic() - started:.1f}s)", flush=True)
                return
            if parent.poll() not in (None, 0):
                raise RuntimeError(f"Original smoke host exited with {parent.returncode}.")
            time.sleep(0.1)
        raise TimeoutError(f"{name} updater process test exceeded {timeout} seconds.")
    finally:
        tracker.cleanup()
        if parent.poll() is None:
            parent.kill()
        parent.wait(timeout=10)
        cleanup_staging(results)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--case", choices=("both", "success", "rollback"), default="both")
    parser.add_argument("--version")
    parser.add_argument("--timeout", type=float, default=90)
    args = parser.parse_args()
    if sys.platform != "win32":
        raise RuntimeError("Updater process smoke tests require Windows.")
    version = args.version or ET.parse(ROOT / "src/DevinCleaner/DevinCleaner.csproj").findtext("PropertyGroup/Version")
    if not version or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version) or not any(map(int, version.split("."))):
        raise ValueError("The target version must be a stable version newer than 0.0.0.")
    if not 10 <= args.timeout <= 300:
        raise ValueError("The process timeout must be between 10 and 300 seconds.")
    root = Path(tempfile.mkdtemp(prefix=PREFIX)).resolve()
    try:
        print("Publishing two isolated update test hosts...", flush=True)
        old, new = publish(root, "old", "0.0.0"), publish(root, "new", version)
        cases = ("success", "rollback") if args.case == "both" else (args.case,)
        for case in cases:
            run_case(root, case, old, new, version, args.timeout)
    finally:
        temp_root = Path(tempfile.gettempdir()).resolve()
        if root.parent != temp_root or not root.name.startswith(PREFIX) or root.is_symlink() or root.stat().st_file_attributes & 0x400:
            raise RuntimeError("Refusing to remove an unrecognized smoke workspace.")
        shutil.rmtree(root)
        if root.exists():
            raise RuntimeError("Smoke workspace cleanup did not finish.")
    print("Smoke workspaces and recorded update staging removed.", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"Update smoke test failed: {error}", file=sys.stderr)
        sys.exit(1)
