# Runtime Debug UI

Cool and epic debug menu/window system for epic debugging purposes.
(Runtime Debug UI is a Unity Package Manager package that provides an F1 IMGUI debug menu for the Unity Editor and development builds.)

## Install

In Unity, open Package Manager and install from the Git URL:

1. Open `Window > Package Manager`.
2. Click `+`.
3. Choose `Add package from git URL...`.
4. Enter:

```text
https://github.com/rdpolarity/RuntimeDebugUI.git
```

## Usage

Create a `MonoBehaviour` that derives from `RuntimeDebugWindow`, attach it to the GameObject that owns the data being debugged, and implement `Draw(RuntimeDebugContext context)`.

The menu bootstraps itself before scene load and opens with F1. Windows register while enabled and unregister when disabled.
