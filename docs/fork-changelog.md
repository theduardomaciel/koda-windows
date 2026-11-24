# Fork changelog

Fork-only: do not upstream this file. The release workflow publishes the section
whose heading matches the tag (`## 0.3.0.4` for `v0.3.0.4`) and appends the
Full Changelog compare link. Add the section before tagging; the workflow fails
if it is missing. `CHANGELOG.md` stays upstream's.

## 0.3.0.4

### Lyrics

- Lyrics now prefer the finest sync available — syllables, then words, then lines, then plain text — instead of whichever source answers first. A coarser answer waits briefly for a richer one, and an older coarse cache entry is no longer reused.
- Named looks now carry their own size and line spacing: **Koda** 40 / 32, **Expressive** 44 / 36, **Minimal** 32 / 40. Moving either slider switches the look to **Custom**, and saved named looks pick up the new defaults.
- **Expressive** now dims the lines around the sung one to 0.1.
- Fixed lyrics after a seek or jump: the window is rebuilt at the landing line, so opening lyrics after Now Playing no longer leaves the next line missing, and rows that are already painted are not repainted.

## 0.3.0.3

First fork release, based on upstream 0.3.0 plus upstream beta's video mode and
smooth motion work.

- Removed the global hand-cursor handler that flashed clickable surfaces and broke the lyrics hover box.
- Shelf card subtitles stay on one line, so a long artist line cannot resize the Recently played row.
