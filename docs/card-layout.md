# Card and shelf layout

How cover cards are sized and spaced, and how to retune them without breaking the
alignment of covers, hover backgrounds, titles and loading skeletons.

The short version:

- **Content padding** is on the item container (`CardItemStyle`,
  `LibraryCardItemStyle` or `CardGridItemStyle`).
- **Outer padding** is on the `ListView` / `GridView` that holds the items.
- **Gaps** are the item container's `Margin`.
- **Hover insets** are owned by WinUI, not by the app. On Windows 11 a
  `ListViewItem`'s rounded hover backplate is inset 4 px horizontally and 2 px
  vertically inside the item cell. A `GridViewItem`'s hover fills the cell.

## Where the values live

| Value | File | What it does |
|---|---|---|
| `Padding` | `src/Koda/App.xaml`, `CardItemStyle`, `LibraryCardItemStyle` / `CardGridItemStyle` | Space inside each item cell, between the cell edge and the cover. Also controls how much hover background surrounds the cover. |
| `Margin` | Same styles | Space between item cells. In a shelf this is the horizontal gap; in a grid it is the column/row gap. |
| `ContentInset` | `src/Koda/Controls/ShelvesView.xaml.cs` | Inset of a shelf's title and covers from the control's edges (`32` by default). Pages that already pad their content set it to `0`. |
| `ListView.Padding` / `Margin` | `src/Koda/Controls/ShelvesView.xaml.cs` | Outer space around a shelf row, calculated from `ContentInset` and the card metrics. |
| `GridView.Margin` / `GridView.Padding` | `src/Koda/Pages/LibraryGridPage.xaml` | Outer alignment and available width of the library grid. |
| `StackPanel Spacing` | `ShelvesView.xaml` | Vertical distance between shelves. |
| `Grid RowSpacing` | `ShelvesView.xaml` | Distance between a shelf title and its cards. |
| Header buttons `Margin` | `ShelvesView.xaml` | Right edge of the scroll buttons. Keep it aligned with the cover inset. |
| `CoverRadius` | `src/Koda/App.xaml` | Corner radius of the cover image (`12`). |
| `ListViewItemCornerRadius` / `GridViewItemCornerRadius` | `src/Koda/App.xaml` | Corner radius of the native hover fill (currently `8`). App-wide. |
| Card width / cover size | `src/Koda/App.xaml`, `SquareCardTemplate` / `RoundCardTemplate` | Card content width and cover size (`160`). |
| Skeleton spacing | `src/Koda/Helpers/Skeletons.cs`, `Skeletons.Shelves` | Loading placeholders must mirror the real shelf metrics. |

## Current values

Shelf cards (`ListViewItem`):

```xml
<Style x:Key="CardItemStyle" TargetType="ListViewItem" ...>
    <Setter Property="Padding" Value="12" />
    <Setter Property="Margin" Value="0,0,-3,0" />
    <Setter Property="MinWidth" Value="0" />
    <Setter Property="VerticalContentAlignment" Value="Top" />
</Style>
```

Shelf list (`ShelvesView.xaml`, default `ContentInset="32"`):

```xml
<StackPanel Spacing="22" />          <!-- between shelves -->
<Grid RowSpacing="2">                <!-- title to cards -->
<StackPanel ... Margin="0,0,32,0">   <!-- scroll buttons -->
<ListView Padding="20,0,23,0" ...>
```

`ShelvesView` calculates the title margin, list padding and scroll-button
margin from `ContentInset`. With the default `32` the values are the ones
shown above; with `ContentInset="0"` the title and covers sit on the control's
edges, and the list's outer space moves into a negative margin so the hover
bleed can extend into the page's own padding.

Library grid (`GridViewItem`):

```xml
<Style x:Key="LibraryCardItemStyle" TargetType="GridViewItem" BasedOn="{StaticResource CardGridItemStyle}">
    <Setter Property="Padding" Value="8,10" />
    <Setter Property="Margin" Value="0,0,5,4" />
</Style>
```

