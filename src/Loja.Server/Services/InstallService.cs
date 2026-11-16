using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Loja.Server.Data;
using Loja.Shared;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

public record MachineCheck(bool Ok, string? Message, TacticalAgent? Agent);

public record InstallResult(RequestRow? Request, string? Error, int StatusCode);

/// <summary>Token de uso único que o script devolve no /api/callback. No banco fica só o hash.</summary>
public static class Tokens
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static bool IsWellFormed(string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit);

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token.ToUpperInvariant())));
}

public partial class InstallService(
    ITacticalClient tactical,
    AppRepository apps,
    RequestRepository requests,
    InventoryRepository inventory,
    AccessService access,
    ConfigService config,
    IOptions<LojaOptions> options,
    ILogger<InstallService> log)
{
    /// <summary>Nome gravado no histórico para o que a própria loja dispara (atualização automática).</summary>
    public const string AutomaticUser = "(automático)";

    private static readonly object Gate = new();

    [GeneratedRegex("^[A-Za-z0-9-]{1,63}$")]
    private static partial Regex HostnameRegex();

    /// <summary>
    /// Confere a máquina antes de qualquer ação: existe no Tactical, é estação (nunca servidor),
    /// está online e, para pedidos de usuário, quem pediu é quem está logado nela.
    /// </summary>
    public async Task<MachineCheck> CheckMachineAsync(string user, string hostname, CancellationToken ct, bool requireLoggedUser = true)
    {
        if (!HostnameRegex().IsMatch(hostname ?? "")) return new(false, "Nome de computador inválido.", null);

        IReadOnlyList<TacticalAgent> agents;
        try
        {
            agents = await tactical.GetAgentsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Falha ao consultar os agentes no Tactical");
            return new(false, "Não foi possível consultar o Tactical RMM agora. Tente novamente em alguns minutos.", null);
        }

        var matches = agents.Where(a => string.Equals(a.Hostname, hostname, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return new(false, "Este computador não foi encontrado no Tactical RMM. Procure a TI.", null);

        var agent = PickAgent(matches);
        if (agent is null)
            return new(false, "Há mais de um computador online com este nome no Tactical RMM. Procure a TI.", null);
        var opt = options.Value;
        if (BlockReason(agent) is not null)
            return new(false, "A loja só instala aplicativos em estações de trabalho.", agent);

        if (!string.Equals(agent.Status, "online", StringComparison.OrdinalIgnoreCase))
            return new(false, "Este computador aparece offline no Tactical RMM. Tente novamente em alguns minutos.", agent);

        if (requireLoggedUser && opt.RequireLoggedUser
            && !string.Equals(AccessService.ShortName(agent.LoggedUser), AccessService.ShortName(user), StringComparison.OrdinalIgnoreCase))
            return new(false, "O Tactical RMM ainda não mostra você como usuário logado neste computador. Aguarde alguns minutos e clique em \"Atualizar lista\".", agent);

        return new(true, null, agent);
    }

    /// <summary>
    /// Escolhe o agente quando o Tactical tem mais de um com o mesmo nome (agente reinstalado e o registro
    /// antigo esquecido). Um só: ele. Vários: o único online; se nenhum ou mais de um estiver online,
    /// devolve o primeiro offline (para mostrar "offline") ou null (ambíguo: pode ser outro PC com o mesmo nome).
    /// </summary>
    public static TacticalAgent? PickAgent(IReadOnlyList<TacticalAgent> sameName)
    {
        if (sameName.Count == 1) return sameName[0];
        var online = sameName.Where(a => string.Equals(a.Status, "online", StringComparison.OrdinalIgnoreCase)).ToList();
        return online.Count switch
        {
            1 => online[0],
            0 => sameName[0],
            _ => null,
        };
    }

    /// <summary>Por que a loja nunca age neste agente (null = é estação permitida). Online/offline não entra aqui.</summary>
    public string? BlockReason(TacticalAgent agent)
    {
        var opt = options.Value;
        if (!opt.AllowedMonitoringTypes.Contains(agent.MonitoringType, StringComparer.OrdinalIgnoreCase))
            return $"Tipo \"{agent.MonitoringType}\" no Tactical";
        if (opt.BlockServerOperatingSystems && string.IsNullOrWhiteSpace(agent.OperatingSystem))
            return "Tactical não informa o sistema";
        if (opt.BlockServerOperatingSystems && agent.IsServerOs)
            return "Windows Server";
        return null;
    }

    /// <summary>
    /// Ação disparada pelo painel de admin numa máquina: instalar, atualizar, desinstalar ou consultar.
    /// Mantém as travas de máquina (estação, não servidor, online), mas não exige o usuário logado.
    /// </summary>
    public async Task<InstallResult> RequestAsAdminAsync(ClaimsPrincipal admin, int? appId, string hostname, string action, CancellationToken ct, long? retryOf = null)
    {
        var adminName = $"{access.UserName(admin)} (painel)";
        hostname = (hostname ?? "").Trim();
        if (action != RequestActions.List && !RequestActions.IsUserAction(action)) return new(null, "Ação inválida.", 400);

        AppRow? app = null;
        if (action != RequestActions.List)
        {
            app = appId is { } id ? apps.Get(id) : null;
            if (app is null) return new(null, "Aplicativo não encontrado.", 404);
            if (action != RequestActions.Uninstall && !config.IsSourceEnabled(app.Source)) return new(null, config.SourceOffMessage(app.Source), 409);
        }

        var check = await CheckMachineAsync(adminName, hostname, ct, requireLoggedUser: false);
        if (!check.Ok) return new(null, check.Message, 409);

        if (action == RequestActions.List)
        {
            lock (Gate)
            {
                if (requests.HasRunningList(hostname)) return new(null, "Já existe uma verificação em andamento nesta máquina.", 409);
            }
        }

        return await StartAsync(adminName, hostname, check.Agent!, app, action, retryOf);
    }

    /// <summary>Instalar, atualizar ou desinstalar um app, a pedido do usuário logado na máquina.</summary>
    public async Task<InstallResult> RequestAsync(ClaimsPrincipal user, int appId, string hostname, string action, CancellationToken ct)
    {
        var userName = access.UserName(user);
        hostname = (hostname ?? "").Trim();
        var safeHost = hostname.Length > 63 ? hostname[..63] : hostname;

        if (!RequestActions.IsUserAction(action)) return new(null, "Ação inválida.", 400);

        var app = apps.Get(appId);
        if (app is null || !app.Enabled) return new(null, "Aplicativo não encontrado.", 404);
        if (!CatalogService.PackageIdRegex().IsMatch(app.PackageId) || !PackageSources.IsValid(app.Source))
            return new(null, "Cadastro do aplicativo inválido. Procure a TI.", 400);

        if (!access.CanUse(user, app))
        {
            LogDenied(userName, safeHost, app, action, "Usuário sem permissão para este aplicativo.");
            return new(null, "Você não tem permissão para este aplicativo.", 403);
        }

        if (action != RequestActions.Uninstall && !config.IsSourceEnabled(app.Source))
        {
            LogDenied(userName, safeHost, app, action, config.SourceOffMessage(app.Source));
            return new(null, "Este aplicativo não está disponível no momento. Procure a TI.", 409);
        }

        if (action == RequestActions.Uninstall && !app.AllowUninstall)
        {
            LogDenied(userName, safeHost, app, action, "Desinstalação não liberada para este aplicativo.");
            return new(null, "A TI não liberou a desinstalação deste aplicativo pela loja.", 403);
        }

        var check = await CheckMachineAsync(userName, hostname, ct);
        if (!check.Ok)
        {
            LogDenied(userName, safeHost, app, action, check.Message!);
            return new(null, check.Message, 409);
        }

        return await StartAsync(userName, hostname, check.Agent!, app, action);
    }

    /// <summary>
    /// Pede ao computador a lista de pacotes instalados. Só dispara se a última lista for antiga
    /// (ou se force = true) e se não houver outra consulta em andamento.
    /// </summary>
    public async Task<(bool Started, string? Error)> RequestInventoryAsync(ClaimsPrincipal user, string hostname, bool force, CancellationToken ct)
    {
        var userName = access.UserName(user);
        hostname = (hostname ?? "").Trim();

        var check = await CheckMachineAsync(userName, hostname, ct);
        if (!check.Ok) return (false, check.Message);

        var maxAge = TimeSpan.FromMinutes(force ? 1 : Math.Max(1, options.Value.InventoryMaxAgeMinutes));
        var current = inventory.Get(hostname);
        if (current.UpdatedUtc is { } updated && DateTime.UtcNow - updated < maxAge) return (false, null);

        lock (Gate)
        {
            if (requests.HasRunningList(hostname)) return (false, null);
        }

        var result = await StartAsync(userName, hostname, check.Agent!, null, RequestActions.List);
        return (result.Error is null, result.Error);
    }

    /// <summary>Atualização automática disparada pela loja (sem usuário logado obrigatório).</summary>
    public async Task<bool> RequestAutoUpdateAsync(AppRow app, string hostname, CancellationToken ct)
    {
        var check = await CheckMachineAsync(AutomaticUser, hostname, ct, requireLoggedUser: false);
        if (!check.Ok) return false;
        var result = await StartAsync(AutomaticUser, hostname, check.Agent!, app, RequestActions.Upgrade);
        return result.Error is null;
    }

    private async Task<InstallResult> StartAsync(string userName, string hostname, TacticalAgent agent, AppRow? app, string action, long? retryOf = null)
    {
        var token = Tokens.New();
        var packageId = app?.PackageId ?? "-";
        var title = app?.Title ?? "Consulta de programas instalados";
        var source = app?.Source ?? PackageSources.Community;

        long id;
        lock (Gate)
        {
            if (action != RequestActions.List && requests.HasRunning(hostname))
                return new(null, "Já existe uma instalação em andamento neste computador. Aguarde terminar.", 409);

            id = requests.Insert(new NewRequest(userName, hostname, agent.AgentId, app?.Id, packageId, title, source,
                action, RequestStatus.Running, retryOf is null ? "Enviado ao computador. Aguardando…" : $"Reexecução do pedido {retryOf}. Aguardando…",
                Tokens.Hash(token), retryOf));
        }

        var parameters = new Dictionary<string, string> { ["Action"] = action, ["Token"] = token };
        if (app is not null)
        {
            parameters["Package"] = app.PackageId;
            parameters["Source"] = app.Source;
        }

        try
        {
            // Sem o token de cancelamento da requisição: se o app fechar agora, o pedido já foi registrado e segue.
            await tactical.RunScriptAsync(agent.AgentId, parameters, CancellationToken.None);
            if (action != RequestActions.List)
                log.LogInformation("Pedido {Id}: {User} → {Action} {Package} em {Host}", id, userName, action, packageId, hostname);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Pedido {Id}: falha ao acionar o Tactical", id);
            requests.Finish(id, RequestStatus.Failed, "Não foi possível acionar o Tactical RMM. Tente novamente ou procure a TI.", null, ex.Message);
        }

        return new(requests.Get(id), null, 200);
    }

    private void LogDenied(string user, string hostname, AppRow app, string action, string reason)
    {
        requests.Insert(new NewRequest(user, hostname, null, app.Id, app.PackageId, app.Title, app.Source, action, RequestStatus.Denied, reason, null));
        log.LogInformation("Negado: {User} → {Action} {Package} em {Host}: {Reason}", user, action, app.PackageId, hostname, reason);
    }
}

/// <summary>Recebe o resultado que o script do Tactical envia ao terminar.</summary>
public class CompletionService(RequestRepository requests, InventoryRepository inventory, ILogger<CompletionService> log)
{
    // 1641 e 3010: deu certo, mas precisa reiniciar.
    private static readonly int[] SuccessCodes = [0, 1641, 3010];
    private const int MaxOutput = 8000;

    public bool Complete(string token, int exitCode, string? output, string? installed = null, string? installedWindows = null)
    {
        if (!Tokens.IsWellFormed(token)) return false;

        var row = requests.FindRunningByToken(Tokens.Hash(token));
        if (row is null)
        {
            log.LogWarning("Retorno recebido com token desconhecido ou já usado");
            return false;
        }

        Apply(row, exitCode, output, installed, installedWindows);
        return true;
    }

    /// <summary>
    /// Conclusão pelo agente da loja: autenticada pelo login do computador (conferido na rota), sem token em texto puro.
    /// Só conclui um pedido que está em andamento e que é desta mesma máquina.
    /// </summary>
    public bool CompleteByRequest(long requestId, string hostname, int exitCode, string? output, string? installed = null, string? installedWindows = null)
    {
        var row = requests.Get(requestId);
        if (row is null || row.Status != RequestStatus.Running || !string.Equals(row.Hostname, hostname, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Resultado do agente recusado: pedido {Id} inexistente, já concluído ou de outra máquina", requestId);
            return false;
        }

        Apply(row, exitCode, output, installed, installedWindows);
        return true;
    }

    private void Apply(RequestRow row, int exitCode, string? output, string? installed, string? installedWindows)
    {
        // O script manda a lista de pacotes depois de qualquer ação: mantém o "instalado / atualizar" em dia.
        if (installed is not null) inventory.Replace(row.Hostname, InventoryRepository.Parse(installed));
        // Programas do Windows (script 1.6.0 em diante). Script antigo não manda o campo: a lista e a data de coleta ficam como estão.
        // (O texto é cortado em 1 MB antes de ler: o token já protege, mas ninguém precisa mandar mais que isso.)
        if (installedWindows is not null)
            inventory.ReplacePrograms(row.Hostname, InventoryRepository.ParsePrograms(installedWindows.Length > 1_000_000 ? installedWindows[..1_000_000] : installedWindows));

        var ok = SuccessCodes.Contains(exitCode);
        var (done, verb) = row.Action switch
        {
            RequestActions.Upgrade => ("Atualizado", "atualização"),
            RequestActions.Uninstall => ("Desinstalado", "desinstalação"),
            RequestActions.List => ("Lista de programas atualizada", "consulta"),
            _ => ("Instalado", "instalação"),
        };
        var message = exitCode switch
        {
            0 => $"{done} com sucesso.",
            1641 or 3010 => $"{done}. Reinicie o computador para concluir.",
            90 or 91 or 92 => "O script da loja recusou o pedido (parâmetro inválido). Procure a TI.",
            93 => "O Chocolatey não está instalado neste computador. Procure a TI.",
            94 => "A loja só instala em estações de trabalho (este computador é um servidor).",
            95 => "Esta fonte de aplicativos foi desligada pela TI. Procure a TI.",
            124 => $"A {verb} passou do tempo máximo e foi encerrada. Tente de novo ou procure a TI.",
            125 => $"A {verb} foi cancelada.",
            _ => $"A {verb} falhou (código {exitCode}). A TI pode ver os detalhes no histórico.",
        };

        if (output?.Length > MaxOutput) output = output[^MaxOutput..];
        requests.Finish(row.Id, ok ? RequestStatus.Success : RequestStatus.Failed, message, exitCode, output);
        if (row.Action != RequestActions.List)
            log.LogInformation("Pedido {Id} ({Action} {Package} em {Host}) terminou com código {Code}", row.Id, row.Action, row.PackageId, row.Hostname, exitCode);
    }
}
