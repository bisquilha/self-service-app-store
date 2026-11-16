using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace Loja.Server.Services;

/// <summary>
/// Explica por que a pasta do feed interno (NAS) não abriu, mostrando o que foi OBSERVADO (conta do servidor, nome do NAS resolvido,
/// porta 445, resultado de abrir a pasta) separado do que é só HIPÓTESE. Só lê: não grava nada e não muda nenhuma permissão.
/// </summary>
public static class FeedDiagnosis
{
    /// <summary>Conta com que o servidor roda e como ela aparece para o NAS (numa rede de domínio, pool do IIS e serviços do sistema aparecem como a conta do computador).</summary>
    public static string IdentityText()
    {
        string name;
        try
        {
            name = WindowsIdentity.GetCurrent().Name;
        }
        catch (Exception)
        {
            return "Conta do servidor: não foi possível ler.";
        }

        var machine = Environment.MachineName.ToUpperInvariant();
        var asComputer = name.StartsWith(@"IIS APPPOOL\", StringComparison.OrdinalIgnoreCase)
                         || name.Equals(@"NT AUTHORITY\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase)
                         || name.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);
        if (asComputer) return $"Conta do servidor: {name}. Na rede, o NAS enxerga a conta de computador {machine}$ do domínio.";
        if (name.Equals(@"NT AUTHORITY\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)) return $"Conta do servidor: {name}. Na rede, o NAS a enxerga como anônima.";
        return $"Conta do servidor: {name}. O NAS enxerga esta mesma conta.";
    }

    /// <summary>Para um caminho UNC (\\nome\pasta): o nome resolve? A porta 445 (SMB) responde? Vazio para caminho local.</summary>
    public static string? NetworkEvidence(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var host = path.TrimStart('\\').Split('\\')[0];
        if (host.Length == 0) return null;

        IPAddress[] addresses;
        try
        {
            var lookup = Dns.GetHostAddressesAsync(host);
            if (!lookup.Wait(TimeSpan.FromSeconds(3))) return $"Rede: o nome {host} demorou demais para resolver.";
            addresses = lookup.Result;
        }
        catch (Exception)
        {
            return $"Rede: o nome {host} não resolve neste servidor.";
        }
        if (addresses.Length == 0) return $"Rede: o nome {host} não resolve neste servidor.";

        var first = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(first, 445);
            return connect.Wait(TimeSpan.FromSeconds(2)) && client.Connected
                ? $"Rede: {host} resolve para {first} e a porta 445 (SMB) responde."
                : $"Rede: {host} resolve para {first}, mas a porta 445 (SMB) não respondeu em 2 segundos.";
        }
        catch (Exception)
        {
            return $"Rede: {host} resolve para {first}, mas a porta 445 (SMB) recusou a conexão.";
        }
    }

    /// <summary>O que aconteceu ao tentar abrir a pasta, e a hipótese que esse resultado sugere (sempre dita como hipótese).</summary>
    public static (bool Opened, string Observed, string Hypothesis) OpenEvidence(string path)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return (true, "Abrir a pasta: funcionou agora.", "");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Abrir a pasta: acesso negado (UnauthorizedAccessException).",
                "Hipótese a confirmar: a conta acima não tem permissão de leitura no compartilhamento ou na pasta do NAS.");
        }
        catch (DirectoryNotFoundException)
        {
            return (false, "Abrir a pasta: o caminho não foi encontrado (DirectoryNotFoundException).",
                "Hipótese a confirmar: o nome do compartilhamento ou da pasta é outro (por exemplo depois de trocar o NAS), ou o compartilhamento não está publicado.");
        }
        catch (IOException ex) when ((uint)ex.HResult == 0x80070035)
        {
            return (false, "Abrir a pasta: caminho de rede não encontrado (erro 53).",
                "Hipótese a confirmar: o nome do NAS não resolve aqui, o NAS está desligado ou a rota até ele está bloqueada.");
        }
        catch (IOException ex) when ((uint)ex.HResult == 0x800704C3)
        {
            return (false, "Abrir a pasta: o Windows recusou por já haver outra conexão ao mesmo NAS com outras credenciais (erro 1219).",
                "Hipótese a confirmar: uma conexão antiga com outra conta está aberta neste servidor para o mesmo NAS.");
        }
        catch (IOException ex)
        {
            return (false, $"Abrir a pasta: erro de rede ou de disco ({ex.GetType().Name}, código 0x{(uint)ex.HResult:X8}: {ex.Message}).", "");
        }
        catch (Exception ex)
        {
            return (false, $"Abrir a pasta: {ex.GetType().Name}: {ex.Message}", "");
        }
    }

    /// <summary>Texto completo para o cartão "Feed interno (NAS)" quando a pasta não abre.</summary>
    public static string Explain(string path)
    {
        var (_, observed, hypothesis) = OpenEvidence(path);
        var parts = new List<string> { IdentityText() };
        if (NetworkEvidence(path) is { } network) parts.Add(network);
        parts.Add(observed);
        if (hypothesis.Length > 0) parts.Add(hypothesis);
        parts.Add("Nada foi alterado.");
        return string.Join(' ', parts);
    }
}
