using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Principal;
using Loja.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

/// <summary>Resultado de procurar um grupo no AD. Unavailable = o AD não respondeu (não prova que o grupo não existe).</summary>
public enum GroupLookup { Found, NotFound, Unavailable }

public record ResolvedGroup(GroupLookup Status, GroupRef Group);

/// <summary>
/// Decide quem vê e instala cada app, a partir dos grupos do AD presentes no login do Windows.
/// Cada app guarda o SID de cada grupo (identidade estável) e o nome (exibição). O acesso compara o SID com os SIDs do login,
/// então renomear um grupo no AD não tira o acesso de ninguém. Grupos salvos antes da 1.6.0 só têm o nome: o nome é traduzido
/// para SID (com cache) até a migração gravar o SID.
/// </summary>
public class AccessService(IOptions<LojaOptions> options, LocalDirectoryService local, ConfigService config, ILogger<AccessService> log)
{
    /// <summary>Grupo especial: qualquer usuário autenticado no domínio.</summary>
    public const string Everyone = "*";

    private static readonly TimeSpan NameTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, string> _sidCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Name, DateTime Expires)> _nameCache = new(StringComparer.OrdinalIgnoreCase);

    public string UserName(ClaimsPrincipal user) => user.Identity?.Name ?? "";

    /// <summary>"CORP\joao.silva" ou "joao.silva@empresa" → "joao.silva".</summary>
    public static string ShortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.Trim();
        var slash = s.LastIndexOf('\\');
        if (slash >= 0) s = s[(slash + 1)..];
        var at = s.IndexOf('@');
        if (at >= 0) s = s[..at];
        return s;
    }

    /// <summary>Administrador: está num dos grupos de admin do appsettings, ou entrou por senha local com o perfil de administrador.</summary>
    public bool IsAdmin(ClaimsPrincipal user) =>
        user.HasClaim(LojaAuth.AdminClaim, "1")
        || (config.GetBool("identity.windows.enabled") && options.Value.AdminGroups.Any(g => IsInGroup(user, g)));

    public bool CanUse(ClaimsPrincipal user, AppRow app) => IsAdmin(user) || app.GroupRefs.Any(g => IsInGroup(user, g));

    /// <summary>Grupo informado por nome (grupos de administração do appsettings).</summary>
    public bool IsInGroup(ClaimsPrincipal user, string group) => IsInGroup(user, new GroupRef(group.Trim()));

    public bool IsInGroup(ClaimsPrincipal user, GroupRef group)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        if (group.Name.Length == 0 && group.Sid is null) return false;
        if (group.IsEveryone) return true;

        // Grupo local (LOCAL-n): vale a marca que a transformação de identidade pôs, por login. Não consulta o AD.
        if (group.Sid is not null && group.Sid.StartsWith(LocalDirectoryService.SidPrefix, StringComparison.OrdinalIgnoreCase))
            return user.HasClaim(LojaAuth.GroupClaim, group.Sid.ToUpperInvariant());

        var sid = group.Sid ?? (group.Name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? group.Name.ToUpperInvariant() : SidOf(group.Name, quiet: false).Sid);
        return sid is not null && (user.HasClaim(ClaimTypes.GroupSid, sid) || user.HasClaim(ClaimTypes.PrimaryGroupSid, sid));
    }

    /// <summary>
    /// Monta a identidade de outro usuário do domínio (só para ler os grupos dele, via S4U do Windows).
    /// Aceita "maria.souza", "CORP\maria.souza" ou "maria.souza@dominio". Nada é feito em nome da pessoa.
    /// </summary>
    public (ClaimsPrincipal? User, string? Error) ResolveUser(string input)
    {
        var name = ShortName(input);
        if (name.Length == 0 || name.Length > 64 || name.Any(c => char.IsWhiteSpace(c) || c is '\\' or '/' or '@' or '*'))
            return (null, "Informe um usuário do domínio, por exemplo maria.souza.");

        var dnsDomain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
        if (string.IsNullOrWhiteSpace(dnsDomain)) return (null, "O servidor não está num domínio.");

        try
        {
            // UPN implícito (login@domínio DNS) sempre funciona, mesmo se o UPN do usuário tiver outro sufixo.
            var identity = new WindowsIdentity($"{name}@{dnsDomain}");
            return (new WindowsPrincipal(identity), null);
        }
        catch (Exception ex)
        {
            log.LogInformation("Ver como: usuário {User} não resolvido ({Error})", name, ex.Message);
            return (null, $"Usuário \"{name}\" não encontrado no AD.");
        }
    }

    /// <summary>
    /// Procura o grupo no AD, por nome ("LOJA-TI", "DOMINIO\LOJA-TI") e/ou SID. Devolve o grupo com o SID e o NOME ATUAL do AD.
    /// "*" vale sempre. Só NotFound prova que o grupo não existe; Unavailable é falha de consulta.
    /// </summary>
    public ResolvedGroup ResolveGroup(string? name, string? sid = null)
    {
        name = name?.Trim() ?? "";
        sid = string.IsNullOrWhiteSpace(sid) ? null : sid.Trim().ToUpperInvariant();

        if (sid is null && name == Everyone) return new(GroupLookup.Found, new GroupRef(Everyone));
        if (sid is null && name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            sid = name.ToUpperInvariant();
            name = "";
        }

        if (LocalDirectoryService.TryParseSid(sid, out var localId))
        {
            var localGroupById = local.GetGroup(localId);
            return localGroupById is null ? new(GroupLookup.NotFound, new GroupRef(name.Length > 0 ? name : sid!, sid)) : new(GroupLookup.Found, new GroupRef(localGroupById.Name, localGroupById.Sid));
        }

        if (sid is not null)
        {
            var (status, current) = NameOf(sid, quiet: true);
            return new(status, new GroupRef(status == GroupLookup.Found ? current! : name.Length > 0 ? name : sid, sid));
        }

        if (name.Length == 0) return new(GroupLookup.NotFound, new GroupRef(""));

        // Com o login do Windows desligado só existem grupos locais; com ele ligado, o AD tem prioridade e o grupo local é o plano B.
        var windows = config.GetBool("identity.windows.enabled");
        var (lookup, found) = windows ? SidOf(name, quiet: true) : (GroupLookup.NotFound, (string?)null);
        if (lookup != GroupLookup.Found)
        {
            if (local.FindGroup(name) is { } localGroup) return new(GroupLookup.Found, new GroupRef(localGroup.Name, localGroup.Sid));
            return new(lookup, new GroupRef(name));
        }

        // Volta ao AD pelo SID para guardar o nome do jeito que o AD escreve (maiúsculas e minúsculas, sem o domínio).
        var (back, canonical) = NameOf(found!, quiet: true);
        return new(GroupLookup.Found, new GroupRef(back == GroupLookup.Found ? canonical! : name, found));
    }

    /// <summary>Estado de um grupo já salvo: o nome atual no AD (se mudou) e se ele ainda existe.</summary>
    public (GroupLookup Status, string? CurrentName, string? Sid) Check(GroupRef group)
    {
        if (group.IsEveryone) return (GroupLookup.Found, group.Name, null);
        if (LocalDirectoryService.TryParseSid(group.Sid, out var localId))
        {
            var found = local.GetGroup(localId);
            return found is null ? (GroupLookup.NotFound, null, group.Sid) : (GroupLookup.Found, found.Name, found.Sid);
        }
        if (group.Sid is not null)
        {
            var (status, name) = NameOf(group.Sid, quiet: true);
            return (status, name, group.Sid);
        }
        var resolved = ResolveGroup(group.Name);
        return (resolved.Status, resolved.Status == GroupLookup.Found ? resolved.Group.Name : null, resolved.Group.Sid);
    }

    /// <summary>Dos grupos informados, os que comprovadamente não existem no AD (nomes). "*" nunca é "não encontrado".</summary>
    public List<string> MissingGroups(IEnumerable<GroupRef> groups) =>
        groups.Where(g => !g.IsEveryone && g.Name.Length + (g.Sid?.Length ?? 0) > 0 && Check(g).Status == GroupLookup.NotFound)
              .Select(g => g.Name)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToList();

    private (GroupLookup Status, string? Sid) SidOf(string group, bool quiet)
    {
        var domain = options.Value.DefaultDomain;
        var name = group.Contains('\\') || string.IsNullOrWhiteSpace(domain) ? group : $"{domain}\\{group}";
        if (_sidCache.TryGetValue(name, out var cached)) return (GroupLookup.Found, cached);

        try
        {
            var sid = ((SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier))).Value;
            _sidCache[name] = sid;
            return (GroupLookup.Found, sid);
        }
        catch (IdentityNotMappedException ex)
        {
            // Não guarda a falha em cache: o grupo pode ser criado no AD depois.
            // O painel confere grupos a cada listagem; só o acesso de verdade registra aviso.
            log.Log(quiet ? LogLevel.Debug : LogLevel.Warning, "Grupo do AD não encontrado: {Group} ({Error})", name, ex.Message);
            return (GroupLookup.NotFound, null);
        }
        catch (Exception ex)
        {
            log.LogWarning("Falha ao consultar o AD pelo grupo {Group}: {Error}", name, ex.Message);
            return (GroupLookup.Unavailable, null);
        }
    }

    private (GroupLookup Status, string? Name) NameOf(string sid, bool quiet)
    {
        if (_nameCache.TryGetValue(sid, out var cached) && cached.Expires > DateTime.UtcNow) return (GroupLookup.Found, cached.Name);

        try
        {
            var account = (NTAccount)new SecurityIdentifier(sid).Translate(typeof(NTAccount));
            var slash = account.Value.LastIndexOf('\\');
            var name = slash >= 0 ? account.Value[(slash + 1)..] : account.Value;
            _nameCache[sid] = (name, DateTime.UtcNow + NameTtl);
            return (GroupLookup.Found, name);
        }
        catch (ArgumentException)
        {
            // SID mal formado: não é um grupo.
            return (GroupLookup.NotFound, null);
        }
        catch (IdentityNotMappedException ex)
        {
            log.Log(quiet ? LogLevel.Debug : LogLevel.Warning, "SID de grupo não encontrado no AD: {Sid} ({Error})", sid, ex.Message);
            return (GroupLookup.NotFound, null);
        }
        catch (Exception ex)
        {
            log.LogWarning("Falha ao consultar o AD pelo SID {Sid}: {Error}", sid, ex.Message);
            return (GroupLookup.Unavailable, null);
        }
    }
}

public class AdminRequirement : IAuthorizationRequirement;

public class AdminHandler(AccessService access) : AuthorizationHandler<AdminRequirement>
{
    public const string Policy = "Admin";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (access.IsAdmin(context.User)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
