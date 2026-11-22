using System;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Koda.Helpers;

/// <summary>
/// The right-click menu of a playlist: play or shuffle it, add it to the queue, save an
/// online playlist to the Library and copy its link, or rename and delete one made here.
/// </summary>
public static class PlaylistMenu
{
    /// <summary>Hook a list of covers (or rows) whose items are playlists.</summary>
    public static void Attach(UIElement list) =>
        list.ContextRequested += (sender, args) =>
        {
            if (args.OriginalSource is FrameworkElement { DataContext: var context }
                && PlaylistOf(context) is { } playlist)
            {
                Show((FrameworkElement)sender, args, playlist);
            }
        };

    /// <summary>Hook one element that speaks for a changing online playlist, such as Search's top result.</summary>
    public static void Attach(UIElement host, Func<PlaylistSummary?> playlist) =>
        host.ContextRequested += (sender, args) =>
        {
            if (playlist() is { } current)
            {
                Show((FrameworkElement)sender, args, new PlaylistTarget(Online: current, Mine: null));
            }
        };

    public static PlaylistTarget? PlaylistOf(object? context) => context switch
    {
        PlaylistSummary playlist => new PlaylistTarget(playlist, null),
        UserPlaylist playlist => new PlaylistTarget(null, playlist),
        MusicItem.PlaylistRecord record => new PlaylistTarget(record.Value, null),
        MusicItem.UserPlaylistRecord record => new PlaylistTarget(null, record.Value),
        CardItem { Item: MusicItem.PlaylistRecord record } => new PlaylistTarget(record.Value, null),
        CardItem { Item: MusicItem.UserPlaylistRecord record } => new PlaylistTarget(null, record.Value),
        Pages.LibraryPage.UserPlaylistRow row => new PlaylistTarget(null, row.Playlist),
        _ => null,
    };

    /// <summary>The two kinds of playlist share a shape but not their actions.</summary>
    public sealed record PlaylistTarget(PlaylistSummary? Online, UserPlaylist? Mine);

    public static MenuFlyout MenuFor(PlaylistSummary playlist)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(SongMenu.Item("Play", "\uE768", () => _ = PlayAsync(playlist, shuffle: false)));
        menu.Items.Add(SongMenu.Item("Shuffle", "\uE8B1", () => _ = PlayAsync(playlist, shuffle: true)));
        menu.Items.Add(SongMenu.Item("Add to queue", "\uE71C", () => _ = QueueAsync(playlist)));
        menu.Items.Add(new MenuFlyoutSeparator());

        var saved = App.Library.IsSavedPlaylist(playlist.Id);
        menu.Items.Add(SongMenu.Item(saved ? "Remove from Library" : "Save to Library",
            saved ? "\uEB52" : "\uEB51", () => App.Library.ToggleSave(playlist)));
        menu.Items.Add(SongMenu.Item("Copy link", "\uE8C8", () => CopyLink(playlist)));

        return menu;
    }

    public static MenuFlyout MenuFor(UserPlaylist playlist)
    {
        var menu = new MenuFlyout();
        var hasSongs = playlist.Songs.Length > 0;
        var play = SongMenu.Item("Play", "\uE768", () => Play(playlist, shuffle: false));
        play.IsEnabled = hasSongs;
        menu.Items.Add(play);
        var shuffle = SongMenu.Item("Shuffle", "\uE8B1", () => _ = PlaybackService.Instance.ShuffleAsync(playlist.Songs));
        shuffle.IsEnabled = hasSongs;
        menu.Items.Add(shuffle);
        var queue = SongMenu.Item("Add to queue", "\uE71C", () => PlaybackService.Instance.AddToQueue(playlist.Songs));
        queue.IsEnabled = hasSongs;
        menu.Items.Add(queue);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(SongMenu.Item("Rename…", "\uE70F", () => _ = Dialogs.RenamePlaylistAsync(playlist)));
        menu.Items.Add(SongMenu.Item("Delete playlist…", "\uE74D", () => _ = DeleteAsync(playlist)));

        return menu;
    }

    private static void Show(FrameworkElement host, ContextRequestedEventArgs args, PlaylistTarget playlist)
    {
        var menu = playlist.Online is { } online ? MenuFor(online) : MenuFor(playlist.Mine!);
        if (args.TryGetPosition(host, out var position))
        {
            menu.ShowAt(host, position);
        }
        else
        {
            menu.ShowAt(host);
        }

        args.Handled = true;
    }

    private static void Play(UserPlaylist playlist, bool shuffle)
    {
        if (playlist.Songs.Length == 0)
        {
            return;
        }

        if (shuffle)
        {
            _ = PlaybackService.Instance.ShuffleAsync(playlist.Songs);
        }
        else
        {
            _ = PlaybackService.Instance.PlayNowAsync(playlist.Songs[0], playlist.Songs);
        }
    }

    private static async Task PlayAsync(PlaylistSummary playlist, bool shuffle)
    {
        if (await SongsAsync(playlist) is not { } songs)
        {
            return;
        }

        if (shuffle)
        {
            await PlaybackService.Instance.ShuffleAsync(songs);
        }
        else
        {
            await PlaybackService.Instance.PlayNowAsync(songs[0], songs);
        }
    }

    private static async Task QueueAsync(PlaylistSummary playlist)
    {
        if (await SongsAsync(playlist) is { } songs)
        {
            PlaybackService.Instance.AddToQueue(songs);
        }
    }

    /// <summary>The playlist's songs, fetched on the action rather than when the menu opens.</summary>
    private static async Task<Song[]?> SongsAsync(PlaylistSummary playlist)
    {
        try
        {
            var songs = await EngineHost.Music.PlaylistSongsAsync(playlist.Id);
            if (songs.Length > 0)
            {
                return songs;
            }

            App.Shell?.Note("This playlist has no songs to play.");
        }
        catch (Exception error)
        {
            App.Shell?.Note($"{Errors.Title(error)}. {Errors.Describe(error)}");
        }

        return null;
    }

    private static void CopyLink(PlaylistSummary playlist)
    {
        try
        {
            var id = playlist.Id.StartsWith("VL", StringComparison.Ordinal) ? playlist.Id[2..] : playlist.Id;
            var package = new DataPackage();
            package.SetText($"https://music.youtube.com/playlist?list={id}");
            Clipboard.SetContent(package);
            App.Shell?.Note("Link copied");
        }
        catch (Exception)
        {
            App.Shell?.Note("This playlist's link couldn't be copied.");
        }
    }

    private static async Task DeleteAsync(UserPlaylist playlist)
    {
        if (await Dialogs.ConfirmAsync($"Delete “{playlist.Name}”?",
                "The songs stay wherever else you saved them.", "Delete Playlist"))
        {
            App.Library.DeletePlaylist(playlist.Id);
        }
    }
}
