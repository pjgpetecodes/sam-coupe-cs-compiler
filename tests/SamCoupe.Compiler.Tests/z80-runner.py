import json
import sys

import z80


image_path = sys.argv[1]
image = open(image_path, "rb").read()
machine = z80.Z80Machine()
machine.set_memory_block(0x8000, image)
machine.pc = 0x8000
for _ in range(100):
    machine.ticks_to_stop = 1_000_000
    machine.run()
    if machine.halted:
        break
if not machine.halted:
    output = list(machine.memory[0x7000:0x7010])
    raise SystemExit(
        f"Z80 execution did not reach HALT (PC={machine.pc:04X}, SP={machine.sp:04X}, "
        f"B={machine.b:02X}, C={machine.c:02X}, output={output})"
    )
print(json.dumps(list(machine.memory[0x7000:0x7200])))
