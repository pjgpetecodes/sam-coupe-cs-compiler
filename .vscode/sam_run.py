"""Build the active C# program using the native compiler, optionally running SimCoupe."""

import argparse
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--run", action="store_true")
    modes.add_argument("--build", action="store_true")
    modes.add_argument("--launch-built", action="store_true")
    parser.add_argument("--source-comments", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    source = args.source.resolve()
    if source.suffix.lower() != ".cs" or not source.is_file():
        parser.error("Open a saved C# program (.cs) before pressing F5.")
    project = root / "src" / "SamCoupe.Compiler.Cli" / "SamCoupe.Compiler.Cli.csproj"
    if source.is_relative_to(root / "tutorials") and not args.launch_built:
        generated = subprocess.run(
            [sys.executable, str(root / "tools" / "generate_tutorial_assets.py"),
             "--program", str(source.parent)], cwd=root, check=False)
        if generated.returncode:
            return generated.returncode
    if args.launch_built:
        compiler = project.parent / "bin" / "Debug" / "net10.0" / "SamCoupe.Compiler.Cli.dll"
        if not compiler.is_file():
            parser.error("Build the compiler before launching a built program.")
        command = ["dotnet", str(compiler)]
    else:
        command = ["dotnet", "run", "--project", str(project), "--"]
    command.extend([
        str(source), "--tools", str(root / "tools.local.json"),
    ])
    if args.source_comments:
        command.append("--source-comments")
    if args.run:
        command.append("--run")
    elif args.build:
        command.append("--build")
    elif args.launch_built:
        command.append("--launch-built")
    try:
        result = subprocess.run(command, cwd=root, check=False)
    except OSError as error:
        print(f"SAM C# launch failed: {error}", file=sys.stderr)
        return 1
    return result.returncode


if __name__ == "__main__":
    sys.exit(main())
