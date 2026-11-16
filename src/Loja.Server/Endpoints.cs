using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Loja.Server.Data;
using Loja.Server.Services;
using Loja.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Loja.Server;

public static class Endpoints
{
    /// <summary>
    /// Rotas do agente da loja (Loja.Agent). O agente se identifica com o login do próprio computador (conta PC$):
    /// o nome do computador que ele informa tem de ser o da conta que autenticou. Só existem com Loja:Agent:Enabled.
    /// </summary>
    private static void MapAgentEndpoints(WebApplication app)
    {
        var agentApi = app.MapGroup("/api/agent").RequireAuthorization();

        static string? Cut(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
        static bool ValidHost(string? h) => !string.IsNullOrEmpty(h) && System.Text.RegularExpressions.Regex.IsMatch(h, "^[A-Za-z0-9-]{1,63}$");
        static bool Allowed(ClaimsPrincipal user, string? hostname, LojaOptions o, IWebHostEnvironment env) =>
            ValidHost(hostname) && AgentIdentity.Matches(user, hostname!, o.Agent.TrustUserIdentityInDevelopment, env.IsDevelopment());

        // Política do agente (Configurações do painel). O agente só pede quando o número de versão do batimento mudou.
        agentApi.MapGet("/policy", (string hostname, ClaimsPrincipal user, ConfigService config, IOptions<LojaOptions> options, IWebHostEnvironment env) =>
        {
            var o = options.Value;
            if (!o.Agent.Enabled) return Results.NotFound();
            if (!Allowed(user, hostname, o, env)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(config.Policy());
        });

        // Ponte do agente (sem AD): o agente pede um token curto "o usuário X está logado neste PC", para o app da loja se identificar.
        agentApi.MapPost("/user-token", (UserTokenRequestDto dto, ClaimsPrincipal user, AgentRepository agents, UserTokenService tokens, ConfigService config,
            IOptions<LojaOptions> options, IWebHostEnvironment env) =>
        {
            var o = options.Value;
            if (!o.Agent.Enabled || !config.GetBool("identity.agentBridge.enabled")) return Results.NotFound();
            if (!Allowed(user, dto.Hostname, o, env)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var row = agents.List().FirstOrDefault(a => string.Equals(a.Hostname, dto.Hostname, StringComparison.OrdinalIgnoreCase));
            var login = AccessService.ShortName(row?.LoggedUser);
            if (row is null || login.Length == 0) return Error(409, "Ninguém está logado neste computador (segundo o último batimento do agente).");

            var (token, expires) = tokens.Mint(row.Hostname, login, TimeSpan.FromMinutes(15));
            return Results.Ok(new UserTokenDto(token, login, expires));
        });

        agentApi.MapPost("/heartbeat", (AgentHeartbeatDto dto, ClaimsPrincipal user, AgentRepository agents, ConfigService config, IOptions<LojaOptions> options, IWebHostEnvironment env) =>
        {
            var o = options.Value;
            if (!o.Agent.Enabled) return Results.NotFound();
            if (!Allowed(user, dto.Hostname, o, env)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var clean = dto with
            {
                AgentVersion = Cut(dto.AgentVersion, 20) ?? "",
                OsCaption = Cut(dto.OsCaption, 100),
                LoggedUser = Cut(dto.LoggedUser, 100),
                ProductType = dto.ProductType is >= 1 and <= 3 ? dto.ProductType : 3,
            };
            agents.RecordHeartbeat(clean);
            return Results.Ok(new AgentHeartbeatResponse(Math.Clamp(o.Agent.PollSeconds, 15, 600), agents.CancelledJobs(clean.Hostname), config.Version));
        });

        // Próximo trabalho desta máquina. Sem trabalho, espera até "wait" segundos (máx. 30) por um aviso e responde 204 se nada chegou.
        agentApi.MapGet("/jobs/next", async (string hostname, int? wait, ClaimsPrincipal user, AgentRepository agents, AgentSignals signals,
            IOptions<LojaOptions> options, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var o = options.Value;
            if (!o.Agent.Enabled) return Results.NotFound();
            if (!Allowed(user, hostname, o, env)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var job = agents.TakeNext(hostname);
            if (job is null && wait is > 0)
            {
                try { await signals.WaitAsync(hostname, TimeSpan.FromSeconds(Math.Min(wait.Value, 30)), ct); }
                catch (OperationCanceledException) { return Results.NoContent(); }
                job = agents.TakeNext(hostname);
            }
            return job is null ? Results.NoContent() : Results.Ok(new AgentJobDto(job.RequestId, job.Action, job.PackageId, job.Source, job.TimeoutSeconds));
        });

        agentApi.MapPost("/jobs/{id:long}/result", (long id, AgentResultDto dto, ClaimsPrincipal user, CompletionService completion, AgentRepository agents,
            IOptions<LojaOptions> options, IWebHostEnvironment env) =>
        {
            var o = options.Value;
            if (!o.Agent.Enabled) return Results.NotFound();
            if (!Allowed(user, dto.Hostname, o, env)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var windows = dto.InstalledWindows is { Length: > 1_000_000 } ? dto.InstalledWindows[..1_000_000] : dto.InstalledWindows;
            var ok = completion.CompleteByRequest(id, dto.Hostname, dto.ExitCode, dto.Output, dto.Installed, windows);
            if (ok) agents.RemoveJob(id);
            return ok ? Results.Ok() : Results.NotFound();
        });
    }

    public static void MapLojaEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Text("Loja de Aplicativos - API no ar.")).AllowAnonymous();

        // Chamado pelo script do Tactical (roda como SYSTEM). Autenticado pelo token de uso único.
        app.MapPost("/api/callback", (CallbackDto dto, CompletionService completion) =>
                completion.Complete(dto.Token, dto.ExitCode, dto.Output, dto.Installed, dto.InstalledWindows) ? Results.Ok() : Results.NotFound())
            .AllowAnonymous();

        // Login por usuário e senha locais (cookie). Exige o mesmo cabeçalho do painel: outro site não consegue enviá-lo (CSRF).
        app.MapPost("/api/auth/login", async (LoginDto dto, HttpContext ctx, LocalDirectoryService local, ConfigService config, AuditService audit) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "loja-admin") return Error(403, "Pedido recusado: falta o cabeçalho do painel.");
            var (user, error, locked) = local.Authenticate(dto.Username ?? "", dto.Password ?? "");
            // O administrador de emergência entra sempre; os demais só com o login local ligado nas Configurações.
            if (user is not null && !user.IsEmergency && !config.GetBool("identity.local.enabled")) (user, error, locked) = (null, "Usuário ou senha incorretos.", false);
            if (user is null) return Error(locked ? 429 : 401, error ?? "Usuário ou senha incorretos.");

            await ctx.SignInAsync(LojaAuth.Cookie, LojaAuth.LocalPrincipal(user));
            audit.Action(user.Username, "Login local", "Entrada", user.IsEmergency ? "Administrador de emergência" : user.IsAdmin ? "Administrador" : "Usuário", user.Username);
            return Results.Ok(new { user = user.Username, isAdmin = user.IsAdmin });
        }).AllowAnonymous();

        app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "loja-admin") return Error(403, "Pedido recusado: falta o cabeçalho do painel.");
            await ctx.SignOutAsync(LojaAuth.Cookie);
            return Results.NoContent();
        }).AllowAnonymous();

