using TimeBossClone.Services;

namespace TimeBossClone.Forms;

public enum LoginMode
{
    Login,
    Setup,
    ChangePassword
}

public class LoginForm : Form
{
    private const int MinPasswordLength = 4;
    private const int FreeAttempts = 3;
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(5);

    // Общие для всех окон входа в этом процессе: закрыть и открыть окно заново
    // не сбрасывает счётчик неудачных попыток.
    private static int _failedAttempts;
    private static DateTime _lockedUntil = DateTime.MinValue;

    private readonly LoginMode _mode;
    private readonly ConfigManager _config;
    private readonly TextBox _passwordBox;
    private readonly TextBox? _confirmBox;
    private readonly Label _errorLabel;
    private readonly Button _btnOk;
    private readonly System.Windows.Forms.Timer _lockoutTimer;

    /// <summary>Введённый пароль (после DialogResult.OK) — нужен для подписи запросов.</summary>
    public string Password { get; private set; } = string.Empty;

    public LoginForm(LoginMode mode, ConfigManager config)
    {
        _mode = mode;
        _config = config;
        bool isSetup = mode != LoginMode.Login;

        Text = mode switch
        {
            LoginMode.Setup => "Первый запуск — Создание пароля",
            LoginMode.ChangePassword => "Смена пароля",
            _ => "Вход в панель управления"
        };
        Size = new Size(340, isSetup ? 240 : 185);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true; // окно из трея иначе может открыться под другими окнами

        Controls.Add(new Label
        {
            Text = mode switch
            {
                LoginMode.Setup => "Придумайте пароль для родителя:",
                LoginMode.ChangePassword => "Новый пароль:",
                _ => "Введите пароль:"
            },
            Location = new Point(20, 20),
            AutoSize = true
        });

        _passwordBox = new TextBox
        {
            UseSystemPasswordChar = true,
            Location = new Point(20, 45),
            Width = 285
        };
        Controls.Add(_passwordBox);

        int nextY = 75;

        if (isSetup)
        {
            Controls.Add(new Label
            {
                Text = "Повторите пароль:",
                Location = new Point(20, nextY),
                AutoSize = true
            });
            nextY += 25;

            _confirmBox = new TextBox
            {
                UseSystemPasswordChar = true,
                Location = new Point(20, nextY),
                Width = 285
            };
            Controls.Add(_confirmBox);
            nextY += 35;
        }

        _errorLabel = new Label
        {
            ForeColor = Color.Crimson,
            Location = new Point(20, nextY),
            Size = new Size(285, 20)
        };
        Controls.Add(_errorLabel);
        nextY += 28;

        _btnOk = new Button
        {
            Text = mode == LoginMode.Login ? "Войти" : mode == LoginMode.Setup ? "Создать" : "Сменить",
            Location = new Point(20, nextY),
            Width = 130,
            Height = 32
        };
        _btnOk.Click += (_, _) => TrySubmit();
        Controls.Add(_btnOk);

        var btnCancel = new Button
        {
            Text = "Отмена",
            Location = new Point(175, nextY),
            Width = 130,
            Height = 32,
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(btnCancel);

        // Enter обрабатывается через AcceptButton — дублирующие KeyDown-обработчики убраны.
        AcceptButton = _btnOk;
        CancelButton = btnCancel;

        _lockoutTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _lockoutTimer.Tick += (_, _) => UpdateLockoutState();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _passwordBox.Focus();
        if (_mode == LoginMode.Login) UpdateLockoutState();
    }

    private void TrySubmit()
    {
        _errorLabel.Text = string.Empty;

        if (_mode == LoginMode.Login)
        {
            if (DateTime.Now < _lockedUntil) { UpdateLockoutState(); return; }

            if (_config.VerifyPassword(_passwordBox.Text))
            {
                _failedAttempts = 0;
                Password = _passwordBox.Text;
                DialogResult = DialogResult.OK;
                return;
            }

            // Защита от перебора: после 3 ошибок — растущая пауза (30 с, 60 с, … до 5 мин).
            _failedAttempts++;
            AppLog.Info($"Неверный пароль (попытка {_failedAttempts})");
            if (_failedAttempts >= FreeAttempts)
            {
                var delay = TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Min(_failedAttempts - FreeAttempts, 4)));
                _lockedUntil = DateTime.Now + (delay < MaxLockout ? delay : MaxLockout);
                UpdateLockoutState();
            }
            else
            {
                _errorLabel.Text = "Неверный пароль";
            }
            _passwordBox.Clear();
            _passwordBox.Focus();
            return;
        }

        if (_passwordBox.Text.Length < MinPasswordLength)
        {
            _errorLabel.Text = $"Минимум {MinPasswordLength} символа";
            return;
        }
        if (_passwordBox.Text != _confirmBox!.Text)
        {
            _errorLabel.Text = "Пароли не совпадают";
            _confirmBox.Clear();
            _confirmBox.Focus();
            return;
        }

        UseWaitCursor = true;
        _config.SetPassword(_passwordBox.Text);
        _config.Save();
        UseWaitCursor = false;
        Password = _passwordBox.Text;
        DialogResult = DialogResult.OK;
    }

    private void UpdateLockoutState()
    {
        var left = _lockedUntil - DateTime.Now;
        bool locked = left > TimeSpan.Zero;

        _passwordBox.Enabled = !locked;
        _btnOk.Enabled = !locked;

        if (locked)
        {
            _errorLabel.Text = $"Слишком много попыток. Подождите {Math.Ceiling(left.TotalSeconds)} с";
            _lockoutTimer.Start();
        }
        else if (_lockoutTimer.Enabled)
        {
            _lockoutTimer.Stop();
            _errorLabel.Text = string.Empty;
            _passwordBox.Focus();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _lockoutTimer.Dispose();
        base.Dispose(disposing);
    }
}
