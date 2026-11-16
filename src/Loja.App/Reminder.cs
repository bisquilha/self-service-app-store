using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Loja.App;

/// <summary>
/// Modo "--lembrete": roda sem abrir a janela da loja (tarefa agendada no logon e a cada 4 h, criada pela GPO).
/// Pergunta ao servidor se há aplicativo com atualização neste computador e, se houver, avisa no canto da tela.
/// Nunca mostra erro ao usuário: qualquer problema só vai para o log (lembrete.log).
/// </summary>
public static class Reminder
{
    public const string Flag = "--lembrete";
    private const int MaxLogBytes = 100_000;

    public static bool IsReminder(string[] args) => args.Contains(Flag);

    private static string DataDir =>
        Environment.GetEnvironmentVariable("LOJA_LEMBRETE_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LojaDeAplicativos");

    private static string StatePath => Path.Combine(DataDir, "lembrete.json");
    private static string LogPath => Path.Combine(DataDir, "lembrete.log");

    /// <summary>Roda o lembrete e encerra o aplicativo ao terminar.</summary>
    public static async Task RunAndExitAsync(AppConfig config)
    {
        try
        {
            await RunAsync(config);
        }
        catch (Exception ex)
        {
            Log("erro: " + ex.Message);
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static async Task RunAsync(AppConfig config)
    {
        if (!config.ReminderEnabled) { Log("desligado no loja.json"); return; }

        // Um lembrete por usuário de cada vez (o logon e a repetição de 4 h podem coincidir).
        using var mutex = new Mutex(true, @"Local\LojaDeAplicativos-Lembrete", out var first);
        if (!first) { Log("já há outro lembrete rodando"); return; }

        // Se a loja já está aberta, o usuário está vendo a lista: não avisa por cima.
        if (StoreIsOpen()) { Log("loja aberta; sem aviso"); return; }

        using var api = new LojaApi(config);
        var host = Environment.MachineName;

        var me = await api.GetMeAsync(host);
        if (!me.MachineOk) { Log("computador não liberado: " + me.MachineMessage); return; }

        // Pede a lista de programas ao computador sem forçar (o servidor ignora se a última ainda é recente).
        try { await api.RefreshInventoryAsync(host, force: false); } catch (ApiException) { }

        var catalog = await api.GetCatalogAsync(host);
        var pending = catalog.Items.Where(i => i.UpdateAvailable).OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        var now = DateTime.UtcNow;
        var state = LoadState();
        ReminderPolicy.Track(state, pending.Select(i => i.PackageId), now);

        var interval = TimeSpan.FromHours(Math.Clamp(config.ReminderHours, 1, 72));
        var action = ReminderPolicy.Decide(state, now, interval, config.ReminderEscalationDays);
        Log($"pendentes={pending.Count} ação={action}");

        switch (action)
        {
            case ReminderAction.Popup:
            {
                var choice = ShowPopup(pending.Select(i => i.Title).ToList());
                state.LastNotifyUtc = now;
                if (choice == ReminderWindow.Choice.Snooze) state.SnoozeUntilUtc = now + ReminderPolicy.SnoozeFor;
                if (choice == ReminderWindow.Choice.Update) OpenStore(config);
                Log("aviso: " + choice);
                break;
            }
            case ReminderAction.OpenWindow:
                state.LastNotifyUtc = now;
                state.LastWindowUtc = now;
                OpenStore(config);
                Log("abriu a loja (pendência antiga)");
                break;
        }

        SaveState(state);
    }

    private static ReminderWindow.Choice ShowPopup(IReadOnlyList<string> titles)
    {
        var window = new ReminderWindow(titles);
        window.ShowDialog();
        return window.Result;
    }

    private static void OpenStore(AppConfig config)
    {
        // Abre a loja pelo mesmo .exe do atalho (Program Files): ele cuida de abrir a versão que o servidor mandou usar.
        // Só se ele não existir é que usa este próprio .exe (cópia do lembrete), sem redirecionar.
        var launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Loja de Aplicativos", "LojaDeAplicativos.exe");
        ProcessStartInfo psi;
        if (File.Exists(launcher))
        {
            psi = new ProcessStartInfo(launcher) { UseShellExecute = false };
        }
        else
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;
            psi = new ProcessStartInfo(path) { UseShellExecute = false };
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(config.ConfigPath);
            psi.ArgumentList.Add("--sem-redirecionar");
        }
        Process.Start(psi);
    }

    private static bool StoreIsOpen()
    {
        var me = Process.GetCurrentProcess();
        return Process.GetProcessesByName(me.ProcessName).Any(p =>
        {
            try { return p.Id != me.Id && p.SessionId == me.SessionId && p.MainWindowHandle != IntPtr.Zero; }
            catch (Exception) { return false; }
            finally { p.Dispose(); }
        });
    }

    private static ReminderState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<ReminderState>(File.ReadAllText(StatePath)) ?? new ReminderState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new ReminderState();
    }

    private static void SaveState(ReminderState state)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                File.WriteAllLines(LogPath, File.ReadLines(LogPath).TakeLast(200));
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception) { }
    }
}

/// <summary>Aviso no canto inferior direito, sem roubar o foco. Fecha sozinho em 60 s (conta como ignorado).</summary>
internal sealed class ReminderWindow : Window
{
    public enum Choice { Ignored, Update, Snooze }

    public Choice Result { get; private set; } = Choice.Ignored;

    public ReminderWindow(IReadOnlyList<string> titles)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        Title = "Loja de Aplicativos";
        SetResourceReference(BackgroundProperty, "BgSurface");

        var count = titles.Count;
        var names = string.Join(", ", titles.Take(3)) + (count > 3 ? $" e mais {count - 3}" : "");

        var heading = new TextBlock { Text = "Atualizações disponíveis", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var body = new TextBlock { Text = $"{Pt.Plural(count, "aplicativo", "aplicativos")} com atualização: {names}.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var update = new Button { Content = "Atualizar agora", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        update.SetResourceReference(StyleProperty, "FlatButton");
        update.Click += (_, _) => Finish(Choice.Update);
        var snooze = new Button { Content = "Lembrar amanhã", Padding = new Thickness(14, 6, 14, 6) };
        snooze.SetResourceReference(StyleProperty, "SecondaryButton");
        snooze.Click += (_, _) => Finish(Choice.Snooze);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { snooze, update } };

        var content = new StackPanel { Margin = new Thickness(16, 14, 16, 14), Children = { heading, body, buttons } };
        var stripe = new Border { Width = 4 };
        stripe.SetResourceReference(Border.BackgroundProperty, "Accent");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(stripe);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var frame = new Border { BorderThickness = new Thickness(1), Child = grid };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        Content = frame;

        SizeChanged += (_, _) => Place();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Finish(Choice.Ignored); };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) => { timer.Stop(); Finish(Choice.Ignored); };
        Loaded += (_, _) => timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    private void Finish(Choice choice)
    {
        Result = choice;
        Close();
    }
}
