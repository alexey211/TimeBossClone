using TimeBossClone.Models;

namespace TimeBossClone.Services;

public readonly record struct TrackerState(
    bool IsMonitored,
    bool IsLocked,
    bool UnlockedForToday,
    int UsedSeconds,
    int LimitMinutes,
    int RemainingSeconds);

/// <summary>
/// Учёт экранного времени за день.
///
/// Состояние блокировки не хранится «флагом, который кто-то забыл сбросить», а вычисляется:
/// заблокировано = учётка под контролем И не «разблокировано на сегодня» И использовано ≥ лимита.
/// Поэтому добавление минут, увеличение лимита, смена дня и снятие контроля с учётки
/// корректно снимают блокировку, а уменьшение лимита — ставит.
/// Все события вызываются в UI-потоке (таймер WinForms).
/// </summary>
public sealed class TimeTracker : IDisposable
{
    private static readonly int[] WarningThresholds = { 10, 5 };
    private const long SaveIntervalMs = 30_000;
    private const long RequestCheckIntervalMs = 5_000;
    private const long MaxTickDeltaMs = 5_000; // защита от «скачка» после сна/зависания

    private readonly ConfigManager _config;
    private readonly System.Windows.Forms.Timer _timer;
    private UsageRecord _today;

    private long _lastTickMs;
    private long _carryMs;
    private long _sinceSaveMs;
    private long _sinceRequestCheckMs;
    private bool _dirty;
    private bool _sessionActive = true;
    private bool? _lastLocked;
    private bool _disposed;

    /// <summary>Предупреждение: аргумент — сколько минут осталось.</summary>
    public event Action<int>? Warning;
    public event Action? LimitReached;
    public event Action? Unlocked;
    public event Action<TrackerState>? Tick;

    public TimeTracker(ConfigManager config)
    {
        _config = config;
        _today = config.LoadUsage(DateTime.Today);
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += OnTimerTick;
    }

    public bool IsMonitored => _config.IsCurrentUserMonitored();
    public int UsedSeconds => _today.UsedSeconds;
    public bool UnlockedForToday => _today.UnlockedForToday;
    public int LimitMinutes => _config.GetLimitMinutes(DateTime.Today) + _today.ExtraMinutes;
    public int RemainingSeconds => Math.Max(0, LimitMinutes * 60 - _today.UsedSeconds);
    public bool IsLocked => IsMonitored && !_today.UnlockedForToday && _today.UsedSeconds >= LimitMinutes * 60;
    public bool SessionActive => _sessionActive;

    public TrackerState State => new(IsMonitored, IsLocked, UnlockedForToday, UsedSeconds, LimitMinutes, RemainingSeconds);

    public void Start()
    {
        _lastTickMs = Environment.TickCount64;
        Evaluate();          // сразу сообщит LimitReached или Unlocked
        Tick?.Invoke(State);
        _timer.Start();
    }

    /// <summary>
    /// Сессия заблокирована/переключена на другого пользователя — время не считаем.
    /// Раньше таймер тикал и при заблокированном экране, и при быстром переключении
    /// пользователей, съедая лимит ребёнка, пока за компьютером сидел родитель.
    /// </summary>
    public void SetSessionActive(bool active)
    {
        if (_sessionActive == active) return;
        _sessionActive = active;
        _carryMs = 0;
        if (!active) Flush();
    }

    public void AddExtraMinutes(int minutes)
    {
        if (minutes <= 0) return;
        _today.ExtraMinutes += minutes;
        SaveNow();
        Evaluate();
    }

    /// <summary>
    /// «Разблокировать на сегодня». Раньше просто сбрасывался IsLocked, и уже через
    /// секунду трекер видел used ≥ limit и блокировал компьютер снова.
    /// </summary>
    public void UnlockForToday()
    {
        _today.UnlockedForToday = true;
        SaveNow();
        Evaluate();
    }

    /// <summary>Пересчитать состояние после изменения настроек.</summary>
    public void Refresh()
    {
        Evaluate();
        Tick?.Invoke(State);
    }

    public void Flush()
    {
        if (!_dirty) return;
        _config.SaveUsage(_today);
        _dirty = false;
        _sinceSaveMs = 0;
    }

    private void SaveNow()
    {
        _dirty = true;
        Flush();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        long delta = Math.Clamp(now - _lastTickMs, 0, MaxTickDeltaMs);
        _lastTickMs = now;

        CheckDayRollover();

        _sinceRequestCheckMs += delta;
        if (_sinceRequestCheckMs >= RequestCheckIntervalMs)
        {
            _sinceRequestCheckMs = 0;
            ConsumeRequests();
        }

        if (_sessionActive && IsMonitored && !IsLocked)
        {
            // Считаем по реальному прошедшему времени, а не «+1 за тик»:
            // таймер WinForms пропускает тики, когда UI-поток занят.
            _carryMs += delta;
            int whole = (int)(_carryMs / 1000);
            if (whole > 0)
            {
                _carryMs -= whole * 1000L;
                _today.UsedSeconds += whole;
                _dirty = true;
            }
        }

        Evaluate();

        _sinceSaveMs += delta;
        if (_sinceSaveMs >= SaveIntervalMs) Flush();

        Tick?.Invoke(State);
    }

    private void CheckDayRollover()
    {
        var todayKey = ConfigManager.DateKey(DateTime.Today);
        if (_today.Date == todayKey) return;

        Flush(); // раньше хвост вчерашнего дня терялся
        _today = _config.LoadUsage(DateTime.Today);
        _carryMs = 0;
        AppLog.Info($"Новый день: {todayKey}");
        // Evaluate() в том же тике снимет вчерашнюю блокировку (политики, оверлей).
    }

    private void ConsumeRequests()
    {
        int added;
        try { added = RequestService.ConsumeAddedMinutes(_config, _today.ConsumedRequestIds); }
        catch (Exception ex) { AppLog.Error("Обработка запросов", ex); return; }
        if (added <= 0) return;

        _today.ExtraMinutes += added;
        SaveNow();
        Evaluate(); // снять блокировку сразу, не дожидаясь следующего тика
    }

    private void Evaluate()
    {
        bool locked = IsLocked;

        if (_today.IsLocked != locked)
        {
            _today.IsLocked = locked;
            SaveNow();
        }

        if (!locked) UpdateWarnings();

        if (_lastLocked == locked) return;
        _lastLocked = locked;

        if (locked)
        {
            AppLog.Info("Лимит исчерпан — блокировка");
            LimitReached?.Invoke();
        }
        else
        {
            Unlocked?.Invoke();
        }
    }

    /// <summary>
    /// Раньше предупреждение срабатывало только при точном совпадении секунды
    /// (легко проскочить), не звучало после добавления минут (флаги не сбрасывались)
    /// и не звучало, если программа запускалась, когда оставалось меньше 10 минут.
    /// </summary>
    private void UpdateWarnings()
    {
        if (!IsMonitored || _today.UnlockedForToday) return;

        int remaining = RemainingSeconds;

        // Время добавили — снова предупредим, когда будем подходить к лимиту.
        if (_today.WarnedAtMinutes.RemoveAll(w => remaining > w * 60) > 0) _dirty = true;

        bool fire = false;
        foreach (int w in WarningThresholds)
        {
            if (remaining <= w * 60 && !_today.WarnedAtMinutes.Contains(w))
            {
                _today.WarnedAtMinutes.Add(w);
                _dirty = true;
                fire = true; // если прошли сразу несколько порогов — одно предупреждение
            }
        }

        if (fire && remaining > 0)
            Warning?.Invoke((remaining + 59) / 60);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        Flush();
    }
}
