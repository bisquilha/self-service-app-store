using System.DirectoryServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

public record AdUserDto(string Login, string? DisplayName, string? Email, string? Department, string? Title);

/// <summary>
/// Grupo do AD para o seletor do painel. MemberCount = membros diretos (usuários e grupos aninhados);
/// é null quando o AD não informa (grupos primários como "Domain Users", cujos membros não aparecem em "member").
/// </summary>
public record AdGroupDto(string Name, string? Description, string Sid, int? MemberCount);

/// <summary>Pessoa que é membro direto de um grupo.</summary>
public record AdMemberDto(string Login, string? DisplayName, string? Department);

/// <summary>Grupo citado em outro (grupo aninhado ou grupo que contém este).</summary>
public record AdGroupRefDto(string Name, string Sid);

/// <summary>
/// Detalhe de um grupo para a tela Grupos. MembersListed = false nos grupos primários (Domain Users...), cujos membros o AD não lista.
/// A lista de pessoas é cortada em 200; MembersTotal é a contagem de membros diretos (pessoas e grupos) informada pelo AD.
/// </summary>
public record AdGroupDetailsDto(
    AdGroupDto Group,
    List<AdMemberDto> Members,
    int MembersTotal,
    bool MembersListed,
    List<AdGroupRefDto> NestedGroups,
    List<AdGroupRefDto> ParentGroups);

