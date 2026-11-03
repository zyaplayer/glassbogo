namespace GlassBogo.Core;

public sealed record LessonSlot(int Index, string Label, string Start, string End);

public static class Timetable
{
    public static IReadOnlyList<LessonSlot> Slots { get; } = Array.AsReadOnly(new[]
    {
        new LessonSlot(0, "第1节", "08:00", "09:35"), new LessonSlot(1, "第2节", "09:50", "11:25"),
        new LessonSlot(2, "第3节", "13:45", "15:20"), new LessonSlot(3, "第4节", "15:35", "17:10")
    });
    public static int? PeriodForTime(string time)
    {
        if (!TimeOnly.TryParseExact(time, "HH:mm", out _)) return null;
        return Slots.FirstOrDefault(slot => string.CompareOrdinal(time, slot.Start) >= 0 && string.CompareOrdinal(time, slot.End) < 0)?.Index;
    }
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
    public static IReadOnlyList<DateOnly> WeekDays(DateOnly date)
    {
        var start = WeekStart(date);
        return Enumerable.Range(0, 7).Where(i => start.DayNumber + i <= DateOnly.MaxValue.DayNumber).Select(start.AddDays).ToArray();
    }
    public static DateOnly ShiftWeek(DateOnly date, int direction) => DateOnly.FromDayNumber((int)Math.Clamp((long)date.DayNumber + direction * 7L, DateOnly.MinValue.DayNumber, DateOnly.MaxValue.DayNumber));
}
