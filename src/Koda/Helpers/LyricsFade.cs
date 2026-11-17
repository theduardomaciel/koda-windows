using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Koda.Helpers;

/// <summary>
/// The lyrics fade: the panel is rendered into a composition surface and drawn back
/// through the white-to-transparent gradient in <c>mask</c>, so the words go transparent
/// at the edges instead of being darkened by a wash. Shared by Now Playing, whose
/// background is the blurred cover, and the inline drawer over the app's own surface.
/// </summary>
internal static class LyricsFade
{
    /// <summary>Builds the mask once the host has a XamlRoot. <paramref name="built"/> is the
    /// caller's guard, so a second layout pass does not build a second mask.</summary>
    public static void Apply(Grid view, FrameworkElement content, Border mask, ref bool built)
    {
        if (built || view.XamlRoot is null)
        {
            return;
        }

        built = true;
        var compositor = ElementCompositionPreview.GetElementVisual(view).Compositor;

        CompositionSurfaceBrush Capture(UIElement element)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var surface = compositor.CreateVisualSurface();
            surface.SourceVisual = visual;
            var size = compositor.CreateExpressionAnimation($"{nameof(visual)}.Size");
            size.SetReferenceParameter(nameof(visual), visual);
            surface.StartAnimation(nameof(surface.SourceSize), size);

            // The surface keeps the element's rendered pixels; the element itself is
            // hidden, so its copy is only ever seen through the mask.
            var brush = compositor.CreateSurfaceBrush(surface);
            visual.Opacity = 0;
            return brush;
        }

        var maskBrush = compositor.CreateMaskBrush();
        maskBrush.Source = Capture(content);
        maskBrush.Mask = Capture(mask);

        var sprite = compositor.CreateSpriteVisual();
        sprite.RelativeSizeAdjustment = Vector2.One;
        sprite.Brush = maskBrush;
        ElementCompositionPreview.SetElementChildVisual(view, sprite);
    }
}
