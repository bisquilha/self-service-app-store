using Loja.Server.Data;
using Loja.Shared;

namespace Loja.Server.Services;

public record BulkGroupsDto(List<int>? AppIds, List<GroupRefDto>? Add, List<GroupRefDto>? Remove);

public record RestoreItemDto(int AppId, List<GroupRefDto>? Groups);

public record RestoreGroupsDto(List<RestoreItemDto>? Items);

/// <summary>Como estavam os grupos de um aplicativo antes de uma alteração em lote (para o "Desfazer").</summary>
public record GroupsBeforeDto(int AppId, List<GroupRefDto> Groups);

public record BulkGroupsResultDto(List<AdminAppDto> Updated, List<string> Unchanged, List<GroupsBeforeDto> Before, string Batch);

public record GroupAppDto(int Id, string Title, string PackageId, bool Enabled, string Origin, string? Via);

/// <summary>
/// Tela Grupos. Origin: "group" (o grupo está nos grupos do aplicativo), "nested" (o grupo está dentro de outro que o aplicativo usa;
/// Via diz qual) ou "everyone" (aplicativo liberado para todos os usuários).
/// </summary>
public record GroupDetailsDto(
    AdGroupDto Group,
    List<AdMemberDto> Members,
    int MembersTotal,
    bool MembersListed,
    List<AdGroupRefDto> NestedGroups,
    List<AdGroupRefDto> ParentGroups,
    List<GroupAppDto> Apps,
    List<GroupAppDto> WithoutAccess);

