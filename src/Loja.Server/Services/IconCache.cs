using Loja.Server.Data;

namespace Loja.Server.Services;

/// <summary>
/// Guarda o ícone de cada app no servidor. As máquinas buscam o ícone aqui, não na internet.
/// SVG é convertido para PNG; se não der, o app de desktop mostra a inicial do nome no lugar.
/// </summary>
public class IconCache(Db db, IHttpClientFactory factory, ILogger<IconCache> log)
{
    public const string ClientName = "icons";
    private const int MaxBytes = 1_000_000;

    private static readonly Dictionary<string, string> ContentTypes = new()
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["ico"] = "image/x-icon",
    };

    private string Dir
    {
        get
        {
            var dir = Path.Combine(db.DataDir, "icons");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public bool Has(int appId) => Find(appId) is not null;

    public bool TryGet(int appId, out string path, out string contentType)
    {
        path = Find(appId) ?? "";
        contentType = path.Length > 0 ? ContentTypes[Path.GetExtension(path).TrimStart('.')] : "";
        return path.Length > 0;
    }

    public async Task UpdateAsync(int appId, PackageInfo info, CancellationToken ct)
    {
        var data = info.EmbeddedIcon;
        if (data is null && Uri.TryCreate(info.IconUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            data = await DownloadAsync(uri, ct);
        }

        if (data is not null && LooksLikeSvg(data)) data = SvgToPng(data, info.Id);

        var ext = data is null ? null : Detect(data);
        if (ext is null) return; // mantém o ícone anterior, se houver

        Delete(appId);
        await File.WriteAllBytesAsync(Path.Combine(Dir, $"{appId}.{ext}"), data!, ct);
    }

    public void Delete(int appId)
    {
        foreach (var file in Directory.EnumerateFiles(Dir, $"{appId}.*")) File.Delete(file);
    }

    private string? Find(int appId) =>
        ContentTypes.Keys.Select(ext => Path.Combine(Dir, $"{appId}.{ext}")).FirstOrDefault(File.Exists);

    private async Task<byte[]?> DownloadAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            var http = factory.CreateClient(ClientName);
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxBytes) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > MaxBytes) return null;
            }
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogInformation("Ícone indisponível {Url}: {Error}", uri, ex.Message);
            return null;
        }
    }

    private static bool LooksLikeSvg(byte[] d)
    {
        var head = System.Text.Encoding.UTF8.GetString(d, 0, Math.Min(d.Length, 1024));
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private byte[]? SvgToPng(byte[] svg, string packageId)
    {
        try
        {
            using var input = new MemoryStream(svg);
            var doc = Svg.SvgDocument.Open<Svg.SvgDocument>(input);
            // Mantém a proporção dentro de um quadrado de 128 px.
            var size = doc.GetDimensions();
            var scale = 128f / Math.Max(1f, Math.Max(size.Width, size.Height));
            using var bitmap = doc.Draw((int)Math.Max(1, size.Width * scale), (int)Math.Max(1, size.Height * scale));
            using var output = new MemoryStream();
            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            log.LogInformation("Não foi possível converter o ícone SVG de {Package}: {Error}", packageId, ex.Message);
            return null;
        }
    }

    private static string? Detect(byte[] d)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "png";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "jpg";
        if (d.Length > 6 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return "gif";
        if (d.Length > 4 && d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0) return "ico";
        return null;
    }
}
