using System.Text.Json;
using Loja.Server.Data;
using Loja.Shared;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

public enum SettingType { Bool, Int, Text, Url }

/// <summary>
/// Uma configuração. Declarada UMA vez aqui: o painel desenha o formulário a partir desta descrição
/// (rótulo, ajuda, tipo, faixa, dependência) e o servidor valida com a mesma regra. Criar uma chave nova é só
/// acrescentar uma linha em <see cref="ConfigService.BuildDefinitions"/>: não existe tela para programar.
/// </summary>
public record SettingDef(
    string Key,
    string Group,
    string Label,
    string Help,
    SettingType Type,
    object Default,
    int Min = 0,
    int Max = 0,
    bool AppliesToAgents = false,
    string? DependsOn = null);

public record SettingGroup(string Id, string Label, string Help);

public record ConfigSettingDto(string Key, string Label, string Help, string Type, object Value, object Default, bool IsDefault,
    int? Min, int? Max, bool AppliesToAgents, string? DependsOn);

public record ConfigGroupDto(string Id, string Label, string Help, List<ConfigSettingDto> Settings);

public record ConfigDto(long Version, List<ConfigGroupDto> Groups);

public record ConfigChange(string Key, string Label, string Old, string New);

/// <summary>Corpo do PUT: só as chaves que mudam. Valor nulo = voltar ao padrão.</summary>
public record ConfigPatchDto(Dictionary<string, JsonElement>? Values);

public record ConfigSaveResult(string? Error, List<ConfigChange> Changes);

/// <summary>
/// Motor de configurações do produto: tudo que a TI liga, desliga ou ajusta DEPOIS da instalação, sem reiniciar e sem mexer em arquivo.
/// O padrão de cada chave vem do appsettings (uma instalação existente continua exatamente como está: nada muda enquanto ninguém salvar nada).
/// O que o painel salva fica no banco (tabela settings, prefixo "cfg.") e tem prioridade. Cada mudança sobe o número da versão
/// da configuração; os agentes comparam esse número no batimento e buscam a política nova quando ele muda.
/// </summary>
public class ConfigService
{
    private const string Prefix = "cfg.";
    private const string VersionKey = "cfg._version";

    private readonly Db _db;
    private readonly IOptions<LojaOptions> _options;
    private readonly ILogger<ConfigService> _log;
    private readonly LocalDirectoryService _local;
    private readonly object _gate = new();
    private readonly Dictionary<string, SettingDef> _defs;
    private readonly List<SettingGroup> _groups;
    private volatile Dictionary<string, object> _stored = new();
    private long _version;

    public ConfigService(Db db, IOptions<LojaOptions> options, LocalDirectoryService local, ILogger<ConfigService> log)
    {
        _db = db;
        _options = options;
        _local = local;
        _log = log;
        (_groups, var defs) = BuildDefinitions(options.Value);
        _defs = defs.ToDictionary(d => d.Key, StringComparer.Ordinal);
    }

    /// <summary>Os grupos e as chaves. Os padrões reproduzem o que o servidor já faz hoje.</summary>
    private static (List<SettingGroup>, List<SettingDef>) BuildDefinitions(LojaOptions o)
    {
        var groups = new List<SettingGroup>
        {
            new("sources", "Fontes de aplicativos", "De onde vêm os aplicativos da loja. Dá para ligar e desligar a qualquer momento: o que for desligado some da loja, mas os cadastros ficam guardados."),
            new("agent", "Agente nas estações", "Ajustes que os agentes recebem do servidor. Valem para todos os agentes já instalados, sem reinstalar."),
            new("identity", "Identidade (quem entra)", "Como as pessoas provam quem são. O administrador de emergência (usuário local) sempre consegue entrar, mesmo com tudo desligado aqui."),
        };
        var defs = new List<SettingDef>
        {
            new("chocolatey.enabled", "sources", "Usar o Chocolatey", "Desligado: os aplicativos do Chocolatey somem da loja e novos pedidos são recusados (desinstalar continua liberado).",
                SettingType.Bool, true, AppliesToAgents: true),
            new("chocolatey.communityFeedUrl", "sources", "Endereço do repositório do Chocolatey", "Troque para usar um espelho ou um repositório próprio. Precisa ser um endereço http ou https.",
                SettingType.Url, o.CommunityFeedUrl, AppliesToAgents: true, DependsOn: "chocolatey.enabled"),
            new("internalApps.enabled", "sources", "Usar aplicativos internos", "Desligado: os aplicativos internos somem da loja e novos pedidos são recusados (desinstalar continua liberado).",
                SettingType.Bool, true, AppliesToAgents: true),
            new("internalApps.feedPath", "sources", "Pasta dos pacotes internos", "Pasta com os pacotes .nupkg internos, no formato que o servidor e as estações enxergam (por exemplo \\\\servidor\\pasta). Vazio = sem pacotes internos.",
                SettingType.Text, o.InternalFeedPath, AppliesToAgents: true, DependsOn: "internalApps.enabled"),
            new("agent.pollSeconds", "agent", "Batimento do agente (segundos)", "De quanto em quanto tempo cada agente avisa o servidor que está vivo.",
                SettingType.Int, o.Agent.PollSeconds, 15, 600, AppliesToAgents: true),
            new("agent.jobTimeoutSeconds", "agent", "Tempo máximo de uma instalação (segundos)", "Passou disso, o agente encerra o instalador travado e o pedido falha com \"tempo esgotado\".",
                SettingType.Int, o.Agent.JobTimeoutSeconds, 60, 14400, AppliesToAgents: true),
            new("agent.onlineSeconds", "agent", "Considerar offline depois de (segundos)", "Sem batimento por este tempo, o agente aparece como offline no painel.",
                SettingType.Int, o.Agent.OnlineSeconds, 60, 3600),
            new("identity.windows.enabled", "identity", "Login do Windows / Active Directory", "Usa o login do Windows (Negociar/Kerberos) e os grupos do AD. Só dá para desligar se houver um administrador local ativo.",
                SettingType.Bool, true),
            new("identity.local.enabled", "identity", "Login por usuário e senha locais", "Permite entrar no painel com usuário e senha criados na aba Identidade. O administrador de emergência entra sempre, mesmo com isto desligado.",
                SettingType.Bool, false),
            new("identity.agentBridge.enabled", "identity", "Identificar o funcionário pelo agente", "Sem AD: o agente do PC informa quem está logado no Windows e o app da loja usa essa identidade (token curto, emitido pelo servidor). Os grupos locais usam esse login.",
                SettingType.Bool, false, AppliesToAgents: true),
        };
        return (groups, defs);
    }

