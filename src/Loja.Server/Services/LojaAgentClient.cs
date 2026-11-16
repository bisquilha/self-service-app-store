using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Loja.Server.Data;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

/// <summary>
/// Avisa o agente (que está esperando na conexão aberta) de que há trabalho novo, para ele não depender só do intervalo.
/// Um semáforo por computador; funciona porque o servidor roda em um único processo.
/// </summary>
public class AgentSignals
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _signals = new(StringComparer.OrdinalIgnoreCase);

    public void Notify(string hostname)
    {
        var s = _signals.GetOrAdd(hostname, _ => new SemaphoreSlim(0, 1));
        try { s.Release(); } catch (SemaphoreFullException) { /* já há um aviso pendente */ }
    }

    /// <summary>Espera um aviso por até <paramref name="timeout"/>. True se chegou aviso.</summary>
    public Task<bool> WaitAsync(string hostname, TimeSpan timeout, CancellationToken ct) =>
        _signals.GetOrAdd(hostname, _ => new SemaphoreSlim(0, 1)).WaitAsync(timeout, ct);
}

/// <summary>Quem é o computador que está chamando, a partir do login do Windows (conta PC$).</summary>
public static class AgentIdentity
{
    /// <summary>"DOMINIO\PC-HQ-521$" vira "PC-HQ-521". Contas de usuário (sem "$" no fim) viram null.</summary>
    public static string? MachineName(ClaimsPrincipal user)
    {
        var name = user.Identity?.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        return name.EndsWith('$') ? name[..^1] : null;
    }

    /// <summary>O computador que autenticou é o mesmo que o pedido diz ser? (Um PC não age por outro.)</summary>
    public static bool Matches(ClaimsPrincipal user, string hostname, bool trustUserInDevelopment, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(hostname) || user.Identity?.IsAuthenticated != true) return false;
        if (MachineName(user) is { } machine)
            return string.Equals(machine, hostname, StringComparison.OrdinalIgnoreCase);
        // Só para teste local: o desenvolvedor roda o agente como ele mesmo.
        return trustUserInDevelopment && isDevelopment;
    }
}

/// <summary>
/// Faz o papel do Tactical usando o agente próprio: as máquinas vêm dos batimentos e o "script" vira um trabalho na fila.
/// O identificador de agente é "agent:NOME-DO-PC" (o do Tactical é um código sem esse prefixo).
/// </summary>
public class LojaAgentClient(AgentRepository agents, RequestRepository requests, AgentSignals signals, IOptions<LojaOptions> options) : ITacticalClient
{
    public const string IdPrefix = "agent:";

    public Task<IReadOnlyList<TacticalAgent>> GetAgentsAsync(CancellationToken ct)
    {
        var online = TimeSpan.FromSeconds(Math.Max(30, options.Value.Agent.OnlineSeconds));
        IReadOnlyList<TacticalAgent> list = agents.List().Select(a => new TacticalAgent(
            IdPrefix + a.Hostname,
            a.Hostname,
            // O agente informa o tipo do Windows; qualquer coisa diferente de estação (1) é tratada como servidor.
            a.ProductType == 1 ? "workstation" : "server",
            DateTime.UtcNow - a.LastSeenUtc <= online ? "online" : "offline",
            a.LoggedUser,
            null,
            null,
            a.OsCaption,
            a.LastSeenUtc.ToString("o", CultureInfo.InvariantCulture))).ToList();
        return Task.FromResult(list);
    }

    public Task RunScriptAsync(string agentId, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!agentId.StartsWith(IdPrefix, StringComparison.Ordinal)) throw new InvalidOperationException("Identificador de agente inválido.");
        var hostname = agentId[IdPrefix.Length..];

        // O pedido já foi gravado pelo InstallService; o token (só neste momento em texto puro) serve para achá-lo.
        var token = parameters.GetValueOrDefault("Token") ?? "";
        var row = Tokens.IsWellFormed(token) ? requests.FindRunningByToken(Tokens.Hash(token)) : null;
        if (row is null) throw new InvalidOperationException("Pedido não encontrado para enfileirar.");

        agents.Enqueue(row.Id, hostname, parameters.GetValueOrDefault("Action") ?? "", parameters.GetValueOrDefault("Package"),
            parameters.GetValueOrDefault("Source"), Math.Max(30, options.Value.Agent.JobTimeoutSeconds));
        signals.Notify(hostname);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Usa o agente nas máquinas que o têm instalado e o Tactical nas demais (migração por ondas).
/// Com o mesmo nome nos dois, vale o agente se estiver online; senão o Tactical, se estiver online.
/// </summary>
public class HybridExecutor(ITacticalClient? tactical, LojaAgentClient agent, ILogger<HybridExecutor> log) : ITacticalClient
{
    public async Task<IReadOnlyList<TacticalAgent>> GetAgentsAsync(CancellationToken ct)
    {
        var own = await agent.GetAgentsAsync(ct);
        if (tactical is null) return own;

        IReadOnlyList<TacticalAgent> other;
        try
        {
            other = await tactical.GetAgentsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Tactical fora do ar não derruba quem já usa o agente.
            log.LogWarning("Tactical indisponível; seguindo só com o agente: {Error}", ex.Message);
            return own;
        }

        static bool IsOnline(TacticalAgent a) => string.Equals(a.Status, "online", StringComparison.OrdinalIgnoreCase);
        var ownByHost = own.ToDictionary(a => a.Hostname, StringComparer.OrdinalIgnoreCase);
        var result = new List<TacticalAgent>();

        foreach (var t in other)
        {
            if (ownByHost.TryGetValue(t.Hostname, out var a))
            {
                // Já tem agente: o do Tactical só entra se o agente estiver offline e o Tactical online (evita duplicar o nome).
                if (!IsOnline(a) && IsOnline(t)) { result.Add(t); ownByHost.Remove(t.Hostname); }
                continue;
            }
            result.Add(t);
        }
        result.AddRange(ownByHost.Values);
        return result;
    }

    public Task RunScriptAsync(string agentId, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (agentId.StartsWith(LojaAgentClient.IdPrefix, StringComparison.Ordinal)) return agent.RunScriptAsync(agentId, parameters, ct);
        if (tactical is null) throw new InvalidOperationException("O Tactical não está configurado.");
        return tactical.RunScriptAsync(agentId, parameters, ct);
    }
}
