namespace Loja.Shared;

// Contrato da API entre o servidor da loja e o app instalado nas máquinas.

public static class RequestStatus
{
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Denied = "denied";
    public const string Timeout = "timeout";
}

public static class RequestActions
{
    public const string Install = "install";
    public const string Upgrade = "upgrade";
    public const string Uninstall = "uninstall";

    /// <summary>Só consulta os pacotes instalados na máquina (não aparece nos históricos).</summary>
    public const string List = "list";

    /// <summary>Ações que um usuário pode pedir para um app.</summary>
    public static bool IsUserAction(string? action) => action is Install or Upgrade or Uninstall;
}

public static class PackageSources
{
    /// <summary>Repositório da comunidade Chocolatey (internet), o mesmo que o Tactical usa.</summary>
    public const string Community = "community";

    /// <summary>Feed interno (pasta no NAS com pacotes .nupkg).</summary>
    public const string Internal = "internal";

    public static bool IsValid(string? source) => source is Community or Internal;
}

public record MeDto(string User, bool IsAdmin, string Hostname, bool MachineOk, string? MachineMessage);

/// <summary>
/// Estado de um aplicativo na máquina (CatalogItemDto.VersionState). O servidor é quem decide; o app só mostra.
/// "update" = instalada menor que a disponível; "current" = iguais; "newer" = instalada maior (nunca se oferece downgrade);
/// "unknown" = há versão instalada, mas uma das duas não dá para comparar (não se afirma "em dia");
/// "external" = não veio pela loja, mas um programa com o nome cadastrado para ele foi achado no Windows.
/// </summary>
public static class VersionStates
{
    public const string None = "none";
    public const string Current = "current";
    public const string Update = "update";
    public const string Newer = "newer";
    public const string Unknown = "unknown";
    public const string External = "external";
}

/// <summary>
/// Aplicativo visto de uma máquina. UpdateAvailable e InstalledVersion continuam como na 1.2.0 (só pacotes do Chocolatey).
/// Os quatro últimos campos são da 1.6.0 e opcionais: o app 1.2.0 ignora o que não conhece.
/// OutsideStore: já está no computador sem ter vindo pela loja (nome conferido pelo cadastro da TI); isso não libera atualizar nem desinstalar.
/// </summary>
public record CatalogItemDto(
    int Id,
    string PackageId,
    string Source,
    string Title,
    string? Description,
    string? Version,
    bool HasIcon,
    string? InstalledVersion,
    bool UpdateAvailable,
    bool AllowUninstall,
    string? VersionState = null,
    bool OutsideStore = false,
    string? ExternalVersion = null,
    string? ExternalName = null);

/// <summary>
/// Catálogo visto de uma máquina: inclui o que já está instalado nela. ProgramsUtc = quando os programas do Windows foram
/// coletados pela última vez (vazio se o script ainda não os envia: então não se afirma nada sobre programas fora da loja).
/// </summary>
public record CatalogDto(List<CatalogItemDto> Items, DateTime? InventoryUtc, bool InventoryRunning, DateTime? ProgramsUtc = null);

/// <summary>Pedido do usuário. Action: install (padrão), upgrade ou uninstall.</summary>
public record InstallRequestDto(int AppId, string Hostname, string? Action = null);

public record RequestDto(
    long Id,
    int? AppId,
    string PackageId,
    string Title,
    string Hostname,
    string User,
    string Action,
    string Status,
    string? Message,
    DateTime CreatedUtc,
    DateTime? FinishedUtc,
    string? Output,
    int? ExitCode = null,
    // 1.6.0: número do pedido que esta execução repete ("Reexecutar" do painel). Vazio nos demais.
    long? RetryOf = null);

/// <summary>
/// Grupo do AD ligado a um aplicativo: o SID é a identidade estável (sobrevive a renomear o grupo) e o nome serve para
/// exibição. "Todos os usuários" é o caso especial Name = "*", sem SID.
/// </summary>
public record GroupRefDto(string Name, string? Sid = null);

public record AdminAppDto(
    int Id,
    string PackageId,
    string Source,
    string Title,
    string? Description,
    string? Version,
    bool Enabled,
    bool AutoUpdate,
    bool AllowUninstall,
    // Nomes atuais dos grupos (campo antigo: o app 1.2.0 e anteriores só leem este).
    List<string> Groups,
    // Grupos salvos que deixaram de existir no AD (apagados). Vazio quando está tudo certo.
    List<string>? MissingGroups = null,
    // Os mesmos grupos com o SID de cada um (painel 1.6.0 em diante).
    List<GroupRefDto>? GroupRefs = null,
    // Nomes que o programa tem em "Programas e Recursos" do Windows (cadastro da TI, 1.6.0 em diante).
    List<string>? Aliases = null);

/// <summary>Groups (nomes) continua valendo; se GroupRefs vier preenchido, ele tem prioridade.</summary>
public record AdminAppCreateDto(string PackageId, string Source, List<string> Groups, List<GroupRefDto>? GroupRefs = null);

/// <summary>Reason = "undo" quando a alteração é um "Desfazer" do painel (a auditoria marca como revertida).</summary>
public record AdminAppUpdateDto(List<string> Groups, bool Enabled, bool AutoUpdate, bool AllowUninstall, List<GroupRefDto>? GroupRefs = null, string? Reason = null);

public record PackageSearchResultDto(
    string PackageId,
    string Source,
    string Title,
    string? Version,
    string? Description,
    long DownloadCount,
    bool IsApproved,
    bool InStore);

public record StatusCheckDto(string Name, bool Ok, string Detail);

public record AdminStatusDto(string ServerVersion, List<string> AdminGroups, List<StatusCheckDto> Checks);

/// <summary>
/// Enviado pelo script do Tactical ao terminar. Installed: pacotes do Chocolatey na máquina,
/// uma linha "pacote|versão" por pacote (texto simples, para não depender do ConvertTo-Json do PowerShell 5.1).
/// InstalledWindows (1.6.0, opcional): programas das chaves Uninstall do registro (64 e 32 bits), uma linha "nome&lt;TAB&gt;versão"
/// por programa. Ausente (script antigo) = o servidor não mexe na lista de programas e não afirma nada sobre eles.
/// </summary>
public record CallbackDto(string Token, int ExitCode, string? Output, string? Installed = null, string? InstalledWindows = null);

public record ErrorDto(string Message);

/// <summary>Versão do app da loja que o servidor manda usar (autoupdate).</summary>
public record ClientLatestDto(string Version, string Sha256, long Size, bool Mandatory);
