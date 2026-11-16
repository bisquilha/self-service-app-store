using Microsoft.Data.Sqlite;

namespace Loja.Server.Data;

/// <summary>Uma alteração a registrar. Undo = a alteração é um "Desfazer" de outra.</summary>
public record AuditEntry(
    string User,
    string Kind,
    string EntityType,
    string? EntityId,
    string? EntityLabel,
    string? Field,
    string? OldValue,
    string? NewValue,
    string? Batch = null,
    bool Undo = false);

public record AuditRow(
    long Id,
    DateTime Utc,
    string User,
    string Kind,
    string EntityType,
    string? EntityId,
    string? EntityLabel,
    string? Field,
    string? OldValue,
    string? NewValue,
    string? Batch,
    bool Undo);

public record AuditQuery(
    string? Text = null,
    string? EntityType = null,
    string? EntityId = null,
    string? User = null,
    string? Kind = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Skip = 0,
    int Take = 50,
    string? SortBy = null,
    bool Descending = true);

/// <summary>Trilha de auditoria das alterações feitas pelo painel (tabela audit_log).</summary>
public class AuditRepository(Db db)
{
    private const string Columns = "id, utc, username, kind, entity_type, entity_id, entity_label, field, old_value, new_value, batch, undo";

    // Colunas que o painel pode pedir para ordenar. Lista fixa: o nome vem da URL e nunca entra no SQL como veio.
    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["when"] = "utc",
        ["user"] = "username COLLATE NOCASE",
        ["entity"] = "entity_label COLLATE NOCASE",
        ["field"] = "field COLLATE NOCASE",
        ["kind"] = "kind",
    };

    public void Add(IEnumerable<AuditEntry> entries)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var now = Db.Now();
        foreach (var e in entries)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO audit_log (utc, username, kind, entity_type, entity_id, entity_label, field, old_value, new_value, batch, undo) VALUES ($t, $u, $k, $et, $ei, $el, $f, $o, $n, $b, $x)";
            cmd.Add("$t", now);
            cmd.Add("$u", e.User);
            cmd.Add("$k", e.Kind);
            cmd.Add("$et", e.EntityType);
            cmd.Add("$ei", e.EntityId);
            cmd.Add("$el", e.EntityLabel);
            cmd.Add("$f", e.Field);
            cmd.Add("$o", Trim(e.OldValue));
            cmd.Add("$n", Trim(e.NewValue));
            cmd.Add("$b", e.Batch);
            cmd.Add("$x", e.Undo ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public (List<AuditRow> Rows, int Total) Query(AuditQuery q)
    {
        using var conn = db.Open();
        var where = new List<string>();
        using var count = conn.CreateCommand();
        using var cmd = conn.CreateCommand();

        void Param(string name, object? value)
        {
            count.Add(name, value);
            cmd.Add(name, value);
        }

        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            where.Add("(username LIKE $txt OR entity_label LIKE $txt OR field LIKE $txt OR old_value LIKE $txt OR new_value LIKE $txt)");
            Param("$txt", $"%{q.Text.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(q.EntityType)) { where.Add("entity_type = $et"); Param("$et", q.EntityType); }
        if (!string.IsNullOrWhiteSpace(q.EntityId)) { where.Add("entity_id = $ei"); Param("$ei", q.EntityId); }
        if (!string.IsNullOrWhiteSpace(q.User)) { where.Add("username LIKE $user"); Param("$user", $"%{q.User.Trim()}%"); }
        if (!string.IsNullOrWhiteSpace(q.Kind)) { where.Add("kind = $kind"); Param("$kind", q.Kind); }
        if (q.FromUtc is { } from) { where.Add("utc >= $from"); Param("$from", Db.ToText(from)); }
        if (q.ToUtc is { } to) { where.Add("utc < $to"); Param("$to", Db.ToText(to)); }

        var whereSql = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        count.CommandText = "SELECT COUNT(*) FROM audit_log" + whereSql;
        var total = Convert.ToInt32(count.ExecuteScalar());

        cmd.CommandText = $"SELECT {Columns} FROM audit_log{whereSql} ORDER BY {OrderBy(q)} LIMIT $take OFFSET $skip";
        cmd.Add("$take", Math.Clamp(q.Take, 1, 500));
        cmd.Add("$skip", Math.Max(0, q.Skip));
        return (Read(cmd), total);
    }

    private static string OrderBy(AuditQuery q)
    {
        var direction = q.Descending ? "DESC" : "ASC";
        return q.SortBy is { } key && SortColumns.TryGetValue(key, out var column) ? $"{column} {direction}, id {direction}" : "id DESC";
    }

    private static string? Trim(string? value) => value is { Length: > 2000 } ? value[..1999] + "…" : value;

    private static List<AuditRow> Read(SqliteCommand cmd)
    {
        var list = new List<AuditRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AuditRow(
                r.GetInt64(0), Db.ParseDate(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetNullableString(5), r.GetNullableString(6), r.GetNullableString(7), r.GetNullableString(8), r.GetNullableString(9),
                r.GetNullableString(10), r.GetInt32(11) == 1));
        }
        return list;
    }
}
