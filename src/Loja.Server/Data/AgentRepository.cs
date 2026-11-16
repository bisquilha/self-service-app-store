using System.Globalization;
using Loja.Shared;
using Microsoft.Data.Sqlite;

namespace Loja.Server.Data;

public record AgentRow(
    string Hostname,
    string AgentVersion,
    string? OsCaption,
    int OsBuild,
    int ProductType,
    string? LoggedUser,
    DateTime LastSeenUtc);

public record AgentJob(long RequestId, string Hostname, string Action, string? PackageId, string? Source, int TimeoutSeconds);

/// <summary>Batimentos dos agentes e a fila de trabalhos (um por pedido de instalação).</summary>
public class AgentRepository(Db db)
{
    /// <summary>Quantas vezes um trabalho já entregue e "perdido" (agente reiniciou) volta para a fila.</summary>
    private const int MaxAttempts = 3;

    /// <summary>Um trabalho entregue que o agente não declara mais como em execução volta para a fila depois deste tempo.</summary>
    private static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(3);

    public void RecordHeartbeat(AgentHeartbeatDto h)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO agents (hostname, agent_version, os_caption, os_build, product_type, logged_user, last_seen_utc, first_seen_utc)
                VALUES ($h, $v, $os, $build, $pt, $u, $now, $now)
                ON CONFLICT (hostname) DO UPDATE SET
                    agent_version = $v, os_caption = $os, os_build = $build, product_type = $pt,
                    logged_user = $u, last_seen_utc = $now;
                """;
            cmd.Add("$h", h.Hostname);
            cmd.Add("$v", h.AgentVersion);
            cmd.Add("$os", h.OsCaption);
            cmd.Add("$build", h.OsBuild);
            cmd.Add("$pt", h.ProductType);
            cmd.Add("$u", h.LoggedUser);
            cmd.Add("$now", Db.Now());
            cmd.ExecuteNonQuery();
        }

        // Trabalho entregue há mais de 3 min que o agente não está executando: voltou a ficar disponível (até 3 tentativas).
        var running = (h.RunningJobs ?? []).Distinct().Take(50).ToArray();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            var notIn = running.Length == 0 ? "" : $"AND request_id NOT IN ({string.Join(',', running.Select((_, i) => "$r" + i))})";
            cmd.CommandText = $"""
                UPDATE agent_jobs SET sent_utc = NULL
                WHERE hostname = $h AND sent_utc IS NOT NULL AND sent_utc < $limit AND attempts < $max {notIn};
                """;
            cmd.Add("$h", h.Hostname);
            cmd.Add("$limit", Db.ToText(DateTime.UtcNow - LostAfter));
            cmd.Add("$max", MaxAttempts);
            for (var i = 0; i < running.Length; i++) cmd.Add("$r" + i, running[i]);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<AgentRow> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hostname, agent_version, os_caption, os_build, product_type, logged_user, last_seen_utc FROM agents";
        using var r = cmd.ExecuteReader();
        var list = new List<AgentRow>();
        while (r.Read())
        {
            list.Add(new AgentRow(r.GetString(0), r.GetString(1), r.GetNullableString(2), r.GetInt32(3), r.GetInt32(4),
                r.GetNullableString(5), Db.ParseDate(r.GetString(6))));
        }
        return list;
    }

    public void Enqueue(long requestId, string hostname, string action, string? packageId, string? source, int timeoutSeconds)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO agent_jobs (request_id, hostname, action, package_id, source, timeout_seconds, queued_utc, sent_utc, cancel_requested, attempts)
            VALUES ($id, $h, $a, $p, $s, $t, $now, NULL, 0, 0);
            """;
        cmd.Add("$id", requestId);
        cmd.Add("$h", hostname);
        cmd.Add("$a", action);
        cmd.Add("$p", packageId);
        cmd.Add("$s", source);
        cmd.Add("$t", timeoutSeconds);
        cmd.Add("$now", Db.Now());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Entrega o trabalho mais antigo ainda não enviado (de pedido que continua em andamento) e o marca como enviado.</summary>
    public AgentJob? TakeNext(string hostname)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT j.request_id, j.hostname, j.action, j.package_id, j.source, j.timeout_seconds
            FROM agent_jobs j JOIN requests r ON r.id = j.request_id
            WHERE j.hostname = $h AND j.sent_utc IS NULL AND r.status = $running AND j.cancel_requested = 0
            ORDER BY j.request_id LIMIT 1;
            """;
        cmd.Add("$h", hostname);
        cmd.Add("$running", RequestStatus.Running);

        AgentJob? job = null;
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read())
                job = new AgentJob(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetNullableString(3), r.GetNullableString(4), r.GetInt32(5));
        }
        if (job is null) { tx.Commit(); return null; }

        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE agent_jobs SET sent_utc = $now, attempts = attempts + 1 WHERE request_id = $id";
        upd.Add("$now", Db.Now());
        upd.Add("$id", job.RequestId);
        upd.ExecuteNonQuery();
        tx.Commit();
        return job;
    }

    /// <summary>Pedidos desta máquina que o servidor mandou cancelar e que ainda estão em andamento.</summary>
    public long[] CancelledJobs(string hostname)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT j.request_id FROM agent_jobs j JOIN requests r ON r.id = j.request_id
            WHERE j.hostname = $h AND j.cancel_requested = 1 AND r.status = $running;
            """;
        cmd.Add("$h", hostname);
        cmd.Add("$running", RequestStatus.Running);
        using var rd = cmd.ExecuteReader();
        var ids = new List<long>();
        while (rd.Read()) ids.Add(rd.GetInt64(0));
        return [.. ids];
    }

    /// <summary>Marca o pedido para cancelamento. O agente recebe o aviso no próximo batimento.</summary>
    public bool RequestCancel(long requestId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agent_jobs SET cancel_requested = 1 WHERE request_id = $id";
        cmd.Add("$id", requestId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Cancela um pedido do agente. "queued": ainda não tinha sido entregue (some da fila); "sent": o agente está executando e
    /// recebe o aviso no próximo batimento; "none": não é um pedido do agente (ou já terminou).
    /// </summary>
    public string Cancel(long requestId)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT sent_utc FROM agent_jobs WHERE request_id = $id";
        cmd.Add("$id", requestId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return "none";
        var sent = !r.IsDBNull(0);
        r.Close();

        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = sent ? "UPDATE agent_jobs SET cancel_requested = 1 WHERE request_id = $id" : "DELETE FROM agent_jobs WHERE request_id = $id";
        upd.Add("$id", requestId);
        upd.ExecuteNonQuery();
        tx.Commit();
        return sent ? "sent" : "queued";
    }

    public void RemoveJob(long requestId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_jobs WHERE request_id = $id";
        cmd.Add("$id", requestId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Apaga batimentos e trabalhos antigos (manutenção).</summary>
    public void Purge(DateTime olderThanUtc)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM agent_jobs WHERE queued_utc < $limit;
            DELETE FROM agents WHERE last_seen_utc < $limitAgents;
            """;
        cmd.Add("$limit", Db.ToText(olderThanUtc));
        cmd.Add("$limitAgents", Db.ToText(olderThanUtc.AddDays(-60)));
        cmd.ExecuteNonQuery();
    }
}
