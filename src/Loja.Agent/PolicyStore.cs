using System.Text.Json;
using Loja.Shared;

namespace Loja.Agent;

/// <summary>
/// Política que o servidor entrega (Configurações do painel). Fica em memória e numa cópia em disco (policy.json),
/// para o agente continuar obedecendo à última política conhecida se o servidor estiver fora do ar ao reiniciar.
/// Sem política (servidor antigo ou primeira execução), vale o agent.json, como antes.
/// </summary>
public sealed class PolicyStore(AgentOptions options)
{
    private volatile AgentPolicyDto? _current;

    public AgentPolicyDto? Current => _current;

    /// <summary>Versão em mãos, ou -1 se ainda não há política (assim a primeira resposta sempre dispara a busca).</summary>
    public long Version => _current?.Version ?? -1;

    private string FilePath => Path.Combine(options.ResolvedDataDir(), "policy.json");

    public void LoadCached()
    {
        try
        {
            if (File.Exists(FilePath)) _current = JsonSerializer.Deserialize<AgentPolicyDto>(File.ReadAllText(FilePath));
        }
        catch
        {
            // cópia ilegível: ignora, a próxima busca grava outra
        }
    }

    public void Update(AgentPolicyDto policy)
    {
        _current = policy;
        try
        {
            Directory.CreateDirectory(options.ResolvedDataDir());
            File.WriteAllText(FilePath, JsonSerializer.Serialize(policy));
        }
        catch
        {
            // sem a cópia em disco só se perde a política até o próximo batimento
        }
    }

    /// <summary>Endereço do repositório da comunidade em vigor: o da política, se houver; senão o do agent.json.</summary>
    public static string CommunityFeed(AgentOptions options, AgentPolicyDto? policy) =>
        policy is not null && !string.IsNullOrWhiteSpace(policy.CommunityFeed) ? policy.CommunityFeed : options.CommunityFeed;

    /// <summary>Pasta interna em vigor. Com política, quem manda é o servidor (vazio = sem pacotes internos); sem política, o agent.json.</summary>
    public static string InternalFeed(AgentOptions options, AgentPolicyDto? policy) =>
        policy is not null ? policy.InternalFeed ?? "" : options.InternalFeed;

    /// <summary>A política desliga esta fonte? (Desinstalar não usa fonte e nunca passa por aqui.)</summary>
    public static bool IsSourceDisabled(AgentPolicyDto? policy, string? source) =>
        policy is not null && (source == PackageSources.Internal ? !policy.InternalAppsEnabled : !policy.ChocolateyEnabled);
}