    public long Version => Interlocked.Read(ref _version);

    public bool ChocolateyEnabled => GetBool("chocolatey.enabled");

    public bool InternalAppsEnabled => GetBool("internalApps.enabled");

    /// <summary>A fonte (Chocolatey ou interna) está ligada?</summary>
    public bool IsSourceEnabled(string source) =>
        source == PackageSources.Internal ? InternalAppsEnabled : ChocolateyEnabled;

    public string SourceOffMessage(string source) =>
        source == PackageSources.Internal
            ? "Os aplicativos internos estão desligados em Configurações."
            : "O Chocolatey está desligado em Configurações.";

    public bool GetBool(string key) => Value(key) is bool b && b;

    public int GetInt(string key) => Value(key) is int i ? i : 0;

    public string GetText(string key) => Value(key) as string ?? "";

    /// <summary>O valor em vigor: o salvo pelo painel ou, se nunca foi mexido, o padrão do appsettings.</summary>
    public object Value(string key) =>
        _stored.TryGetValue(key, out var v) ? v : _defs.TryGetValue(key, out var d) ? d.Default : throw new KeyNotFoundException(key);

    public string LabelOf(string key) => _defs.TryGetValue(key, out var d) ? d.Label : key;

    /// <summary>Política que os agentes recebem. O servidor é a única fonte: o agente não decide nada sozinho.</summary>
    public AgentPolicyDto Policy() => new(
        Version,
        GetBool("chocolatey.enabled"),
        GetText("chocolatey.communityFeedUrl"),
        GetBool("internalApps.enabled"),
        GetText("internalApps.feedPath"),
        GetInt("agent.pollSeconds"),
        GetInt("agent.jobTimeoutSeconds"));

    public ConfigDto Describe() =>
        new(Version, _groups.Select(g => new ConfigGroupDto(g.Id, g.Label, g.Help,
            _defs.Values.Where(d => d.Group == g.Id).Select(ToDto).ToList())).ToList());

    private ConfigSettingDto ToDto(SettingDef d) =>
        new(d.Key, d.Label, d.Help, d.Type.ToString().ToLowerInvariant(), Value(d.Key), d.Default, !_stored.ContainsKey(d.Key),
            d.Type == SettingType.Int ? d.Min : null, d.Type == SettingType.Int ? d.Max : null, d.AppliesToAgents, d.DependsOn);

