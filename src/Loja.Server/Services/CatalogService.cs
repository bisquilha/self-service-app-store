using System.Text.RegularExpressions;
using Loja.Server.Data;
using Loja.Shared;

namespace Loja.Server.Services;

/// <summary>
/// Monta o catálogo a partir dos repositórios: o admin só escolhe o pacote e os grupos;
/// nome, descrição, versão e ícone vêm do repositório e são atualizados sozinhos.
/// </summary>
public partial class CatalogService(
    AppRepository apps,
    CommunityFeed community,
    InternalFeed internalFeed,
    IconCache icons,
    AccessService access,
    ConfigService config,
    ILogger<CatalogService> log)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    public static partial Regex PackageIdRegex();

    public async Task<List<PackageSearchResultDto>> SearchAsync(string term, string source, CancellationToken ct)
    {
        var inStore = apps.List(onlyEnabled: false)
                          .Where(a => a.Source == source)
                          .Select(a => a.PackageId)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var found = source == PackageSources.Internal
            ? internalFeed.Search(term)
            : await community.SearchAsync(term, 40, ct);

        return found.Select(p => new PackageSearchResultDto(
                p.Id, p.Source, p.Title, p.Version, CleanDescription(p.Description, 300),
                p.DownloadCount, p.IsApproved, inStore.Contains(p.Id)))
            .ToList();
    }

    public async Task<(AppRow? App, string? Error)> AddAsync(AdminAppCreateDto dto, CancellationToken ct)
    {
        if (!PackageSources.IsValid(dto.Source)) return (null, "Origem inválida.");
        if (!config.IsSourceEnabled(dto.Source)) return (null, config.SourceOffMessage(dto.Source));
        if (!PackageIdRegex().IsMatch(dto.PackageId ?? "")) return (null, "Nome de pacote inválido.");
        var (groups, groupsError) = ResolveGroups(dto.Groups, dto.GroupRefs);
        if (groupsError is not null) return (null, groupsError);
        if (apps.Find(dto.PackageId!, dto.Source) is not null) return (null, "Este aplicativo já está na loja.");

        var info = await GetInfoAsync(dto.PackageId!, dto.Source, ct);
        if (info is null) return (null, "Pacote não encontrado no repositório.");

        var id = apps.Insert(info.Id, dto.Source, info.Title, CleanDescription(info.Description, 600), info.Version, info.IconUrl, groups);
        await icons.UpdateAsync(id, info, ct);
        return (apps.Get(id), null);
    }

    public async Task<bool> RefreshAsync(AppRow app, CancellationToken ct)
    {
        var info = await GetInfoAsync(app.PackageId, app.Source, ct);
        if (info is null) return false;
        apps.UpdateMetadata(app.Id, info.Title, CleanDescription(info.Description, 600), info.Version, info.IconUrl);
        await icons.UpdateAsync(app.Id, info, ct);
        return true;
    }

    public async Task RefreshAllAsync(CancellationToken ct)
    {
        foreach (var app in apps.List(onlyEnabled: false).Where(a => config.IsSourceEnabled(a.Source)))
        {
            try
            {
                if (!await RefreshAsync(app, ct)) log.LogWarning("Pacote {Package} ({Source}) não encontrado no repositório", app.PackageId, app.Source);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Falha ao atualizar dados de {Package}: {Error}", app.PackageId, ex.Message);
            }
        }
    }

    public static List<string> CleanGroups(IEnumerable<string>? groups) =>
        groups?.Select(g => g.Trim()).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();

    /// <summary>
    /// Transforma o que o painel (ou o app antigo) mandou em grupos com SID e nome atual do AD.
    /// GroupRefs, quando vem, tem prioridade sobre os nomes. Só o que é NOVO em relação a <paramref name="existing"/> é conferido no AD:
    /// um grupo já salvo (mesmo SID ou mesmo nome) continua passando, senão nem dava para ligar um switch enquanto um grupo antigo não fosse corrigido.
    /// </summary>
    public (List<GroupRef> Groups, string? Error) ResolveGroups(IEnumerable<string>? names, IEnumerable<GroupRefDto>? refs, IEnumerable<GroupRef>? existing = null)
    {
        var input = refs?.Where(r => !string.IsNullOrWhiteSpace(r.Name) || !string.IsNullOrWhiteSpace(r.Sid)).Select(r => (r.Name ?? "", r.Sid)).ToList();
        if (input is null || input.Count == 0)
            input = CleanGroups(names).Select(n => (n, (string?)null)).ToList();
        if (input.Count == 0) return (new(), "Informe pelo menos um grupo do AD (ou * para todos).");

        var kept = existing?.ToList() ?? new();
        var result = new List<GroupRef>();
        var missing = new List<string>();
        var unavailable = new List<string>();
        foreach (var (name, sid) in input)
        {
            var already = kept.FirstOrDefault(e =>
                (sid is not null && e.Sid is not null && string.Equals(e.Sid, sid, StringComparison.OrdinalIgnoreCase))
                || (e.Sid is null || sid is null) && string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (already is not null)
            {
                result.Add(already);
                continue;
            }

            var resolved = access.ResolveGroup(name, sid);
            switch (resolved.Status)
            {
                case GroupLookup.Found: result.Add(resolved.Group); break;
                case GroupLookup.NotFound: missing.Add(name.Length > 0 ? name : sid ?? "?"); break;
                default: unavailable.Add(name.Length > 0 ? name : sid ?? "?"); break;
            }
        }

        if (missing.Count > 0) return (new(), GroupsError(missing));
        if (unavailable.Count > 0)
            return (new(), "Não foi possível consultar o AD agora para conferir " + (unavailable.Count == 1 ? $"o grupo \"{unavailable[0]}\"" : "os grupos " + string.Join(", ", unavailable.Select(g => $"\"{g}\""))) + ". Tente de novo em instantes.");

        return (result.GroupBy(g => g.Key).Select(g => g.First()).ToList(), null);
    }

    /// <summary>
    /// Migração para a 1.6.0 (e conferência de rotina): grava o SID dos grupos salvos só pelo nome e atualiza o nome de exibição
    /// dos que foram renomeados no AD. Se o AD não responder, nada muda e o acesso continua pelo nome. Devolve o que mudou.
    /// </summary>
    public (int SidsSaved, int Renamed, int Pending) SyncGroups(IEnumerable<AppRow>? rows = null)
    {
        int saved = 0, renamed = 0, pending = 0;
        foreach (var app in rows ?? apps.List(onlyEnabled: false))
        {
            foreach (var group in app.GroupRefs.Where(g => !g.IsEveryone))
            {
                var (status, currentName, sid) = access.Check(group);
                if (status != GroupLookup.Found)
                {
                    if (group.Sid is null) pending++;
                    continue;
                }
                if (group.Sid is null && sid is not null)
                {
                    apps.SetGroupSid(app.Id, group.Name, sid);
                    saved++;
                }
                if (currentName is not null && !string.Equals(currentName, group.Name, StringComparison.Ordinal) && sid is not null)
                {
                    apps.RenameGroup(sid, currentName);
                    renamed++;
                }
            }
        }
        if (saved + renamed > 0) log.LogInformation("Grupos dos aplicativos: {Saved} SID(s) gravado(s), {Renamed} nome(s) atualizado(s)", saved, renamed);
        return (saved, renamed, pending);
    }

    /// <summary>Mensagem para o painel quando algum grupo informado não existe no AD.</summary>
    public static string GroupsError(IReadOnlyList<string> missing) =>
        missing.Count == 1
            ? $"O grupo \"{missing[0]}\" não existe no AD. Escolha um grupo da lista."
            : $"Estes grupos não existem no AD: {string.Join(", ", missing.Select(g => $"\"{g}\""))}. Escolha grupos da lista.";

    private Task<PackageInfo?> GetInfoAsync(string packageId, string source, CancellationToken ct) =>
        source == PackageSources.Internal
            ? Task.FromResult(internalFeed.Get(packageId))
            : community.GetAsync(packageId, ct);

    /// <summary>As descrições do Chocolatey vêm em Markdown e às vezes enormes: fica só o primeiro parágrafo, em texto.</summary>
    private static string? CleanDescription(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var firstParagraph = Regex.Split(text.Trim(), @"\r?\n\s*\r?\n")[0];
        var plain = Regex.Replace(firstParagraph, @"[#*_`>]+|\[([^\]]*)\]\([^)]*\)", m => m.Groups[1].Success ? m.Groups[1].Value : "");
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        return plain.Length <= max ? plain : plain[..(max - 1)].TrimEnd() + "…";
    }
}
