# Rich search suggestions ("quick answers")

Status: **implemented** (November 2026). The plan below is the behavior the code now
follows; keep it in sync when the ranking or row template changes.

The title-bar search currently suggests only query text. The goal is to promote a
high-confidence track, album or artist into the suggestion list, styled as a compact
search-result row, while keeping the existing query suggestions as the fallback.

The reference is YouTube Music's rich suggestion row. We can do better on Windows:
make the result kind explicit, highlight the matched query, keep the row scannable,
and make the whole thing work with the keyboard and a screen reader.

## What we already have

`YouTubeMusic.SuggestionsAsync` calls `music/get_search_suggestions` and keeps only
the text:

```csharp
var root = await _api.MusicAsync("music/get_search_suggestions",
    new JsonObject { ["input"] = query }, cancellation);
return [.. root.Objects("searchSuggestionRenderer")
    .Select(s => s["suggestion"].Text())
    .Where(s => s.Length > 0)];
```

The same response already contains a second section of rich
`musicResponsiveListItemRenderer` rows. Verified live with the current WEB_REMIX
client (November 2026):

| Query | Rich rows returned by YouTube |
|---|---|
| `city walls` | Song "City Walls" (twenty one pilots, album Breach), its video, and "Bandito" |
| `breach` | Album "Breach" (twenty one pilots) and an unrelated "Security Breach" rap song |
| `trench` | Album "Trench" (twenty one pilots) and three songs that only contain the word "Trench" |
| `twenty one pilots` | Artist "Twenty One Pilots" and three songs by the artist |

This is important: **rich suggestions need no extra network call.** The response is
already larger than the text-only parse; we are simply ignoring it today.

`Rows.ItemFromListRow` already parses these rows into `MusicItem.SongRecord`,
`AlbumRecord`, `ArtistRecord` and `PlaylistRecord`, including artwork and page links.
That means the implementation is mostly parsing, ranking and presentation.

## Target UX

```
┌──────────────────────────────────────────────────────────┐
│ [art]  City Walls                                    ▶   │
│        ♪ Song · twenty one pilots · Breach               │
│ [art]  Trench                                        ›   │
│        ◉ Album · twenty one pilots · 2018                │
│ [art]  Twenty One Pilots                             ›   │
│        ♟ Artist · 50.8M monthly audience                 │
│ ──────────────────────────────────────────────────────── │
│ 🔍  city walls twenty one pilots                         │
│ 🔍  city walls live                                      │
│ 🔍  city walls slowed                                    │
│ 🔍  See all results for “city walls”                     │
└──────────────────────────────────────────────────────────┘
```

Rules:

- **Entities first, text second, "See all results" last.**
- At most **3 entity rows**. A wrong rich answer is worse than a plain text
  suggestion.
- At most **4 query rows** plus the footer. Total rows never exceed 8.
- Rich row height is 52 DIP (minimum, not fixed) with 40×40 artwork. Artist
  artwork is round; everything else uses `ThumbRadius`.
- Title is one line, ellipsized. The matched part of the query is semibold and
  uses the primary text brush; the rest stays normal.
- Detail line is one line: kind icon + short kind label + artist + album/year.
  Keep it to the facts a listener needs to choose the right result. Play counts
  go in the tooltip, not the row.
- A small trailing glyph signals the action: play for tracks/videos, chevron for
  albums, artists and playlists. The entire row is still one click target.
- Query rows use a search glyph in the 40 px slot, so their titles align with
  entity titles. Recent searches use a clock glyph when the field is empty.
- A 1 px divider separates the entity group from the query group. Do not add
  section headers: `AutoSuggestBox` keyboard navigation treats every item as
  focusable, and a header row would be announced as a suggestion.
- The footer is visually distinct (search glyph + "See all results") and is the
  answer to "I want the full results page, not this one track".

## Interaction spec

### Typing

- Debounce stays at the current **220 ms**. One request per pause.
- Queries shorter than **2 characters** do not hit the network. Show matching
  recent searches plus the footer.
- Keep the previous list visible while a new request is in flight. Do not clear
  it and make the popup jump.
- A response only applies when `TitleSearch.Text` still equals the requested
  text and its cancellation token has not fired. This is the existing generation
  guard; keep it.

### Selecting a row

| Row | Action |
|---|---|
| Song / video | Stop the timer, close the list, play it (video pages follow the existing link behaviour) |
| Album | Open `AlbumPage` |
| Artist | Open `ArtistPage` |
| Playlist (if shown) | Open `PlaylistPage` |
| Query row / recent search | Put the query in the field and run the search |
| "See all results" | Run the current query |
| Pasted YouTube link | Existing `OpenLink` path |

