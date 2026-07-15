using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public readonly struct RuntimeDebugContext
{
    private readonly HashSet<string> tags;
    private readonly Dictionary<Type, object> services;
    private readonly string[] labels;

    internal RuntimeDebugContext(Scene activeScene, IEnumerable<string> tags, Dictionary<Type, object> services, IEnumerable<string> labels)
    {
        ActiveScene = activeScene;
        this.tags = tags != null ? new HashSet<string>(tags, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        this.services = services != null ? new Dictionary<Type, object>(services) : new Dictionary<Type, object>();
        this.labels = labels != null ? new List<string>(labels).ToArray() : Array.Empty<string>();
    }

    public Scene ActiveScene { get; }
    public string SceneName => ActiveScene.IsValid() ? ActiveScene.name : string.Empty;
    public string ScenePath => ActiveScene.IsValid() ? ActiveScene.path : string.Empty;

    public static event Action<RuntimeDebugContextBuilder> Building;

    public bool HasTag(string tag)
    {
        return string.IsNullOrWhiteSpace(tag) || tags != null && tags.Contains(tag);
    }

    public bool HasAllTags(IReadOnlyList<string> requiredTags)
    {
        if (requiredTags == null || requiredTags.Count == 0)
            return true;

        for (int i = 0; i < requiredTags.Count; i++)
        {
            if (!HasTag(requiredTags[i]))
                return false;
        }

        return true;
    }

    public bool TryGetService<T>(out T service) where T : class
    {
        if (services != null && services.TryGetValue(typeof(T), out object value) && value is T typedValue)
        {
            service = typedValue;
            return true;
        }

        service = null;
        return false;
    }

    public T GetService<T>() where T : class
    {
        return TryGetService(out T service) ? service : null;
    }

    public string ContextLabel
    {
        get
        {
            if (labels != null && labels.Length > 0)
                return string.Join(", ", labels);
            if (tags != null && tags.Count > 0)
                return string.Join(", ", tags);

            return "None";
        }
    }

    public static RuntimeDebugContext Create()
    {
        var builder = new RuntimeDebugContextBuilder(SceneManager.GetActiveScene());
        Building?.Invoke(builder);
        return builder.Build();
    }
}

public sealed class RuntimeDebugContextBuilder
{
    private readonly HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<Type, object> services = new Dictionary<Type, object>();
    private readonly List<string> labels = new List<string>(8);

    public RuntimeDebugContextBuilder(Scene activeScene)
    {
        ActiveScene = activeScene;
    }

    public Scene ActiveScene { get; }

    public void AddTag(string tag)
    {
        if (!string.IsNullOrWhiteSpace(tag))
            tags.Add(tag);
    }

    public void AddTags(IReadOnlyList<string> tags)
    {
        if (tags == null)
            return;

        for (int i = 0; i < tags.Count; i++)
            AddTag(tags[i]);
    }

    public void AddLabel(string label)
    {
        if (!string.IsNullOrWhiteSpace(label) && !labels.Contains(label))
            labels.Add(label);
    }

    public void AddService<T>(T service) where T : class
    {
        if (service != null)
            services[typeof(T)] = service;
    }

    internal RuntimeDebugContext Build()
    {
        return new RuntimeDebugContext(ActiveScene, tags, services, labels);
    }
}

/// <summary>
/// Base class for an object-owned debug window. Create a MonoBehaviour that derives from this,
/// attach it to the scene object or prefab that owns the data it needs, wire that data with
/// serialized fields, then draw the window with IMGUI inside Draw. The window registers itself
/// while enabled, so its lifetime follows the GameObject; use IsAvailable for extra filtering,
/// OnOpened/OnClosed for refresh or cleanup, DrawSectionHeader for titled groups, and DrawOverlay
/// for previews that should render outside the window.
/// </summary>
public abstract class RuntimeDebugWindow : MonoBehaviour
{
    private Vector2 scroll;

    public abstract string Title { get; }
    public virtual string Id => $"{GetType().FullName}:{GetEntityId()}";
    public virtual string Icon => RuntimeDebugSymbols.BugReport;
    public virtual string IconFallback => "DBG";
    public virtual bool IconFilled => true;
    public virtual string Category => "General";
    public virtual Color AccentColor => new Color(0.86f, 0.86f, 0.86f, 1f);
    public virtual int SortOrder => 0;
    public virtual IReadOnlyList<string> RequiredTags => Array.Empty<string>();
    public virtual Vector2 DefaultSize => new Vector2(420f, 360f);
    public virtual bool DefaultOpen => false;
    public virtual bool UseScrollView => true;

    public virtual bool IsAvailable(RuntimeDebugContext context) => context.HasAllTags(RequiredTags);
    public virtual void OnOpened(RuntimeDebugContext context) { }
    public virtual void OnClosed() { }
    public virtual void DrawOverlay(RuntimeDebugContext context) { }

    protected virtual void OnEnable()
    {
        RuntimeDebugUI.Register(this);
    }

    protected virtual void OnDisable()
    {
        RuntimeDebugUI.Unregister(this);
    }

    protected void DrawSectionHeader(string label)
    {
        RuntimeDebugGuiUtility.SectionHeader(label, AccentColor, Icon, IconFallback, IconFilled);
    }

    protected void DrawSectionHeader(string label, string icon, string fallback, bool filled = true)
    {
        RuntimeDebugGuiUtility.SectionHeader(label, AccentColor, icon, fallback, filled);
    }

    public void DrawWindow(RuntimeDebugContext context)
    {
        if (UseScrollView)
        {
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Space(2f);
        }

        Draw(context);

        if (UseScrollView)
        {
            GUILayout.Space(4f);
            GUILayout.EndScrollView();
        }
    }

    protected abstract void Draw(RuntimeDebugContext context);
}

[DefaultExecutionOrder(-32000)]
public sealed class RuntimeDebugUI : MonoBehaviour
{
    private sealed class WindowState
    {
        public RuntimeDebugWindow Window;
        public Rect Rect;
        public bool Open;
        public bool Pinned;
        public bool WasOpen;
    }

    private const Key ToggleKey = Key.F1;
    private const int FirstWindowId = 940000;
    private const float HubWidth = 344f;
    private const float WindowStartX = 372f;
    private static RuntimeDebugUI instance;

    private readonly List<WindowState> windows = new List<WindowState>(16);
    private bool visible;
    private bool pauseGameplay;
    private bool pausedByDebugUi;
    private bool pointerBlocked;
    private bool pointerCaptureActive;
    private Rect hubRect = new Rect(12f, 12f, HubWidth, 430f);
    private Vector2 hubScroll;
    private GUISkin debugSkin;
    private GUIStyle hubStyle;
    private GUIStyle titleStyle;
    private GUIStyle headerStyle;
    private GUIStyle statusStyle;
    private GUIStyle keyStyle;
    private GUIStyle windowButtonStyle;
    private GUIStyle windowButtonOpenStyle;
    private GUIStyle chipStyle;
    private GUIStyle chipActiveStyle;
    private Texture2D windowTexture;
    private Texture2D buttonTexture;
    private Texture2D buttonHoverTexture;
    private Texture2D buttonActiveTexture;
    private Texture2D rowTexture;
    private Texture2D rowHoverTexture;
    private Texture2D rowOpenTexture;
    private Texture2D chipTexture;
    private Texture2D chipActiveTexture;
    private Texture2D fieldTexture;
    private Texture2D fieldFocusTexture;
    private Texture2D boxTexture;
    private Texture2D scrollbarTexture;
    private Texture2D scrollbarThumbTexture;
    private RuntimeDebugContext cachedContext;
    private int cachedContextFrame = -1;
    private static bool debugModeActive;

    public static bool IsDebugModeActive => debugModeActive;
    public static bool IsVisible => debugModeActive && instance != null && instance.visible;
    public static bool IsPointerBlocked => debugModeActive && instance != null && instance.IsPointerBlockedNow();
    public static bool IsPointerOverDebugUi => debugModeActive && instance != null && instance.ContainsGuiPoint(GetCurrentGuiPointerPosition());
    public static bool IsGuiPointOverDebugUi(Vector2 guiPosition) => debugModeActive && instance != null && instance.ContainsGuiPoint(guiPosition);
    public static event Action<bool> PauseStateChanged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        EnsureInstance();
    }

    public static GUISkin AcquireSkin()
    {
        RuntimeDebugUI ui = EnsureInstance();
        ui.EnsureStyles();
        return ui.debugSkin;
    }

    public static void Register(RuntimeDebugWindow window)
    {
        if (window == null)
            return;

        EnsureInstance().RegisterWindow(window);
    }

    public static void Unregister(RuntimeDebugWindow window)
    {
        if (window == null || instance == null)
            return;

        instance.UnregisterWindow(window);
    }

    public static void SetDebugModeActive(bool active)
    {
        if (debugModeActive == active)
            return;

        debugModeActive = active;
        if (!debugModeActive && instance != null)
            instance.SetVisible(false);
    }

    public static void SetHubVisible(bool value)
    {
        if (value && !debugModeActive)
            return;

        EnsureInstance().SetVisible(value);
    }

    public static void ToggleVisible()
    {
        if (!debugModeActive)
            return;

        RuntimeDebugUI ui = EnsureInstance();
        ui.SetVisible(!ui.visible);
    }

    public static bool IsWindowOpen(RuntimeDebugWindow window)
    {
        if (window == null || instance == null)
            return false;

        WindowState state = instance.FindWindowState(window);
        return state != null && state.Open;
    }

    public static void SetWindowOpen(RuntimeDebugWindow window, bool open, bool revealHub = true)
    {
        if (window == null)
            return;

        if (open && !debugModeActive)
            return;

        RuntimeDebugUI ui = EnsureInstance();
        WindowState state = ui.FindWindowState(window);
        if (state == null)
        {
            ui.RegisterWindow(window);
            state = ui.FindWindowState(window);
        }

        if (state == null)
            return;

        if (revealHub && open)
            ui.SetVisible(true);

        ui.SetWindowOpen(state, open, ui.GetContext());
    }

    public static void ToggleWindow(RuntimeDebugWindow window, bool revealHub = true)
    {
        if (window == null)
            return;

        if (!debugModeActive)
            return;

        RuntimeDebugUI ui = EnsureInstance();
        WindowState state = ui.FindWindowState(window);
        if (state == null)
        {
            ui.RegisterWindow(window);
            state = ui.FindWindowState(window);
        }

        if (state == null)
            return;

        bool open = !state.Open || (!ui.visible && !state.Pinned);
        if (revealHub && open)
            ui.SetVisible(true);

        ui.SetWindowOpen(state, open, ui.GetContext());
    }

    private static RuntimeDebugUI EnsureInstance()
    {
        if (instance != null)
            return instance;

        instance = UnityEngine.Object.FindFirstObjectByType<RuntimeDebugUI>();
        if (instance != null)
            return instance;

        GameObject host = new GameObject(nameof(RuntimeDebugUI));
        instance = host.AddComponent<RuntimeDebugUI>();
        DontDestroyOnLoad(host);
        return instance;
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;

        SetPausedByDebugUi(false);
        DestroyStyleResources();
    }

    private void Update()
    {
        if (!debugModeActive)
        {
            if (visible)
                SetVisible(false);
            UpdatePointerBlockState();
            return;
        }

        UpdatePointerBlockState();

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || !keyboard[ToggleKey].wasPressedThisFrame)
            return;

        SetVisible(!visible);
    }

    private void OnGUI()
    {
        if (!debugModeActive)
            return;

        RuntimeDebugContext context = GetContext();
        if (!visible && !HasPinnedOpenWindows(context))
            return;

        EnsureStyles();
        GUISkin previousSkin = GUI.skin;
        Color previousColor = GUI.color;
        Color previousContentColor = GUI.contentColor;
        Color previousBackgroundColor = GUI.backgroundColor;
        GUI.skin = debugSkin;
        GUI.depth = -20000;
        GUI.color = Color.white;
        GUI.contentColor = Color.white;
        GUI.backgroundColor = Color.white;

        try
        {
            if (visible)
                DrawHub(context);

            DrawOpenWindows(context);
            DrawWindowOverlays(context);
            UpdatePointerBlockState();
        }
        finally
        {
            GUI.skin = previousSkin;
            GUI.color = previousColor;
            GUI.contentColor = previousContentColor;
            GUI.backgroundColor = previousBackgroundColor;
        }
    }

    private void SetVisible(bool value)
    {
        if (visible == value)
            return;

        visible = value;
        if (!visible)
        {
            pointerBlocked = false;
            pointerCaptureActive = false;
        }

        UpdatePauseState();

        if (!visible)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i].Pinned)
                    continue;

                if (windows[i].WasOpen)
                    windows[i].Window.OnClosed();

                windows[i].WasOpen = false;
            }
        }
    }

    private void RegisterWindow(RuntimeDebugWindow window)
    {
        if (window == null || windows.Exists(state => state.Window == window))
            return;

        Vector2 size = window.DefaultSize;
        int index = windows.Count;
        windows.Add(new WindowState
        {
            Window = window,
            Open = window.DefaultOpen,
            Rect = ResolveDefaultWindowRect(index, size)
        });

        windows.Sort(CompareWindows);
    }

    private WindowState FindWindowState(RuntimeDebugWindow window)
    {
        return windows.Find(state => state.Window == window);
    }

    private void UnregisterWindow(RuntimeDebugWindow window)
    {
        WindowState state = windows.Find(candidate => candidate.Window == window);
        if (state == null)
            return;

        if (state.WasOpen)
            state.Window.OnClosed();

        windows.Remove(state);
    }

    private Rect ResolveDefaultWindowRect(int index, Vector2 size)
    {
        int column = index % 2;
        int row = index / 2;
        return new Rect(WindowStartX + column * 430f + row * 18f, 24f + row * 44f, Mathf.Max(240f, size.x), Mathf.Max(160f, size.y));
    }

    private int CompareWindows(WindowState a, WindowState b)
    {
        if (a?.Window == null && b?.Window == null)
            return 0;
        if (a?.Window == null)
            return 1;
        if (b?.Window == null)
            return -1;

        int order = a.Window.SortOrder.CompareTo(b.Window.SortOrder);
        return order != 0 ? order : string.Compare(a.Window.Title, b.Window.Title, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawHub(RuntimeDebugContext context)
    {
        int visibleWindows = 0;
        for (int i = 0; i < windows.Count; i++)
        {
            WindowState state = windows[i];
            if (state.Window != null && state.Window.IsAvailable(context))
                visibleWindows++;
        }

        GUIContent pauseContent = new GUIContent("Pause gameplay while open");
        float toggleHeight = debugSkin.toggle.CalcHeight(pauseContent, hubRect.width - 32f);
        float headerHeight = 14f + 38f + 16f + toggleHeight + 12f + 26f + 14f;
        float listHeight = visibleWindows > 0 ? visibleWindows * 50f : 24f;
        float maxHeight = Screen.height - 24f;
        hubRect.height = Mathf.Min(headerHeight + listHeight, maxHeight);
        bool needsScroll = headerHeight + listHeight > maxHeight;

        GUILayout.BeginArea(hubRect, hubStyle);
        DrawHubChrome();
        DrawHubTitle();
        GUILayout.Space(16f);

        bool nextPauseGameplay = GUILayout.Toggle(pauseGameplay, pauseContent);
        if (nextPauseGameplay != pauseGameplay)
        {
            pauseGameplay = nextPauseGameplay;
            UpdatePauseState();
        }

        GUILayout.Space(12f);
        DrawHubSectionTitle("Windows", RuntimeDebugSymbols.Dashboard);

        if (needsScroll)
            hubScroll = GUILayout.BeginScrollView(hubScroll);

        for (int i = 0; i < windows.Count; i++)
        {
            WindowState state = windows[i];
            if (state.Window == null || !state.Window.IsAvailable(context))
                continue;

            DrawWindowButton(state, context);
        }

        if (visibleWindows == 0)
            GUILayout.Label("No debug windows are available in this context.");

        if (needsScroll)
            GUILayout.EndScrollView();

        GUILayout.EndArea();
    }

    private void DrawHubTitle()
    {
        Rect rect = GUILayoutUtility.GetRect(0f, 38f, GUILayout.ExpandWidth(true));
        Rect iconRect = new Rect(rect.x + 4f, rect.y + 5f, 28f, 28f);
        Rect titleRect = new Rect(iconRect.xMax + 10f, rect.y + 3f, rect.width - 112f, 20f);
        Rect subtitleRect = new Rect(titleRect.x, rect.y + 22f, titleRect.width, 14f);
        Rect keyRect = new Rect(rect.xMax - 54f, rect.y + 7f, 42f, 22f);

        RuntimeDebugGuiUtility.DrawSolidRect(iconRect, new Color(0.34f, 0.76f, 1f, 1f));
        RuntimeDebugGuiUtility.DrawMaterialIcon(iconRect, RuntimeDebugSymbols.BugReport, "DBG", Color.black, 21, true);
        GUI.Label(titleRect, "Runtime Debug", titleStyle);
        GUI.Label(subtitleRect, "Universal tools", statusStyle);
        if (Event.current.type == EventType.Repaint)
            chipActiveStyle.Draw(keyRect, GUIContent.none, false, false, false, false);
        GUI.Label(keyRect, "F1", keyStyle);
    }

    private void DrawHubSectionTitle(string label, string icon)
    {
        Rect rect = GUILayoutUtility.GetRect(0f, 26f, GUILayout.ExpandWidth(true));
        Rect iconRect = new Rect(rect.x + 4f, rect.y + 4f, 18f, 18f);
        Rect labelRect = new Rect(iconRect.xMax + 8f, rect.y + 3f, rect.width - 32f, 18f);

        if (Event.current.type == EventType.Repaint)
            RuntimeDebugGuiUtility.DrawSolidRect(new Rect(labelRect.x, rect.yMax - 2f, labelRect.width, 1f), new Color(1f, 1f, 1f, 0.18f));

        RuntimeDebugGuiUtility.DrawMaterialIcon(iconRect, icon, "WIN", new Color(0.86f, 0.86f, 0.86f, 1f), 17, true);
        GUI.Label(labelRect, label, headerStyle);
    }

    private void DrawHubChrome()
    {
        if (Event.current.type != EventType.Repaint)
            return;

        RuntimeDebugGuiUtility.DrawSolidRect(new Rect(0f, 0f, 5f, hubRect.height), new Color(0.34f, 0.76f, 1f, 1f));
        RuntimeDebugGuiUtility.DrawSolidRect(new Rect(0f, 52f, hubRect.width, 1f), new Color(1f, 1f, 1f, 0.16f));
    }

    private void DrawWindowButton(WindowState state, RuntimeDebugContext context)
    {
        Rect rect = GUILayoutUtility.GetRect(0f, 50f, GUILayout.ExpandWidth(true));
        GUIStyle style = state.Open ? windowButtonOpenStyle : windowButtonStyle;
        Event evt = Event.current;
        bool hover = evt != null && rect.Contains(evt.mousePosition);
        if (evt != null && evt.type == EventType.Repaint)
            style.Draw(rect, GUIContent.none, hover, false, false, false);

        Color accentColor = state.Window.AccentColor;
        Rect accentRect = new Rect(rect.x, rect.y, 5f, rect.height);
        Rect badgeRect = new Rect(rect.x + 13f, rect.y + 9f, 32f, 32f);
        Rect titleRect = new Rect(badgeRect.xMax + 12f, rect.y + 8f, rect.width - badgeRect.width - 34f, 18f);
        Rect metaRect = new Rect(titleRect.x, rect.y + 28f, titleRect.width, 16f);

        RuntimeDebugGuiUtility.DrawSolidRect(accentRect, accentColor);
        RuntimeDebugGuiUtility.DrawSolidRect(badgeRect, accentColor);

        Color previousContentColor = GUI.contentColor;
        RuntimeDebugGuiUtility.DrawMaterialIcon(badgeRect, state.Window.Icon, state.Window.IconFallback, Color.black, 22, state.Window.IconFilled);
        GUI.contentColor = Color.white;
        GUI.Label(titleRect, state.Window.Title, headerStyle);
        GUI.contentColor = new Color(0.72f, 0.72f, 0.72f, 1f);
        GUI.Label(metaRect, GetWindowSubtitle(state.Window), statusStyle);
        GUI.contentColor = previousContentColor;

        if (evt == null || evt.type != EventType.MouseDown || evt.button != 0 || !rect.Contains(evt.mousePosition))
            return;

        SetWindowOpen(state, !state.Open, context);
        evt.Use();
    }

    private static string GetWindowSubtitle(RuntimeDebugWindow window)
    {
        string category = string.IsNullOrWhiteSpace(window.Category) ? "General" : window.Category;
        string requirement = GetContextRequirementLabel(window.RequiredTags);
        return string.IsNullOrWhiteSpace(requirement) ? category : $"{category} / {requirement}";
    }

    private static string GetContextRequirementLabel(IReadOnlyList<string> tags)
    {
        if (tags == null || tags.Count == 0)
            return string.Empty;

        var labels = new List<string>(tags.Count);
        for (int i = 0; i < tags.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(tags[i]))
                labels.Add(tags[i]);
        }

        return labels.Count > 0 ? string.Join(" + ", labels) : string.Empty;
    }

    private RuntimeDebugContext GetContext()
    {
        if (cachedContextFrame == Time.frameCount)
            return cachedContext;

        cachedContext = RuntimeDebugContext.Create();
        cachedContextFrame = Time.frameCount;
        return cachedContext;
    }

    private void DrawOpenWindows(RuntimeDebugContext context)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            WindowState state = windows[i];
            bool shouldDraw = ShouldDrawWindow(state, context);
            if (!shouldDraw)
            {
                if (state.WasOpen)
                {
                    state.Window.OnClosed();
                    state.WasOpen = false;
                }

                continue;
            }

            if (!state.WasOpen)
            {
                state.Window.OnOpened(context);
                state.WasOpen = true;
            }

            int windowId = FirstWindowId + i;
            Rect windowRect = RuntimeDebugGuiUtility.ClampRectToScreen(state.Rect);
            float alpha = ResolveWindowAlpha(state, windowRect);
            Color previousColor = GUI.color;
            Color previousContentColor = GUI.contentColor;
            Color previousBackgroundColor = GUI.backgroundColor;
            GUI.color = WithAlpha(GUI.color, GUI.color.a * alpha);
            GUI.contentColor = WithAlpha(GUI.contentColor, GUI.contentColor.a * alpha);
            GUI.backgroundColor = WithAlpha(GUI.backgroundColor, GUI.backgroundColor.a * alpha);
            state.Rect = GUI.Window(windowId, windowRect, id =>
            {
                bool closed = DrawWindowChrome(state, context, windowRect);
                if (!closed)
                    state.Window.DrawWindow(context);

                GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, windowRect.width - 66f), 32f));
            }, string.Empty);
            GUI.color = previousColor;
            GUI.contentColor = previousContentColor;
            GUI.backgroundColor = previousBackgroundColor;
        }
    }

    private bool ShouldDrawWindow(WindowState state, RuntimeDebugContext context)
    {
        if (state == null || state.Window == null || !state.Open || !state.Window.IsAvailable(context))
            return false;

        return visible || state.Pinned;
    }

    private float ResolveWindowAlpha(WindowState state, Rect windowRect)
    {
        if (visible || !state.Pinned)
            return 1f;

        return windowRect.Contains(GetCurrentGuiPointerPosition()) ? 0.96f : 0.34f;
    }

    private bool DrawWindowChrome(WindowState state, RuntimeDebugContext context, Rect windowRect)
    {
        RuntimeDebugWindow window = state.Window;
        Color accentColor = window.AccentColor;
        Rect titleBarRect = new Rect(0f, 0f, windowRect.width, 32f);
        Rect iconRect = new Rect(12f, 6f, 20f, 20f);
        Rect closeRect = new Rect(windowRect.width - 31f, 4f, 24f, 24f);
        Rect pinRect = new Rect(closeRect.x - 28f, 4f, 24f, 24f);
        Rect dragIconRect = new Rect(pinRect.x - 28f, 6f, 20f, 20f);
        Rect titleRect = new Rect(iconRect.xMax + 10f, 6f, Mathf.Max(0f, dragIconRect.x - iconRect.xMax - 16f), 20f);
        Event evt = Event.current;
        bool titleHover = evt != null && titleBarRect.Contains(evt.mousePosition) && !closeRect.Contains(evt.mousePosition) && !pinRect.Contains(evt.mousePosition);
        bool closeHover = evt != null && closeRect.Contains(evt.mousePosition);
        bool pinHover = evt != null && pinRect.Contains(evt.mousePosition);

        RuntimeDebugGuiUtility.DrawSolidRect(new Rect(0f, 0f, 5f, windowRect.height), accentColor);
        RuntimeDebugGuiUtility.DrawSolidRect(titleBarRect, new Color(1f, 1f, 1f, titleHover ? 0.085f : 0.035f));
        RuntimeDebugGuiUtility.DrawSolidRect(new Rect(0f, 31f, windowRect.width, 1f), new Color(accentColor.r, accentColor.g, accentColor.b, 0.5f));
        RuntimeDebugGuiUtility.DrawSolidRect(iconRect, accentColor);
        RuntimeDebugGuiUtility.DrawMaterialIcon(iconRect, window.Icon, window.IconFallback, Color.black, 16, window.IconFilled);
        RuntimeDebugGuiUtility.DrawMaterialIcon(dragIconRect, RuntimeDebugSymbols.DragIndicator, "DRG", titleHover ? Color.white : new Color(0.58f, 0.58f, 0.58f, 1f), 18, false);

        Color previousContentColor = GUI.contentColor;
        GUI.contentColor = Color.white;
        GUI.Label(titleRect, window.Title, headerStyle);
        GUI.contentColor = previousContentColor;

        RuntimeDebugGuiUtility.DrawSolidRect(pinRect, state.Pinned ? accentColor : pinHover ? new Color(1f, 1f, 1f, 0.18f) : new Color(1f, 1f, 1f, 0.07f));
        bool pinClicked = GUI.Button(pinRect, GUIContent.none, GUIStyle.none);
        RuntimeDebugGuiUtility.DrawMaterialIcon(pinRect, RuntimeDebugSymbols.PushPin, "PIN", state.Pinned ? Color.black : pinHover ? Color.white : new Color(0.78f, 0.78f, 0.78f, 1f), 17, true);
        if (pinClicked)
            SetWindowPinned(state, !state.Pinned);

        RuntimeDebugGuiUtility.DrawSolidRect(closeRect, closeHover ? new Color(1f, 1f, 1f, 0.18f) : new Color(1f, 1f, 1f, 0.07f));
        bool closeClicked = GUI.Button(closeRect, GUIContent.none, GUIStyle.none);
        RuntimeDebugGuiUtility.DrawMaterialIcon(closeRect, RuntimeDebugSymbols.Close, "X", closeHover ? Color.white : new Color(0.78f, 0.78f, 0.78f, 1f), 18, true);
        if (!closeClicked)
            return false;

        SetWindowOpen(state, false, context);
        return true;
    }

    private void DrawWindowOverlays(RuntimeDebugContext context)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            WindowState state = windows[i];
            if (ShouldDrawWindow(state, context))
                state.Window.DrawOverlay(context);
        }
    }

    private void SetWindowOpen(WindowState state, bool open, RuntimeDebugContext context)
    {
        if (state.Open == open)
            return;

        state.Open = open;
        if (open)
            state.Window.OnOpened(context);
        else
            state.Window.OnClosed();

        state.WasOpen = open;
    }

    private void SetWindowPinned(WindowState state, bool pinned)
    {
        if (state.Pinned == pinned)
            return;

        state.Pinned = pinned;
        if (visible || pinned || !state.WasOpen)
            return;

        state.Window.OnClosed();
        state.WasOpen = false;
    }

    private bool HasPinnedOpenWindows(RuntimeDebugContext context)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            WindowState state = windows[i];
            if (state.Open && state.Pinned && state.Window.IsAvailable(context))
                return true;
        }

        return false;
    }

    private void UpdatePauseState()
    {
        SetPausedByDebugUi(visible && pauseGameplay);
    }

    private void SetPausedByDebugUi(bool paused)
    {
        if (pausedByDebugUi == paused)
            return;

        pausedByDebugUi = paused;
        PauseStateChanged?.Invoke(pausedByDebugUi);
    }

    private void UpdatePointerBlockState()
    {
        if (!debugModeActive)
        {
            pointerBlocked = false;
            pointerCaptureActive = false;
            return;
        }

        RuntimeDebugContext context = GetContext();
        if (!visible && !HasPinnedOpenWindows(context))
        {
            pointerBlocked = false;
            pointerCaptureActive = false;
            return;
        }

        bool pointerPressed = IsPointerPressed();
        bool pointerOverDebugUi = ContainsGuiPoint(GetCurrentGuiPointerPosition(), context);
        if (!pointerPressed)
            pointerCaptureActive = false;
        else if (pointerOverDebugUi)
            pointerCaptureActive = true;

        pointerBlocked = pointerOverDebugUi || pointerCaptureActive;
    }

    private bool ContainsGuiPoint(Vector2 guiPosition)
    {
        return ContainsGuiPoint(guiPosition, GetContext());
    }

    private bool ContainsGuiPoint(Vector2 guiPosition, RuntimeDebugContext context)
    {
        if (visible && hubRect.Contains(guiPosition))
            return true;

        for (int i = 0; i < windows.Count; i++)
        {
            WindowState state = windows[i];
            if (state.Open && (visible || state.Pinned) && state.Window.IsAvailable(context) && state.Rect.Contains(guiPosition))
                return true;
        }

        return false;
    }

    private bool IsPointerBlockedNow()
    {
        RuntimeDebugContext context = GetContext();
        if (!visible && !HasPinnedOpenWindows(context))
            return false;

        return pointerBlocked || pointerCaptureActive || ContainsGuiPoint(GetCurrentGuiPointerPosition(), context);
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        color.a = Mathf.Clamp01(alpha);
        return color;
    }

    private static Vector2 GetCurrentGuiPointerPosition()
    {
        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            Vector2 screenPosition = mouse.position.ReadValue();
            return new Vector2(screenPosition.x, Screen.height - screenPosition.y);
        }

        UnityEngine.InputSystem.Pointer pointer = UnityEngine.InputSystem.Pointer.current;
        if (pointer != null)
        {
            Vector2 screenPosition = pointer.position.ReadValue();
            return new Vector2(screenPosition.x, Screen.height - screenPosition.y);
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        Vector2 fallbackPosition = Input.mousePosition;
        return new Vector2(fallbackPosition.x, Screen.height - fallbackPosition.y);
#else
        return Vector2.zero;
#endif
    }

    private static bool IsPointerPressed()
    {
        Mouse mouse = Mouse.current;
        if (mouse != null)
            return mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed;

        UnityEngine.InputSystem.Pointer pointer = UnityEngine.InputSystem.Pointer.current;
        if (pointer != null)
            return pointer.press.isPressed;

#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
#else
        return false;
#endif
    }

    private void EnsureStyles()
    {
        if (debugSkin != null && hubStyle != null && headerStyle != null && windowButtonStyle != null)
            return;

        CreateDebugSkin();

        hubStyle = new GUIStyle(debugSkin.window)
        {
            padding = new RectOffset(16, 16, 14, 14)
        };

        titleStyle = new GUIStyle(debugSkin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 15,
            normal = { textColor = Color.white },
            hover = { textColor = Color.white },
            active = { textColor = Color.white },
            focused = { textColor = Color.white }
        };

        headerStyle = new GUIStyle(debugSkin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 12,
            normal = { textColor = Color.white },
            hover = { textColor = Color.white },
            active = { textColor = Color.white },
            focused = { textColor = Color.white }
        };

        statusStyle = new GUIStyle(debugSkin.label)
        {
            fontSize = 11,
            normal = { textColor = new Color(0.78f, 0.78f, 0.78f, 1f) },
            hover = { textColor = new Color(0.9f, 0.9f, 0.9f, 1f) },
            active = { textColor = new Color(0.9f, 0.9f, 0.9f, 1f) },
            focused = { textColor = new Color(0.78f, 0.78f, 0.78f, 1f) }
        };

        keyStyle = new GUIStyle(statusStyle)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white },
            hover = { textColor = Color.white },
            active = { textColor = Color.white },
            focused = { textColor = Color.white }
        };

        windowButtonStyle = new GUIStyle(debugSkin.button)
        {
            normal = CreateStyleState(rowTexture, Color.white),
            hover = CreateStyleState(rowHoverTexture, Color.white),
            active = CreateStyleState(rowOpenTexture, Color.white),
            focused = CreateStyleState(rowHoverTexture, Color.white),
            onNormal = CreateStyleState(rowOpenTexture, Color.white),
            onHover = CreateStyleState(rowOpenTexture, Color.white),
            onActive = CreateStyleState(rowOpenTexture, Color.white),
            onFocused = CreateStyleState(rowOpenTexture, Color.white),
            border = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 3, 3)
        };

        windowButtonOpenStyle = new GUIStyle(windowButtonStyle)
        {
            normal = CreateStyleState(rowOpenTexture, Color.white),
            hover = CreateStyleState(rowOpenTexture, Color.white),
            active = CreateStyleState(rowHoverTexture, Color.white),
            focused = CreateStyleState(rowOpenTexture, Color.white),
            onNormal = CreateStyleState(rowOpenTexture, Color.white),
            onHover = CreateStyleState(rowOpenTexture, Color.white),
            onActive = CreateStyleState(rowOpenTexture, Color.white),
            onFocused = CreateStyleState(rowOpenTexture, Color.white)
        };

        chipStyle = new GUIStyle(debugSkin.box)
        {
            normal = CreateStyleState(chipTexture, Color.white),
            hover = CreateStyleState(rowHoverTexture, Color.white),
            active = CreateStyleState(chipTexture, Color.white),
            focused = CreateStyleState(chipTexture, Color.white),
            border = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 3, 3)
        };

        chipActiveStyle = new GUIStyle(chipStyle)
        {
            normal = CreateStyleState(chipActiveTexture, Color.white),
            hover = CreateStyleState(rowOpenTexture, Color.white),
            active = CreateStyleState(chipActiveTexture, Color.white),
            focused = CreateStyleState(chipActiveTexture, Color.white)
        };
    }

    private void CreateDebugSkin()
    {
        if (debugSkin != null)
            return;

        windowTexture = CreateBorderTexture(new Color(0.015f, 0.015f, 0.015f, 0.82f), new Color(1f, 1f, 1f, 0.38f));
        buttonTexture = CreateBorderTexture(new Color(0.055f, 0.055f, 0.055f, 0.74f), new Color(0.7f, 0.7f, 0.7f, 0.34f));
        buttonHoverTexture = CreateBorderTexture(new Color(0.13f, 0.13f, 0.13f, 0.88f), new Color(1f, 1f, 1f, 0.62f));
        buttonActiveTexture = CreateBorderTexture(new Color(0.92f, 0.92f, 0.92f, 0.94f), new Color(1f, 1f, 1f, 0.86f));
        rowTexture = CreateBorderTexture(new Color(0.035f, 0.035f, 0.035f, 0.58f), new Color(0.42f, 0.42f, 0.42f, 0.26f));
        rowHoverTexture = CreateBorderTexture(new Color(0.1f, 0.1f, 0.1f, 0.76f), new Color(0.82f, 0.82f, 0.82f, 0.5f));
        rowOpenTexture = CreateBorderTexture(new Color(0.045f, 0.045f, 0.045f, 0.86f), new Color(0.34f, 0.34f, 0.34f, 0.46f));
        chipTexture = CreateBorderTexture(new Color(0.03f, 0.03f, 0.03f, 0.46f), new Color(0.45f, 0.45f, 0.45f, 0.22f));
        chipActiveTexture = CreateBorderTexture(new Color(0.07f, 0.07f, 0.07f, 0.66f), new Color(0.72f, 0.72f, 0.72f, 0.34f));
        fieldTexture = CreateBorderTexture(new Color(0f, 0f, 0f, 0.68f), new Color(0.72f, 0.72f, 0.72f, 0.42f));
        fieldFocusTexture = CreateBorderTexture(new Color(0.035f, 0.035f, 0.035f, 0.82f), new Color(1f, 1f, 1f, 0.72f));
        boxTexture = CreateBorderTexture(new Color(0.03f, 0.03f, 0.03f, 0.48f), new Color(0.45f, 0.45f, 0.45f, 0.26f));
        scrollbarTexture = CreateBorderTexture(new Color(0.02f, 0.02f, 0.02f, 1f), new Color(0.25f, 0.25f, 0.25f, 1f));
        scrollbarThumbTexture = CreateBorderTexture(new Color(0.82f, 0.82f, 0.82f, 1f), Color.white);

        debugSkin = Instantiate(GUI.skin);
        debugSkin.hideFlags = HideFlags.HideAndDontSave;
        debugSkin.settings.cursorColor = Color.white;
        debugSkin.settings.selectionColor = new Color(1f, 1f, 1f, 0.28f);

        ConfigureWindowStyle();
        ConfigureLabelStyle();
        ConfigureButtonStyle();
        ConfigureFieldStyle();
        ConfigureBoxStyle();
        ConfigureToggleStyle();
        ConfigureScrollbarStyle(debugSkin.verticalScrollbar, debugSkin.verticalScrollbarThumb, true);
        ConfigureScrollbarStyle(debugSkin.horizontalScrollbar, debugSkin.horizontalScrollbarThumb, false);
        ConfigureScrollbarButtons(debugSkin.verticalScrollbarUpButton, debugSkin.verticalScrollbarDownButton);
        ConfigureScrollbarButtons(debugSkin.horizontalScrollbarLeftButton, debugSkin.horizontalScrollbarRightButton);
    }

    private void ConfigureWindowStyle()
    {
        debugSkin.window = new GUIStyle(debugSkin.window)
        {
            normal = CreateStyleState(windowTexture, Color.white),
            onNormal = CreateStyleState(windowTexture, Color.white),
            hover = CreateStyleState(windowTexture, Color.white),
            focused = CreateStyleState(windowTexture, Color.white),
            active = CreateStyleState(windowTexture, Color.white),
            border = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(16, 16, 38, 16),
            margin = new RectOffset(0, 0, 0, 0),
            alignment = TextAnchor.UpperCenter,
            fontStyle = FontStyle.Bold,
            fontSize = 13
        };
    }

    private void ConfigureLabelStyle()
    {
        debugSkin.label = new GUIStyle(debugSkin.label)
        {
            normal = CreateStyleState(null, Color.white),
            hover = CreateStyleState(null, Color.white),
            focused = CreateStyleState(null, Color.white),
            active = CreateStyleState(null, Color.white),
            padding = new RectOffset(2, 2, 3, 3)
        };
    }

    private void ConfigureButtonStyle()
    {
        debugSkin.button = new GUIStyle(debugSkin.button)
        {
            normal = CreateStyleState(buttonTexture, Color.white),
            hover = CreateStyleState(buttonHoverTexture, Color.white),
            active = CreateStyleState(buttonActiveTexture, Color.black),
            focused = CreateStyleState(buttonHoverTexture, Color.white),
            onNormal = CreateStyleState(buttonActiveTexture, Color.black),
            onHover = CreateStyleState(buttonActiveTexture, Color.black),
            onActive = CreateStyleState(buttonHoverTexture, Color.white),
            onFocused = CreateStyleState(buttonActiveTexture, Color.black),
            border = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(10, 10, 6, 6),
            margin = new RectOffset(2, 2, 3, 3),
            alignment = TextAnchor.MiddleCenter
        };
    }

    private void ConfigureFieldStyle()
    {
        debugSkin.textField = new GUIStyle(debugSkin.textField)
        {
            normal = CreateStyleState(fieldTexture, Color.white),
            hover = CreateStyleState(fieldFocusTexture, Color.white),
            focused = CreateStyleState(fieldFocusTexture, Color.white),
            active = CreateStyleState(fieldFocusTexture, Color.white),
            border = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(8, 8, 5, 5),
            margin = new RectOffset(2, 2, 3, 3)
        };

        debugSkin.textArea = new GUIStyle(debugSkin.textField);
    }

    private void ConfigureBoxStyle()
    {
        debugSkin.box = new GUIStyle(debugSkin.box)
        {
            normal = CreateStyleState(boxTexture, Color.white),
            hover = CreateStyleState(boxTexture, Color.white),
            focused = CreateStyleState(boxTexture, Color.white),
            active = CreateStyleState(boxTexture, Color.white),
            border = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(8, 8, 8, 8),
            margin = new RectOffset(2, 2, 3, 3)
        };
    }

    private void ConfigureToggleStyle()
    {
        debugSkin.toggle = new GUIStyle(debugSkin.button)
        {
            normal = CreateStyleState(buttonTexture, Color.white),
            hover = CreateStyleState(buttonHoverTexture, Color.white),
            active = CreateStyleState(buttonActiveTexture, Color.black),
            focused = CreateStyleState(buttonHoverTexture, Color.white),
            onNormal = CreateStyleState(buttonActiveTexture, Color.black),
            onHover = CreateStyleState(buttonActiveTexture, Color.black),
            onActive = CreateStyleState(buttonHoverTexture, Color.white),
            onFocused = CreateStyleState(buttonActiveTexture, Color.black),
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(10, 10, 6, 6),
            margin = new RectOffset(2, 2, 3, 3)
        };
    }

    private void ConfigureScrollbarStyle(GUIStyle scrollbar, GUIStyle thumb, bool vertical)
    {
        scrollbar.normal = CreateStyleState(scrollbarTexture, Color.white);
        scrollbar.hover = CreateStyleState(scrollbarTexture, Color.white);
        scrollbar.active = CreateStyleState(scrollbarTexture, Color.white);
        scrollbar.focused = CreateStyleState(scrollbarTexture, Color.white);
        scrollbar.border = new RectOffset(1, 1, 1, 1);
        scrollbar.margin = new RectOffset(2, 2, 2, 2);
        if (vertical)
            scrollbar.fixedWidth = 12f;
        else
            scrollbar.fixedHeight = 12f;

        thumb.normal = CreateStyleState(scrollbarThumbTexture, Color.black);
        thumb.hover = CreateStyleState(buttonActiveTexture, Color.black);
        thumb.active = CreateStyleState(buttonActiveTexture, Color.black);
        thumb.focused = CreateStyleState(scrollbarThumbTexture, Color.black);
        thumb.border = new RectOffset(1, 1, 1, 1);
    }

    private void ConfigureScrollbarButtons(GUIStyle firstButton, GUIStyle secondButton)
    {
        ConfigureScrollbarButton(firstButton);
        ConfigureScrollbarButton(secondButton);
    }

    private void ConfigureScrollbarButton(GUIStyle style)
    {
        style.normal = CreateStyleState(buttonTexture, Color.white);
        style.hover = CreateStyleState(buttonHoverTexture, Color.white);
        style.active = CreateStyleState(buttonActiveTexture, Color.black);
        style.focused = CreateStyleState(buttonTexture, Color.white);
        style.border = new RectOffset(1, 1, 1, 1);
        style.fixedWidth = 0f;
        style.fixedHeight = 0f;
    }

    private static GUIStyleState CreateStyleState(Texture2D background, Color textColor)
    {
        return new GUIStyleState
        {
            background = background,
            textColor = textColor
        };
    }

    private static Texture2D CreateBorderTexture(Color fillColor, Color borderColor)
    {
        Texture2D texture = new Texture2D(3, 3, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                bool isBorder = x == 0 || x == 2 || y == 0 || y == 2;
                texture.SetPixel(x, y, isBorder ? borderColor : fillColor);
            }
        }

        texture.Apply(false, true);
        return texture;
    }

    private void DestroyStyleResources()
    {
        DestroyStyleObject(debugSkin);
        DestroyStyleObject(windowTexture);
        DestroyStyleObject(buttonTexture);
        DestroyStyleObject(buttonHoverTexture);
        DestroyStyleObject(buttonActiveTexture);
        DestroyStyleObject(rowTexture);
        DestroyStyleObject(rowHoverTexture);
        DestroyStyleObject(rowOpenTexture);
        DestroyStyleObject(chipTexture);
        DestroyStyleObject(chipActiveTexture);
        DestroyStyleObject(fieldTexture);
        DestroyStyleObject(fieldFocusTexture);
        DestroyStyleObject(boxTexture);
        DestroyStyleObject(scrollbarTexture);
        DestroyStyleObject(scrollbarThumbTexture);
        debugSkin = null;
        windowTexture = null;
        buttonTexture = null;
        buttonHoverTexture = null;
        buttonActiveTexture = null;
        rowTexture = null;
        rowHoverTexture = null;
        rowOpenTexture = null;
        chipTexture = null;
        chipActiveTexture = null;
        fieldTexture = null;
        fieldFocusTexture = null;
        boxTexture = null;
        scrollbarTexture = null;
        scrollbarThumbTexture = null;
        hubStyle = null;
        titleStyle = null;
        headerStyle = null;
        statusStyle = null;
        keyStyle = null;
        windowButtonStyle = null;
        windowButtonOpenStyle = null;
        chipStyle = null;
        chipActiveStyle = null;
    }

    private static void DestroyStyleObject(UnityEngine.Object styleObject)
    {
        if (styleObject == null)
            return;

        if (Application.isPlaying)
            Destroy(styleObject);
        else
            DestroyImmediate(styleObject);
    }
}

