using CitusManager.Domain;

namespace CitusManager.Services;

public sealed record BackupSchedule(
    BackupScheduleUnit Unit,
    int Interval,
    int MinuteOfHour,
    TimeOnly RunAtLocalTime,
    DayOfWeek RunOnDayOfWeek,
    int RunOnDayOfMonth,
    string TimeZoneId);

public sealed record BackupRetentionCandidate(
    Guid Id,
    DateTimeOffset CreatedAt,
    bool IsPinned,
    bool HasActiveRestore);

public static class BackupScheduleCalculator
{
    private static readonly DateTime HourAndDayAnchor = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime WeekAnchor = new(1970, 1, 5, 0, 0, 0, DateTimeKind.Unspecified);

    public static DateTimeOffset CalculateNext(
        BackupSchedule schedule,
        DateTimeOffset afterUtc,
        DateTimeOffset? lastScheduledUtc = null)
    {
        Validate(schedule);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var threshold = lastScheduledUtc is { } last && last > afterUtc ? last : afterUtc;
        var localAfter = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(threshold, zone).DateTime, DateTimeKind.Unspecified);
        var candidate = FirstLocalCandidate(schedule, localAfter);

        // Bounded only as a defensive guard. A valid IANA zone cannot invalidate every future slot.
        for (var i = 0; i < 10_000; i++)
        {
            var utc = ConvertStableLocalSlot(candidate, zone);
            if (utc is { } instant && instant > threshold)
                return instant;

            candidate = AddInterval(candidate, schedule);
        }

