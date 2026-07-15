using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ConsoleCommandAttribute : Attribute
{
    public readonly string Command;
    public readonly string Description;

    public ConsoleCommandAttribute(string command, string description = "")
    {
        Command = command;
        Description = description;
    }
}

[DefaultExecutionOrder(-31000)]
public sealed class RuntimeConsole : MonoBehaviour
{
    private sealed class CommandEntry
    {
        public string Name;
        public string Description;
        public string Syntax;
        public string[] ParamSyntax;
        public bool LastParamIsString;
        public MethodInfo Method;
        public object Owner;
    }

    private struct LogLine
    {
        public string Formatted;
        public Color Color;
        public LogType Type;
    }

    public static RuntimeConsole instance;
    public static bool show;

    private const Key ToggleKey = Key.Backquote;
    private const int WindowId = 939990;
    private const int MaxLogLines = 400;
    private const int MaxSuggestions = 8;
    private const float InputRowHeight = 30f;
    private const float SuggestionRowHeight = 22f;
    private const string InputControlName = "RuntimeConsoleInput";
    private static readonly Color AccentColor = new Color(0.55f, 0.95f, 0.55f, 1f);
    private static readonly Color EchoColor = new Color(0.55f, 0.85f, 1f, 1f);
    private static readonly Color TextColor = new Color(0.88f, 0.88f, 0.88f, 1f);
    private static readonly Color WarningColor = new Color(1f, 0.85f, 0.4f, 1f);
    private static readonly Color ErrorColor = new Color(1f, 0.45f, 0.45f, 1f);
    private const string AccentHex = "8CF28C";
    private const string NameHex = "E8E8E8";
    private const string ArgsHex = "8A8A8A";
    private const string DescHex = "6E6E6E";
    private const string TimeHex = "5A5A5A";

    private static readonly List<CommandEntry> commands = new List<CommandEntry>(64);
    private static readonly List<LogLine> logLines = new List<LogLine>(MaxLogLines + 1);
    private static readonly List<string> history = new List<string>(64);
    private static readonly List<CommandEntry> prefixMatches = new List<CommandEntry>(32);
    private static readonly List<CommandEntry> containsMatches = new List<CommandEntry>(32);
    private static bool scrollToBottom;
    private static Font monoFont;
    private static bool monoFontAttempted;

    private readonly List<CommandEntry> suggestions = new List<CommandEntry>(MaxSuggestions);
    private int hiddenMatches;
    private Rect windowRect = new Rect(12f, 12f, 780f, 440f);
    private string input = string.Empty;
    private int historyIndex = -1;
    private int suggestionIndex = -1;
    private Vector2 logScroll;
    private bool focusInput;
    private Vector2 resizeGrabOffset;
    private GUIStyle logStyle;
    private GUIStyle titleStyle;
    private GUIStyle inputStyle;
    private GUIStyle suggestionStyle;
    private GUIStyle footerStyle;
    private GUIStyle placeholderStyle;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (instance != null)
            return;

