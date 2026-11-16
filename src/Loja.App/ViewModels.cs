using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Loja.Shared;

namespace Loja.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    protected void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static class Palette
{
    // Fundos das letras iniciais (texto branco por cima): todos com contraste de pelo menos 4,5:1.
    public static readonly Brush[] Avatars =
        new[] { "#1F5FBF", "#0E7C66", "#7D3C98", "#A84300", "#B3261E", "#2C3E50", "#1E7A44", "#7A5C00" }.Select(Make).ToArray();

    public static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

internal static class Pt
{
    /// <summary>"1 aplicativo" / "3 aplicativos": o plural certo, sem "(s)".</summary>
    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}

internal static class Labels
{
    public static string Status(string status) => status switch
    {
        RequestStatus.Running => "Em andamento",
        RequestStatus.Success => "Concluído",
        RequestStatus.Failed => "Falhou",
        RequestStatus.Denied => "Negado",
        RequestStatus.Timeout => "Sem retorno",
        _ => status,
    };

    public static string Source(string source) => source == PackageSources.Internal ? "Interno" : "Internet";

    public static string Action(string action) => action switch
    {
        RequestActions.Upgrade => "Atualizar",
        RequestActions.Uninstall => "Desinstalar",
        _ => "Instalar",
    };

    public static string Running(string? action) => action switch
    {
        RequestActions.Upgrade => "Atualizando…",
        RequestActions.Uninstall => "Desinstalando…",
        _ => "Instalando…",
    };

    public static string Verb(string? action) => action switch
    {
        RequestActions.Upgrade => "atualizar",
        RequestActions.Uninstall => "desinstalar",
        _ => "instalar",
    };
}

/// <summary>
/// Card de um app. Quem decide o estado da versão é o servidor (CatalogItemDto.VersionState); o app só mostra:
/// não instalado (Instalar), instalado em dia, atualização disponível (Atualizar), instalado com versão que não dá para
/// comparar (não se afirma "em dia"), instalado fora da loja (sem nenhuma ação) e, por cima de tudo, o pedido em andamento
/// ou o último erro. Cada card tem sempre as mesmas três faixas: situação, versões e botões.
/// </summary>
public sealed class CatalogItemVm(CatalogItemDto dto) : ObservableObject
{
    private CatalogItemDto _dto = dto;
    private ImageSource? _icon;
    private string? _runningAction;
    private bool _sending;
    private string? _failText;
    private string? _failDetail;
    private bool _machineOk;
    private string? _machineMessage;
    private string? _lockReason;

    public CatalogItemDto Dto => _dto;
    public int Id => Dto.Id;
    public string Title => Dto.Title;
    public string Description => Dto.Description ?? "";
    public string Initial => string.IsNullOrEmpty(Dto.Title) ? "?" : Dto.Title[..1].ToUpperInvariant();
    public Brush AvatarBrush { get; } = Palette.Avatars[dto.PackageId.Sum(c => c) % Palette.Avatars.Length];
    public long? ActiveRequestId { get; private set; }

    /// <summary>Resultado do último pedido que terminou (RequestStatus.*). Usado pelo "Atualizar todos".</summary>
    public string? LastStatus { get; private set; }

    /// <summary>Texto do último erro, e o código HTTP quando foi o servidor que recusou (409 = já há um pedido rodando).</summary>
    public string? LastError { get; private set; }

    public int? LastErrorCode { get; private set; }

    /// <summary>Avisa a janela que o pedido deste card mudou (ela recalcula o que depende de "há algo em andamento").</summary>
    public event Action? RequestChanged;

    // ---------- Versões ----------

    public string Subtitle => Labels.Source(Dto.Source) + (string.IsNullOrWhiteSpace(Dto.Version) ? " · versão não informada" : $" · versão disponível {Dto.Version}");

    public string State => Dto.VersionState
                           ?? (Dto.UpdateAvailable ? VersionStates.Update : Dto.InstalledVersion is null ? VersionStates.None : VersionStates.Current);

    /// <summary>Instalado pela loja (Chocolatey). Programa de fora da loja NÃO conta: nunca libera atualizar nem desinstalar.</summary>
    public bool IsInstalled => Dto.InstalledVersion is not null;

