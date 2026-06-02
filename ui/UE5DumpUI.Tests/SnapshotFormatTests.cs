using UE5DumpUI.Models;
using Xunit;

namespace UE5DumpUI.Tests;

public class SnapshotFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(44040192, "42.0 MB")]   // 42 * 1024 * 1024
    [InlineData(1610612736, "1.5 GB")]  // 1.5 * 1024^3
    [InlineData(-5, "0 B")]             // negative clamped
    public void Bytes_FormatsHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, SnapshotFormat.Bytes(bytes));
    }
}
