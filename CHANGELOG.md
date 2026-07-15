# Changelog

## 0.2.0

- Added runtime console window (`RuntimeConsole`) with `ConsoleCommandAttribute` command registration.
- Added debug mode gating (`SetDebugModeActive`, `IsDebugModeActive`) replacing the `UNITY_EDITOR || DEVELOPMENT_BUILD` compile guard.
- Added static window control API: `SetHubVisible`, `ToggleVisible`, `IsWindowOpen`, `SetWindowOpen`, `ToggleWindow`, `AcquireSkin`.
- Hub panel now sizes to the number of available windows and scrolls when it exceeds screen height.
- Switched window ids to `GetEntityId()` (requires Unity 6).

## 0.1.0

- Initial package extraction.
- Added core runtime debug menu assembly.
- Added example debug window sample.