Selecting an entity must **not** replace the query text with the entity title and
then run a text search. That is the current behaviour for strings and would make
a rich result useless.

Use:

```xml
<AutoSuggestBox
    ItemTemplate="{StaticResource SearchSuggestionTemplate}"
    TextMemberPath="Text"
    UpdateTextOnSelect="False"
    MaxSuggestionListHeight="420"
    AutoMaximizeSuggestionArea="False" />
```

`UpdateTextOnSelect="False"` keeps arrow-key navigation from rewriting the field
while the user browses the list. `QuerySubmitted` then decides between
`SuggestionRow.Item`, `SuggestionRow.Link`, and `SuggestionRow.Query`.

### Failure and empty states

- Network failure: show recent searches that match the typed prefix, then the
  footer. Search itself still works.
- No rich rows: text suggestions plus the footer. This is the current experience
  with a clearer way to submit.
- Empty field: recent searches (text rows with a clock glyph), no entity rows,
  no footer.
- Link pasted: one link row, as today, but rendered with the same template.

## Confidence gate

YouTube's rich section is ranked, not filtered. `breach` returns an unrelated
"Security Breach" rap song; `trench` returns "Trench Town" and "Trench Coat".
Showing those as if they were the user's intent is worse than showing text.

Implement a pure, testable scorer in Core:

```
SuggestionRanking.Score(query, MusicItem) -> double [0..1]
```

Normalize both sides first: lowercase invariant, strip diacritics, replace
punctuation with spaces, collapse whitespace. Then score the title (and, for an
artist entity, the artist name):

| Match | Base score |
|---|---|
| Exact title / exact artist name | `1.00` |
| Query is a whole-word prefix of the title, or the title is a whole-word prefix of the query | `0.92` |
| Every query token equals a title token, in any order, same token count | `0.90` |
| Every query token is a prefix of a title token | `0.86` |
| Whole-word substring, but with extra title words | `0.80` |
| One-character typo of a whole title (title length ≥ 6, query length ≥ 5) | `0.87` |
| Anything else | `0.00` |

Adjustments:

- Song by the exact queried artist: `+0.05`.
- Video version of an otherwise matched song: `-0.05` (prefer the audio track).
- Title has more than two extra words beyond the query: `-0.08`.
- A single-token query shorter than 4 characters is capped at `0.60`.
- Generic tokens (`the`, `live`, `love`, `music`, `song`, `album`, `best`,
  `remix`, `mix`, `radio`, `playlist`) are capped at `0.70`, even when a row
  happens to use that word as its exact title: the query is too ambiguous to
  answer with one result.

Promote a row only when `score >= 0.85`. This is deliberately conservative: it
allows `city walls`, `trench`, `breach`, `blurryface` and `twenty one pilots`,
and rejects `the`, `love`, `trench town` (when the user typed `trench`), and
unrelated songs that merely contain the word.

Then:

1. Sort by score, descending.
2. Tie-break by YouTube's order (the endpoint already has popularity context).
3. Dedupe by item id.
4. Collapse a song and its video when they have the same normalized title and
   artist; keep the song.
5. Keep the top 3.

`SuggestionRanking` belongs in `src/Koda.Core/Search/` so it can be unit-tested
without XAML or network.

## Implementation plan

### 1. Core: parse the rich section

Add to `Koda.Core`:

```csharp
public enum SuggestionKind { Query, Song, Video, Album, Artist, Playlist }

public sealed record SearchSuggestion(
    string Text,                 // the query text or the entity title
    SuggestionKind Kind,
    MusicItem? Item,
    double Confidence);

public sealed record SearchSuggestions(
    SearchSuggestion[] Entities,
    string[] Queries);
```

- Add `Pages.Suggestions(Json root, string query)` (or a small
  `Parsing/Suggestions.cs`) that:
  - reads `searchSuggestionRenderer.suggestion` for text;
  - reads `musicResponsiveListItemRenderer` and calls
    `Rows.ItemFromListRow(row)`;
  - scores, filters, sorts and dedupes the entities;
  - returns text suggestions in YouTube's order.
- Change `YouTubeMusic.SuggestionsAsync` to return `SearchSuggestions`.
  The only caller is `MainWindow.xaml.cs`.
- Keep the parser total: a missing rich section returns no entities, never an
  exception. The UI must still show text suggestions.

**Do not** call `SearchAsync` per keystroke to build rich suggestions. That would
add latency and rate-limit pressure for data the suggestion endpoint already
provides.

### 2. Core: ranking with tests

- `SuggestionRanking.Score` and `Normalize` in
  `src/Koda.Core/Search/SuggestionRanking.cs`.
