using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Loja.Server.Data;

namespace Loja.Server.Services;

public record LocalUserDto(string Username, string? DisplayName, bool IsAdmin, bool IsEmergency, bool Enabled, bool Locked, string CreatedUtc);

public record LocalGroupDto(int Id, string Name, string? Description, string Sid, List<string> Members);

public record LocalUserRow(string Username, string? DisplayName, string PasswordHash, bool IsAdmin, bool IsEmergency, bool Enabled, int FailedCount, DateTime? LockedUntilUtc);

/// <summary>
/// Diretório local da loja: usuários com senha (para a TI entrar no painel sem AD), grupos e membros por login.
/// O "login" de um membro é o nome do usuário no Windows (como o agente o informa) ou o usuário local; compara-se sem diferenciar
/// maiúsculas e sem o domínio. O grupo local tem o identificador "LOCAL-n", que ocupa o lugar do SID do AD nos cadastros.
/// </summary>
public partial class LocalDirectoryService(Db db, ILogger<LocalDirectoryService> log)
{
    public const string SidPrefix = "LOCAL-";
    public const string EmergencyUsername = "emergencia";
    private const int Iterations = 210_000;
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$")]
    private static partial Regex NameRegex();

    public static string SidOf(int groupId) => SidPrefix + groupId.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseSid(string? sid, out int id)
    {
        id = 0;
        return sid is not null && sid.StartsWith(SidPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(sid[SidPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    // ---------- Senhas ----------

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string? PasswordProblem(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < 10 ? "A senha deve ter pelo menos 10 caracteres." : password.Length > 200 ? "A senha é longa demais." : null;

    private static readonly string DummyHash = HashPassword("sem-usuario-" + Guid.NewGuid());

    /// <summary>Confere usuário e senha. Cinco erros seguidos travam a conta por 5 minutos. A mensagem nunca diz se o usuário existe.</summary>
    public (LocalUserRow? User, string? Error, bool Locked) Authenticate(string username, string password)
    {
        var user = GetUser(username);
        if (user is null)
        {
            VerifyPassword(password, DummyHash); // gasta o mesmo tempo
            return (null, "Usuário ou senha incorretos.", false);
        }
        if (user.LockedUntilUtc is { } until && until > DateTime.UtcNow)
            return (null, "Conta bloqueada por tentativas erradas. Tente de novo em alguns minutos.", true);

        if (!user.Enabled || !VerifyPassword(password, user.PasswordHash))
        {
            if (user.Enabled) RegisterFailure(user.Username, user.FailedCount + 1);
            return (null, "Usuário ou senha incorretos.", false);
        }

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE local_users SET failed_count = 0, locked_until = NULL WHERE username = $u";
        cmd.Add("$u", user.Username);
        cmd.ExecuteNonQuery();
        return (user, null, false);
    }

    private void RegisterFailure(string username, int failures)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var lockNow = failures >= MaxFailures;
        cmd.CommandText = "UPDATE local_users SET failed_count = $n, locked_until = $l WHERE username = $u";
        cmd.Add("$n", lockNow ? 0 : failures);
        cmd.Add("$l", lockNow ? Db.ToText(DateTime.UtcNow + LockFor) : null);
        cmd.Add("$u", username);
        cmd.ExecuteNonQuery();
        if (lockNow) log.LogWarning("Conta local {User} bloqueada por {Minutes} min após {Count} erros de senha", username, LockFor.TotalMinutes, MaxFailures);
    }

    // ---------- Usuários ----------

    public LocalUserRow? GetUser(string username)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, display_name, password_hash, is_admin, is_emergency, enabled, failed_count, locked_until FROM local_users WHERE username = $u";
        cmd.Add("$u", username?.Trim() ?? "");
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    private static LocalUserRow Read(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetString(0), r.GetNullableString(1), r.GetString(2), r.GetInt32(3) == 1, r.GetInt32(4) == 1, r.GetInt32(5) == 1, r.GetInt32(6),
            r.IsDBNull(7) ? null : Db.ParseDate(r.GetString(7)));

    public List<LocalUserDto> ListUsers()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, display_name, password_hash, is_admin, is_emergency, enabled, failed_count, locked_until, created_utc FROM local_users ORDER BY username";
        using var r = cmd.ExecuteReader();
        var list = new List<LocalUserDto>();
        while (r.Read())
        {
            var row = Read(r);
            list.Add(new(row.Username, row.DisplayName, row.IsAdmin, row.IsEmergency, row.Enabled, row.LockedUntilUtc > DateTime.UtcNow, r.GetString(8)));
        }
        return list;
    }

    public bool HasEnabledAdmin()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM local_users WHERE is_admin = 1 AND enabled = 1";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public string? CreateUser(string username, string? displayName, string password, bool isAdmin, bool emergency = false)
    {
        username = (username ?? "").Trim();
        if (!NameRegex().IsMatch(username)) return "Usuário: use de 2 a 64 letras, números, ponto, hífen ou sublinhado.";
        if (PasswordProblem(password) is { } bad) return bad;
        if (GetUser(username) is not null) return "Já existe um usuário com este nome.";

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO local_users (username, display_name, password_hash, is_admin, is_emergency, enabled, created_utc) VALUES ($u, $d, $h, $a, $e, 1, $now)";
        cmd.Add("$u", username);
        cmd.Add("$d", string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim()[..Math.Min(100, displayName.Trim().Length)]);
        cmd.Add("$h", HashPassword(password));
        cmd.Add("$a", isAdmin || emergency ? 1 : 0);
        cmd.Add("$e", emergency ? 1 : 0);
        cmd.Add("$now", Db.Now());
        cmd.ExecuteNonQuery();
        return null;
    }

    /// <summary>Muda nome de exibição, perfil de administrador, ligado/desligado e/ou senha. O administrador de emergência não perde o perfil nem é desligado.</summary>
    public string? UpdateUser(string username, string? displayName, bool? isAdmin, bool? enabled, string? newPassword)
    {
        var user = GetUser(username);
        if (user is null) return "Usuário não encontrado.";
        if (user.IsEmergency && (isAdmin == false || enabled == false)) return "O administrador de emergência não pode ser desligado nem perder o perfil de administrador.";
        if (newPassword is not null && PasswordProblem(newPassword) is { } bad) return bad;

        var finalAdmin = isAdmin ?? user.IsAdmin;
        var finalEnabled = enabled ?? user.Enabled;
        if (user.IsAdmin && user.Enabled && (!finalAdmin || !finalEnabled) && CountEnabledAdmins() <= 1)
            return "Este é o último administrador ativo: crie outro antes de desligar este.";

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_users SET
                display_name = COALESCE($d, display_name),
                is_admin = $a, enabled = $en,
                password_hash = COALESCE($h, password_hash),
                failed_count = 0, locked_until = NULL
            WHERE username = $u
            """;
        cmd.Add("$u", user.Username);
        cmd.Add("$d", displayName is null ? null : displayName.Trim());
        cmd.Add("$a", finalAdmin ? 1 : 0);
        cmd.Add("$en", finalEnabled ? 1 : 0);
        cmd.Add("$h", newPassword is null ? null : HashPassword(newPassword));
        cmd.ExecuteNonQuery();
        return null;
    }

    public string? DeleteUser(string username)
    {
        var user = GetUser(username);
        if (user is null) return "Usuário não encontrado.";
        if (user.IsEmergency) return "O administrador de emergência não pode ser apagado.";
        if (user.IsAdmin && user.Enabled && CountEnabledAdmins() <= 1) return "Este é o último administrador ativo.";
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_users WHERE username = $u";
        cmd.Add("$u", user.Username);
        cmd.ExecuteNonQuery();
        return null;
    }

    private int CountEnabledAdmins()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM local_users WHERE is_admin = 1 AND enabled = 1";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Garante o administrador de emergência (fora do AD, sempre ativo). Só cria se ainda não existe e se houver uma senha informada
    /// na configuração do servidor (Loja:Identity:EmergencyAdminPassword); depois de criado, a senha do arquivo não é mais usada.
    /// </summary>
    public void EnsureEmergencyAdmin(string? password)
    {
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM local_users WHERE is_emergency = 1";
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) return;
        }
        if (string.IsNullOrEmpty(password)) return;
        if (CreateUser(EmergencyUsername, "Administrador de emergência", password, true, emergency: true) is { } error)
            log.LogWarning("Administrador de emergência não criado: {Error}", error);
        else
            log.LogWarning("Administrador de emergência \"{User}\" criado. Troque a senha pelo painel e tire a senha do arquivo de configuração.", EmergencyUsername);
    }

    // ---------- Grupos ----------

    public List<LocalGroupDto> ListGroups(string? term = null)
    {
        using var conn = db.Open();
        var groups = new List<LocalGroupDto>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, description FROM local_groups WHERE ($t IS NULL OR name LIKE $like) ORDER BY name";
            var t = string.IsNullOrWhiteSpace(term) ? null : term.Trim();
            cmd.Add("$t", t);
            cmd.Add("$like", t is null ? null : "%" + t.Replace("%", "").Replace("_", "") + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read()) groups.Add(new(r.GetInt32(0), r.GetString(1), r.GetNullableString(2), SidOf(r.GetInt32(0)), new()));
        }
        foreach (var g in groups)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT login FROM local_group_members WHERE group_id = $g ORDER BY login";
            cmd.Add("$g", g.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read()) g.Members.Add(r.GetString(0));
        }
        return groups;
    }

    public LocalGroupDto? GetGroup(int id) => ListGroups().FirstOrDefault(g => g.Id == id);

    public LocalGroupDto? FindGroup(string name) =>
        ListGroups().FirstOrDefault(g => string.Equals(g.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    public (LocalGroupDto? Group, string? Error) CreateGroup(string name, string? description)
    {
        name = (name ?? "").Trim();
        if (!NameRegex().IsMatch(name)) return (null, "Grupo: use de 2 a 64 letras, números, ponto, hífen ou sublinhado.");
        if (FindGroup(name) is not null) return (null, "Já existe um grupo com este nome.");
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO local_groups (name, description) VALUES ($n, $d); SELECT last_insert_rowid();";
        cmd.Add("$n", name);
        cmd.Add("$d", string.IsNullOrWhiteSpace(description) ? null : description.Trim());
        var id = Convert.ToInt32(cmd.ExecuteScalar());
        return (GetGroup(id), null);
    }

    /// <summary>Troca descrição e/ou a lista inteira de membros (logins). Logins repetidos ou vazios são ignorados.</summary>
    public string? UpdateGroup(int id, string? description, IEnumerable<string>? members)
    {
        if (GetGroup(id) is null) return "Grupo não encontrado.";
        var logins = members?.Select(m => AccessService.ShortName(m)).Where(m => m.Length > 0 && m.Length <= 64 && m.All(c => !char.IsControl(c)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (description is not null)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE local_groups SET description = $d WHERE id = $g";
            cmd.Add("$d", description.Trim());
            cmd.Add("$g", id);
            cmd.ExecuteNonQuery();
        }
        if (logins is not null)
        {
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM local_group_members WHERE group_id = $g";
                del.Add("$g", id);
                del.ExecuteNonQuery();
            }
            foreach (var login in logins)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO local_group_members (group_id, login) VALUES ($g, $l)";
                ins.Add("$g", id);
                ins.Add("$l", login);
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return null;
    }

    public string? DeleteGroup(int id)
    {
        if (GetGroup(id) is null) return "Grupo não encontrado.";
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var sql in new[] { "DELETE FROM local_group_members WHERE group_id = $g", "DELETE FROM local_groups WHERE id = $g" })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Add("$g", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return null;
    }

    /// <summary>Identificadores (LOCAL-n) dos grupos locais em que este login é membro.</summary>
    public List<string> GroupSidsOf(string login)
    {
        login = AccessService.ShortName(login);
        if (login.Length == 0) return new();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT group_id FROM local_group_members WHERE login = $l";
        cmd.Add("$l", login);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(SidOf(r.GetInt32(0)));
        return list;
    }

    /// <summary>Grupos locais para o seletor do painel (mesmo formato dos grupos do AD; o identificador LOCAL-n ocupa o lugar do SID).</summary>
    public List<AdGroupDto> SearchGroups(string term) =>
        ListGroups(term).Select(g => new AdGroupDto(g.Name, g.Description, g.Sid, g.Members.Count)).ToList();
}
