using Loja.Server;
using Loja.Server.Data;
using Loja.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

var lojaSection = builder.Configuration.GetSection(LojaOptions.Section);
builder.Services.Configure<LojaOptions>(lojaSection);
var loja = lojaSection.Get<LojaOptions>() ?? new LojaOptions();

if (!loja.Tactical.Mock && !(loja.Agent.Enabled && loja.Agent.Only) && (string.IsNullOrWhiteSpace(loja.Tactical.Url) || string.IsNullOrWhiteSpace(loja.Tactical.ApiKey) || loja.Tactical.ScriptId <= 0))
{
    throw new InvalidOperationException(
        "Configuração do Tactical incompleta: preencha Loja:Tactical:Url, ApiKey e ScriptId no appsettings.Production.json.");
}

// Quem entra: a cada requisição o esquema "LojaSmart" escolhe entre token do agente (Bearer), cookie de login local e login do Windows
// (Kerberos/NTLM). No IIS, o próprio IIS faz a autenticação do Windows e o handler só repassa. Sem nada de novo ligado nas
// Configurações, tudo cai no login do Windows, como sempre foi.
builder.Services.AddAuthentication(LojaAuth.Smart)
    .AddPolicyScheme(LojaAuth.Smart, "Loja", o => o.ForwardDefaultSelector = LojaAuth.Select)
    .AddNegotiate()
    .AddCookie(LojaAuth.Cookie, o =>
    {
        o.Cookie.Name = LojaAuth.CookieName;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.LoginPath = "/login";
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        // A cada requisição confere que o usuário ainda existe e está ativo (desligar ou apagar vale na hora) e atualiza o perfil de admin.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var directory = ctx.HttpContext.RequestServices.GetRequiredService<LocalDirectoryService>();
            var user = directory.GetUser(ctx.Principal?.Identity?.Name ?? "");
            if (user is null || !user.Enabled)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(LojaAuth.Cookie);
                return;
            }
            ctx.ReplacePrincipal(LojaAuth.LocalPrincipal(user));
        };
    })
    .AddScheme<AuthenticationSchemeOptions, UserTokenHandler>(LojaAuth.UserToken, _ => { });
builder.Services.AddTransient<IClaimsTransformation, LocalGroupClaims>();
builder.Services.AddSingleton<IAuthorizationHandler, AdminHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AdminHandler.Policy, p => p.RequireAuthenticatedUser().AddRequirements(new AdminRequirement()));

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<AppRepository>();
builder.Services.AddSingleton<RequestRepository>();
builder.Services.AddSingleton<InventoryRepository>();
builder.Services.AddSingleton<AuditRepository>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<GroupMembershipService>();
builder.Services.AddSingleton<GroupAdminService>();
builder.Services.AddSingleton<CatalogHealthService>();
builder.Services.AddSingleton<AccessService>();
builder.Services.AddSingleton<CommunityFeed>();
builder.Services.AddSingleton<InternalFeed>();
builder.Services.AddSingleton<IconCache>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<CompletionService>();
builder.Services.AddSingleton<InstallService>();
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<LocalDirectoryService>();
builder.Services.AddSingleton<UserTokenService>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<DirectoryService>();
builder.Services.AddSingleton<ClientRepository>();
builder.Services.AddSingleton<ClientReleaseService>();
// Envio do .exe do app pelo painel (~70 MB).
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = ClientReleaseService.MaxSize);
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddSingleton<AutoUpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoUpdateService>());

builder.Services.AddHttpClient(CommunityFeed.ClientName, c =>
{
    c.BaseAddress = new Uri(loja.CommunityFeedUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("LojaDeAplicativos/1.0");
});
builder.Services.AddHttpClient(IconCache.ClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("LojaDeAplicativos/1.0");
});

// Agente próprio da loja (Loja.Agent): as rotas sempre existem, mas respondem 404 enquanto Loja:Agent:Enabled for falso.
builder.Services.AddSingleton<AgentRepository>();
builder.Services.AddSingleton<AgentSignals>();
builder.Services.AddSingleton<LojaAgentClient>();