/// <summary>Busca de usuários e grupos no AD (só leitura), com a conta do próprio servidor.</summary>
public class DirectoryService(IOptions<LojaOptions> options, ILogger<DirectoryService> log)
{
    /// <summary>
    /// Procura por pedaço do nome, sobrenome, login ou e-mail ("ana", "vidigal", "Ana Maria").
    /// Só contas de pessoas e ativas.
    /// </summary>
    public List<AdUserDto> SearchUsers(string term, int take = 15)
    {
        term = term.Trim();
        if (term.Length < 2) return new();

        var t = Escape(term);
        // anr = busca "ambígua" do AD (nome, sobrenome, nome de exibição, login, e-mail);
        // os curingas cobrem pedaços no meio do login ou do nome.
        var filter = "(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))"
                     + $"(|(anr={t})(sAMAccountName=*{t}*)(displayName=*{t}*)))";
        try
        {
            using var searcher = new DirectorySearcher(filter, ["sAMAccountName", "displayName", "mail", "department", "title"])
            {
                SizeLimit = Math.Clamp(take, 1, 50),
                ClientTimeout = TimeSpan.FromSeconds(10),
            };
            using var results = searcher.FindAll();
            return results.Cast<SearchResult>()
                .Select(r => new AdUserDto(
                    Prop(r, "sAMAccountName") ?? "",
                    Prop(r, "displayName"),
                    Prop(r, "mail"),
                    Prop(r, "department"),
                    Prop(r, "title")))
                .Where(u => u.Login.Length > 0)
                .OrderBy(u => u.DisplayName ?? u.Login, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning("Busca no AD falhou para \"{Term}\": {Error}", term, ex.Message);
            throw new InvalidOperationException("Não foi possível consultar o AD agora.");
        }
    }

    /// <summary>
    /// Grupos para o seletor. Por padrão só os que começam com o prefixo da loja (LOJA-); com <paramref name="all"/>
    /// procura em todo o domínio, mas exige ao menos 2 letras para não listar o AD inteiro.
    /// </summary>
    public List<AdGroupDto> SearchGroups(string term, bool all = false, int take = 30)
    {
        term = term.Trim();
        var t = Escape(term);
        var prefix = Escape(options.Value.GroupPrefix?.Trim() ?? "");

        string filter;
        if (all)
        {
            if (term.Length < 2) return new();
            filter = $"(&(objectCategory=group)(|(anr={t})(sAMAccountName=*{t}*)(description=*{t}*)))";
        }
        else
        {
            if (prefix.Length == 0 && term.Length < 2) return new();
            var byPrefix = prefix.Length > 0 ? $"(sAMAccountName={prefix}*)" : "";
            var byTerm = term.Length > 0 ? $"(|(sAMAccountName=*{t}*)(description=*{t}*))" : "";
            filter = $"(&(objectCategory=group){byPrefix}{byTerm})";
        }

        try
        {
            using var searcher = new DirectorySearcher(filter, ["sAMAccountName", "description", "objectSid", "member"])
            {
                SizeLimit = Math.Clamp(take, 1, 50),
                ClientTimeout = TimeSpan.FromSeconds(10),
            };
            using var results = searcher.FindAll();
            return results.Cast<SearchResult>()
                .Select(ToGroup)
                .Where(g => g is not null)
                .Select(g => g!)
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning("Busca de grupos no AD falhou para \"{Term}\": {Error}", term, ex.Message);
            throw new InvalidOperationException("Não foi possível consultar o AD agora.");
        }
    }

    /// <summary>
    /// SIDs de TODOS os grupos do token da pessoa (diretos, aninhados e o grupo primário), pelo atributo tokenGroups do AD:
    /// o mesmo conjunto que o Windows põe no login e que o AccessService compara. Null se a pessoa não existe.
    /// </summary>
    public HashSet<string>? TokenGroups(string login)
    {
        login = login.Trim();
        if (login.Length is 0 or > 64) return null;
        try
        {
            var filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={Escape(login)}))";
            using var searcher = new DirectorySearcher(filter, ["distinguishedName"]) { SizeLimit = 1, ClientTimeout = TimeSpan.FromSeconds(10) };
            var found = searcher.FindOne();
            if (found is null) return null;
            using var entry = found.GetDirectoryEntry();
            entry.RefreshCache(["tokenGroups"]);
            var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in entry.Properties["tokenGroups"])
            {
                if (value is byte[] raw) sids.Add(new SecurityIdentifier(raw, 0).Value);
            }
            return sids;
        }
        catch (Exception ex)
        {
            log.LogWarning("Grupos de {User} no AD: consulta falhou: {Error}", login, ex.Message);
            throw new InvalidOperationException("Não foi possível consultar o AD agora.");
        }
    }

    /// <summary>Detalhe de um grupo pelo SID: membros diretos, grupos aninhados e grupos que o contêm (em qualquer nível).</summary>
    public AdGroupDetailsDto? GroupDetails(string sid, int maxMembers = 200)
    {
        if (!sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var searcher = new DirectorySearcher($"(&(objectCategory=group)(objectSid={Escape(sid)}))", ["sAMAccountName", "description", "objectSid", "member", "distinguishedName"])
            {
                SizeLimit = 1,
                ClientTimeout = TimeSpan.FromSeconds(10),
            };
            var found = searcher.FindOne();
            if (found is null || ToGroup(found) is not { } group) return null;
            var dn = Prop(found, "distinguishedName");
            if (dn is null) return null;
            var listed = group.MemberCount is not null; // grupos primários não têm "member"

            var members = new List<AdMemberDto>();
            if (listed)
            {
                using var people = new DirectorySearcher($"(&(objectCategory=person)(objectClass=user)(memberOf={Escape(dn)}))", ["sAMAccountName", "displayName", "department"])
                {
                    SizeLimit = maxMembers,
                    ClientTimeout = TimeSpan.FromSeconds(10),
                };
                using var results = people.FindAll();
                members = results.Cast<SearchResult>()
                    .Select(r => new AdMemberDto(Prop(r, "sAMAccountName") ?? "", Prop(r, "displayName"), Prop(r, "department")))
                    .Where(m => m.Login.Length > 0)
                    .OrderBy(m => m.DisplayName ?? m.Login, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            return new AdGroupDetailsDto(group, members, group.MemberCount ?? 0, listed,
                GroupsWhere($"(&(objectCategory=group)(memberOf={Escape(dn)}))"),
                // 1.2.840.113556.1.4.1941 = "em cadeia": traz os grupos que contêm este em qualquer nível de aninhamento.
                GroupsWhere($"(&(objectCategory=group)(member:1.2.840.113556.1.4.1941:={Escape(dn)}))"));
        }
        catch (Exception ex)
        {
            log.LogWarning("Detalhe do grupo {Sid} no AD falhou: {Error}", sid, ex.Message);
            throw new InvalidOperationException("Não foi possível consultar o AD agora.");
        }
    }

    private static List<AdGroupRefDto> GroupsWhere(string filter)
    {
        using var searcher = new DirectorySearcher(filter, ["sAMAccountName", "objectSid"]) { SizeLimit = 100, ClientTimeout = TimeSpan.FromSeconds(10) };
        using var results = searcher.FindAll();
        return results.Cast<SearchResult>()
            .Select(r => r.Properties.Contains("objectSid") && r.Properties["objectSid"][0] is byte[] raw && Prop(r, "sAMAccountName") is { } name
                ? new AdGroupRefDto(name, new SecurityIdentifier(raw, 0).Value)
                : null)
            .Where(g => g is not null)
            .Select(g => g!)
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static AdGroupDto? ToGroup(SearchResult r)
    {
        var name = Prop(r, "sAMAccountName");
        if (string.IsNullOrEmpty(name) || !r.Properties.Contains("objectSid") || r.Properties["objectSid"][0] is not byte[] raw) return null;
        var sid = new SecurityIdentifier(raw, 0).Value;

        // Domain Users (513), Guests (514) e Computers (515): a participação vem do grupo primário, não de "member".
        int? members = sid.EndsWith("-513") || sid.EndsWith("-514") || sid.EndsWith("-515") ? null : MemberCount(r);
        return new AdGroupDto(name, Prop(r, "description"), sid, members);
    }

    /// <summary>"member" vem em páginas de 1500; acima disso o AD devolve o atributo como "member;range=0-1499".</summary>
    private static int MemberCount(SearchResult r)
    {
        if (r.Properties.Contains("member")) return r.Properties["member"].Count;
        var ranged = r.Properties.PropertyNames.Cast<string>().FirstOrDefault(n => n.StartsWith("member;range=", StringComparison.OrdinalIgnoreCase));
        return ranged is null ? 0 : r.Properties[ranged].Count;
    }

    private static string? Prop(SearchResult r, string name) =>
        r.Properties.Contains(name) && r.Properties[name].Count > 0 ? r.Properties[name][0]?.ToString() : null;

    /// <summary>Escapa caracteres especiais de filtro LDAP (RFC 4515).</summary>
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\\' => @"\5c",
                '*' => @"\2a",
                '(' => @"\28",
                ')' => @"\29",
                '\0' => @"\00",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
