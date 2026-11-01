using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        Sam.ClearScreen();
        for (int row = 0; row < 4; row++)
        {
            byte pixels = 0xFF;
            if ((row & 1) != 0)
                pixels = 0x44;
            for (int column = 0; column < 2; column++)
                Sam.WriteScreenByte((ushort)(row * 128 + column), pixels);
        }
        Sam.WaitForKey();
    }
}