Library page (`LibraryGridPage.xaml`):

```xml
<Grid Padding="26,16,26,0" RowSpacing="10">
<GridView Margin="-8,0" Padding="0,0,4,28" ... />
```

The Browse mood grid still uses `CardGridItemStyle` (`Padding=6`,
`Margin=0,0,6,0`).

Skeleton (`Skeletons.Shelves`): shelf spacing `22`, card row spacing `21`,
card width `160`.

## The formulas

Let:

- `T` = target inset of the **cover** from the window edge. For shelves this is
  `32`, the title's left inset.
- `P` = item container `Padding` on that side.
- `M` = item container `Margin` on that side. Left is `0`, right is `-3` for
  shelves.
- `C` = cover corner radius (`12`).
- `H` = native hover inset. For `ListViewItem` on Windows 11, `H = 4`
  horizontally and `2` vertically. For `GridViewItem`, `H = 0`.

### Outer padding

```text
outer padding = T - P - M
```

For the current shelf values:

- Left: `32 - 12 - 0 = 20`
- Right: `32 - 12 - (-3) = 23`

That is why the shelf uses `Padding="20,0,23,0"`. The negative right margin is
part of the calculation: ignoring it makes the right side 3 px too far out.

### What the numbers produce

```text
cover inset              = outer padding + P + M
visible hover inset      = outer padding + H + M
cover gap                = 2 * P + M
visible hover gap        = M + 2 * H
item pitch               = card width + 2 * P + M
```

Current shelf results:

| Measurement | Result |
|---|---|
| Cover inset, left | `20 + 12 + 0 = 32` |
| Cover inset, right | `23 + 12 - 3 = 32` |
| Visible hover inset, left | `20 + 4 + 0 = 24` |
| Visible hover inset, right | `23 + 4 - 3 = 24` |
| Cover gap | `2 * 12 - 3 = 21` |
| Visible hover gap | `-3 + 2 * 4 = 5` |
| Item pitch | `160 + 2 * 12 - 3 = 181` |

`GridViewItem` has no hover inset, so its visible hover gap is just `Margin`.

### Solving for a desired gap

```text
Margin.Right = desired cover gap - 2 * Padding
Margin.Right = desired visible hover gap - 2 * H
```

For example, a `5 px` visible hover gap with `H = 4` needs
`Margin.Right = 5 - 8 = -3`.

### Vertical spacing

```text
title-to-cover      = RowSpacing + Padding.Top
text-to-next-title  = StackPanel.Spacing + Padding.Bottom
hover-to-next-title = StackPanel.Spacing
```

The hover background fills the item cell, so the item padding is inside the
hover. To preserve a visual rhythm after changing padding, adjust the outer
spacing by the same delta.

Current values:

- Title-to-cover: `2 + 12 = 14`
- Text-to-next-title: `22 + 12 = 34`

## Changing the values

1. Decide the target cover inset `T` (usually `32`, the title inset).
2. Decide the item `Padding` (`P`) and gap (`Margin`, `M`).
   - A good default is `P = C`, the cover radius.
   - For shelves, `M` is negative to compensate for the native hover inset.
3. Compute the outer padding per side: `T - P - M`.
4. Update `CardItemStyle` and `ShelvesView`'s `ContentInset` (its default is the
   shelf target `T = 32`). For the library grid, update `LibraryCardItemStyle`
   and `LibraryGridPage.xaml`.
5. Keep the scroll buttons' right margin equal to `T`; `ContentInset` applies
   it to both the title and the buttons.
6. Recompute vertical spacing if you changed `P`:
   - `RowSpacing = desired title-to-cover - P.Top`
   - `StackPanel.Spacing = desired text-to-next-title - P.Bottom`
7. Update `Skeletons.Shelves`:
   - shelf spacing = `StackPanel.Spacing`
   - card row spacing = `2 * P + M`
