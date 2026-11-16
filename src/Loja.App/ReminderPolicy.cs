namespace Loja.App;

public enum ReminderAction
{
    /// <summary>Não avisa agora (nada pendente, adiado ou avisou há pouco).</summary>
    None,
    /// <summary>Mostra o aviso no canto da tela.</summary>
    Popup,
    /// <summary>Pendência antiga: abre a janela da loja (no máximo uma vez por dia).</summary>
    OpenWindow,
}

/// <summary>O que o lembrete guarda entre uma execução e outra (por usuário, em %LOCALAPPDATA%).</summary>
public sealed class ReminderState
{
    /// <summary>Desde quando cada aplicativo (id do pacote) está com atualização pendente.</summary>
    public Dictionary<string, DateTime> FirstSeenUtc { get; set; } = new();
    public DateTime? LastNotifyUtc { get; set; }
    public DateTime? SnoozeUntilUtc { get; set; }
    public DateTime? LastWindowUtc { get; set; }
}

/// <summary>
/// Regras do lembrete de atualização, sem tela nem rede (dá para testar sozinha).
/// Aviso no logon e a cada <c>interval</c>; depois de <c>escalationDays</c> sem atualizar, abre a janela da loja uma vez por dia.
/// "Lembrar amanhã" suspende tudo por 24 h.
/// </summary>
public static class ReminderPolicy
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(4);
    public const int DefaultEscalationDays = 3;

    // O logon e a repetição de 4 h podem cair juntos: um aviso conta como feito se o anterior foi há quase o intervalo.
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan WindowEvery = TimeSpan.FromHours(24);
    public static readonly TimeSpan SnoozeFor = TimeSpan.FromHours(24);

    /// <summary>Atualiza o estado com a lista de agora: mantém a data de quem já estava pendente e esquece quem foi atualizado.</summary>
    public static void Track(ReminderState state, IEnumerable<string> pendingPackageIds, DateTime nowUtc)
    {
        var pending = new HashSet<string>(pendingPackageIds, StringComparer.OrdinalIgnoreCase);
        foreach (var id in state.FirstSeenUtc.Keys.Where(id => !pending.Contains(id)).ToList())
            state.FirstSeenUtc.Remove(id);
        foreach (var id in pending)
            state.FirstSeenUtc.TryAdd(id, nowUtc);
        if (pending.Count == 0) state.SnoozeUntilUtc = null;
    }

    public static ReminderAction Decide(ReminderState state, DateTime nowUtc, TimeSpan interval, int escalationDays)
    {
        if (state.FirstSeenUtc.Count == 0) return ReminderAction.None;
        if (state.SnoozeUntilUtc is { } snooze && snooze > nowUtc) return ReminderAction.None;
        if (state.LastNotifyUtc is { } last && nowUtc - last < interval - Tolerance) return ReminderAction.None;

        var oldest = nowUtc - state.FirstSeenUtc.Values.Min();
        var escalate = escalationDays > 0 && oldest >= TimeSpan.FromDays(escalationDays);
        if (escalate && (state.LastWindowUtc is null || nowUtc - state.LastWindowUtc.Value >= WindowEvery))
            return ReminderAction.OpenWindow;
        return ReminderAction.Popup;
    }
}
