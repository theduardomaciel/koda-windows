using System.Text.Json;
using System.Text.Json.Serialization;

namespace Koda.Core.Profiles;

public enum ProfileKind
{
    /// <summary>Only on this PC; never makes a signed-in request.</summary>
    Device,
    /// <summary>Signed in to a YouTube Music account.</summary>
    YouTube,
}

/// <summary>
/// One channel under a signed-in Google account: the primary channel or a brand channel.
/// YouTube Music keeps likes, history and the library per channel, so each one owns a folder.
/// </summary>
public sealed class KodaChannel
{
    /// <summary>The brand channel's page id; null for the account's primary channel.</summary>
    public string? PageId { get; set; }

    /// <summary>The channel's <c>UC…</c> id, when the account menu names it.</summary>
    public string? ChannelId { get; set; }

    public string Name { get; set; } = "";
    public string? Handle { get; set; }
    public string? Photo { get; set; }

    /// <summary>The server-issued switch URL (<c>/signin?…</c>) that selects this identity.</summary>
    public string? SigninUrl { get; set; }

    /// <summary>The Google account's id, the same on every channel under it.</summary>
    public string? DatasyncId { get; set; }

    public bool IsSelected { get; set; }
    public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Channel" : Name.Trim();

    /// <summary>The folder key: <c>primary</c>, or the brand page id.</summary>
    [JsonIgnore]
    public string Key => string.IsNullOrEmpty(PageId) ? "primary" : PageId;
}

/// <summary>
/// One person's identity in Koda: a library of their own, and optionally the YouTube Music
/// account it is signed in to. Ported from KodaCore's KodaProfile.
/// </summary>
public sealed class KodaProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ProfileKind Kind { get; set; } = ProfileKind.Device;
    public string Name { get; set; } = "";
    public string? Handle { get; set; }
    public string? AccountPhoto { get; set; }

    /// <summary>The Google account's display name and email, when the account list named them.</summary>
    public string? AccountName { get; set; }
    public string? AccountEmail { get; set; }

    /// <summary>The channels under this account, cached from <c>account/accounts_list</c>.</summary>
    public List<KodaChannel> Channels { get; set; } = [];

    /// <summary>The channel selected now; null is the primary channel.</summary>
    public string? ActiveChannelPageId { get; set; }

    /// <summary>The channel that owns the profile's own folder. The rest live under channels/.</summary>
    public string? RootChannelPageId { get; set; }

    /// <summary>False until the first channel list says which channel owns the existing folder.</summary>
    public bool RootChannelAssigned { get; set; }

    /// <summary>YouTube's own id for the account: signing in to it again repairs this profile
    /// instead of adding a second one.</summary>
    public string? DatasyncId { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>YouTube answered this account's requests as signed out.</summary>
    public bool IsExpired { get; set; }

    /// <summary>The install's first library, which stays where it always was in the data root,
    /// under the credential it always had. Upgrading moves nothing.</summary>
    public bool UsesRoot { get; set; }

    [JsonIgnore]
    public bool IsSignedIn => Kind == ProfileKind.YouTube;

    /// <summary>Never blank on screen.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Listener" : Name.Trim();

    /// <summary>What the profile is, in a line under its name.</summary>
    [JsonIgnore]
    public string Subtitle => IsExpired ? "Sign in again"
        : IsSignedIn ? Handle ?? "YouTube Music"
        : "On this PC";

    /// <summary>The channel selected now, when the cached list has it.</summary>
    [JsonIgnore]
    public KodaChannel? ActiveChannel =>
        IsSignedIn
            ? Channels.FirstOrDefault(channel => string.Equals(channel.PageId, ActiveChannelPageId, StringComparison.Ordinal))
            : null;

    /// <summary>The name the title bar should show: the active channel, or the account/device name.</summary>
    [JsonIgnore]
    public string ListeningName => ActiveChannel?.DisplayName ?? DisplayName;

    [JsonIgnore]
    public string ListeningSubtitle => ActiveChannel is { } channel
        ? channel.Handle ?? "YouTube Music"
        : Subtitle;

    [JsonIgnore]
    public string? ListeningPhoto => ActiveChannel?.Photo ?? AccountPhoto;
}

