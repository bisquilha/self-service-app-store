using Loja.Shared;
using Microsoft.Data.Sqlite;

namespace Loja.Server.Data;

/// <summary>
/// Grupo do AD de um aplicativo. O SID é a identidade (sobrevive a renomear o grupo no AD); o nome é para exibição.
/// Sid nulo = grupo salvo antes da 1.6.0 que ainda não foi resolvido (o acesso cai para o nome). "*" é "todos os usuários".
/// </summary>
public record GroupRef(string Name, string? Sid = null)
{
    public bool IsEveryone => Name == "*" && Sid is null;

    /// <summary>Chave de comparação: o SID quando existe, senão o nome sem diferenciar maiúsculas.</summary>
    public string Key => Sid is not null ? Sid.ToUpperInvariant() : Name.ToLowerInvariant();

    public GroupRefDto ToDto() => new(Name, Sid);
}

public record AppRow(
    int Id,
    string PackageId,
    string Source,
    string Title,
    string? Description,
    string? Version,
    string? IconUrl,
    bool Enabled,
    bool AutoUpdate,
    bool AllowUninstall,
    List<GroupRef> GroupRefs)
{
    /// <summary>Nomes dos grupos (campo que o app 1.2.0 e anteriores conhecem).</summary>
    public List<string> Groups => GroupRefs.Select(g => g.Name).ToList();

    public AdminAppDto ToAdminDto(List<string>? missingGroups = null, List<string>? aliases = null) =>
        new(Id, PackageId, Source, Title, Description, Version, Enabled, AutoUpdate, AllowUninstall, Groups, missingGroups ?? new(),
            GroupRefs.Select(g => g.ToDto()).ToList(), aliases);
}

/// <summary>Apps liberados na loja e os grupos do AD que podem instalar cada um.</summary>
public class AppRepository(Db db)
{
    private const string Columns = "id, package_id, source, title, description, version, icon_url, enabled, auto_update, allow_uninstall";

    public List<AppRow> List(bool onlyEnabled)
    {
        using var conn = db.Open();
        var groups = LoadGroups(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM apps" + (onlyEnabled ? " WHERE enabled = 1" : "") + " ORDER BY title COLLATE NOCASE";
        return ReadApps(cmd, groups);
    }

    public AppRow? Get(int id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM apps WHERE id = $id";
        cmd.Add("$id", id);
        return ReadApps(cmd, LoadGroups(conn, id)).FirstOrDefault();
    }

    public AppRow? Find(string packageId, string source)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM apps WHERE package_id = $p AND source = $s";
        cmd.Add("$p", packageId);
        cmd.Add("$s", source);
        var app = ReadApps(cmd, new()).FirstOrDefault();
        return app is null ? null : Get(app.Id);
    }

    public int Insert(string packageId, string source, string title, string? description, string? version, string? iconUrl, IEnumerable<GroupRef> groups)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO apps (package_id, source, title, description, version, icon_url, enabled, auto_update, created_utc, updated_utc)
            VALUES ($p, $s, $t, $d, $v, $i, 1, 0, $now, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Add("$p", packageId);
        cmd.Add("$s", source);
        cmd.Add("$t", title);
        cmd.Add("$d", description);
        cmd.Add("$v", version);
        cmd.Add("$i", iconUrl);
        cmd.Add("$now", Db.Now());
        var id = Convert.ToInt32(cmd.ExecuteScalar());
        ReplaceGroups(conn, tx, id, groups);
        tx.Commit();
        return id;
    }

