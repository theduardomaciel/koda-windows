using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Koda.Core.Profiles;
using Koda.Interop;
using Koda.Pages;
using Koda.Playback;

namespace Koda.Services;

/// <summary>
/// The app's side of profiles: the roster, and moving the library and session between them.
/// Switching is local and instant; whatever an account needs follows behind it.
/// </summary>
public static class Profiles
{
    private static bool _switching;
    private static Task<Koda.Core.AccountsList?>? _channelsRefresh;
    private static Guid _channelsRefreshProfile;

    public static ProfileRoster Roster { get; } = new(App.DataDirectory);

    /// <summary>Why the last channel read had nothing, in the user's words. Null when it worked.</summary>
    public static string? LastChannelsError { get; private set; }

    public static string ActiveFolder => Roster.DataFolder(Roster.Active);

    /// <summary>Points the music client at the active channel's brand identity. Call before an
    /// account sync or after the roster selects a channel.</summary>
    public static void ApplyChannelContext(KodaProfile? profile = null)
    {
        profile ??= Roster.Active;
        EngineHost.Music.OnBehalfOfUser = profile.IsSignedIn ? profile.ActiveChannelPageId : null;
    }

    public static Koda.Core.ISecretStore ActiveCredentials => new CredentialStore(ProfileRoster.CredentialTarget(Roster.Active));

    /// <summary>The active profile keeps up with its account: who it is, or that it needs signing in.</summary>
    public static void FollowAccount()
    {
        if (_switching) return;
        var account = App.Account;
        var signedIn = account.IsSignedIn;
        var info = account.Info;
        var wasSignedIn = Roster.Active.IsSignedIn;
        Roster.Update(Roster.ActiveId, profile =>
        {
            profile.Kind = signedIn ? ProfileKind.YouTube : ProfileKind.Device;
            profile.IsExpired = signedIn && account.IsExpired;
            if (!signedIn)
            {
                profile.Handle = null;
                profile.AccountPhoto = null;
                profile.DatasyncId = null;
                return;
            }

            if (info is null) return;
            if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = info.Name;
            profile.Handle = info.Handle ?? profile.Handle;
            profile.AccountPhoto = info.Photo?.Url.ToString() ?? profile.AccountPhoto;
            profile.DatasyncId = info.DatasyncId ?? profile.DatasyncId;

            // The active channel is the one the account menu named; refresh its cached row.
            if (info.ChannelId is { } channelId)
            {
                var channel = profile.Channels.FirstOrDefault(c =>
                    string.Equals(c.ChannelId, channelId, StringComparison.Ordinal));
                if (channel is null)
                {
                    channel = profile.Channels.FirstOrDefault(c =>
                        string.Equals(c.Name, info.Name, StringComparison.OrdinalIgnoreCase));
                }

                if (channel is not null)
                {
                    channel.ChannelId = channelId;
                    channel.Name = info.Name;
                    channel.Handle = info.Handle ?? channel.Handle;
                    channel.Photo = info.Photo?.Url.ToString() ?? channel.Photo;
                    profile.ActiveChannelPageId = channel.PageId;
                }
            }
        });

        // A Home saved for the other state is the wrong person's: signed in, it is the account's.
        if (wasSignedIn != signedIn) ForgetSavedHome();

        // Before the channel list exists, the engine may already have selected the brand at
        // sign-in; clearing it here would pull the primary's library into the brand's folder.
        if (!signedIn || Roster.Active.Channels.Count > 0)
        {
            ApplyChannelContext();
        }

        // The channel list is read once per session in the background; the menu and the
        // title bar use what is cached until it answers.
        if (signedIn && Roster.Active.Channels.Count == 0)
        {
            _ = RefreshChannelsAsync();
        }
    }

    /// <summary>Reads the channels of the active account and caches them on the roster.</summary>
    public static Task<Koda.Core.AccountsList?> RefreshChannelsAsync(CancellationToken cancellation = default)
    {
        if (!App.Account.IsSignedIn || _switching)
        {
            return Task.FromResult<Koda.Core.AccountsList?>(null);
        }

        var id = Roster.ActiveId;
        if (_channelsRefresh is { IsCompleted: false } && _channelsRefreshProfile == id)
        {
            return _channelsRefresh;
        }

        _channelsRefreshProfile = id;
        _channelsRefresh = RefreshChannelsCoreAsync(id, cancellation);
        return _channelsRefresh;
    }

