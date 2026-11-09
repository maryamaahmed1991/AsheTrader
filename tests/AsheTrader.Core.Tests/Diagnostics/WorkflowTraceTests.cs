using System.Globalization;
using AsheTrader.Core.Diagnostics;

namespace AsheTrader.Core.Tests.Diagnostics;

public sealed class WorkflowTraceTests
{
    [Fact]
    public void PasteBudgetKeepsNewestFailureAndCompleteChronologicalLines()
    {
        var trace = new WorkflowTrace();
        for (var index = 0; index < 50; index++) trace.Add("native", $"earlier step {index}: " + new string('x', 100));
        trace.Add("native", "bell interaction dispatched");
        trace.Add("native", "bell failed to open");
        var report = trace.ExportRecent(500);
        Assert.InRange(report.Length, 1, 500);
        Assert.Contains("earlier trace entries omitted", report);
        Assert.DoesNotContain("earlier step 0:", report);
        Assert.Contains("bell interaction dispatched", report);
        Assert.EndsWith("bell failed to open", report);
        Assert.True(report.IndexOf("bell interaction dispatched", StringComparison.Ordinal) < report.IndexOf("bell failed to open", StringComparison.Ordinal));
        Assert.All(report.Split('\n').Skip(1), line => Assert.StartsWith("20", line));
    }

    [Fact]
    public void UntrimmedRecentExportMatchesFullTraceAndDoesNotMutateIt()
    {
        var trace = new WorkflowTrace();
        Assert.Equal(string.Empty, trace.ExportRecent(2000));
        trace.Add("native", "one");
        trace.Add("native", "two");
        var full = trace.Export();
        Assert.Equal(full, trace.ExportRecent(2000));
        _ = trace.ExportRecent(128);
        Assert.Equal(full, trace.Export());
        Assert.Throws<ArgumentOutOfRangeException>(() => trace.ExportRecent(127));
    }

    [Fact]
    public void BoundedTraceKeepsRecentStepsAndImmutableExport()
    {
        var trace = new WorkflowTrace(capacity: 3);
        trace.Add("native", "step-one");
        var saved = trace.Export();
        foreach (var step in new[] { "step-two", "step-three", "step-four" }) trace.Add("native", step);

        var lines = trace.Export().Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("step-two", lines[0]);
        Assert.EndsWith("step-four", lines[2]);
        Assert.Contains("step-one", saved);
        Assert.DoesNotContain("step-one", trace.Export());
        Assert.True(DateTimeOffset.TryParseExact(lines[0][..24], "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp));
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
    }

    [Fact]
    public void UntrustedMenuTextCannotInsertLinesOrInvisibleFormatting()
    {
        var trace = new WorkflowTrace();
        trace.Add("menu\nspoof\t", "Entrust\r\npretend-success\0\u2028\u2029\u202eitems");
        var exported = trace.Export();

        Assert.DoesNotContain('\n', exported);
        Assert.DoesNotContain('\r', exported);
        Assert.DoesNotContain('\0', exported);
        Assert.Contains("[menu\\nspoof\\t]", exported);
        Assert.Contains("Entrust\\r\\npretend-success\\u0000\\u2028\\u2029\\u202Eitems", exported);
    }

    [Fact]
    public void OversizedTextCannotExceedPerLineOrRetentionBounds()
    {
        var trace = new WorkflowTrace(capacity: 2, maxLineCharacters: 128);
        for (var index = 0; index < 8; index++) trace.Add(new string('s', 10_000), new string('m', 100_000));
        var lines = trace.Export().Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.InRange(line.Length, 1, 128);
            Assert.EndsWith("…", line);
        });
        Assert.InRange(trace.Export().Length, 1, 257);
    }

    [Fact]
    public void ConcurrentWritersAndExportsKeepWholeEntriesWithoutLoss()
    {
        var trace = new WorkflowTrace(capacity: 500);
        Parallel.For(0, 400, index =>
        {
            trace.Add("worker", $"entry:{index:D3}");
            foreach (var line in trace.Export().Split('\n'))
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[worker\] entry:\d{3}$", line);
        });

        var entries = trace.Export().Split('\n');
        Assert.Equal(400, entries.Length);
        Assert.Equal(400, entries.Select(line => line[^3..]).Distinct().Count());
    }
}
