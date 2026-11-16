using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Loja.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Loja.Server.Services;

public record LoginDto(string? Username, string? Password);

public record LocalUserCreateDto(string? Username, string? DisplayName, string? Password, bool IsAdmin);

public record LocalUserUpdateDto(string? DisplayName, bool? IsAdmin, bool? Enabled, string? Password);

public record LocalGroupCreateDto(string? Name, string? Description);

public record LocalGroupUpdateDto(string? Description, List<string>? Members);

public record UserTokenRequestDto(string? Hostname);

public record UserTokenDto(string Token, string User, DateTime ExpiresUtc);

/// <summary>Nomes dos esquemas de login e das marcas (claims) que a loja usa além das do Windows.</summary>
public static class LojaAuth
{
    /// <summary>Esquema padrão: escolhe, a cada requisição, entre token do agente, cookie de login local e login do Windows.</summary>
    public const string Smart = "LojaSmart";

    public const string Cookie = "LojaLocal";
    public const string UserToken = "LojaUserToken";
    public const string CookieName = "loja.sid";

    /// <summary>Perfil de administrador de quem entrou por senha local.</summary>
    public const string AdminClaim = "loja:admin";

    /// <summary>Grupo local (LOCAL-n) a que a pessoa pertence. Vale para qualquer forma de login.</summary>
    public const string GroupClaim = "loja:group";

    private const string GroupsLoaded = "loja:groups-loaded";

    /// <summary>Decide como autenticar esta requisição. Windows só é tentado se estiver ligado nas Configurações.</summary>
    public static string Select(HttpContext ctx)
    {
        if (ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return UserToken;
        // Os agentes se identificam sempre pela conta do computador (Windows), nunca por cookie.
        if (ctx.Request.Path.StartsWithSegments("/api/agent")) return Microsoft.AspNetCore.Authentication.Negotiate.NegotiateDefaults.AuthenticationScheme;
        if (ctx.Request.Cookies.ContainsKey(CookieName)) return Cookie;
        return ctx.RequestServices.GetRequiredService<ConfigService>().GetBool("identity.windows.enabled")
            ? Microsoft.AspNetCore.Authentication.Negotiate.NegotiateDefaults.AuthenticationScheme
            : Cookie;
    }

    public static ClaimsPrincipal LocalPrincipal(LocalUserRow user)
    {
        var identity = new ClaimsIdentity(Cookie, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Username));
        if (user.IsAdmin) identity.AddClaim(new Claim(AdminClaim, "1"));
        return new ClaimsPrincipal(identity);
    }
}

/// <summary>
/// Token curto que prova "o usuário X está logado no PC Y". Emitido só pelo servidor, a pedido do agente daquele PC
/// (que se identifica pela conta do computador); assinado com uma chave que fica só no servidor.
/// </summary>
public class UserTokenService(Db db)
{
    private const string KeyName = "secret.userTokenKey";
    private byte[]? _key;

    private byte[] Key()
    {
        if (_key is not null) return _key;
        using var conn = db.Open();
        using var read = conn.CreateCommand();
        read.CommandText = "SELECT value FROM settings WHERE key = $k";
        read.Add("$k", KeyName);
        if (read.ExecuteScalar() is string stored) return _key = Convert.FromBase64String(stored);

        var fresh = RandomNumberGenerator.GetBytes(32);
        using var write = conn.CreateCommand();
        write.CommandText = "INSERT OR IGNORE INTO settings (key, value) VALUES ($k, $v)";
        write.Add("$k", KeyName);
        write.Add("$v", Convert.ToBase64String(fresh));
        write.ExecuteNonQuery();
        // se outra thread gravou primeiro, vale a que ficou no banco
        using var again = conn.CreateCommand();
        again.CommandText = "SELECT value FROM settings WHERE key = $k";
        again.Add("$k", KeyName);
        return _key = Convert.FromBase64String((string)again.ExecuteScalar()!);
    }

    public (string Token, DateTime ExpiresUtc) Mint(string hostname, string user, TimeSpan ttl)
    {
        var expires = DateTime.UtcNow + ttl;
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new TokenPayload(hostname, user, new DateTimeOffset(expires).ToUnixTimeSeconds())));
        return ($"{payload}.{Sign(payload)}", expires);
    }

    public (string Hostname, string User)? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 1000) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(parts[0])), Encoding.ASCII.GetBytes(parts[1]))) return null;
        try
        {
            var p = JsonSerializer.Deserialize<TokenPayload>(FromBase64Url(parts[0]));
            if (p is null || DateTimeOffset.FromUnixTimeSeconds(p.Exp) < DateTimeOffset.UtcNow || p.User.Length == 0) return null;
            return (p.Host, p.User);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private string Sign(string payload) => Base64Url(HMACSHA256.HashData(Key(), Encoding.ASCII.GetBytes(payload)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private record TokenPayload(string Host, string User, long Exp);
}

/// <summary>Autentica "Authorization: Bearer ..." com o token do agente. Só vale com a ponte do agente ligada.</summary>
public class UserTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, UserTokenService tokens, ConfigService config)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!config.GetBool("identity.agentBridge.enabled")) return Task.FromResult(AuthenticateResult.Fail("Ponte do agente desligada."));
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(AuthenticateResult.NoResult());
        if (tokens.Validate(header["Bearer ".Length..].Trim()) is not { } valid) return Task.FromResult(AuthenticateResult.Fail("Token inválido ou vencido."));

        var identity = new ClaimsIdentity(LojaAuth.UserToken, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, valid.User));
        identity.AddClaim(new Claim("loja:host", valid.Hostname));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

/// <summary>
/// Acrescenta as marcas dos grupos locais a qualquer pessoa autenticada (Windows, senha local ou token do agente), conforme os logins
/// cadastrados como membros. Se a pessoa não está em nenhum grupo local, a identidade segue intacta (a identidade original não muda).
/// </summary>
public class LocalGroupClaims(LocalDirectoryService local) : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == "loja:groups-loaded")) return Task.FromResult(principal);

        var sids = local.GroupSidsOf(principal.Identity.Name ?? "");
        if (sids.Count == 0) return Task.FromResult(principal);

        var clone = principal.Clone();
        var extra = new ClaimsIdentity((string?)null, ClaimTypes.Name, ClaimTypes.Role);
        extra.AddClaim(new Claim("loja:groups-loaded", "1"));
        foreach (var sid in sids) extra.AddClaim(new Claim(LojaAuth.GroupClaim, sid.ToUpperInvariant()));
        clone.AddIdentity(extra);
        return Task.FromResult(clone);
    }
}