    private static async Task<Koda.Core.AccountsList?> RefreshChannelsCoreAsync(Guid profileId, CancellationToken cancellation)
    {
        var scope = EngineHost.Music.Accounts.CacheScope;
        try
        {
            var list = await EngineHost.Music.AccountsListAsync(cancellation);
            // A channel switch in the meantime bumped the session; its answer is the one to keep.
            if (Roster.ActiveId != profileId || EngineHost.Music.Accounts.CacheScope != scope)
            {
                return null;
            }

            var previousBrand = EngineHost.Music.OnBehalfOfUser;
            Roster.SetChannels(profileId, list);
            LastChannelsError = list.Channels.Length == 0 ? "no channels in the answer" : null;
            ApplyChannelContext(Roster.Profile(profileId));

            // The channel context changed from primary to a brand (or the reverse); bring
            // that channel's library over. Opening the menu on the same channel does not.
            if (!string.Equals(previousBrand, EngineHost.Music.OnBehalfOfUser, StringComparison.Ordinal))
            {
                _ = App.Account.SyncAsync(force: true);
            }
            if (await SyncActiveFolderAsync())
            {
                App.Shell?.NavigateRoot(typeof(HomePage), null, "home", fresh: true);
            }

            return list;
        }
        catch (Koda.Core.MusicException error)
        {
            LastChannelsError = error.Kind switch
            {
                Koda.Core.MusicException.Reason.SignedOut => "sign in again",
                Koda.Core.MusicException.Reason.Network => "network",
                Koda.Core.MusicException.Reason.RateLimited => "YouTube asked Koda to wait",
                _ => "unexpected answer",
            };
            System.Diagnostics.Debug.WriteLine($"channels: {error.Kind}");
            return null;
        }
        catch (Exception error)
        {
            LastChannelsError = "unexpected answer";
            System.Diagnostics.Debug.WriteLine($"channels: {error.Message}");
            return null;
        }
    }

