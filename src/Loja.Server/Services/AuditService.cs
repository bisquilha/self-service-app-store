using Loja.Server.Data;

namespace Loja.Server.Services;

/// <summary>
/// Registra na trilha de auditoria o que o painel altera: aplicativos, parâmetros, versões do app e ações em lote.
/// Registrar nunca pode derrubar a alteração em si: se a gravação da auditoria falhar, só vai para o log do servidor.
/// </summary>
public class AuditService(AuditRepository repo, ILogger<AuditService> log)
{
    public const string TypeApp = "app";
    public const string TypeSettings = "settings";
    public const string TypeRelease = "release";
    public const string TypeAction = "action";

    public static string YesNo(bool value) => value ? "Sim" : "Não";

    /// <summary>Nomes dos grupos para ler na tela ("*" vira "Todos os usuários").</summary>
    public static string GroupsText(IEnumerable<GroupRef> groups) =>
        string.Join(", ", groups.Select(g => g.IsEveryone ? "Todos os usuários" : g.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));

    public void Record(IEnumerable<AuditEntry> entries)
    {
        try
        {
            var list = entries.ToList();
            if (list.Count > 0) repo.Add(list);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Não foi possível gravar a auditoria");
        }
    }

    public void AppCreated(string user, AppRow app) =>
        Record([new AuditEntry(user, "create", TypeApp, app.Id.ToString(), app.Title, "Aplicativo", null, $"Adicionado à loja para {GroupsText(app.GroupRefs)}")]);

    public void AppDeleted(string user, AppRow app) =>
        Record([new AuditEntry(user, "delete", TypeApp, app.Id.ToString(), app.Title, "Aplicativo", $"Estava na loja para {GroupsText(app.GroupRefs)}", "Removido da loja")]);

    /// <summary>Uma linha por campo que mudou. Se nada mudou, não grava nada.</summary>
    public void AppUpdated(string user, AppRow before, AppRow after, bool undo = false, string? batch = null)
    {
        var id = after.Id.ToString();
        var entries = new List<AuditEntry>();
        void Add(string field, string old, string now)
        {
            if (old != now) entries.Add(new AuditEntry(user, undo ? "undo" : "update", TypeApp, id, after.Title, field, old, now, batch, undo));
        }

        var oldKeys = before.GroupRefs.Select(g => g.Key).ToHashSet();
        var newKeys = after.GroupRefs.Select(g => g.Key).ToHashSet();
        if (!oldKeys.SetEquals(newKeys)) Add("Grupos do AD", GroupsText(before.GroupRefs), GroupsText(after.GroupRefs));
        Add("Ativo", YesNo(before.Enabled), YesNo(after.Enabled));
        Add("Atualização automática", YesNo(before.AutoUpdate), YesNo(after.AutoUpdate));
        Add("Permitir desinstalar", YesNo(before.AllowUninstall), YesNo(after.AllowUninstall));
        Record(entries);
    }

    /// <summary>Nomes que o programa do aplicativo tem no Windows (cadastro que liga "já instalado fora da loja" ao aplicativo).</summary>
    public void AliasesChanged(string user, AppRow app, IReadOnlyCollection<string> before, IReadOnlyCollection<string> after, bool undo = false)
    {
        string Text(IEnumerable<string> names) => names.Any() ? string.Join(" | ", names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)) : "(nenhum)";
        var (old, now) = (Text(before), Text(after));
        if (old == now) return;
        Record([new AuditEntry(user, undo ? "undo" : "update", TypeApp, app.Id.ToString(), app.Title, "Nomes no Windows", old, now, null, undo)]);
    }

    public void SettingsChanged(string user, SettingsDto before, SettingsDto after, SettingsService settings)
    {
        var entries = new List<AuditEntry>();
        foreach (var p in typeof(SettingsDto).GetProperties())
        {
            var (old, now) = (p.GetValue(before)?.ToString() ?? "", p.GetValue(after)?.ToString() ?? "");
            if (old != now) entries.Add(new AuditEntry(user, "update", TypeSettings, p.Name, "Configurações", settings.LabelOf(p.Name), old, now));
        }
        Record(entries);
    }

    /// <summary>Mudanças do motor de configurações: uma linha por chave que mudou (a chave vai como identificador).</summary>
    public void ConfigChanged(string user, IEnumerable<ConfigChange> changes) =>
        Record(changes.Select(c => new AuditEntry(user, "update", TypeSettings, c.Key, "Configurações", c.Label, c.Old, c.New)));

    public void Release(string user, string kind, string version, string detail) =>
        Record([new AuditEntry(user, kind, TypeRelease, version, $"App da loja {version}", "Versão do app", null, detail)]);

    /// <summary>Ação que não muda cadastro, mas mexe em várias máquinas (atualização automática, atualizar em todas).</summary>
    public void Action(string user, string label, string field, string result, string? entityId = null) =>
        Record([new AuditEntry(user, "run", TypeAction, entityId, label, field, null, result)]);
}
