using System.Collections.Concurrent;
using Loja.Shared;

namespace Loja.Agent;

/// <summary>
/// Dois laços: o batimento (avisa que está vivo, recebe pedidos de cancelamento) e o de trabalho (pede o próximo pedido,
/// executa e devolve o resultado). Um pedido por vez, como na loja hoje ("já existe uma instalação em andamento").
/// </summary>
public sealed class AgentWorker(AgentOptions options, ServerClient server, PolicyStore policy, ILogger<AgentWorker> log) : BackgroundService
{
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    private bool _firstBeat;
    private bool _policyUnsupported;

    protected override Task ExecuteAsync(CancellationToken stop)
    {
        policy.LoadCached();
        return Task.WhenAll(HeartbeatLoop(stop), JobLoop(stop));
    }

    /// <summary>O batimento traz o número da versão da configuração do servidor: se mudou, busca a política nova.</summary>
    private async Task RefreshPolicyAsync(long serverVersion, CancellationToken stop)
    {
        if (serverVersion == policy.Version || _policyUnsupported) return;
        try
        {
            if (await server.PolicyAsync(SystemInfo.Hostname, stop) is { } fresh)
            {
                policy.Update(fresh);
                log.LogInformation("Política do servidor atualizada (versão {Version}): Chocolatey {Choco}, internos {Internal}",
                    fresh.Version, fresh.ChocolateyEnabled ? "ligado" : "desligado", fresh.InternalAppsEnabled ? "ligados" : "desligados");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Servidor mais antigo (sem política): o agente segue só com o agent.json e para de perguntar até reiniciar.
            _policyUnsupported = true;
            log.LogInformation("O servidor não tem política de agente (versão antiga): valem as configurações do agent.json.");
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            log.LogWarning("Não foi possível buscar a política do servidor: {Error}", ex.Message);
        }
    }

    private async Task HeartbeatLoop(CancellationToken stop)
    {
        log.LogInformation("Agente {Version} iniciado em {Host}. Servidor: {Server}", ServerClient.Version, SystemInfo.Hostname, options.ServerUrl);
        while (!stop.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try
            {
                var (caption, build) = SystemInfo.Windows();
                var response = await server.HeartbeatAsync(new AgentHeartbeatDto(
                    SystemInfo.Hostname, ServerClient.Version, caption, build, SystemInfo.ProductType(), SystemInfo.LoggedUser(),
                    [.. _running.Keys]), stop);

                if (!_firstBeat)
                {
                    _firstBeat = true;
                    log.LogInformation("Primeiro batimento aceito pelo servidor.");
                }

                foreach (var id in response.CancelJobs)
                {
                    if (_running.TryGetValue(id, out var cts))
                    {
                        log.LogWarning("Pedido {Id}: cancelamento pedido pelo servidor", id);
                        cts.Cancel();
                    }
                }
                delay = TimeSpan.FromSeconds(Math.Clamp(response.PollSeconds, 15, 600));
                await RefreshPolicyAsync(response.ConfigVersion, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning("Batimento falhou: {Error}", ex.Message);
            }

            try { await Task.Delay(delay, stop); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task JobLoop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (SystemInfo.ProductType() != 1 && !options.AllowServerOs)
                {
                    // A loja só instala em estação. Aqui não se pede trabalho nenhum; o servidor também recusa.
                    await Task.Delay(TimeSpan.FromMinutes(5), stop);
                    continue;
                }

                var started = DateTime.UtcNow;
                var job = await server.NextJobAsync(SystemInfo.Hostname, options.JobWaitSeconds, stop);
                if (job is null)
                {
                    // O servidor respondeu "nada" na hora (por exemplo, agente desligado nele): não fica em laço apertado.
                    if (DateTime.UtcNow - started < TimeSpan.FromSeconds(2)) await Task.Delay(TimeSpan.FromSeconds(10), stop);
                    continue;
                }
                await RunJobAsync(job, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning("Busca de trabalho falhou: {Error}", ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunJobAsync(AgentJobDto dto, CancellationToken stop)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _running[dto.Id] = cancel;
        try
        {
            log.LogInformation("Pedido {Id}: {Action} {Package} ({Source})", dto.Id, dto.Action, dto.Package, dto.Source);
            var outcome = await ExecuteAsync(dto, cancel.Token);
            log.LogInformation("Pedido {Id}: terminou com código {Code}", dto.Id, outcome.ExitCode);

            // O inventário vai junto de qualquer resposta: a loja mostra "Instalado / Atualizar" com ele.
            string? installed = null, windows = null;
            try
            {
                installed = await Inventory.ChocoListAsync(options, stop);
                windows = Inventory.WindowsPrograms();
            }
            catch (Exception ex)
            {
                log.LogWarning("Pedido {Id}: não foi possível coletar o inventário: {Error}", dto.Id, ex.Message);
            }

            await SendWithRetryAsync(dto.Id, new AgentResultDto(SystemInfo.Hostname, outcome.ExitCode, outcome.Output, installed, windows), stop);
        }
        finally
        {
            _running.TryRemove(dto.Id, out _);
        }
    }

    private async Task<JobOutcome> ExecuteAsync(AgentJobDto dto, CancellationToken cancel)
    {
        if (JobRunner.Validate(dto) is { } invalid) return new JobOutcome(invalid.Code, invalid.Message);
        if (SystemInfo.ProductType() != 1 && !options.AllowServerOs)
            return new JobOutcome(AgentExitCodes.NotAWorkstation, "Este computador e um servidor: a loja so instala em estacoes.");
        if (dto.Action == RequestActions.List) return new JobOutcome(0, "Lista de pacotes consultada.");

        // Desinstalar nao usa fonte nenhuma e continua liberado mesmo com a fonte desligada.
        var current = policy.Current;
        if (dto.Action != RequestActions.Uninstall && PolicyStore.IsSourceDisabled(current, dto.Source))
            return new JobOutcome(AgentExitCodes.SourceDisabled, "Esta fonte de aplicativos esta desligada nas Configuracoes da loja.");

        var choco = options.ResolvedChocoPath();
        if (!File.Exists(choco)) return new JobOutcome(AgentExitCodes.ChocolateyMissing, $"Chocolatey nao encontrado em {choco}");

        return await JobRunner.RunChocoAsync(new AgentJob(dto), options, current, cancel, log);
    }

    /// <summary>Se o servidor não responde, tenta de novo por alguns minutos. Depois disso o pedido vira "sem retorno" no servidor.</summary>
    private async Task SendWithRetryAsync(long id, AgentResultDto result, CancellationToken stop)
    {
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                await server.SendResultAsync(id, result, stop);
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                // O servidor recusou de propósito (pedido já concluído, ou de outra máquina): tentar de novo não muda nada.
                log.LogWarning("Pedido {Id}: resultado recusado pelo servidor: {Error}", id, ex.Message);
                return;
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                log.LogWarning("Pedido {Id}: falha ao enviar o resultado (tentativa {N}): {Error}", id, attempt, ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(10 * attempt), stop); } catch (OperationCanceledException) { return; }
            }
        }
        log.LogError("Pedido {Id}: o resultado não pôde ser enviado", id);
    }
}
