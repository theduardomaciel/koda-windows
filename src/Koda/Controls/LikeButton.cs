using System;
using Koda.Core;
using Koda.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Koda.Controls;

/// <summary>
/// A song row's like button: an outline heart for one that isn't liked, the filled accent
/// heart for one that is. The state is read from <see cref="App.Library"/> and redrawn on its
/// changes, never kept here; the song comes from the row's DataContext through
/// <see cref="SongMenu.SongOf"/>, so it works for plain songs, tracks, history and stats rows.
/// </summary>
public sealed class LikeButton : Button
{
    /// <summary>Set on a list whose rows should not show like hearts (Home's Quick picks).
    /// The rows share their template with places that do show them, so the list, not the
    /// row, decides.</summary>
    public static readonly DependencyProperty HideInRowsProperty = DependencyProperty.RegisterAttached(
        "HideInRows", typeof(bool), typeof(LikeButton), new PropertyMetadata(false));

    public static void SetHideInRows(DependencyObject element, bool value) => element.SetValue(HideInRowsProperty, value);

    public static bool GetHideInRows(DependencyObject element) => (bool)element.GetValue(HideInRowsProperty);

    private readonly FontIcon _icon = new() { Glyph = "\uEB51", FontSize = 15 };
    private bool _attached;

    public LikeButton()
    {
        Width = 32;
        Height = 32;
        Padding = new Thickness(0);
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        BorderThickness = new Thickness(0);
        Content = _icon;
        Click += OnClick;
        Tapped += (_, args) => args.Handled = true;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => Render();
        Render();
    }

    private Song? CurrentSong => SongMenu.SongOf(DataContext);

    /// <summary>Whether a list above this row asked for hearts to stay hidden.</summary>
    private bool HiddenByAncestor()
    {
        for (var node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (GetHideInRows(node))
            {
                return true;
            }
        }

        return false;
    }

    private void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        App.Library.Changed += OnLibraryChanged;
        Render();
    }

    private void Detach()
    {
        _attached = false;
        App.Library.Changed -= OnLibraryChanged;
    }

    private void OnLibraryChanged() => DispatcherQueue?.TryEnqueue(Render);

    private void Render()
    {
        if (CurrentSong is not { } song || HiddenByAncestor())
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        var liked = App.Library.IsLiked(song);
        _icon.Glyph = liked ? "\uEB52" : "\uEB51";
        _icon.Foreground = (Brush)Application.Current.Resources[
            liked ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
        var label = liked ? "Remove from Liked" : "Add to Liked";
        ToolTipService.SetToolTip(this, label);
        AutomationProperties.SetName(this, label);
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (CurrentSong is { } song)
        {
            App.Library.ToggleLike(song);
        }
    }
}
