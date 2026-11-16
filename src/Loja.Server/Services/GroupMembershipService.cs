using System.Collections.Concurrent;

namespace Loja.Server.Services;

/// <summary>
/// "Esta pessoa está neste grupo?", para o filtro "grupo do usuário" do Histórico e das Máquinas.
/// Usa o tokenGroups do AD (diretos, aninhados e grupo primário), com cache curto: a consulta é por pessoa que aparece na tela,
/// não pelo domínio inteiro. Vale a associação ATUAL, não a do momento em que o pedido foi feito.
/// </summary>
public class GroupMembershipService(DirectoryService directory, ILogger<GroupMembershipService> log)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (HashSet<string>? Sids, DateTime Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SIDs do token da pessoa (cache de 10 min). Null: a pessoa não existe no AD. Lança se o AD não responder.</summary>
    public HashSet<string>? SidsOf(string login)
    {
        var key = login.Trim();
        if (_cache.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow) return hit.Sids;
        var sids = directory.TokenGroups(key);
        _cache[key] = (sids, DateTime.UtcNow + Ttl);
        return sids;
    }

    /// <summary>
    /// Dos logins informados, os que pertencem ao grupo (SID). "*" (todos os usuários) devolve todos.
    /// Consulta o AD só para quem não está no cache, em poucas conexões ao mesmo tempo. Se o AD falhar, a exceção sobe.
    /// </summary>
    public HashSet<string> Members(string sid, IEnumerable<string> logins)
    {
        var distinct = logins.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sid == AccessService.Everyone) return new HashSet<string>(distinct, StringComparer.OrdinalIgnoreCase);

        var failure = default(Exception);
        var pending = distinct.Where(l => !(_cache.TryGetValue(l, out var hit) && hit.Expires > DateTime.UtcNow)).ToList();
        if (pending.Count > 0)
        {
            Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = 6 }, login =>
            {
                try { SidsOf(login); }
                catch (Exception ex) { failure ??= ex; }
            });
            log.LogInformation("Filtro por grupo: {Count} pessoa(s) consultada(s) no AD", pending.Count);
        }
        if (failure is not null) throw failure;

        return distinct.Where(l => SidsOf(l)?.Contains(sid) == true).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"CORP\ana.souza (painel)" → "ana.souza". Lançamentos automáticos não têm pessoa: devolve null.</summary>
    public static string? LoginOf(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var s = stored.Trim();
        var suffix = s.IndexOf(" (", StringComparison.Ordinal);
        if (suffix >= 0) s = s[..suffix];
        s = AccessService.ShortName(s);
        return s.Length == 0 || s.StartsWith('(') ? null : s;
    }
}