8. Build and check the first/last cover alignment and the hover gap.

### Worked example: `Padding` 12 -> 14, same `-3` margin

- Left outer padding: `32 - 14 - 0 = 18`
- Right outer padding: `32 - 14 - (-3) = 21`
- `ListView.Padding="18,0,21,0"`
- Cover gap: `2 * 14 - 3 = 25`
- Visible hover gap: still `5`
- Skeleton row spacing: `25`
- To keep title-to-cover at `14`: `RowSpacing = 0`
- To keep text-to-next-title at `34`: `StackPanel.Spacing = 20`
- Scroll buttons stay at `32`

## Grid-specific notes

`LibraryCardItemStyle` is the album/playlist/artist grid. `CardGridItemStyle`
remains the Browse mood-tile style. `GridViewItem` hover fills the cell, so a
negative margin would make hover backgrounds overlap rather than close a native
inset.

### Matching a shelf card in a GridView

To reproduce a `ListView` shelf's visible card edges in a `GridView`, strip the
native `ListViewItem` hover inset out of the padding and margin:

```text
P_grid_h = P_shelf_h - H_shelf_h        = 12 - 4 = 8
M_grid_h = M_shelf_h + 2 * H_shelf_h    = -3 + 8 = 5
P_grid_v = P_shelf_v - H_shelf_v        = 12 - 2 = 10
M_grid_v = M_shelf_v + 2 * H_shelf_v    = 0 + 4 = 4
```

That produces the same visible result:

| Measurement | Shelf | Library grid |
|---|---|---|
| Cover gap, horizontal | `2 * 12 - 3 = 21` | `2 * 8 + 5 = 21` |
| Visible hover gap, horizontal | `-3 + 2 * 4 = 5` | `5` |
| Visible hover gap, vertical | `0 + 2 * 2 = 4` | `4` |
| Hover extension beyond cover, horizontal | `12 - 4 = 8` | `8` |
| Hover extension beyond cover, vertical | `12 - 2 = 10` | `10` |
| Item pitch, horizontal | `160 + 2 * 12 - 3 = 181` | `160 + 2 * 8 + 5 = 181` |

To keep the first grid cover aligned with the page padding, set
`GridView.Margin.Left = -P_grid_h` (`-8`). If you also need to preserve the
original available width, add right padding equal to twice the horizontal
padding increase (`4` here; from `6` to `8`). The current `LibraryGridPage` uses
`Margin="-8,0"` and `Padding="0,0,4,28"`.

When the vertical item padding changes from `6` to `10`, the first cover moves
down `4 px`. Reduce the page `RowSpacing` by the same amount (`14` to `10`) to
keep the first row where it was.

## Corner radius

- `CoverRadius` controls the cover image. The item padding is independent, but
  `Padding = CoverRadius` is a useful visual rule.
- `ListViewItemCornerRadius` and `GridViewItemCornerRadius` control the native
  hover fill. They are app-wide theme resources; changing them affects catalog
  rows, video lists, etc.
- Pure native styling cannot set a separate hover radius for cards only. That
  requires a custom item template, which was tried and removed because it
  noticeably slowed the interface.

## Scope warning

`CardItemStyle` is used by every `ShelvesView`: Home, Album versions, Artist
releases, Mood, and Search. Changing it changes all shelves, not just Home.
`LibraryCardItemStyle` affects the Library albums, playlists and artists grid.
`CardGridItemStyle` affects the Browse mood grid.

`ShelvesView.xaml` hard-codes `ItemContainerStyle="{StaticResource
CardItemStyle}"`. If Home ever needs different metrics, add a configurable
style/DP to `ShelvesView` rather than duplicating the control.

Home leaves `ContentInset` at `32`, because its shelves are full-bleed. Pages
that wrap a `ShelvesView` in their own 26 px content margin (Artist, Album,
Mood, Search) set `ContentInset="0"`, so their shelf titles and covers share
that page margin instead of adding another 32 px.
