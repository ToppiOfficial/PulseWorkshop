using System.Net.Http;
using System.Xml.Linq;

namespace PulseWorkshop.App.Services;

/// <summary>
/// Resolves public Steam profile bits we can't get cheaply from the bridge. The avatar image URL
/// isn't derivable from the SteamID alone (it's a SHA1 of the image), so we read it from the
/// public Steam Community profile XML - no API key, no SDK call.
/// </summary>
public static class SteamProfile
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // A User-Agent matters: without one the Community endpoint can answer with a non-XML page
        // (an interstitial / redirect) instead of the profile XML, so parsing quietly yields nothing.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PulseWorkshop/1.0");
        return http;
    }

    /// <summary>
    /// Returns the medium (64px) avatar image URL for a SteamID64, or null if the profile can't be
    /// read (offline, private, or no avatar). Never throws for the expected failure cases.
    /// </summary>
    public static async Task<string?> GetAvatarUrlAsync(ulong steamId64, CancellationToken ct = default)
    {
        try
        {
            var profileUrl = $"https://steamcommunity.com/profiles/{steamId64}?xml=1";
            await using var stream = await Http.GetStreamAsync(profileUrl, ct).ConfigureAwait(false);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);

            // Prefer the medium avatar; fall back to the small one.
            var url = doc.Root?.Element("avatarMedium")?.Value
                      ?? doc.Root?.Element("avatarIcon")?.Value;
            return string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the public display (persona) name for a SteamID64, or null if the profile can't be read
    /// (offline, private, or unset). Same keyless Community-profile XML source as <see cref="GetAvatarUrlAsync"/>.
    /// </summary>
    public static async Task<string?> GetPersonaNameAsync(ulong steamId64, CancellationToken ct = default)
    {
        if (steamId64 == 0)
            return null;
        try
        {
            var profileUrl = $"https://steamcommunity.com/profiles/{steamId64}?xml=1";
            await using var stream = await Http.GetStreamAsync(profileUrl, ct).ConfigureAwait(false);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);

            var name = doc.Root?.Element("steamID")?.Value;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
        }
    }
}
