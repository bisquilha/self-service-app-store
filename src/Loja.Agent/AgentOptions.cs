namespace Loja.Agent;

/// <summary>Configuração do agente (arquivo agent.json ao lado do .exe). Variáveis LOJAAGENT_* sobrepõem o arquivo.</summary>
public class AgentOptions
{
    /// <summary>Endereço do servidor da loja, sem barra no final. Ex.: https://loja.corp.example.com</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>SHA-256 do certificado do servidor (como no loja.json do app). Vazio só é aceito com http (testes).</summary>
    public string CertSha256 { get; set; } = "";

    /// <summary>Caminho do choco.exe. Vazio = C:\ProgramData\chocolatey\bin\choco.exe (ou o ChocolateyInstall).</summary>
    public string ChocoPath { get; set; } = "";

    /// <summary>Pasta com os pacotes internos (.nupkg). Vazio = sem feed interno.</summary>
    public string InternalFeed { get; set; } = "";

    public string CommunityFeed { get; set; } = "https://community.chocolatey.org/api/v2/";

    /// <summary>Quanto tempo o agente espera, na conexão aberta, por um trabalho novo (o servidor limita a 30).</summary>
    public int JobWaitSeconds { get; set; } = 25;

    /// <summary>Pasta de dados do agente (log). Vazio = C:\ProgramData\LojaAgent.</summary>
    public string DataDir { get; set; } = "";

    /// <summary>Só para testes: ignora a trava que recusa Windows Server.</summary>
    public bool AllowServerOs { get; set; }

    public string ResolvedDataDir() =>
        string.IsNullOrWhiteSpace(DataDir) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LojaAgent") : DataDir;

    public string ResolvedChocoPath()
    {
        if (!string.IsNullOrWhiteSpace(ChocoPath)) return ChocoPath;
        var root = Environment.GetEnvironmentVariable("ChocolateyInstall");
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
        return Path.Combine(root, "bin", "choco.exe");
    }
}
