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
/// The right-click menu of an artist: play or shuffle their top songs, start their radio,
/// add them to the queue, follow them, and copy their link.
/// </summary>
public static class ArtistMenu
{
    /// <summary>Hook a list of covers (or rows) whose items are artists.</summary>
    public static void Attach(UIElement list) =>
        list.ContextRequested += (sender, args) =>
        {
            if (args.OriginalSource is FrameworkElement { DataContext: var context }
                && ArtistOf(context) is { } artist)
            {
                Show((FrameworkElement)sender, args, artist);
            }
        };

    /// <summary>Hook one element that speaks for a changing artist, such as Search's top result.</summary>
    public static void Attach(UIElement host, Func<ArtistSummary?> artist) =>
        host.ContextRequested += (sender, args) =>
        {
            if (artist() is { } current)
            {
                Show((FrameworkElement)sender, args, current);
            }
        };

    public static ArtistSummary? ArtistOf(object? context) => context switch
    {
        ArtistSummary artist => artist,
        MusicItem.ArtistRecord record => record.Value,
        CardItem { Item: MusicItem.ArtistRecord record } => record.Value,
        _ => null,
    };

    public static MenuFlyout MenuFor(ArtistSummary artist)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(SongMenu.Item("Play", "\uE768", () => _ = PlayAsync(artist, shuffle: false)));
        menu.Items.Add(SongMenu.Item("Shuffle", "\uE8B1", () => _ = PlayAsync(artist, shuffle: true)));
        menu.Items.Add(SongMenu.Item("Start radio", "\uE8FE", () => _ = RadioAsync(artist)));
        menu.Items.Add(SongMenu.Item("Add to queue", "\uE71C", () => _ = QueueAsync(artist)));
        menu.Items.Add(new MenuFlyoutSeparator());

        var followed = App.Library.IsSavedArtist(artist.Id);
        menu.Items.Add(SongMenu.Item(followed ? "Unfollow" : "Follow",
            followed ? "\uEB52" : "\uEB51", () => App.Library.ToggleSave(artist)));
        menu.Items.Add(SongMenu.Item("Copy link", "\uE8C8", () => CopyLink(artist)));

        return menu;
    }

    private static void Show(FrameworkElement host, ContextRequestedEventArgs args, ArtistSummary artist)
    {
        var menu = MenuFor(artist);
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

    private static async Task PlayAsync(ArtistSummary artist, bool shuffle)
    {
        if (await SongsAsync(artist) is not { } songs)
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

    private static async Task RadioAsync(ArtistSummary artist)
    {
        if (await SongsAsync(artist) is { } songs)
        {
            await PlaybackService.Instance.StartRadioAsync(songs[0]);
        }
    }

    private static async Task QueueAsync(ArtistSummary artist)
    {
        if (await SongsAsync(artist) is { } songs)
        {
            PlaybackService.Instance.AddToQueue(songs);
        }
    }

    /// <summary>The artist's top songs, fetched on the action rather than when the menu opens.</summary>
    private static async Task<Song[]?> SongsAsync(ArtistSummary artist)
    {
        try
        {
            var songs = (await EngineHost.Music.ArtistAsync(artist.Id)).TopSongs;
            if (songs.Length > 0)
            {
                return songs;
            }

            App.Shell?.Note("This artist has no songs to play.");
        }
        catch (Exception error)
        {
            App.Shell?.Note($"{Errors.Title(error)}. {Errors.Describe(error)}");
        }

        return null;
    }

    private static void CopyLink(ArtistSummary artist)
    {
        try
        {
            var package = new DataPackage();
            package.SetText($"https://music.youtube.com/channel/{artist.Id}");
            Clipboard.SetContent(package);
            App.Shell?.Note("Link copied");
        }
        catch (Exception)
        {
            App.Shell?.Note("This artist's link couldn't be copied.");
        }
    }
}