    public bool UpdateSettings(int id, IEnumerable<GroupRef> groups, bool enabled, bool autoUpdate, bool allowUninstall)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE apps SET enabled = $e, auto_update = $a, allow_uninstall = $u, updated_utc = $now WHERE id = $id";
        cmd.Add("$e", enabled ? 1 : 0);
        cmd.Add("$a", autoUpdate ? 1 : 0);
        cmd.Add("$u", allowUninstall ? 1 : 0);
        cmd.Add("$now", Db.Now());
        cmd.Add("$id", id);
        if (cmd.ExecuteNonQuery() == 0) return false;
        ReplaceGroups(conn, tx, id, groups);
        tx.Commit();
        return true;
    }

    public void UpdateMetadata(int id, string title, string? description, string? version, string? iconUrl)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE apps SET title = $t, description = $d, version = $v, icon_url = $i, updated_utc = $now WHERE id = $id";
        cmd.Add("$t", title);
        cmd.Add("$d", description);
        cmd.Add("$v", version);
        cmd.Add("$i", iconUrl);
        cmd.Add("$now", Db.Now());
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    public bool Delete(int id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM apps WHERE id = $id";
        cmd.Add("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Nomes que o programa de cada aplicativo tem no Windows (cadastro da TI), por id de aplicativo.</summary>
    public Dictionary<int, List<string>> ListAliases()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT app_id, alias FROM app_aliases ORDER BY alias COLLATE NOCASE";
        var result = new Dictionary<int, List<string>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt32(0);
            if (!result.TryGetValue(id, out var list)) result[id] = list = new();
            list.Add(r.GetString(1));
        }
        return result;
    }

    public List<string> GetAliases(int appId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT alias FROM app_aliases WHERE app_id = $id ORDER BY alias COLLATE NOCASE";
        cmd.Add("$id", appId);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Troca os nomes do aplicativo. Os nomes já vêm limpos (<see cref="Services.ProgramAliases.Clean"/>).</summary>
    public bool SetAliases(int appId, IEnumerable<string> aliases)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var exists = conn.CreateCommand())
        {
            exists.Transaction = tx;
            exists.CommandText = "SELECT COUNT(*) FROM apps WHERE id = $id";
            exists.Add("$id", appId);
            if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return false;
        }
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM app_aliases WHERE app_id = $id";
            del.Add("$id", appId);
            del.ExecuteNonQuery();
        }
        foreach (var alias in aliases)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO app_aliases (app_id, alias, alias_key) VALUES ($id, $a, $k)";
            ins.Add("$id", appId);
            ins.Add("$a", alias);
            ins.Add("$k", Services.ProgramAliases.KeyOf(alias));
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    /// <summary>Troca os grupos de vários aplicativos numa transação só: ou todos mudam, ou nenhum.</summary>
    public void UpdateGroupsMany(IReadOnlyList<(int Id, List<GroupRef> Groups)> changes)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (id, groups) in changes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE apps SET updated_utc = $now WHERE id = $id";
            cmd.Add("$now", Db.Now());
            cmd.Add("$id", id);
            if (cmd.ExecuteNonQuery() == 0) throw new InvalidOperationException($"Aplicativo {id} não existe mais.");
            ReplaceGroups(conn, tx, id, groups);
        }
        tx.Commit();
    }

    /// <summary>Grava o SID de um grupo salvo só pelo nome (migração para a 1.6.0). Se o app já tem esse SID, some a linha antiga.</summary>
    public void SetGroupSid(int appId, string name, string sid)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE OR IGNORE app_groups SET group_sid = $s WHERE app_id = $id AND group_name = $n AND group_sid IS NULL";
        cmd.Add("$s", sid);
        cmd.Add("$id", appId);
        cmd.Add("$n", name);
        if (cmd.ExecuteNonQuery() > 0) return;

        // Não atualizou: ou o app já tem esse SID (a linha sem SID é repetida), ou a linha nem existe mais.
        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM app_groups WHERE app_id = $id AND group_name = $n AND group_sid IS NULL AND EXISTS (SELECT 1 FROM app_groups WHERE app_id = $id AND group_sid = $s)";
        del.Add("$id", appId);
        del.Add("$n", name);
        del.Add("$s", sid);
        del.ExecuteNonQuery();
    }

    /// <summary>O grupo foi renomeado no AD: atualiza o nome de exibição em todos os aplicativos que usam esse SID.</summary>
    public void RenameGroup(string sid, string newName)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE OR IGNORE app_groups SET group_name = $n WHERE group_sid = $s AND group_name <> $n";
        cmd.Add("$n", newName);
        cmd.Add("$s", sid);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Troca os grupos do aplicativo (dentro da transação de quem chama). Repetidos pela mesma chave são ignorados.</summary>
    internal static void ReplaceGroups(SqliteConnection conn, SqliteTransaction tx, int appId, IEnumerable<GroupRef> groups)
    {
        using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM app_groups WHERE app_id = $id";
        del.Add("$id", appId);
        del.ExecuteNonQuery();

        var seen = new HashSet<string>();
        foreach (var group in groups)
        {
            if (!seen.Add(group.Key)) continue;
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO app_groups (app_id, group_name, group_sid) VALUES ($id, $g, $s)";
            ins.Add("$id", appId);
            ins.Add("$g", group.Name);
            ins.Add("$s", group.Sid);
            ins.ExecuteNonQuery();
        }
    }

    private static Dictionary<int, List<GroupRef>> LoadGroups(SqliteConnection conn, int? appId = null)
    {
        var result = new Dictionary<int, List<GroupRef>>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT app_id, group_name, group_sid FROM app_groups" + (appId is null ? "" : " WHERE app_id = $id") + " ORDER BY group_name";
        if (appId is not null) cmd.Add("$id", appId.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt32(0);
            if (!result.TryGetValue(id, out var list)) result[id] = list = new();
            list.Add(new GroupRef(r.GetString(1), r.GetNullableString(2)));
        }
        return result;
    }

    private static List<AppRow> ReadApps(SqliteCommand cmd, Dictionary<int, List<GroupRef>> groups)
    {
        var list = new List<AppRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt32(0);
            list.Add(new AppRow(
                id,
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.GetNullableString(4),
                r.GetNullableString(5),
                r.GetNullableString(6),
                r.GetInt32(7) == 1,
                r.GetInt32(8) == 1,
                r.GetInt32(9) == 1,
                groups.TryGetValue(id, out var g) ? g : new()));
        }
        return list;
    }
}
