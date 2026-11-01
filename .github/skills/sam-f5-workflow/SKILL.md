---
name: sam-f5-workflow
description: Use when changing VS Code F5 launch, source discovery, disk packaging or source-address maps.
---

# F5 build and run

1. Inspect `.vscode\launch.json`, `tasks.json`, `sam_run.py` and the CLI.
2. Capture the active source once; compile only its program folder and explicit
   shared inputs. Exclude generated files and reject ambiguous entry points.
3. Propagate compiler/tool errors; never launch stale or incomplete artifacts.
4. Test paths containing spaces, missing tools and repeated launches.
5. Resolve source-map labels against the final assembler map; reject stale maps.
6. Verify the actual VS Code F5 path as well as the wrapper command.
7. Use manual mapped SimCoupe breakpoints until an approved remote bridge exists;
   report debug capabilities accurately.
