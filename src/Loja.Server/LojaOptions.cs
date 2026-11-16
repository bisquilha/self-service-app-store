namespace Loja.Server;

public class LojaOptions
{
    public const string Section = "Loja";

    /// <summary>Pasta do banco SQLite e do cache de ícones.</summary>
    public string DataDir { get; set; } = "App_Data";

    /// <summary>Domínio NetBIOS usado quando um grupo é informado sem "DOMINIO\".</summary>
    public string DefaultDomain { get; set; } = "";

    /// <summary>Grupos do AD com acesso ao painel de administração.</summary>
    public List<string> AdminGroups { get; set; } = new();

    /// <summary>Prefixo dos grupos da loja no AD. A lista de grupos do painel mostra só estes, a não ser que se peça todos.</summary>
    public string GroupPrefix { get; set; } = "LOJA-";

    /// <summary>Tipos de agente do Tactical onde a loja pode instalar. Servidores ficam de fora.</summary>
    public List<string> AllowedMonitoringTypes { get; set; } = new() { "workstation" };

    /// <summary>
    /// Recusa agentes cujo sistema operacional (informado pelo Tactical) seja Windows Server,
    /// mesmo que estejam cadastrados como "workstation". Sem sistema informado, também recusa.
    /// </summary>
    public bool BlockServerOperatingSystems { get; set; } = true;

    /// <summary>Exige que o Tactical mostre o solicitante como usuário logado na máquina.</summary>
    public bool RequireLoggedUser { get; set; } = true;

    public string CommunityFeedUrl { get; set; } = "https://community.chocolatey.org/api/v2/";

    /// <summary>Pasta UNC com os pacotes internos (.nupkg). Vazio = sem feed interno.</summary>
    public string InternalFeedPath { get; set; } = "";

    /// <summary>Tempo sem retorno do script até o pedido ser marcado como "sem retorno".</summary>
    public int RequestTimeoutMinutes { get; set; } = 60;

    /// <summary>Intervalo para atualizar nome, versão e ícone dos apps a partir dos repositórios.</summary>
    public int MetadataRefreshHours { get; set; } = 12;

    /// <summary>Idade máxima da lista de programas de uma máquina antes de pedir uma nova ao abrir a loja.</summary>
    public int InventoryMaxAgeMinutes { get; set; } = 10;

    /// <summary>Janela (hora local do servidor) em que a atualização automática roda: [início, fim).</summary>
    public int AutoUpdateStartHour { get; set; } = 12;

    public int AutoUpdateEndHour { get; set; } = 14;

    /// <summary>Máximo de atualizações automáticas disparadas a cada ciclo (10 min), para não sobrecarregar o Tactical.</summary>
    public int AutoUpdateMaxPerCycle { get; set; } = 20;

    /// <summary>
    /// Caminho do LojaDeAplicativos.exe que acompanha o servidor. Vazio = "cliente\LojaDeAplicativos.exe" ao lado do servidor
    /// (onde o Publicar.ps1 o coloca). Ao subir, ele vira a versão atual do app oferecida às estações.
    /// </summary>
    public string ClientBundlePath { get; set; } = "";

    /// <summary>A atualização do app que veio com o servidor é obrigatória? (padrão: não; o app oferece e o usuário atualiza.)</summary>
    public bool ClientUpdateMandatory { get; set; }

    public TacticalOptions Tactical { get; set; } = new();

    /// <summary>Agente próprio da loja (Loja.Agent). Desligado por padrão: nada muda até ser ligado.</summary>
    public AgentOptions Agent { get; set; } = new();
}

public class AgentOptions
{
    /// <summary>
    /// Liga o agente. As máquinas que têm o agente instalado passam a usá-lo; as demais continuam pelo Tactical.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Só o agente, sem Tactical (a configuração do Tactical deixa de ser exigida).</summary>
    public bool Only { get; set; }

    /// <summary>Sem batimento por este tempo, o agente aparece como offline.</summary>
    public int OnlineSeconds { get; set; } = 180;

    /// <summary>De quanto em quanto tempo o agente bate (o servidor repassa este valor ao agente).</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Tempo máximo de um pedido: passou disso, o agente encerra o instalador e o pedido falha com "tempo esgotado".</summary>
    public int JobTimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// Só para testes: em Development, aceita como identidade da máquina o login do usuário que chamou
    /// (em produção o agente se identifica com a conta do computador, PC$).
    /// </summary>
    public bool TrustUserIdentityInDevelopment { get; set; }
}

public class TacticalOptions
{
    /// <summary>Simula o Tactical (desenvolvimento e testes). Nunca usar em produção.</summary>
    public bool Mock { get; set; }

    /// <summary>Com Mock: não conclui o pedido sozinho e mostra o token no log, para testar o script real.</summary>
    public bool MockManualCallback { get; set; }

    /// <summary>URL da API do Tactical, ex.: https://api.rmm.empresa.com.br</summary>
    public string Url { get; set; } = "";

    public string ApiKey { get; set; } = "";

    /// <summary>ID do script "Loja-Choco" no Script Manager do Tactical.</summary>
    public int ScriptId { get; set; }

    public int ScriptTimeoutSeconds { get; set; } = 3600;

    public int AgentCacheSeconds { get; set; } = 60;
}
