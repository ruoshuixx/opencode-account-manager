using AccountManager.Core.Updates;
using Xunit;

namespace AccountManager.Core.Tests;

public sealed class ExecutableFormatTests
{
    [Theory]
    [InlineData(2, 1, 0x3e, true)]
    [InlineData(2, 1, 0xb7, false)]
    [InlineData(1, 1, 0x3e, false)]
    [InlineData(2, 2, 0x3e, false)]
    public void ElfMustMatchClassEndianAndMachine(byte elfClass, byte endian, int machine, bool expected)
    {
        var header = BuildSyntheticElf(elfClass, endian, (ushort)machine);

        Assert.Equal(expected, ExecutableFormat.IsLinuxX64(header));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ElfAcceptsExecutableAndSharedObjectTypes(ushort type)
    {
        var header = BuildSyntheticElf(type: type);

        Assert.True(ExecutableFormat.IsLinuxX64(header));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(64)]
    public void ElfRejectsInvalidMagicOrHeaderVersion(int field)
    {
        var badMagic = BuildSyntheticElf();
        badMagic[0] = 0;
        var badIdentVersion = BuildSyntheticElf();
        badIdentVersion[6] = 0;
        var badHeaderVersion = BuildSyntheticElf();
        badHeaderVersion[20] = 0;
        var badType = BuildSyntheticElf(type: 1);
        var header = field switch
        {
            0 => badMagic,
            1 => badIdentVersion,
            4 => badHeaderVersion,
            _ => badType,
        };

        Assert.False(ExecutableFormat.IsLinuxX64(header));
    }

    [Fact]
    public void ElfRejectsEveryHeaderShorterThanSixtyFourBytes()
    {
        var complete = BuildSyntheticElf();

        for (var length = 0; length < 64; length++)
        {
            Assert.False(ExecutableFormat.IsLinuxX64(complete.AsSpan(0, length)), $"Accepted {length}-byte ELF header.");
        }
    }

    [Fact]
    public void ElfRequiresTheFullThirtyTwoBitHeaderVersionToEqualOne()
    {
        var header = BuildSyntheticElf();
        header[23] = 1;

        Assert.False(ExecutableFormat.IsLinuxX64(header));
    }

    [Fact]
    public void ElfRejectsPortableExecutableHeader()
    {
        var peHeader = new byte[64];
        peHeader[0] = (byte)'M';
        peHeader[1] = (byte)'Z';

        Assert.False(ExecutableFormat.IsLinuxX64(peHeader));
    }

    private static byte[] BuildSyntheticElf(
        byte elfClass = 2,
        byte endian = 1,
        ushort machine = 0x3e,
        ushort type = 2)
    {
        var header = new byte[64];
        header[0] = 0x7f;
        header[1] = (byte)'E';
        header[2] = (byte)'L';
        header[3] = (byte)'F';
        header[4] = elfClass;
        header[5] = endian;
        header[6] = 1;
        header[16] = (byte)type;
        header[17] = (byte)(type >> 8);
        header[18] = (byte)machine;
        header[19] = (byte)(machine >> 8);
        header[20] = 1;
        return header;
    }
}
