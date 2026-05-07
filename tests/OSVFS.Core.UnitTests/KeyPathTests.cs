using OSVFS.ObjectStore;
using Xunit;

namespace OSVFS.Core.UnitTests;

public class KeyPathTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("foo", "foo")]
    [InlineData("foo\\bar.txt", "foo/bar.txt")]
    [InlineData("a\\b\\c\\d.bin", "a/b/c/d.bin")]
    public void ToObjectKey_replaces_backslashes_with_slashes(string input, string expected)
    {
        Assert.Equal(expected, KeyPath.ToObjectKey(input));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("foo", "foo")]
    [InlineData("foo/bar.txt", "foo\\bar.txt")]
    [InlineData("a/b/c/d.bin", "a\\b\\c\\d.bin")]
    public void ToRelativePath_replaces_slashes_with_backslashes(string input, string expected)
    {
        Assert.Equal(expected, KeyPath.ToRelativePath(input));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("foo", "foo/")]
    [InlineData("foo/", "foo/")]
    [InlineData("foo\\bar", "foo/bar/")]
    [InlineData("foo\\bar\\", "foo/bar/")]
    public void NormalizePrefix_returns_empty_or_trailing_slash(string input, string expected)
    {
        Assert.Equal(expected, KeyPath.NormalizePrefix(input));
    }

    [Fact]
    public void Roundtrip_relative_to_key_to_relative_is_stable()
    {
        const string original = "a\\b\\c.txt";
        var roundTripped = KeyPath.ToRelativePath(KeyPath.ToObjectKey(original));
        Assert.Equal(original, roundTripped);
    }
}