- Tests in `tests/Koda.Core.Tests/SuggestionTests.cs`.
- Capture two or three fixtures from the live endpoint (city walls, breach,
  twenty one pilots) and add them under
  `tests/Koda.Core.Tests/Fixtures/`. Keep the raw response, not a hand-written
  subset, so parser regressions are caught.

Required tests:

- `city walls` → exactly the "City Walls" song; the video is collapsed.
- `breach` → the "Breach" album; the unrelated rap song is not promoted.
- `trench` → the "Trench" album; "Trench Town"/"Trench Coat" are not promoted.
- `twenty one pilots` → the artist; songs with unrelated titles are not promoted.
- `the`, `love`, `lve` → no entities.
- Text suggestions are still returned when the rich section is absent.
- A row with missing artwork still parses.
- Ranking is stable for equal scores.

### 3. UI: row view model and template

Add `SuggestionRow` to `src/Koda/Helpers/ViewModels.cs` (or a small dedicated
file). It is the item `AutoSuggestBox` binds to:

```csharp
public sealed record SuggestionRow(
    string Text,                 // TextMemberPath: what the field shows if used
    string Title,
    string Detail,
    string KindGlyph,
    Artwork? Artwork,
    CornerRadius ArtRadius,
    string TrailingGlyph,
    MusicItem? Item,
    YouTubeLink? Link,
    string? Query,
    bool IsSubmit,
    bool IsRecent,
    bool ShowsDivider);
```

Builders:

- `SuggestionRow.From(MusicItem item, string query)`
- `SuggestionRow.QueryRow(string query)`
- `SuggestionRow.Recent(string query)`
- `SuggestionRow.SeeAll(string query)`
- `SuggestionRow.LinkRow(YouTubeLink link)`

Detail formatting:

- Song: `Song · {ArtistLine}` plus ` · {Album.Title}` when present.
- Video: `Video · {ArtistLine}`.
- Album: `{Album/EP/Single} · {ArtistLine} · {Year}`.
- Artist: `Artist · {Subtitle}`.
- Playlist: `Playlist · {Subtitle}`.
- Query/footer: no detail.

Add `SearchSuggestionTemplate` to `MainWindow.xaml` resources:

```xml
<DataTemplate x:Key="SearchSuggestionTemplate">
    <Grid MinHeight="52" Padding="8,4" ColumnSpacing="12"
          ToolTipService.ToolTip="{Binding Detail}">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="40" />
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>

        <Border Width="40" Height="40"
                CornerRadius="{Binding ArtRadius}"
                Background="{ThemeResource SubtleFillColorSecondaryBrush}">
            <Grid>
                <FontIcon Glyph="{Binding KindGlyph}" FontSize="16"
                          Foreground="{ThemeResource TextFillColorTertiaryBrush}" />
                <Image helpers:Art.Pixels="40"
                       helpers:Art.Source="{Binding Artwork}"
                       Stretch="UniformToFill" />
            </Grid>
        </Border>

        <StackPanel Grid.Column="1" VerticalAlignment="Center" Spacing="1">
            <TextBlock Text="{Binding Title}"
                       helpers:Highlight.Query="{Binding Query}"
                       Style="{ThemeResource BodyTextBlockStyle}"
                       TextTrimming="CharacterEllipsis"
                       TextWrapping="NoWrap" />
            <TextBlock Text="{Binding Detail}"
                       Style="{ThemeResource CaptionTextBlockStyle}"
                       Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                       TextTrimming="CharacterEllipsis"
                       TextWrapping="NoWrap" />
        </StackPanel>

        <FontIcon Grid.Column="2" Glyph="{Binding TrailingGlyph}"
                  FontSize="12"
                  Foreground="{ThemeResource TextFillColorTertiaryBrush}" />
    </Grid>
</DataTemplate>
```

For a simple first pass, the divider can be a top border bound through a
`BoolToVisibility` helper or a dedicated `ShowsDivider` template selector. Do not
add a header row.

Add a tiny `Highlight` attached property (`src/Koda/Helpers/Highlight.cs`) that
splits the title into `Run`s using the existing `FuzzySearch` ranges or a simpler
normalized substring. This is the main visual upgrade over the reference: the
user can see why the row matched.

### 4. UI: wire `MainWindow`

Changes in `src/Koda/MainWindow.xaml.cs`:

- Replace every `ItemsSource = ...string...` with `SuggestionRow` lists:
  - empty field → recents;
  - link → link row;
  - network → `BuildRows(text, response)`.
- `BuildRows` appends entities, query rows (drop the exact-query duplicate and
  text rows that duplicate an entity title), and the footer.
