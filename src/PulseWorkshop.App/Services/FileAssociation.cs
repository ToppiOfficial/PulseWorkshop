using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PulseWorkshop.App.Services;

/// <summary>
/// Registers PulseWorkshop as the default handler for <c>.pw_mdlproject</c> files for the current user
/// (HKCU - no administrator rights needed), so double-clicking a project in Explorer launches the app
/// with the file path as its argument (picked up in <c>App.OnStartup</c>).
///
/// Best-effort and idempotent: it only rewrites the registry (and notifies the shell) when the stored
/// open command isn't already this exe, so a normal launch does no work once the association is set.
/// Windows-only; a no-op on other platforms.
/// </summary>
internal static class FileAssociation
{
    private const string Extension = ".pw_mdlproject";
    private const string ProgId = "PulseWorkshop.mdlproject";
    private const string FriendlyType = "PulseWorkshop Model Project";

    public static void EnsureRegistered()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return;

            var command = $"\"{exePath}\" \"%1\"";

            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            if (classes is null)
                return;

            // Already pointing at this exe (extension -> our ProgId -> our command)? Nothing to do -
            // this keeps every normal launch from rewriting the registry and refreshing the shell.
            using (var existingCmd = classes.OpenSubKey($@"{ProgId}\shell\open\command"))
            using (var existingExt = classes.OpenSubKey(Extension))
            {
                if (existingCmd?.GetValue(null) as string == command &&
                    existingExt?.GetValue(null) as string == ProgId)
                    return;
            }

            using (var progId = classes.CreateSubKey(ProgId))
            {
                progId.SetValue(null, FriendlyType);
                using var cmd = progId.CreateSubKey(@"shell\open\command");
                cmd.SetValue(null, command);
            }

            using (var ext = classes.CreateSubKey(Extension))
                ext.SetValue(null, ProgId);

            // Tell Explorer the association changed so the icon/handler updates without a re-login.
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // A locked-down or roaming profile may deny the write. The app still works fully; it just
            // won't be wired up for double-click opening.
        }
    }

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
