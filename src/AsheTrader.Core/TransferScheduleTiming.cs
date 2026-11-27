namespace AsheTrader.Core;

public enum TransferRecurrence { Once, Daily, Weekly }

public sealed class TransferScheduleTiming
{
    public DateTime LocalStart { get; set; }
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public TransferRecurrence Recurrence { get; set; }
    // Zero skips missed starts after a one-minute dispatch tolerance.
    public int CatchUpMinutes { get; set; } = 60;

    public void Validate()
    {
        if (!Enum.IsDefined(Recurrence) || LocalStart.Year < 2020 || CatchUpMinutes is < 0 or > 1440)
            throw new ArgumentException("Choose a valid date, recurrence and catch-up window (0–1440 minutes).");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }

    public DateTimeOffset First() { Validate(); return Resolve(LocalStart); }

    public DateTimeOffset? NextAfter(DateTimeOffset instant)
    {
        Validate();
        if (First() > instant) return First();
        if (Recurrence == TransferRecurrence.Once) return null;
        var days = Recurrence == TransferRecurrence.Daily ? 1 : 7;
        var localNow = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId)).DateTime;
        var n = Math.Max(0, (int)((localNow.Date - LocalStart.Date).TotalDays / days));
        var candidate = LocalStart.AddDays(n * days);
        while (Resolve(candidate) <= instant) candidate = candidate.AddDays(days);
        return Resolve(candidate);
    }

    public bool Expired(DateTimeOffset due, DateTimeOffset now) => now > due.AddMinutes(Math.Max(1, CatchUpMinutes));

    private DateTimeOffset Resolve(DateTime local)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Spring gap: first valid minute. Fall overlap: later occurrence, once only.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Min() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
