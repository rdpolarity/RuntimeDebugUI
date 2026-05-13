# Runtime Debug UI

`RuntimeDebugUI` is the singleton F1 menu. It is created before scene load and persists across scenes.

`RuntimeDebugWindow` is the base class for object-owned windows. Override `Title`, optional metadata such as `Category`, `IconFallback`, `AccentColor`, and implement `Draw(RuntimeDebugContext context)`.

`RuntimeDebugContext.Building` lets a game provide scene or runtime tags and services. Windows can use `RequiredTags` or override `IsAvailable(RuntimeDebugContext context)` to hide themselves outside valid contexts.

`RuntimeDebugGuiUtility` contains shared IMGUI helpers for section headers, parsing, sprite drawing, formatting, and loading the embedded Material Symbols fonts.
