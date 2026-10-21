using Microsoft.Win32;
using TimeBossClone.Services;

namespace TimeBossClone.Forms;

/// <summary>
/// Иконка в трее + принуждение к блокировке. Логика учёта времени — в TimeTracker,
/// здесь только реакция на его события и на события сессии Windows.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const int LockGraceMs = 2500;         // пауза после «Время вышло!» перед блокировкой
    private const int RelockAfterUnlockMs = 500;  // ребёнок разблокировал экран — блокируем снова
    private const int RelockRetryMs = 5000;       // страховка, если блокировка не сработала

    private readonly ConfigManager _config;
    private readonly TimeTracker _tracker;
    private readonly SpeechService _speech = new();
    private readonly NotifyIcon _trayIcon;
    private readonly TimerOverlayForm _overlay;
    private readonly System.Windows.Forms.Timer _lockTimer;
    private readonly SynchronizationContext _ui;

    private bool _sessionLocked;
    private bool _sessionDisconnected;
    private bool _dialogOpen;
    private bool _cleanedUp;

    public TrayApplicationContext(ConfigManager config)
    {
        _config = config;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _overlay = new TimerOverlayForm();
        _lockTimer = new System.Windows.Forms.Timer();
        _lockTimer.Tick += OnLockTimerTick;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Настройки (родитель)", null, OpenAdmin);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Завершить (родитель)", null, ExitApp);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "TimeBoss",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += OpenAdmin;

        _tracker = new TimeTracker(config);
        _tracker.Warning += OnWarning;
        _tracker.LimitReached += OnLimitReached;
        _tracker.Unlocked += OnUnlocked;
        _tracker.Tick += OnTick;

        _config.Changed += OnConfigChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.SessionEnding += OnSessionEnding;

        // Start() сразу вызовет LimitReached или Unlocked — политики реестра
        // придут в корректное состояние (раньше после вчерашней блокировки
        // Диспетчер задач мог остаться заблокированным навсегда).
        _tracker.Start();
    }

    // ---------------- События трекера ----------------

    private void OnWarning(int minutesLeft)
    {
        _speech.SayWarning(minutesLeft);
        _trayIcon.ShowBalloonTip(6000, "TimeBoss",
            $"Осталось {minutesLeft} {Plural.Minutes(minutesLeft)}!", ToolTipIcon.Warning);
    }

    private void OnLimitReached()
    {
        _trayIcon.ShowBalloonTip(3000, "TimeBoss", "Время вышло!", ToolTipIcon.Error);
        _overlay.SetVisible(false);
        ApplyPolicies();
        // Блокировка через таймер, а не Task.Delay: её можно отменить,
        // если родитель успел добавить время/разблокировать.
        ScheduleLock(LockGraceMs);
    }

    private void OnUnlocked()
    {
        _lockTimer.Stop();
        SystemPolicyService.RemoveLockPolicies();
    }

    private void OnTick(TrackerState state)
    {
        string tip;
        if (!state.IsMonitored) tip = "TimeBoss — для этой учётной записи ограничений нет";
        else if (state.UnlockedForToday) tip = "TimeBoss — разблокировано на сегодня";
        else if (state.IsLocked) tip = "TimeBoss — время вышло";
        else tip = $"TimeBoss — осталось {(state.RemainingSeconds + 59) / 60} мин";
        _trayIcon.Text = tip.Length > 63 ? tip[..63] : tip;

        // Видимость оверлея вычисляется каждый тик — изменение списка контролируемых
        // учёток применяется сразу, без перезапуска программы.
        bool showOverlay = state.IsMonitored && !state.IsLocked && !state.UnlockedForToday;
        if (showOverlay) _overlay.UpdateRemaining(state.RemainingSeconds);
        _overlay.SetVisible(showOverlay);

        // Страховка: лимит исчерпан, сессия активна, а блокировка не запланирована
        // (например, пропущено событие SessionUnlock).
        if (state.IsLocked && _tracker.SessionActive && !_lockTimer.Enabled)
            ScheduleLock(RelockAfterUnlockMs);
    }

    // ---------------- Принуждение к блокировке ----------------

    private void ScheduleLock(int delayMs)
    {
        _lockTimer.Stop();
        _lockTimer.Interval = delayMs;
        _lockTimer.Start();
    }

    private void OnLockTimerTick(object? sender, EventArgs e)
    {
        _lockTimer.Stop();
        if (!_tracker.IsLocked || !_tracker.SessionActive) return;

        LockService.Lock();
        ScheduleLock(RelockRetryMs);
    }

    private void ApplyPolicies() =>
        SystemPolicyService.ApplyLockPolicies(_config.Config.BlockTaskManager, _config.Config.BlockRegistryEditor);

    private void OnConfigChanged()
    {
        // Поменяли галочки «Безопасность» во время блокировки — применяем сразу.
        if (_tracker.IsLocked) ApplyPolicies();
        _tracker.Refresh();
    }

    // ---------------- События сессии Windows ----------------

    // SystemEvents могут приходить в отдельном потоке — переносим в UI-поток.
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) =>
        _ui.Post(_ => HandleSessionSwitch(e.Reason), null);

    private void HandleSessionSwitch(SessionSwitchReason reason)
    {
        if (_cleanedUp) return;

        switch (reason)
        {
            case SessionSwitchReason.SessionLock: _sessionLocked = true; break;
            case SessionSwitchReason.SessionUnlock: _sessionLocked = false; break;
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect: _sessionDisconnected = true; break;
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect: _sessionDisconnected = false; break;
            default: return;
        }

        _tracker.SetSessionActive(!_sessionLocked && !_sessionDisconnected);

        if (reason == SessionSwitchReason.SessionUnlock && _tracker.IsLocked)
        {
            ApplyPolicies();
            ScheduleLock(RelockAfterUnlockMs);
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        // Выход из системы/выключение — сохраняем накопленное (раньше терялось до 30 с).
        try { _ui.Send(_ => _tracker.Flush(), null); }
        catch (Exception ex) { AppLog.Error("Сохранение при завершении сеанса", ex); }
    }

    // ---------------- Меню ----------------

    private string? AskParentPassword()
    {
        using var login = new LoginForm(LoginMode.Login, _config);
        return login.ShowDialog() == DialogResult.OK ? login.Password : null;
    }

    private void OpenAdmin(object? sender, EventArgs e)
    {
        // Защита от повторного открытия (двойной клик по иконке при открытом окне).
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var password = AskParentPassword();
            if (password == null) return;
            using var admin = new AdminForm(_config, _tracker, password);
            admin.ShowDialog();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void ExitApp(object? sender, EventArgs e)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            if (AskParentPassword() == null) return;
        }
        finally
        {
            _dialogOpen = false;
        }

        AppLog.Info("Завершение родителем");
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        Cleanup();
        base.ExitThreadCore();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _config.Changed -= OnConfigChanged;

        _lockTimer.Stop();
        _lockTimer.Dispose();
        _tracker.Dispose(); // сохраняет статистику
        SystemPolicyService.RemoveLockPolicies();

        _overlay.Close();
        _overlay.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _speech.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Cleanup();
        base.Dispose(disposing);
    }
}
