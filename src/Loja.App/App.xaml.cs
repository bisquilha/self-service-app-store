using System.Windows;

namespace Loja.App;

public partial class App : Application
{
    /// <summary>Argumentos e configuração da abertura: repassados quando o app reinicia numa versão nova.</summary>
    public static string[] StartupArgs { get; private set; } = [];
    public static string ConfigPath { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // No modo --lembrete (segundo plano) nunca aparece mensagem de erro para o usuário.
        var silent = Reminder.IsReminder(e.Args);
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            if (silent) { Shutdown(); return; }
            MessageBox.Show("Erro inesperado: " + ex.Exception.Message, "Loja de Aplicativos", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        try
        {
            var config = AppConfig.Load(e.Args);
            StartupArgs = e.Args;
            ConfigPath = config.ConfigPath;

            // O servidor mandou usar outra versão e ela já está baixada: abre a outra e sai.
            if (Updater.RedirectIfNeeded(e.Args, config.ConfigPath))
            {
                Shutdown();
                return;
            }

            Theme.Initialize();

            if (silent)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = Reminder.RunAndExitAsync(config);
                return;
            }

            var api = new LojaApi(config);
            var window = new MainWindow(api);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            if (!silent) MessageBox.Show(ex.Message, "Loja de Aplicativos", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
