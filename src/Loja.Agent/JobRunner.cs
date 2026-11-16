using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Loja.Shared;

namespace Loja.Agent;

/// <summary>Resultado da execução de um pedido (antes do inventário).</summary>
public record JobOutcome(int ExitCode, string Output);

/// <summary>
/// Executa UM pedido da loja. Nada além de install / upgrade / uninstall / list passa daqui:
/// as mesmas validações do script do Tactical (Loja-Choco.ps1), conferidas de novo no próprio computador.
/// </summary>
public static partial class JobRunner
{
    private const int MaxOutput = 6000;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex PackageRegex();

    public static bool IsValidAction(string? a) => a is RequestActions.Install or RequestActions.Upgrade or RequestActions.Uninstall or RequestActions.List;

    public static bool IsValidPackage(string? p) => p is not null && PackageRegex().IsMatch(p);

    /// <summary>Valida o pedido. Devolve o código de erro e a mensagem, ou null se está tudo certo.</summary>
    public static (int Code, string Message)? Validate(AgentJobDto job)
    {
        if (!IsValidAction(job.Action)) return (AgentExitCodes.InvalidAction, $"Acao invalida: '{job.Action}'");
        if (job.Action == RequestActions.List) return null;
        if (!IsValidPackage(job.Package)) return (AgentExitCodes.InvalidPackage, $"Nome de pacote invalido: '{job.Package}'");
        if (!PackageSources.IsValid(job.Source)) return (AgentExitCodes.InvalidSource, $"Origem invalida: '{job.Source}'");
        return null;
    }

    /// <summary>Argumentos do choco para o pedido (lista de argumentos, sem passar por interpretador de comando).</summary>
    public static IReadOnlyList<string> BuildArguments(AgentJobDto job, AgentOptions options, AgentPolicyDto? policy = null)
    {
        if (job.Action == RequestActions.Uninstall)
            return ["uninstall", job.Package!, "-y", "--no-progress", "-x"]; // -x remove também as dependências instaladas junto

        // Os endereços vêm da política do servidor (Configurações); sem política, do agent.json.
        var community = PolicyStore.CommunityFeed(options, policy);
        var internalFeed = PolicyStore.InternalFeed(options, policy);

        // Pacotes internos podem depender de pacotes da comunidade, por isso as duas fontes.
        var feed = job.Source == PackageSources.Internal && !string.IsNullOrWhiteSpace(internalFeed)
            ? $"{internalFeed};{community}"
            : community;
        var args = new List<string> { job.Action, job.Package!, "-y", "--no-progress", $"--source={feed}" };
        if (job.Action == RequestActions.Upgrade) args.Add("--fail-on-not-installed");
        return args;
    }

    /// <summary>
    /// Roda o choco com tempo máximo. Se passar do tempo (ou for cancelado), encerra o choco e TODOS os processos filhos
    /// (o instalador travado, msiexec etc.) e apaga a marca de pendência do pacote.
    /// </summary>
    public static async Task<JobOutcome> RunChocoAsync(AgentJob job, AgentOptions options, AgentPolicyDto? policy, CancellationToken cancel, ILogger log)
    {
        var choco = options.ResolvedChocoPath();
        var psi = new ProcessStartInfo(choco)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BuildArguments(job.Dto, options, policy)) psi.ArgumentList.Add(a);

        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null) return;
            lock (output)
            {
                output.AppendLine(line);
                if (output.Length > MaxOutput * 2) output.Remove(0, output.Length - MaxOutput);
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        Append($"Loja: choco {job.Dto.Action} {job.Dto.Package} (origem: {job.Dto.Source})");
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new JobOutcome(AgentExitCodes.ChocolateyMissing, $"Nao foi possivel iniciar o Chocolatey ({choco}): {ex.Message}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, job.Dto.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            process.WaitForExit(); // garante que a saída terminou de ser lida
            return new JobOutcome(process.ExitCode, Tail(output));
        }
        catch (OperationCanceledException)
        {
            var timedOut = timeout.IsCancellationRequested && !cancel.IsCancellationRequested;
            log.LogWarning("Pedido {Id}: {Why}; encerrando o choco e os processos filhos", job.Dto.Id, timedOut ? "tempo máximo" : "cancelado");
            KillTree(process, log);
            if (job.Dto.Action != RequestActions.Uninstall) ClearPending(choco, job.Dto.Package!);
            Append(timedOut
                ? $"Loja: passou do tempo maximo ({job.Dto.TimeoutSeconds} s); processos encerrados."
                : "Loja: cancelado; processos encerrados.");
            return new JobOutcome(timedOut ? AgentExitCodes.TimedOut : AgentExitCodes.Cancelled, Tail(output));
        }
    }

    private static string Tail(StringBuilder sb)
    {
        lock (sb)
        {
            var text = sb.ToString();
            return text.Length > MaxOutput ? text[^MaxOutput..] : text;
        }
    }

    private static void KillTree(Process p, ILogger log)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            log.LogWarning("Não foi possível encerrar o processo {Pid}: {Error}", p.Id, ex.Message);
        }
    }

    /// <summary>O choco deixa .chocolateyPending quando morre no meio; apagar evita o aviso na próxima vez.</summary>
    private static void ClearPending(string chocoExe, string package)
    {
        try
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(chocoExe));
            if (root is null) return;
            var marker = Path.Combine(root, "lib", package, ".chocolateyPending");
            if (File.Exists(marker)) File.Delete(marker);
        }
        catch
        {
            // sem a limpeza, o choco só reclama na próxima execução
        }
    }
}

/// <summary>Pedido em execução no agente (o DTO do servidor mais o que o agente precisa guardar).</summary>
public record AgentJob(AgentJobDto Dto)
{
    public long Id => Dto.Id;
}
