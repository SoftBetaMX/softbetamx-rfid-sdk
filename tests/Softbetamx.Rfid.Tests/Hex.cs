namespace Softbetamx.Rfid.Tests;

internal static class Hex
{
    /// <summary>"A0 03 FF 72 EC" → bytes.</summary>
    public static byte[] Parse(string hex) => Convert.FromHexString(hex.Replace(" ", "").Replace("\n", "").Replace("\r", ""));

    public static string Format(byte[] bytes) => Convert.ToHexString(bytes);
}
