using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Loja.Shared;

namespace Loja.App;

/// <summary>
/// Autoupdate do app da loja, sem nunca sobrescrever o .exe em uso:
/// - as versões baixadas ficam em %LOCALAPPDATA%\LojaDeAplicativos\versoes\{versão}\LojaDeAplicativos.exe;
/// - "atual.txt" diz qual versão o servidor mandou usar;
/// - o .exe do atalho (Program Files, via GPO) só abre a versão de "atual.txt" quando ela é outra.
/// Quem decide a versão é o servidor (painel), então dá para voltar para uma anterior.
/// </summary>
public static class Updater
{
    private const string NoRedirectFlag = "--sem-redirecionar";
    private const string ExeName = "LojaDeAplicativos.exe";

    public static Version Current { get; } =
        Parse(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0);

    public static string CurrentText => Current.ToString(3);

    private static string VersionsDir =>
        Environment.GetEnvironmentVariable("LOJA_VERSIONS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LojaDeAplicativos", "versoes");

    private static string TargetFile => Path.Combine(VersionsDir, "atual.txt");

    public static Version? Parse(string? text)
    {
        var clean = (text ?? "").Split('+')[0].Trim();
        return Version.TryParse(clean, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    private static string ExePath(Version v) => Path.Combine(VersionsDir, v.ToString(3), ExeName);

    /// <summary>
    /// Na abertura: se o servidor mandou usar outra versão e ela já foi baixada, abre essa e devolve true
    /// (quem chamou encerra). A versão aberta assim recebe --sem-redirecionar, para nunca entrar em ciclo.
    /// </summary>
    public static bool RedirectIfNeeded(string[] args, string configPath)
    {
        if (args.Contains(NoRedirectFlag)) return false;
        try
        {
            var target = File.Exists(TargetFile) ? Parse(File.ReadAllText(TargetFile)) : null;
            if (target is null || target == Current || !File.Exists(ExePath(target))) return false;
            Launch(target, args, configPath);
            return true;
        }
        catch (Exception)
        {
            return false; // na dúvida, abre a versão que o usuário clicou
        }
    }

    /// <summary>Abre a versão informada repassando os argumentos e a configuração original.</summary>
    public static void Launch(Version version, string[] args, string configPath)
    {
        var psi = new ProcessStartInfo(ExePath(version)) { UseShellExecute = false };
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == NoRedirectFlag) continue;
            if (args[i] == "--config") { i++; continue; }
            psi.ArgumentList.Add(args[i]);
        }
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(configPath);
        psi.ArgumentList.Add(NoRedirectFlag);
        Process.Start(psi);
    }

    /// <summary>
    /// Pergunta ao servidor qual versão usar. Se for outra, baixa (em segundo plano), confere o SHA-256
    /// e grava em "atual.txt". Devolve a versão pronta para uso, ou null se não há nada a fazer.
    /// </summary>
    public static async Task<(Version Version, bool Mandatory)?> CheckAsync(LojaApi api, CancellationToken ct)
    {
        var latest = await api.GetLatestClientAsync();
        if (latest is null || Parse(latest.Version) is not { } version) return null;

        Directory.CreateDirectory(VersionsDir);
        if (version == Current)
        {
            await File.WriteAllTextAsync(TargetFile, version.ToString(3), ct);
            Cleanup(version);
            return null;
        }

        var exe = ExePath(version);
        if (!await MatchesAsync(exe, latest.Sha256, ct))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            var part = exe + ".part";
            await api.DownloadClientAsync(latest.Version, part, ct);
            if (!await MatchesAsync(part, latest.Sha256, ct))
            {
                File.Delete(part);
                throw new InvalidDataException("A versão baixada não confere com o SHA-256 informado pelo servidor.");
            }
            File.Move(part, exe, overwrite: true);
        }

        await File.WriteAllTextAsync(TargetFile, version.ToString(3), ct);
        Cleanup(version);
        return (version, latest.Mandatory);
    }

    private static async Task<bool> MatchesAsync(string path, string sha256, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Apaga versões baixadas que não são a em uso nem a que o servidor mandou usar.</summary>
    private static void Cleanup(Version keep)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(VersionsDir))
            {
                var v = Parse(Path.GetFileName(dir));
                if (v is null || v == keep || v == Current) continue;
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException)
        {
            // limpeza é só cortesia
        }
    }
}
