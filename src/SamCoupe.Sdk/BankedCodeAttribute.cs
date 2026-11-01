namespace SamCoupe.Sdk;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class BankedCodeAttribute(byte page) : Attribute
{
    public byte Page { get; } = page;
}
