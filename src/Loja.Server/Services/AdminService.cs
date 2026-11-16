using System.Security.Claims;
using Loja.Server.Data;
using Loja.Shared;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

public record DayStatDto(string Day, int Success, int Failed, int Denied);

public record OverviewDto(
    int Apps,
    int ActiveApps,
    int Machines,
    int MachinesWithStore,
    int RequestsToday,
    int Requests7d,
    int Failures7d,
    int PendingUpdates,
    List<DayStatDto> PerDay,
    List<RequestDto> Recent);

public record AppStatsDto(int AppId, int Installed, int Outdated);

public record MachineDto(
    string Hostname,
    string? Client,
    string? Site,
    string Status,
    string? LoggedUser,
    string? OperatingSystem,
    string? BlockReason,
    DateTime? InventoryUtc,
    int StoreAppsInstalled,
    int Outdated,
    DateTime? LastRequestUtc,
    string AgentId,
    int SameNameCount,
    bool Preferred,
    string? AppVersion,
    DateTime? AppSeenUtc,
    // Último contato do agente com o Tactical (para distinguir nomes duplicados). Vazio se o Tactical não informa.
    DateTime? LastSeenUtc = null);

/// <summary>Verificar programas em várias máquinas de uma vez (painel).</summary>
public record ScanMachinesDto(List<string>? Hostnames);

public record StoreAppStateDto(int AppId, string Title, string PackageId, bool Enabled, string? LatestVersion, string? InstalledVersion, bool UpdateAvailable,
    string? VersionState = null, string? ExternalVersion = null);

public record PackageRowDto(string PackageId, string Version, bool InStore);

/// <summary>Programa do Windows da máquina. StoreApp = aplicativo da loja que tem este nome cadastrado (vazio se nenhum).</summary>
public record ProgramRowDto(string Name, string Version, string? StoreApp);

/// <summary>Programas: nulo e ProgramsUtc nulo = o script desta máquina ainda não envia os programas do Windows.</summary>
public record MachineDetailDto(MachineDto Machine, List<StoreAppStateDto> StoreApps, List<PackageRowDto> Packages, List<RequestDto> Requests,
    List<ProgramRowDto>? Programs = null, DateTime? ProgramsUtc = null);

public record ProgramNameDto(string Name, int Machines);

public record AliasesDto(List<string>? Aliases, string? Reason = null);

/// <summary>
/// Reach: "ready" (online, estação), "offline", "blocked" (servidor ou tipo não permitido), "duplicate" (dois com o mesmo nome online),
/// "missing" (não está no Tactical) ou "unknown" (o Tactical não respondeu agora).
/// </summary>
public record UpdateMachineDto(string Hostname, string InstalledVersion, string Reach);

public record UpdateAppDto(int AppId, string Title, string PackageId, bool Enabled, bool AutoUpdate, string LatestVersion, List<UpdateMachineDto> Machines);

public record UpgradeAllResultDto(int Started, int Skipped, List<string> Messages);

public record PreviewGroupDto(string Group, bool Member);

public record PreviewHiddenDto(int AppId, string Title, string PackageId, List<string> Groups, bool Disabled);

/// <summary>"Ver como usuário": a loja exatamente como aquela pessoa vê.</summary>
public record PreviewDto(
    string User,
    bool IsAdmin,
    List<PreviewGroupDto> StoreGroups,
    List<string> LoggedOn,
    string? Hostname,
    DateTime? InventoryUtc,
    List<CatalogItemDto> Visible,
    List<PreviewHiddenDto> Hidden,
    DateTime? ProgramsUtc = null);

/// <summary>Ação remota pelo painel. AppId é opcional para "list" (verificar programas instalados).</summary>
public record AdminMachineActionDto(int? AppId, string Action);

