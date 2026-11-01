# Tutorial ports: Blogs 7–9

| Reference entry point | C# program | Added lesson |
|---|---|---|
| `assembly\blog7\9-1.asm` | `tutorials\Blog07\09-1-BankedSpriteData\Program.cs` | Keep the 38 cropped frames in banked C# data; stage one selected frame in resident memory before drawing. |
| `assembly\blog8\10-1.asm` | `tutorials\Blog08\10-1-ByteStepMovement\Program.cs` | Move the sprite horizontally in byte steps while retaining the animation and per-buffer old-position state. |
| `assembly\blog9\11-1.asm` | `tutorials\Blog09\11-1-PixelPositioning\Program.cs` | Track pixel positions and use a typed pixel-shift kernel that retains the transparent edge nibbles. |

The three C# programs share the original Blog 6 cropped frames and CLUT values.
Their generated `TutorialData.g.cs` files retain frame order and source labels.
Frame bytes are authored as C# array initializers, not copied into runtime
assembly. Blog 7–9 bank the 38 × 572-byte frames on physical page 8; the
compiler reserves mapped address zero for null, so the first array begins at
address 2. A mapped source is copied to a resident one-frame buffer, then the
bank is restored before the screen drawing intrinsic runs.

All three programs retain the five background keys and the earlier idle, walk,
roll and jump selections. Blog 8 tracks the previous X independently for both
screen buffers to erase the correct old rectangle. Blog 9 derives its odd
pixel-position frame from the C# source frame with
`Sam.ShiftMaskedSpriteOnePixel` and draws a 14-byte row rather than truncating
the 13-byte sprite. The bounded 13×22 byte/nibble transform is in the
tree-shaken native runtime; movement and animation state remain authored C#.
The focused Z80 CPU test verifies every shifted byte and measures 99,338
nominal T-states for the validated transform (about 16.6 ms at 6 MHz, excluding
SAM display contention and interrupt overhead).

Build a single entry point with:

```powershell
python .\.vscode\sam_run.py .\tutorials\Blog07\09-1-BankedSpriteData\Program.cs --build
python .\.vscode\sam_run.py .\tutorials\Blog08\10-1-ByteStepMovement\Program.cs --build
python .\.vscode\sam_run.py .\tutorials\Blog09\11-1-PixelPositioning\Program.cs --build
```

The three resulting native disks assembled and fit below the resident `0xE000`
limit. Blog 7 was run in SimCoupe and its banked frame rendered; Blog 8 was
tested with X movement; Blog 9 was tested with pixel-stepped movement. These are
focused checks, not a claim that every tutorial in Blogs 7–28 has been ported.
