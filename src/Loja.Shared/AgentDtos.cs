namespace Loja.Shared;

// Contrato entre o servidor da loja e o agente (serviço instalado nas estações).
// O agente sempre chama o servidor; o servidor nunca abre conexão com a estação.

/// <summary>Batimento do agente (a cada ~60 s). RunningJobs = pedidos que o agente está executando agora.</summary>
public record AgentHeartbeatDto(
    string Hostname,
    string AgentVersion,
    string? OsCaption,
    int OsBuild,
    // 1 = estação, 2 = controlador de domínio, 3 = servidor (Win32_OperatingSystem.ProductType).
    int ProductType,
    string? LoggedUser,
    long[]? RunningJobs);

/// <summary>
/// Resposta ao batimento: de quanto em quanto tempo bater de novo e quais pedidos devem ser cancelados.
/// ConfigVersion é o número da versão da configuração no servidor: se for diferente do que o agente tem, ele busca a política nova.
/// </summary>
public record AgentHeartbeatResponse(int PollSeconds, long[] CancelJobs, long ConfigVersion = 0);

/// <summary>
/// O que o servidor manda cada agente fazer, decidido no painel (Configurações). O agente guarda só o mínimo para chegar
/// ao servidor; o resto vem daqui e vale para todos os agentes já instalados.
/// </summary>
public record AgentPolicyDto(
    long Version,
    bool ChocolateyEnabled,
    string CommunityFeed,
    bool InternalAppsEnabled,
    string InternalFeed,
    int PollSeconds,
    int JobTimeoutSeconds);

/// <summary>Um trabalho para o agente executar. O mesmo número do pedido (RequestId) volta no resultado.</summary>
public record AgentJobDto(long Id, string Action, string? Package, string? Source, int TimeoutSeconds);

/// <summary>Resultado de um trabalho. Os campos de inventário são opcionais (como no /api/callback).</summary>
public record AgentResultDto(string Hostname, int ExitCode, string? Output, string? Installed, string? InstalledWindows);

/// <summary>Códigos de saída que o agente usa além dos do Chocolatey e do Windows Installer.</summary>
public static class AgentExitCodes
{
    /// <summary>Passou do tempo máximo e a árvore de processos foi encerrada.</summary>
    public const int TimedOut = 124;

    /// <summary>Cancelado a pedido do servidor.</summary>
    public const int Cancelled = 125;

    public const int InvalidAction = 90;
    public const int InvalidPackage = 91;
    public const int InvalidSource = 92;
    public const int ChocolateyMissing = 93;

    /// <summary>O agente recusou rodar: o computador é Windows Server ou controlador de domínio.</summary>
    public const int NotAWorkstation = 94;

    /// <summary>A fonte do pacote (Chocolatey ou interna) está desligada nas Configurações.</summary>
    public const int SourceDisabled = 95;
}