    /// <summary>The channel list can name a different channel than the folder opened at
    /// startup; move the library to the channel that is really selected.</summary>
    private static async Task<bool> SyncActiveFolderAsync()
    {
        if (!App.Account.IsSignedIn || _switching)
        {
            return false;
        }

        var folder = Roster.DataFolder(Roster.Active);
        if (string.IsNullOrEmpty(folder) ||
            string.Equals(folder, App.Library.OpenFolder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            await App.Account.SwitchAsync(folder, ActiveCredentials, () => App.Library.Open(folder));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ForgetSavedHome()
    {
        try
        {
            File.Delete(Path.Combine(ActiveFolder, "home-snapshot.json"));
        }
        catch (Exception)
        {
            // The next load replaces it anyway.
        }
    }

    /// <summary>The same account: by YouTube's id when it gave one, else by its @handle.</summary>
    private static bool SameAccount(KodaProfile a, KodaProfile b) =>
        (a.DatasyncId is not null && a.DatasyncId == b.DatasyncId)
        || (a.Handle is not null && string.Equals(a.Handle, b.Handle, StringComparison.OrdinalIgnoreCase));

    /// <summary>Another profile takes over: its library everywhere at once, its session or none.
    /// Music keeps playing; a video belongs to the profile it was started in, so it closes.</summary>
    public static async Task SwitchAsync(Guid id)
    {
        if (id == Roster.ActiveId || Roster.Profile(id) is not { } profile || _switching) return;
        _switching = true;
        try
        {
            VideoPlaybackService.Instance.Stop();
            var folder = Roster.DataFolder(profile);
            ApplyChannelContext(profile);
            try
            {
                await App.Account.SwitchAsync(
                    folder,
                    new CredentialStore(ProfileRoster.CredentialTarget(profile)),
                    () => App.Library.Open(folder));
            }
            catch
            {
                ApplyChannelContext();
                throw;
            }

            Roster.SetActive(id);
        }
        finally
        {
            _switching = false;
        }

        FollowAccount();
        App.Shell?.NavigateRoot(typeof(HomePage), null, "home", fresh: true);
        App.Shell?.Note($"Switched to {profile.DisplayName}");
    }

    /// <summary>Back to the profile before this one.</summary>
    public static Task SwitchBackAsync() =>
        Roster.PreviousId is { } previous ? SwitchAsync(previous)
        : Roster.Profiles.FirstOrDefault(p => p.Id != Roster.ActiveId) is { } other ? SwitchAsync(other.Id)
        : Task.CompletedTask;

    /// <summary>
    /// Another channel of the active account takes over: the cookies are the ones the switch
    /// returned, and the library moves to that channel's folder. Music keeps playing.
    /// </summary>
    public static async Task SwitchChannelAsync(KodaChannel channel, Koda.Core.YouTubeCookies cookies)
    {
        var active = Roster.Active;
        if (!active.IsSignedIn || _switching)
        {
            return;
        }

        // The folder that owns the existing data must be settled before the identity moves.
        if (!active.RootChannelAssigned)
        {
            await RefreshChannelsAsync();
            active = Roster.Active;
            if (!active.RootChannelAssigned)
            {
                App.Shell?.Note("Channels are still loading. Try again in a moment.");
                return;
            }
        }

        _switching = true;
        try
        {
            VideoPlaybackService.Instance.Stop();
            var folder = Roster.ChannelFolder(active, channel.PageId);
            EngineHost.Music.OnBehalfOfUser = channel.PageId;
            try
            {
                await App.Account.SwitchChannelAsync(
                    folder,
                    ActiveCredentials,
                    cookies,
                    () => App.Library.Open(folder));
            }
            catch
            {
                ApplyChannelContext(active);
                throw;
            }

            Roster.SetActiveChannel(Roster.ActiveId, channel.PageId, cached =>
            {
                cached.ChannelId = channel.ChannelId ?? cached.ChannelId;
                cached.Name = string.IsNullOrWhiteSpace(channel.Name) ? cached.Name : channel.Name;
                cached.Handle = channel.Handle ?? cached.Handle;
                cached.Photo = channel.Photo ?? cached.Photo;
                cached.SigninUrl = channel.SigninUrl ?? cached.SigninUrl;
            });
        }
        finally
        {
            _switching = false;
        }

        FollowAccount();
        App.Shell?.NavigateRoot(typeof(HomePage), null, "home", fresh: true);
        App.Shell?.Note($"Switched to {channel.DisplayName}");
    }

    public static async Task DeleteAsync(Guid id)
    {
        if (Roster.Profile(id) is not { } profile || !Roster.HasSeveral) return;
        if (id == Roster.ActiveId)
        {
            await SwitchBackAsync();
        }

        var credentials = new CredentialStore(ProfileRoster.CredentialTarget(profile));
        if (Roster.Delete(id))
        {
            try
            {
                credentials.Write(null);
            }
            catch (Exception)
            {
                // A stored session with no profile is never read again.
            }
        }
    }

    /// <summary>A new profile signed in to an account. Giving up on the sign-in leaves everything
    /// as it was, and an account already here goes to its own profile rather than a second one.</summary>
    public static async Task AddSignedInAsync()
    {
        var before = Roster.ActiveId;
        var added = Roster.AddDeviceProfile("");
        await SwitchAsync(added.Id);
        await YouTubeSignInDialog.ShowSignInAsync();

        if (!App.Account.IsSignedIn)
        {
            await SwitchAsync(before);
            await DeleteAsync(added.Id);
            return;
        }

        FollowAccount();
        var channels = await RefreshChannelsAsync();
        var signedIn = Roster.Profile(added.Id);
        var twin = signedIn is null ? null : Roster.Profiles.FirstOrDefault(p => p.Id != added.Id && SameAccount(p, signedIn));
        if (twin is not null)
        {
            // The fresh session and the channel list belong to the account that was already here.
            if (ActiveCredentials.Read() is { } fresh)
            {
                new CredentialStore(ProfileRoster.CredentialTarget(twin)).Write(fresh);
            }

            if (channels is not null)
            {
                Roster.SetChannels(twin.Id, channels);
            }

            await SwitchAsync(twin.Id);
            await RefreshChannelsAsync();
            await DeleteAsync(added.Id);
            App.Shell?.Note($"That account is already {twin.DisplayName}");
            return;
        }

        ForgetSavedHome();
        App.Shell?.NavigateRoot(typeof(HomePage), null, "home", fresh: true);
    }
}
