using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Loja.App;

/// <summary>
/// Lido do loja.json ao lado do .exe (ou do caminho em --config, usado quando uma versão baixada pelo
/// autoupdate é aberta a partir do atalho). Pode ser sobrescrito por --server e --cert.
/// </summary>
public sealed class AppConfig
{
    public string ServerUrl { get; set; } = "";

    /// <summary>SHA-256 do certificado do servidor. Com ele preenchido, o app só aceita esse certificado.</summary>
    public string? CertSha256 { get; set; }

    /// <summary>Lembrete de atualização (modo --lembrete). "lembrete": false desliga; "lembreteHoras" é o intervalo; "lembreteEscaladaDias" é quantos dias sem atualizar até abrir a loja sozinha (0 = nunca).</summary>
    [JsonPropertyName("lembrete")]
    public bool ReminderEnabled { get; set; } = true;

    [JsonPropertyName("lembreteHoras")]
    public int ReminderHours { get; set; } = 4;

    [JsonPropertyName("lembreteEscaladaDias")]
    public int ReminderEscalationDays { get; set; } = 3;

    /// <summary>De onde a configuração foi lida (repassado às versões novas do app).</summary>
    [JsonIgnore]
    public string ConfigPath { get; private set; } = "";

    public static AppConfig Load(string[] args)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "loja.json");
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config") path = args[i + 1];
        }

        var config = new AppConfig();
        if (File.Exists(path))
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options) ?? config;
        }
        config.ConfigPath = path;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--server") config.ServerUrl = args[i + 1];
            if (args[i] == "--cert") config.CertSha256 = args[i + 1];
        }

        if (string.IsNullOrWhiteSpace(config.ServerUrl))
            throw new InvalidOperationException($"Endereço do servidor da loja não configurado ({path}).");
        return config;
    }
}
