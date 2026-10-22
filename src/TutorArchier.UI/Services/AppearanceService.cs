using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using TutorArchier.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TutorArchier.UI.Services;

/// <summary>
/// Applies System / Dark / Light: WPF-UI's control theme, our colour tokens (plan §6.5) and the accent.
/// "System" follows Windows live.
/// </summary>
public sealed class AppearanceService(ISettingsStore settings) : IDisposable
{
    private const string TokensMarker = "Themes/Tokens.";

    public AppTheme Preference { get; private set; }

    public bool IsDark { get; private set; }

    public void Initialize()
    {
        Preference = settings.Get(AppSettingKeys.Theme);
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void SetPreference(AppTheme theme)
    {
        if (theme == Preference)
            return;
        Preference = theme;
        settings.Set(AppSettingKeys.Theme, theme);
        Apply();
    }

    private void Apply()
    {
        IsDark = Preference switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemUsesDarkTheme(),
        };

        var theme = IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: false);
        SwapTokens(IsDark ? "Tokens.Dark.xaml" : "Tokens.Light.xaml");

        // Explicit shades: the single-colour overload lightens and desaturates the accent for dark mode.
        var resources = Application.Current.Resources;
        var accent = (Color)resources["Color.Accent"];
        ApplicationAccentColorManager.Apply(accent, accent, accent, (Color)resources["Color.AccentText"]);
    }

    private static void SwapTokens(string fileName)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/TutorArchier;component/Themes/{fileName}", UriKind.Absolute),
        };

        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source?.OriginalString.Contains(TokensMarker, StringComparison.Ordinal) == true)
            {
                merged[i] = replacement;
                return;
            }
        }
        merged.Add(replacement);
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int useLight && useLight == 0;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Preference != AppTheme.System || SystemUsesDarkTheme() == IsDark)
            return;
        Application.Current?.Dispatcher.Invoke(Apply);
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
