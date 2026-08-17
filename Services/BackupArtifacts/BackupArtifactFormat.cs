using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CitusManager.Services.BackupArtifacts;

internal static class BackupArtifactFormat
{
    public const int Version = 1;
    public const int HeaderLength = 8;
    public const int FrameHeaderLength = 4 + 12 + 16;
    public static ReadOnlySpan<byte> Magic => "CMBA"u8;

    public static async Task WriteHeaderAsync(Stream output, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), Version);
        await output.WriteAsync(header, cancellationToken);
    }

    public static async Task ReadAndValidateHeaderAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        await input.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic) ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != Version)
        {
            throw new BackupArtifactIntegrityException("Unsupported or corrupt backup object header.");
        }
    }

    public static string Hex(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(value);

    public static bool FixedHexEquals(string expected, ReadOnlySpan<byte> actual)
    {
        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expected);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actual);
    }
}
