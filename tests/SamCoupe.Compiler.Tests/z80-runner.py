import argparse
import json
import z80


parser = argparse.ArgumentParser()
parser.add_argument("image")
parser.add_argument("--metrics", action="store_true")
parser.add_argument("--stop-at", type=lambda value: int(value, 0), nargs="*", default=[])
args = parser.parse_args()
if args.metrics and not args.stop_at:
    parser.error("--metrics requires --stop-at addresses before HALT to exclude idle cycles")
image_path = args.image
image = open(image_path, "rb").read()
machine = z80.Z80Machine()
machine.set_memory_block(0x8000, image)
machine.pc = 0x8000
for address in args.stop_at:
    machine.set_breakpoint(address)
ticks = 0
for _ in range(100):
    machine.ticks_to_stop = 1_000_000
    machine.run()
    ticks += 1_000_000 - machine.ticks_to_stop
    if machine.halted or machine.pc in args.stop_at:
        break
if not machine.halted and machine.pc not in args.stop_at:
    output = list(machine.memory[0x7000:0x7010])
    raise SystemExit(
        f"Z80 execution did not reach HALT (PC={machine.pc:04X}, SP={machine.sp:04X}, "
        f"B={machine.b:02X}, C={machine.c:02X}, output={output})"
    )
output = list(machine.memory[0x7000:0x7200])
print(json.dumps({"output": output, "tStates": ticks} if args.metrics else output))