/// <summary>Números e listas do painel web de administração.</summary>
public class AdminService(
    AppRepository apps,
    RequestRepository requests,
    InventoryRepository inventory,
    ITacticalClient tactical,
    InstallService install,
    AccessService access,
    IconCache icons,
    ClientRepository clients,
    IOptions<LojaOptions> options,
    ILogger<AdminService> log)
{
    public OverviewDto Overview()
    {
        var all = apps.List(onlyEnabled: false);
        var inv = inventory.ListAll();
        var today = DateTime.Today;
        var since = today.AddDays(-13).ToUniversalTime();
        var stats = requests.StatusesSince(since);

        var perDay = Enumerable.Range(0, 14)
            .Select(i => today.AddDays(-13 + i))
            .Select(day =>
            {
                var ofDay = stats.Where(s => s.CreatedUtc.ToLocalTime().Date == day).ToList();
                return new DayStatDto(day.ToString("dd/MM"),
                    ofDay.Count(s => s.Status == RequestStatus.Success),
                    ofDay.Count(s => s.Status is RequestStatus.Failed or RequestStatus.Timeout),
                    ofDay.Count(s => s.Status == RequestStatus.Denied));
            })
            .ToList();

        // "7 dias" = hoje e os 6 anteriores, a mesma janela do gráfico e do filtro de datas do histórico.
        var weekAgo = today.AddDays(-6).ToUniversalTime();
        var storePackages = all.Where(a => a.Enabled).ToList();
        var pending = inv.Sum(h => storePackages.Count(a =>
            h.Value.Versions.TryGetValue(a.PackageId, out var v) && VersionRules.IsOutdated(v, a.Version)));

        return new OverviewDto(
            all.Count,
            all.Count(a => a.Enabled),
            inv.Count,
            inv.Count(h => storePackages.Any(a => h.Value.Versions.ContainsKey(a.PackageId))),
            stats.Count(s => s.CreatedUtc.ToLocalTime().Date == today),
            stats.Count(s => s.CreatedUtc >= weekAgo),
            stats.Count(s => s.CreatedUtc >= weekAgo && s.Status is RequestStatus.Failed or RequestStatus.Timeout),
            pending,
            perDay,
            requests.Query(new RequestQuery(Take: 10)).Rows.Select(r => r.ToDto(includeOutput: true)).ToList());
    }

    public List<AppStatsDto> AppStats()
    {
        var inv = inventory.ListAll();
        return apps.List(onlyEnabled: false).Select(a =>
        {
            var installed = inv.Values.Where(h => h.Versions.ContainsKey(a.PackageId)).ToList();
            var outdated = installed.Count(h => VersionRules.IsOutdated(h.Versions[a.PackageId], a.Version));
            return new AppStatsDto(a.Id, installed.Count, outdated);
        }).ToList();
    }

    /// <summary>Todas as máquinas do Tactical, com o que a loja sabe de cada uma.</summary>
    public async Task<List<MachineDto>> MachinesAsync(CancellationToken ct)
    {
        var agents = await tactical.GetAgentsAsync(ct);
        var inv = inventory.ListAll();
        var last = requests.LastRequestPerHost();
        var store = apps.List(onlyEnabled: false);
        var sameName = agents.GroupBy(a => a.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var appVersions = clients.ListHosts();

        return agents
            .Select(a => ToMachine(a, inv.GetValueOrDefault(a.Hostname), last, store, sameName[a.Hostname], appVersions.GetValueOrDefault(a.Hostname)))
            .OrderBy(m => m.Hostname, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(m => m.Preferred)
            .ToList();
    }

    /// <summary>Detalhe de uma máquina. Com nome duplicado no Tactical, mostra o mesmo agente que a loja usa (o online).</summary>
    public async Task<MachineDetailDto?> MachineAsync(string hostname, CancellationToken ct)
    {
        var matches = (await tactical.GetAgentsAsync(ct))
            .Where(a => string.Equals(a.Hostname, hostname, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return null;
        var agent = InstallService.PickAgent(matches) ?? matches[0];

        var inv = inventory.Get(agent.Hostname);
        var store = apps.List(onlyEnabled: false);
        var machine = ToMachine(agent, inv, requests.LastRequestPerHost(), store, matches, clients.ListHosts().GetValueOrDefault(agent.Hostname));

        var aliases = apps.ListAliases();
        var storeApps = store.Select(a =>
        {
            var item = CatalogItems.Build(a, inv, CatalogItems.KeysOf(aliases, a.Id), false);
            return new StoreAppStateDto(a.Id, a.Title, a.PackageId, a.Enabled, a.Version, item.InstalledVersion, item.UpdateAvailable, item.VersionState, item.ExternalVersion);
        }).OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        var storeIds = store.Select(a => a.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packages = inv.Versions.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PackageRowDto(p.Key, p.Value, storeIds.Contains(p.Key))).ToList();

        // Programa do Windows -> aplicativo da loja que o reivindica pelo nome cadastrado (um nome, um aplicativo: o primeiro).
        var byAlias = new Dictionary<string, string>();
        foreach (var a in store)
            foreach (var key in CatalogItems.KeysOf(aliases, a.Id) ?? new HashSet<string>())
                byAlias.TryAdd(key, a.Title);
        var programs = inv.ProgramsUtc is null
            ? null
            : inv.Programs.Select(p => new ProgramRowDto(p.Name, p.Version, byAlias.GetValueOrDefault(ProgramAliases.KeyOf(p.Name)))).ToList();

        var history = requests.Query(new RequestQuery(Hostname: agent.Hostname, Take: 30)).Rows.Select(r => r.ToDto(includeOutput: true)).ToList();
        return new MachineDetailDto(machine, storeApps, packages, history, programs, inv.ProgramsUtc);
    }

    /// <summary>
    /// Atualizações disponíveis: cada aplicativo com máquinas em versão MENOR que a da loja (só versões comparáveis),
    /// e se cada máquina está ao alcance agora. É a lista exata do que "Atualizar em todas" tenta (ele só pula quem não passa nas travas).
    /// </summary>
    public async Task<List<UpdateAppDto>> UpdatesAsync(CancellationToken ct)
    {
        IReadOnlyList<TacticalAgent>? agents = null;
        try
        {
            agents = await tactical.GetAgentsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Atualizações: Tactical indisponível ({Error}); o alcance das máquinas fica como desconhecido", ex.Message);
        }

        string Reach(string hostname)
        {
            if (agents is null) return "unknown";
            var matches = agents.Where(a => string.Equals(a.Hostname, hostname, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return "missing";
            var agent = InstallService.PickAgent(matches);
            if (agent is null) return "duplicate";
            if (install.BlockReason(agent) is not null) return "blocked";
            return string.Equals(agent.Status, "online", StringComparison.OrdinalIgnoreCase) ? "ready" : "offline";
        }

        return apps.List(onlyEnabled: false)
            .Where(a => a.Version is not null)
            .Select(a => new UpdateAppDto(a.Id, a.Title, a.PackageId, a.Enabled, a.AutoUpdate, a.Version!,
                inventory.HostsWithPackage(a.PackageId)
                    .Where(h => VersionRules.IsOutdated(h.Version, a.Version))
                    .OrderBy(h => h.Hostname, StringComparer.OrdinalIgnoreCase)
                    .Select(h => new UpdateMachineDto(h.Hostname, h.Version, Reach(h.Hostname)))
                    .ToList()))
            .Where(a => a.Machines.Count > 0)
            .OrderByDescending(a => a.Machines.Count)
            .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public const int MaxScan = 100;

    /// <summary>
    /// "Verificar programas" em várias máquinas: pede a lista a cada uma (quatro por vez). Só consulta, não instala nada.
    /// As que não passam nas travas (offline, servidor, nome repetido, outra verificação em andamento) ficam puladas, com o motivo.
    /// </summary>
    public async Task<UpgradeAllResultDto> ScanAsync(ClaimsPrincipal admin, IEnumerable<string> hostnames, CancellationToken ct)
    {
        var list = hostnames.Select(h => (h ?? "").Trim()).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(list, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (host, token) =>
        {
            var result = await install.RequestAsAdminAsync(admin, null, host, RequestActions.List, token);
            results[host] = result.Error;
        });

        var skipped = list.Where(h => results.TryGetValue(h, out var error) && error is not null).ToList();
        log.LogInformation("{User} pediu a verificação de programas em {Count} máquina(s): {Started} enviadas, {Skipped} puladas",
            admin.Identity?.Name, list.Count, list.Count - skipped.Count, skipped.Count);
        return new UpgradeAllResultDto(list.Count - skipped.Count, skipped.Count, skipped.Select(h => $"{h}: {results[h]}").ToList());
    }

    /// <summary>Atualiza o app em todas as máquinas que têm versão antiga (online, estação, sem outra ação rodando).</summary>
    public async Task<UpgradeAllResultDto?> UpgradeEverywhereAsync(ClaimsPrincipal admin, int appId, CancellationToken ct)
    {
        var app = apps.Get(appId);
        if (app is null) return null;

        int started = 0, skipped = 0;
        var messages = new List<string>();
        foreach (var (hostname, version) in inventory.HostsWithPackage(app.PackageId))
        {
            if (!VersionRules.IsOutdated(version, app.Version)) continue;
            var result = await install.RequestAsAdminAsync(admin, app.Id, hostname, RequestActions.Upgrade, ct);
            if (result.Error is null) started++;
            else
            {
                skipped++;
                messages.Add($"{hostname}: {result.Error}");
            }
        }

        log.LogInformation("{User} pediu atualizar {Package} em todas as máquinas: {Started} enviadas, {Skipped} puladas",
            admin.Identity?.Name, app.PackageId, started, skipped);
        return new UpgradeAllResultDto(started, skipped, messages);
    }

    /// <summary>
    /// Monta a loja como o usuário informado vê: mesmos filtros de grupo do app de desktop e, se ele estiver
    /// logado em alguma estação (segundo o Tactical), o estado "instalado / atualizar" daquela máquina.
    /// </summary>
    public async Task<(PreviewDto? Preview, string? Error)> PreviewAsync(string userInput, string? hostname, CancellationToken ct)
    {
        var (user, error) = access.ResolveUser(userInput);
        if (user is null) return (null, error);

        var userName = access.UserName(user);
        var store = apps.List(onlyEnabled: false);

        // Grupos usados na loja (apps + administração) e se a pessoa está em cada um.
        var storeGroups = store.SelectMany(a => a.GroupRefs)
            .Concat(options.Value.AdminGroups.Select(g => new GroupRef(g)))
            .Where(g => !g.IsEveryone)
            .GroupBy(g => g.Key)
            .Select(g => g.OrderByDescending(x => x.Sid is not null).First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PreviewGroupDto(g.Name, access.IsInGroup(user, g)))
            .ToList();

        // Estações em que ela está logada agora (o Tactical informa o usuário logado).
        List<string> loggedOn;
        try
        {
            loggedOn = (await tactical.GetAgentsAsync(ct))
                .Where(a => install.BlockReason(a) is null
                            && string.Equals(AccessService.ShortName(a.LoggedUser), AccessService.ShortName(userName), StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Hostname)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (HttpRequestException)
        {
            loggedOn = new();
        }

        var host = !string.IsNullOrWhiteSpace(hostname) ? hostname.Trim() : loggedOn.FirstOrDefault();
        var inv = host is null ? new HostInventory(null, new()) : inventory.Get(host);

        var aliases = apps.ListAliases();
        var visible = new List<CatalogItemDto>();
        var hidden = new List<PreviewHiddenDto>();
        foreach (var a in store.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (a.Enabled && access.CanUse(user, a))
            {
                visible.Add(CatalogItems.Build(a, inv, CatalogItems.KeysOf(aliases, a.Id), icons.Has(a.Id)));
            }
            else
            {
                hidden.Add(new PreviewHiddenDto(a.Id, a.Title, a.PackageId, a.Groups, !a.Enabled));
            }
        }

        return (new PreviewDto(userName, access.IsAdmin(user), storeGroups, loggedOn, host, inv.UpdatedUtc, visible, hidden, inv.ProgramsUtc), null);
    }

    private MachineDto ToMachine(TacticalAgent a, HostInventory? inv, Dictionary<string, DateTime> last, List<AppRow> store,
        List<TacticalAgent> sameName, HostClientRow? app)
    {
        var versions = inv?.Versions ?? new Dictionary<string, string>();
        var installed = store.Where(s => versions.ContainsKey(s.PackageId)).ToList();
        var outdated = installed.Count(s => VersionRules.IsOutdated(versions[s.PackageId], s.Version));
        return new MachineDto(
            a.Hostname, a.Client, a.Site, a.Status, a.LoggedUser, a.OperatingSystem, install.BlockReason(a),
            inv?.UpdatedUtc, installed.Count, outdated,
            last.TryGetValue(a.Hostname, out var at) ? at : null,
            a.AgentId, sameName.Count, InstallService.PickAgent(sameName)?.AgentId == a.AgentId,
            app?.Version, app?.LastSeenUtc,
            DateTime.TryParse(a.LastSeen, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var seen) ? seen : null);
    }
}
