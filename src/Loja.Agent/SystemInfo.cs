using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Loja.Agent;

/// <summary>O que o agente sabe sobre o computador: nome, Windows e quem está logado.</summary>
public static class SystemInfo
{
    public static string Hostname => Environment.MachineName;

    /// <summary>1 = estação, 2 = controlador de domínio, 3 = servidor. Vem do registro (mesma fonte do ProductType do WMI).</summary>
    public static int ProductType()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            return (key?.GetValue("ProductType") as string)?.ToUpperInvariant() switch
            {
                "WINNT" => 1,
                "LANMANNT" => 2,
                "SERVERNT" => 3,
                _ => 3, // na dúvida, trata como servidor: a loja nunca instala nele
            };
        }
        catch
        {
            return 3;
        }
    }

    public static (string Caption, int Build) Windows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = key?.GetValue("ProductName") as string ?? "Windows";
            var build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var b) ? b : 0;
            // O Windows 11 ainda grava "Windows 10" no ProductName; da build 22000 em diante é o 11.
            if (build >= 22000 && name.Contains("Windows 10", StringComparison.Ordinal)) name = name.Replace("Windows 10", "Windows 11");
            return (name, build);
        }
        catch
        {
            return ("Windows", 0);
        }
    }

    /// <summary>Usuário da sessão ativa (console, ou a primeira sessão ativa), como "DOMINIO\usuario". Nulo se ninguém está logado.</summary>
    public static string? LoggedUser()
    {
        try
        {
            var console = WTSGetActiveConsoleSessionId();
            if (console != 0xFFFFFFFF && UserOf(console) is { } u) return u;

            if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
            {
                try
                {
                    var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                    for (var i = 0; i < count; i++)
                    {
                        var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                        if (info.State == 0 /* WTSActive */ && UserOf(info.SessionId) is { } user) return user;
                    }
                }
                finally
                {
                    WTSFreeMemory(buffer);
                }
            }
        }
        catch
        {
            // sem usuário conhecido: a loja decide o que fazer
        }
        return null;
    }

    private static string? UserOf(uint session)
    {
        var name = Query(session, 5 /* WTSUserName */);
        if (string.IsNullOrWhiteSpace(name)) return null;
        var domain = Query(session, 7 /* WTSDomainName */);
        return string.IsNullOrWhiteSpace(domain) ? name : $"{domain}\\{name}";
    }

    private static string? Query(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buffer, out _)) return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