        throw new InvalidOperationException("Could not find a valid future backup schedule slot.");
    }

    public static DateTimeOffset CalculateNext(
        ClusterBackupPolicy policy,
        DateTimeOffset afterUtc,
        DateTimeOffset? lastScheduledUtc = null) =>
        CalculateNext(new BackupSchedule(
            policy.ScheduleUnit,
            policy.ScheduleInterval,
            policy.MinuteOfHour,
            policy.RunAtLocalTime,
            policy.RunOnDayOfWeek,
            policy.RunOnDayOfMonth,
            policy.TimeZoneId), afterUtc, lastScheduledUtc);

    private static DateTime FirstLocalCandidate(BackupSchedule schedule, DateTime localAfter)
    {
        return schedule.Unit switch
        {
            BackupScheduleUnit.Hour => FirstHourly(schedule, localAfter),
            BackupScheduleUnit.Day => FirstDaily(schedule, localAfter),
            BackupScheduleUnit.Week => FirstWeekly(schedule, localAfter),
            BackupScheduleUnit.Month => FirstMonthly(schedule, localAfter),
            _ => throw new ArgumentOutOfRangeException(nameof(schedule.Unit))
        };
    }

    private static DateTime FirstHourly(BackupSchedule schedule, DateTime localAfter)
    {
        var elapsedHours = (long)Math.Floor((localAfter - HourAndDayAnchor).TotalHours);
        var alignedHours = elapsedHours - Mod(elapsedHours, schedule.Interval);
        var candidate = HourAndDayAnchor.AddHours(alignedHours).AddMinutes(schedule.MinuteOfHour);
        return candidate <= localAfter ? candidate.AddHours(schedule.Interval) : candidate;
    }

    private static DateTime FirstDaily(BackupSchedule schedule, DateTime localAfter)
    {
        var elapsedDays = (long)Math.Floor((localAfter.Date - HourAndDayAnchor).TotalDays);
        var alignedDays = elapsedDays - Mod(elapsedDays, schedule.Interval);
        var candidate = HourAndDayAnchor.AddDays(alignedDays).Date + schedule.RunAtLocalTime.ToTimeSpan();
        return candidate <= localAfter ? candidate.AddDays(schedule.Interval) : candidate;
    }

    private static DateTime FirstWeekly(BackupSchedule schedule, DateTime localAfter)
    {
        var elapsedWeeks = (long)Math.Floor((localAfter.Date - WeekAnchor).TotalDays / 7d);
        var alignedWeeks = elapsedWeeks - Mod(elapsedWeeks, schedule.Interval);
        var weekdayOffset = Mod((int)schedule.RunOnDayOfWeek - (int)DayOfWeek.Monday, 7);
        var candidate = WeekAnchor.AddDays(alignedWeeks * 7 + weekdayOffset).Date + schedule.RunAtLocalTime.ToTimeSpan();
        return candidate <= localAfter ? candidate.AddDays(schedule.Interval * 7) : candidate;
    }

    private static DateTime FirstMonthly(BackupSchedule schedule, DateTime localAfter)
    {
        var elapsedMonths = (localAfter.Year - 1970) * 12L + localAfter.Month - 1;
        var alignedMonths = elapsedMonths - Mod(elapsedMonths, schedule.Interval);
        var monthStart = HourAndDayAnchor.AddMonths(checked((int)alignedMonths));
        var candidate = InMonth(monthStart, schedule.RunOnDayOfMonth, schedule.RunAtLocalTime);
        if (candidate <= localAfter)
            candidate = InMonth(monthStart.AddMonths(schedule.Interval), schedule.RunOnDayOfMonth, schedule.RunAtLocalTime);
        return candidate;
    }

    private static DateTime AddInterval(DateTime candidate, BackupSchedule schedule) => schedule.Unit switch
    {
        BackupScheduleUnit.Hour => candidate.AddHours(schedule.Interval),
        BackupScheduleUnit.Day => candidate.AddDays(schedule.Interval),
        BackupScheduleUnit.Week => candidate.AddDays(schedule.Interval * 7),
        BackupScheduleUnit.Month => InMonth(
            new DateTime(candidate.Year, candidate.Month, 1).AddMonths(schedule.Interval),
            schedule.RunOnDayOfMonth,
            schedule.RunAtLocalTime),
        _ => throw new ArgumentOutOfRangeException(nameof(schedule.Unit))
    };

    private static DateTime InMonth(DateTime monthStart, int requestedDay, TimeOnly time)
    {
        var day = Math.Min(requestedDay, DateTime.DaysInMonth(monthStart.Year, monthStart.Month));
        return new DateTime(monthStart.Year, monthStart.Month, day) + time.ToTimeSpan();
    }

    private static DateTimeOffset? ConvertStableLocalSlot(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
            return null;

        if (!zone.IsAmbiguousTime(local))
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);

        // The earliest UTC occurrence is the canonical occurrence; never schedule both repeated wall-clock instants.
        return zone.GetAmbiguousTimeOffsets(local)
            .Select(offset => new DateTimeOffset(local, offset).ToUniversalTime())
            .Min();
    }

    private static void Validate(BackupSchedule schedule)
    {
        if (schedule.Interval is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(schedule.Interval));
        if (schedule.MinuteOfHour is < 0 or > 59)
            throw new ArgumentOutOfRangeException(nameof(schedule.MinuteOfHour));
        if (schedule.RunOnDayOfMonth is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(schedule.RunOnDayOfMonth));
        if (string.IsNullOrWhiteSpace(schedule.TimeZoneId))
            throw new ArgumentException("A time zone is required.", nameof(schedule.TimeZoneId));
    }

    private static long Mod(long value, long modulus) => (value % modulus + modulus) % modulus;
    private static int Mod(int value, int modulus) => (value % modulus + modulus) % modulus;
}

public static class BackupRetentionSelector
{
    public static IReadOnlyList<Guid> SelectForDeletion(
        IEnumerable<BackupRetentionCandidate> successfulBackups,
        DateTimeOffset nowUtc,
        int maxAgeDays,
        int minimumBackups,
        int maximumBackups)
    {
        if (maxAgeDays < 1) throw new ArgumentOutOfRangeException(nameof(maxAgeDays));
        if (minimumBackups < 0) throw new ArgumentOutOfRangeException(nameof(minimumBackups));
        if (maximumBackups < 1 || maximumBackups < minimumBackups)
            throw new ArgumentOutOfRangeException(nameof(maximumBackups));

        var ordered = successfulBackups.OrderByDescending(x => x.CreatedAt).ToArray();
        var cutoff = nowUtc.AddDays(-maxAgeDays);

        return ordered
            .Select((backup, index) => (backup, index))
            .Where(x => x.index >= minimumBackups)
            .Where(x => !x.backup.IsPinned && !x.backup.HasActiveRestore)
            .Where(x => x.backup.CreatedAt < cutoff || x.index >= maximumBackups)
            .Select(x => x.backup.Id)
            .ToArray();
    }
}
