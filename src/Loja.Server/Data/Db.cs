using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Loja.Server.Data;

public class Db
{
    private readonly string _connectionString;

    public Db(IOptions<LojaOptions> options, IWebHostEnvironment env)
    {
        var dir = options.Value.DataDir;
        if (!Path.IsPathRooted(dir)) dir = Path.Combine(env.ContentRootPath, dir);
        Directory.CreateDirectory(dir);
        DataDir = dir;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dir, "loja.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public string DataDir { get; }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS apps (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                package_id  TEXT NOT NULL COLLATE NOCASE,
                source      TEXT NOT NULL,
                title       TEXT NOT NULL,
                description TEXT,
                version     TEXT,
                icon_url    TEXT,
                enabled     INTEGER NOT NULL DEFAULT 1,
                auto_update INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                UNIQUE (package_id, source)
            );

            CREATE TABLE IF NOT EXISTS app_groups (
                app_id     INTEGER NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
                group_name TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (app_id, group_name)
            );

            CREATE TABLE IF NOT EXISTS requests (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc  TEXT NOT NULL,
                finished_utc TEXT,
                username     TEXT NOT NULL,
                hostname     TEXT NOT NULL COLLATE NOCASE,
                agent_id     TEXT,
                app_id       INTEGER,
                package_id   TEXT NOT NULL,
                title        TEXT NOT NULL,
                source       TEXT NOT NULL,
                action       TEXT NOT NULL,
                status       TEXT NOT NULL,
                message      TEXT,
                exit_code    INTEGER,
                output       TEXT,
                token_hash   TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_requests_host ON requests (hostname, status);
            CREATE INDEX IF NOT EXISTS ix_requests_user ON requests (username, id);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_requests_token ON requests (token_hash) WHERE token_hash IS NOT NULL;

            -- Pacotes do Chocolatey instalados em cada máquina (último retorno do script).
            CREATE TABLE IF NOT EXISTS host_inventory (
                hostname    TEXT PRIMARY KEY COLLATE NOCASE,
                updated_utc TEXT NOT NULL
            );

            -- Versões do app da loja publicadas pelo painel (autoupdate). Só uma é a "atual".
            CREATE TABLE IF NOT EXISTS client_releases (
                version       TEXT PRIMARY KEY,
                sha256        TEXT NOT NULL,
                size          INTEGER NOT NULL,
                mandatory     INTEGER NOT NULL DEFAULT 0,
                is_current    INTEGER NOT NULL DEFAULT 0,
                published_utc TEXT NOT NULL,
                published_by  TEXT NOT NULL
            );

            -- Versão do app que cada máquina usou por último.
            CREATE TABLE IF NOT EXISTS host_clients (
                hostname      TEXT PRIMARY KEY COLLATE NOCASE,
                version       TEXT NOT NULL,
                username      TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL
            );

            -- Parâmetros alterados pelo painel web (sobrepõem o appsettings).
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            -- Trilha de auditoria das alterações feitas pelo painel (quem, quando, o quê, antes e depois).
            CREATE TABLE IF NOT EXISTS audit_log (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                utc          TEXT NOT NULL,
                username     TEXT NOT NULL,
                kind         TEXT NOT NULL,
                entity_type  TEXT NOT NULL,
                entity_id    TEXT,
                entity_label TEXT,
                field        TEXT,
                old_value    TEXT,
                new_value    TEXT,
                batch        TEXT,
                undo         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_audit_entity ON audit_log (entity_type, entity_id, id);

            CREATE TABLE IF NOT EXISTS host_packages (
                hostname   TEXT NOT NULL COLLATE NOCASE,
                package_id TEXT NOT NULL COLLATE NOCASE,
                version    TEXT NOT NULL,
                PRIMARY KEY (hostname, package_id)
            );

            -- 1.6.0: programas do Windows (chaves Uninstall do registro) de cada máquina, só para conferir "já instalado fora da loja".
            CREATE TABLE IF NOT EXISTS host_programs (
                hostname TEXT NOT NULL COLLATE NOCASE,
                name     TEXT NOT NULL,
                version  TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (hostname, name, version)
            );

            -- 1.6.0: nomes que o programa de cada aplicativo tem no Windows (cadastro da TI; a comparação é por nome exato).
            CREATE TABLE IF NOT EXISTS app_aliases (
                app_id    INTEGER NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
                alias     TEXT NOT NULL,
                alias_key TEXT NOT NULL,
                PRIMARY KEY (app_id, alias_key)
            );

            -- Agente da loja (Loja.Agent): batimentos de cada estação e fila de trabalhos. Só é usado com Loja:Agent:Enabled.
            CREATE TABLE IF NOT EXISTS agents (
                hostname      TEXT PRIMARY KEY COLLATE NOCASE,
                agent_version TEXT NOT NULL,
                os_caption    TEXT,
                os_build      INTEGER NOT NULL DEFAULT 0,
                product_type  INTEGER NOT NULL DEFAULT 1,
                logged_user   TEXT,
                last_seen_utc TEXT NOT NULL,
                first_seen_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS agent_jobs (
                request_id       INTEGER PRIMARY KEY REFERENCES requests(id) ON DELETE CASCADE,
                hostname         TEXT NOT NULL COLLATE NOCASE,
                action           TEXT NOT NULL,
                package_id       TEXT,
                source           TEXT,
                timeout_seconds  INTEGER NOT NULL,
                queued_utc       TEXT NOT NULL,
                sent_utc         TEXT,
                cancel_requested INTEGER NOT NULL DEFAULT 0,
                attempts         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_agent_jobs_host ON agent_jobs (hostname, sent_utc);

            -- 1.8.0 identidade: usuários locais (entrada por senha, para o painel da TI), grupos locais e seus membros (por login).
            -- Só é usado quando o login local está ligado em Configurações; o modo AD não usa nada disto.
            CREATE TABLE IF NOT EXISTS local_users (
                username      TEXT PRIMARY KEY COLLATE NOCASE,
                display_name  TEXT,
                password_hash TEXT NOT NULL,
                is_admin      INTEGER NOT NULL DEFAULT 0,
                is_emergency  INTEGER NOT NULL DEFAULT 0,
                enabled       INTEGER NOT NULL DEFAULT 1,
                failed_count  INTEGER NOT NULL DEFAULT 0,
                locked_until  TEXT,
                created_utc   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS local_groups (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT NOT NULL UNIQUE COLLATE NOCASE,
                description TEXT
            );

            CREATE TABLE IF NOT EXISTS local_group_members (
                group_id INTEGER NOT NULL REFERENCES local_groups(id) ON DELETE CASCADE,
                login    TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (group_id, login)
            );
            """;
        cmd.ExecuteNonQuery();

        // Fase 2: colunas novas em bancos criados na Fase 1.
        AddColumnIfMissing(conn, "apps", "allow_uninstall", "INTEGER NOT NULL DEFAULT 1");

        // 1.6.0: o grupo passa a ser identificado pelo SID (o nome fica para exibição). Só acrescenta: a 1.5.0 ignora a coluna.
        AddColumnIfMissing(conn, "app_groups", "group_sid", "TEXT");
        using (var index = conn.CreateCommand())
        {
            index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ix_app_groups_sid ON app_groups (app_id, group_sid) WHERE group_sid IS NOT NULL";
            index.ExecuteNonQuery();
        }

        // 1.6.0: quando os programas do Windows foram coletados (vazio = o script ainda não os envia). A 1.5.0 ignora a coluna.
        AddColumnIfMissing(conn, "host_inventory", "programs_utc", "TEXT");

        // 1.6.0: pedido criado por "Reexecutar" guarda o número do pedido que repete. A 1.5.0 ignora a coluna.
        AddColumnIfMissing(conn, "requests", "retry_of", "INTEGER");
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    public static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    public static string ToText(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

    public static DateTime ParseDate(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

internal static class SqliteExtensions
{
    public static string? GetNullableString(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static int? GetNullableInt(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static void Add(this SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
