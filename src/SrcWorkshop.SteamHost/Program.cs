using System.IO.Pipes;
using System.Text;
using SrcWorkshop.Core.Ipc;
using SrcWorkshop.SteamBridge;

namespace SrcWorkshop.SteamHost;

/// <summary>
/// Per-game Steam helper process. Launched by the App with a single argument: the App ID.
/// It configures the App ID, initializes the Steam session via the C++/CLI bridge, then serves
/// JSON requests over a named pipe until told to shut down.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1 || !uint.TryParse(args[0], out var appId))
        {
            Console.Error.WriteLine("Usage: SrcWorkshop.SteamHost <appId>");
            return 2;
        }

        // The Steamworks App ID must be known *before* SteamAPI_Init. Writing steam_appid.txt
        // next to the exe is the documented way and lets us hook the running client without
        // relaunching through Steam (and without a separate login).
        TryWriteAppIdFile(appId);

        using var workshop = new SteamWorkshop();
        if (!workshop.Init())
        {
            Console.Error.WriteLine("Steam is not running or the game is not owned by this account.");
            // Still serve the pipe so the App gets a clean "not running" Ping instead of a crash.
        }

        var server = new HostServer(workshop, appId);
        try
        {
            server.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            workshop.Shutdown();
        }
    }

    private static void TryWriteAppIdFile(uint appId)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            File.WriteAllText(Path.Combine(dir, "steam_appid.txt"), appId.ToString(), Encoding.ASCII);
        }
        catch
        {
            // Fall back to the SteamAppId env var if the directory is not writable.
            Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
        }
    }
}
