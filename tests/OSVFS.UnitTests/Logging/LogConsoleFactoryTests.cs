using System.Text.Json;
using Microsoft.Extensions.Logging;
using OSVFS.Configuration;
using OSVFS.Logging;
using Xunit;

namespace OSVFS.UnitTests.Logging;

/// <summary>
/// Smoke tests for <see cref="LogConsoleFactory"/>: verify that the JSON
/// formatter emits one UTF-8 JSON object per log entry with the expected
/// timestamp / level / category / message / properties shape, and that the
/// text formatter still produces non-JSON single-line output.
/// </summary>
public class LogConsoleFactoryTests
{
    [Fact]
    public void Json_format_emits_one_json_line_per_log_entry()
    {
        var output = CaptureConsoleOut(() =>
        {
            using var factory = LogConsoleFactory.Create(verbose: false, LogFormat.Json);
            var logger = factory.CreateLogger("OSVFS");
            logger.LogInformation("Virtualizing s3://{Bucket}", "my-bucket");
        });

        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();

        Assert.Single(lines);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;

        Assert.Equal("Information", root.GetProperty("LogLevel").GetString());
        Assert.Equal("OSVFS", root.GetProperty("Category").GetString());
        Assert.Equal("Virtualizing s3://my-bucket", root.GetProperty("Message").GetString());

        var timestamp = root.GetProperty("Timestamp").GetString();
        Assert.NotNull(timestamp);
        Assert.EndsWith("Z", timestamp);
        Assert.True(DateTimeOffset.TryParse(timestamp, out _),
            $"Timestamp '{timestamp}' is not parseable as ISO-8601 UTC.");

        var state = root.GetProperty("State");
        Assert.Equal("my-bucket", state.GetProperty("Bucket").GetString());
    }

    [Fact]
    public void Text_format_emits_non_json_single_line()
    {
        var output = CaptureConsoleOut(() =>
        {
            using var factory = LogConsoleFactory.Create(verbose: false, LogFormat.Text);
            var logger = factory.CreateLogger("OSVFS");
            logger.LogInformation("hello {Name}", "world");
        });

        Assert.Contains("hello world", output, StringComparison.Ordinal);
        Assert.False(output.TrimStart().StartsWith('{'),
            "Text format should not start with a JSON object brace.");
    }

    [Fact]
    public void Verbose_flag_opens_debug_level()
    {
        var output = CaptureConsoleOut(() =>
        {
            using var factory = LogConsoleFactory.Create(verbose: true, LogFormat.Json);
            var logger = factory.CreateLogger("OSVFS");
            logger.LogDebug("debug payload {Value}", 42);
        });

        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();

        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("Debug", doc.RootElement.GetProperty("LogLevel").GetString());
    }

    /// <summary>
    /// Redirects <see cref="Console.Out"/> to an in-memory writer for the
    /// duration of <paramref name="action"/>. The console logger flushes its
    /// background queue when the factory is disposed inside the action, so the
    /// returned string captures the full output.
    /// </summary>
    private static string CaptureConsoleOut(Action action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }
}