public static class RuntimeDebugGuiUtility
{
    private const string MaterialSymbolsStandardResourcePath = "RuntimeDebugUI/MaterialSymbols-Standard";
    private const string MaterialSymbolsFilledResourcePath = "RuntimeDebugUI/MaterialSymbols-Filled";

    private static Font standardMaterialSymbolsFont;
    private static Font filledMaterialSymbolsFont;
    private static GUIStyle materialIconStyle;
    private static GUIStyle materialIconFallbackStyle;
    private static GUIStyle sectionHeaderStyle;
    private static readonly GUIContent MaterialIconContent = new GUIContent();
    private static bool materialSymbolsLoadAttempted;

    public static Rect ClampRectToScreen(Rect rect)
    {
        float width = Mathf.Clamp(rect.width, 220f, Mathf.Max(220f, Screen.width - 24f));
        float height = Mathf.Clamp(rect.height, 140f, Mathf.Max(140f, Screen.height - 24f));
        float x = Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, Screen.width - width));
        float y = Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, Screen.height - height));
        return new Rect(x, y, width, height);
    }

    public static void TextField(string label, ref string value, float labelWidth = 80f)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(labelWidth));
        value = GUILayout.TextField(value ?? string.Empty);
        GUILayout.EndHorizontal();
    }

    public static bool TryParseInt(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    public static bool TryParseFloat(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    public static string FormatFloat(float value)
    {
        if (float.IsPositiveInfinity(value))
            return "Infinity";
        if (float.IsNegativeInfinity(value))
            return "-Infinity";

        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public static string EmptyFallback(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value;
    }

    public static bool PassesFilter(string label, string assetName, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        string term = filter.Trim();
        return (!string.IsNullOrWhiteSpace(label) && label.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(assetName) && assetName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static void DrawSolidRect(Rect rect, Color color)
    {
        if (Event.current != null && Event.current.type != EventType.Repaint)
            return;

        Color previousColor = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previousColor;
    }

    public static void DrawRectOutline(Rect rect, Color color)
    {
        DrawSolidRect(new Rect(rect.x, rect.y, rect.width, 1f), color);
        DrawSolidRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
        DrawSolidRect(new Rect(rect.x, rect.y, 1f, rect.height), color);
        DrawSolidRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
    }

    public static void DrawMaterialIcon(Rect rect, string icon, string fallback, Color color, int fontSize, bool filled)
    {
        Font font = GetMaterialSymbolsFont(filled);
        GUIStyle style = font != null ? GetMaterialIconStyle(font, fontSize) : GetMaterialIconFallbackStyle(fontSize);
        MaterialIconContent.text = font != null && !string.IsNullOrWhiteSpace(icon) ? icon : fallback;
        MaterialIconContent.tooltip = string.Empty;

        Color previousContentColor = GUI.contentColor;
        GUI.contentColor = color;
        GUI.Label(rect, MaterialIconContent, style);
        GUI.contentColor = previousContentColor;
    }

    public static void SectionHeader(string label, Color accentColor, string icon, string fallback, bool filled)
    {
        Rect rect = GUILayoutUtility.GetRect(0f, 30f, GUILayout.ExpandWidth(true));
        Rect iconRect = new Rect(rect.x, rect.y + 5f, 20f, 20f);
        if (Event.current.type == EventType.Repaint)
        {
            DrawSolidRect(iconRect, accentColor);
            DrawSolidRect(new Rect(rect.x, rect.yMax - 4f, rect.width, 1f), new Color(accentColor.r, accentColor.g, accentColor.b, 0.42f));
        }

        DrawMaterialIcon(iconRect, icon, fallback, Color.black, 16, filled);

        Color previousContentColor = GUI.contentColor;
        GUI.contentColor = Color.white;
        GUI.Label(new Rect(rect.x + 30f, rect.y + 4f, rect.width - 30f, 22f), label, GetSectionHeaderStyle());
        GUI.contentColor = previousContentColor;
        GUILayout.Space(2f);
    }

    private static GUIStyle GetSectionHeaderStyle()
    {
        if (sectionHeaderStyle != null)
            return sectionHeaderStyle;

        sectionHeaderStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 13,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Color.white },
            hover = { textColor = Color.white },
            active = { textColor = Color.white },
            focused = { textColor = Color.white }
        };
        return sectionHeaderStyle;
    }

    private static Font GetMaterialSymbolsFont(bool filled)
    {
        if (!materialSymbolsLoadAttempted)
        {
            materialSymbolsLoadAttempted = true;
            standardMaterialSymbolsFont = Resources.Load<Font>(MaterialSymbolsStandardResourcePath);
            filledMaterialSymbolsFont = Resources.Load<Font>(MaterialSymbolsFilledResourcePath);
        }

        return filled && filledMaterialSymbolsFont != null ? filledMaterialSymbolsFont : standardMaterialSymbolsFont;
    }

    private static GUIStyle GetMaterialIconStyle(Font font, int fontSize)
    {
        if (materialIconStyle == null)
            materialIconStyle = new GUIStyle(GUI.skin.label);

        materialIconStyle.font = font;
        materialIconStyle.fontSize = fontSize;
        materialIconStyle.fontStyle = FontStyle.Normal;
        materialIconStyle.alignment = TextAnchor.MiddleCenter;
        materialIconStyle.clipping = TextClipping.Clip;
        materialIconStyle.padding = new RectOffset(0, 0, 0, 0);
        materialIconStyle.margin = new RectOffset(0, 0, 0, 0);
        materialIconStyle.normal.textColor = GUI.contentColor;
        materialIconStyle.hover.textColor = GUI.contentColor;
        materialIconStyle.active.textColor = GUI.contentColor;
        materialIconStyle.focused.textColor = GUI.contentColor;
        return materialIconStyle;
    }

    private static GUIStyle GetMaterialIconFallbackStyle(int fontSize)
    {
        if (materialIconFallbackStyle == null)
            materialIconFallbackStyle = new GUIStyle(GUI.skin.label);

        materialIconFallbackStyle.font = GUI.skin.label.font;
        materialIconFallbackStyle.fontSize = Mathf.Clamp(fontSize - 7, 8, 11);
        materialIconFallbackStyle.fontStyle = FontStyle.Bold;
        materialIconFallbackStyle.alignment = TextAnchor.MiddleCenter;
        materialIconFallbackStyle.clipping = TextClipping.Clip;
        materialIconFallbackStyle.padding = new RectOffset(1, 1, 0, 0);
        materialIconFallbackStyle.margin = new RectOffset(0, 0, 0, 0);
        materialIconFallbackStyle.normal.textColor = GUI.contentColor;
        materialIconFallbackStyle.hover.textColor = GUI.contentColor;
        materialIconFallbackStyle.active.textColor = GUI.contentColor;
        materialIconFallbackStyle.focused.textColor = GUI.contentColor;
        return materialIconFallbackStyle;
    }

    public static void DrawSprite(Rect rect, Sprite sprite, ScaleMode scaleMode = ScaleMode.ScaleToFit)
    {
        if (sprite == null || sprite.texture == null)
        {
            GUI.Box(rect, string.Empty);
            return;
        }

        Rect textureRect;
        try
        {
            textureRect = sprite.textureRect;
        }
        catch (InvalidOperationException)
        {
            textureRect = sprite.rect;
        }

        Texture2D texture = sprite.texture;
        Rect uv = new Rect(
            textureRect.x / texture.width,
            textureRect.y / texture.height,
            textureRect.width / texture.width,
            textureRect.height / texture.height);

        Rect drawRect = rect;
        if (scaleMode == ScaleMode.ScaleToFit && textureRect.width > 0f && textureRect.height > 0f)
        {
            float spriteAspect = textureRect.width / textureRect.height;
            float rectAspect = rect.width / rect.height;
            if (rectAspect > spriteAspect)
            {
                float width = rect.height * spriteAspect;
                drawRect = new Rect(rect.x + (rect.width - width) * 0.5f, rect.y, width, rect.height);
            }
            else
            {
                float height = rect.width / spriteAspect;
                drawRect = new Rect(rect.x, rect.y + (rect.height - height) * 0.5f, rect.width, height);
            }
        }

        GUI.DrawTextureWithTexCoords(drawRect, texture, uv, true);
    }
}
