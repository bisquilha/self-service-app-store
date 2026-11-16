using System.Windows;
using Microsoft.Win32;

namespace Loja.App;

/// <summary>
/// Tema do app: segue o do Windows (claro/escuro para aplicativos). Só LÊ a configuração do usuário
/// (HKCU\...\Themes\Personalize\AppsUseLightTheme); não grava nada no registro. LOJA_TEMA=claro|escuro força um dos dois (suporte e testes).
/// </summary>
internal static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsLight { get; private set; } = true;

    /// <summary>Disparado (na thread da tela) depois de trocar claro/escuro; as janelas ajustam o que não vem dos recursos (barra de título).</summary>
    public static event Action? Changed;

    public static void Initialize()
    {
        Apply(DetectLight());
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Application.Current.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        // O evento chega numa thread do sistema; a troca de recursos tem de ser feita na da tela.
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(DetectLight()));
    }

    private static bool DetectLight()
    {
        switch (Environment.GetEnvironmentVariable("LOJA_TEMA")?.Trim().ToLowerInvariant())
        {
            case "claro": return true;
            case "escuro": return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception)
        {
            return true; // sem conseguir ler, fica no claro
        }
    }

    private static void Apply(bool light)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var uri = new Uri($"pack://application:,,,/Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Absolute);
        if (dictionaries.Count > 0) dictionaries[0] = new ResourceDictionary { Source = uri };
        else dictionaries.Add(new ResourceDictionary { Source = uri });
        IsLight = light;
        Changed?.Invoke();
    }
}
