using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Relay.Agents.Processes;

/// <summary>Best-effort kills: a failed kill must not replace the cancellation or error already in flight.</summary>
internal static class ProcessKill
{
    private const int SigKill = 9;

    /// <summary>The CLI is often a shim (cmd/node/sh) around the real process, so the whole tree goes.</summary>
    public static void Tree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
            // It exited after all, or could not be killed.
        }
    }

    /// <summary>
    /// Kills, on macOS and Linux, the process group an agent leads in its own session, with what it
    /// left there when its parent exited: a tree walk no longer finds those.
    /// </summary>
    public static void Group(int id) => kill(-id, SigKill);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
