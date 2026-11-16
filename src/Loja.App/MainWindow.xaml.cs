using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Loja.Shared;

namespace Loja.App;

public partial class MainWindow : Window
{
    private enum ListState { None, Loading, Empty, NoResults, Error }

    private readonly LojaApi _api;
    private readonly string _hostname = Environment.MachineName;
    private readonly ObservableCollection<CatalogItemVm> _items = new();
    private readonly Dictionary<int, ImageSource?> _icons = new();
    private readonly ICollectionView _catalogView;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _polling;
    private bool _machineOk;
    private string? _machineMessage;
    private bool _inventoryPending;
    private bool _inventoryFailed;
    private bool _reloading;
    private bool _closing;
    private CatalogDto? _catalog;
    private ListState _listState = ListState.None;

    // "Atualizar todos": fila sequencial no lado do app (o servidor só aceita um pedido por computador por vez).
    private bool _batchRunning;
    private bool _batchStop;
    private string? _batchSummary;
    private string? _batchDetail;
    private bool _batchProblem;

    public MainWindow(LojaApi api)
    {
        InitializeComponent();
        _api = api;

        // Em tela pequena (1366x768 tem só ~728 de área útil) a janela não pode nascer maior que a tela.
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 24));
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 24));

        CatalogList.ItemsSource = _items;
        _catalogView = CollectionViewSource.GetDefaultView(_items);
        _catalogView.Filter = o => o is CatalogItemVm vm && MatchesSearch(vm);
        MyRequestsGrid.ItemsSource = Array.Empty<RequestRowVm>();
        HostText.Text = $"Computador: {_hostname}";

        _pollTimer.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) =>
        {
            await ReloadAsync();
            await CheckForUpdateAsync();

            // Quem deixa a loja aberta também recebe as versões novas (e as obrigatórias reiniciam sozinhas).
            var interval = int.TryParse(Environment.GetEnvironmentVariable("LOJA_UPDATE_CHECK_SECONDS"), out var s) && s >= 5 ? s : 1800;
            _updateTimer.Interval = TimeSpan.FromSeconds(interval);
            _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
            _updateTimer.Start();
        };
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        Theme.Changed += ApplyTitleBarTheme;
        Closed += (_, _) =>
        {
            _closing = true;
            Theme.Changed -= ApplyTitleBarTheme;
            _api.Dispose();
        };
        Title = $"Loja de Aplicativos {Updater.CurrentText}";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Barra de título escura quando o tema é escuro (só desta janela; não mexe em nenhuma configuração do Windows).</summary>
    private void ApplyTitleBarTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = Theme.IsLight ? 0 : 1;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1 em diante e Windows 11); 19 nas versões mais antigas do 10.
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
    }

    // ---------- Autoupdate do próprio app ----------

    private readonly DispatcherTimer _updateTimer = new();
    private Version? _readyVersion;
    private DispatcherTimer? _mandatoryTimer;
    private bool _checking;

    /// <summary>Baixa em segundo plano a versão que o servidor mandou usar. Nunca atrapalha o uso da loja.</summary>
    private async Task CheckForUpdateAsync()
    {
        if (_checking || _mandatoryTimer is not null) return; // já baixando, ou já em contagem para reiniciar
        _checking = true;
        try
        {
            await CheckForUpdateCoreAsync();
        }
        finally
        {
            _checking = false;
        }
    }

    private async Task CheckForUpdateCoreAsync()
    {
        (Version Version, bool Mandatory)? ready;
        try
        {
            ready = await Updater.CheckAsync(_api, CancellationToken.None);
        }
        catch (Exception)
        {
            return; // sem rede ou download com problema: tenta de novo na próxima abertura
        }
        if (ready is not { } r) return;

        _readyVersion = r.Version;
        UpdateBar.Visibility = Visibility.Visible;
        if (!r.Mandatory)
        {
            UpdateText.Text = r.Version > Updater.Current
                ? $"Nova versão da loja ({r.Version.ToString(3)}) pronta. Ela será usada na próxima vez que você abrir a loja."
                : $"A TI definiu a versão {r.Version.ToString(3)} da loja. Ela será usada na próxima vez que você abrir a loja.";
            return;
        }

        // Obrigatória: avisa e reinicia sozinha (sem interromper um pedido em andamento).
        var seconds = 15;
        UpdateText.Text = $"Atualização obrigatória da loja ({r.Version.ToString(3)}). Reiniciando em {seconds} s…";
        _mandatoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _mandatoryTimer.Tick += (_, _) =>
        {
            if (_batchRunning || _items.Any(i => i.IsBusy))
            {
                UpdateText.Text = $"Atualização obrigatória da loja ({r.Version.ToString(3)}). Reinicia assim que o pedido em andamento terminar.";
                return;
            }
            seconds--;
            UpdateText.Text = $"Atualização obrigatória da loja ({r.Version.ToString(3)}). Reiniciando em {seconds} s…";
            if (seconds <= 0) RestartIntoNewVersion();
        };
        _mandatoryTimer.Start();
    }

    private void RestartNow_Click(object sender, RoutedEventArgs e) => RestartIntoNewVersion();

    private void RestartIntoNewVersion()
    {
        if (_readyVersion is null) return;
        _mandatoryTimer?.Stop();
        try
        {
            Updater.Launch(_readyVersion, App.StartupArgs, App.ConfigPath);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateText.Text = $"Não foi possível abrir a versão nova: {ex.Message}";
        }
    }

    // ---------- Loja ----------

    /// <param name="forceInventory">true no botão "Atualizar lista": pede a lista de instalados mesmo se for recente.</param>
    private async Task ReloadAsync(bool forceInventory = false)
    {
        if (_reloading) return; // clique duplo / F5 repetido: um de cada vez
        _reloading = true;
        ReloadButton.IsEnabled = false;
        _inventoryFailed = false;
        if (!_batchRunning) _batchSummary = null;
        SetStatus("Carregando…");
        if (_items.Count == 0) SetListState(ListState.Loading);
        try
        {
            var me = await _api.GetMeAsync(_hostname);
            UserText.Text = me.User;
            _machineOk = me.MachineOk;
            _machineMessage = me.MachineMessage;
            AdminButton.Visibility = me.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
            ShowBanner(me.MachineOk ? null : me.MachineMessage);

            await LoadCatalogAsync();

            // Pedidos anteriores só aparecem no card se ainda estão em andamento ou deram erro recente;
            // os que deram certo já estão refletidos no estado "Instalado / Atualizar".
            var mine = await _api.GetMyRequestsAsync(_hostname);
            foreach (var vm in _items)
            {
                var last = mine.FirstOrDefault(r => r.AppId == vm.Id);
                if (last is null || vm.IsBusy) continue;
                if (last.Status == RequestStatus.Running
                    || (last.Status != RequestStatus.Success && last.CreatedUtc > DateTime.UtcNow.AddHours(-12)))
                    vm.Apply(last);
            }
            ShowMyRequests(mine);

            if (me.MachineOk) await RequestInventoryAsync(forceInventory);
            UpdatePolling();
        }
        catch (ApiException ex)
        {
            if (_items.Count == 0)
            {
                ShowBanner(null);
                SetListState(ListState.Error, ex.Message);
            }
            else
            {
                ShowBanner(ex.Message);
            }
            SetStatus("Sem conexão com o servidor da loja.");
        }
        finally
        {
            _reloading = false;
            ReloadButton.IsEnabled = true;
        }
    }

    /// <summary>Busca o catálogo (com o que está instalado aqui) e atualiza os cards sem perder o estado deles.</summary>
    private async Task LoadCatalogAsync()
    {
        var catalog = await _api.GetCatalogAsync(_hostname);
        _catalog = catalog;
        var existing = _items.ToDictionary(i => i.Id);
        var ordered = new List<CatalogItemVm>();
        foreach (var dto in catalog.Items.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (existing.TryGetValue(dto.Id, out var vm))
            {
                vm.Update(dto);
            }
            else
            {
                vm = new CatalogItemVm(dto);
                vm.RequestChanged += OnRequestChanged;
            }
            vm.MachineMessage = _machineMessage;
            vm.MachineOk = _machineOk;
            ordered.Add(vm);
        }

        if (!ordered.Select(v => v.Id).SequenceEqual(_items.Select(v => v.Id)))
        {
            _items.Clear();
            foreach (var vm in ordered) _items.Add(vm);
        }

        _inventoryPending = catalog.InventoryRunning;
        RefreshGate();
        RefreshListState();
        RefreshUpdatesBar();
        RefreshFooter();

        _ = LoadIconsAsync(ordered.Where(v => v.Icon is null).ToList());
    }

    private async Task RequestInventoryAsync(bool force)
    {
        try
        {
            await _api.RefreshInventoryAsync(_hostname, force);
            await LoadCatalogAsync();
        }
        catch (ApiException)
        {
            // sem a lista de instalados os cards só mostram "Instalar"; o rodapé avisa e o "Atualizar lista" tenta de novo
            _inventoryFailed = true;
            RefreshFooter();
        }
    }

    private async Task LoadIconsAsync(List<CatalogItemVm> items)
    {
        foreach (var vm in items.Where(i => i.Dto.HasIcon))
        {
            if (!_icons.TryGetValue(vm.Id, out var image))
            {
                try
                {
                    var bytes = await _api.GetIconAsync(vm.Id);
                    image = bytes is null ? null : ToImage(bytes);
                }
                catch (Exception)
                {
                    image = null; // sem ícone: fica a inicial do nome
                }
                _icons[vm.Id] = image;
            }
            vm.Icon = image;
        }
    }

    private static ImageSource? ToImage(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 88;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------- Pedidos do card ----------

    /// <summary>Botão principal do card: Instalar ou Atualizar.</summary>
    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CatalogItemVm vm || !CanSend(vm)) return;
        await SendAsync(vm, vm.PrimaryAction);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CatalogItemVm vm || !CanSend(vm)) return;
        var answer = MessageBox.Show(this, $"Desinstalar \"{vm.Title}\" deste computador?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes && CanSend(vm)) await SendAsync(vm, RequestActions.Uninstall);
    }

    /// <summary>Só um pedido por vez neste computador: evita clique duplo e pedidos em cima de outro (o servidor recusaria).</summary>
    private bool CanSend(CatalogItemVm vm) => _machineOk && !_batchRunning && !vm.IsBusy && !_items.Any(i => i.IsBusy);

    private async Task SendAsync(CatalogItemVm vm, string action)
    {
        vm.MarkSending(action);
        try
        {
            vm.Apply(await _api.RequestAsync(vm.Id, _hostname, action));
            UpdatePolling();
        }
        catch (ApiException ex)
        {
            vm.ShowError(ex.Message, ex.Status);
        }
    }

    private void OnRequestChanged()
    {
        RefreshGate();
        RefreshUpdatesBar();
    }

    /// <summary>Trava os botões dos outros cards enquanto algum pedido (ou a fila do "Atualizar todos") está em andamento.</summary>
    private void RefreshGate()
    {
        var reason = _batchRunning ? "Atualizando todos os aplicativos. Aguarde terminar."
            : _items.Any(i => i.IsBusy) ? "Há outro pedido em andamento neste computador. Aguarde ele terminar."
            : null;
        foreach (var vm in _items) vm.LockReason = vm.IsBusy ? null : reason;
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        var finished = false;
        try
        {
            foreach (var vm in _items.Where(i => i.ActiveRequestId.HasValue).ToList())
            {
                try
                {
                    var request = await _api.GetRequestAsync(vm.ActiveRequestId!.Value);
                    vm.Apply(request);
                    finished |= request.Status != RequestStatus.Running;
                }
                catch (ApiException)
                {
                    // falha momentânea de rede: tenta de novo no próximo ciclo
                }
            }

            // Quando um pedido termina, o script já mandou a lista nova de instalados: recarrega os cards.
            if (finished || _inventoryPending)
            {
                try
                {
                    await LoadCatalogAsync();
                }
                catch (ApiException)
                {
                    // tenta de novo no próximo ciclo
                }
            }

            if (finished) await RefreshMyRequestsAsync();
        }
        finally
        {
            _polling = false;
            UpdatePolling();
        }
    }

    private void UpdatePolling()
    {
        var anyRunning = _inventoryPending || _items.Any(i => i.ActiveRequestId.HasValue);
        if (anyRunning && !_pollTimer.IsEnabled) _pollTimer.Start();
        else if (!anyRunning && _pollTimer.IsEnabled) _pollTimer.Stop();
    }

    private async Task RefreshMyRequestsAsync()
    {
        try
        {
            ShowMyRequests(await _api.GetMyRequestsAsync(_hostname));
        }
        catch (ApiException)
        {
            // a aba de pedidos atualiza na próxima vez
        }
    }

    private void ShowMyRequests(List<RequestDto> mine)
    {
        MyRequestsGrid.ItemsSource = mine.Select(r => new RequestRowVm(r)).ToList();
        MyRequestsEmpty.Visibility = mine.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Atualizar todos (só este computador, só os apps liberados para o usuário) ----------

    private async void UpdateAll_Click(object sender, RoutedEventArgs e) => await UpdateAllAsync();

    private void StopBatch_Click(object sender, RoutedEventArgs e)
    {
        _batchStop = true;
        StopButton.IsEnabled = false;
        UpdatesDetail.Text = " · Parando: o aplicativo em andamento termina e os próximos não começam.";
    }

    private async Task UpdateAllAsync()
    {
        if (_batchRunning || !_machineOk) return;
        if (_items.Any(i => i.IsBusy))
        {
            MessageBox.Show(this, "Há um pedido em andamento neste computador. Aguarde ele terminar e tente de novo.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var todo = _items.Where(i => i.CanUpdate).ToList();
        if (todo.Count == 0) return;

        var lines = string.Join("\n", todo.Take(8).Select(i => $"•  {i.Title}: {i.Dto.InstalledVersion} → {i.Dto.Version}"));
        if (todo.Count > 8) lines += $"\n•  e mais {todo.Count - 8}";
        var answer = MessageBox.Show(this,
            $"Atualizar {Pt.Plural(todo.Count, "aplicativo", "aplicativos")} neste computador ({_hostname})?\n\n{lines}\n\n"
            + "Eles são atualizados um de cada vez, e você pode continuar usando o computador.",
            "Atualizar todos", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        todo = todo.Where(i => i.CanUpdate && !i.IsBusy).ToList(); // o diálogo pode ter ficado aberto um tempo
        if (todo.Count == 0) return;

        _batchRunning = true;
        _batchStop = false;
        _batchSummary = null;
        RefreshGate();
        RefreshUpdatesBar();

        var updated = 0;
        var failed = new List<string>();
        var notStarted = 0;
        try
        {
            for (var i = 0; i < todo.Count; i++)
            {
                if (_batchStop || _closing)
                {
                    notStarted = todo.Count - i;
                    break;
                }

                var vm = todo[i];
                UpdatesTitle.Text = $"Atualizando {i + 1} de {todo.Count}: {vm.Title}…";
                UpdatesDetail.Text = " · Pode continuar usando o computador.";
                await SendAsync(vm, RequestActions.Upgrade);

                // O acompanhamento (a cada 3 s) é o mesmo dos cards; aqui só se espera o pedido terminar.
                while (vm.ActiveRequestId is not null && !_closing) await Task.Delay(700);

                if (vm.LastStatus == RequestStatus.Success)
                {
                    updated++;
                    continue;
                }

                failed.Add($"{vm.Title}: {vm.LastError ?? "sem detalhes"}");
                if (vm.LastErrorCode == 409)
                {
                    // O servidor já tem outro pedido rodando neste computador: continuar só repetiria o erro.
                    notStarted = todo.Count - i - 1;
                    break;
                }
            }
        }
        finally
        {
            _batchRunning = false;
        }

        try
        {
            await LoadCatalogAsync();
            await RefreshMyRequestsAsync();
        }
        catch (ApiException)
        {
            // o resumo abaixo continua valendo; os cards atualizam no próximo "Atualizar lista"
        }

        var parts = new List<string>();
        if (updated > 0) parts.Add(Pt.Plural(updated, "aplicativo atualizado", "aplicativos atualizados"));
        if (failed.Count > 0) parts.Add(Pt.Plural(failed.Count, "não foi atualizado", "não foram atualizados"));
        if (notStarted > 0) parts.Add(Pt.Plural(notStarted, "ficou de fora", "ficaram de fora"));
        _batchProblem = failed.Count > 0 || notStarted > 0;
        _batchSummary = (updated == todo.Count ? "Pronto: " : "Terminou: ") + string.Join(" · ", parts) + ".";
        _batchDetail = failed.Count > 0 ? string.Join(" | ", failed) : null;
        RefreshGate();
        RefreshUpdatesBar();
    }

    /// <summary>Faixa do topo da lista: quantos têm atualização, o andamento do "Atualizar todos" ou o resumo dele.</summary>
    private void RefreshUpdatesBar()
    {
        var count = _machineOk ? _items.Count(i => i.CanUpdate && !i.IsBusy) : 0;
        var warn = false;

        if (_batchRunning)
        {
            UpdateAllButton.IsEnabled = false;
            StopButton.Visibility = Visibility.Visible;
            UpdateAllButton.Visibility = Visibility.Visible;
            UpdatesBar.Visibility = Visibility.Visible;
        }
        else if (_batchSummary is not null)
        {
            warn = _batchProblem;
            StopButton.Visibility = Visibility.Collapsed;
            UpdatesTitle.Text = _batchSummary;
            UpdatesDetail.Text = _batchDetail is null ? "" : " · " + _batchDetail;
            UpdateAllButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateAllButton.IsEnabled = !_items.Any(i => i.IsBusy);
            UpdatesBar.Visibility = Visibility.Visible;
        }
        else if (count > 0)
        {
            StopButton.Visibility = Visibility.Collapsed;
            UpdatesTitle.Text = $"{Pt.Plural(count, "aplicativo", "aplicativos")} com atualização";
            UpdatesDetail.Text = " · Só neste computador e só os aplicativos liberados para você.";
            UpdateAllButton.Visibility = Visibility.Visible;
            UpdateAllButton.IsEnabled = !_items.Any(i => i.IsBusy);
            UpdatesBar.Visibility = Visibility.Visible;
        }
        else
        {
            UpdatesBar.Visibility = Visibility.Collapsed;
            return;
        }

        // SetResourceReference: a faixa continua acompanhando o tema se o Windows trocar de claro/escuro.
        UpdatesBar.SetResourceReference(Border.BackgroundProperty, warn ? "WarnBg" : "InfoBarBg");
        UpdatesBar.SetResourceReference(Border.BorderBrushProperty, warn ? "WarnBorder" : "InfoBarBorder");
        UpdatesText.SetResourceReference(TextBlock.ForegroundProperty, warn ? "WarnText" : "InfoBarText");
    }

    // ---------- Busca, estados da lista e rodapé ----------

    private bool MatchesSearch(CatalogItemVm vm)
    {
        var term = SearchBox.Text.Trim();
        return term.Length == 0
               || vm.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)
               || vm.Description.Contains(term, StringComparison.CurrentCultureIgnoreCase)
               || vm.Dto.PackageId.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_catalogView is null) return;
        _catalogView.Refresh();
        RefreshListState();
        RefreshFooter();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync(forceInventory: true);

    private void StateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_listState == ListState.NoResults) SearchBox.Clear();
        else if (_listState == ListState.Error) _ = ReloadAsync();
    }

    /// <summary>Lista vazia x busca sem resultado x lista normal (não mexe em "carregando" nem em "erro").</summary>
    private void RefreshListState()
    {
        if (_items.Count == 0) SetListState(ListState.Empty);
        else if (!_items.Any(MatchesSearch)) SetListState(ListState.NoResults);
        else SetListState(ListState.None);
    }

    private void SetListState(ListState state, string? message = null)
    {
        _listState = state;
        CatalogScroll.Visibility = state == ListState.None ? Visibility.Visible : Visibility.Collapsed;
        StatePanel.Visibility = state == ListState.None ? Visibility.Collapsed : Visibility.Visible;
        StateProgress.Visibility = state == ListState.Loading ? Visibility.Visible : Visibility.Collapsed;
        StateButton.Visibility = state is ListState.NoResults or ListState.Error ? Visibility.Visible : Visibility.Collapsed;

        switch (state)
        {
            case ListState.Loading:
                StateTitle.Text = "Carregando aplicativos…";
                StateMessage.Text = "Aguarde um instante.";
                break;
            case ListState.Empty:
                StateTitle.Text = "Nenhum aplicativo liberado para você ainda";
                StateMessage.Text = "Se precisar de algum programa, abra um chamado para a TI.";
                break;
            case ListState.NoResults:
                StateTitle.Text = $"Nenhum aplicativo encontrado para “{SearchBox.Text.Trim()}”";
                StateMessage.Text = "Confira o nome ou tente outra palavra.";
                StateButton.Content = "Limpar busca";
                break;
            case ListState.Error:
                StateTitle.Text = "Não foi possível carregar a loja";
                StateMessage.Text = message ?? "";
                StateButton.Content = "Tentar de novo";
                break;
        }
    }

    /// <summary>
    /// Rodapé: quantos aplicativos (com o plural certo) e, só quando a coleta de fato terminou, a hora dela.
    /// Sem coleta concluída não se mostra hora nenhuma.
    /// </summary>
    private void RefreshFooter()
    {
        if (_catalog is null) return;

        var total = _items.Count;
        var term = SearchBox.Text.Trim();
        string count;
        if (total == 0)
            count = "Nenhum aplicativo disponível para você";
        else if (term.Length == 0)
            count = total == 1 ? "1 aplicativo disponível para você" : $"{total} aplicativos disponíveis para você";
        else
            count = $"{_items.Count(MatchesSearch)} de {total} {(total == 1 ? "aplicativo disponível" : "aplicativos disponíveis")} para você";

        SetStatus(count + CheckSuffix());
    }

    private string CheckSuffix()
    {
        if (!_machineOk || _catalog is null) return "";
        if (_inventoryPending) return " · Verificando os programas instalados…";

        if (_catalog.InventoryUtc is { } at)
        {
            var local = at.ToLocalTime();
            var when = local.Date == DateTime.Today ? $"às {local:HH:mm}" : $"em {local:dd/MM} às {local:HH:mm}";
            // Sem ProgramsUtc o script do computador ainda só informa os pacotes da loja, não os programas do Windows.
            return _catalog.ProgramsUtc is null ? $" · Aplicativos da loja verificados {when}" : $" · Programas verificados {when}";
        }

        return _inventoryFailed ? " · Não foi possível verificar os programas instalados agora" : " · Programas instalados ainda não verificados";
    }

    // ---------- Painel de administração (abre no navegador) e atalhos ----------

    private void OpenAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(new Uri(_api.BaseAddress, "admin").AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBanner($"Não foi possível abrir o painel de administração no navegador: {ex.Message}");
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            _ = ReloadAsync(forceInventory: true);
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Tabs.SelectedIndex = 0;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0)
        {
            e.Handled = true;
            SearchBox.Clear();
        }
    }

    // ---------- Utilitários ----------

    private void ShowBanner(string? message)
    {
        Banner.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = message ?? "";
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