    public bool IsOutside => State == VersionStates.External;
    public bool CanUpdate => Dto.UpdateAvailable;

    // ---------- Pedido em andamento ----------

    public bool IsBusy => _runningAction is not null;

    /// <summary>
    /// Motivo de os botões deste card estarem travados por causa de OUTRO pedido (o servidor só aceita um por computador
    /// por vez), ou null quando não há. Quem define é a janela.
    /// </summary>
    public string? LockReason
    {
        get => _lockReason;
        set { if (Set(ref _lockReason, value)) RaiseState(); }
    }

    public bool OtherBusy => _lockReason is not null;

    // ---------- Botões ----------

    /// <summary>Ação do botão principal: atualizar se houver versão nova, senão instalar.</summary>
    public string PrimaryAction => Dto.UpdateAvailable ? RequestActions.Upgrade : RequestActions.Install;

    public bool ShowPrimary => IsBusy || (!IsOutside && (Dto.UpdateAvailable || !IsInstalled));
    public string PrimaryText => IsBusy ? Labels.Running(_runningAction) : Dto.UpdateAvailable ? "Atualizar" : "Instalar";
    public bool PrimaryEnabled => MachineOk && !IsBusy && !OtherBusy;
    public bool ShowUninstall => !IsBusy && IsInstalled && Dto.AllowUninstall;
    public bool UninstallEnabled => MachineOk && !OtherBusy;

    public string PrimaryAutomationName => IsBusy ? $"{Title}: {Labels.Running(_runningAction)}" : $"{(Dto.UpdateAvailable ? "Atualizar" : "Instalar")} {Title}";
    public string UninstallAutomationName => $"Desinstalar {Title}";

    /// <summary>Por que os botões estão desligados (aparece como dica); vazio quando estão ligados.</summary>
    public string? DisabledReason => !MachineOk
        ? (string.IsNullOrWhiteSpace(_machineMessage) ? "Este computador não está liberado para usar a loja." : _machineMessage)
        : !IsBusy ? _lockReason : null;

    // ---------- Situação e versões (duas linhas, sempre) ----------

    public string StatusKind => View().Kind;
    public string StatusGlyph => View().Glyph;
    public string StatusText => View().Text;
    public string DetailText => View().Detail;
    public string StatusTip { get { var v = View(); return string.IsNullOrEmpty(v.Tip) ? v.Text : v.Tip; } }

    /// <summary>Mostra a faixa "em andamento" no card.</summary>
    public bool IsWorking => IsBusy;

    private (string Kind, string Glyph, string Text, string Detail, string Tip) View()
    {
        if (_sending) return ("info", "●", "Enviando pedido…", "Aguarde um instante.", "");
        if (IsBusy) return ("info", "●", Labels.Running(_runningAction), "Pode continuar usando o computador.", "");
        if (_failText is not null) return ("bad", "✕", _failText, _failDetail ?? "", _failDetail ?? "");

        var installed = ShowVersion(Dto.InstalledVersion);
        var available = ShowVersion(Dto.Version);
        switch (State)
        {
            case VersionStates.Current:
                return ("ok", "✓", "Instalado · Em dia", $"Versão instalada: {installed}", "");
            case VersionStates.Update:
                return ("info", "↑", "Atualização disponível", $"{installed} → {available}", $"Versão instalada: {installed}. Versão disponível: {available}.");
            case VersionStates.Newer:
                return ("muted", "✓", "Instalado · Mais nova que a da loja", $"Versão instalada: {installed} (loja: {available})",
                    "A versão instalada é mais nova que a da loja. Nada a fazer.");
            case VersionStates.Unknown:
                return ("warn", "?", "Instalado · Versão não conferida", $"Instalada: {installed} · Na loja: {available}",
                    "Não foi possível comparar a versão instalada com a da loja, então não dá para dizer se está em dia.");
            case VersionStates.External:
                var outside = string.IsNullOrWhiteSpace(Dto.ExternalVersion) ? "Versão instalada não informada" : $"Versão instalada: {Dto.ExternalVersion}";
                var name = string.IsNullOrWhiteSpace(Dto.ExternalName) ? "" : $" (\"{Dto.ExternalName}\")";
                return ("muted", "✓", "Já instalado no computador", outside,
                    $"Este programa já estava no computador{name}; não veio pela loja. A loja não o atualiza nem o desinstala.");
            default:
                return ("muted", "○", "Não instalado", "", "");
        }
    }

