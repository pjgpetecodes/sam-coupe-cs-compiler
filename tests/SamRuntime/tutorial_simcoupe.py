"""Run owned SimCoupe instances, sample backgrounds, exercise keys, save evidence."""
import argparse
import ctypes
import hashlib
import json
import subprocess
import time
from collections import Counter
from ctypes import wintypes
from pathlib import Path

import mss
from PIL import Image
from build_runtime_tests import ROOT, load_tools
from verify_simcoupe import find_window, user32


def verify(disk, output, executable, custom=False, walk=False, anykey=False, repro=False):
    proc = subprocess.Popen([executable, str(disk), "-mainmem", "256", "-externalmem", "0",
                             "-autoload", "yes", "-fullscreen", "0", "-scale", "2"], cwd=output)
    held = set()
    try:
        time.sleep(7)
        hwnd = find_window(proc.pid)
        if not hwnd:
            raise RuntimeError(f"No owned window: {proc.pid}")
        thread = user32.GetWindowThreadProcessId(user32.GetForegroundWindow(), None)
        own = ctypes.windll.kernel32.GetCurrentThreadId()
        attached = thread != own and user32.AttachThreadInput(own, thread, True)
        try:
            user32.ShowWindow(hwnd, 9)
            user32.SetForegroundWindow(hwnd)
        finally:
            if attached:
                user32.AttachThreadInput(own, thread, False)
        time.sleep(.5)
        origin = wintypes.POINT()
        rect = wintypes.RECT()
        user32.GetClientRect(hwnd, ctypes.byref(rect))
        user32.ClientToScreen(hwnd, ctypes.byref(origin))
        region = dict(left=origin.x, top=origin.y, width=rect.right, height=rect.bottom)

        def key(vk, down):
            if user32.GetForegroundWindow() != hwnd:
                raise RuntimeError("Owned emulator lost focus; refusing input")
            user32.keybd_event(vk, 0, 0 if down else 2, 0)
            (held.add if down else held.discard)(vk)

        def sample(stage, seconds=2, black=True):
            colours, frames = Counter(), set()
            with mss.MSS() as capture:
                end = time.monotonic() + seconds
                while time.monotonic() < end:
                    if user32.GetForegroundWindow() != hwnd:
                        raise RuntimeError("Owned emulator lost focus")
                    shot = capture.grab(region)
                    image = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
                    colours[image.getpixel((rect.right // 2, rect.bottom // 2))] += 1
                    frames.add(hashlib.sha256(image.crop((55, 45, 200, 170)).tobytes()).hexdigest())
                    time.sleep(.02)
                image.save(output / f"{disk.parent.parent.name}-{stage}.png")
            if black:
                assert set(colours) == {(0, 0, 0)}, (disk, stage, colours)
            return colours, len(frames)

        colours, changes = sample("idle", 4 if repro else 2, black=not repro)
        if repro:
            assert (0, 0, 0) in colours and len(colours) >= 2, colours
            print(f"PASS actual ROM black/cyan reproduction: {dict(colours)}", flush=True)
            return dict(backgrounds={str(k): v for k, v in colours.items()})
        assert changes >= 2, (disk, "Animation did not change", changes)
        key(0x41 if anykey else 0x20, True)
        time.sleep(.2)
        key(0x41 if anykey else 0x20, False)
        _, changes = sample("jump-release", 2)
        assert changes >= 2, "Animation stopped after key release"
        if walk:
            for vk, name in ((0x5A, "left"), (0x58, "right")):
                key(vk, True)
                _, changes = sample(name)
                assert changes >= 2
                key(0x20, True)
                time.sleep(.2)
                key(0x20, False)
                key(vk, False)
                sample(name + "-jump")
        selections = {}
        if custom:
            for vk in range(0x31, 0x36):
                key(vk, True)
                time.sleep(.5)
                key(vk, False)
                colours, _ = sample(chr(vk), black=vk == 0x31)
                assert len(colours) == 1, ("Palette flashes", colours)
                selections[chr(vk)] = str(next(iter(colours)))
            assert len(set(selections.values())) == 5, selections
            key(0x31, True)
            time.sleep(.5)
            key(0x31, False)
            sample("black-restored")
        print(f"PASS SimCoupe {disk.parent.parent.name}: black idle, animation, input, {selections}", flush=True)
        return dict(idle_changes=changes, palette=selections)
    finally:
        for vk in held:
            user32.keybd_event(vk, 0, 2, 0)
        if proc.poll() is None:
            hwnd = find_window(proc.pid)
            if hwnd:
                user32.PostMessageW(hwnd, 0x0010, 0, 0)
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tools", type=Path)
    parser.add_argument("--only", help="One tutorial folder name")
    parser.add_argument("--repro", type=Path, help="Deliberately stale ROM-palette fixture disk")
    args = parser.parse_args()
    tools = load_tools(args.tools, ("simcoupe",))
    out = ROOT / "out" / "verification"
    out.mkdir(parents=True, exist_ok=True)
    if args.repro:
        verify(args.repro.resolve(), out, tools["simcoupe"], repro=True)
        return
    names = ("06-4-DoubleBuffer", "07-1-KeyboardJump", "07-2-SpaceJump", "07-3-WalkAndJump",
             "08-1-OriginalPalette", "08-2-ChangeBackground", "08-3-CroppedSidewaysJump")
    results = {}
    for name in names:
        if args.only and args.only != name:
            continue
        disk = next((ROOT / "tutorials").glob(f"**/{name}/out/program.dsk"))
        results[name] = verify(disk, out, tools["simcoupe"],
                               custom=name.startswith(("08-2", "08-3")),
                               walk=name.startswith(("07-3", "08-")),
                               anykey=name.startswith("07-1"))
    (out / "simcoupe-results.json").write_text(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
