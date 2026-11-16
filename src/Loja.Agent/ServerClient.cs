using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Loja.Shared;

namespace Loja.Agent;

/// <summary>
/// Conversa do agente com o servidor da loja. A identidade é o login do próprio computador (Kerberos/Negociar):
/// rodando como SYSTEM, o Windows apresenta a conta PC$, e o servidor confere que o nome bate. Não há senha nem chave a distribuir.
/// O certificado do servidor é conferido pelo SHA-256 (o mesmo "CertSha256" do loja.json do app).
/// </summary>
public sealed class ServerClient : IDisposable
{
    private readonly HttpClient _http;

    public ServerClient(AgentOptions options)
    {
        var handler = new HttpClientHandler { UseDefaultCredentials = true };
        var expected = Normalize(options.CertSha256);
        if (expected.Length > 0)
        {
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), expected, StringComparison.OrdinalIgnoreCase);
        }

        // Maior que a espera do servidor por trabalho novo (até 30 s).
        _http = new HttpClient(handler) { BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(75) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LojaAgent/" + Version);
    }

    public static string Version =>
        typeof(ServerClient).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static string Normalize(string? sha) => (sha ?? "").Replace(":", "").Replace(" ", "").Trim();

    public async Task<AgentHeartbeatResponse> HeartbeatAsync(AgentHeartbeatDto dto, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("api/agent/heartbeat", dto, ct);
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(ct) ?? new AgentHeartbeatResponse(60, []);
    }

    public async Task<AgentPolicyDto?> PolicyAsync(string hostname, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"api/agent/policy?hostname={Uri.EscapeDataString(hostname)}", ct);
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AgentPolicyDto>(ct);
    }

    /// <summary>Próximo trabalho desta máquina, esperando até <paramref name="waitSeconds"/> na conexão aberta. Nulo se não há.</summary>
    public async Task<AgentJobDto?> NextJobAsync(string hostname, int waitSeconds, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"api/agent/jobs/next?hostname={Uri.EscapeDataString(hostname)}&wait={waitSeconds}", ct);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AgentJobDto>(ct);
    }

    public async Task SendResultAsync(long jobId, AgentResultDto dto, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"api/agent/jobs/{jobId}/result", dto, ct);
        await EnsureAsync(resp, ct);
    }

    /// <summary>Só confere se o servidor responde (e se o certificado confere). Usado pelo comando "status".</summary>
    public async Task<(bool Ok, string Message)> PingAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("", ct);
            return (resp.IsSuccessStatusCode, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.InnerException?.Message ?? ex.Message);
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (text.Length > 200) text = text[..200];
        throw new HttpRequestException($"O servidor respondeu {(int)resp.StatusCode}: {text}", null, resp.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}
