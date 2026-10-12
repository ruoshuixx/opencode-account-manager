using System.Buffers.Binary;

namespace AccountManager.Core.Updates;

/// <summary>Validates executable headers without executing the downloaded file.</summary>
public static class ExecutableFormat
{
    private const int ElfHeaderSize = 64;
    private const ushort EtExecutable = 2;
    private const ushort EtDyn = 3;
    private const ushort EmX86_64 = 0x3e;

    /// <summary>
    /// Returns whether <paramref name="header"/> starts like a Debian package: the <c>ar</c> archive
    /// magic followed by a first member named <c>debian-binary</c>.
    /// </summary>
    public static bool IsDebPackage(ReadOnlySpan<byte> header)
    {
        ReadOnlySpan<byte> magic = "!<arch>\n"u8;
        ReadOnlySpan<byte> member = "debian-binary"u8;
        return header.Length >= magic.Length + 16
            && header[..magic.Length].SequenceEqual(magic)
            && header.Slice(magic.Length, member.Length).SequenceEqual(member);
    }

    /// <summary>Returns whether <paramref name="header"/> is a complete ELF64 little-endian x86-64 header.</summary>
    public static bool IsLinuxX64(ReadOnlySpan<byte> header)
    {
        if (header.Length < ElfHeaderSize) { return false; }
        if (header[0] != 0x7f || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F') { return false; }
        if (header[4] != 2 || header[5] != 1 || header[6] != 1) { return false; }
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]) != EmX86_64) { return false; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]) != 1) { return false; }

        var type = BinaryPrimitives.ReadUInt16LittleEndian(header[16..18]);
        return type is EtExecutable or EtDyn;
    }
}
