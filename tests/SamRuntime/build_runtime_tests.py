"""Build native fixtures using repo tools.local.json or --tools CONFIG.

All generated sources, binaries, maps and disks are written under --out
(default tests/SamRuntime/out). No emulator is launched.
"""
import argparse
import json
import shutil
import subprocess
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
DEFAULT_OUT = HERE / "out"
FIXTURES = ("smoke", "trap", "high", "registers", "rom")
INCLUDE = 'INCLUDE "../../runtime/sam/hardware.asm"'


def load_tools(config=None, required=("pyz80", "samdisk", "samdos")):
    """Resolve executable names on PATH; relative paths against config directory."""
    path = Path(config).resolve() if config else ROOT / "tools.local.json"
    if not path.is_file():
        raise FileNotFoundError(f"Missing tools config: {path}; supply --tools CONFIG")
    values = json.loads(path.read_text(encoding="utf-8-sig"))
    resolved = {}
    for name in required:
        value = values.get(name)
        if not isinstance(value, str) or not value.strip():
            raise ValueError(f"{path}: missing configured {name}")
        candidate = Path(value).expanduser()
        local = candidate if candidate.is_absolute() else path.parent / candidate
        if local.is_file():
            resolved[name] = str(local.resolve())
        elif name != "samdos" and not candidate.is_absolute() and shutil.which(value):
            resolved[name] = shutil.which(value)
        else:
            raise FileNotFoundError(f"{path}: configured {name} not found: {value}")
    return resolved


def build(config=None, output=DEFAULT_OUT):
    tools = load_tools(config)
    output = Path(output).resolve()
    if output == HERE:
        raise ValueError("Output must not be the fixture source directory")
    output.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(ROOT / "runtime" / "sam" / "hardware.asm",
                    output / "sam-runtime.asm")
    for name in (*FIXTURES, "over_budget"):
        text = (HERE / f"{name}.asm").read_text(encoding="utf-8")
        if text.count(INCLUDE) != 1:
            raise ValueError(f"{name}: expected exactly one runtime include")
        (output / f"{name}.asm").write_text(
            "cs_initial_screen_page: EQU 4\n" +
            text.replace(INCLUDE, 'INCLUDE "sam-runtime.asm"'), encoding="utf-8")
        command = [tools["pyz80"], "--nozip", f"--obj={name}.bin",
                   f"--mapfile={name}.map"]
        if name != "over_budget":
            command += ["-I", tools["samdos"], "-o", f"{name}.mgt"]
        command.append(f"{name}.asm")
        print(subprocess.list2cmdline(command), flush=True)
        result = subprocess.run(command, cwd=output, capture_output=True, text=True)
        print(result.stdout, end="")
        print(result.stderr, end="")
        if name == "over_budget":
            detail = result.stdout + result.stderr
            if result.returncode == 0 or "Assertion failed" not in detail or \
                    "sam_runtime_end < sam_stack_floor" not in detail:
                raise RuntimeError("Negative fixture did not fail the resident-budget ASSERT")
            print("PASS expected resident-budget ASSERT failure")
        else:
            result.check_returncode()
            subprocess.run([tools["samdisk"], "copy", "--force",
                            f"{name}.mgt", f"{name}.dsk"], cwd=output, check=True)
            for suffix in ("bin", "map", "mgt", "dsk"):
                artifact = output / f"{name}.{suffix}"
                if not artifact.is_file() or artifact.stat().st_size == 0:
                    raise RuntimeError(f"Missing/empty artifact: {artifact}")
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tools", type=Path, help="Tool JSON; default repo tools.local.json")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    args = parser.parse_args()
    print(f"Built fixtures in {build(args.tools, args.out)}")


if __name__ == "__main__":
    main()
