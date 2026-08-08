using System.Diagnostics;
using System.IO;
using System.Windows.Input;

namespace PulseWorkshop.App.Services;

/// <summary>
/// The one place the app hands a path to the Windows shell. Every "open this file" action routes
/// through <see cref="Open"/>, so the Alt modifier means the same thing everywhere: hold Alt while
/// clicking and the file is revealed in Explorer instead of launched.
/// </summary>
public static class ShellOpen
{
    /// <summary>
    /// Opens <paramref name="path"/> with whatever app the user has associated (or the shell's
    /// "Open with" picker when nothing is registered) - unless Alt is held, which reveals the file
    /// in Explorer instead. Folders always open in place; a blank path does nothing.
    /// </summary>
    public static void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && File.Exists(path))
            Reveal(path);
        else
            // The trailing separator is trimmed because the shell won't open "C:\dir\" as a folder.
            Start(new ProcessStartInfo(path.TrimEnd('\\', '/')) { UseShellExecute = true });
    }

    /// <summary>Shows <paramref name="path"/> in Explorer: a file selected inside its parent
    /// folder, a folder opened in place. Used by the explicit "Go to file" buttons.</summary>
    public static void Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Start(Directory.Exists(path)
            ? new ProcessStartInfo("explorer.exe", $"\"{path.TrimEnd('\\', '/')}\"")
            : new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
    }

    private static void Start(ProcessStartInfo info)
    {
        info.UseShellExecute = true;
        Process.Start(info);
    }
}
