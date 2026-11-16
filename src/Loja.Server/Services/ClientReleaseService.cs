using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Loja.Server.Data;
using Loja.Shared;

namespace Loja.Server.Services;

public record ClientReleaseDto(string Version, string Sha256, long Size, bool Mandatory, bool IsCurrent, DateTime PublishedUtc, string PublishedBy, int Machines);

/// <summary>
/// O que o painel mostra antes de publicar: a versão que o executável diz ter e o que acontece se for publicado.
/// Nada é gravado em DataDir\cliente.
/// </summary>
public record ClientInspectDto(string Version, string Sha256, long Size, bool AlreadyPublished, bool IsCurrent, string? CurrentVersion, bool OlderThanCurrent);

/// <summary>
/// Versões do app da loja publicadas pelo painel. O .exe fica em DataDir\cliente\{versão}.exe;
/// o app baixa pelo HTTPS (certificado fixado) e confere o SHA-256 antes de usar.
/// </summary>
public partial class ClientReleaseService(Db db, ClientRepository repo, ILogger<ClientReleaseService> log)
{
    public const long MaxSize = 250L * 1024 * 1024;
    private const string ProductName = "Loja de Aplicativos";

    [GeneratedRegex(@"^\d+\.\d+\.\d+(\.\d+)?$")]
    private static partial Regex VersionRegex();

    private record CheckedFile(string Version, string Sha256);

