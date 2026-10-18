using DustPlanner.Host;
using Microsoft.Extensions.Logging;

namespace DustPlanner.Host.Tests;

#pragma warning disable CA1873 // the logger under test is always enabled; the arguments are the point
public sealed class FileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dustplanner-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempDir.Delete(_dir);

    [Fact]
    public void Writes_one_line_per_entry_with_level_short_category_and_exception()
    {
        using (var provider = new FileLoggerProvider(_dir))
        {
            var logger = provider.CreateLogger("DustPlanner.Kick.KickConnector");
            logger.LogInformation("Kick: connected as @{Slug}", "tsukuru-tv");
            logger.LogWarning(new InvalidOperationException("boom"), "Kick: answering !{Command} failed", "deck");
        }

        var lines = File.ReadAllLines(Path.Combine(_dir, FileLoggerProvider.FileName));
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} INF KickConnector: Kick: connected as @tsukuru-tv$", lines[0]);
        Assert.Matches(@" WRN KickConnector: Kick: answering !deck failed \| InvalidOperationException: boom$", lines[1]);
    }

    [Fact]
    public void Rotates_once_past_the_size_cap_and_keeps_the_previous_file()
    {
        var path = Path.Combine(_dir, FileLoggerProvider.FileName);
        using (var provider = new FileLoggerProvider(_dir))
        {
            var logger = provider.CreateLogger("Test");
            var filler = new string('x', 1000);
            for (var i = 0; i < FileLoggerProvider.MaxBytes / 1000 + 10; i++)
            {
                logger.LogInformation("{Filler}", filler);
            }

            logger.LogInformation("after rotation");
        }

        Assert.True(File.Exists(path + ".1"));
        Assert.True(new FileInfo(path + ".1").Length > FileLoggerProvider.MaxBytes);
        var current = File.ReadAllLines(path);
        Assert.Contains(current, l => l.EndsWith("Test: after rotation", StringComparison.Ordinal));
        Assert.True(new FileInfo(path).Length < FileLoggerProvider.MaxBytes / 2);
    }
}
#pragma warning restore CA1873
