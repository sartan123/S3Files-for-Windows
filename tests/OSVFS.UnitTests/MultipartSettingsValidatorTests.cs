using OSVFS.ObjectStore.S3;
using Xunit;

namespace OSVFS.UnitTests;

/// <summary>
/// Boundary checks for the multipart-upload validation that gates startup.
/// </summary>
public class MultipartSettingsValidatorTests
{
    [Fact]
    public void Returns_null_when_both_inputs_are_null()
    {
        Assert.Null(MultipartSettingsValidator.Validate(null, null));
    }

    [Fact]
    public void Returns_null_at_min_part_size()
    {
        Assert.Null(MultipartSettingsValidator.Validate(
            thresholdBytes: 8L * 1024 * 1024,
            partSizeBytes: S3Backend.MinMultipartPartSizeBytes));
    }

    [Fact]
    public void Returns_null_at_max_part_size()
    {
        Assert.Null(MultipartSettingsValidator.Validate(
            thresholdBytes: 8L * 1024 * 1024,
            partSizeBytes: S3Backend.MaxMultipartPartSizeBytes));
    }

    [Fact]
    public void Rejects_part_size_one_byte_below_min()
    {
        var error = MultipartSettingsValidator.Validate(
            thresholdBytes: 8L * 1024 * 1024,
            partSizeBytes: S3Backend.MinMultipartPartSizeBytes - 1);
        Assert.NotNull(error);
        Assert.Contains("multipart-part-size", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_part_size_one_byte_above_max()
    {
        var error = MultipartSettingsValidator.Validate(
            thresholdBytes: 8L * 1024 * 1024,
            partSizeBytes: S3Backend.MaxMultipartPartSizeBytes + 1);
        Assert.NotNull(error);
        Assert.Contains("5 GiB", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_zero_threshold()
    {
        var error = MultipartSettingsValidator.Validate(
            thresholdBytes: 0,
            partSizeBytes: S3Backend.MinMultipartPartSizeBytes);
        Assert.NotNull(error);
        Assert.Contains("multipart-threshold", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_negative_threshold()
    {
        var error = MultipartSettingsValidator.Validate(
            thresholdBytes: -1,
            partSizeBytes: S3Backend.MinMultipartPartSizeBytes);
        Assert.NotNull(error);
    }

    [Fact]
    public void Allows_threshold_below_part_size()
    {
        // A threshold below part size means files between threshold and part size
        // become a single-part multipart upload — unusual but legal under S3.
        Assert.Null(MultipartSettingsValidator.Validate(
            thresholdBytes: 6L * 1024 * 1024,
            partSizeBytes: 16L * 1024 * 1024));
    }

    [Fact]
    public void Max_part_count_constant_matches_S3_documented_cap()
    {
        // The S3 service caps multipart uploads at 10 000 parts. Surfacing the
        // constant lets callers compute "is this file size achievable with these
        // settings" against a single source of truth.
        Assert.Equal(10_000, S3Backend.MaxMultipartPartCount);
    }
}
