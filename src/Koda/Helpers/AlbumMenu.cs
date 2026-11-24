using System;
using System.Linq;
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
/// The right-click menu of an album: play or shuffle it, add it to the queue,
/// save it to the Library, copy its link, and the way to its artists.
/// </summary>
public static class AlbumMenu
{
    /// <summary>Hook a list of covers (or rows) whose items are albums.</summary>
    public static void Attach(UIElement list) =>
        list.ContextRequested += (sender, args) =>
        {
            if (args.OriginalSource is FrameworkElement { DataContext: var context }
                && AlbumOf(context) is { } album)
            {
                Show((FrameworkElement)sender, args, album);
            }
        };

    /// <summary>Hook one element that speaks for a changing album, such as Search's top result.</summary>
    public static void Attach(UIElement host, Func<AlbumSummary?> album) =>
        host.ContextRequested += (sender, args) =>
        {
            if (album() is { } current)
            {
                Show((FrameworkElement)sender, args, current);
            }
        };

    public static AlbumSummary? AlbumOf(object? context) => context switch
    {
        AlbumSummary album => album,
        MusicItem.AlbumRecord record => record.Value,
        CardItem { Item: MusicItem.AlbumRecord record } => record.Value,
        _ => null,
    };

    public static MenuFlyout MenuFor(AlbumSummary album)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(SongMenu.Item("Play", "\uE768", () => _ = PlayAsync(album, shuffle: false)));
        menu.Items.Add(SongMenu.Item("Shuffle", "\uE8B1", () => _ = PlayAsync(album, shuffle: true)));
        menu.Items.Add(SongMenu.Item("Add to queue", "\uE8FD", () => _ = QueueAsync(album)));
        menu.Items.Add(new MenuFlyoutSeparator());

        var saved = App.Library.IsSavedAlbum(album.Id);
        menu.Items.Add(SongMenu.Item(saved ? "Remove from Library" : "Save to Library",
            saved ? "\uEB52" : "\uEB51", () => App.Library.ToggleSave(album)));
        menu.Items.Add(SongMenu.Item("Copy link", "\uE8C8", () => CopyLink(album)));

        var artists = album.Artists.Where(a => a.Id is not null).Take(3).ToArray();
        if (artists.Length > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var artist in artists)
            {
                menu.Items.Add(SongMenu.Item($"Go to {artist.Name}", "\uE77B",
                    () => Navigator.Go(typeof(Pages.ArtistPage), artist.Id)));
            }
        }

        return menu;
    }

    private static void Show(FrameworkElement host, ContextRequestedEventArgs args, AlbumSummary album)
    {
        var menu = MenuFor(album);
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

    private static async Task PlayAsync(AlbumSummary album, bool shuffle)
    {
        if (await SongsAsync(album) is not { } songs)
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

    private static async Task QueueAsync(AlbumSummary album)
    {
        if (await SongsAsync(album) is { } songs)
        {
            PlaybackService.Instance.AddToQueue(songs);
        }
    }

    /// <summary>The album's songs, fetched on the action rather than when the menu opens.</summary>
    private static async Task<Song[]?> SongsAsync(AlbumSummary album)
    {
        try
        {
            var songs = (await EngineHost.Music.AlbumAsync(album.Id)).Songs;
            if (songs.Length > 0)
            {
                return songs;
            }

            App.Shell?.Note("This album has no songs to play.");
        }
        catch (Exception error)
        {
            App.Shell?.Note($"{Errors.Title(error)}. {Errors.Describe(error)}");
        }

        return null;
    }

    private static void CopyLink(AlbumSummary album)
    {
        try
        {
            var package = new DataPackage();
            package.SetText($"https://music.youtube.com/browse/{album.Id}");
            Clipboard.SetContent(package);
            App.Shell?.Note("Link copied");
        }
        catch (Exception)
        {
            App.Shell?.Note("This album's link couldn't be copied.");
        }
    }
}