    private static string ShowVersion(string? version) => string.IsNullOrWhiteSpace(version) ? "não informada" : version.Trim();

    public bool ShowOutsideBadge => IsOutside && !IsBusy && _failText is null;

    // ---------- Ícone e máquina ----------

    public ImageSource? Icon
    {
        get => _icon;
        set { if (Set(ref _icon, value)) OnChanged(nameof(ShowAvatar)); }
    }

    public bool ShowAvatar => Icon is null;

    public bool MachineOk
    {
        get => _machineOk;
        set { if (Set(ref _machineOk, value)) RaiseState(); }
    }

    public string? MachineMessage
    {
        get => _machineMessage;
        set { if (Set(ref _machineMessage, value)) OnChanged(nameof(DisabledReason)); }
    }

    /// <summary>Recebe os dados novos do servidor (ex.: depois que a lista de instalados foi atualizada).</summary>
    public void Update(CatalogItemDto dto)
    {
        _dto = dto;
        OnChanged(nameof(Subtitle));
        OnChanged(nameof(Description));
        RaiseState();
    }

    public void ShowError(string message, int? code = null)
    {
        ActiveRequestId = null;
        _runningAction = null;
        _sending = false;
        _failText = "Não foi possível enviar o pedido";
        _failDetail = message;
        LastError = message;
        LastErrorCode = code;
        RaiseState();
        RequestChanged?.Invoke();
    }

    public void Apply(RequestDto request)
    {
        var running = request.Status == RequestStatus.Running;
        ActiveRequestId = running ? request.Id : null;
        _runningAction = running ? request.Action : null;
        _sending = false;
        LastErrorCode = null;
        if (!running) LastStatus = request.Status;

        // Concluído: o card passa a mostrar o estado novo (instalado / em dia) no lugar da mensagem do pedido.
        _failText = running || request.Status == RequestStatus.Success
            ? null
            : request.Status switch
            {
                RequestStatus.Failed => $"Falha ao {Labels.Verb(request.Action)}",
                RequestStatus.Denied => "Pedido negado",
                RequestStatus.Timeout => "O computador não respondeu",
                _ => Labels.Status(request.Status),
            };
        _failDetail = _failText is null ? null : string.IsNullOrWhiteSpace(request.Message) ? "Veja os detalhes em Meus pedidos." : request.Message;
        LastError = _failDetail;
        RaiseState();
        RequestChanged?.Invoke();
    }

    public void MarkSending(string action)
    {
        _runningAction = action;
        _sending = true;
        _failText = null;
        _failDetail = null;
        LastStatus = null;
        LastError = null;
        LastErrorCode = null;
        RaiseState();
        RequestChanged?.Invoke();
    }

    private void RaiseState()
    {
        foreach (var name in new[]
                 {
                     nameof(State), nameof(IsInstalled), nameof(IsOutside), nameof(CanUpdate), nameof(IsBusy), nameof(IsWorking),
                     nameof(PrimaryAction), nameof(ShowPrimary), nameof(PrimaryText), nameof(PrimaryEnabled), nameof(PrimaryAutomationName),
                     nameof(ShowUninstall), nameof(UninstallEnabled), nameof(UninstallAutomationName), nameof(DisabledReason),
                     nameof(StatusKind), nameof(StatusGlyph), nameof(StatusText), nameof(DetailText), nameof(StatusTip), nameof(ShowOutsideBadge),
                 })
            OnChanged(name);
    }
}

public sealed class RequestRowVm(RequestDto dto)
{
    public RequestDto Dto { get; } = dto;
    public string When => Dto.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public string Title => Dto.Title;
    public string Hostname => Dto.Hostname;
    public string User => Dto.User;
    public string ActionText => Labels.Action(Dto.Action);
    public string StatusText => Labels.Status(Dto.Status);
    public string Message => Dto.Message ?? "";
    public string Output => Dto.Output ?? "";
}
