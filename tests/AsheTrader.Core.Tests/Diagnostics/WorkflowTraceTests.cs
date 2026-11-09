using System.Globalization;
using AsheTrader.Core.Diagnostics;

namespace AsheTrader.Core.Tests.Diagnostics;

public sealed class WorkflowTraceTests
{
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
