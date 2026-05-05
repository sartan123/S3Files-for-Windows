using S3Files.Windows.S3;
using Xunit;

namespace S3Files.Windows.UnitTests;

public class S3UtilTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("foo", "foo")]
    [InlineData("foo\\bar.txt", "foo/bar.txt")]
    [InlineData("a\\b\\c\\d.bin", "a/b/c/d.bin")]
    public void ToS3Key_replaces_backslashes_with_slashes(string input, string expected)
    {
        Assert.Equal(expected, S3Util.ToS3Key(input));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("foo", "foo")]
    [InlineData("foo/bar.txt", "foo\\bar.txt")]
    [InlineData("a/b/c/d.bin", "a\\b\\c\\d.bin")]
    public void ToRelativePath_replaces_slashes_with_backslashes(string input, string expected)
    {
        Assert.Equal(expected, S3Util.ToRelativePath(input));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("foo", "foo/")]
    [InlineData("foo/", "foo/")]
    [InlineData("foo\\bar", "foo/bar/")]
    [InlineData("foo\\bar\\", "foo/bar/")]
    public void NormalizePrefix_returns_empty_or_trailing_slash(string input, string expected)
    {
        Assert.Equal(expected, S3Util.NormalizePrefix(input));
    }

    [Fact]
    public void Roundtrip_relative_to_key_to_relative_is_stable()
    {
        const string original = "a\\b\\c.txt";
        var roundTripped = S3Util.ToRelativePath(S3Util.ToS3Key(original));
        Assert.Equal(original, roundTripped);
    }
}
