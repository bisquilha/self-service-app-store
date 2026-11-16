using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Loja.Agent;

/// <summary>
/// O que a loja mostra como "Instalado / Atualizar": pacotes do Chocolatey e programas do Windows.
/// SOMENTE LEITURA, igual ao script do Tactical (Loja-Choco.ps1): nada é instalado, alterado ou removido por aqui.
/// </summary>
public static partial class Inventory
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\|[^|\s]+$")]
    private static partial Regex PackageLine();

    /// <summary>Uma linha "pacote|versão" por pacote do Chocolatey. Vazio se o choco não existe ou falha.</summary>
    public static async Task<string> ChocoListAsync(AgentOptions options, CancellationToken ct)
    {
        var choco = options.ResolvedChocoPath();
        if (!File.Exists(choco)) return "";

        // choco 2.x lista só o que está instalado com "list -r"; o 1.x precisa de "--local-only".
        var version = await RunAsync(choco, ["--version"], ct);
        var major = int.TryParse(version.Split('.')[0].Trim(), out var m) ? m : 0;
        var text = await RunAsync(choco, major >= 2 ? ["list", "-r"] : ["list", "--local-only", "-r"], ct);

        return string.Join('\n', text.Split('\n').Select(l => l.Trim()).Where(l => PackageLine().IsMatch(l)));
    }

    /// <summary>
    /// Programas do Windows (chaves Uninstall do registro, 64 e 32 bits), uma linha "nome&lt;TAB&gt;versão" por programa.
    /// Alcance: só programas instalados para a máquina toda (HKLM); não aparecem os instalados só para um usuário nem os portáteis.
    /// </summary>
    public static string WindowsPrograms()
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        var seen = new HashSet<string>();
        var lines = new List<string>();
        foreach (var root in roots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                try
                {
                    using var k = key.OpenSubKey(name);
                    if (k is null) continue;
                    var display = Clean(k.GetValue("DisplayName") as string);
                    if (display.Length == 0 || display.Length > 200) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue; // componentes escondidos do sistema
                    if (k.GetValue("ParentKeyName") is not null) continue;              // atualização ou patch de outro programa
                    var release = k.GetValue("ReleaseType") as string;
                    if (release is "Update" or "Hotfix" or "Security Update" or "Service Pack") continue;
                    if (UpdateName().IsMatch(display)) continue;

                    var version = Clean(k.GetValue("DisplayVersion")?.ToString());
                    if (version.Length > 50) version = version[..50];
                    if (!seen.Add((display + "\t" + version).ToLowerInvariant())) continue;
                    lines.Add(display + "\t" + version);
                }
                catch
                {
                    // chave ilegível: segue para a próxima
                }
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines.Take(1500));
    }

    [GeneratedRegex(@"^(KB\d+|Update for |Security Update for |Hotfix for )")]
    private static partial Regex UpdateName();

    private static string Clean(string? s) =>
        string.IsNullOrEmpty(s) ? "" : Regex.Replace(Regex.Replace(s, @"[\p{C}]", " "), @"\s+", " ").Trim();

    private static async Task<string> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(120));
            var output = await p.StandardOutput.ReadToEndAsync(limit.Token);
            await p.WaitForExitAsync(limit.Token);
            return output;
        }
        catch
        {
            return "";
        }
    }
}
