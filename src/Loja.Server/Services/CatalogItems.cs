using System.Text.RegularExpressions;
using Loja.Server.Data;
using Loja.Shared;

namespace Loja.Server.Services;

/// <summary>Regras de versão da loja: uma só, para o app, o painel e a atualização automática dizerem a mesma coisa.</summary>
public static partial class VersionRules
{
    // Versão no estilo NuGet/Chocolatey: 1 a 5 números separados por ponto, com sufixo opcional (1.0.0-beta, 1.0+build).
    [GeneratedRegex(@"^\d+(\.\d+){0,4}([-+][0-9A-Za-z.\-+]*)?$")]
    private static partial Regex Comparable();

    /// <summary>Dá para comparar? Texto como "latest" ou "158.0 (x64 pt-BR)" não dá, e nunca vira "atualização disponível".</summary>
    public static bool IsComparable(string? version) => version is not null && Comparable().IsMatch(version.Trim());

    /// <summary>Estado de um pacote do Chocolatey frente à versão disponível na loja (ver <see cref="VersionStates"/>).</summary>
    public static string StateOf(string? installed, string? available)
    {
        if (installed is null) return VersionStates.None;
        if (!IsComparable(installed) || !IsComparable(available)) return VersionStates.Unknown;
        var order = VersionComparer.Instance.Compare(installed.Trim(), available!.Trim());
        return order < 0 ? VersionStates.Update : order == 0 ? VersionStates.Current : VersionStates.Newer;
    }

    /// <summary>Instalada é MENOR que a disponível (e as duas são comparáveis). Só neste caso se oferece atualizar.</summary>
    public static bool IsOutdated(string? installed, string? available) => StateOf(installed, available) == VersionStates.Update;
}

/// <summary>
/// Nomes que o programa de cada aplicativo tem em "Programas e Recursos". A comparação é por nome EXATO (sem diferenciar
/// maiúsculas nem espaços repetidos): nada de semelhança, para um programa parecido nunca ser tomado pelo aplicativo errado.
/// </summary>
public static partial class ProgramAliases
{
    public const int MaxPerApp = 10;
    public const int MaxLength = 200;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\p{C}]")]
    private static partial Regex Control();

    public static string KeyOf(string name) => Spaces().Replace(name.Trim(), " ").ToLowerInvariant();

    /// <summary>Tira controle e espaço sobrando, descarta vazios e repetidos (pela chave) e limita o tamanho. Erro em português se passar do limite.</summary>
    public static (List<string> Aliases, string? Error) Clean(IEnumerable<string>? input)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        foreach (var raw in input ?? [])
        {
            var name = Spaces().Replace(Control().Replace(raw ?? "", " "), " ").Trim();
            if (name.Length == 0) continue;
            if (name.Length > MaxLength) return (new(), $"Um nome passa de {MaxLength} letras: \"{name[..40]}…\".");
            if (seen.Add(KeyOf(name))) result.Add(name);
        }
        return result.Count > MaxPerApp ? (new(), $"No máximo {MaxPerApp} nomes por aplicativo.") : (result, null);
    }
}

/// <summary>Monta o aplicativo como uma máquina o vê. Usado pelo catálogo do app e pelo "Ver como usuário" do painel.</summary>
public static class CatalogItems
{
    /// <summary>Chaves de comparação dos nomes cadastrados para o aplicativo (null se não há nenhum).</summary>
    public static IReadOnlySet<string>? KeysOf(IReadOnlyDictionary<int, List<string>> aliases, int appId) =>
        aliases.TryGetValue(appId, out var list) && list.Count > 0 ? list.Select(ProgramAliases.KeyOf).ToHashSet() : null;

    public static CatalogItemDto Build(AppRow app, HostInventory inventory, IReadOnlySet<string>? aliasKeys, bool hasIcon)
    {
        var installed = inventory.Versions.GetValueOrDefault(app.PackageId);
        var state = VersionRules.StateOf(installed, app.Version);

        // Fora da loja: só se o Chocolatey NÃO tem o pacote (evita contar duas vezes o que a loja instalou), os programas do
        // Windows foram coletados de verdade e algum programa tem exatamente um dos nomes cadastrados pela TI.
        HostProgram? outside = null;
        if (installed is null && inventory.ProgramsUtc is not null && aliasKeys is { Count: > 0 })
        {
            outside = inventory.Programs
                .Where(p => aliasKeys.Contains(ProgramAliases.KeyOf(p.Name)))
                .OrderByDescending(p => p.Version, VersionComparer.Instance)
                .FirstOrDefault();
            if (outside is not null) state = VersionStates.External;
        }

        return new CatalogItemDto(app.Id, app.PackageId, app.Source, app.Title, app.Description, app.Version, hasIcon,
            installed, state == VersionStates.Update, app.AllowUninstall,
            state, outside is not null, outside is { Version.Length: > 0 } ? outside.Version : null, outside?.Name);
    }
}
