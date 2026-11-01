"""Focused native runtime check. Uses only the process it launches.

python verify_simcoupe.py smoke
python verify_simcoupe.py trap
Screenshots are evidence, not an automated image oracle.
"""
import ctypes
import argparse
import subprocess
import time
from ctypes import wintypes
from pathlib import Path

import mss
from PIL import Image
from build_runtime_tests import DEFAULT_OUT, FIXTURES, load_tools

HERE = Path(__file__).resolve().parent
user32 = ctypes.windll.user32
user32.SetProcessDPIAware()


def find_window(pid):
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def callback(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd) and \
                user32.GetWindowTextLengthW(hwnd):
            found.append(hwnd)
        return True

    user32.EnumWindows(callback, 0)
    return found[0] if found else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("name", nargs="?", choices=FIXTURES, default="smoke")
    parser.add_argument("--tools", type=Path)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    args = parser.parse_args()
    name = args.name
    output = args.out.resolve()
    tools = load_tools(args.tools, required=("simcoupe",))
    disk = output / f"{name}.dsk"
    if not disk.is_file():
        parser.error(f"Build fixture first: missing {disk}")
    proc = subprocess.Popen(
        [tools["simcoupe"],
         str(disk), "-mainmem", "256",
         "-scale", "2", "-fullscreen", "0"],
        cwd=output,
    )
    try:
        time.sleep(7)
        hwnd = find_window(proc.pid)
        if not hwnd:
            raise RuntimeError("No SimCoupe window for owned process")
        foreground = user32.GetForegroundWindow()
        tid = user32.GetWindowThreadProcessId(foreground, None)
        own = ctypes.windll.kernel32.GetCurrentThreadId()
        attached = user32.AttachThreadInput(own, tid, True)
        try:
            user32.ShowWindow(hwnd, 9)
            user32.SetForegroundWindow(hwnd)
        finally:
            if attached:
                user32.AttachThreadInput(own, tid, False)
        time.sleep(1)

        def key(vk, down):
            if user32.GetForegroundWindow() != hwnd:
                raise RuntimeError("Owned emulator not foreground")
            user32.keybd_event(vk, 0, 0 if down else 2, 0)

        def shot(stage):
            if user32.GetForegroundWindow() != hwnd:
                raise RuntimeError("Owned emulator not foreground; refusing screenshot")
            rect = wintypes.RECT()
            origin = wintypes.POINT(0, 0)
            user32.GetClientRect(hwnd, ctypes.byref(rect))
            user32.ClientToScreen(hwnd, ctypes.byref(origin))
            with mss.MSS() as sct:
                img = sct.grab({"left": origin.x, "top": origin.y,
                                "width": rect.right, "height": rect.bottom})
                path = output / f"{name}-{stage}.png"
                Image.frombytes("RGB", img.size, img.bgra, "raw", "BGRX").save(path)
                print(path, flush=True)

        shot("boot")
        if name == "high":
            # F9 pauses a real post-load resident instruction. Commands use
            # hexadecimal, and the disk's pyz80 map resolves high_after_key.
            key(0x78, True)
            key(0x78, False)
            time.sleep(0.5)
            shot("debugger")
            key(0x0D, True)
            key(0x0D, False)
            time.sleep(0.3)
            address = next(line.split("=")[0] for line in
                           (output / "high.map").read_text().splitlines()
                           if line.endswith("=high_after_key"))
            for ch in f"bpx {address}":
                vk = user32.VkKeyScanW(ord(ch)) & 0xFF
                key(vk, True)
                key(vk, False)
                time.sleep(0.04)
            key(0x0D, True)
            key(0x0D, False)
            time.sleep(0.5)
            shot("breakpoint")
            key(0x1B, True)
            key(0x1B, False)
            time.sleep(0.2)
            key(0x1B, True)
            key(0x1B, False)
            time.sleep(0.5)
        key(0x20, True)
        time.sleep(0.5)
        shot("held")
        key(0x20, False)
        time.sleep(1)
        shot("released")
        if name in ("smoke", "rom"):
            key(0x5A, True)
            time.sleep(0.3)
            key(0x5A, False)
            time.sleep(1)
            shot("exit")
    finally:
        if proc.poll() is None:
            proc.kill()
        proc.wait()


if __name__ == "__main__":
    main()
