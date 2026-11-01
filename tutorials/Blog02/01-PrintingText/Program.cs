// Blog 2, lesson 1: ROM text output; source: assembly\blog2\1.asm.
using SamCoupe.Sdk;

// The SDK names hardware/ROM operations that the compiler translates to native Z80 code.
// A static class needs no object allocation; Main is the program's starting point on the SAM.
static class Program
{
    static void Main()
    {
        // byte is an unsigned 8-bit number (0..255), sufficient for these character codes.
        // The for loop starts at 32, tests character < 96 before each print, then increments
        // with ++. Thus it prints codes 32..95 inclusive: 64 characters, not 96 characters.
        // The (char) cast explicitly interprets the numeric code as a C# character.
        // ROM means the machine's built-in read-only firmware; its current output stream
        // determines where PrintChar sends the character instead of us drawing its pixels.
        for (byte character = 32; character < 96; character++)
            Sam.Rom.PrintChar((char)character);

        // Unlike the graphics lessons, both printing and this key wait use ROM services.
        // A quoted string supplies the whole message. Waiting keeps it visible until a key
        // is pressed; reaching the end of Main then returns through the runtime to BASIC.
        Sam.Rom.Print("Howsaboutis, then!!!");
        Sam.Rom.WaitForKey();
    }
}
