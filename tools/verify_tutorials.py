"""Build/measure every registered port; original assembly is strictly read-only.

Comparison products go under each C# tutorial's ignored out/comparison folder.
"""
import argparse
import hashlib
import json
import re
import shutil
import subprocess
from pathlib import Path
from generate_tutorial_assets import ROOT, TUTORIALS
import sys

sys.path.insert(0, str(ROOT / "tests" / "SamRuntime"))
from loader_test import auto_code


def run(command, cwd):
    result = subprocess.run(command, cwd=cwd, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)


def read_symbols(path):
    return {label: int(value) for label, value in re.findall(
        r"V(\w+)\s+p\d+\s+I(\d+)", path.read_text())}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build", action="store_true")
    parser.add_argument("--compare", action="store_true")
    parser.add_argument("--tools", type=Path, default=ROOT / "tools.local.json")
    args = parser.parse_args()
    tools = json.loads(args.tools.read_text(encoding="utf-8-sig"))
    reference = Path(tools["referenceRepo"])
    cli = ROOT / "src" / "SamCoupe.Compiler.Cli" / "bin" / "Debug" / "net10.0" / "SamCoupe.Compiler.Cli.dll"
    if args.build:
        run(["dotnet", "build", "--nologo", "-v:q"], ROOT)
    rows = []
    registered_programs = {
        (ROOT / "tutorials" / Path(folder) / "Program.cs").resolve()
        for folder, *_ in TUTORIALS
    }
    for folder, source, *_ in TUTORIALS:
        program = ROOT / "tutorials" / Path(folder)
        out = program / "out"
        if args.build:
            run(["dotnet", str(cli), str(program / "Program.cs"),
                 "--tools", str(args.tools.resolve()), "--build"], ROOT)
        labels = read_symbols(out / "program.symbols")
        assembly = (out / "program.asm").read_text()
        data = sum(int(length) * (1 if directive == "DEFB" else 2) + 2
                   for label, length, directive in re.findall(
                       r"(cs_data_\d+):\s+DEFW (\d+)\s+cs_data_\d+_payload:\s+(DEFB|DEFW)",
                       assembly)
                   if 0x8000 <= labels[label] < labels["sam_start"])
        application = labels["cs_trap_string_bounds"] - labels["cs_method_001"]
        compiler = labels["sam_start"] - 0x8000
        binary = (out / "program.bin").stat().st_size
        row = dict(folder=folder, application=application, asset_data=data,
                   compiler_other=compiler - application - data,
                   runtime_loaded=min(binary, labels["sam_cli_image_end"] - 0x8000) - compiler,
                   runtime_reserved=labels["sam_runtime_end"] - labels["sam_start"],
                   native_binary=binary)
        disk = (out / "program.dsk").read_bytes()
        assert disk.startswith(b"EXTENDED CPC DSK File\r\nDisk-Info\r\n")
        assert disk[48:50] == bytes((80, 2)), "Expected 80 tracks, two sides"
        assert len(disk) == 256 + sum(disk[52:212]) * 256, "Incomplete EDSK track data"
        assert labels["sam_runtime_end"] < 0xE000
        if args.compare:
            comparison = out / "comparison"
            comparison.mkdir(exist_ok=True)
            original = reference / "assembly" / Path(source)
            # Stage only text with includes pointing at their read-only originals.
            text = original.read_text(encoding="utf-8")
            text = re.sub(r'(?im)(\bINCLUDE\s+)["\']([^"\']+)["\']',
                          lambda m: m[1] + '"' + str(original.parent / m[2]) + '"', text)
            (comparison / "original.asm").write_text(text, encoding="utf-8")
            executable = tools["pyz80"]
            if not shutil.which(executable):
                executable = str((args.tools.resolve().parent / executable).resolve())
            run([executable, "--nozip", "--obj=original.bin", "--mapfile=original.map",
                 "--exportfile=original.symbols", "original.asm"], comparison)
            original_labels = read_symbols(comparison / "original.symbols")
            starts = [value for label, value in original_labels.items()
                      if re.fullmatch(r"Sprite1_1|Sprite1_Pixels|Sprite1_Interleaved|Dizzy_System_Interleaved_Image1|TIDizzy_1_Interleaved", label)]
            original_size = (comparison / "original.bin").stat().st_size
            data_start = min(starts) if starts else 0x8000 + (28 if source.endswith("1.asm") else original_size)
            row.update(handwritten_binary=original_size,
                       handwritten_code=data_start - 0x8000,
                       handwritten_data=original_size - (data_start - 0x8000))
        rows.append(row)
        print(f"PASS disk/budget {folder}: app {application}, data {data}, runtime {row['runtime_loaded']}, binary {binary}", flush=True)

    additional_rows = []
    for program in sorted((ROOT / "tutorials").rglob("Program.cs")):
        if program.resolve() in registered_programs:
            continue
        out = program.parent / "out"
        if args.build:
            run(["dotnet", str(cli), str(program),
                 "--tools", str(args.tools.resolve()), "--build"], ROOT)
        if not (out / "program.symbols").is_file():
            raise FileNotFoundError(f"Build output is missing for {program}; use --build.")
        labels = read_symbols(out / "program.symbols")
        resident_end = labels.get("sam_cli_image_end", labels.get("sam_runtime_end", 0))
        if resident_end == 0:
            raise AssertionError(f"{program} has no resident-end symbol.")
        assert resident_end < 0xE000, f"{program} overlaps the resident stack boundary"
        disk = (out / "program.dsk").read_bytes()
        assert disk.startswith(b"EXTENDED CPC DSK File\r\nDisk-Info\r\n")
        assert disk[48:50] == bytes((80, 2)), "Expected 80 tracks, two sides"
        assert len(disk) == 256 + sum(disk[52:212]) * 256, "Incomplete EDSK track data"
        binary = (out / "program.bin").stat().st_size
        row = dict(folder=str(program.parent.relative_to(ROOT / "tutorials")),
                   resident_end=resident_end, native_binary=binary)
        additional_rows.append(row)
        print(f"PASS standalone tutorial {row['folder']}: resident {resident_end:#06x}, binary {binary}", flush=True)

    rows.extend(additional_rows)
    required_inputs = []
    for row in rows:
        program = ROOT / "tutorials" / Path(row["folder"])
        out = program / "out"
        labels = read_symbols(out / "program.symbols")
        resident_end = labels["sam_cli_image_end"]
        image = auto_code((out / "program.mgt").read_bytes())
        mapping = json.loads((out / "program.source-map.json").read_text())
        assert hashlib.sha256((out / "program.asm").read_bytes()).hexdigest().upper() == \
            mapping["assemblySha256"], f"Stale assembly map: {program}"
        for source in mapping["sources"]:
            assert hashlib.sha256(Path(source["file"]).read_bytes()).hexdigest().upper() == \
                source["sha256"], f"Stale source map: {source['file']}"
        inputs = [path for path in program.rglob("*") if path.is_file() and
                  not set(path.relative_to(program).parts) & {"out", "bin", "obj", "__pycache__"}]
        required_inputs.extend(str(path.relative_to(ROOT)) for path in inputs)
        row.update(
            resident_end=resident_end,
            resident_bytes=resident_end - 0x8000,
            stack_headroom=0xE000 - resident_end,
            resident_runtime_reserved=labels["sam_runtime_end"] - labels["sam_start"],
            bank3_code_and_staged_bytes=labels.get("cs_banked_code_page_3_end", 0),
            actual_samdos_load=len(image),
            emitted_methods=len(re.findall(r"(?m)^cs_method_\d+:", (out / "program.asm").read_text())),
            source_files=len(list(program.glob("*.cs"))),
            source_map_hashes_checked=len(mapping["sources"]),
            required_input_files=len(inputs),
            disk_sha256=hashlib.sha256((out / "program.dsk").read_bytes()).hexdigest(),
        )
    ignored = subprocess.run(["git", "check-ignore", "--stdin"], cwd=ROOT,
                             input="\n".join(required_inputs) + "\n",
                             text=True, capture_output=True)
    assert ignored.returncode in (0, 1), ignored.stderr
    assert not ignored.stdout, f"Required tutorial inputs are ignored:\n{ignored.stdout}"
    destination = ROOT / "out" / "verification"
    destination.mkdir(parents=True, exist_ok=True)
    (destination / "tutorial-sizes.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    print(f"Verified {len(rows)} actual disks; measurements: {destination / 'tutorial-sizes.json'}")


if __name__ == "__main__":
    main()