/// <summary>Alterações de grupos em vários aplicativos de uma vez (atômicas e auditadas) e o detalhe de um grupo.</summary>
public class GroupAdminService(
    AppRepository apps,
    CatalogService catalog,
    AuditService audit,
    DirectoryService directory,
    ILogger<GroupAdminService> log)
{
    /// <summary>
    /// Acrescenta e/ou tira grupos de vários aplicativos numa transação só: ou todos mudam, ou nenhum.
    /// Só os grupos acrescentados são conferidos no AD. Aplicativo liberado para "Todos os usuários" não recebe grupo específico
    /// (seria tirar acesso), e nenhum aplicativo pode ficar sem grupo.
    /// </summary>
    public (BulkGroupsResultDto? Result, string? Error) Bulk(string user, BulkGroupsDto dto, bool undo = false)
    {
        var ids = dto.AppIds?.Distinct().ToList() ?? new();
        if (ids.Count == 0) return (null, "Escolha ao menos um aplicativo.");
        if ((dto.Add?.Count ?? 0) + (dto.Remove?.Count ?? 0) == 0) return (null, "Escolha ao menos um grupo para acrescentar ou tirar.");

        var rows = ids.Select(apps.Get).ToList();
        if (rows.Any(r => r is null)) return (null, "Algum aplicativo não existe mais. Recarregue a lista.");

        var add = new List<GroupRef>();
        if (dto.Add is { Count: > 0 })
        {
            var (resolved, error) = catalog.ResolveGroups(null, dto.Add);
            if (error is not null) return (null, error);
            add = resolved;
        }
        var removeKeys = (dto.Remove ?? new()).Select(r => new GroupRef(r.Name ?? "", r.Sid)).ToList();

        var changes = new List<(AppRow Before, List<GroupRef> After)>();
        var unchanged = new List<string>();
        var empty = new List<string>();
        foreach (var app in rows.Select(r => r!))
        {
            var current = app.GroupRefs;
            var next = current.Where(g => !removeKeys.Any(r => Matches(g, r))).ToList();
            if (add.Any(g => g.IsEveryone)) next = [new GroupRef("*")];
            else if (current.Any(g => g.IsEveryone) && next.Any(g => g.IsEveryone)) { /* já é de todos: grupo específico não entra */ }
            else foreach (var g in add.Where(g => next.All(n => n.Key != g.Key))) next.Add(g);

            if (next.Count == 0) { empty.Add(app.Title); continue; }
            if (next.Select(g => g.Key).ToHashSet().SetEquals(current.Select(g => g.Key))) unchanged.Add(app.Title);
            else changes.Add((app, next));
        }

        if (empty.Count > 0)
            return (null, $"Isso deixaria {(empty.Count == 1 ? "o aplicativo" : "os aplicativos")} {string.Join(", ", empty.Select(t => $"\"{t}\""))} sem nenhum grupo. Nada foi alterado.");

        var batch = Guid.NewGuid().ToString("N");
        if (changes.Count > 0) apps.UpdateGroupsMany(changes.Select(c => (c.Before.Id, c.After)).ToList());
        foreach (var (before, after) in changes)
        {
            audit.Record([new AuditEntry(user, undo ? "undo" : "bulk", AuditService.TypeApp, before.Id.ToString(), before.Title, "Grupos do AD",
                AuditService.GroupsText(before.GroupRefs), AuditService.GroupsText(after), batch, undo)]);
        }
        log.LogInformation("{User} alterou os grupos de {Count} aplicativo(s) em lote ({Unchanged} sem mudança)", user, changes.Count, unchanged.Count);

        var updated = changes.Select(c => apps.Get(c.Before.Id)!.ToAdminDto()).ToList();
        var snapshot = changes.Select(c => new GroupsBeforeDto(c.Before.Id, c.Before.GroupRefs.Select(g => g.ToDto()).ToList())).ToList();
        return (new BulkGroupsResultDto(updated, unchanged, snapshot, batch), null);
    }

    /// <summary>"Desfazer" de uma alteração em lote: volta cada aplicativo aos grupos de antes, também numa transação só.</summary>
    public (BulkGroupsResultDto? Result, string? Error) Restore(string user, RestoreGroupsDto dto)
    {
        var items = dto.Items ?? new();
        if (items.Count == 0) return (null, "Não há o que desfazer.");

        var changes = new List<(AppRow Before, List<GroupRef> After)>();
        foreach (var item in items)
        {
            var app = apps.Get(item.AppId);
            if (app is null) return (null, "Algum aplicativo não existe mais. Nada foi desfeito.");
            // Só o que for NOVO em relação ao que está salvo é conferido no AD; um grupo antigo continua passando.
            var (groups, error) = catalog.ResolveGroups(item.Groups?.Select(g => g.Name), item.Groups, app.GroupRefs);
            if (error is not null) return (null, error);
            changes.Add((app, groups));
        }

        var batch = Guid.NewGuid().ToString("N");
        apps.UpdateGroupsMany(changes.Select(c => (c.Before.Id, c.After)).ToList());
        foreach (var (before, after) in changes)
        {
            audit.Record([new AuditEntry(user, "undo", AuditService.TypeApp, before.Id.ToString(), before.Title, "Grupos do AD",
                AuditService.GroupsText(before.GroupRefs), AuditService.GroupsText(after), batch, true)]);
        }
        log.LogInformation("{User} desfez a alteração de grupos de {Count} aplicativo(s)", user, changes.Count);
        return (new BulkGroupsResultDto(changes.Select(c => apps.Get(c.Before.Id)!.ToAdminDto()).ToList(), new(), new(), batch), null);
    }

    /// <summary>Um grupo do AD e o que a loja libera para quem está nele (pelo grupo, por um grupo que o contém, ou para todos).</summary>
    public GroupDetailsDto? Details(string sid)
    {
        if (directory.GroupDetails(sid) is not { } d) return null;

        var parents = d.ParentGroups;
        var liberados = new List<GroupAppDto>();
        var sem = new List<GroupAppDto>();
        foreach (var app in apps.List(onlyEnabled: false).OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            bool Is(GroupRef g, string groupSid, string groupName) =>
                g.Sid is not null ? string.Equals(g.Sid, groupSid, StringComparison.OrdinalIgnoreCase) : string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase);

            if (app.GroupRefs.Any(g => Is(g, d.Group.Sid, d.Group.Name)))
                liberados.Add(new GroupAppDto(app.Id, app.Title, app.PackageId, app.Enabled, "group", null));
            else if (parents.FirstOrDefault(p => app.GroupRefs.Any(g => Is(g, p.Sid, p.Name))) is { } via)
                liberados.Add(new GroupAppDto(app.Id, app.Title, app.PackageId, app.Enabled, "nested", via.Name));
            else if (app.GroupRefs.Any(g => g.IsEveryone))
                liberados.Add(new GroupAppDto(app.Id, app.Title, app.PackageId, app.Enabled, "everyone", null));
            else
                sem.Add(new GroupAppDto(app.Id, app.Title, app.PackageId, app.Enabled, "none", null));
        }

        return new GroupDetailsDto(d.Group, d.Members, d.MembersTotal, d.MembersListed, d.NestedGroups, d.ParentGroups, liberados, sem);
    }

    /// <summary>Mesmo grupo: pelo SID quando os dois têm; senão pelo nome (grupo antigo sem SID).</summary>
    private static bool Matches(GroupRef saved, GroupRef wanted) =>
        saved.Sid is not null && wanted.Sid is not null
            ? string.Equals(saved.Sid, wanted.Sid, StringComparison.OrdinalIgnoreCase)
            : string.Equals(saved.Name, wanted.Name, StringComparison.OrdinalIgnoreCase);
}
