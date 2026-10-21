using System.Speech.Synthesis;

namespace TimeBossClone.Services;

/// <summary>Голосовые предупреждения. Любая ошибка TTS не должна мешать работе контроля.</summary>
public sealed class SpeechService : IDisposable
{
    private SpeechSynthesizer? _synth;
    private bool _hasRussian;
    private bool _initFailed;

    public void SayWarning(int minutesLeft)
    {
        var synth = GetSynth();
        if (synth == null) return;

        string text = _hasRussian
            ? $"Внимание! Осталось {minutesLeft} {Plural.Minutes(minutesLeft)}."
            : $"Warning! {minutesLeft} minute{(minutesLeft == 1 ? "" : "s")} remaining.";

        try
        {
            synth.SpeakAsyncCancelAll();
            synth.SpeakAsync(text);
        }
        catch (Exception ex) { AppLog.Error("Озвучка", ex); }
    }

    // Ленивая инициализация: создание синтезатора медленное и может упасть
    // (нет голосов/служб) — раньше это происходило в конструкторе приложения.
    private SpeechSynthesizer? GetSynth()
    {
        if (_synth != null || _initFailed) return _synth;
        try
        {
            var synth = new SpeechSynthesizer { Volume = 100, Rate = -1 };
            var ruVoice = synth.GetInstalledVoices()
                .FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "ru");
            if (ruVoice != null)
            {
                synth.SelectVoice(ruVoice.VoiceInfo.Name);
                _hasRussian = true;
            }
            _synth = synth;
        }
        catch (Exception ex)
        {
            _initFailed = true;
            AppLog.Error("Инициализация синтезатора речи", ex);
        }
        return _synth;
    }

    public void Dispose()
    {
        _synth?.Dispose();
        _synth = null;
    }
}

public static class Plural
{
    /// <summary>1 минута, 2 минуты, 5 минут, 11 минут, 21 минута.</summary>
    public static string Minutes(int n)
    {
        n = Math.Abs(n);
        int mod10 = n % 10, mod100 = n % 100;
        if (mod10 == 1 && mod100 != 11) return "минута";
        if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return "минуты";
        return "минут";
    }
}
