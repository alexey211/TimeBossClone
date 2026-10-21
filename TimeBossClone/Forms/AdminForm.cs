using TimeBossClone.Services;

namespace TimeBossClone.Forms;

public class AdminForm : Form
{
    private readonly ConfigManager _config;
    private readonly TimeTracker _tracker;
    private string _parentPassword; // только в памяти, на время открытого окна — для подписи запросов

    private NumericUpDown _weekdayHours = null!;
    private NumericUpDown _weekdayMins = null!;
    private NumericUpDown _weekendHours = null!;
    private NumericUpDown _weekendMins = null!;
    private NumericUpDown _extraMins = null!;
    private Label _statsLabel = null!;
    private Button _autostartBtn = null!;
    private CheckedListBox _accountsList = null!;
    private CheckBox _blockTaskMgrCheck = null!;
    private CheckBox _blockRegistryCheck = null!;

    private readonly System.Windows.Forms.Timer _refreshTimer;

    public AdminForm(ConfigManager config, TimeTracker tracker, string parentPassword)
    {
        _config = config;
        _tracker = tracker;
        _parentPassword = parentPassword;
        BuildUI();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshStats();
        _refreshTimer.Start();
    }

    private void BuildUI()
    {
        Text = "TimeBoss — Панель управления";
        Size = new Size(456, 578);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        BackColor = Color.WhiteSmoke;

        var tabs = new TabControl
        {
            Location = new Point(10, 10),
            Size = new Size(420, 400),
            Font = new Font("Segoe UI", 9f)
        };
        tabs.TabPages.Add(BuildTimeTab());
        tabs.TabPages.Add(BuildAccountsTab());
        tabs.TabPages.Add(BuildSecurityTab());
        tabs.TabPages.Add(BuildOtherUsersTab());
        Controls.Add(tabs);

        // Save button
        var btnSave = new Button
        {
            Text = "Сохранить настройки",
            Location = new Point(10, 418),
            Size = new Size(200, 36),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += SaveAll;
        Controls.Add(btnSave);

        var btnPwd = new Button
        {
            Text = "Сменить пароль",
            Location = new Point(220, 418),
            Size = new Size(210, 36)
        };
        btnPwd.Click += ChangePassword;
        Controls.Add(btnPwd);

        var btnUnlock = new Button
        {
            Text = "Разблокировать на сегодня",
            Location = new Point(10, 462),
            Size = new Size(420, 36),
            BackColor = Color.FromArgb(16, 124, 16),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f)
        };
        btnUnlock.FlatAppearance.BorderSize = 0;
        btnUnlock.Click += (_, _) =>
        {
            _tracker.UnlockForToday();
            RefreshStats();
        };
        Controls.Add(btnUnlock);

        _autostartBtn = new Button
        {
            Location = new Point(10, 506),
            Size = new Size(420, 36),
            FlatStyle = FlatStyle.Flat
        };
        _autostartBtn.Click += (_, _) =>
        {
            try { AutostartService.Toggle(); }
            catch (Exception ex)
            {
                AppLog.Error("Автозапуск", ex);
                MessageBox.Show($"Не удалось изменить автозапуск: {ex.Message}", "TimeBoss",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateAutostartButton();
        };
        UpdateAutostartButton();
        Controls.Add(_autostartBtn);

        RefreshStats();
    }

    private TabPage BuildTimeTab()
    {
        var page = new TabPage("Время");

        var grpStats = new GroupBox
        {
            Text = "Статистика на сегодня",
            Location = new Point(8, 8),
            Size = new Size(394, 82),
            Font = new Font("Segoe UI", 9f)
        };
        _statsLabel = new Label
        {
            Location = new Point(10, 20),
            Size = new Size(372, 54),
            Font = new Font("Segoe UI", 10f)
        };
        grpStats.Controls.Add(_statsLabel);
        page.Controls.Add(grpStats);

        var grpLimits = new GroupBox
        {
            Text = "Лимиты времени",
            Location = new Point(8, 97),
            Size = new Size(394, 112),
            Font = new Font("Segoe UI", 9f)
        };
        AddLimitRow(grpLimits, "Будни (Пн–Пт):", 20,
            _config.Config.WeekdayLimitMinutes, out _weekdayHours, out _weekdayMins);
        AddLimitRow(grpLimits, "Выходные (Сб–Вс):", 60,
            _config.Config.WeekendLimitMinutes, out _weekendHours, out _weekendMins);
        page.Controls.Add(grpLimits);

        var grpExtra = new GroupBox
        {
            Text = "Дополнительное время на сегодня",
            Location = new Point(8, 216),
            Size = new Size(394, 62),
            Font = new Font("Segoe UI", 9f)
        };
        grpExtra.Controls.Add(new Label { Text = "Добавить минут:", Location = new Point(12, 26), AutoSize = true });
        _extraMins = new NumericUpDown { Minimum = 1, Maximum = 480, Value = 15, Location = new Point(130, 23), Width = 70 };
        grpExtra.Controls.Add(_extraMins);
        var btnAdd = new Button { Text = "Добавить", Location = new Point(218, 22), Width = 100, Height = 26 };
        btnAdd.Click += AddExtraTime;
        grpExtra.Controls.Add(btnAdd);
        page.Controls.Add(grpExtra);

        return page;
    }

    private TabPage BuildAccountsTab()
    {
        var page = new TabPage("Учётные записи");

        page.Controls.Add(new Label
        {
            Text = "Блокировка действует только для отмеченных учётных записей.\nЕсли ни одна не выбрана — ограничения применяются ко всем.",
            Location = new Point(8, 10),
            Size = new Size(394, 38),
            Font = new Font("Segoe UI", 8.5f)
        });

        _accountsList = new CheckedListBox
        {
            Location = new Point(8, 54),
            Size = new Size(394, 280),
            CheckOnClick = true,
            Font = new Font("Segoe UI", 10.5f),
            BorderStyle = BorderStyle.FixedSingle
        };

        var accounts = SystemPolicyService.GetLocalUserAccounts();
        var monitored = _config.Config.MonitoredAccounts;
        foreach (var acc in accounts)
        {
            bool isChecked = monitored.Count == 0
                || monitored.Contains(acc, StringComparer.OrdinalIgnoreCase);
            _accountsList.Items.Add(acc, isChecked);
        }
        page.Controls.Add(_accountsList);

        page.Controls.Add(new Label
        {
            Text = $"Текущий пользователь: {Environment.UserName}",
            Location = new Point(8, 342),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.Gray
        });

        return page;
    }

    private TabPage BuildSecurityTab()
    {
        var page = new TabPage("Безопасность");

        page.Controls.Add(new Label
        {
            Text = "Системные инструменты, блокируемые при достижении лимита:",
            Location = new Point(8, 14),
            Size = new Size(394, 22),
            Font = new Font("Segoe UI", 9f)
        });

        _blockTaskMgrCheck = new CheckBox
        {
            Text = "Блокировать Диспетчер задач (Ctrl+Shift+Esc)",
            Location = new Point(16, 48),
            Size = new Size(380, 28),
            Checked = _config.Config.BlockTaskManager,
            Font = new Font("Segoe UI", 9.5f)
        };
        page.Controls.Add(_blockTaskMgrCheck);

        _blockRegistryCheck = new CheckBox
        {
            Text = "Блокировать Редактор реестра (regedit)",
            Location = new Point(16, 84),
            Size = new Size(380, 28),
            Checked = _config.Config.BlockRegistryEditor,
            Font = new Font("Segoe UI", 9.5f)
        };
        page.Controls.Add(_blockRegistryCheck);

        page.Controls.Add(new Label
        {
            Text = "Блокировка применяется через политики реестра HKCU.\n" +
                   "При разблокировке ограничения снимаются автоматически.",
            Location = new Point(8, 126),
            Size = new Size(394, 40),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.Gray
        });

        return page;
    }

    private TabPage BuildOtherUsersTab()
    {
        var page = new TabPage("Другой польз.");

        page.Controls.Add(new Label
        {
            Text = "Добавить время другому пользователю",
            Location = new Point(8, 12),
            Size = new Size(394, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        });

        page.Controls.Add(new Label
        {
            Text = "Пользователь:",
            Location = new Point(8, 50),
            AutoSize = true
        });

        var userCombo = new ComboBox
        {
            Location = new Point(130, 47),
            Size = new Size(200, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        var otherAccounts = SystemPolicyService.GetLocalUserAccounts()
            .Where(a => !a.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        userCombo.Items.AddRange(otherAccounts.ToArray<object>());
        if (userCombo.Items.Count > 0) userCombo.SelectedIndex = 0;
        page.Controls.Add(userCombo);

        page.Controls.Add(new Label
        {
            Text = "Минут:",
            Location = new Point(8, 90),
            AutoSize = true
        });

        var minsUpDown = new NumericUpDown
        {
            Location = new Point(130, 87),
            Width = 80,
            Minimum = 1,
            Maximum = RequestService.MaxMinutesPerRequest,
            Value = 30
        };
        page.Controls.Add(minsUpDown);

        var statusLabel = new Label
        {
            Location = new Point(8, 168),
            Size = new Size(394, 80),
            Font = new Font("Segoe UI", 9f)
        };
        page.Controls.Add(statusLabel);

        var btnSend = new Button
        {
            Text = "Отправить запрос на добавление времени",
            Location = new Point(8, 128),
            Size = new Size(394, 32),
            BackColor = Color.FromArgb(16, 124, 16),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f),
            Enabled = userCombo.Items.Count > 0
        };
        btnSend.FlatAppearance.BorderSize = 0;

        // Ожидание ответа получателя (.ack). Получатель проверяет папку каждые 5 с.
        const int ackTimeoutSec = 30;
        string? pendingId = null;
        string pendingUser = string.Empty;
        int pendingMinutes = 0;
        DateTime pendingSince = DateTime.MinValue;
        var ackTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        FormClosed += (_, _) => { ackTimer.Stop(); ackTimer.Dispose(); };

        void Finish(Color color, string text)
        {
            ackTimer.Stop();
            pendingId = null;
            btnSend.Enabled = userCombo.Items.Count > 0;
            statusLabel.ForeColor = color;
            statusLabel.Text = text;
        }

        ackTimer.Tick += (_, _) =>
        {
            if (pendingId == null) { ackTimer.Stop(); return; }

            var ack = RequestService.TryReadAck(pendingId);
            if (ack != null)
            {
                Finish(ack.Applied ? Color.DarkGreen : Color.Crimson,
                    ack.Applied ? $"✔ {ack.Message}" : $"✖ TimeBoss у {pendingUser} отклонил запрос: {ack.Message}");
                return;
            }

            int waited = (int)(DateTime.Now - pendingSince).TotalSeconds;
            if (waited < ackTimeoutSec)
            {
                statusLabel.Text = $"Запрос +{pendingMinutes} мин для {pendingUser} отправлен, ждём ответа… {waited} с";
                return;
            }

            // Не забрали — у адресата TimeBoss не запущен (или старая версия).
            // Оставляем запрос: он применится, когда TimeBoss там запустится (сегодня).
            Finish(Color.DarkOrange,
                $"{pendingUser} не ответил за {ackTimeoutSec} с: TimeBoss в его учётке не запущен или устаревшей версии.\n" +
                "Запрос сохранён и применится при запуске TimeBoss сегодня.");
        };

        btnSend.Click += (_, _) =>
        {
            if (userCombo.SelectedItem is not string targetUser)
            {
                statusLabel.ForeColor = Color.Crimson;
                statusLabel.Text = "Выберите пользователя.";
                return;
            }
            int minutes = (int)minsUpDown.Value;
            try
            {
                UseWaitCursor = true;
                btnSend.Enabled = false;
                pendingId = RequestService.SendAddTime(targetUser, minutes, _parentPassword);
                pendingUser = targetUser;
                pendingMinutes = minutes;
                pendingSince = DateTime.Now;
                statusLabel.ForeColor = Color.DimGray;
                statusLabel.Text = $"Запрос +{minutes} мин для {targetUser} отправлен, ждём ответа…";
                ackTimer.Start();
            }
            catch (Exception ex)
            {
                AppLog.Error("Отправка запроса", ex);
                Finish(Color.Crimson, $"Ошибка отправки: {ex.Message}");
            }
            finally
            {
                UseWaitCursor = false;
            }
        };
        page.Controls.Add(btnSend);

        if (userCombo.Items.Count == 0)
        {
            statusLabel.ForeColor = Color.Gray;
            statusLabel.Text = "Других пользователей на этом компьютере не найдено.";
        }

        return page;
    }

    private static void AddLimitRow(GroupBox parent, string text, int y,
        int totalMinutes, out NumericUpDown hours, out NumericUpDown mins)
    {
        parent.Controls.Add(new Label { Text = text, Location = new Point(12, y + 4), AutoSize = true });

        hours = new NumericUpDown
        {
            Minimum = 0, Maximum = 23,
            Value = totalMinutes / 60,
            Location = new Point(180, y), Width = 58
        };
        parent.Controls.Add(hours);
        parent.Controls.Add(new Label { Text = "ч", Location = new Point(242, y + 4), AutoSize = true });

        mins = new NumericUpDown
        {
            Minimum = 0, Maximum = 59,
            Value = totalMinutes % 60,
            Location = new Point(260, y), Width = 58
        };
        parent.Controls.Add(mins);
        parent.Controls.Add(new Label { Text = "м", Location = new Point(322, y + 4), AutoSize = true });
    }

    private void RefreshStats()
    {
        var st = _tracker.State;
        int limit = st.LimitMinutes;
        int usedMin = st.UsedSeconds / 60;
        int remaining = (st.RemainingSeconds + 59) / 60;
        string status = !st.IsMonitored ? "  (учётка не контролируется)"
            : st.UnlockedForToday ? "  🔓 РАЗБЛОКИРОВАНО НА СЕГОДНЯ"
            : st.IsLocked ? "  ⛔ ЗАБЛОКИРОВАНО"
            : string.Empty;

        _statsLabel.Text =
            $"Использовано: {usedMin / 60} ч {usedMin % 60} мин{status}\n" +
            $"Лимит:  {limit / 60} ч {limit % 60} мин       " +
            $"Осталось: {remaining / 60} ч {remaining % 60} мин";
    }

    private void SaveAll(object? sender, EventArgs e)
    {
        _config.Config.WeekdayLimitMinutes = (int)_weekdayHours.Value * 60 + (int)_weekdayMins.Value;
        _config.Config.WeekendLimitMinutes = (int)_weekendHours.Value * 60 + (int)_weekendMins.Value;
        _config.Config.BlockTaskManager = _blockTaskMgrCheck.Checked;
        _config.Config.BlockRegistryEditor = _blockRegistryCheck.Checked;

        var selected = _accountsList.CheckedItems.Cast<string>().ToList();
        var all = _accountsList.Items.Cast<string>().ToList();
        _config.Config.MonitoredAccounts = selected.Count == all.Count
            ? new List<string>()
            : selected;

        // Save() поднимает ConfigManager.Changed: трекер пересчитает блокировку,
        // трей переприменит политики — отдельно дергать их отсюда не нужно.
        _config.Save();
        RefreshStats();

        MessageBox.Show("Настройки сохранены.", "TimeBoss", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void AddExtraTime(object? sender, EventArgs e)
    {
        int extra = (int)_extraMins.Value;
        _tracker.AddExtraMinutes(extra); // раньше при активной блокировке минуты добавлялись, но блок не снимался
        RefreshStats();
        MessageBox.Show($"Добавлено {extra} мин на сегодня.", "TimeBoss",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ChangePassword(object? sender, EventArgs e)
    {
        using var form = new LoginForm(LoginMode.ChangePassword, _config);
        if (form.ShowDialog() != DialogResult.OK) return;
        _parentPassword = form.Password;
        MessageBox.Show("Пароль изменён. Не забудьте сменить его и в TimeBoss других учётных записей — " +
                            "запросы на добавление времени работают только при одинаковом пароле.",
                "TimeBoss", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UpdateAutostartButton()
    {
        bool enabled = AutostartService.IsEnabled();
        _autostartBtn.Text = enabled
            ? "Автозапуск: ВКЛ  (нажмите чтобы отключить)"
            : "Автозапуск: ВЫКЛ  (нажмите чтобы включить)";
        _autostartBtn.BackColor = enabled ? Color.FromArgb(0, 99, 177) : Color.FromArgb(200, 200, 200);
        _autostartBtn.ForeColor = enabled ? Color.White : Color.Black;
        _autostartBtn.FlatAppearance.BorderSize = 0;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosed(e);
    }
}
