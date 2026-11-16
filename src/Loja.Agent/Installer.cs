using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace Loja.Agent;

/// <summary>
/// Instalação do próprio agente, em um comando e sem perguntas (serve igual para GPO, Tactical, Intune ou à mão):
///   LojaAgent.exe install --server https://loja.empresa.local [--cert SHA256] [--choco CAMINHO] [--internal-feed PASTA]
///   LojaAgent.exe uninstall [--purge]
///   LojaAgent.exe status
/// Rodar de novo o "install" atualiza no lugar (para o serviço, troca o .exe, inicia). Códigos de saída: 0 ok, 1 erro, 2 sem permissão.
/// </summary>
public static class Installer
{
    public const string ServiceName = "LojaAgent";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Loja de Aplicativos", "Agent");

    public static string ExePath => Path.Combine(InstallDir, "LojaAgent.exe");

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0].ToLowerInvariant() is "install" or "uninstall" or "status" or "version" or "help" or "--help" or "-h" or "/?";

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0].ToLowerInvariant();
        var opt = ParseOptions(args.Skip(1));
        try
        {
            switch (command)
            {
                case "version":
                    Console.WriteLine(ServerClient.Version);
                    return 0;
                case "status":
                    return await StatusAsync();
                case "install":
                    if (!IsAdmin()) return Fail("Rode como administrador (ou como SYSTEM).", 2);
                    return Install(opt);
                case "uninstall":
                    if (!IsAdmin()) return Fail("Rode como administrador (ou como SYSTEM).", 2);
                    return Uninstall(opt.ContainsKey("purge"));
                default:
                    Console.WriteLine(Help);
                    return 0;
            }
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, 1);
        }
    }

    private const string Help = """
        Agente da Loja de Aplicativos
          LojaAgent.exe install --server URL [--cert SHA256] [--choco CAMINHO] [--internal-feed PASTA]
              Instala (ou atualiza) o servico, grava a configuracao e inicia. Sem perguntas.
          LojaAgent.exe uninstall [--purge]     Remove o servico e os arquivos (--purge apaga tambem configuracao e log)
          LojaAgent.exe status                  Mostra o servico, a configuracao e se o servidor responde
          LojaAgent.exe version
        """;

    private static int Install(Dictionary<string, string> opt)
    {
        // Servidor e controlador de domínio: não instala (e sai sem erro, para a GPO ou o Tactical não acusarem falha).
        if (SystemInfo.ProductType() != 1 && Environment.GetEnvironmentVariable("LOJAAGENT_ALLOWSERVEROS") is not ("1" or "true"))
        {
            Console.WriteLine("Este computador e um servidor: o agente da loja so e instalado em estacoes. Nada foi feito.");
            return 0;
        }

        // Configuração: o que veio na linha de comando; o que faltar vem do arquivo que já existe (atualização sem repetir tudo).
        var current = ConfigLoader.Load(readEnvironment: false);
        if (opt.TryGetValue("server", out var server)) current.ServerUrl = server.TrimEnd('/');
        if (opt.TryGetValue("cert", out var cert)) current.CertSha256 = ServerClient.Normalize(cert);
        if (opt.TryGetValue("choco", out var choco)) current.ChocoPath = choco;
        if (opt.TryGetValue("internal-feed", out var feed)) current.InternalFeed = feed;

        if (!Uri.TryCreate(current.ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return Fail("Informe o servidor da loja: --server https://loja.suaempresa.local", 1);
        if (uri.Scheme == "http" && !uri.IsLoopback)
            return Fail("Servidor sem HTTPS so e aceito em localhost (testes).", 1);
        if (uri.Scheme == "https" && current.CertSha256.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(current.CertSha256, "^[0-9A-Fa-f]{64}$"))
            return Fail("--cert deve ser o SHA-256 do certificado (64 caracteres hexadecimais).", 1);

        var dataDir = current.ResolvedDataDir();
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(ConfigLoader.DefaultPath, JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Configuracao gravada em {ConfigLoader.DefaultPath}");

        var existed = ServiceExists();
        if (existed) { StopService(); }

        Directory.CreateDirectory(InstallDir);
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Nao foi possivel achar o proprio executavel.");
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(self, ExePath, overwrite: true);
            Console.WriteLine($"Programa copiado para {ExePath}");
        }

        if (!existed)
        {
            // "binPath=" e o valor vao como argumentos separados, como o sc.exe exige.
            Sc("create", ServiceName, "binPath=", $"\"{ExePath}\"", "start=", "delayed-auto", "DisplayName=", "Loja de Aplicativos - Agente");
            Sc("description", ServiceName, "Executa, nesta estacao, as instalacoes aprovadas pela Loja de Aplicativos da TI.");
        }
        else
        {
            Sc("config", ServiceName, "binPath=", $"\"{ExePath}\"", "start=", "delayed-auto");
        }
        // Se o servico cair, o Windows o reinicia (1 min, 1 min, 1 min); zera a contagem depois de 1 dia.
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000");

        Sc("start", ServiceName);
        Thread.Sleep(3000);
        var state = ServiceState();
        Console.WriteLine($"Servico {ServiceName}: {state}");
        return state == "RUNNING" ? 0 : Fail("O servico nao ficou em execucao. Veja o log em " + Path.Combine(dataDir, "agent.log"), 1);
    }

    private static int Uninstall(bool purge)
    {
        if (ServiceExists())
        {
            StopService();
            Sc("delete", ServiceName);
            Console.WriteLine("Servico removido.");
        }
        else
        {
            Console.WriteLine("O servico nao estava instalado.");
        }

        TryDelete(InstallDir);
        if (purge) TryDelete(ConfigLoader.DefaultDir);
        Console.WriteLine(purge ? "Arquivos, configuracao e log removidos." : "Arquivos removidos (configuracao e log mantidos; use --purge para apagar).");
        return 0;
    }

    private static async Task<int> StatusAsync()
    {
        Console.WriteLine($"Versao do agente : {ServerClient.Version}");
        Console.WriteLine($"Servico          : {(ServiceExists() ? ServiceState() : "NAO INSTALADO")}");
        var o = ConfigLoader.Load(readEnvironment: true);
        Console.WriteLine($"Configuracao     : {ConfigLoader.DefaultPath} ({(File.Exists(ConfigLoader.DefaultPath) ? "existe" : "nao existe")})");
        Console.WriteLine($"Servidor         : {(o.ServerUrl.Length > 0 ? o.ServerUrl : "(nao configurado)")}");
        Console.WriteLine($"Chocolatey       : {o.ResolvedChocoPath()} ({(File.Exists(o.ResolvedChocoPath()) ? "ok" : "NAO ENCONTRADO")})");
        Console.WriteLine($"Tipo do Windows  : {(SystemInfo.ProductType() == 1 ? "estacao" : "servidor (a loja nao instala aqui)")}");
        if (o.ServerUrl.Length == 0) return 1;

        using var client = new ServerClient(o);
        var (ok, message) = await client.PingAsync(CancellationToken.None);
        Console.WriteLine($"Servidor responde: {(ok ? "sim" : "NAO")} ({message})");
        return ok ? 0 : 1;
    }

    // --- sc.exe e arquivos ---

    private static bool ServiceExists() => RunSc("query", ServiceName).ExitCode == 0;

    private static string ServiceState()
    {
        var text = RunSc("query", ServiceName).Output;
        foreach (var s in new[] { "RUNNING", "STOPPED", "START_PENDING", "STOP_PENDING" })
            if (text.Contains(s, StringComparison.Ordinal)) return s;
        return "DESCONHECIDO";
    }

    private static void StopService()
    {
        if (ServiceState() == "STOPPED") return;
        RunSc("stop", ServiceName);
        for (var i = 0; i < 30 && ServiceState() != "STOPPED"; i++) Thread.Sleep(1000);
    }

    private static void Sc(params string[] args)
    {
        var r = RunSc(args);
        if (r.ExitCode != 0 && args[0] is not ("start" or "stop"))
            throw new InvalidOperationException($"sc.exe {args[0]} falhou ({r.ExitCode}): {r.Output.Trim()}");
    }

    private static (int ExitCode, string Output) RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Nao foi possivel apagar {dir}: {ex.Message}");
        }
    }

    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int Fail(string message, int code)
    {
        Console.Error.WriteLine("ERRO: " + message);
        return code;
    }

    /// <summary>"--chave valor" e "--chave=valor". Opção sem valor (como --purge) vale "true".</summary>
    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--")) continue;
            var key = list[i][2..];
            string value;
            var eq = key.IndexOf('=');
            if (eq >= 0) { value = key[(eq + 1)..]; key = key[..eq]; }
            else if (i + 1 < list.Count && !list[i + 1].StartsWith("--")) value = list[++i];
            else value = "true";
            result[key] = value;
        }
        return result;
    }
}
