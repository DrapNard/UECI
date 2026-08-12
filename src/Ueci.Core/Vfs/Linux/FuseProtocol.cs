using System.Globalization;
using System.Text;

namespace Ueci.Vfs.Linux;

internal static class FuseProtocol
{
    public static string Encode(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();

    public static string Decode(string value)
    {
        if ((value.Length & 1) != 0)
        {
            throw new InvalidDataException("Malformed hex field in FUSE protocol.");
        }
        return Encoding.UTF8.GetString(Convert.FromHexString(value));
    }

    public static string Kind(VirtualEngineNodeKind kind) => kind switch
    {
        VirtualEngineNodeKind.File => "F",
        VirtualEngineNodeKind.Directory => "D",
        VirtualEngineNodeKind.SymbolicLink => "L",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Mode(int value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
