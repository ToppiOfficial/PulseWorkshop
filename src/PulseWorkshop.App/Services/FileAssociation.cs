using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PulseWorkshop.App.Services;

/// <summary>
/// Registers PulseWorkshop as the default handler for its project files (<c>.pw_mdlproject</c> and
/// <c>.pw_textureproject</c>) for the current user (HKCU - no administrator rights needed), so
/// double-clicking a project in Explorer launches the app with the file path as its argument (picked up
/// in <c>App.OnStartup</c>).
///
/// Best-effort and idempotent: it only rewrites the registry (and notifies the shell) when a stored open
/// command isn't already this exe, so a normal launch does no work once the associations are set.
/// Windows-only; a no-op on other platforms.
/// </summary>
internal static class FileAssociation
{
    /// <summary>One registered project file type: its extension, ProgId, and Explorer-friendly name.</summary>
    private readonly record struct Association(string Extension, string ProgId, string FriendlyType);

    private static readonly Association[] Associations =
    {
        new(".pw_mdlproject", "PulseWorkshop.mdlproject", "PulseWorkshop Model Project"),
        new(".pw_textureproject", "PulseWorkshop.textureproject", "PulseWorkshop Textures Project"),
    };

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

            var changed = false;
            foreach (var a in Associations)
                changed |= EnsureOne(classes, a, command);

            // Tell Explorer any changed association's icon/handler updates without a re-login.
            if (changed)
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // A locked-down or roaming profile may deny the write. The app still works fully; it just
            // won't be wired up for double-click opening.
        }
    }

    /// <summary>Registers one extension -> ProgId -> command mapping. Returns whether anything changed
    /// (already pointing at this exe is a no-op, so normal launches don't rewrite the registry).</summary>
    private static bool EnsureOne(RegistryKey classes, Association a, string command)
    {
        using (var existingCmd = classes.OpenSubKey($@"{a.ProgId}\shell\open\command"))
        using (var existingExt = classes.OpenSubKey(a.Extension))
        {
            if (existingCmd?.GetValue(null) as string == command &&
                existingExt?.GetValue(null) as string == a.ProgId)
                return false;
        }

        using (var progId = classes.CreateSubKey(a.ProgId))
        {
            progId.SetValue(null, a.FriendlyType);
            using var cmd = progId.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, command);
        }

        using (var ext = classes.CreateSubKey(a.Extension))
            ext.SetValue(null, a.ProgId);

        return true;
    }

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