        GameObject host = new GameObject(nameof(RuntimeConsole));
        host.AddComponent<RuntimeConsole>();
        DontDestroyOnLoad(host);
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
        Register(this);
    }

    private void OnEnable()
    {
        Application.logMessageReceived += HandleLog;
    }

    private void OnDisable()
    {
        Application.logMessageReceived -= HandleLog;
    }

    private void OnDestroy()
    {
        Unregister(this);
        if (instance != this)
            return;

        instance = null;
        show = false;
    }

    public static void Register(object target)
    {
        if (target == null)
            return;

        Unregister(target);

        MethodInfo[] methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            ConsoleCommandAttribute attribute = method.GetCustomAttribute<ConsoleCommandAttribute>();
            if (attribute == null)
                continue;

            commands.Add(new CommandEntry
            {
                Name = attribute.Command,
                Description = attribute.Description,
                Syntax = BuildSyntax(attribute.Command, method),
                ParamSyntax = BuildParamSyntax(method, out bool lastIsString),
                LastParamIsString = lastIsString,
                Method = method,
                Owner = target
            });
        }

        commands.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static void Unregister(object target)
    {
        if (target == null)
            return;

        commands.RemoveAll(entry => ReferenceEquals(entry.Owner, target));
    }

    public static void ExecuteCommand(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return;

        AddLog("> " + commandLine, EchoColor);

        List<string> tokens = Tokenize(commandLine);
        string name = tokens[0];
        tokens.RemoveAt(0);

        CommandEntry match = null;
        string error = null;
        for (int i = 0; i < commands.Count; i++)
        {
            CommandEntry entry = commands[i];
            if (!string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.Owner is UnityEngine.Object unityOwner && unityOwner == null)
                continue;

            match = entry;
            if (TryBuildArguments(entry.Method, tokens, out object[] args, out error))
            {
                Invoke(entry, args);
                return;
            }
        }

        if (match == null)
        {
            Debug.LogWarning($"Unknown command '{name}'. Type 'help' to list commands.");
            return;
        }

        Debug.LogWarning($"{error}\nUsage: {match.Syntax}");
    }

    [ConsoleCommand("help", "List all available commands")]
    private void Help()
    {
        StringBuilder builder = new StringBuilder("Available commands:");
        for (int i = 0; i < commands.Count; i++)
        {
            builder.Append('\n');
            builder.Append(commands[i].Syntax);
            if (!string.IsNullOrEmpty(commands[i].Description))
                builder.Append("  -  ").Append(commands[i].Description);
        }

        Debug.Log(builder.ToString());
    }

    [ConsoleCommand("clear", "Clear the console log")]
    private void Clear()
    {
        logLines.Clear();
    }

    private static void Invoke(CommandEntry entry, object[] args)
    {
        try
        {
            object result = entry.Method.Invoke(entry.Method.IsStatic ? null : entry.Owner, args);
            if (result != null)
                Debug.Log(result.ToString());
        }
        catch (TargetInvocationException exception)
        {
            Debug.LogError(exception.InnerException?.ToString() ?? exception.ToString());
        }
        catch (Exception exception)
        {
            Debug.LogError(exception.ToString());
        }
    }

    private static bool TryBuildArguments(MethodInfo method, List<string> tokens, out object[] args, out string error)
    {
        ParameterInfo[] parameters = method.GetParameters();
        args = new object[parameters.Length];
        error = null;

        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo parameter = parameters[i];
            if (i >= tokens.Count)
            {
                if (parameter.HasDefaultValue)
                {
                    args[i] = parameter.DefaultValue;
                    continue;
                }

                error = $"Missing argument '{parameter.Name}'.";
                return false;
            }

            bool joinsRemainder = i == parameters.Length - 1 && parameter.ParameterType == typeof(string) && tokens.Count > parameters.Length;
            string token = joinsRemainder ? string.Join(" ", tokens.GetRange(i, tokens.Count - i)) : tokens[i];

            if (!TryConvert(token, parameter.ParameterType, out args[i], out error))
                return false;
        }

        if (tokens.Count > parameters.Length && (parameters.Length == 0 || parameters[parameters.Length - 1].ParameterType != typeof(string)))
        {
            error = "Too many arguments.";
            return false;
        }

        return true;
    }

    private static bool TryConvert(string token, Type type, out object value, out string error)
    {
        value = null;
        error = null;

        try
        {
            if (type == typeof(string))
            {
                value = token;
                return true;
            }

            if (type == typeof(bool))
            {
                if (token == "1")
                {
                    value = true;
                    return true;
                }

                if (token == "0")
                {
                    value = false;
                    return true;
                }

                value = bool.Parse(token);
                return true;
            }

            if (type.IsEnum)
            {
                try
                {
                    value = Enum.Parse(type, token, true);
                    return true;
                }
                catch (ArgumentException)
                {
                }

                foreach (object enumValue in Enum.GetValues(type))
                {
                    if (enumValue.ToString().StartsWith(token, StringComparison.OrdinalIgnoreCase))
                    {
                        value = enumValue;
                        return true;
                    }
                }

                foreach (object enumValue in Enum.GetValues(type))
                {
                    if (enumValue.ToString().IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        value = enumValue;
                        return true;
                    }
                }

                error = $"'{token}' is not a valid {type.Name}.";
                return false;
            }

            value = Convert.ChangeType(token, type, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            error = $"Could not convert '{token}' to {TypeLabel(type)}.";
            return false;
        }
    }

    private static string BuildSyntax(string name, MethodInfo method)
    {
        StringBuilder builder = new StringBuilder(name);
        ParameterInfo[] parameters = method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            builder.Append(' ');
            builder.Append(DescribeParameter(parameters[i], '<', '>', '[', ']'));
        }

        return builder.ToString();
    }

    private static string[] BuildParamSyntax(MethodInfo method, out bool lastIsString)
    {
        ParameterInfo[] parameters = method.GetParameters();
        lastIsString = parameters.Length > 0 && parameters[parameters.Length - 1].ParameterType == typeof(string);
        string[] parts = new string[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
            parts[i] = DescribeParameter(parameters[i], '‹', '›', '[', ']');

        return parts;
    }

    private static string DescribeParameter(ParameterInfo parameter, char openRequired, char closeRequired, char openOptional, char closeOptional)
    {
        return parameter.HasDefaultValue
            ? $"{openOptional}{TypeLabel(parameter.ParameterType)} {parameter.Name}={FormatDefault(parameter.DefaultValue)}{closeOptional}"
            : $"{openRequired}{TypeLabel(parameter.ParameterType)} {parameter.Name}{closeRequired}";
    }

    private static string FormatDefault(object value)
    {
        if (value == null)
            return "null";
        if (value is bool boolValue)
            return boolValue ? "true" : "false";

        return value.ToString();
    }

    private static string TypeLabel(Type type)
    {
        if (type == typeof(int))
            return "int";
        if (type == typeof(float))
            return "float";
        if (type == typeof(bool))
            return "bool";
        if (type == typeof(string))
            return "string";

        return type.Name;
    }

    private static List<string> Tokenize(string commandLine)
    {
        List<string> tokens = new List<string>(8);
        StringBuilder current = new StringBuilder();
        bool inQuotes = false;
        bool quoted = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char character = commandLine[i];
            if (character == '"')
            {
                inQuotes = !inQuotes;
                quoted = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(character))
            {
                if (current.Length > 0 || quoted)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                quoted = false;
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0 || quoted)
            tokens.Add(current.ToString());

        return tokens;
    }

    private void HandleLog(string condition, string stackTrace, LogType type)
    {
        Color color = type == LogType.Warning
            ? WarningColor
            : type == LogType.Log
                ? TextColor
                : ErrorColor;
        AddLog(condition, color, type);
    }

    private static void AddLog(string text, Color color, LogType type = LogType.Log)
    {
        string formatted = $"<color=#{TimeHex}>{DateTime.Now:HH:mm:ss}</color> <color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";
        logLines.Add(new LogLine { Formatted = formatted, Color = color, Type = type });
        if (logLines.Count > MaxLogLines)
            logLines.RemoveRange(0, logLines.Count - MaxLogLines);

        scrollToBottom = true;
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard[ToggleKey].wasPressedThisFrame)
            SetShow(!show);
    }

    private void SetShow(bool value)
    {
        if (show == value)
            return;

        show = value;
        if (!show)
            return;

        focusInput = true;
        historyIndex = -1;
        suggestionIndex = -1;
        scrollToBottom = true;
    }

    private void OnGUI()
    {
        if (!show)
            return;

        GUISkin previousSkin = GUI.skin;
        Color previousColor = GUI.color;
        Color previousContentColor = GUI.contentColor;
        Color previousBackgroundColor = GUI.backgroundColor;
        GUI.skin = RuntimeDebugUI.AcquireSkin();
        GUI.depth = -20500;
        GUI.color = Color.white;
        GUI.contentColor = Color.white;
        GUI.backgroundColor = Color.white;

        try
        {
            EnsureStyles();
            windowRect = RuntimeDebugGuiUtility.ClampRectToScreen(windowRect);
            windowRect = GUI.Window(WindowId, windowRect, DrawWindow, string.Empty);
        }
        finally
        {
            GUI.skin = previousSkin;
            GUI.color = previousColor;
            GUI.contentColor = previousContentColor;
            GUI.backgroundColor = previousBackgroundColor;
        }
    }

    private void DrawWindow(int id)
    {
        DrawChrome();
        UpdateSuggestions();
        HandleKeys();
        DrawLog();
        DrawInputRow();
        DrawSuggestionsPopup();
        DrawResizeGrip();
        GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, windowRect.width - 40f), 32f));
    }

    private void DrawChrome()
    {
        Rect titleBarRect = new Rect(0f, 0f, windowRect.width, 32f);
        Rect iconRect = new Rect(12f, 6f, 20f, 20f);
        Rect titleRect = new Rect(iconRect.xMax + 10f, 6f, Mathf.Max(0f, windowRect.width - iconRect.xMax - 26f), 20f);

        RuntimeDebugGuiUtility.DrawSolidRect(new Rect(0f, 0f, 5f, windowRect.height), AccentColor);
        RuntimeDebugGuiUtility.DrawSolidRect(titleBarRect, new Color(1f, 1f, 1f, 0.035f));
        RuntimeDebugGuiUtility.DrawSolidRect(new Rect(0f, 31f, windowRect.width, 1f), new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.5f));
        RuntimeDebugGuiUtility.DrawSolidRect(iconRect, AccentColor);
        RuntimeDebugGuiUtility.DrawMaterialIcon(iconRect, RuntimeDebugSymbols.Terminal, "CON", Color.black, 16, true);

        Color previousContentColor = GUI.contentColor;
        GUI.contentColor = Color.white;
        GUI.Label(titleRect, "Console", titleStyle);
        GUI.contentColor = previousContentColor;
    }

    private void HandleKeys()
    {
        Event evt = Event.current;
        if (evt == null || evt.type != EventType.KeyDown)
            return;

        if (evt.keyCode == KeyCode.Escape)
        {
            SetShow(false);
            evt.Use();
            return;
        }

        if (GUI.GetNameOfFocusedControl() != InputControlName)
            return;

        switch (evt.keyCode)
        {
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                Submit();
                evt.Use();
                break;
            case KeyCode.Tab:
                AcceptSuggestion();
                evt.Use();
                break;
            case KeyCode.UpArrow:
                if (suggestions.Count > 0 && !input.Contains(" "))
                    CycleSuggestion(-1);
                else
                    NavigateHistory(-1);
                evt.Use();
                break;
            case KeyCode.DownArrow:
                if (suggestions.Count > 0 && !input.Contains(" "))
                    CycleSuggestion(1);
                else
                    NavigateHistory(1);
                evt.Use();
                break;
        }
    }

    private void DrawLog()
    {
        if (scrollToBottom)
        {
            logScroll.y = float.MaxValue;
            if (Event.current.type == EventType.Repaint)
                scrollToBottom = false;
        }

        logScroll = GUILayout.BeginScrollView(logScroll, false, true, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandHeight(true));
        if (logLines.Count == 0)
        {
            GUILayout.Label("Type a command — Tab autocompletes, 'help' lists everything.", placeholderStyle);
        }

        for (int i = 0; i < logLines.Count; i++)
        {
            GUILayout.Label(logLines[i].Formatted, logStyle);
            if (Event.current.type != EventType.Repaint)
                continue;

            LogType type = logLines[i].Type;
            if (type == LogType.Log)
                continue;

            Rect rowRect = GUILayoutUtility.GetLastRect();
            RuntimeDebugGuiUtility.DrawSolidRect(new Rect(rowRect.x - 6f, rowRect.y + 3f, 3f, rowRect.height - 6f), logLines[i].Color);
        }

        GUILayout.EndScrollView();
    }

    private void DrawInputRow()
    {
        GUILayout.Space(4f);
        GUILayout.BeginHorizontal(GUILayout.Height(InputRowHeight - 4f));
        Rect promptRect = GUILayoutUtility.GetRect(18f, 26f, GUILayout.Width(18f));

        GUI.SetNextControlName(InputControlName);
        string next = GUILayout.TextField(input, inputStyle, GUILayout.ExpandWidth(true), GUILayout.Height(26f));
        if (Event.current.type == EventType.Repaint)
        {
            Rect fieldRect = GUILayoutUtility.GetLastRect();
            RuntimeDebugGuiUtility.DrawMaterialIcon(new Rect(promptRect.x, fieldRect.y + (fieldRect.height - 18f) * 0.5f, 18f, 18f), RuntimeDebugSymbols.ChevronRightE5CC, ">", AccentColor, 16, false);
        }
        next = next.Replace("`", string.Empty).Replace("~", string.Empty).Replace("\t", string.Empty).Replace("\n", string.Empty);
        if (next != input)
        {
            input = next;
            historyIndex = -1;
            suggestionIndex = -1;
        }

        GUILayout.EndHorizontal();

        if (!focusInput)
            return;

        GUI.FocusControl(InputControlName);
        if (Event.current.type == EventType.Repaint)
            focusInput = false;
    }

    private void DrawSuggestionsPopup()
    {
        if (suggestions.Count == 0)
            return;

        bool hintMode = input.Contains(" ");
        string typed = input.Trim();
        float footerHeight = hintMode ? 0f : 18f;
        float popupHeight = suggestions.Count * SuggestionRowHeight + footerHeight + 8f;
        float inputTop = windowRect.height - 16f - InputRowHeight;
        float popupY = inputTop - popupHeight - 4f;
        if (popupY < 36f)
        {
            popupHeight = inputTop - 4f - 36f;
            popupY = 36f;
        }

        Rect popupRect = new Rect(16f, popupY, windowRect.width - 32f, popupHeight);
        RuntimeDebugGuiUtility.DrawSolidRect(popupRect, new Color(0.02f, 0.02f, 0.02f, 0.97f));
        RuntimeDebugGuiUtility.DrawRectOutline(popupRect, new Color(1f, 1f, 1f, 0.28f));
        RuntimeDebugGuiUtility.DrawSolidRect(new Rect(popupRect.x, popupRect.y, 3f, popupRect.height), new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.75f));

        GUI.BeginGroup(popupRect);
        Event evt = Event.current;
        Color previousContentColor = GUI.contentColor;
        GUI.contentColor = Color.white;

        for (int i = 0; i < suggestions.Count; i++)
        {
            CommandEntry entry = suggestions[i];
            Rect rowRect = new Rect(4f, 4f + i * SuggestionRowHeight, popupRect.width - 8f, SuggestionRowHeight);
            bool selected = !hintMode && i == suggestionIndex;
            bool hover = !hintMode && evt != null && rowRect.Contains(evt.mousePosition);

            if (selected)
            {
                RuntimeDebugGuiUtility.DrawSolidRect(rowRect, new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.14f));
                RuntimeDebugGuiUtility.DrawSolidRect(new Rect(rowRect.x, rowRect.y, 2f, rowRect.height), AccentColor);
            }
            else if (hover)
            {
                RuntimeDebugGuiUtility.DrawSolidRect(rowRect, new Color(1f, 1f, 1f, 0.06f));
            }

            string label = hintMode ? BuildHintRichText(entry) : BuildSuggestionRichText(entry, typed);
            GUI.Label(new Rect(rowRect.x + 8f, rowRect.y, rowRect.width - 12f, rowRect.height), label, suggestionStyle);

            if (!hintMode && evt != null && evt.type == EventType.MouseDown && evt.button == 0 && rowRect.Contains(evt.mousePosition))
            {
                SetInput(entry.Name + " ");
                focusInput = true;
                evt.Use();
            }
        }

        if (!hintMode)
        {
            string footer = "Tab complete · ↑↓ select · Enter run";
            if (hiddenMatches > 0)
                footer += $" · +{hiddenMatches} more";
            GUI.Label(new Rect(12f, popupRect.height - footerHeight - 2f, popupRect.width - 20f, footerHeight), footer, footerStyle);
        }

        GUI.contentColor = previousContentColor;
        GUI.EndGroup();
    }

    private string BuildSuggestionRichText(CommandEntry entry, string typed)
    {
        string name = entry.Name;
        int matchIndex = typed.Length > 0 ? name.IndexOf(typed, StringComparison.OrdinalIgnoreCase) : -1;
        string highlighted = matchIndex < 0
            ? name
            : $"{name.Substring(0, matchIndex)}<b><color=#{AccentHex}>{name.Substring(matchIndex, typed.Length)}</color></b>{name.Substring(matchIndex + typed.Length)}";

        StringBuilder builder = new StringBuilder();
        builder.Append($"<color=#{NameHex}>").Append(highlighted).Append("</color>");
        if (entry.ParamSyntax.Length > 0)
            builder.Append($" <color=#{ArgsHex}>").Append(string.Join(" ", entry.ParamSyntax)).Append("</color>");
        if (!string.IsNullOrEmpty(entry.Description))
            builder.Append($"  <color=#{DescHex}>— ").Append(entry.Description).Append("</color>");

        return builder.ToString();
    }

    private string BuildHintRichText(CommandEntry entry)
    {
        List<string> tokens = Tokenize(input);
        bool endsWithSpace = input.Length > 0 && char.IsWhiteSpace(input[input.Length - 1]);
        int active = tokens.Count - 1 - (endsWithSpace ? 0 : 1);
        if (active >= entry.ParamSyntax.Length)
            active = entry.LastParamIsString ? entry.ParamSyntax.Length - 1 : -1;

        StringBuilder builder = new StringBuilder();
        builder.Append($"<color=#{NameHex}>").Append(entry.Name).Append("</color>");
        for (int i = 0; i < entry.ParamSyntax.Length; i++)
        {
            builder.Append(' ');
            builder.Append(i == active
                ? $"<b><color=#{AccentHex}>{entry.ParamSyntax[i]}</color></b>"
                : $"<color=#{ArgsHex}>{entry.ParamSyntax[i]}</color>");
        }

        if (!string.IsNullOrEmpty(entry.Description))
            builder.Append($"  <color=#{DescHex}>— ").Append(entry.Description).Append("</color>");

        return builder.ToString();
    }

    private void DrawResizeGrip()
    {
        Rect gripRect = new Rect(windowRect.width - 20f, windowRect.height - 20f, 18f, 18f);
        int controlId = GUIUtility.GetControlID(FocusType.Passive);
        Event evt = Event.current;
        switch (evt.GetTypeForControl(controlId))
        {
            case EventType.MouseDown:
                if (evt.button == 0 && gripRect.Contains(evt.mousePosition))
                {
                    GUIUtility.hotControl = controlId;
                    resizeGrabOffset = new Vector2(windowRect.width - evt.mousePosition.x, windowRect.height - evt.mousePosition.y);
                    evt.Use();
                }
                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl == controlId)
                {
                    windowRect.width = Mathf.Clamp(evt.mousePosition.x + resizeGrabOffset.x, 480f, Screen.width);
                    windowRect.height = Mathf.Clamp(evt.mousePosition.y + resizeGrabOffset.y, 260f, Screen.height);
                    evt.Use();
                }
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == controlId)
                {
                    GUIUtility.hotControl = 0;
                    evt.Use();
                }
                break;
        }

        bool gripActive = GUIUtility.hotControl == controlId;
        bool gripHover = gripRect.Contains(evt.mousePosition);
        Color gripColor = new Color(1f, 1f, 1f, gripActive || gripHover ? 0.8f : 0.3f);
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3 - i; j++)
            {
                RuntimeDebugGuiUtility.DrawSolidRect(new Rect(windowRect.width - 7f - i * 4f, windowRect.height - 7f - j * 4f, 2f, 2f), gripColor);
            }
        }
    }

    private void UpdateSuggestions()
    {
        suggestions.Clear();
        hiddenMatches = 0;
        string trimmed = input.TrimStart();
        if (trimmed.Length == 0)
        {
            suggestionIndex = -1;
            return;
        }

        int space = trimmed.IndexOf(' ');
        if (space >= 0)
        {
            string name = trimmed.Substring(0, space);
            for (int i = 0; i < commands.Count; i++)
            {
                if (string.Equals(commands[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    suggestions.Add(commands[i]);
            }

            suggestionIndex = -1;
            return;
        }

        prefixMatches.Clear();
        containsMatches.Clear();
        for (int i = 0; i < commands.Count; i++)
        {
            CommandEntry entry = commands[i];
            if (entry.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                prefixMatches.Add(entry);
            else if (entry.Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                containsMatches.Add(entry);
        }

        int total = prefixMatches.Count + containsMatches.Count;
        for (int i = 0; i < prefixMatches.Count && suggestions.Count < MaxSuggestions; i++)
            suggestions.Add(prefixMatches[i]);
        for (int i = 0; i < containsMatches.Count && suggestions.Count < MaxSuggestions; i++)
            suggestions.Add(containsMatches[i]);

        hiddenMatches = total - suggestions.Count;

        if (suggestions.Count == 0)
            suggestionIndex = -1;
        else if (suggestionIndex < 0)
            suggestionIndex = 0;
        else if (suggestionIndex >= suggestions.Count)
            suggestionIndex = suggestions.Count - 1;
    }

    private void Submit()
    {
        string commandLine = input.Trim();
        SetInput(string.Empty);
        historyIndex = -1;
        suggestionIndex = -1;
        focusInput = true;
        if (commandLine.Length == 0)
            return;

        if (history.Count == 0 || history[history.Count - 1] != commandLine)
            history.Add(commandLine);

        ExecuteCommand(commandLine);
    }

    private void AcceptSuggestion()
    {
        if (suggestions.Count == 0 || input.Contains(" "))
            return;

        CommandEntry entry = suggestions[Mathf.Clamp(suggestionIndex, 0, suggestions.Count - 1)];
        SetInput(entry.Name + " ");
    }

    private void CycleSuggestion(int direction)
    {
        if (suggestions.Count == 0)
            return;

        suggestionIndex = (suggestionIndex + direction + suggestions.Count) % suggestions.Count;
    }

    private void NavigateHistory(int direction)
    {
        if (history.Count == 0)
            return;

        if (historyIndex == -1)
        {
            if (direction > 0)
                return;

            historyIndex = history.Count - 1;
        }
        else
        {
            historyIndex += direction;
            if (historyIndex >= history.Count)
            {
                historyIndex = -1;
                SetInput(string.Empty);
                return;
            }

            if (historyIndex < 0)
                historyIndex = 0;
        }

        SetInput(history[historyIndex]);
    }

    private void SetInput(string value)
    {
        input = value;
        if (GUIUtility.keyboardControl == 0)
            return;

        TextEditor editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
        if (editor == null)
            return;

        editor.text = input;
        editor.MoveTextEnd();
    }

    private static Font GetMonoFont()
    {
        if (monoFontAttempted)
            return monoFont;

        monoFontAttempted = true;
        try
        {
            monoFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New", "Menlo", "DejaVu Sans Mono" }, 12);
        }
        catch (Exception)
        {
            monoFont = null;
        }

        return monoFont;
    }

    private void EnsureStyles()
    {
        if (logStyle != null)
            return;

        Font mono = GetMonoFont();

        GUIStyle scrollbar = GUI.skin.verticalScrollbar;
        scrollbar.fixedWidth = 12f;
        scrollbar.padding = new RectOffset(2, 2, 2, 2);
        GUIStyle scrollbarThumb = GUI.skin.verticalScrollbarThumb;
        scrollbarThumb.fixedWidth = 0f;
        scrollbarThumb.fixedHeight = 0f;

        logStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = true,
            richText = true,
            fontSize = 12,
            padding = new RectOffset(8, 2, 2, 2)
        };
        if (mono != null)
            logStyle.font = mono;

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 12,
            normal = { textColor = Color.white },
            hover = { textColor = Color.white }
        };

        inputStyle = new GUIStyle(GUI.skin.textField)
        {
            fontSize = 13
        };
        if (mono != null)
            inputStyle.font = mono;

        suggestionStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            richText = true,
            fontSize = 12,
            clipping = TextClipping.Clip,
            wordWrap = false
        };
        if (mono != null)
            suggestionStyle.font = mono;

        footerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f, 1f) },
            hover = { textColor = new Color(0.5f, 0.5f, 0.5f, 1f) }
        };

        placeholderStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Italic,
            fontSize = 12,
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f, 1f) },
            hover = { textColor = new Color(0.5f, 0.5f, 0.5f, 1f) }
        };
    }
}