    private string Dir
    {
        get
        {
            var dir = Path.Combine(db.DataDir, "cliente");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// O app da loja viaja DENTRO da atualização do servidor (pasta "cliente" ao lado do servidor, como o Tactical entrega o agente):
    /// ao subir, o servidor lê esse .exe e, se não for a versão atual, publica e passa a oferecer. Ninguém publica versão pelo painel.
    /// Sem o arquivo, nada muda (continua valendo a última versão já publicada).
    /// </summary>
    public void SyncBundled(string? bundlePath, bool mandatory)
    {
        var path = string.IsNullOrWhiteSpace(bundlePath) ? Path.Combine(AppContext.BaseDirectory, "cliente", "LojaDeAplicativos.exe") : bundlePath;
        if (!File.Exists(path)) return;

        var (checkedFile, error) = ValidateAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        if (checkedFile is null)
        {
            log.LogError("O app que veio com o servidor ({Path}) foi recusado: {Error}", path, error);
            return;
        }

        var current = repo.Current();
        if (current is not null && current.Version == checkedFile.Version && current.Sha256 == checkedFile.Sha256 && PathOf(current.Version) is not null) return;

        var final = Path.Combine(Dir, $"{checkedFile.Version}.exe");
        File.Copy(path, final, overwrite: true);
        repo.Publish(checkedFile.Version, checkedFile.Sha256, new FileInfo(final).Length, mandatory, "atualização do servidor");
        log.LogWarning("App da loja {Version} veio com a atualização do servidor e passou a ser a versão atual (obrigatória: {Mandatory}, SHA-256 {Sha})",
            checkedFile.Version, mandatory, checkedFile.Sha256);
    }

    public string? PathOf(string version) =>
        VersionRegex().IsMatch(version) && File.Exists(Path.Combine(Dir, $"{version}.exe")) ? Path.Combine(Dir, $"{version}.exe") : null;

    public ClientLatestDto? Latest() =>
        repo.Current() is { } c ? new ClientLatestDto(c.Version, c.Sha256, c.Size, c.Mandatory) : null;

    public List<ClientReleaseDto> List()
    {
        var hosts = repo.ListHosts().Values.GroupBy(h => h.Version).ToDictionary(g => g.Key, g => g.Count());
        return repo.ListReleases()
            .Select(r => new ClientReleaseDto(r.Version, r.Sha256, r.Size, r.Mandatory, r.IsCurrent, r.PublishedUtc, r.PublishedBy, hosts.GetValueOrDefault(r.Version)))
            .ToList();
    }

    /// <summary>
    /// Confere o .exe enviado e diz que versão é e o que a publicação faria (substituir uma versão já publicada,
    /// voltar para uma mais antiga que a atual), sem publicar nada.
    /// </summary>
    public async Task<(ClientInspectDto? Result, string? Error)> InspectAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length is 0 or > MaxSize) return (null, "Arquivo vazio ou grande demais.");

        var temp = Path.Combine(Dir, $"envio-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = File.Create(temp))
                await file.CopyToAsync(output, ct);

            var (checkedFile, error) = await ValidateAsync(temp, ct);
            if (checkedFile is null) return (null, error);

            var releases = repo.ListReleases();
            var current = releases.FirstOrDefault(r => r.IsCurrent);
            var existing = releases.FirstOrDefault(r => r.Version == checkedFile.Version);
            var older = System.Version.TryParse(checkedFile.Version, out var candidate)
                        && current is not null
                        && System.Version.TryParse(current.Version, out var currentVersion)
                        && candidate < currentVersion;

            return (new ClientInspectDto(checkedFile.Version, checkedFile.Sha256, file.Length, existing is not null, existing?.IsCurrent == true, current?.Version, older), null);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Recebe o .exe enviado pelo painel, confere se é mesmo a Loja de Aplicativos e o torna a versão atual.</summary>
    public async Task<(ClientReleaseDto? Release, string? Error)> PublishAsync(IFormFile file, bool mandatory, string user, CancellationToken ct)
    {
        if (file.Length is 0 or > MaxSize) return (null, "Arquivo vazio ou grande demais.");

        var temp = Path.Combine(Dir, $"envio-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = File.Create(temp))
                await file.CopyToAsync(output, ct);

            var (checkedFile, error) = await ValidateAsync(temp, ct);
            if (checkedFile is null) return (null, error);

            var final = Path.Combine(Dir, $"{checkedFile.Version}.exe");
            File.Move(temp, final, overwrite: true);
            repo.Publish(checkedFile.Version, checkedFile.Sha256, new FileInfo(final).Length, mandatory, user);
            log.LogWarning("{User} publicou a versão {Version} do app da loja (obrigatória: {Mandatory}, SHA-256 {Sha})", user, checkedFile.Version, mandatory, checkedFile.Sha256);
            return (List().First(r => r.Version == checkedFile.Version), null);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>A mesma conferência para inspecionar e publicar: executável do Windows, produto, nome original, versão e SHA-256.</summary>
    private async Task<(CheckedFile? Result, string? Error)> ValidateAsync(string path, CancellationToken ct)
    {
        await using (var check = File.OpenRead(path))
        {
            var mz = new byte[2];
            if (await check.ReadAsync(mz, ct) != 2 || mz[0] != 'M' || mz[1] != 'Z')
                return (null, "O arquivo não é um executável do Windows.");
        }

        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName is null && info.OriginalFilename is null)
        {
            // O Windows não devolveu nenhuma informação de versão. Uma causa conhecida: o caminho do arquivo passar de 260 letras
            // (a API de versão não aceita caminho longo), o que só acontece com uma pasta de dados de nome enorme.
            log.LogWarning("Não foi possível ler as informações de versão do executável enviado ({Length} letras no caminho, {Size} bytes)", path.Length, new FileInfo(path).Length);
            return (null, path.Length >= 250
                ? "Não foi possível ler a versão do executável: o caminho da pasta de dados do servidor é longo demais."
                : "Não foi possível ler as informações de versão do executável. Confira se é o LojaDeAplicativos.exe gerado pelo Publicar.ps1.");
        }
        if (info.ProductName != ProductName || !string.Equals(info.OriginalFilename, "LojaDeAplicativos.dll", StringComparison.OrdinalIgnoreCase))
        {
            // Diz no log o que foi lido do arquivo, para dar para diferenciar "não é a loja" de "não deu para ler as informações".
            log.LogWarning("Executável recusado: produto \"{Product}\", nome original \"{Original}\", versão \"{Version}\", {Size} bytes", info.ProductName, info.OriginalFilename, info.ProductVersion, new FileInfo(path).Length);
            return (null, "Este executável não é a Loja de Aplicativos.");
        }

        var version = (info.ProductVersion ?? "").Split('+')[0];
        if (!VersionRegex().IsMatch(version)) return (null, $"Versão do executável inválida: \"{info.ProductVersion}\".");

        string sha;
        await using (var stream = File.OpenRead(path))
            sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));

        return (new CheckedFile(version, sha), null);
    }

    public bool SetCurrent(string version, bool mandatory, string user)
    {
        if (PathOf(version) is null || !repo.SetCurrent(version, mandatory)) return false;
        log.LogWarning("{User} definiu a versão {Version} do app como atual (obrigatória: {Mandatory})", user, version, mandatory);
        return true;
    }

    public bool Delete(string version, string user)
    {
        if (!VersionRegex().IsMatch(version) || !repo.Delete(version)) return false;
        var path = Path.Combine(Dir, $"{version}.exe");
        if (File.Exists(path)) File.Delete(path);
        log.LogInformation("{User} apagou a versão {Version} do app", user, version);
        return true;
    }
}
