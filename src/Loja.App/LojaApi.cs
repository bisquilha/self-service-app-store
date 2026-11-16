using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Loja.Shared;

namespace Loja.App;

public sealed class ApiException(string message, int? status = null) : Exception(message)
{
    /// <summary>Código HTTP quando foi o servidor que recusou (409 = já existe um pedido em andamento neste computador).</summary>
    public int? Status { get; } = status;
}

/// <summary>
/// Cliente do servidor da loja. Autentica com o login do Windows (sem pedir senha) e,
/// se houver CertSha256 configurado, só aceita o certificado com essa impressão digital.
/// </summary>
public sealed class LojaApi : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    /// <summary>Endereço do servidor (termina em /). O botão "Abrir painel de administração" parte dele.</summary>
    public Uri BaseAddress { get; }

    public LojaApi(AppConfig config)
    {
        var baseUri = new Uri(config.ServerUrl.TrimEnd('/') + "/");
        if (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback)
            throw new InvalidOperationException("O endereço da loja precisa usar https.");

        var handler = new HttpClientHandler { UseDefaultCredentials = true };
        var pin = config.CertSha256?.Replace(":", "").Replace(" ", "").Trim();
        if (!string.IsNullOrEmpty(pin))
        {
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), pin, StringComparison.OrdinalIgnoreCase);
        }

        BaseAddress = baseUri;
        _http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LojaDeAplicativos/{Updater.CurrentText}");
        // Versão do app (o painel mostra quem já atualizou) e cabeçalho exigido nas ações de admin (proteção CSRF).
        _http.DefaultRequestHeaders.Add("X-Loja-App-Version", Updater.CurrentText);
        _http.DefaultRequestHeaders.Add("X-Requested-With", "loja-admin");
    }

    /// <summary>Versão do app que o servidor manda usar (null = nenhuma publicada).</summary>
    public async Task<ClientLatestDto?> GetLatestClientAsync()
    {
        using var resp = await SendRawAsync(HttpMethod.Get, "api/client/latest", null);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        return await resp.Content.ReadFromJsonAsync<ClientLatestDto>(Json);
    }

    /// <summary>Baixa o .exe de uma versão para o arquivo informado (sem limite de tempo para o corpo: ~70 MB).</summary>
    public async Task DownloadClientAsync(string version, string destination, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"api/client/download/{Uri.EscapeDataString(version)}", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var source = await resp.Content.ReadAsStreamAsync(ct);
        await using var target = System.IO.File.Create(destination);
        await source.CopyToAsync(target, ct);
    }

    public Task<MeDto> GetMeAsync(string hostname) => SendAsync<MeDto>(HttpMethod.Get, $"api/me?hostname={Uri.EscapeDataString(hostname)}");

    public Task<CatalogDto> GetCatalogAsync(string hostname) =>
        SendAsync<CatalogDto>(HttpMethod.Get, $"api/catalog?hostname={Uri.EscapeDataString(hostname)}");

    /// <summary>Pede ao computador a lista de programas instalados (o servidor ignora se a última ainda é recente).</summary>
    public async Task RefreshInventoryAsync(string hostname, bool force)
    {
        using var resp = await SendRawAsync(HttpMethod.Post, $"api/inventory/refresh?hostname={Uri.EscapeDataString(hostname)}&force={force}", null);
    }

    public async Task<byte[]?> GetIconAsync(int appId)
    {
        using var resp = await _http.GetAsync($"api/icons/{appId}");
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync() : null;
    }

    /// <summary>action: install, upgrade ou uninstall.</summary>
    public Task<RequestDto> RequestAsync(int appId, string hostname, string action) =>
        SendAsync<RequestDto>(HttpMethod.Post, "api/install", new InstallRequestDto(appId, hostname, action));

    public Task<RequestDto> GetRequestAsync(long id) => SendAsync<RequestDto>(HttpMethod.Get, $"api/requests/{id}");

    public Task<List<RequestDto>> GetMyRequestsAsync(string hostname) =>
        SendAsync<List<RequestDto>>(HttpMethod.Get, $"api/requests?hostname={Uri.EscapeDataString(hostname)}");

    public void Dispose() => _http.Dispose();

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body = null)
    {
        using var resp = await SendRawAsync(method, url, body);
        try
        {
            return (await resp.Content.ReadFromJsonAsync<T>(Json))!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Resposta num formato que este app não conhece: app e servidor em versões diferentes.
            throw new ApiException("O servidor da loja está numa versão diferente deste aplicativo. Avise a TI.");
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, object? body)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req);
        }
        catch (HttpRequestException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new ApiException($"Não foi possível falar com o servidor da loja. Verifique a rede ou procure a TI.\n({detail})");
        }
        catch (TaskCanceledException)
        {
            throw new ApiException("O servidor da loja demorou demais para responder. Tente novamente.");
        }

        if (resp.IsSuccessStatusCode) return resp;

        var message = await ReadErrorAsync(resp);
        var status = (int)resp.StatusCode;
        resp.Dispose();
        throw new ApiException(message, status);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var error = await resp.Content.ReadFromJsonAsync<ErrorDto>(Json);
            if (!string.IsNullOrWhiteSpace(error?.Message)) return error.Message;
        }
        catch (Exception)
        {
            // corpo vazio ou não-JSON: usa a mensagem padrão abaixo
        }

        return resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Não foi possível identificar seu usuário pelo login do Windows.",
            HttpStatusCode.Forbidden => "Acesso negado.",
            HttpStatusCode.NotFound => "Não encontrado.",
            _ => $"Erro no servidor da loja ({(int)resp.StatusCode}).",
        };
    }
}
