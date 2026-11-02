"""Run owned SimCoupe instances, sample backgrounds, exercise keys, save evidence."""
import argparse
import ctypes
import hashlib
import json
import statistics
import subprocess
import time
from collections import Counter
from ctypes import wintypes
from pathlib import Path

import mss
from PIL import Image
from build_runtime_tests import ROOT, load_tools
from verify_simcoupe import find_window, user32


def verify(disk, output, executable, custom=False, walk=False, anykey=False, repro=False,
           measure_cadence=False, black_background=True):
    proc = subprocess.Popen([executable, str(disk), "-mainmem", "256", "-externalmem", "0", "-speed", "100",
                             "-autoload", "yes", "-fullscreen", "0", "-scale", "2"], cwd=output)
    held = set()
    try:
        time.sleep(7)
        hwnd = find_window(proc.pid)
        if not hwnd:
            raise RuntimeError(f"No owned window: {proc.pid}")
        thread = user32.GetWindowThreadProcessId(user32.GetForegroundWindow(), None)
        own = ctypes.windll.kernel32.GetCurrentThreadId()
        target_thread = user32.GetWindowThreadProcessId(hwnd, None)
        attached = [tid for tid in {thread, target_thread}
                    if tid != own and user32.AttachThreadInput(own, tid, True)]
        try:
            user32.ShowWindow(hwnd, 9)
            user32.BringWindowToTop(hwnd)
            user32.SetActiveWindow(hwnd)
            user32.SetForegroundWindow(hwnd)
        finally:
            for tid in attached:
                user32.AttachThreadInput(own, tid, False)
        time.sleep(.5)
        origin = wintypes.POINT()
        rect = wintypes.RECT()
        user32.GetClientRect(hwnd, ctypes.byref(rect))
        user32.ClientToScreen(hwnd, ctypes.byref(origin))
        region = dict(left=origin.x, top=origin.y, width=rect.right, height=rect.bottom)
        roi = (rect.right * 12 // 100, rect.bottom * 12 // 100,
               rect.right * 35 // 100, rect.bottom * 40 // 100)

        def key(vk, down):
            if user32.GetForegroundWindow() != hwnd:
                raise RuntimeError("Owned emulator lost focus; refusing input")
            user32.keybd_event(vk, 0, 0 if down else 2, 0)
            (held.add if down else held.discard)(vk)

        def sample(stage, seconds=2, black=None):
            if black is None:
                black = black_background
            colours, frames = Counter(), set()
            with mss.MSS() as capture:
                end = time.monotonic() + seconds
                while time.monotonic() < end:
                    if user32.GetForegroundWindow() != hwnd:
                        raise RuntimeError("Owned emulator lost focus")
                    shot = capture.grab(region)
                    image = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
                    colours[image.getpixel((rect.right // 2, rect.bottom // 2))] += 1
                    frames.add(hashlib.sha256(image.crop(roi).tobytes()).hexdigest())
                    time.sleep(.02)
                image.save(output / f"{disk.parent.parent.name}-{stage}.png")
            if black:
                assert set(colours) == {(0, 0, 0)}, (disk, stage, colours)
            return colours, len(frames)

        colours, changes = sample("idle", 4 if repro else 2, black=not repro and black_background)
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
        cadence = None
        if walk:
            for vk, name in ((0x5A, "left"), (0x58, "right")):
                key(vk, True)
                if measure_cadence and name == "left":
                    time.sleep(.6)
                    anchor, previous = None, None
                    stable_since = time.monotonic()
                    entries = []
                    was_anchor = False
                    with mss.MSS() as capture:
                        end = time.monotonic() + 6
                        while time.monotonic() < end:
                            if user32.GetForegroundWindow() != hwnd:
                                raise RuntimeError("Owned emulator lost focus during cadence measurement")
                            shot = capture.grab(region)
                            image = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
                            pose = image.crop(roi).tobytes()
                            now = time.monotonic()
                            if pose != previous:
                                stable_since, previous = now, pose
                            if anchor is None and now - stable_since > .045:
                                anchor = pose
                            if anchor is not None:
                                is_anchor = pose == anchor
                                if is_anchor and not was_anchor and (not entries or now - entries[-1] > .4):
                                    entries.append(now)
                                was_anchor = is_anchor
                            time.sleep(.005)
                    cycles = [b - a for a, b in zip(entries, entries[1:])][1:]
                    assert len(cycles) >= 3, ("Insufficient complete walking cycles", entries)
                    cadence = statistics.mean(cycles)
                    print(f"Walking cadence: {cadence:.4f}s/eight-pose cycle; {cadence / 8:.4f}s/pose", flush=True)
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
        print(f"PASS SimCoupe {disk.parent.parent.name}: {'black' if black_background else 'reference'} idle, animation, input, {selections}", flush=True)
        return dict(idle_changes=changes, palette=selections, walking_cycle_seconds=cadence)
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
    parser.add_argument("--cadence", action="store_true", help="Measure full eight-pose left-walk cycles")
    args = parser.parse_args()
    tools = load_tools(args.tools, ("simcoupe",))
    out = ROOT / "out" / "verification"
    out.mkdir(parents=True, exist_ok=True)
    if args.repro:
        verify(args.repro.resolve(), out, tools["simcoupe"], repro=True)
        return
    names = ("06-4-DoubleBuffer", "07-1-KeyboardJump", "07-2-SpaceJump", "07-3-WalkAndJump",
             "08-1-OriginalPalette", "08-2-ChangeBackground", "08-3-CroppedSidewaysJump",
             "08-3-AssemblyStyle")
    results = {}
    for name in names:
        if args.only and args.only != name:
            continue
        disk = next((ROOT / "tutorials").glob(f"**/{name}/out/program.dsk"))
        results[name] = verify(disk, out, tools["simcoupe"],
                               custom=name.startswith(("08-2", "08-3")),
                               walk=name.startswith(("07-3", "08-")),
                               anykey=name.startswith("07-1"), measure_cadence=args.cadence)
    (out / "simcoupe-results.json").write_text(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
