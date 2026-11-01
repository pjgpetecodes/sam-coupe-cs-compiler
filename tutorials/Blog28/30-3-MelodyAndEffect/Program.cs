using SamCoupe.Sdk;

// The final sound example runs an original short melody on channel zero while
// Space triggers an independent effect on channel one. Both timers advance
// once per 50 Hz update, so changing one pitch never stalls the other.
static class Program
{
    // Each table pair is a frequency-register value followed by update ticks.
    // These simple sequences are authored for this lesson, not game music.
    static readonly byte[] MelodyNotes = new byte[]
    {
        33, 8, 85, 8, 132, 8, 193, 8, 132, 8, 85, 8, 33, 12
    };
    static readonly byte[] EffectNotes = new byte[] { 33, 4, 85, 4, 132, 8 };

    static byte MelodyStep = 0;
    static byte MelodyTicks = 0;
    static byte EffectStep = 0;
    static byte EffectTicks = 0;
    static bool EffectActive = false;
    static bool SpaceWasDown = false;

    static void Main()
    {
        // Clear inherited sound state before configuring the two channels.
        ResetSoundChip();
        // Separate amplitude registers make the effect quieter. Register 16
        // packs channel-zero octave 2 into bits 0..2 and channel-one octave 3
        // into bits 4..6. The melody starts alone; bit 1 is enabled on demand.
        Sam.WriteSaaRegister(0, 0x88);
        Sam.WriteSaaRegister(1, 0x66);
        Sam.WriteSaaRegister(16, 0x32);
        Sam.WriteSaaRegister(20, 1);
        Sam.WriteSaaRegister(28, 1);

        while (true)
        {
            bool spaceDown = Sam.IsKeyPressed(0x7FFE, 0x01);
            bool freshPress = spaceDown && !SpaceWasDown;
            SpaceWasDown = spaceDown;
            if (freshPress)
            {
                // Retriggering restarts only the effect. MelodyStep and
                // MelodyTicks continue with no change.
                EffectStep = 0;
                EffectTicks = 0;
                EffectActive = true;
            }

            UpdateMelody();
            UpdateEffect();
            Sam.WaitForFrames(1);
        }
    }

    static void UpdateMelody()
    {
        // A zero timer starts the first note or follows an expired one.
        if (MelodyTicks != 0)
        {
            MelodyTicks--;
            if (MelodyTicks != 0)
                return;
        }

        // Seven pairs form one loop. Wrap to the beginning only after the
        // previous note's duration has elapsed.
        if (MelodyStep >= 7)
            MelodyStep = 0;
        byte tableOffset = (byte)(MelodyStep * 2);
        Sam.WriteSaaRegister(8, MelodyNotes[tableOffset]);
        MelodyTicks = MelodyNotes[tableOffset + 1];
        MelodyStep++;
    }

    static void UpdateEffect()
    {
        // An inactive effect does not change the continuously playing melody.
        if (!EffectActive)
            return;

        // Decrement once per update. Only expiration selects the next pair;
        // returning here lets the melody and the ROM frame wait continue.
        if (EffectTicks != 0)
        {
            EffectTicks--;
            if (EffectTicks != 0)
                return;
        }

        if (EffectStep >= 3)
        {
            // Bit 0 keeps the melody generator running; only channel one stops.
            Sam.WriteSaaRegister(20, 1);
            EffectActive = false;
            return;
        }

        byte tableOffset = (byte)(EffectStep * 2);
        Sam.WriteSaaRegister(9, EffectNotes[tableOffset]);
        EffectTicks = EffectNotes[tableOffset + 1];
        // Bits 0 and 1 enable melody and effect together. The melody frequency
        // remains in register 8 while the effect uses register 9.
        Sam.WriteSaaRegister(20, 3);
        EffectStep++;
    }

    static void ResetSoundChip()
    {
        // Registers 0..31 include level, pitch, octave, enables and envelopes.
        // Clear each through the real select/data ports, not a software shadow.
        byte register = 0;
        while (register < 32)
        {
            Sam.WriteSaaRegister(register, 0);
            register++;
        }
    }
}
