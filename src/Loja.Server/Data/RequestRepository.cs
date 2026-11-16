using Loja.Shared;
using Microsoft.Data.Sqlite;

namespace Loja.Server.Data;

public record RequestRow(
    long Id,
    DateTime CreatedUtc,
    DateTime? FinishedUtc,
    string Username,
    string Hostname,
    string? AgentId,
    int? AppId,
    string PackageId,
    string Title,
    string Source,
    string Action,
    string Status,
    string? Message,
    int? ExitCode,
    string? Output,
    long? RetryOf = null)
{
    public RequestDto ToDto(bool includeOutput) =>
        new(Id, AppId, PackageId, Title, Hostname, Username, Action, Status, Message, CreatedUtc, FinishedUtc, includeOutput ? Output : null, ExitCode, RetryOf);
}

public record NewRequest(
    string Username,
    string Hostname,
    string? AgentId,
    int? AppId,
    string PackageId,
    string Title,
    string Source,
    string Action,
    string Status,
    string? Message,
    string? TokenHash,
    long? RetryOf = null);

public record RequestQuery(
    string? Text = null,
    string? Status = null,
    string? Action = null,
    string? Hostname = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Skip = 0,
    int Take = 50,
    string? SortBy = null,
    bool Descending = true,
    // Só pedidos destes usuários (como estão gravados em "username"). Lista vazia = ninguém. Nulo = sem filtro.
    // É assim que o filtro "grupo do usuário" chega ao banco: o servidor resolve o grupo no AD e passa os logins.
    IReadOnlyList<string>? Usernames = null);

/// <summary>Pedidos de instalação: é o log de auditoria da loja (quem, o quê, onde, quando, resultado).</summary>
public class RequestRepository(Db db)
{
    private const string Columns =
        "id, created_utc, finished_utc, username, hostname, agent_id, app_id, package_id, title, source, action, status, message, exit_code, output, retry_of";

