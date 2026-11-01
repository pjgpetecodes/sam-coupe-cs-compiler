"""Assemble a large banked C# initializer, parse its actual SAMDOS load chain,
and execute disk-loaded startup on the physical 16 KB memory model.
"""
import re
import subprocess
from pathlib import Path

from build_runtime_tests import ROOT
from startup_banked_test import StartupMachine


def auto_code(disk):
    entries = [disk[i // 20 * 20 * 512 + i % 20 * 256:
                    i // 20 * 20 * 512 + i % 20 * 256 + 256] for i in range(80)]
    entry, = [e for e in entries if e[0] & 31 == 19 and e[1:5] == b"AUTO"]
    length = entry[239] * 16384 + entry[240] + entry[241] * 256
    assert entry[236:239] == bytes((1, 0, 0x80))
    assert entry[242:245] == bytes((2, 0, 0x80))
    assert length <= 11 * 16384, "Contiguous load reaches reserved high-page envelope"
    track, sector = entry[13], entry[14]
    data, seen = bytearray(), set()
    while track or sector:
        assert (track, sector) not in seen, "Cyclic sector chain"
        seen.add((track, sector))
        side, cylinder = track >> 7, track & 127
        assert cylinder < 80 and 1 <= sector <= 10
        offset = (cylinder * 20 + side * 10 + sector - 1) * 512
        block = disk[offset:offset + 512]
        if not data:
            assert block[0] == 19 and block[8] == 1
            assert block[7] * 16384 + block[1] + block[2] * 256 == length
            data.extend(block[9:510])
        else:
            data.extend(block[:510])
        track, sector = block[510:512]
    assert len(seen) == entry[11] * 256 + entry[12]
    assert len(data) >= length and len(data) - length < 510
    return bytes(data[:length])


def main():
    output = ROOT / "out" / "verification" / "loader-native"
    output.mkdir(parents=True, exist_ok=True)
    payload = bytes([0x11]) * 8192 + bytes(8192) + bytes([0xA5]) * 8192
    zeros = bytes(617)
    source = f"""
using SamCoupe.Sdk;
static class Program
{{
    [BankedData(12)]
    static readonly byte[] High = new byte[] {{ {','.join(str(b) for b in payload)} }};
    [BankedData(12)]
    static readonly ushort[] Words = new ushort[] {{ 0, 4660, 65535 }};
    [BankedData(14)]
    static readonly byte[] Zero = new byte[] {{ {','.join('0' for _ in zeros)} }};
    [BankedCode(3)]
    static void Code() {{ Sam.SetBorderColor(1); }}
    static void Main()
    {{
        Code();
        Sam.MapMemoryPage(12);
        Sam.SetBorderColor(High[0]);
        Sam.SetBorderColor((byte)Words[1]);
        Sam.RestoreMemoryPage();
        Sam.MapMemoryPage(14);
        Sam.SetBorderColor(Zero[616]);
        Sam.RestoreMemoryPage();
        Sam.WaitForFrames(1);
    }}
}}
"""
    (output / "Program.cs").write_text(source)
    cli = ROOT / "src" / "SamCoupe.Compiler.Cli" / "bin" / "Debug" / "net10.0" / "SamCoupe.Compiler.Cli.dll"
    subprocess.run(["dotnet", str(cli), str(output / "Program.cs"), "--build",
                    "--tools", str(ROOT / "tools.local.json")],
                   cwd=ROOT, check=True, capture_output=True, text=True)
    built = output / "out"
    assembly = (built / "program.asm").read_text()
    assert "DUMP 12," not in assembly and "DUMP 14," not in assembly
    disk_image = auto_code((built / "program.mgt").read_bytes())
    initial_screen = int(re.search(r"cs_initial_screen_page:\s+EQU (\d+)", assembly)[1])
    machine = StartupMachine(built, initial_screen, image=disk_image)
    machine.run_to(machine.labels["cs_main"])
    page3_before = bytes(machine.pages[3])
    assert machine.pages[12] == bytes([0xA5]) * 16384, "Fixture must not preload high arrays"
    assert machine.pages[14] == bytes([0xA5]) * 16384
    method = re.search(r"cs_main:\s+CALL cs_initialize_banked_data\s+CALL (\w+)", assembly)[1]
    machine.cpu.clear_breakpoint(machine.labels["cs_main"])
    machine.cpu.set_breakpoint(machine.labels[method])
    machine.run_to(machine.labels[method])
    high = bytes(machine.pages[12] + machine.pages[13])
    expected = len(payload).to_bytes(2, "little") + payload + b"\x03\x00\x00\x00\x34\x12\xFF\xFF"
    assert high[2:2 + len(expected)] == expected, "Byte/word length prefixes or full authored values changed"
    assert high[0:2] == b"\xA5\xA5" and high[2 + len(expected):] == \
        bytes([0xA5]) * (32768 - 2 - len(expected)), "Initializer touched neighboring bytes"
    zero = bytes(machine.pages[14] + machine.pages[15])
    assert zero[2:621] == b"\x69\x02" + zeros
    assert zero[:2] == b"\xA5\xA5" and zero[621:] == bytes([0xA5]) * (32768 - 621)
    assert bytes(machine.pages[3]) == page3_before, "Initializer corrupted staged data or banked code"
    assert machine.lmpr == 0x1F and machine.hmpr == 1 and not machine.cpu.iff1
    machine.cpu.clear_breakpoint(machine.labels[method])
    machine.run_to(machine.labels["sam_wait_frames"])
    assert machine.lmpr == 0x1F and machine.hmpr == 1
    print(f"PASS actual MGT CODE header/sector chain: {len(disk_image)} loaded bytes, native entry reached")
    print("PASS full startup: 24 KB nonzero bank12 array, adjacent ushort array, bank14 zero buffer, "
          "unchanged neighbors/page3 code, ROM restored and native C# reached first wait")


if __name__ == "__main__":
    main()
