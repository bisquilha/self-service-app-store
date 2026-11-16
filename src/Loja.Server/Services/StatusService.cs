using System.Reflection;
using Loja.Server.Data;
using Loja.Shared;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

/// <summary>Painel "Status" da administração: confere cada dependência da loja.</summary>
public class StatusService(
    ITacticalClient tactical,
    CommunityFeed community,
    InternalFeed internalFeed,
    AppRepository apps,
    Db db,
    ConfigService config,
    IOptions<LojaOptions> options)
{
    public async Task<AdminStatusDto> GetAsync(CancellationToken ct)
    {
        var opt = options.Value;
        var checks = new List<StatusCheckDto>
        {
            await CheckTacticalAsync(opt, ct),
            await CheckCommunityAsync(ct),
            CheckInternalFeed(opt),
            CheckDatabase(),
            CheckAutoUpdate(opt),
        };

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var plus = version.IndexOf('+');
        return new AdminStatusDto(plus > 0 ? version[..plus] : version, opt.AdminGroups, checks);
    }

    private async Task<StatusCheckDto> CheckTacticalAsync(LojaOptions opt, CancellationToken ct)
    {
        const string name = "Tactical RMM";
        try
        {
            var agents = await tactical.GetAgentsAsync(ct);
            var allowed = agents.Where(a => opt.AllowedMonitoringTypes.Contains(a.MonitoringType, StringComparer.OrdinalIgnoreCase)).ToList();
            var blockedByOs = opt.BlockServerOperatingSystems
                ? allowed.Count(a => string.IsNullOrWhiteSpace(a.OperatingSystem) || a.IsServerOs)
                : 0;
            var eligible = allowed.Where(a => !opt.BlockServerOperatingSystems || (!string.IsNullOrWhiteSpace(a.OperatingSystem) && !a.IsServerOs)).ToList();
            var online = eligible.Count(a => string.Equals(a.Status, "online", StringComparison.OrdinalIgnoreCase));
            var summary = $"{Plural(agents.Count, "agente", "agentes")}; {Plural(eligible.Count, "estação elegível", "estações elegíveis")} ({online} online agora); "
                          + $"{Plural(blockedByOs, "bloqueado", "bloqueados")} por serem Windows Server ou sem SO informado. Script ID {opt.Tactical.ScriptId}.";
            return opt.Tactical.Mock
                ? new(name, false, "SIMULADO (Loja:Tactical:Mock = true): nada é instalado de verdade. " + summary)
                : new(name, true, $"Conectado a {opt.Tactical.Url}. {summary}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new(name, false, $"Sem resposta: {ex.Message}");
        }
    }

    private async Task<StatusCheckDto> CheckCommunityAsync(CancellationToken ct)
    {
        const string name = "Repositório Chocolatey (internet)";
        if (!config.ChocolateyEnabled) return new(name, true, "Desligado em Configurações.");
        try
        {
            var found = await community.SearchAsync("7zip", 1, ct);
            return found.Count > 0
                ? new(name, true, $"Respondendo ({options.Value.CommunityFeedUrl}).")
                : new(name, false, "Respondeu, mas sem resultados.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new(name, false, $"Sem resposta: {ex.Message}");
        }
    }

    private StatusCheckDto CheckInternalFeed(LojaOptions opt)
    {
        const string name = "Feed interno (NAS)";
        if (!config.InternalAppsEnabled) return new(name, true, "Desligado em Configurações.");
        if (!internalFeed.IsConfigured) return new(name, false, "Não configurado (Loja:InternalFeedPath).");
        try
        {
            // Ler a lista de pacotes engole erros de leitura (e devolve 0 pacotes): por isso a pasta é aberta de verdade antes.
            if (!Directory.Exists(opt.InternalFeedPath) || !FeedDiagnosis.OpenEvidence(opt.InternalFeedPath).Opened)
                return new(name, false, $"Pasta inacessível: {opt.InternalFeedPath}. {FeedDiagnosis.Explain(opt.InternalFeedPath)}");
            var count = internalFeed.ListLatest().Count;
            return new(name, true, $"{opt.InternalFeedPath}: {Plural(count, "pacote", "pacotes")}.");
        }
        catch (Exception ex)
        {
            // Mostra o que foi observado (conta do servidor, rede, resultado de abrir a pasta) e deixa claro o que é só hipótese.
            return new(name, false, $"{opt.InternalFeedPath}: {ex.Message} {FeedDiagnosis.Explain(opt.InternalFeedPath)}");
        }
    }

    private StatusCheckDto CheckAutoUpdate(LojaOptions opt)
    {
        var marked = apps.List(onlyEnabled: true).Count(a => a.AutoUpdate);
        return new("Atualização automática", true,
            $"Roda entre {opt.AutoUpdateStartHour}h e {opt.AutoUpdateEndHour}h (horário do servidor), até {opt.AutoUpdateMaxPerCycle} máquinas a cada 10 min. "
            + $"{Plural(marked, "aplicativo marcado", "aplicativos marcados")} para atualizar sozinho.");
    }

    private StatusCheckDto CheckDatabase()
    {
        var all = apps.List(onlyEnabled: false);
        return new("Banco de dados", true, $"{Plural(all.Count, "aplicativo cadastrado", "aplicativos cadastrados")} ({Plural(all.Count(a => a.Enabled), "ativo", "ativos")}). Pasta: {db.DataDir}");
    }

    private static string Plural(int count, string singular, string plural) => $"{count} {(count == 1 ? singular : plural)}";
}
