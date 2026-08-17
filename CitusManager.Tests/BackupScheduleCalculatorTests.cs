using CitusManager.Domain;
using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class BackupScheduleCalculatorTests
{
    [Fact]
    public void Hourly_UsesIntervalAndSelectedMinute()
    {
        var schedule = Schedule(BackupScheduleUnit.Hour, interval: 3, minute: 15);

        var next = BackupScheduleCalculator.CalculateNext(schedule, Utc(2026, 1, 1, 4, 20));

        Assert.Equal(Utc(2026, 1, 1, 6, 15), next);
    }

    [Fact]
    public void Daily_UsesLocalTimeZone()
    {
        var schedule = Schedule(BackupScheduleUnit.Day, time: new TimeOnly(2, 30), zone: "Asia/Ho_Chi_Minh");

        var next = BackupScheduleCalculator.CalculateNext(schedule, Utc(2026, 1, 1, 18, 0));

        Assert.Equal(Utc(2026, 1, 1, 19, 30), next);
    }

    [Fact]
    public void Weekly_UsesSelectedWeekday()
    {
        var schedule = Schedule(BackupScheduleUnit.Week, time: new TimeOnly(9, 0), weekday: DayOfWeek.Monday);

        var next = BackupScheduleCalculator.CalculateNext(schedule, Utc(2026, 1, 6));

        Assert.Equal(Utc(2026, 1, 12, 9), next);
    }

    [Fact]
    public void Monthly_ClampsToLastDay()
    {
        var schedule = Schedule(BackupScheduleUnit.Month, time: new TimeOnly(5, 0), monthDay: 31);

        var next = BackupScheduleCalculator.CalculateNext(schedule, Utc(2026, 2, 1));

        Assert.Equal(Utc(2026, 2, 28, 5), next);
    }

    [Fact]
    public void DstGap_SkipsInvalidSlot()
    {
        var schedule = Schedule(BackupScheduleUnit.Day, time: new TimeOnly(2, 30), zone: "America/New_York");

        var next = BackupScheduleCalculator.CalculateNext(schedule, Utc(2026, 3, 8, 0));

        Assert.Equal(Utc(2026, 3, 9, 6, 30), next);
    }

    [Fact]
    public void DstOverlap_ReturnsOnlyCanonicalFirstOccurrence()
    {
        var schedule = Schedule(BackupScheduleUnit.Day, time: new TimeOnly(1, 30), zone: "America/New_York");
        var first = BackupScheduleCalculator.CalculateNext(schedule, Utc(2026, 11, 1, 0));

        var next = BackupScheduleCalculator.CalculateNext(schedule, first, first);

        Assert.Equal(Utc(2026, 11, 1, 5, 30), first);
        Assert.Equal(Utc(2026, 11, 2, 6, 30), next);
    }

    private static BackupSchedule Schedule(
        BackupScheduleUnit unit,
        int interval = 1,
        int minute = 0,
        TimeOnly? time = null,
        DayOfWeek weekday = DayOfWeek.Sunday,
        int monthDay = 1,
        string zone = "UTC") =>
        new(unit, interval, minute, time ?? new TimeOnly(0, 0), weekday, monthDay, zone);

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}

public sealed class BackupRetentionSelectorTests
{
    [Fact]
    public void KeepsMinimumNewestAndDeletesOldBackups()
    {
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        var backups = Enumerable.Range(0, 6)
            .Select(i => Candidate(now.AddDays(-(i * 20))))
            .ToArray();

        var deleted = BackupRetentionSelector.SelectForDeletion(backups, now, 30, 3, 30);

        Assert.Equal(backups.Skip(3).Select(x => x.Id), deleted);
    }

    [Fact]
    public void EnforcesMaximumButNeverDeletesPinnedOrActiveRestore()
    {
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        var backups = Enumerable.Range(0, 8)
            .Select(i => Candidate(now.AddDays(-i), pinned: i == 6, active: i == 7))
            .ToArray();

        var deleted = BackupRetentionSelector.SelectForDeletion(backups, now, 30, 3, 5);

        Assert.Equal(backups.Skip(5).Take(1).Select(x => x.Id), deleted);
    }

    private static BackupRetentionCandidate Candidate(DateTimeOffset created, bool pinned = false, bool active = false) =>
        new(Guid.NewGuid(), created, pinned, active);
}
