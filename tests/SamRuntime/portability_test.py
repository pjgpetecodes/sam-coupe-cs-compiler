"""Rebuild from an unrelated working directory with an explicit relative-path
tools config. Generated artifacts stay under out/portability.
"""
import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

from build_runtime_tests import DEFAULT_OUT, FIXTURES, HERE, load_tools


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tools", type=Path)
    args = parser.parse_args()
    tools = load_tools(args.tools)
    sandbox = DEFAULT_OUT / "portability"
    sandbox.mkdir(parents=True, exist_ok=True)
    config = sandbox / "relative-tools.json"
    values = {}
    for name, value in tools.items():
        try:
            values[name] = os.path.relpath(value, sandbox)
        except ValueError:  # Windows executables may live on another drive.
            values[name] = value
    config.write_text(json.dumps(values, indent=2), encoding="utf-8")
    resolved = load_tools(config)
    assert all(Path(resolved[name]) == Path(value) for name, value in tools.items())
    build_out = sandbox / "build"
    subprocess.run(
        [sys.executable, str(HERE / "build_runtime_tests.py"),
         "--tools", str(config), "--out", str(build_out)],
        cwd=sandbox, check=True,
    )
    for name in FIXTURES:
        assert (build_out / f"{name}.dsk").is_file()
        source = (build_out / f"{name}.asm").read_text()
        assert 'INCLUDE "sam-runtime.asm"' in source
        assert "../../runtime" not in source
    for script in ("build_runtime_tests.py", "verify_simcoupe.py", "bounds_test.py"):
        assert "petecodes" not in (HERE / script).read_text()
    leftovers = [path.name for path in HERE.iterdir() if path.is_file() and
                 path.suffix in {".bin", ".map", ".mgt", ".dsk", ".png"}]
    assert not leftovers, f"Generated artifacts left beside fixtures: {leftovers}"
    print("PASS explicit relative config, unrelated cwd, staged include and clean source directory")


if __name__ == "__main__":
    main()
