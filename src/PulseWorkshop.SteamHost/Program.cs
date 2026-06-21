using System.IO.Pipes;
using System.Text;
using PulseWorkshop.Core.Ipc;
using PulseWorkshop.SteamBridge;

namespace PulseWorkshop.SteamHost;

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
            Console.Error.WriteLine("Usage: PulseWorkshop.SteamHost <appId>");
            return 2;
        }

        // The Steamworks App ID must be known *before* SteamAPI_Init. Use the SteamAppId /
        // SteamGameId environment variables (the approach Crowbar's SteamPipe relies on) rather
        // than a steam_appid.txt next to the exe - a stray steam_appid.txt is known to break
        // Workshop content uploads (they fail with EResult NoConnection / Fail).
        SetAppIdEnvironment(appId);

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

    private static void SetAppIdEnvironment(uint appId)
    {
        var id = appId.ToString();
        Environment.SetEnvironmentVariable("SteamAppId", id);
        Environment.SetEnvironmentVariable("SteamGameId", id);

        // Defensive: if a steam_appid.txt was left next to the exe by an older build, remove it -
        // it overrides the env var and breaks content uploads.
        try
        {
            var stale = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
            if (File.Exists(stale))
                File.Delete(stale);
        }
        catch { /* best effort */ }
    }
}