// "Só o agente" dispensa o Tactical por completo.
var useTactical = !(loja.Agent.Enabled && loja.Agent.Only);
if (useTactical && loja.Tactical.Mock)
{
    builder.Services.AddSingleton<FakeTacticalClient>();
}
else if (useTactical)
{
    builder.Services.AddHttpClient(TacticalClient.ClientName, c =>
    {
        c.BaseAddress = new Uri(loja.Tactical.Url.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(30);
        c.DefaultRequestHeaders.Add("X-API-KEY", loja.Tactical.ApiKey);
    });
    builder.Services.AddSingleton<TacticalClient>();
}

if (loja.Agent.Enabled)
{
    // Agente nas máquinas que o têm; Tactical nas demais (migração por ondas).
    builder.Services.AddSingleton<ITacticalClient>(sp => new HybridExecutor(
        !useTactical ? null : loja.Tactical.Mock ? sp.GetRequiredService<FakeTacticalClient>() : sp.GetRequiredService<TacticalClient>(),
        sp.GetRequiredService<LojaAgentClient>(),
        sp.GetRequiredService<ILogger<HybridExecutor>>()));
}
else if (loja.Tactical.Mock)
{
    builder.Services.AddSingleton<ITacticalClient>(sp => sp.GetRequiredService<FakeTacticalClient>());
}
else
{
    builder.Services.AddSingleton<ITacticalClient>(sp => sp.GetRequiredService<TacticalClient>());
}

var app = builder.Build();

app.Services.GetRequiredService<Db>().Init();
app.Services.GetRequiredService<SettingsService>().ApplyStored();
try
{
    app.Services.GetRequiredService<ClientReleaseService>().SyncBundled(loja.ClientBundlePath, loja.ClientUpdateMandatory);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Não foi possível publicar o app que veio com o servidor");
}
// Administrador de emergência (usuário local fora do AD): criado uma vez, se houver senha na configuração do servidor.
app.Services.GetRequiredService<LocalDirectoryService>().EnsureEmergencyAdmin(builder.Configuration["Loja:Identity:EmergencyAdminPassword"]);
app.Services.GetRequiredService<ConfigService>().Load();
try
{
    // 1.6.0: grava o SID dos grupos salvos só pelo nome. Se o AD não responder, o acesso segue pelo nome e a listagem do painel tenta de novo.
    var (sids, renamed, pending) = app.Services.GetRequiredService<CatalogService>().SyncGroups();
    if (pending > 0) app.Logger.LogWarning("{Count} grupo(s) de aplicativos ainda sem SID (não encontrados ou AD indisponível agora)", pending);
}
catch (Exception ex)
{
    app.Logger.LogWarning("Migração dos grupos para SID adiada: {Error}", ex.Message);
}
if (loja.Tactical.Mock) app.Logger.LogWarning("Tactical SIMULADO (Loja:Tactical:Mock = true). Nada será instalado de verdade.");

app.UseAuthentication();

// Proteção contra CSRF: o navegador manda o login do Windows sozinho, então um site malicioso poderia
// disparar ações do painel. Toda alteração de admin exige este cabeçalho, que outro site não consegue enviar.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/admin")
        && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
        && ctx.Request.Headers["X-Requested-With"] != "loja-admin")
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new Loja.Shared.ErrorDto("Pedido recusado: falta o cabeçalho do painel (proteção contra CSRF)."));
        return;
    }
    await next();
});

// Painel web: as páginas em /admin só são entregues a quem está no grupo de administração.
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/admin"), branch => branch.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
    {
        // Com o login do Windows desligado não há o que negociar: vai para a página de entrada.
        if (!ctx.RequestServices.GetRequiredService<ConfigService>().GetBool("identity.windows.enabled"))
            ctx.Response.Redirect("/login?return=" + Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString));
        else
            await ctx.ChallengeAsync();
        return;
    }
    var allowed = await ctx.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(ctx.User, AdminHandler.Policy);
    if (!allowed.Succeeded)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync("Acesso restrito à TI (grupo de administração da loja).");
        return;
    }
    await next();
}));
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // O painel muda a cada versão: o navegador sempre confere se há arquivo novo.
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

app.UseAuthorization();
app.MapLojaEndpoints();

// Página de entrada (login por usuário e senha locais).
app.MapGet("/login", (IWebHostEnvironment env) =>
{
    var file = env.WebRootFileProvider.GetFileInfo("login.html");
    return file.Exists ? Results.File(file.CreateReadStream(), "text/html; charset=utf-8") : Results.NotFound();
}).AllowAnonymous();

// A prévia do painel novo ficava em /admin/v2 (1.4.x). Quem guardou esse endereço cai no painel de verdade.
app.MapGet("/admin/v2/{**rest}", () => Results.Redirect("/admin/"));

app.Run();
