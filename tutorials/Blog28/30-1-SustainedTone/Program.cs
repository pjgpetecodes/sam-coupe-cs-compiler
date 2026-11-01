using SamCoupe.Sdk;

// This sound-only lesson introduces the SAA1099 without involving the graphics
// engine. The chip keeps producing a tone after these register writes, so the
// program needs only to wait for the next ROM-serviced interrupt repeatedly.
static class Program
{
    static void Main()
    {
        // Reset first: a newly loaded program must not inherit another tune's
        // channel enables, stereo levels, noise or envelope settings.
        ResetSoundChip();

        // Register 0 sets the stereo level for tone channel zero. Each nibble
        // controls one output side; 0xAA is a moderate equal-volume setting.
        Sam.WriteSaaRegister(0, 0xAA);
        // Register 8 is channel zero's frequency value. The octave register
        // supplies the other part of the pitch, so neither value is a note name.
        Sam.WriteSaaRegister(8, 227);
        // Register 16 contains octave settings for channels zero and one.
        // Its low three bits select octave two for channel zero.
        Sam.WriteSaaRegister(16, 2);
        // Register 20's bit 0 enables only channel zero's tone generator.
        Sam.WriteSaaRegister(20, 1);
        // Register 28 is the global output enable. A configured channel is
        // silent until this master bit is set.
        Sam.WriteSaaRegister(28, 1);

        // The hardware sustains the configured tone; HALT waits for a 50 Hz
        // ROM interrupt with ROM mapped in and interrupts enabled.
        while (true)
            Sam.WaitForFrames(1);
    }

    static void ResetSoundChip()
    {
        // The SAA1099 has 32 registers. Clear them first so a previous sound
        // program cannot leave a second channel, envelope, or noise enabled.
        byte register = 0;
        while (register < 32)
        {
            Sam.WriteSaaRegister(register, 0);
            register++;
        }
    }
}
