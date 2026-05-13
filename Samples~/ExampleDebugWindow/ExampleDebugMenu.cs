#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;

/// <summary>
/// Template debug window. Duplicate this component, rename it, attach it to the object that owns
/// the data being debugged, then replace target with concrete serialized references.
/// </summary>
public sealed class ExampleDebugWindow : RuntimeDebugWindow
{
    [SerializeField] private GameObject target;

    private string amount = "1";
    private bool enabledOverride;
    private string lastResult;

    /// <summary>Name shown in the F1 hub and title bar.</summary>
    public override string Title => "Example";

    /// <summary>Hub grouping label. Use this to keep related windows together.</summary>
    public override string Category => "Systems";

    /// <summary>Material Symbols icon displayed beside the window title.</summary>
    public override string Icon => RuntimeDebugSymbols.Settings;

    /// <summary>Short fallback text used if the icon font is unavailable.</summary>
    public override string IconFallback => "EX";

    /// <summary>Accent color for this window's hub badge, rail, and section headers.</summary>
    public override Color AccentColor => new Color(0.4f, 0.8f, 1f, 1f);

    /// <summary>Lower values sort earlier in the F1 hub.</summary>
    public override int SortOrder => 100;

    /// <summary>Initial window size when the component first registers.</summary>
    public override Vector2 DefaultSize => new Vector2(420f, 360f);

    private void Awake()
    {
        if (target == null)
            target = gameObject;
    }

    /// <summary>
    /// Return true when the window should be visible in the hub
    /// </summary>
    public override bool IsAvailable(RuntimeDebugContext context)
    {
        return target != null && target.activeInHierarchy;
    }

    /// <summary>Called when the window opens.</summary>
    public override void OnOpened(RuntimeDebugContext context)
    {
    }

    /// <summary>Called when the window closes.</summary>
    public override void OnClosed()
    {
    }

    /// <summary>Draws overlays outside the window body.</summary>
    public override void DrawOverlay(RuntimeDebugContext context)
    {
    }

    /// <summary>Draw the window body with IMGUI controls.</summary>
    protected override void Draw(RuntimeDebugContext context)
    {
        DrawSectionHeader("State", RuntimeDebugSymbols.Analytics, "STAT");

        GUILayout.Label($"Target: {target.name}");
        GUILayout.Label($"Active In Hierarchy: {target.activeInHierarchy}");

        enabledOverride = GUILayout.Toggle(enabledOverride, "Example toggle");
        GUILayout.Space(8f);
        DrawSectionHeader("Actions", RuntimeDebugSymbols.Settings, "ACT");

        RuntimeDebugGuiUtility.TextField("Amount", ref amount);

        if (GUILayout.Button("Apply"))
            lastResult = RuntimeDebugGuiUtility.TryParseFloat(amount, out float value)
                ? $"Applied example value {RuntimeDebugGuiUtility.FormatFloat(value)}."
                : "Amount is invalid.";

        if (GUILayout.Button("Reset"))
            lastResult = "Example state reset.";

        if (!string.IsNullOrWhiteSpace(lastResult))
        {
            GUILayout.Space(6f);
            GUILayout.Label(lastResult);
        }
    }
}
#endif
