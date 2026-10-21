using TimeBossClone.Forms;
using TimeBossClone.Services;

namespace TimeBossClone;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "TimeBossClone_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("TimeBoss уже запущен.", "TimeBoss",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Необработанное исключение раньше просто убивало процесс — и контроль пропадал.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppLog.Error("Необработанное исключение (UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Необработанное исключение", e.ExceptionObject as Exception ?? new Exception("unknown"));

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var config = new ConfigManager();

        // Первый запуск — ДО Application.Run. Раньше Application.Exit() вызывался из
        // конструктора контекста, когда цикл сообщений ещё не запущен, и при «Отмене»
        // оставался невидимый процесс-зомби, держащий мьютекс.
        if (config.NeedsSetup)
        {
            using var setup = new LoginForm(LoginMode.Setup, config);
            if (setup.ShowDialog() != DialogResult.OK) return;
            config.Config.IsFirstRun = false;
            config.Save();
        }

        AppLog.Info($"Запуск для пользователя {config.CurrentUser}");
        Application.Run(new TrayApplicationContext(config));
    }
}
