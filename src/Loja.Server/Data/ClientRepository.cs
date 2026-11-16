using Microsoft.Data.Sqlite;

namespace Loja.Server.Data;

public record ClientReleaseRow(string Version, string Sha256, long Size, bool Mandatory, bool IsCurrent, DateTime PublishedUtc, string PublishedBy);

public record HostClientRow(string Hostname, string Version, string Username, DateTime LastSeenUtc);

/// <summary>Versões do app da loja (autoupdate) e a versão que cada máquina está usando.</summary>
public class ClientRepository(Db db)
{
    private const string Columns = "version, sha256, size, mandatory, is_current, published_utc, published_by";

    public List<ClientReleaseRow> ListReleases()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM client_releases ORDER BY published_utc DESC";
        return ReadReleases(cmd);
    }

    public ClientReleaseRow? Current()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM client_releases WHERE is_current = 1";
        return ReadReleases(cmd).FirstOrDefault();
    }

    public ClientReleaseRow? Get(string version)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM client_releases WHERE version = $v";
        cmd.Add("$v", version);
        return ReadReleases(cmd).FirstOrDefault();
    }

    /// <summary>Grava (ou regrava) a versão e a torna a atual.</summary>
    public void Publish(string version, string sha256, long size, bool mandatory, string user)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "UPDATE client_releases SET is_current = 0");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO client_releases (version, sha256, size, mandatory, is_current, published_utc, published_by)
            VALUES ($v, $s, $z, $m, 1, $now, $u)
            """;
        cmd.Add("$v", version);
        cmd.Add("$s", sha256);
        cmd.Add("$z", size);
        cmd.Add("$m", mandatory ? 1 : 0);
        cmd.Add("$now", Db.Now());
        cmd.Add("$u", user);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>Torna atual uma versão já publicada (ex.: voltar para a anterior).</summary>
    public bool SetCurrent(string version, bool mandatory)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var check = conn.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT COUNT(*) FROM client_releases WHERE version = $v";
        check.Add("$v", version);
        if (Convert.ToInt64(check.ExecuteScalar()) == 0) return false;

        Exec(conn, tx, "UPDATE client_releases SET is_current = 0");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE client_releases SET is_current = 1, mandatory = $m WHERE version = $v";
        cmd.Add("$v", version);
        cmd.Add("$m", mandatory ? 1 : 0);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public bool Delete(string version)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM client_releases WHERE version = $v AND is_current = 0";
        cmd.Add("$v", version);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void RecordHost(string hostname, string version, string username)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO host_clients (hostname, version, username, last_seen_utc) VALUES ($h, $v, $u, $now)";
        cmd.Add("$h", hostname);
        cmd.Add("$v", version);
        cmd.Add("$u", username);
        cmd.Add("$now", Db.Now());
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, HostClientRow> ListHosts()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hostname, version, username, last_seen_utc FROM host_clients";
        var result = new Dictionary<string, HostClientRow>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetString(0)] = new HostClientRow(r.GetString(0), r.GetString(1), r.GetString(2), Db.ParseDate(r.GetString(3)));
        return result;
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<ClientReleaseRow> ReadReleases(SqliteCommand cmd)
    {
        var list = new List<ClientReleaseRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ClientReleaseRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3) == 1, r.GetInt32(4) == 1,
                Db.ParseDate(r.GetString(5)), r.GetString(6)));
        }
        return list;
    }
}
