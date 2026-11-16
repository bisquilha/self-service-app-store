using System.Text.Json;
using Loja.Agent;
using Microsoft.Extensions.Logging.Abstractions;

// Comandos de instalação (install / uninstall / status): rodam e saem, sem subir o serviço.
if (Installer.IsCommand(args)) return await Installer.RunAsync(args);

var options = ConfigLoader.Load(readEnvironment: true);
if (string.IsNullOrWhiteSpace(options.ServerUrl))
{
    Console.Error.WriteLine($"ERRO: servidor da loja nao configurado. Rode: LojaAgent.exe install --server https://loja.suaempresa.local  (configuracao esperada em {ConfigLoader.DefaultPath})");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = Installer.ServiceName);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ServerClient>();
builder.Services.AddSingleton<PolicyStore>();
builder.Services.AddHostedService<AgentWorker>();

builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(options.ResolvedDataDir(), "agent.log")));
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

await builder.Build().RunAsync();
return 0;

namespace Loja.Agent
{
    /// <summary>Lê a configuração: arquivo em ProgramData (gravado pelo "install"); variáveis LOJAAGENT_* sobrepõem (testes).</summary>
    public static class ConfigLoader
    {
        public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LojaAgent");

        public static string DefaultPath => Environment.GetEnvironmentVariable("LOJAAGENT_CONFIG") is { Length: > 0 } custom
            ? custom
            : Path.Combine(DefaultDir, "agent.json");

        public static AgentOptions Load(bool readEnvironment)
        {
            var options = new AgentOptions();
            try
            {
                if (File.Exists(DefaultPath))
                    options = JsonSerializer.Deserialize<AgentOptions>(File.ReadAllText(DefaultPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? options;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Aviso: nao foi possivel ler {DefaultPath}: {ex.Message}");
            }

            if (!readEnvironment) return options;
            static string? Env(string name) => Environment.GetEnvironmentVariable("LOJAAGENT_" + name) is { Length: > 0 } v ? v : null;
            if (Env("SERVERURL") is { } s) options.ServerUrl = s.TrimEnd('/');
            if (Env("CERTSHA256") is { } c) options.CertSha256 = ServerClient.Normalize(c);
            if (Env("CHOCOPATH") is { } p) options.ChocoPath = p;
            if (Env("INTERNALFEED") is { } f) options.InternalFeed = f;
            if (Env("DATADIR") is { } d) options.DataDir = d;
            if (Env("ALLOWSERVEROS") is "1" or "true") options.AllowServerOs = true;
            return options;
        }
    }

    /// <summary>Log simples em arquivo (agent.log), com rotação em 2 MB. Um arquivo antigo (agent.log.1) é mantido.</summary>
    public sealed class FileLoggerProvider(string path) : ILoggerProvider
    {
        private readonly object _gate = new();

        public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

        internal void Write(string line)
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 2 * 1024 * 1024) File.Move(path, path + ".1", overwrite: true);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch
                {
                    // sem log em arquivo, o agente segue funcionando
                }
            }
        }

        public void Dispose() { }

        private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(level)) return;
                var text = formatter(state, exception);
                if (exception is not null) text += " | " + exception.Message;
                owner.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level.ToString()[..3].ToUpperInvariant()}] {category.Split('.').Last()}: {text}");
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
