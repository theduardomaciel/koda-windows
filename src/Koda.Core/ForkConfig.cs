namespace Koda.Core;

/// <summary>
/// Fork-only links. Upstream's own URLs stay where they are; every surface that
/// sends someone to Koda's repository or releases reads the values here instead,
/// so a fork build never points its users at upstream's downloads.
/// </summary>
public static class ForkConfig
{
    public const string Repo = "theduardomaciel/koda-windows";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string LatestReleaseUrl = ReleasesUrl + "/latest";
    public const string IssuesUrl = RepoUrl + "/issues";

    /// <summary>The publishable Spicy Lyrics key (<c>sl_pk_…</c>). It ships in the app
    /// and is public by design; the Spicy application must allow requests with no origin
    /// header for native clients. Leave it empty to skip Spicy until the key is issued.</summary>
    public const string SpicyLyricsKey = "sl_pk_wot6V6SY5BcKmxaLHaO_BwOyGCmR4kpgth4NZGd2suE";
}
