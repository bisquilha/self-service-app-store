using Loja.Server.Data;
using Loja.Shared;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

/// <summary>
/// Atualização automática: a cada 10 minutos, dentro da janela configurada, procura máquinas com
/// versão antiga dos apps marcados como "Auto-atualizar" e pede a atualização pelo Tactical.
/// Cada máquina/app é tentado no máximo uma vez por dia; tudo fica no histórico como "(automático)".
/// </summary>
public class AutoUpdateService(
    AppRepository apps,
    InventoryRepository inventory,
    RequestRepository requests,
    InstallService install,
    ConfigService config,
    IOptions<LojaOptions> options,
    ILogger<AutoUpdateService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                if (InWindow()) await RunCycleAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Falha na atualização automática");
            }
        }
    }

    public bool InWindow()
    {
        var opt = options.Value;
        var hour = DateTime.Now.Hour;
        return opt.AutoUpdateStartHour <= opt.AutoUpdateEndHour
            ? hour >= opt.AutoUpdateStartHour && hour < opt.AutoUpdateEndHour
            : hour >= opt.AutoUpdateStartHour || hour < opt.AutoUpdateEndHour; // janela que passa da meia-noite
    }

    public async Task<int> RunCycleAsync(CancellationToken ct)
    {
        var started = 0;
        var max = Math.Max(1, options.Value.AutoUpdateMaxPerCycle);
        var since = DateTime.UtcNow.AddHours(-20);
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in apps.List(onlyEnabled: true).Where(a => a.AutoUpdate && !string.IsNullOrWhiteSpace(a.Version) && config.IsSourceEnabled(a.Source)))
        {
            foreach (var (hostname, installed) in inventory.HostsWithPackage(app.PackageId))
            {
                if (started >= max) return started;
                if (busy.Contains(hostname)) continue;
                // Só atualiza quando as duas versões dão para comparar e a instalada é menor: na dúvida, não mexe.
                if (!VersionRules.IsOutdated(installed, app.Version)) continue;
                // Só conta as tentativas automáticas: uma atualização manual do usuário não bloqueia a automática.
                if (requests.HasRunning(hostname)
                    || requests.HasRecent(hostname, app.PackageId, RequestActions.Upgrade, InstallService.AutomaticUser, since)) continue;

                if (await install.RequestAutoUpdateAsync(app, hostname, ct))
                {
                    busy.Add(hostname); // uma ação por máquina por ciclo
                    started++;
                    log.LogInformation("Atualização automática: {Package} {From} → {To} em {Host}", app.PackageId, installed, app.Version, hostname);
                }
            }
        }

        return started;
    }
}