    /// <summary>Na subida do servidor: carrega o que o painel salvou e aplica.</summary>
    public void Load()
    {
        var stored = new Dictionary<string, object>();
        long version = 0;
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM settings WHERE key LIKE 'cfg.%'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var (key, text) = (r.GetString(0), r.GetString(1));
                if (key == VersionKey)
                {
                    long.TryParse(text, out version);
                    continue;
                }
                var name = key[Prefix.Length..];
                if (_defs.TryGetValue(name, out var def) && Parse(def, text) is { } value) stored[name] = value;
            }
        }
        lock (_gate)
        {
            _stored = stored;
            Interlocked.Exchange(ref _version, version);
            Apply();
        }
    }

    /// <summary>
    /// Salva várias chaves de uma vez (tudo ou nada). Valor nulo volta a chave para o padrão.
    /// Só sobe a versão se algo mudou de verdade.
    /// </summary>
    public ConfigSaveResult Save(IReadOnlyDictionary<string, JsonElement> patch, string user)
    {
        var next = new Dictionary<string, object>(_stored);
        var changes = new List<ConfigChange>();
        var toWrite = new List<(string Key, string? Text)>();

        lock (_gate)
        {
            foreach (var (key, raw) in patch)
            {
                if (!_defs.TryGetValue(key, out var def)) return new($"Configuração desconhecida: \"{key}\".", []);

                object? value;
                if (raw.ValueKind == JsonValueKind.Null)
                {
                    value = null;
                }
                else
                {
                    var (parsed, error) = Validate(def, raw);
                    if (error is not null) return new(error, []);
                    value = parsed;
                }

                var old = Value(key);
                object now;
                if (value is null || Equals(value, def.Default))
                {
                    next.Remove(key);
                    now = def.Default;
                    toWrite.Add((key, null));
                }
                else
                {
                    next[key] = value;
                    now = value;
                    toWrite.Add((key, Format(value)));
                }
                if (!Equals(old, now)) changes.Add(new(key, def.Label, Show(old), Show(now)));
            }

            if (changes.Count == 0) return new(null, []);

            // Trava contra se trancar para fora: sem o login do Windows, precisa existir um administrador local ativo.
            var windowsAfter = next.TryGetValue("identity.windows.enabled", out var w) ? w is true : (bool)_defs["identity.windows.enabled"].Default;
            if (!windowsAfter && !_local.HasEnabledAdmin())
                return new("Não dá para desligar o login do Windows sem um administrador local ativo. Crie um na aba Identidade antes.", []);

            using (var conn = _db.Open())
            using (var tx = conn.BeginTransaction())
            {
                foreach (var (key, text) in toWrite)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    if (text is null)
                    {
                        cmd.CommandText = "DELETE FROM settings WHERE key = $k";
                        cmd.Add("$k", Prefix + key);
                    }
                    else
                    {
                        cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)";
                        cmd.Add("$k", Prefix + key);
                        cmd.Add("$v", text);
                    }
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)";
                    cmd.Add("$k", VersionKey);
                    cmd.Add("$v", (Version + 1).ToString());
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }

            _stored = next;
            Interlocked.Increment(ref _version);
            Apply();
        }

        _log.LogInformation("{User} alterou a configuração (versão {Version}): {Changes}", user, Version,
            string.Join("; ", changes.Select(c => $"{c.Key}: {c.Old} → {c.New}")));
        return new(null, changes);
    }

    /// <summary>
    /// Empurra o valor em vigor para as opções que o resto do servidor já lê, para as chaves valerem na hora
    /// sem reiniciar (e sem cada parte do código ter de conhecer o motor).
    /// </summary>
    private void Apply()
    {
        var o = _options.Value;
        o.CommunityFeedUrl = GetText("chocolatey.communityFeedUrl");
        o.InternalFeedPath = GetText("internalApps.feedPath");
        o.Agent.PollSeconds = GetInt("agent.pollSeconds");
        o.Agent.JobTimeoutSeconds = GetInt("agent.jobTimeoutSeconds");
        o.Agent.OnlineSeconds = GetInt("agent.onlineSeconds");
    }

    private static (object? Value, string? Error) Validate(SettingDef def, JsonElement raw)
    {
        switch (def.Type)
        {
            case SettingType.Bool:
                return raw.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? (raw.GetBoolean(), null)
                    : (null, $"\"{def.Label}\" deve ser sim ou não.");

            case SettingType.Int:
                if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetInt32(out var n))
                    return (null, $"\"{def.Label}\" deve ser um número inteiro.");
                return n < def.Min || n > def.Max
                    ? (null, $"\"{def.Label}\" deve ficar entre {def.Min} e {def.Max}.")
                    : (n, null);

            case SettingType.Url:
            {
                var text = raw.ValueKind == JsonValueKind.String ? raw.GetString()!.Trim() : "";
                return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && text.Length <= 500
                    ? (text, null)
                    : (null, $"\"{def.Label}\" deve ser um endereço http ou https.");
            }

            default:
            {
                if (raw.ValueKind != JsonValueKind.String) return (null, $"\"{def.Label}\" deve ser um texto.");
                var text = raw.GetString()!.Trim();
                if (text.Length > 500 || text.Any(char.IsControl)) return (null, $"\"{def.Label}\" tem caracteres inválidos ou é longo demais.");
                return (text, null);
            }
        }
    }

    private static object? Parse(SettingDef def, string text) => def.Type switch
    {
        SettingType.Bool => bool.TryParse(text, out var b) ? b : null,
        SettingType.Int => int.TryParse(text, out var n) && n >= def.Min && n <= def.Max ? n : null,
        _ => text,
    };

    private static string Format(object value) => value is bool b ? (b ? "true" : "false") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static string Show(object value) => value switch
    {
        bool b => b ? "Sim" : "Não",
        string { Length: 0 } => "(vazio)",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };
}