/// <summary>
/// Every profile on this PC and which one is active. Each owns a folder under
/// <c>Profiles/&lt;id&gt;/</c>, so switching points the library at another folder: no network,
/// instant, offline. There is always at least one profile.
///
/// Profiles are optional. An install with just the one writes no roster at all: nothing is
/// moved and nothing changes until a second profile is added.
/// </summary>
public sealed class ProfileRoster
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<ProfileKind>() },
    };

    /// <summary>The files a library keeps, for deleting the one that lives in the data root.</summary>
    private static readonly string[] LibraryFiles = ["library.json", "history.json", "account.json", "account-mirror.json"];

    private readonly string _root;
    private readonly string _file;
    private readonly List<KodaProfile> _profiles = [];

    private sealed class Saved
    {
        public List<KodaProfile> Profiles { get; set; } = [];
        public Guid? ActiveId { get; set; }
    }

    public ProfileRoster(string root)
    {
        _root = root;
        _file = Path.Combine(root, "profiles.json");
        try
        {
            if (File.Exists(_file)
                && JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file), Options) is { Profiles.Count: > 0 } saved)
            {
                _profiles.AddRange(saved.Profiles);
                ActiveId = saved.ActiveId is { } id && _profiles.Exists(p => p.Id == id) ? id : _profiles[0].Id;
                return;
            }
        }
        catch (Exception)
        {
            // A damaged roster falls back to the library in the data root, which is intact.
        }

        var first = new KodaProfile { UsesRoot = true };
        _profiles.Add(first);
        ActiveId = first.Id;
    }

    public event Action? Changed;

    public IReadOnlyList<KodaProfile> Profiles => _profiles;

    public Guid ActiveId { get; private set; }

    /// <summary>The profile active before this one, for a quick switch back. In memory only:
    /// after a relaunch "back" would lead somewhere the listener didn't just come from.</summary>
    public Guid? PreviousId { get; private set; }

    public KodaProfile Active => _profiles.Find(p => p.Id == ActiveId) ?? _profiles[0];

    /// <summary>More than one: only then does Koda show that profiles exist outside Settings.</summary>
    public bool HasSeveral => _profiles.Count > 1;

    public KodaProfile? Profile(Guid id) => _profiles.Find(p => p.Id == id);

    /// <summary>Where the profile keeps its library; made on first use. This is the account's
    /// root; a channel folder resolves through <see cref="DataFolder"/>.</summary>
    public string Folder(KodaProfile profile)
    {
        if (profile.UsesRoot) return _root;
        var folder = Path.Combine(_root, "Profiles", profile.Id.ToString());
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The folder of the channel selected now.</summary>
    public string DataFolder(KodaProfile profile) => ChannelFolder(profile, profile.ActiveChannelPageId);

    /// <summary>
    /// A channel's library folder. The channel that owns the profile's root (<see cref="KodaProfile.RootChannelPageId"/>)
    /// keeps it; every other channel lives under <c>channels/&lt;pageId&gt;</c>. Before the first
    /// channel list is read, the primary channel is assumed to own the root.
    /// </summary>
    public string ChannelFolder(KodaProfile profile, string? pageId)
    {
        var root = Folder(profile);
        var isRoot = profile.RootChannelAssigned
            ? string.Equals(pageId, profile.RootChannelPageId, StringComparison.Ordinal)
            : pageId is null;
        if (isRoot)
        {
            return root;
        }

        var folder = Path.Combine(root, "channels", ChannelKey(pageId));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A page id is URL-safe; anything else is hashed so it can never escape the folder.</summary>
    private static string ChannelKey(string? pageId)
    {
        if (string.IsNullOrEmpty(pageId))
        {
            return "primary";
        }

        if (pageId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return pageId;
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pageId));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>Replaces the cached channel list; the channel the server marks selected owns the
    /// existing root folder the first time this runs.</summary>
    public void SetChannels(Guid id, AccountsList list)
    {
        if (Profile(id) is not { } profile)
        {
            return;
        }

        profile.Channels = [.. list.Channels.Select(channel => new KodaChannel
        {
            PageId = channel.PageId,
            ChannelId = channel.ChannelId,
            Name = channel.Name,
            Handle = channel.Handle,
            Photo = channel.Photo?.Url.ToString(),
            SigninUrl = channel.SigninUrl,
            DatasyncId = channel.DatasyncId,
            IsSelected = channel.IsSelected,
            SeenAt = DateTimeOffset.UtcNow,
        })];
        profile.AccountEmail = list.Email ?? profile.AccountEmail;
        profile.AccountName = list.Name ?? profile.AccountName;

        var selected = profile.RootChannelAssigned
            ? profile.Channels.FirstOrDefault(channel =>
                    string.Equals(channel.PageId, profile.ActiveChannelPageId, StringComparison.Ordinal))
                ?? profile.Channels.FirstOrDefault(channel => channel.IsSelected)
            : profile.Channels.FirstOrDefault(channel => channel.IsSelected);
        if (selected is not null)
        {
            profile.ActiveChannelPageId = selected.PageId;
            if (!profile.RootChannelAssigned)
            {
                profile.RootChannelPageId = selected.PageId;
                profile.RootChannelAssigned = true;
            }
        }

        // The menu's check mark follows the channel the app is on, not a server hint that
        // may still name the identity from the previous session.
        foreach (var channel in profile.Channels)
        {
            channel.IsSelected = string.Equals(channel.PageId, profile.ActiveChannelPageId, StringComparison.Ordinal);
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>Selects a channel and optionally refreshes its metadata, then points the profile
    /// at that channel's folder on the next <see cref="DataFolder"/> call.</summary>
    public void SetActiveChannel(Guid id, string? pageId, Action<KodaChannel>? update = null)
    {
        if (Profile(id) is not { } profile)
        {
            return;
        }

        var channel = profile.Channels.FirstOrDefault(candidate =>
            string.Equals(candidate.PageId, pageId, StringComparison.Ordinal));
        if (channel is not null)
        {
            update?.Invoke(channel);
            foreach (var other in profile.Channels)
            {
                other.IsSelected = ReferenceEquals(other, channel);
            }
        }

        profile.ActiveChannelPageId = pageId;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Where the profile's session is filed in the Credential Manager.</summary>
    public static string CredentialTarget(KodaProfile profile) =>
        profile.UsesRoot ? "Koda/YouTube" : $"Koda/YouTube/{profile.Id}";

    public KodaProfile AddDeviceProfile(string name)
    {
        var profile = new KodaProfile { Name = name.Trim() };
        _profiles.Add(profile);
        Save();
        Changed?.Invoke();
        return profile;
    }

    public void Update(Guid id, Action<KodaProfile> change)
    {
        if (Profile(id) is not { } profile) return;
        var before = JsonSerializer.Serialize(profile, Options);
        change(profile);
        if (before == JsonSerializer.Serialize(profile, Options)) return;
        Save();
        Changed?.Invoke();
    }

    public void SetActive(Guid id)
    {
        if (id == ActiveId || Profile(id) is null) return;
        PreviousId = ActiveId;
        ActiveId = id;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Deletes a profile that isn't active and everything it keeps on this PC. The last
    /// profile can't go. Its stored session is the caller's to clear.</summary>
    public bool Delete(Guid id)
    {
        if (!HasSeveral || id == ActiveId || Profile(id) is not { } profile) return false;
        _profiles.Remove(profile);
        if (PreviousId == id) PreviousId = null;
        Save();
        try
        {
            if (profile.UsesRoot)
            {
                foreach (var file in LibraryFiles)
                {
                    var path = Path.Combine(_root, file);
                    if (File.Exists(path)) File.Delete(path);
                }

                var channels = Path.Combine(_root, "channels");
                if (Directory.Exists(channels)) Directory.Delete(channels, recursive: true);
            }
            else
            {
                var folder = Path.Combine(_root, "Profiles", id.ToString());
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception)
        {
            // Files left behind are never read again.
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Small and rare: written at once, so a switch is on disk before anything reads it.
    /// A lone profile that was never joined by another writes nothing.</summary>
    private void Save()
    {
        if (!HasSeveral && !File.Exists(_file)) return;
        try
        {
            Directory.CreateDirectory(_root);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Saved { Profiles = _profiles, ActiveId = ActiveId }, Options));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception)
        {
            // The profiles still work this run; the next change writes them.
        }
    }
}