- `OnSearchSubmitted` branches on `SuggestionRow`:
  - `Item` → `Navigator.Open(item)`;
  - `Link` → `OpenLink(link)`;
  - `Query` / `IsSubmit` → `SearchFor(query)`.
- Stop the timer and cancel the request when an entity action is taken.
- Set `ItemsSource = null` and close the suggestion list after selecting an
  entity, so the popup cannot immediately reopen with stale rows.
- On failure, fall back to matching recents plus the footer.

Keep the current title-bar layout and `420` width. Long titles and details
ellipsize; the tooltip carries the full line.

### 5. Accessibility and polish

- Set `AutomationProperties.Name` on each row to the full sentence:
  `"City Walls, Song by twenty one pilots from Breach. Press Enter to play."`
- Set `AutomationProperties.HelpText` to the action (`Play`, `Open album`,
  `Search`).
- Do not remove the native focus visual. Verify arrow keys, Enter, Escape and
  Ctrl+F all behave.
- Verify 200% text scaling: the row grows because `MinHeight` is used instead of
  `Height`; the list scrolls inside `MaxSuggestionListHeight`.
- Verify light, dark and high-contrast themes. Use theme brushes only; the art
  fallback uses `SubtleFillColorSecondaryBrush`.
- Verify the popup is not clipped by the title bar or the content frame. This is
  the one layout risk of a rich `ItemTemplate`; test it early with two dummy
  rows before building the full feature. If it is clipped, the fallback is a
  custom anchored `Flyout`, but `AutoSuggestBox` should render its popup above
  the content correctly.

## Files

| File | Change |
|---|---|
| `src/Koda.Core/Models.cs` | `SearchSuggestion`, `SearchSuggestions`, `SuggestionKind` |
| `src/Koda.Core/Parsing/Pages.cs` or `Parsing/Suggestions.cs` | Parse text + rich sections |
| `src/Koda.Core/Search/SuggestionRanking.cs` | Normalize, score, gate, dedupe |
| `src/Koda.Core/YouTubeMusic.cs` | Return `SearchSuggestions` from `SuggestionsAsync` |
| `src/Koda/Helpers/ViewModels.cs` | `SuggestionRow` and builders |
| `src/Koda/Helpers/Highlight.cs` | Matched-text runs (new) |
| `src/Koda/MainWindow.xaml` | `ItemTemplate`, `UpdateTextOnSelect`, list height |
| `src/Koda/MainWindow.xaml.cs` | Build rows, selection handling, fallbacks |
| `tests/Koda.Core.Tests/SuggestionTests.cs` | Ranking and parser tests |
| `tests/Koda.Core.Tests/Fixtures/search-suggestions-*.json` | Captured responses |

## Acceptance criteria

- Typing `city walls` shows a rich song row at the top with artwork, title,
  "Song · twenty one pilots · Breach"; text suggestions stay below; Enter plays
  the song; clicking plays it.
- `trench` promotes the album; `twenty one pilots` promotes the artist;
  `breach` promotes the album and not the unrelated rap song.
- `the`, `love` and `lve` show no rich rows.
- Empty field shows recent searches with a clock glyph.
- Pasting a YouTube link still shows one link row and opens it.
- Offline/failure shows matching recents plus the footer; search still works.
- Rapid typing produces one request per pause, and an older response never
  replaces a newer one.
- Keyboard: Down/Up traverse every row, Enter acts on the selected row, Escape
  closes the list, Ctrl+F focuses the field.
- Screen reader reads "title, kind, artist, album, action" for each entity row.
- No clipped text at 420 px width; tooltips show the full detail.
- Light, dark and high-contrast themes are legible; 200% text scale scrolls.
- `dotnet test` passes, including the new ranking/parser tests.

## Phases

1. **Spike (small):** render two dummy rich rows in the real title-bar
   `AutoSuggestBox`, confirm popup placement and keyboard behaviour.
2. **Core (small):** parse rich sections, add ranking, tests and fixtures.
3. **UI (medium):** `SuggestionRow`, template, build/selection logic, fallbacks.
4. **Polish (small):** highlight, accessibility, themes, text scale.
5. **Verify (small):** acceptance matrix, build, tests, manual pass.

## Open questions

- Should a selected song play immediately (recommended, matching link
  behaviour) or open its page? The footer remains the way to the full results.
- Should playlists appear when they are the only exact match? The user asked for
  tracks, albums and artists; recommend hiding playlists in v1 unless they score
  at least as high as every other entity.
- Should the empty state also offer recently played tracks? Nice, but out of
  scope for the first pass.
- Do we want a hover-only play button for songs, or is the trailing glyph enough?
  Recommend the trailing glyph first: it keeps the row one hit target and avoids
  nested buttons inside the suggestion list.
