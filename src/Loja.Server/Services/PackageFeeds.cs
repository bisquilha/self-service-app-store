using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml.Linq;
using Loja.Shared;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

public record PackageInfo(
    string Id,
    string Source,
    string Title,
    string? Version,
    string? Description,
    string? IconUrl,
    long DownloadCount,
    bool IsApproved,
    byte[]? EmbeddedIcon = null);

/// <summary>Repositório da comunidade Chocolatey (OData v2), a mesma fonte da lista de apps do Tactical.</summary>
public class CommunityFeed(IHttpClientFactory factory, IOptions<LojaOptions> options)
{
    public const string ClientName = "community";

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Meta = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";
    private static readonly XNamespace Data = "http://schemas.microsoft.com/ado/2007/08/dataservices";

    public Task<List<PackageInfo>> SearchAsync(string term, int take, CancellationToken ct) =>
        QueryAsync($"Search()?$filter=IsLatestVersion&$skip=0&$top={take}&searchTerm={OData(term)}&targetFramework=%27%27&includePrerelease=false", ct);

    public async Task<PackageInfo?> GetAsync(string id, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"Id eq '{id.Replace("'", "''")}' and IsLatestVersion");
        var latest = await QueryAsync($"Packages()?$filter={filter}", ct);
        var match = latest.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        // Plano B (mais pesado, traz todas as versões): pega a maior versão estável.
        var all = await QueryAsync($"FindPackagesById()?id={OData(id)}", ct);
        return all.Where(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) && p.Version?.Contains('-') != true)
                  .OrderByDescending(p => p.Version, VersionComparer.Instance)
                  .FirstOrDefault();
    }

    private async Task<List<PackageInfo>> QueryAsync(string relativeUrl, CancellationToken ct)
    {
        var http = factory.CreateClient(ClientName);
        // O endereço vem das Configurações (pode mudar com o servidor no ar): a pergunta usa o endereço completo em vigor.
        var baseUrl = options.Value.CommunityFeedUrl.TrimEnd('/') + "/";
        using var resp = await http.GetAsync(new Uri(new Uri(baseUrl), relativeUrl), ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct);

        var result = new List<PackageInfo>();
        foreach (var entry in doc.Root?.Elements(Atom + "entry") ?? Enumerable.Empty<XElement>())
        {
            var props = entry.Element(Meta + "properties");
            string? P(string name) => props?.Element(Data + name)?.Value;

            var id = P("Id") ?? entry.Element(Atom + "title")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var summary = P("Summary");
            result.Add(new PackageInfo(
                id,
                PackageSources.Community,
                NonEmpty(P("Title")) ?? id,
                P("Version"),
                NonEmpty(summary) ?? P("Description"),
                NonEmpty(P("IconUrl")),
                long.TryParse(P("DownloadCount"), out var downloads) ? downloads : 0,
                string.Equals(P("IsApproved"), "true", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    private static string OData(string value) => Uri.EscapeDataString("'" + value.Replace("'", "''") + "'");

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Feed interno: pasta (UNC) com pacotes .nupkg, a mesma que o Chocolatey usa como "source" nas máquinas.
/// Qualquer pacote colocado ali aparece automaticamente na busca do painel de administração.
/// </summary>
public class InternalFeed(IOptions<LojaOptions> options, ConfigService config, ILogger<InternalFeed> log)
{
    private readonly ConcurrentDictionary<string, (DateTime Stamp, PackageInfo? Info)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured => config.InternalAppsEnabled && !string.IsNullOrWhiteSpace(options.Value.InternalFeedPath);

    public List<PackageInfo> ListLatest()
    {
        var path = options.Value.InternalFeedPath;
        if (!config.InternalAppsEnabled || string.IsNullOrWhiteSpace(path)) return new();

        List<string> files;
        try
        {
            if (!Directory.Exists(path))
            {
                log.LogWarning("Feed interno inacessível: {Path}", path);
                return new();
            }
            files = Directory.EnumerateFiles(path, "*.nupkg", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Falha ao ler o feed interno {Path}", path);
            return new();
        }

        var infos = new List<PackageInfo>();
        foreach (var file in files)
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            if (!_cache.TryGetValue(file, out var entry) || entry.Stamp != stamp)
            {
                entry = (stamp, Read(file));
                _cache[file] = entry;
            }
            if (entry.Info is not null) infos.Add(entry.Info);
        }

        return infos.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(i => i.Version, VersionComparer.Instance).First())
                    .ToList();
    }

    public List<PackageInfo> Search(string term) =>
        ListLatest()
            .Where(p => string.IsNullOrWhiteSpace(term)
                        || p.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || p.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public PackageInfo? Get(string id) =>
        ListLatest().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private PackageInfo? Read(string file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            var specEntry = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (specEntry is null) return null;

            XDocument spec;
            using (var s = specEntry.Open()) spec = XDocument.Load(s);
            var metadata = spec.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            string? M(string name) => metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();

            var id = M("id");
            if (string.IsNullOrWhiteSpace(id)) return null;

            byte[]? icon = null;
            var iconPath = M("icon");
            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                var normalized = iconPath.Replace('\\', '/').TrimStart('/');
                var iconEntry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, normalized, StringComparison.OrdinalIgnoreCase));
                if (iconEntry is not null && iconEntry.Length < 1_000_000)
                {
                    using var s = iconEntry.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    icon = ms.ToArray();
                }
            }

            var title = M("title");
            return new PackageInfo(
                id,
                PackageSources.Internal,
                string.IsNullOrWhiteSpace(title) ? id : title,
                M("version"),
                string.IsNullOrWhiteSpace(M("summary")) ? M("description") : M("summary"),
                M("iconUrl"),
                0,
                true,
                icon);
        }
        catch (Exception ex)
        {
            log.LogWarning("Pacote interno ilegível {File}: {Error}", file, ex.Message);
            return null;
        }
    }
}

/// <summary>Compara versões no estilo NuGet (1.2.10 > 1.2.9; estável > pré-lançamento).</summary>
public sealed class VersionComparer : IComparer<string?>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a == b) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        var (aNum, aPre) = Split(a);
        var (bNum, bPre) = Split(b);
        var aParts = aNum.Split('.');
        var bParts = bNum.Split('.');
        for (var i = 0; i < Math.Max(aParts.Length, bParts.Length); i++)
        {
            var x = i < aParts.Length && long.TryParse(aParts[i], out var xa) ? xa : 0;
            var y = i < bParts.Length && long.TryParse(bParts[i], out var yb) ? yb : 0;
            if (x != y) return x.CompareTo(y);
        }

        if (aPre.Length == 0 && bPre.Length > 0) return 1;
        if (aPre.Length > 0 && bPre.Length == 0) return -1;
        return string.Compare(aPre, bPre, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Number, string Prerelease) Split(string version)
    {
        var v = version.Split('+')[0];
        var dash = v.IndexOf('-');
        return dash < 0 ? (v, "") : (v[..dash], v[(dash + 1)..]);
    }
}
