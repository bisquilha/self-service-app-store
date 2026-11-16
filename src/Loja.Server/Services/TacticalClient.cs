using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

public record TacticalAgent(
    string AgentId,
    string Hostname,
    string MonitoringType,
    string Status,
    string? LoggedUser,
    string? Client,
    string? Site,
    string? OperatingSystem,
    // Último contato do agente com o Tactical (texto ISO do campo last_seen). Vazio se o Tactical não informa.
    string? LastSeen = null)
{
    public bool IsServerOs => OperatingSystem?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Tudo que a loja usa do Tactical RMM: listar agentes e rodar UM script. Manter assim pequeno
/// reduz o risco de quebrar quando o Tactical for atualizado.
/// </summary>
public interface ITacticalClient
{
    Task<IReadOnlyList<TacticalAgent>> GetAgentsAsync(CancellationToken ct);

    /// <summary>Roda o script da loja no agente, sem esperar. O resultado chega pelo /api/callback.</summary>
    Task RunScriptAsync(string agentId, IReadOnlyDictionary<string, string> parameters, CancellationToken ct);
}

public class TacticalClient(IHttpClientFactory factory, IOptions<LojaOptions> options) : ITacticalClient
{
    public const string ClientName = "tactical";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<TacticalAgent>? _cache;
    private DateTime _cacheTime;

    public async Task<IReadOnlyList<TacticalAgent>> GetAgentsAsync(CancellationToken ct)
    {
        var ttl = TimeSpan.FromSeconds(options.Value.Tactical.AgentCacheSeconds);
        if (_cache is not null && DateTime.UtcNow - _cacheTime < ttl) return _cache;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is not null && DateTime.UtcNow - _cacheTime < ttl) return _cache;

            var http = factory.CreateClient(ClientName);
            using var resp = await http.GetAsync("agents/", ct);
            await EnsureSuccessAsync(resp, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var agents = new List<TacticalAgent>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                agents.Add(new TacticalAgent(
                    Str(e, "agent_id") ?? "",
                    Str(e, "hostname") ?? "",
                    Str(e, "monitoring_type") ?? "",
                    Str(e, "status") ?? "",
                    // A listagem traz "logged_username"; o detalhe do agente traz "logged_in_username".
                    Str(e, "logged_username", "logged_in_username"),
                    Str(e, "client_name"),
                    Str(e, "site_name"),
                    Str(e, "operating_system"),
                    Str(e, "last_seen")));
            }

            _cache = agents;
            _cacheTime = DateTime.UtcNow;
            return agents;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RunScriptAsync(string agentId, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var t = options.Value.Tactical;
        // Os valores vão como argumentos (-Nome valor) e também como variáveis de ambiente LOJA_NOME.
        // O script prefere as variáveis de ambiente, que não passam pelo parser do PowerShell.
        var body = new
        {
            output = "forget",
            script = t.ScriptId,
            args = parameters.Select(p => $"-{p.Key} {p.Value}").ToArray(),
            env_vars = parameters.Select(p => $"LOJA_{p.Key.ToUpperInvariant()}={p.Value}").ToArray(),
            timeout = t.ScriptTimeoutSeconds,
            run_as_user = false,
        };

        var http = factory.CreateClient(ClientName);
        using var resp = await http.PostAsJsonAsync($"agents/{Uri.EscapeDataString(agentId)}/runscript/", body, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (text.Length > 300) text = text[..300];
        throw new HttpRequestException($"Tactical respondeu {(int)resp.StatusCode}: {text}");
    }

    private static string? Str(JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        }
        return null;
    }
}

/// <summary>
/// Tactical simulado para desenvolvimento: um agente com o nome deste PC (estação, online, com você logado),
/// um servidor e uma estação offline. A "instalação" termina em 6 s; pacotes com "falha" no nome falham.
/// Guarda em memória o que foi "instalado": install grava a versão 1.0 (para aparecer "Atualizar"),
/// upgrade grava 9999.0 e uninstall remove — assim dá para testar todos os estados do card.
/// </summary>
public class FakeTacticalClient(CompletionService completion, IOptions<LojaOptions> options, ILogger<FakeTacticalClient> log) : ITacticalClient
{
    private readonly Dictionary<string, Dictionary<string, string>> _installed = new(StringComparer.OrdinalIgnoreCase);

    // Programas do Windows do PC simulado (não vieram da loja, nem do Chocolatey). Servem para testar "Já instalado no computador · Fora da loja"
    // depois de cadastrar o nome ("VLC media player", por exemplo) em um aplicativo. Texto "nome<TAB>versão", como o script real manda.
    private const string FakeWindowsPrograms =
        "Microsoft Edge\t141.0.3537.57\nVLC media player\t3.0.20\nNotepad++ (64-bit x64)\t8.7.1\nFoxit PDF Reader\t\n7-Zip 24.09 (x64)\t24.09";

    /// <summary>Último contato simulado: "agora" ou alguns dias atrás.</summary>
    private static string Seen(int daysAgo) => DateTime.UtcNow.AddDays(-daysAgo).ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    public Task<IReadOnlyList<TacticalAgent>> GetAgentsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TacticalAgent>>(new[]
        {
            new TacticalAgent("mock-" + Environment.MachineName, Environment.MachineName, "workstation", "online", Environment.UserName, "Simulado", "Simulado", "Windows 11 Pro, 64 bit (build 26200)", Seen(0)),
            new TacticalAgent("mock-servidor", "SRV-EXEMPLO", "server", "online", Environment.UserName, "Simulado", "Simulado", "Windows Server 2022 Standard, 64 bit (build 20348)", Seen(0)),
            // Servidor cadastrado como estação (como acontece em instalações reais do Tactical): barrado pelo SO.
            new TacticalAgent("mock-disfarcado", "SRV-COMO-ESTACAO", "workstation", "online", Environment.UserName, "Simulado", "Simulado", "Windows Server 2019 Standard, 64 bit (build 17763)", Seen(0)),
            new TacticalAgent("mock-offline", "PC-OFFLINE", "workstation", "offline", null, "Simulado", "Simulado", "Windows 10 Pro, 64 bit (build 19045)", Seen(10)),
            // Agente reinstalado com o registro antigo esquecido (como acontece na prática): a loja usa o online.
            new TacticalAgent("mock-dup-antigo", "PC-DUPLICADO", "workstation", "overdue", Environment.UserName, "Simulado", "Simulado", "Windows 11 Pro, 64 bit (build 26200)", Seen(3)),
            new TacticalAgent("mock-dup-novo", "PC-DUPLICADO", "workstation", "online", Environment.UserName, "Simulado", "Simulado", "Windows 11 Pro, 64 bit (build 26200)", Seen(0)),
            // Dois online com o mesmo nome: ambíguo, a loja recusa.
            new TacticalAgent("mock-dup-a", "PC-DUP-ONLINE", "workstation", "online", Environment.UserName, "Simulado", "Simulado", "Windows 11 Pro, 64 bit (build 26200)", Seen(0)),
            new TacticalAgent("mock-dup-b", "PC-DUP-ONLINE", "workstation", "online", Environment.UserName, "Simulado", "Simulado", "Windows 11 Pro, 64 bit (build 26200)", Seen(0)),
        });

    public Task RunScriptAsync(string agentId, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var package = parameters.GetValueOrDefault("Package") ?? "";
        log.LogWarning("[SIMULADO] runscript em {Agent}: {Args}", agentId,
            string.Join(' ', parameters.Where(p => p.Key != "Token").Select(p => $"-{p.Key} {p.Value}")));

        if (options.Value.Tactical.MockManualCallback)
        {
            log.LogWarning("[SIMULADO] aguardando o script real. LOJA_TOKEN={Token}", parameters["Token"]);
            return Task.CompletedTask;
        }

        var action = parameters.GetValueOrDefault("Action") ?? "";
        var fail = package.Contains("falha", StringComparison.OrdinalIgnoreCase);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(action == "list" ? 2 : 6));
            string installed;
            lock (_installed)
            {
                if (!_installed.TryGetValue(agentId, out var pkgs)) _installed[agentId] = pkgs = new(StringComparer.OrdinalIgnoreCase);
                if (!fail)
                {
                    if (action == "install") pkgs[package] = "1.0";
                    else if (action == "upgrade") pkgs[package] = "9999.0";
                    else if (action == "uninstall") pkgs.Remove(package);
                }
                installed = string.Join('\n', pkgs.Select(p => $"{p.Key}|{p.Value}").Prepend("chocolatey|2.4.3"));
            }
            completion.Complete(parameters["Token"], fail ? 1 : 0,
                $"[SIMULADO] choco {action} {package} --source {parameters.GetValueOrDefault("Source")}", installed, FakeWindowsPrograms);
        });
        return Task.CompletedTask;
    }
}
