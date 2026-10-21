using Microsoft.Win32;

namespace TimeBossClone.Forms;

public class TimerOverlayForm : Form
{
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Label _label;

    public TimerOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(20, 20, 20);
        Opacity = 0.82;
        Size = new Size(164, 44);
        StartPosition = FormStartPosition.Manual;

        _label = new Label
        {
            ForeColor = Color.Lime,
            BackColor = Color.Transparent,
            Font = new Font("Consolas", 17f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            Text = "--:--"
        };
        Controls.Add(_label);

        RepositionToBottomRight();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>
    /// Раньше текст не обновлялся, пока у формы нет handle (до первого Show),
    /// а позиция пересчитывалась каждую секунду.
    /// </summary>
    public void UpdateRemaining(int remainingSeconds)
    {
        if (IsDisposed) return;

        int h = remainingSeconds / 3600;
        int m = remainingSeconds % 3600 / 60;
        int s = remainingSeconds % 60;
        _label.Text = h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m:D2}:{s:D2}";
        _label.ForeColor = remainingSeconds <= 300 ? Color.OrangeRed : Color.Lime;
    }

    public void SetVisible(bool visible)
    {
        if (IsDisposed || Visible == visible) return;
        if (visible)
        {
            RepositionToBottomRight();
            Show();
        }
        else
        {
            Hide();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(RepositionToBottomRight); return; }
        RepositionToBottomRight();
    }

    private void RepositionToBottomRight()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Right - Width - 10, area.Bottom - Height - 10);
    }

    protected override bool ShowWithoutActivation => true;

    // Полная «прозрачность» для мыши, без фокуса и без появления в Alt+Tab.
    // (HTTRANSPARENT в WM_NCHITTEST работает только для окон своего же потока.)
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        base.Dispose(disposing);
    }
}