    public long Insert(NewRequest n)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO requests (created_utc, finished_utc, username, hostname, agent_id, app_id, package_id, title, source, action, status, message, token_hash, retry_of)
            VALUES ($now, $fin, $u, $h, $ag, $app, $p, $t, $s, $a, $st, $m, $tk, $retry);
            SELECT last_insert_rowid();
            """;
        var now = Db.Now();
        cmd.Add("$now", now);
        cmd.Add("$fin", n.Status == RequestStatus.Running ? null : now);
        cmd.Add("$u", n.Username);
        cmd.Add("$h", n.Hostname);
        cmd.Add("$ag", n.AgentId);
        cmd.Add("$app", n.AppId);
        cmd.Add("$p", n.PackageId);
        cmd.Add("$t", n.Title);
        cmd.Add("$s", n.Source);
        cmd.Add("$a", n.Action);
        cmd.Add("$st", n.Status);
        cmd.Add("$m", n.Message);
        cmd.Add("$tk", n.TokenHash);
        cmd.Add("$retry", n.RetryOf);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public RequestRow? Get(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM requests WHERE id = $id";
        cmd.Add("$id", id);
        return Read(cmd).FirstOrDefault();
    }

    public RequestRow? FindRunningByToken(string tokenHash)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM requests WHERE token_hash = $tk AND status = $st";
        cmd.Add("$tk", tokenHash);
        cmd.Add("$st", RequestStatus.Running);
        return Read(cmd).FirstOrDefault();
    }

    public bool HasRunning(string hostname)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        // A consulta de pacotes (list) só lê, então não impede instalar/atualizar/desinstalar.
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM requests WHERE hostname = $h AND status = $st AND action <> $list)";
        cmd.Add("$h", hostname);
        cmd.Add("$st", RequestStatus.Running);
        cmd.Add("$list", RequestActions.List);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    public bool HasRunningList(string hostname)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM requests WHERE hostname = $h AND status = $st AND action = $list)";
        cmd.Add("$h", hostname);
        cmd.Add("$st", RequestStatus.Running);
        cmd.Add("$list", RequestActions.List);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    /// <summary>Este usuário já pediu esta ação para o pacote nesta máquina desde a data informada?</summary>
    public bool HasRecent(string hostname, string packageId, string action, string username, DateTime sinceUtc)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS (SELECT 1 FROM requests
                            WHERE hostname = $h AND package_id = $p COLLATE NOCASE AND action = $a
                              AND username = $u AND created_utc >= $since)
            """;
        cmd.Add("$h", hostname);
        cmd.Add("$p", packageId);
        cmd.Add("$a", action);
        cmd.Add("$u", username);
        cmd.Add("$since", Db.ToText(sinceUtc));
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    /// <summary>Encerra um pedido em andamento. O token é apagado para não poder ser reutilizado.</summary>
    public bool Finish(long id, string status, string message, int? exitCode, string? output)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE requests
               SET status = $st, message = $m, exit_code = $ec, output = $o, finished_utc = $now, token_hash = NULL
             WHERE id = $id AND status = $running
            """;
        cmd.Add("$st", status);
        cmd.Add("$m", message);
        cmd.Add("$ec", exitCode);
        cmd.Add("$o", output);
        cmd.Add("$now", Db.Now());
        cmd.Add("$id", id);
        cmd.Add("$running", RequestStatus.Running);
        return cmd.ExecuteNonQuery() > 0;
    }

    public int ExpireRunning(DateTime olderThanUtc, string message)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE requests
               SET status = $timeout, message = $m, finished_utc = $now, token_hash = NULL
             WHERE status = $running AND created_utc < $limit
            """;
        cmd.Add("$timeout", RequestStatus.Timeout);
        cmd.Add("$m", message);
        cmd.Add("$now", Db.Now());
        cmd.Add("$running", RequestStatus.Running);
        cmd.Add("$limit", Db.ToText(olderThanUtc));
        return cmd.ExecuteNonQuery();
    }

    public List<RequestRow> ListByUser(string username, string? hostname, int take)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM requests WHERE username = $u COLLATE NOCASE AND action <> $list"
                          + (string.IsNullOrWhiteSpace(hostname) ? "" : " AND hostname = $h")
                          + " ORDER BY id DESC LIMIT $take";
        cmd.Add("$u", username);
        cmd.Add("$h", hostname);
        cmd.Add("$list", RequestActions.List);
        cmd.Add("$take", take);
        return Read(cmd);
    }

    public List<RequestRow> ListAll(int take)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM requests WHERE action <> $list ORDER BY id DESC LIMIT $take";
        cmd.Add("$list", RequestActions.List);
        cmd.Add("$take", take);
        return Read(cmd);
    }

    // Colunas que o painel pode pedir para ordenar. Lista fixa: o nome vem da URL e nunca entra no SQL como veio.
    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["created"] = "created_utc",
        ["user"] = "username COLLATE NOCASE",
        ["host"] = "hostname COLLATE NOCASE",
        ["app"] = "title COLLATE NOCASE",
        ["action"] = "action",
        ["status"] = "status",
    };

    private static string OrderBy(RequestQuery q)
    {
        var direction = q.Descending ? "DESC" : "ASC";
        // O id desempata (e é a ordem natural: mais recente primeiro).
        return q.SortBy is { } key && SortColumns.TryGetValue(key, out var column) ? $"{column} {direction}, id {direction}" : "id DESC";
    }

    /// <summary>
    /// Quem pediu algo no histórico filtrado (sem o filtro de usuários e sem paginação), do jeito que está gravado.
    /// O filtro "grupo do usuário" parte daqui: só consulta o AD por quem realmente aparece nos resultados.
    /// </summary>
    public List<string> DistinctUsernames(RequestQuery q)
    {
        using var conn = db.Open();
        var where = new List<string> { "action <> $list" };
        using var cmd = conn.CreateCommand();
        cmd.Add("$list", RequestActions.List);
        AddFilters(q with { Usernames = null }, where, (name, value) => cmd.Add(name, value));
        cmd.CommandText = "SELECT DISTINCT username FROM requests WHERE " + string.Join(" AND ", where);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Histórico filtrado do painel (sem as consultas "list"), mais o total para paginação.</summary>
    public (List<RequestRow> Rows, int Total) Query(RequestQuery q)
    {
        using var conn = db.Open();
        var where = new List<string> { "action <> $list" };
        using var count = conn.CreateCommand();
        using var cmd = conn.CreateCommand();

        void Param(string name, object? value)
        {
            count.Add(name, value);
            cmd.Add(name, value);
        }

        Param("$list", RequestActions.List);
        AddFilters(q, where, Param);

        var whereSql = " WHERE " + string.Join(" AND ", where);
        count.CommandText = "SELECT COUNT(*) FROM requests" + whereSql;
        var total = Convert.ToInt32(count.ExecuteScalar());

        cmd.CommandText = $"SELECT {Columns} FROM requests{whereSql} ORDER BY {OrderBy(q)} LIMIT $take OFFSET $skip";
        cmd.Add("$take", Math.Clamp(q.Take, 1, 5000));
        cmd.Add("$skip", Math.Max(0, q.Skip));
        return (Read(cmd), total);
    }

    /// <summary>Condições do histórico (texto, situação, ação, máquina, datas e usuários). Valores sempre por parâmetro.</summary>
    private static void AddFilters(RequestQuery q, List<string> where, Action<string, object?> Param)
    {
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            where.Add("(username LIKE $txt OR hostname LIKE $txt OR title LIKE $txt OR package_id LIKE $txt OR message LIKE $txt)");
            Param("$txt", $"%{q.Text.Trim()}%");
        }
        // Mais de uma situação separada por vírgula ("failed,timeout"): os números de falha da visão geral somam as duas.
        var statuses = (q.Status ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(8).ToList();
        if (statuses.Count > 0)
        {
            var names = statuses.Select((_, i) => $"$st{i}").ToList();
            where.Add($"status IN ({string.Join(',', names)})");
            for (var i = 0; i < statuses.Count; i++) Param(names[i], statuses[i]);
        }
        if (!string.IsNullOrWhiteSpace(q.Action)) { where.Add("action = $ac"); Param("$ac", q.Action); }
        if (!string.IsNullOrWhiteSpace(q.Hostname)) { where.Add("hostname = $h"); Param("$h", q.Hostname); }
        if (q.FromUtc is { } from) { where.Add("created_utc >= $from"); Param("$from", Db.ToText(from)); }
        if (q.ToUtc is { } to) { where.Add("created_utc < $to"); Param("$to", Db.ToText(to)); }
        if (q.Usernames is { } users)
        {
            if (users.Count == 0) where.Add("1 = 0");
            else
            {
                var names = users.Take(900).Select((_, i) => $"$us{i}").ToList();
                where.Add($"username IN ({string.Join(',', names)})");
                for (var i = 0; i < names.Count; i++) Param(names[i], users[i]);
            }
        }
    }

    /// <summary>Pedidos (sem consultas) desde a data: base dos números da visão geral.</summary>
    public List<(DateTime CreatedUtc, string Status)> StatusesSince(DateTime sinceUtc)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT created_utc, status FROM requests WHERE action <> $list AND created_utc >= $since";
        cmd.Add("$list", RequestActions.List);
        cmd.Add("$since", Db.ToText(sinceUtc));
        var list = new List<(DateTime, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((Db.ParseDate(r.GetString(0)), r.GetString(1)));
        return list;
    }

    /// <summary>Data do último pedido de cada máquina.</summary>
    public Dictionary<string, DateTime> LastRequestPerHost()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hostname, MAX(created_utc) FROM requests WHERE action <> $list GROUP BY hostname";
        cmd.Add("$list", RequestActions.List);
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetString(0)] = Db.ParseDate(r.GetString(1));
        return result;
    }

    private static List<RequestRow> Read(SqliteCommand cmd)
    {
        var list = new List<RequestRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RequestRow(
                r.GetInt64(0),
                Db.ParseDate(r.GetString(1)),
                r.IsDBNull(2) ? null : Db.ParseDate(r.GetString(2)),
                r.GetString(3),
                r.GetString(4),
                r.GetNullableString(5),
                r.GetNullableInt(6),
                r.GetString(7),
                r.GetString(8),
                r.GetString(9),
                r.GetString(10),
                r.GetString(11),
                r.GetNullableString(12),
                r.GetNullableInt(13),
                r.GetNullableString(14),
                r.IsDBNull(15) ? null : r.GetInt64(15)));
        }
        return list;
    }
}
