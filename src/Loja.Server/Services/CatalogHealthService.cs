using Loja.Server.Data;

namespace Loja.Server.Services;

/// <summary>
/// Um problema do catálogo. Kind: "no-group" (ativo sem grupo), "group-missing" (grupo comprovadamente inexistente no AD),
/// "group-unverified" (o AD não respondeu: NÃO prova que o grupo não existe), "group-empty" (zero membros diretos) ou
/// "outdated" (muitas máquinas com versão antiga). Severity: "error" ou "warning".
/// </summary>
public record HealthIssueDto(string Kind, string Severity, int AppId, string Title, string PackageId, string Message, string? GroupSid = null, string? GroupName = null);

/// <summary>AdAvailable = false quando alguma consulta ao AD falhou (então parte dos grupos ficou sem conferir). OutdatedRule diz o critério usado.</summary>
public record CatalogHealthDto(List<HealthIssueDto> Issues, int AppsChecked, int GroupsChecked, bool AdAvailable, string? AdNote, DateTime CheckedUtc, string OutdatedRule);

/// <summary>
/// Confere o catálogo: aplicativo ativo sem grupo, grupo que não existe mais no AD (distinguindo "não existe" de "o AD não respondeu"),
/// grupo sem nenhum membro direto e aplicativo desatualizado em muitas máquinas. Só lê. Cada grupo é consultado uma vez por conferência.
/// </summary>
public class CatalogHealthService(AppRepository apps, AccessService access, DirectoryService directory, InventoryRepository inventory, ILogger<CatalogHealthService> log)
{
    /// <summary>Máquinas desatualizadas a partir das quais o aplicativo entra na lista, qualquer que seja a proporção.</summary>
    public const int OutdatedMachines = 5;

    /// <summary>Proporção (de quem tem o aplicativo) a partir da qual entra, com pelo menos <see cref="OutdatedMinimum"/> máquinas.</summary>
    public const double OutdatedShare = 0.20;

    public const int OutdatedMinimum = 2;

    public static string RuleText =>
        $"{OutdatedMachines} ou mais máquinas desatualizadas, ou {OutdatedMinimum} ou mais que sejam {OutdatedShare:P0} ou mais das máquinas que têm o aplicativo.";

    public static bool IsOutdatedTooMuch(int outdated, int installed) =>
        outdated >= OutdatedMachines || (outdated >= OutdatedMinimum && installed > 0 && outdated >= installed * OutdatedShare);

    public CatalogHealthDto Check()
    {
        var issues = new List<HealthIssueDto>();
        var active = apps.List(onlyEnabled: true);
        var adAvailable = true;
        var emptyCache = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        var groupsChecked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in active.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (app.GroupRefs.Count == 0)
            {
                issues.Add(new("no-group", "error", app.Id, app.Title, app.PackageId, "Está ativo, mas não tem nenhum grupo: ninguém, além da TI, vê este aplicativo na loja."));
                continue;
            }

            foreach (var group in app.GroupRefs.Where(g => !g.IsEveryone))
            {
                groupsChecked.Add(group.Key);
                var (status, currentName, sid) = access.Check(group);
                var label = currentName ?? group.Name;
                if (status == GroupLookup.NotFound)
                {
                    issues.Add(new("group-missing", "error", app.Id, app.Title, app.PackageId,
                        $"O grupo \"{group.Name}\" não existe mais no AD, então não dá acesso a ninguém. Troque o grupo deste aplicativo.", group.Sid, group.Name));
                }
                else if (status == GroupLookup.Unavailable)
                {
                    adAvailable = false;
                    issues.Add(new("group-unverified", "warning", app.Id, app.Title, app.PackageId,
                        $"Não foi possível conferir o grupo \"{group.Name}\" agora porque o AD não respondeu. Isso não significa que ele não exista.", group.Sid, group.Name));
                }
                else if (sid is not null && adAvailable)
                {
                    // Zero membros diretos (pessoas e grupos). Só avisa: quem está em um grupo que CONTÉM este também tem acesso.
                    if (!emptyCache.TryGetValue(sid, out var empty))
                    {
                        try
                        {
                            var details = directory.GroupDetails(sid, maxMembers: 1);
                            empty = details is { MembersListed: true, MembersTotal: 0 };
                        }
                        catch (InvalidOperationException ex)
                        {
                            adAvailable = false;
                            empty = null;
                            log.LogWarning("Saúde do catálogo: não foi possível contar os membros de {Group}: {Error}", label, ex.Message);
                        }
                        emptyCache[sid] = empty;
                    }
                    if (empty == true)
                    {
                        issues.Add(new("group-empty", "warning", app.Id, app.Title, app.PackageId,
                            $"O grupo \"{label}\" tem 0 membros diretos. Isso não prova que ninguém tem acesso: pessoas de grupos que contêm este grupo também entram.", sid, label));
                    }
                }
            }
        }

        // Aplicativos desatualizados em muitas máquinas (só versões comparáveis).
        var inv = inventory.ListAll();
        foreach (var app in active.Where(a => a.Version is not null))
        {
            var installed = inv.Values.Where(h => h.Versions.ContainsKey(app.PackageId)).ToList();
            var outdated = installed.Count(h => VersionRules.IsOutdated(h.Versions[app.PackageId], app.Version));
            if (IsOutdatedTooMuch(outdated, installed.Count))
            {
                issues.Add(new("outdated", "warning", app.Id, app.Title, app.PackageId,
                    $"{outdated} de {installed.Count} {(installed.Count == 1 ? "máquina" : "máquinas")} com este aplicativo estão em versão antiga (a da loja é {app.Version}).", null, null));
            }
        }

        var note = adAvailable
            ? null
            : "O AD não respondeu a todas as consultas, então parte dos grupos ficou sem conferir. O que aparece como inexistente foi comprovado; o resto está marcado como não conferido.";
        var ordered = issues
            .OrderBy(i => i.Severity == "error" ? 0 : 1)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.Kind)
            .ToList();
        return new CatalogHealthDto(ordered, active.Count, groupsChecked.Count, adAvailable, note, DateTime.UtcNow, RuleText);
    }
}
