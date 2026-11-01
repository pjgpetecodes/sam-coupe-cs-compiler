using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        Sam.Rom.ClearScreen();
        string message = "C# on " + "SAM Coupe!";
        PrintMessage(message);
        Sam.Rom.WaitForKey();
    }

    static void PrintMessage(string text)
    {
        Sam.Rom.Print(text);
        Sam.Rom.PrintChar('\r');
        for (int index = 0; index < text.Length; index++)
            Sam.Rom.PrintChar(text[index]);
        Sam.Rom.PrintChar('\r');
    }
}