        MapAgentEndpoints(app);

        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/me", async (string? hostname, HttpRequest request, ClaimsPrincipal user, AccessService access, InstallService install,
            ClientRepository clients, CancellationToken ct) =>
        {
            var name = access.UserName(user);
            var check = await install.CheckMachineAsync(name, hostname ?? "", ct);

            // O app informa a própria versão: o painel mostra quem já atualizou.
            var appVersion = request.Headers["X-Loja-App-Version"].ToString();
            if (check.Agent is not null && appVersion.Length is > 0 and <= 20 && appVersion.All(c => char.IsDigit(c) || c == '.'))
                clients.RecordHost(check.Agent.Hostname, appVersion, name);

            return new MeDto(name, access.IsAdmin(user), hostname ?? "", check.Ok, check.Message);
        });

        // Autoupdate do app: qual é a versão atual e o download dela (o app confere o SHA-256).
        api.MapGet("/client/latest", (ClientReleaseService releases) =>
            releases.Latest() is { } latest ? Results.Ok(latest) : Results.NoContent());

        api.MapGet("/client/download/{version}", (string version, ClientReleaseService releases) =>
            releases.PathOf(version) is { } path
                ? Results.File(path, "application/octet-stream", $"LojaDeAplicativos-{version}.exe")
                : Results.NotFound());

        // Catálogo do usuário, já com o que está instalado na máquina informada.
        api.MapGet("/catalog", (string? hostname, ClaimsPrincipal user, AccessService access, AppRepository apps, IconCache icons,
            InventoryRepository inventory, RequestRepository requests, ConfigService config) =>
        {
            var host = (hostname ?? "").Trim();
            var inv = host.Length > 0 ? inventory.Get(host) : new HostInventory(null, new());
            var aliases = apps.ListAliases();
            var items = apps.List(onlyEnabled: true)
                .Where(a => config.IsSourceEnabled(a.Source) && access.CanUse(user, a))
                .Select(a => CatalogItems.Build(a, inv, CatalogItems.KeysOf(aliases, a.Id), icons.Has(a.Id)))
                .ToList();
            return new CatalogDto(items, inv.UpdatedUtc, host.Length > 0 && requests.HasRunningList(host), inv.ProgramsUtc);
        });

        // Pede ao computador a lista de programas instalados (só se a última for antiga, ou force=true).
        api.MapPost("/inventory/refresh", async (string? hostname, bool? force, ClaimsPrincipal user, InstallService install, CancellationToken ct) =>
        {
            var (started, error) = await install.RequestInventoryAsync(user, hostname ?? "", force == true, ct);
            return error is null ? Results.Ok(new { started }) : Error(409, error);
        });

        api.MapGet("/icons/{id:int}", (int id, IconCache icons) =>
            icons.TryGet(id, out var path, out var type) ? Results.File(path, type) : Results.NotFound());

        // Instalar, atualizar ou desinstalar (Action: install | upgrade | uninstall).
        api.MapPost("/install", async (InstallRequestDto dto, ClaimsPrincipal user, InstallService install, CancellationToken ct) =>
        {
            var result = await install.RequestAsync(user, dto.AppId, dto.Hostname, dto.Action ?? RequestActions.Install, ct);
            return result.Error is null ? Results.Ok(result.Request!.ToDto(includeOutput: false)) : Error(result.StatusCode, result.Error);
        });

        api.MapGet("/requests", (string? hostname, ClaimsPrincipal user, AccessService access, RequestRepository requests) =>
            requests.ListByUser(access.UserName(user), hostname, 30).Select(r => r.ToDto(includeOutput: false)));

        api.MapGet("/requests/{id:long}", (long id, ClaimsPrincipal user, AccessService access, RequestRepository requests) =>
        {
            var row = requests.Get(id);
            var isAdmin = access.IsAdmin(user);
            if (row is null || (!isAdmin && !string.Equals(row.Username, access.UserName(user), StringComparison.OrdinalIgnoreCase)))
                return Results.NotFound();
            return Results.Ok(row.ToDto(includeOutput: isAdmin));
        });

        var admin = api.MapGroup("/admin").RequireAuthorization(AdminHandler.Policy);

        admin.MapGet("/apps", (AppRepository apps, AccessService access, CatalogService catalog) =>
        {
            // A cada listagem confere o AD: grava o SID de grupos antigos e atualiza o nome dos renomeados (rápido: tem cache).
            var rows = apps.List(onlyEnabled: false);
            var (saved, renamed, _) = catalog.SyncGroups(rows);
            if (saved + renamed > 0) rows = apps.List(onlyEnabled: false);
            var aliases = apps.ListAliases();
            return rows.Select(a => a.ToAdminDto(access.MissingGroups(a.GroupRefs), aliases.GetValueOrDefault(a.Id) ?? new())).ToList();
        });

        admin.MapPost("/apps", async (AdminAppCreateDto dto, CatalogService catalog, AuditService audit, ClaimsPrincipal user, ILogger<CatalogService> log, CancellationToken ct) =>
        {
            var (created, error) = await catalog.AddAsync(dto, ct);
            if (created is null) return Error(400, error!);
            audit.AppCreated(user.Identity?.Name ?? "?", created);
            log.LogInformation("{User} adicionou {Package} ({Source}) para {Groups}", user.Identity?.Name, created.PackageId, created.Source, string.Join(", ", created.Groups));
            return Results.Ok(created.ToAdminDto());
        });

        admin.MapPut("/apps/{id:int}", (int id, AdminAppUpdateDto dto, AppRepository apps, AccessService access, CatalogService catalog, AuditService audit, ClaimsPrincipal user, ILogger<CatalogService> log) =>
        {
            var current = apps.Get(id);
            if (current is null) return Results.NotFound();

            // Só os grupos ACRESCENTADOS precisam existir. Um grupo que já estava salvo e foi renomeado no AD
            // continua passando, senão nem dava para ligar ou desligar um switch do app até corrigir os grupos.
            var (groups, groupsError) = catalog.ResolveGroups(dto.Groups, dto.GroupRefs, current.GroupRefs);
            if (groupsError is not null) return Error(400, groupsError);

            if (!apps.UpdateSettings(id, groups, dto.Enabled, dto.AutoUpdate, dto.AllowUninstall)) return Results.NotFound();
            var updated = apps.Get(id)!;
            audit.AppUpdated(user.Identity?.Name ?? "?", current, updated, undo: dto.Reason == "undo");
            log.LogInformation("{User} alterou {Package}: ativo={Enabled}, auto={Auto}, desinstalar={Uninstall}, grupos={Groups}",
                user.Identity?.Name, updated.PackageId, updated.Enabled, updated.AutoUpdate, updated.AllowUninstall, string.Join(", ", updated.Groups));
            return Results.Ok(updated.ToAdminDto(access.MissingGroups(updated.GroupRefs)));
        });

        admin.MapDelete("/apps/{id:int}", (int id, AppRepository apps, IconCache icons, AuditService audit, ClaimsPrincipal user, ILogger<CatalogService> log) =>
        {
            var existing = apps.Get(id);
            if (existing is null || !apps.Delete(id)) return Results.NotFound();
            icons.Delete(id);
            audit.AppDeleted(user.Identity?.Name ?? "?", existing);
            log.LogInformation("{User} removeu {Package} da loja", user.Identity?.Name, existing.PackageId);
            return Results.NoContent();
        });

        admin.MapPost("/apps/{id:int}/refresh", async (int id, AppRepository apps, CatalogService catalog, AccessService access, CancellationToken ct) =>
        {
            var existing = apps.Get(id);
            if (existing is null) return Results.NotFound();
            if (!await catalog.RefreshAsync(existing, ct)) return Error(404, "Pacote não encontrado no repositório.");
            var refreshed = apps.Get(id)!;
            return Results.Ok(refreshed.ToAdminDto(access.MissingGroups(refreshed.GroupRefs)));
        });

        admin.MapGet("/search", async (string? q, string? source, CatalogService catalog, ConfigService config, CancellationToken ct) =>
        {
            source ??= PackageSources.Community;
            if (!PackageSources.IsValid(source)) return Error(400, "Origem inválida.");
            if (!config.IsSourceEnabled(source)) return Error(409, config.SourceOffMessage(source));
            try
            {
                return Results.Ok(await catalog.SearchAsync(q?.Trim() ?? "", source, ct));
            }
            catch (HttpRequestException ex)
            {
                return Error(502, $"Não foi possível consultar o repositório: {ex.Message}");
            }
        });

        admin.MapGet("/requests", (int? take, RequestRepository requests) =>
            requests.ListAll(Math.Clamp(take ?? 300, 1, 2000)).Select(r => r.ToDto(includeOutput: true)));

        // Reexecutar um pedido que falhou (ou ficou sem retorno). Vira um pedido NOVO do painel, ligado ao original, e tudo é
        // conferido de novo agora: o aplicativo ainda existe (e está ativo, se for instalar/atualizar), a desinstalação continua
        // liberada e o computador passa nas travas (estação, online, nome único).
        admin.MapPost("/requests/{id:long}/retry", async (long id, ClaimsPrincipal user, RequestRepository requests, AppRepository apps, InstallService install, AuditService audit, CancellationToken ct) =>
        {
            var original = requests.Get(id);
            if (original is null) return Results.NotFound();
            if (original.Status is not (RequestStatus.Failed or RequestStatus.Timeout))
                return Error(409, "Só dá para reexecutar um pedido que falhou ou ficou sem retorno.");
            if (!RequestActions.IsUserAction(original.Action))
                return Error(409, "Este tipo de pedido não pode ser reexecutado.");
            var app = original.AppId is { } appId ? apps.Get(appId) : null;
            if (app is null) return Error(409, "O aplicativo deste pedido não está mais na loja.");
            if (original.Action != RequestActions.Uninstall && !app.Enabled)
                return Error(409, "O aplicativo está desativado na loja. Ative-o antes de reexecutar.");
            if (original.Action == RequestActions.Uninstall && !app.AllowUninstall)
                return Error(409, "A desinstalação deste aplicativo não está liberada pela loja hoje.");

            var result = await install.RequestAsAdminAsync(user, app.Id, original.Hostname, original.Action, ct, retryOf: id);
            if (result.Error is not null) return Error(result.StatusCode, result.Error);
            audit.Action(user.Identity?.Name ?? "?", app.Title, "Reexecutar pedido",
                $"Pedido {id} ({original.Action} em {original.Hostname}) → pedido {result.Request!.Id}", app.Id.ToString());
            return Results.Ok(result.Request!.ToDto(includeOutput: true));
        });

        // Cancela uma instalação em andamento (só pedidos executados pelo agente da loja; os do Tactical não têm como ser cancelados daqui).
        admin.MapPost("/requests/{id:long}/cancel", (long id, RequestRepository requests, AgentRepository agents) =>
        {
            var row = requests.Get(id);
            if (row is null) return Results.NotFound();
            if (row.Status != RequestStatus.Running) return Error(409, "Este pedido já terminou.");

            switch (agents.Cancel(id))
            {
                case "queued":
                    requests.Finish(id, RequestStatus.Failed, "Cancelado antes de começar.", AgentExitCodes.Cancelled, null);
                    return Results.Ok(new { cancelled = true, message = "Cancelado antes de começar." });
                case "sent":
                    return Results.Ok(new { cancelled = true, message = "O agente vai encerrar a instalação no próximo batimento." });
                default:
                    return Error(409, "Este pedido não foi executado pelo agente da loja; só pelo Tactical não há como cancelar daqui.");
            }
        });

        admin.MapGet("/status", async (StatusService status, CancellationToken ct) => await status.GetAsync(ct));

        // Saúde do catálogo: ativo sem grupo, grupo que não existe mais no AD (diferente de "o AD não respondeu"), grupo sem membros
        // diretos e aplicativos desatualizados em muitas máquinas. Só lê.
        admin.MapGet("/catalog-health", (CatalogHealthService health) => Results.Ok(health.Check()));

        // ---------- Painel web (/admin) ----------

        admin.MapGet("/info", (ClaimsPrincipal user, IOptions<LojaOptions> options) =>
        {
            var o = options.Value;
            var version = typeof(Endpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
            return new
            {
                user = user.Identity?.Name,
                serverVersion = version,
                adminGroups = o.AdminGroups,
                tacticalUrl = o.Tactical.Mock ? "(simulado)" : o.Tactical.Url,
                tacticalScriptId = o.Tactical.ScriptId,
                internalFeed = o.InternalFeedPath,
                requireLoggedUser = o.RequireLoggedUser,
                blockServers = o.BlockServerOperatingSystems,
            };
        });

        admin.MapGet("/overview", (AdminService panel) => panel.Overview());

        admin.MapGet("/apps/stats", (AdminService panel) => panel.AppStats());

        // Atualizações disponíveis: por aplicativo, as máquinas com versão antiga e se cada uma está ao alcance agora.
        admin.MapGet("/updates", async (AdminService panel, CancellationToken ct) => Results.Ok(await panel.UpdatesAsync(ct)));

        // Nomes de programa do Windows (já coletados nas máquinas) que contêm o texto: ajuda a TI a achar o nome exato de um aplicativo.
        admin.MapGet("/programs", (string? q, InventoryRepository inventory) =>
        {
            var text = (q ?? "").Trim();
            if (text.Length < 2) return Results.Ok(new List<ProgramNameDto>());
            return Results.Ok(inventory.SearchPrograms(text, 30).Select(p => new ProgramNameDto(p.Name, p.Machines)).ToList());
        });

        // Nomes que o programa do aplicativo tem no Windows. É o que liga "já instalado fora da loja" ao aplicativo (nome exato).
        admin.MapPut("/apps/{id:int}/aliases", (int id, AliasesDto dto, AppRepository apps, AccessService access, AuditService audit, ClaimsPrincipal user) =>
        {
            var app = apps.Get(id);
            if (app is null) return Results.NotFound();
            var (aliases, error) = ProgramAliases.Clean(dto.Aliases);
            if (error is not null) return Error(400, error);
            var before = apps.GetAliases(id);
            if (!apps.SetAliases(id, aliases)) return Results.NotFound();
            var after = apps.GetAliases(id);
            audit.AliasesChanged(user.Identity?.Name ?? "?", app, before, after, undo: dto.Reason == "undo");
            return Results.Ok(app.ToAdminDto(access.MissingGroups(app.GroupRefs), after));
        });

        admin.MapPost("/apps/{id:int}/upgrade-all", async (int id, ClaimsPrincipal user, AdminService panel, AppRepository apps, AuditService audit, CancellationToken ct) =>
        {
            if (await panel.UpgradeEverywhereAsync(user, id, ct) is not { } result) return Results.NotFound();
            audit.Action(user.Identity?.Name ?? "?", apps.Get(id)?.Title ?? $"Aplicativo {id}", "Atualizar em todas as máquinas",
                $"{result.Started} {(result.Started == 1 ? "enviada" : "enviadas")}, {result.Skipped} {(result.Skipped == 1 ? "pulada" : "puladas")}", id.ToString());
            return Results.Ok(result);
        });

        admin.MapGet("/machines", async (string? group, AdminService panel, GroupMembershipService membership, CancellationToken ct) =>
        {
            try
            {
                var machines = await panel.MachinesAsync(ct);
                if (string.IsNullOrWhiteSpace(group)) return Results.Ok(machines);
                if (!IsGroupFilter(group)) return Error(400, "Grupo inválido.");

                // "Grupo do usuário": quem está com sessão aberta na estação (segundo o Tactical) pertence ao grupo?
                var logins = machines.Select(m => GroupMembershipService.LoginOf(m.LoggedUser)).Where(l => l is not null).Select(l => l!).ToList();
                var members = membership.Members(group.Trim(), logins);
                return Results.Ok(machines.Where(m => GroupMembershipService.LoginOf(m.LoggedUser) is { } login && members.Contains(login)).ToList());
            }
            catch (HttpRequestException ex)
            {
                return Error(502, $"Não foi possível consultar o Tactical RMM: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                return Error(502, ex.Message);
            }
        });

        // Verificar programas em várias máquinas de uma vez (só consulta). Até 100 por pedido.
        admin.MapPost("/machines/scan", async (ScanMachinesDto dto, ClaimsPrincipal user, AdminService panel, AuditService audit, CancellationToken ct) =>
        {
            var hosts = (dto.Hostnames ?? new()).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (hosts.Count == 0) return Error(400, "Escolha ao menos uma máquina.");
            if (hosts.Count > AdminService.MaxScan) return Error(400, $"No máximo {AdminService.MaxScan} máquinas por vez.");
            var result = await panel.ScanAsync(user, hosts, ct);
            audit.Action(user.Identity?.Name ?? "?", "Verificar programas", "Verificação pedida a várias máquinas",
                $"{hosts.Count} {(hosts.Count == 1 ? "máquina" : "máquinas")}: {result.Started} {(result.Started == 1 ? "enviada" : "enviadas")}, {result.Skipped} {(result.Skipped == 1 ? "pulada" : "puladas")}");
            return Results.Ok(result);
        });

        admin.MapGet("/machines/{hostname}", async (string hostname, AdminService panel, CancellationToken ct) =>
            await panel.MachineAsync(hostname, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());

        // Ação remota do admin numa máquina: list (verificar programas), install, upgrade ou uninstall.
        admin.MapPost("/machines/{hostname}/action", async (string hostname, AdminMachineActionDto dto, ClaimsPrincipal user, InstallService install, CancellationToken ct) =>
        {
            var result = await install.RequestAsAdminAsync(user, dto.AppId, hostname, dto.Action, ct);
            return result.Error is null ? Results.Ok(result.Request!.ToDto(includeOutput: true)) : Error(result.StatusCode, result.Error);
        });

        // Sugestões de usuários do AD enquanto o admin digita (nome, sobrenome, login ou e-mail).
        admin.MapGet("/users", (string? q, DirectoryService directory) =>
        {
            try
            {
                return Results.Ok(directory.SearchUsers(q ?? ""));
            }
            catch (InvalidOperationException ex)
            {
                return Error(502, ex.Message);
            }
        });

        // Seletor de grupos do painel: por padrão só os LOJA-*; all=true procura em todo o domínio (mínimo 2 letras).
        admin.MapGet("/groups", (string? q, bool? all, DirectoryService directory, LocalDirectoryService local, ConfigService config) =>
        {
            var found = new List<AdGroupDto>();
            if (config.GetBool("identity.windows.enabled"))
            {
                try
                {
                    found.AddRange(directory.SearchGroups(q ?? "", all ?? false));
                }
                catch (InvalidOperationException ex)
                {
                    return Error(502, ex.Message);
                }
            }
            // Grupos locais (identificador LOCAL-n no lugar do SID) aparecem junto, nos mesmos seletores.
            found.AddRange(local.SearchGroups(q ?? ""));
            return Results.Ok(found.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        });

        // ---------- Identidade local: usuários (entrada por senha) e grupos (membros por login) ----------

        admin.MapGet("/identity/users", (LocalDirectoryService local) => Results.Ok(local.ListUsers()));

        admin.MapPost("/identity/users", (LocalUserCreateDto dto, LocalDirectoryService local, AuditService audit, ClaimsPrincipal user) =>
        {
            if (local.CreateUser(dto.Username ?? "", dto.DisplayName, dto.Password ?? "", dto.IsAdmin) is { } error) return Error(400, error);
            audit.Action(user.Identity?.Name ?? "?", "Usuário local", "Criado", dto.IsAdmin ? "Administrador" : "Usuário", dto.Username!.Trim());
            return Results.Ok(local.ListUsers());
        });

        admin.MapPut("/identity/users/{username}", (string username, LocalUserUpdateDto dto, LocalDirectoryService local, AuditService audit, ClaimsPrincipal user) =>
        {
            if (local.UpdateUser(username, dto.DisplayName, dto.IsAdmin, dto.Enabled, dto.Password) is { } error) return Error(error.StartsWith("Usuário não") ? 404 : 400, error);
            var what = new List<string>();
            if (dto.IsAdmin is { } a) what.Add(a ? "virou administrador" : "deixou de ser administrador");
            if (dto.Enabled is { } e) what.Add(e ? "ligado" : "desligado");
            if (dto.Password is not null) what.Add("senha trocada");
            if (dto.DisplayName is not null) what.Add("nome alterado");
            audit.Action(user.Identity?.Name ?? "?", "Usuário local", "Alterado", string.Join(", ", what), username);
            return Results.Ok(local.ListUsers());
        });

        admin.MapDelete("/identity/users/{username}", (string username, LocalDirectoryService local, AuditService audit, ClaimsPrincipal user) =>
        {
            if (local.DeleteUser(username) is { } error) return Error(error.StartsWith("Usuário não") ? 404 : 400, error);
            audit.Action(user.Identity?.Name ?? "?", "Usuário local", "Apagado", "Removido", username);
            return Results.Ok(local.ListUsers());
        });

        admin.MapGet("/identity/groups", (LocalDirectoryService local) => Results.Ok(local.ListGroups()));

        admin.MapPost("/identity/groups", (LocalGroupCreateDto dto, LocalDirectoryService local, AuditService audit, ClaimsPrincipal user) =>
        {
            var (group, error) = local.CreateGroup(dto.Name ?? "", dto.Description);
            if (error is not null) return Error(400, error);
            audit.Action(user.Identity?.Name ?? "?", group!.Name, "Grupo local criado", "Criado", group.Sid);
            return Results.Ok(local.ListGroups());
        });

        admin.MapPut("/identity/groups/{id:int}", (int id, LocalGroupUpdateDto dto, LocalDirectoryService local, AuditService audit, ClaimsPrincipal user) =>
        {
            var before = local.GetGroup(id);
            if (local.UpdateGroup(id, dto.Description, dto.Members) is { } error) return Error(error.StartsWith("Grupo não") ? 404 : 400, error);
            var after = local.GetGroup(id)!;
            if (dto.Members is not null)
            {
                var (old, now) = (string.Join(", ", before!.Members), string.Join(", ", after.Members));
                if (old != now) audit.Record([new AuditEntry(user.Identity?.Name ?? "?", "update", "identity", after.Sid, after.Name, "Membros do grupo local", old.Length == 0 ? "(vazio)" : old, now.Length == 0 ? "(vazio)" : now)]);
            }
            return Results.Ok(local.ListGroups());
        });

        admin.MapDelete("/identity/groups/{id:int}", (int id, LocalDirectoryService local, AuditService audit, ClaimsPrincipal user) =>
        {
            var before = local.GetGroup(id);
            if (local.DeleteGroup(id) is { } error) return Error(404, error);
            audit.Action(user.Identity?.Name ?? "?", before!.Name, "Grupo local apagado", "Removido", before.Sid);
            return Results.Ok(local.ListGroups());
        });

        // Detalhe de um grupo (tela Grupos): membros diretos, grupos relacionados e o que a loja libera para ele.
        admin.MapGet("/groups/{sid}", (string sid, GroupAdminService groups) =>
        {
            try
            {
                return groups.Details(sid) is { } details ? Results.Ok(details) : Error(404, "Grupo não encontrado no AD.");
            }
            catch (InvalidOperationException ex)
            {
                return Error(502, ex.Message);
            }
        });

        // Acrescenta/tira grupos de vários aplicativos de uma vez (tudo ou nada). O resultado traz como estavam, para o "Desfazer".
        admin.MapPost("/apps/bulk-groups", (BulkGroupsDto dto, GroupAdminService groups, ClaimsPrincipal user) =>
        {
            var (result, error) = groups.Bulk(user.Identity?.Name ?? "?", dto);
            return result is null ? Error(400, error!) : Results.Ok(result);
        });

        admin.MapPost("/apps/restore-groups", (RestoreGroupsDto dto, GroupAdminService groups, ClaimsPrincipal user) =>
        {
            var (result, error) = groups.Restore(user.Identity?.Name ?? "?", dto);
            return result is null ? Error(400, error!) : Results.Ok(result);
        });

        // "Ver como usuário": a loja exatamente como aquela pessoa vê (só leitura).
        admin.MapGet("/preview", async (string? user, string? host, AdminService panel, ClaimsPrincipal admin, ILogger<AdminService> log, CancellationToken ct) =>
        {
            var (preview, error) = await panel.PreviewAsync(user ?? "", host, ct);
            if (preview is null) return Error(404, error!);
            log.LogInformation("{Admin} viu a loja como {User}", admin.Identity?.Name, preview.User);
            return Results.Ok(preview);
        });

        // Trilha de auditoria: quem alterou o quê, quando, com o valor de antes e de depois.
        admin.MapGet("/audit", (string? text, string? entity, string? entityId, string? user, string? kind, string? from, string? to, int? page, int? size, string? sort, string? dir, AuditRepository audit) =>
        {
            static DateTime? Day(string? s) =>
                DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            var pageSize = Math.Clamp(size ?? 50, 10, 200);
            var current = Math.Max(1, page ?? 1);
            var (rows, total) = audit.Query(new AuditQuery(text, entity, entityId, user, kind, Day(from)?.ToUniversalTime(), Day(to)?.AddDays(1).ToUniversalTime(),
                (current - 1) * pageSize, pageSize, sort, !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase)));
            return new { total, page = current, size = pageSize, items = rows };
        });

        admin.MapGet("/history", (string? text, string? status, string? action, string? host, string? from, string? to, int? page, int? size, string? sort, string? dir, string? group, RequestRepository requests, GroupMembershipService membership) =>
        {
            var pageSize = Math.Clamp(size ?? 50, 10, 500);
            var query = BuildQuery(text, status, action, host, from, to, (Math.Max(1, page ?? 1) - 1) * pageSize, pageSize, sort, dir);
            if (!string.IsNullOrWhiteSpace(group))
            {
                if (!IsGroupFilter(group)) return Error(400, "Grupo inválido.");
                try { query = query with { Usernames = UsersInGroup(group.Trim(), query, requests, membership) }; }
                catch (InvalidOperationException ex) { return Error(502, ex.Message); }
            }
            var (rows, total) = requests.Query(query);
            return Results.Ok(new { total, page = Math.Max(1, page ?? 1), size = pageSize, items = rows.Select(r => r.ToDto(includeOutput: true)) });
        });

        admin.MapGet("/history.csv", (string? text, string? status, string? action, string? host, string? from, string? to, string? sort, string? dir, string? group, RequestRepository requests, GroupMembershipService membership) =>
        {
            var query = BuildQuery(text, status, action, host, from, to, 0, 5000, sort, dir);
            if (!string.IsNullOrWhiteSpace(group))
            {
                if (!IsGroupFilter(group)) return Error(400, "Grupo inválido.");
                try { query = query with { Usernames = UsersInGroup(group.Trim(), query, requests, membership) }; }
                catch (InvalidOperationException ex) { return Error(502, ex.Message); }
            }
            var (rows, _) = requests.Query(query);
            var csv = new StringBuilder();
            csv.AppendLine("Data;Usuário;Computador;Aplicativo;Pacote;Ação;Situação;Detalhe;Código");
            foreach (var r in rows)
            {
                csv.AppendLine(string.Join(';', new[]
                {
                    r.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"), r.Username, r.Hostname, r.Title, r.PackageId,
                    r.Action, r.Status, r.Message ?? "", r.ExitCode?.ToString() ?? "",
                }.Select(Csv)));
            }
            // BOM para o Excel abrir com acentos certos.
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"loja-historico-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        });

        // ---------- Versões do app da loja (autoupdate) ----------

        admin.MapGet("/client/releases", (ClientReleaseService releases) => releases.List());

        admin.MapPost("/client/releases", async (IFormFile file, bool? mandatory, ClientReleaseService releases, AuditService audit, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (release, error) = await releases.PublishAsync(file, mandatory == true, user.Identity?.Name ?? "?", ct);
                if (release is null) return Error(400, error!);
                audit.Release(user.Identity?.Name ?? "?", "create", release.Version, mandatory == true ? "Publicada como obrigatória" : "Publicada");
                return Results.Ok(release);
            })
            .DisableAntiforgery() // a proteção contra CSRF é o cabeçalho X-Requested-With exigido em Program.cs
            .WithMetadata(new RequestSizeLimitAttribute(ClientReleaseService.MaxSize));

        // Confere o .exe e mostra a versão detectada e o efeito de publicar, SEM publicar (o painel chama antes do botão "Publicar").
        admin.MapPost("/client/releases/inspect", async (IFormFile file, ClientReleaseService releases, CancellationToken ct) =>
            {
                var (inspected, error) = await releases.InspectAsync(file, ct);
                return inspected is null ? Error(400, error!) : Results.Ok(inspected);
            })
            .DisableAntiforgery() // a proteção contra CSRF é o cabeçalho X-Requested-With exigido em Program.cs
            .WithMetadata(new RequestSizeLimitAttribute(ClientReleaseService.MaxSize));

        admin.MapPost("/client/releases/{version}/current", (string version, bool? mandatory, ClientReleaseService releases, AuditService audit, ClaimsPrincipal user) =>
        {
            if (!releases.SetCurrent(version, mandatory == true, user.Identity?.Name ?? "?")) return Results.NotFound();
            audit.Release(user.Identity?.Name ?? "?", "update", version, mandatory == true ? "Passou a ser a atual (obrigatória)" : "Passou a ser a atual");
            return Results.Ok(releases.List());
        });

        admin.MapDelete("/client/releases/{version}", (string version, ClientReleaseService releases, AuditService audit, ClaimsPrincipal user) =>
        {
            if (!releases.Delete(version, user.Identity?.Name ?? "?")) return Error(400, "Só dá para apagar versões que não são a atual.");
            audit.Release(user.Identity?.Name ?? "?", "delete", version, "Apagada");
            return Results.NoContent();
        });

        admin.MapGet("/settings", (SettingsService settings) => settings.Get());

        // Rótulo e faixa de cada parâmetro: a fonte é o servidor, para o formulário do painel não duplicar a regra.
        admin.MapGet("/settings/meta", (SettingsService settings) => settings.Describe());

        admin.MapPut("/settings", (SettingsDto dto, SettingsService settings, AuditService audit, ClaimsPrincipal user) =>
        {
            var before = settings.Get();
            if (settings.Save(dto, user.Identity?.Name ?? "?") is { } error) return Error(400, error);
            audit.SettingsChanged(user.Identity?.Name ?? "?", before, settings.Get(), settings);
            return Results.Ok(settings.Get());
        });

        // Motor de configurações: o painel desenha o formulário a partir desta descrição (nada de tela escrita à mão por chave).
        admin.MapGet("/config", (ConfigService config) => Results.Ok(config.Describe()));

        // Salva várias chaves de uma vez: { "values": { "chocolatey.enabled": false, "agent.pollSeconds": null } }. Null volta ao padrão.
        admin.MapPut("/config", (ConfigPatchDto dto, ConfigService config, AuditService audit, ClaimsPrincipal user) =>
        {
            if (dto.Values is null || dto.Values.Count == 0) return Error(400, "Nada para salvar.");
            var who = user.Identity?.Name ?? "?";
            var result = config.Save(dto.Values, who);
            if (result.Error is not null) return Error(400, result.Error);
            audit.ConfigChanged(who, result.Changes);
            return Results.Ok(config.Describe());
        });

        // Ferramenta do painel: roda um ciclo da atualização automática agora, fora da janela de horário.
        admin.MapPost("/autoupdate/run", async (AutoUpdateService autoUpdate, AuditService audit, ClaimsPrincipal user, ILogger<AutoUpdateService> log, CancellationToken ct) =>
        {
            var started = await autoUpdate.RunCycleAsync(ct);
            audit.Action(user.Identity?.Name ?? "?", "Atualização automática", "Rodada agora, fora da janela", started == 1 ? "1 pedido enviado" : $"{started} pedidos enviados");
            log.LogInformation("{User} rodou a atualização automática manualmente: {Count} pedido(s)", user.Identity?.Name, started);
            return Results.Ok(new { started });
        });
    }

    private static IResult Error(int status, string message) => Results.Json(new ErrorDto(message), statusCode: status);

    /// <summary>
    /// Filtro "grupo do usuário": o servidor pergunta ao AD (com cache) quem, entre as pessoas que aparecem nos resultados,
    /// está no grupo, e devolve os nomes de usuário como estão gravados no banco. Vale a associação de agora.
    /// </summary>
    private static List<string> UsersInGroup(string sid, RequestQuery filtered, RequestRepository requests, GroupMembershipService membership)
    {
        var stored = requests.DistinctUsernames(filtered)
            .Select(s => (Stored: s, Login: GroupMembershipService.LoginOf(s)))
            .Where(x => x.Login is not null)
            .ToList();
        var members = membership.Members(sid, stored.Select(x => x.Login!));
        return stored.Where(x => members.Contains(x.Login!)).Select(x => x.Stored).ToList();
    }

    private static bool IsGroupFilter(string? group) => group == AccessService.Everyone || group?.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Filtros do histórico. Datas no formato aaaa-mm-dd (dia local, "até" inclui o dia inteiro).</summary>
    private static RequestQuery BuildQuery(string? text, string? status, string? action, string? host, string? from, string? to, int skip, int take, string? sort = null, string? dir = null)
    {
        static DateTime? Day(string? s) =>
            DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        return new RequestQuery(
            string.IsNullOrWhiteSpace(text) ? null : text,
            string.IsNullOrWhiteSpace(status) ? null : status,
            string.IsNullOrWhiteSpace(action) ? null : action,
            string.IsNullOrWhiteSpace(host) ? null : host,
            Day(from)?.ToUniversalTime(),
            Day(to)?.AddDays(1).ToUniversalTime(),
            skip,
            take,
            string.IsNullOrWhiteSpace(sort) ? null : sort,
            !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase));
    }

    private static string Csv(string value)
    {
        // Evita que o Excel interprete fórmulas vindas dos dados (=, +, -, @).
        if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
        return value.Contains(';') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
