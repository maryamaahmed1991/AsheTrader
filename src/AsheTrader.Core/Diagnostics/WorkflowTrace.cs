using System.Globalization;
using System.Text;

namespace AsheTrader.Core.Diagnostics;

/// <summary>A bounded, process-local trace. Callers select diagnostic fields; no configuration is serialized.</summary>
public sealed class WorkflowTrace
{
    private readonly object gate = new();
    private readonly Queue<string> entries = new();
    private readonly int capacity;
    private readonly int maxLineCharacters;

    public WorkflowTrace(int capacity = 500, int maxLineCharacters = 2000)
    {
        if (capacity is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(capacity));
        // Leave room for the UTC timestamp, a bounded source and useful message text.
        if (maxLineCharacters is < 128 or > 16_384) throw new ArgumentOutOfRangeException(nameof(maxLineCharacters));
        this.capacity = capacity;
        this.maxLineCharacters = maxLineCharacters;
    }

    public void Add(string source, string message)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(message);
        var safeSource = SingleLine(source, 64);
        lock (gate)
        {
            var prefix = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
                + " [" + safeSource + "] ";
            entries.Enqueue(prefix + SingleLine(message, maxLineCharacters - prefix.Length));
            while (entries.Count > capacity) entries.Dequeue();
        }
    }

    /// <summary>Returns one coherent, oldest-to-newest snapshot, safe to copy while writers continue.</summary>
    public string Export()
    {
        lock (gate) return string.Join('\n', entries);
    }

    /// <summary>Preserves the newest complete entries within a paste-friendly character budget.</summary>
    public string ExportRecent(int maxCharacters)
    {
        if (maxCharacters < 128) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        lock (gate)
        {
            var snapshot = entries.ToArray();
            var start = snapshot.Length;
            var length = 0;
            while (start > 0)
            {
                var nextLength = length + snapshot[start - 1].Length + (start < snapshot.Length ? 1 : 0);
                var noticeLength = start > 1 ? OmittedNotice(start - 1).Length + 1 : 0;
                if (nextLength + noticeLength > maxCharacters) break;
                start--;
                length = nextLength;
            }
            var recent = string.Join('\n', snapshot.AsSpan(start).ToArray());
            return start == 0 ? recent : OmittedNotice(start) + (recent.Length > 0 ? "\n" + recent : "");
        }
    }

    private static string OmittedNotice(int count) => $"--- {count} earlier trace entries omitted; full detail remains in the Dalamud log. ---";

    private static string SingleLine(string value, int limit)
    {
        var output = new StringBuilder(Math.Min(limit, value.Length));
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var token = rune.Value switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when category is UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                    => rune.Value <= 0xffff ? $"\\u{rune.Value:X4}" : $"\\U{rune.Value:X8}",
                _ => rune.ToString(),
            };
            if (output.Length + token.Length <= limit) { output.Append(token); continue; }
            if (output.Length == limit)
            {
                var remove = char.IsLowSurrogate(output[^1]) ? 2 : 1;
                output.Length -= remove;
            }
            output.Append('…');
            break;
        }
        return output.ToString();
    }
}
