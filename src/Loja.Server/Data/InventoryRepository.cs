using System.Text.RegularExpressions;

namespace Loja.Server.Data;

/// <summary>Programa das chaves Uninstall do Windows (nome como aparece em "Programas e Recursos" e versão, vazia se o programa não informa).</summary>
public record HostProgram(string Name, string Version);

/// <summary>
/// O que a loja sabe de uma máquina. Versions = pacotes do Chocolatey. Programs = programas do Windows; só valem quando
/// ProgramsUtc tem data (a coleta é do script 1.6.0 em diante: sem data, ninguém afirma nada sobre programas fora da loja).
/// </summary>
public record HostInventory(DateTime? UpdatedUtc, Dictionary<string, string> Versions, IReadOnlyList<HostProgram>? ProgramList = null, DateTime? ProgramsUtc = null)
{
    public IReadOnlyList<HostProgram> Programs => ProgramList ?? [];
}

/// <summary>Pacotes do Chocolatey e programas do Windows instalados em cada máquina, conforme o último retorno do script.</summary>
public partial class InventoryRepository(Db db)
{
    public const int MaxPrograms = 2000;
    public const int MaxProgramName = 200;
    public const int MaxProgramVersion = 50;

    public HostInventory Get(string hostname)
    {
        using var conn = db.Open();
        DateTime? updated = null;
        DateTime? programsAt = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT updated_utc, programs_utc FROM host_inventory WHERE hostname = $h";
            cmd.Add("$h", hostname);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                updated = Db.ParseDate(r.GetString(0));
                if (r.GetNullableString(1) is { } text) programsAt = Db.ParseDate(text);
            }
        }

        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT package_id, version FROM host_packages WHERE hostname = $h";
            cmd.Add("$h", hostname);
            using var r = cmd.ExecuteReader();
            while (r.Read()) versions[r.GetString(0)] = r.GetString(1);
        }

        var programs = new List<HostProgram>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, version FROM host_programs WHERE hostname = $h ORDER BY name COLLATE NOCASE, version";
            cmd.Add("$h", hostname);
            using var r = cmd.ExecuteReader();
            while (r.Read()) programs.Add(new HostProgram(r.GetString(0), r.GetString(1)));
        }

        return new HostInventory(updated, versions, programs, programsAt);
    }

    public void Replace(string hostname, IReadOnlyDictionary<string, string> versions)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM host_packages WHERE hostname = $h";
            del.Add("$h", hostname);
            del.ExecuteNonQuery();
        }

        foreach (var (package, version) in versions)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO host_packages (hostname, package_id, version) VALUES ($h, $p, $v)";
            ins.Add("$h", hostname);
            ins.Add("$p", package);
            ins.Add("$v", version);
            ins.ExecuteNonQuery();
        }

        // Upsert (e não INSERT OR REPLACE): não pode apagar o programs_utc da mesma linha.
        using (var up = conn.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = "INSERT INTO host_inventory (hostname, updated_utc) VALUES ($h, $now) ON CONFLICT(hostname) DO UPDATE SET updated_utc = excluded.updated_utc";
            up.Add("$h", hostname);
            up.Add("$now", Db.Now());
            up.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Troca a lista de programas do Windows da máquina e marca a data da coleta.</summary>
    public void ReplacePrograms(string hostname, IReadOnlyCollection<HostProgram> programs)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM host_programs WHERE hostname = $h";
            del.Add("$h", hostname);
            del.ExecuteNonQuery();
        }

        foreach (var program in programs)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO host_programs (hostname, name, version) VALUES ($h, $n, $v)";
            ins.Add("$h", hostname);
            ins.Add("$n", program.Name);
            ins.Add("$v", program.Version);
            ins.ExecuteNonQuery();
        }

        var now = Db.Now();
        using (var up = conn.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = """
                INSERT INTO host_inventory (hostname, updated_utc, programs_utc) VALUES ($h, $now, $now)
                ON CONFLICT(hostname) DO UPDATE SET programs_utc = excluded.programs_utc
                """;
            up.Add("$h", hostname);
            up.Add("$now", now);
            up.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Máquinas que têm o pacote instalado, com a versão (para a atualização automática).</summary>
    public List<(string Hostname, string Version)> HostsWithPackage(string packageId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hostname, version FROM host_packages WHERE package_id = $p";
        cmd.Add("$p", packageId);
        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Inventário de todas as máquinas (para o painel). Não traz os programas do Windows: são muitos e só o detalhe da máquina os usa.</summary>
    public Dictionary<string, HostInventory> ListAll()
    {
        using var conn = db.Open();
        var result = new Dictionary<string, HostInventory>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT hostname, updated_utc, programs_utc FROM host_inventory";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result[r.GetString(0)] = new HostInventory(Db.ParseDate(r.GetString(1)), new(StringComparer.OrdinalIgnoreCase), null,
                    r.GetNullableString(2) is { } text ? Db.ParseDate(text) : null);
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT hostname, package_id, version FROM host_packages";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var host = r.GetString(0);
                if (!result.TryGetValue(host, out var inv))
                    result[host] = inv = new HostInventory(null, new(StringComparer.OrdinalIgnoreCase));
                inv.Versions[r.GetString(1)] = r.GetString(2);
            }
        }
        return result;
    }

    /// <summary>Nomes de programa do Windows que contêm o texto, com em quantas máquinas aparecem (para a TI escolher o nome certo de um aplicativo).</summary>
    public List<(string Name, int Machines)> SearchPrograms(string text, int take)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name, COUNT(DISTINCT hostname) AS machines FROM host_programs
            WHERE name LIKE $q ESCAPE '\' GROUP BY name ORDER BY machines DESC, name COLLATE NOCASE LIMIT $take
            """;
        cmd.Add("$q", "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        cmd.Add("$take", Math.Clamp(take, 1, 100));
        var list = new List<(string, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
        return list;
    }

    /// <summary>Lê a saída "pacote|versão" (uma por linha) do choco list.</summary>
    public static Dictionary<string, string> Parse(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Trim().Split('|');
            if (parts.Length != 2) continue;
            var (id, version) = (parts[0].Trim(), parts[1].Trim());
            if (id.Length is > 0 and <= 100 && version.Length is > 0 and <= 50 && !id.Contains(' ') && !version.Contains(' '))
                result[id] = version;
        }
        return result;
    }

    [GeneratedRegex(@"[\p{C}]")]
    private static partial Regex ControlChars();

    /// <summary>
    /// Lê as linhas "nome&lt;TAB&gt;versão" das chaves Uninstall. Texto externo: caracteres de controle saem, tamanhos são limitados,
    /// repetidos (mesmo nome e versão) viram um só e há um teto de programas por máquina. Linha fora do padrão é ignorada.
    /// </summary>
    public static List<HostProgram> ParsePrograms(string? text)
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<HostProgram>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length is < 1 or > 2) continue;
            var name = ControlChars().Replace(parts[0], "").Trim();
            var version = parts.Length == 2 ? ControlChars().Replace(parts[1], "").Trim() : "";
            if (name.Length is 0 or > MaxProgramName || version.Length > MaxProgramVersion) continue;
            if (!seen.Add((name.ToLowerInvariant(), version.ToLowerInvariant()))) continue;
            result.Add(new HostProgram(name, version));
            if (result.Count >= MaxPrograms) break;
        }
        return result;
    }
}
