using System.Text.RegularExpressions;

namespace GlassBogo.Core;

public sealed class CalendarService(Database db) : ICalendarService
{
    public event EventHandler? Changed { add => db.Changed += value; remove => db.Changed -= value; }
    public static readonly string[] Periods = ["08:00", "09:50", "13:45", "15:35"];
    public void Save(CalendarTask task) => db.Update(s => { var i = s.Tasks.FindIndex(t => t.Id == task.Id); if (i < 0) s.Tasks.Add(task); else s.Tasks[i] = task; });
    public void Remove(string id) => db.Update(s => s.Tasks.RemoveAll(t => t.Id == id));
    public IEnumerable<CalendarTask> Query(string? date = null, string query = "", string? categoryId = null, string status = "all")
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        return db.Current.Tasks.Where(t => (date is null || t.Date == date) && (categoryId is null || t.CategoryId == categoryId)
            && (query.Length == 0 || (t.Title + " " + t.Notes + " " + db.Current.TaskCategories.FirstOrDefault(c => c.Id == t.CategoryId)?.Name).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            && (status == "all" || status == "open" && !t.Completed || status == "done" && t.Completed || status == "high" && t.Priority == "high" || status == "overdue" && !t.Completed && string.CompareOrdinal(t.Date + " " + (t.Time == "" ? "23:59" : t.Time), now) < 0))
            .OrderBy(t => t.Date).ThenBy(t => t.Completed).ThenBy(t => t.Time == "" ? "99:99" : t.Time).ThenBy(t => t.Order);
    }
    public void Reorder(string id, string before)
    {
        if (id == before) return;
        db.Update(s =>
        {
            var source = s.Tasks.First(t => t.Id == id);
            var target = s.Tasks.First(t => t.Id == before);
            if (source.Date != target.Date || source.Time != "" || target.Time != "" || source.Completed || target.Completed)
                return;
            var ordered = s.Tasks.Where(t => t.Date == target.Date && t.Time == "" && !t.Completed).OrderBy(t => t.Order).ToList();
            ordered.RemoveAll(t => t.Id == id);
            ordered.Insert(ordered.FindIndex(t => t.Id == before), source);
            for (int i = 0; i < ordered.Count; i++)
            {
                var at = s.Tasks.FindIndex(t => t.Id == ordered[i].Id);
                s.Tasks[at] = ordered[i] with
                {
                    Order = i
                };
            }
        });
    }
    public void SaveCategory(TaskCategory category) => db.Update(s => { var i = s.TaskCategories.FindIndex(c => c.Id == category.Id); if (i < 0) s.TaskCategories.Add(category); else s.TaskCategories[i] = category; });
    public void RemoveCategory(string id) => db.Update(s => { s.TaskCategories.RemoveAll(c => c.Id == id); for (int i = 0; i < s.Tasks.Count; i++) if (s.Tasks[i].CategoryId == id) s.Tasks[i] = s.Tasks[i] with { CategoryId = null }; });
    public string Import(string path) => Migration.Calendar(db, path);
    public void Export(string path) => Database.AtomicWrite(path, System.Text.Json.JsonSerializer.Serialize(new
    {
        version = 1,
        tasks = db.Current.Tasks,
        categories = db.Current.TaskCategories
    }, DataJson.Options));
}
public static class QuickAdd
{
    private static int Number(string text)
    {
        if (int.TryParse(text, out var n))
            return n;
        var digits = "零一二三四五六七八九";
        if (text == "两")
            return 2;
        if (text == "十")
            return 10;
        if (text.Contains('十'))
        {
            var p = text.Split('十');
            return (p[0] == "" ? 1 : Number(p[0])) * 10 + (p[1] == "" ? 0 : Number(p[1]));
        }
        if (text.Length == 1 && digits.Contains(text))
            return digits.IndexOf(text, StringComparison.Ordinal);
        throw new FormatException("无法识别数字。");
    }
    public static CalendarTask Parse(string input, DateOnly today)
    {
        var text = input.Trim();
        var date = today;
        var time = "";
        var explicitDate = Regex.Match(text, @"(?<!\d)(\d{4})[-/](\d{1,2})[-/](\d{1,2})(?!\d)");
        if (explicitDate.Success)
        {
            date = new DateOnly(int.Parse(explicitDate.Groups[1].Value), int.Parse(explicitDate.Groups[2].Value), int.Parse(explicitDate.Groups[3].Value));
            text = text.Remove(explicitDate.Index, explicitDate.Length);
        }
        else
        {
            var relative = Regex.Match(text, "今天|明天|后天");
            if (relative.Success)
            {
                date = today.AddDays(relative.Value == "明天" ? 1 : relative.Value == "后天" ? 2 : 0);
                text = text.Remove(relative.Index, relative.Length);
            }
            else
            {
                var week = Regex.Match(text, @"(下周|本周|周)([一二三四五六日天])");
                if (week.Success)
                {
                    var day = "一二三四五六日".IndexOf(week.Groups[2].Value.Replace('天', '日'), StringComparison.Ordinal);
                    var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                    date = monday.AddDays(day + (week.Groups[1].Value == "下周" ? 7 : 0));
                    if (week.Groups[1].Value == "周" && date < today)
                        date = date.AddDays(7);
                    text = text.Remove(week.Index, week.Length);
                }
            }
        }
        var period = Regex.Match(text, @"第([一二三四1-4])节");
        if (period.Success)
        {
            time = CalendarService.Periods[Number(period.Groups[1].Value) - 1];
            text = text.Remove(period.Index, period.Length);
        }
        else
        {
            var clock = Regex.Match(text, @"(上午|下午|晚上|中午)?\s*([零一二三四五六七八九十两\d]+)(?:点|[:：])(?:(半)|([零一二三四五六七八九十\d]+)(?:分)?)?");
            if (clock.Success)
            {
                int hour = Number(clock.Groups[2].Value), minute = clock.Groups[3].Success ? 30 : clock.Groups[4].Success ? Number(clock.Groups[4].Value) : 0;
                if (clock.Groups[1].Value is "下午" or "晚上" or "中午" && hour < 12)
                    hour += 12;
                time = new TimeOnly(hour, minute).ToString("HH:mm");
                text = text.Remove(clock.Index, clock.Length);
            }
        }
        text = text.Trim();
        if (text.Length == 0)
            throw new FormatException("请输入任务内容。");
        return new(Guid.NewGuid().ToString("N"), text, date.ToString("yyyy-MM-dd"), time, Order: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
