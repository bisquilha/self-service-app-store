using Loja.App;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine((ok ? "ok    " : "FALHOU ") + name);
    if (!ok) failures++;
}

var t0 = new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc);
var interval = ReminderPolicy.DefaultInterval;
const int escal = 3;

ReminderState Pending(params string[] ids)
{
    var s = new ReminderState();
    ReminderPolicy.Track(s, ids, t0);
    return s;
}

// nada pendente
Check("sem pendência: não avisa", ReminderPolicy.Decide(new ReminderState(), t0, interval, escal) == ReminderAction.None);

// primeira vez
var s1 = Pending("vlc");
Check("primeira vez: avisa", ReminderPolicy.Decide(s1, t0, interval, escal) == ReminderAction.Popup);

// logo depois de avisar
s1.LastNotifyUtc = t0;
Check("1 h depois: não repete", ReminderPolicy.Decide(s1, t0.AddHours(1), interval, escal) == ReminderAction.None);
Check("logon a 3 h 45: ainda dentro da tolerância, repete", ReminderPolicy.Decide(s1, t0.AddHours(3.75), interval, escal) == ReminderAction.Popup);
Check("3 h 15: ainda não", ReminderPolicy.Decide(s1, t0.AddHours(3.25), interval, escal) == ReminderAction.None);
Check("4 h: repete", ReminderPolicy.Decide(s1, t0.AddHours(4), interval, escal) == ReminderAction.Popup);

// adiar
var s2 = Pending("vlc");
s2.SnoozeUntilUtc = t0.AddHours(24);
Check("adiado: não avisa em 23 h", ReminderPolicy.Decide(s2, t0.AddHours(23), interval, escal) == ReminderAction.None);
Check("adiado: volta a avisar depois de 24 h", ReminderPolicy.Decide(s2, t0.AddHours(24.5), interval, escal) == ReminderAction.Popup);

// escalada
var s3 = Pending("vlc");
Check("2 dias pendente: só aviso", ReminderPolicy.Decide(s3, t0.AddDays(2), interval, escal) == ReminderAction.Popup);
Check("3 dias pendente: abre a loja", ReminderPolicy.Decide(s3, t0.AddDays(3), interval, escal) == ReminderAction.OpenWindow);
s3.LastWindowUtc = t0.AddDays(3);
s3.LastNotifyUtc = t0.AddDays(3);
Check("depois de abrir: no mesmo dia volta ao aviso comum", ReminderPolicy.Decide(s3, t0.AddDays(3).AddHours(5), interval, escal) == ReminderAction.Popup);
Check("depois de 24 h abre de novo", ReminderPolicy.Decide(s3, t0.AddDays(4).AddHours(1), interval, escal) == ReminderAction.OpenWindow);
Check("escalada desligada (0 dias): nunca abre sozinha", ReminderPolicy.Decide(Pending("vlc"), t0.AddDays(30), interval, 0) == ReminderAction.Popup);

// Track: mantém a data de quem continua pendente, esquece quem atualizou, zera adiamento quando acaba
var s4 = new ReminderState();
ReminderPolicy.Track(s4, ["vlc", "7zip"], t0);
ReminderPolicy.Track(s4, ["VLC", "chrome"], t0.AddDays(1));
Check("Track: vlc mantém a data original (ignora maiúsculas)", s4.FirstSeenUtc["vlc"] == t0);
Check("Track: 7zip saiu da lista", !s4.FirstSeenUtc.ContainsKey("7zip"));
Check("Track: chrome entrou com a data de agora", s4.FirstSeenUtc["chrome"] == t0.AddDays(1));
s4.SnoozeUntilUtc = t0.AddDays(2);
ReminderPolicy.Track(s4, [], t0.AddDays(1));
Check("Track: sem pendência zera tudo e o adiamento", s4.FirstSeenUtc.Count == 0 && s4.SnoozeUntilUtc is null);

// atualizou e apareceu outra depois: conta como pendência nova (não herda a escalada antiga)
var s5 = Pending("vlc");
ReminderPolicy.Track(s5, [], t0.AddDays(5));
ReminderPolicy.Track(s5, ["chrome"], t0.AddDays(6));
Check("pendência nova depois de zerar: aviso comum, sem escalada", ReminderPolicy.Decide(s5, t0.AddDays(6), interval, escal) == ReminderAction.Popup);

Console.WriteLine(failures == 0 ? "\nTudo certo." : $"\n{failures} falha(s).");
return failures == 0 ? 0 : 1;
