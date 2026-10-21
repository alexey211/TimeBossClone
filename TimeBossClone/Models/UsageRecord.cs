namespace TimeBossClone.Models;

public class UsageRecord
{
    public string Date { get; set; } = string.Empty;
    public int UsedSeconds { get; set; } = 0;

    /// <summary>Отражение текущего состояния (для статистики). Источник истины — TimeTracker.</summary>
    public bool IsLocked { get; set; } = false;

    public int ExtraMinutes { get; set; } = 0;

    /// <summary>Родитель нажал «Разблокировать на сегодня» — лимит до конца дня не действует.</summary>
    public bool UnlockedForToday { get; set; } = false;

    public List<int> WarnedAtMinutes { get; set; } = new();

    /// <summary>Id уже применённых запросов — защита от повторного начисления.</summary>
    public List<string> ConsumedRequestIds { get; set; } = new();
}
