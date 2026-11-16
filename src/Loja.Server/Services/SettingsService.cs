using Loja.Server.Data;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

public record SettingsDto(
    int AutoUpdateStartHour,
    int AutoUpdateEndHour,
    int AutoUpdateMaxPerCycle,
    int InventoryMaxAgeMinutes,
    int RequestTimeoutMinutes,
    int MetadataRefreshHours);

/// <summary>
/// Parâmetros que o painel pode mudar sem reiniciar a loja. Ficam no banco e sobrepõem o appsettings.
/// Grupos de admin, Tactical e certificado continuam só no arquivo do servidor, de propósito.
/// </summary>
public class SettingsService(Db db, IOptions<LojaOptions> options, ILogger<SettingsService> log)
{
    private static readonly Dictionary<string, (int Min, int Max)> Ranges = new()
    {
        [nameof(SettingsDto.AutoUpdateStartHour)] = (0, 23),
        [nameof(SettingsDto.AutoUpdateEndHour)] = (0, 24),
        [nameof(SettingsDto.AutoUpdateMaxPerCycle)] = (1, 200),
        [nameof(SettingsDto.InventoryMaxAgeMinutes)] = (1, 1440),
        [nameof(SettingsDto.RequestTimeoutMinutes)] = (5, 1440),
        [nameof(SettingsDto.MetadataRefreshHours)] = (1, 168),
    };

    private static readonly Dictionary<string, string> Labels = new()
    {
        [nameof(SettingsDto.AutoUpdateStartHour)] = "Início da atualização automática",
        [nameof(SettingsDto.AutoUpdateEndHour)] = "Fim da atualização automática",
        [nameof(SettingsDto.AutoUpdateMaxPerCycle)] = "Máquinas por ciclo",
        [nameof(SettingsDto.InventoryMaxAgeMinutes)] = "Idade máxima da lista de instalados",
        [nameof(SettingsDto.RequestTimeoutMinutes)] = "Tempo limite de um pedido",
        [nameof(SettingsDto.MetadataRefreshHours)] = "Atualizar dados dos apps a cada",
    };

    /// <summary>Um parâmetro como o painel o descreve: chave (igual à do JSON), rótulo e faixa aceita.</summary>
    public record SettingFieldDto(string Key, string Label, int Min, int Max);

    public List<SettingFieldDto> Describe() =>
        Ranges.Select(r => new SettingFieldDto(char.ToLowerInvariant(r.Key[0]) + r.Key[1..], Labels[r.Key], r.Value.Min, r.Value.Max)).ToList();

    public string LabelOf(string key) => Labels.TryGetValue(key, out var label) ? label : key;

    public SettingsDto Get()
    {
        var o = options.Value;
        return new(o.AutoUpdateStartHour, o.AutoUpdateEndHour, o.AutoUpdateMaxPerCycle, o.InventoryMaxAgeMinutes, o.RequestTimeoutMinutes, o.MetadataRefreshHours);
    }

    /// <summary>Na subida do servidor: aplica o que foi salvo pelo painel.</summary>
    public void ApplyStored()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (int.TryParse(r.GetString(1), out var value)) Set(r.GetString(0), value);
        }
    }

    public string? Save(SettingsDto dto, string user)
    {
        var values = new Dictionary<string, int>
        {
            [nameof(dto.AutoUpdateStartHour)] = dto.AutoUpdateStartHour,
            [nameof(dto.AutoUpdateEndHour)] = dto.AutoUpdateEndHour,
            [nameof(dto.AutoUpdateMaxPerCycle)] = dto.AutoUpdateMaxPerCycle,
            [nameof(dto.InventoryMaxAgeMinutes)] = dto.InventoryMaxAgeMinutes,
            [nameof(dto.RequestTimeoutMinutes)] = dto.RequestTimeoutMinutes,
            [nameof(dto.MetadataRefreshHours)] = dto.MetadataRefreshHours,
        };

        foreach (var (key, value) in values)
        {
            var (min, max) = Ranges[key];
            if (value < min || value > max) return $"\"{Labels[key]}\" deve ficar entre {min} e {max}.";
        }

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (key, value) in values)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)";
            cmd.Add("$k", key);
            cmd.Add("$v", value.ToString());
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        foreach (var (key, value) in values) Set(key, value);
        log.LogInformation("{User} alterou as configurações: {Settings}", user, string.Join(", ", values.Select(v => $"{v.Key}={v.Value}")));
        return null;
    }

    private void Set(string key, int value)
    {
        if (!Ranges.TryGetValue(key, out var range) || value < range.Min || value > range.Max) return;
        var o = options.Value;
        switch (key)
        {
            case nameof(SettingsDto.AutoUpdateStartHour): o.AutoUpdateStartHour = value; break;
            case nameof(SettingsDto.AutoUpdateEndHour): o.AutoUpdateEndHour = value; break;
            case nameof(SettingsDto.AutoUpdateMaxPerCycle): o.AutoUpdateMaxPerCycle = value; break;
            case nameof(SettingsDto.InventoryMaxAgeMinutes): o.InventoryMaxAgeMinutes = value; break;
            case nameof(SettingsDto.RequestTimeoutMinutes): o.RequestTimeoutMinutes = value; break;
            case nameof(SettingsDto.MetadataRefreshHours): o.MetadataRefreshHours = value; break;
        }
    }
}
