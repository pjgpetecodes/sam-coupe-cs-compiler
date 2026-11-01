using SamCoupe.Sdk;

// Space starts a short three-note effect on channel zero. The table stores
// frequency-register values and durations in 50 Hz interrupt ticks, not
// milliseconds or musical note names. The chip sustains each pitch between
// updates, so the loop never waits in a blocking sound routine.
static class Program
{
    // An entry is [frequency, duration]. These are the rising cue values from
    // the lesson; duration counts independent frame updates.
    static readonly byte[] EffectNotes = new byte[] { 33, 4, 85, 4, 132, 8 };
    static byte EffectStep = 0;
    static byte EffectTicks = 0;
    static bool EffectActive = false;
    static bool SpaceWasDown = false;

    static void Main()
    {
        // Establish a silent, known chip state, then configure channel zero.
        ResetSoundChip();
        Sam.WriteSaaRegister(0, 0xAA);
        // Channel zero's octave is 3; global sound is enabled even while its
        // tone-generator bit remains clear between effects.
        Sam.WriteSaaRegister(16, 3);
        Sam.WriteSaaRegister(28, 1);

        while (true)
        {
            // Keyboard rows are active-low: zero in bit 0 means Space is down.
            // The latch recognizes an up-to-down edge and permits retriggering
            // only after the key has been released.
            bool spaceDown = Sam.IsKeyPressed(0x7FFE, 0x01);
            bool freshPress = spaceDown && !SpaceWasDown;
            SpaceWasDown = spaceDown;
            if (freshPress)
            {
                EffectStep = 0;
                EffectTicks = 0;
                EffectActive = true;
            }

            UpdateEffect();
            Sam.WaitForFrames(1);
        }
    }

    static void UpdateEffect()
    {
        // Return without touching the chip when no effect is scheduled.
        if (!EffectActive)
            return;

        // A nonzero timer means the current frequency is still playing.
        // Count one tick, and only select another note when it reaches zero.
        if (EffectTicks != 0)
        {
            EffectTicks--;
            if (EffectTicks != 0)
                return;
        }

        if (EffectStep >= 3)
        {
            // Disable channel zero rather than leaving its last pitch ringing.
            Sam.WriteSaaRegister(20, 0);
            EffectActive = false;
            return;
        }

        // Each two-byte table entry gives one pitch then its duration. Register
        // 8 selects channel zero's pitch; register 20 bit 0 starts its tone.
        byte tableOffset = (byte)(EffectStep * 2);
        Sam.WriteSaaRegister(8, EffectNotes[tableOffset]);
        EffectTicks = EffectNotes[tableOffset + 1];
        Sam.WriteSaaRegister(20, 1);
        EffectStep++;
    }

    static void ResetSoundChip()
    {
        // Explicitly clear all 32 registers so this program is independent of
        // whatever a previously run sound program left in the chip.
        byte register = 0;
        while (register < 32)
        {
            Sam.WriteSaaRegister(register, 0);
            register++;
        }
    }
}
