using Loja.Server.Data;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

/// <summary>
/// A cada minuto: encerra pedidos que nunca tiveram retorno.
/// A cada N horas: atualiza nome, versão e ícone dos apps a partir dos repositórios.
/// </summary>
public class MaintenanceService(
    RequestRepository requests,
    CatalogService catalog,
    IOptions<LojaOptions> options,
    ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var nextRefresh = DateTime.UtcNow.AddMinutes(2);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                var opt = options.Value;
                var expired = requests.ExpireRunning(
                    DateTime.UtcNow.AddMinutes(-opt.RequestTimeoutMinutes),
                    $"Sem retorno do computador após {opt.RequestTimeoutMinutes} min. A TI pode verificar o histórico do Tactical RMM.");
                if (expired > 0) log.LogWarning("{Count} pedido(s) marcados como sem retorno", expired);

                if (DateTime.UtcNow >= nextRefresh)
                {
                    await catalog.RefreshAllAsync(ct);
                    nextRefresh = DateTime.UtcNow.AddHours(Math.Max(1, opt.MetadataRefreshHours));
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Falha na manutenção periódica");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
