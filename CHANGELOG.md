# Changelog

## 0.1.3.0

- Cleans up every native hook if initialization fails partway through.
- Clears stale hook-error diagnostics after a successful initialization.
- Adds standard build-output exclusions for local development.

## 0.1.2.0

- Converts button labels only for hovered buttons currently inside QoLBar.
- Tracks QoLBar window scope without allocating managed window-name strings.
- Moves preview mapping rescans off the render thread.
- Throttles preview file metadata checks and cleans stale preview cache files.
- Uses immutable versioned release links for cache-safe updates.

## 0.1.1.0

- Adds `/qgp` as the new primary command.
- Keeps `/qolglampreview` as a legacy alias.

## 0.1.0.0

- Initial preview-tooltip prototype.
- Matches QoLBar text button names to Glamourer Preview Manager screenshots.
- Adds `/qolglampreview` settings command.
