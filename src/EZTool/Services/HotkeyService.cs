using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace EZTool.Services;

/// <summary>Eine Tastenbelegung: Auslöser (Taste + Zusatztasten) → Aktion.</summary>
public sealed class KeyBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public int Vk { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    /// <summary>next, prev, playpause, stop, volup, voldown, mute, key, run, gaming, overlay, open</summary>
    public string Action { get; set; } = "next";

    /// <summary>Für "key": die Taste, die stattdessen gesendet wird.</summary>
    public int TargetVk { get; set; }
    public bool TargetCtrl { get; set; }
    public bool TargetAlt { get; set; }
    public bool TargetShift { get; set; }

    /// <summary>Für "run": Programm oder Datei.</summary>
    public string Path { get; set; } = "";
}

/// <summary>
/// Globale Tastenbelegung über einen Low-Level-Tastatur-Hook: Die Auslöse-Taste wird abgefangen
/// (ihre normale Funktion entfällt) und stattdessen die gewählte Aktion ausgeführt – in jedem Programm.
/// </summary>
public sealed class HotkeyService
{
    public static HotkeyService Instance { get; } = new();

    public static readonly (string Id, string Label, string Icon)[] Actions =
    [
        ("next", "Nächster Titel", ""),
        ("prev", "Vorheriger Titel", ""),
        ("playpause", "Play / Pause", ""),
        ("stop", "Stopp", ""),
        ("volup", "Lauter", ""),
        ("voldown", "Leiser", ""),
        ("mute", "Stumm", ""),
        ("key", "Andere Taste senden", ""),
        ("run", "Programm starten", ""),
        ("gaming", "Gaming-Modus an/aus", ""),
        ("overlay", "Overlay an/aus", ""),
        ("open", "EZTool öffnen", ""),
    ];

    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const int LLKHF_INJECTED = 0x10;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr extra; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private readonly HookProc _proc;
    private IntPtr _hook;
    private List<KeyBinding> _bindings = [];
    private readonly HashSet<int> _swallowedDown = [];

    /// <summary>Aufnahme-Modus: Die nächste gedrückte Taste wird gemeldet statt ausgeführt.</summary>
    private Action<int, bool, bool, bool, bool>? _record;

    public HotkeyService() => _proc = Hook;

    /// <summary>Belegungen übernehmen; der Hook läuft nur, wenn er gebraucht wird.</summary>
    public void Apply(IEnumerable<KeyBinding> bindings)
    {
        _bindings = bindings.Where(b => b.Enabled && b.Vk != 0).ToList();
        UpdateHook();
    }

    private void UpdateHook()
    {
        bool needed = _bindings.Count > 0 || _record != null;
        if (needed && _hook == IntPtr.Zero)
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        else if (!needed && _hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>Nächste Taste aufnehmen (für die Einstellungsseite). Esc bricht ab (vk = 0).</summary>
    public void Record(Action<int, bool, bool, bool, bool> callback)
    {
        _record = callback;
        UpdateHook();
    }

    public void CancelRecord()
    {
        _record = null;
        UpdateHook();
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);
        var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int msg = (int)wParam;
        bool keyDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN, keyUp = msg is WM_KEYUP or WM_SYSKEYUP;

        // Selbst gesendete Tasten nie erneut auswerten
        if ((k.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_hook, code, wParam, lParam);

        if (keyUp && _swallowedDown.Remove(k.vkCode)) return 1;
        if (!keyDown || IsModifier(k.vkCode)) return CallNextHookEx(_hook, code, wParam, lParam);

        bool ctrl = Down(0x11), alt = Down(0x12), shift = Down(0x10), win = Down(0x5B) || Down(0x5C);

        if (_record is { } rec)
        {
            _record = null;
            _swallowedDown.Add(k.vkCode);
            int vk = k.vkCode == 0x1B && !ctrl && !alt && !shift ? 0 : k.vkCode; // Esc = Abbrechen
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                rec(vk, ctrl, alt, shift, win);
                UpdateHook();
            });
            return 1;
        }

        var b = _bindings.FirstOrDefault(x => x.Vk == k.vkCode && x.Ctrl == ctrl && x.Alt == alt && x.Shift == shift && x.Win == win);
        if (b == null) return CallNextHookEx(_hook, code, wParam, lParam);

        _swallowedDown.Add(k.vkCode);
        // Aktion außerhalb des Hooks ausführen (Hooks müssen sofort zurückkehren)
        Application.Current?.Dispatcher.BeginInvoke(() => Execute(b));
        return 1;
    }

    public static void Execute(KeyBinding b)
    {
        try
        {
            switch (b.Action)
            {
                case "next": Tap(0xB0); break;
                case "prev": Tap(0xB1); break;
                case "stop": Tap(0xB2); break;
                case "playpause": Tap(0xB3); break;
                case "mute": Tap(0xAD); break;
                case "voldown": Tap(0xAE); break;
                case "volup": Tap(0xAF); break;
                case "key": SendCombo(b.TargetVk, b.TargetCtrl, b.TargetAlt, b.TargetShift); break;
                case "run":
                    // Über den Explorer starten → das Programm läuft mit normalen Rechten, nicht als Administrator
                    if (!string.IsNullOrWhiteSpace(b.Path))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{b.Path}\"") { UseShellExecute = true });
                    break;
                case "gaming":
                    _ = GamingService.Instance.IsActive ? GamingService.Instance.DeactivateAsync() : GamingService.Instance.ActivateAsync();
                    break;
                case "overlay":
                    SettingsService.Current.OverlayEnabled = !SettingsService.Current.OverlayEnabled;
                    SettingsService.Save();
                    break;
                case "open":
                    ((App)Application.Current).ShowMain(null);
                    break;
            }
        }
        catch { /* eine Aktion darf nie die Tastatur blockieren */ }
    }

    // ───────────── Tasten senden ─────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private const uint KEYEVENTF_KEYUP = 0x2;

    private static INPUT KeyInput(int vk, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } }
    };

    private static void Tap(int vk) => SendInput(2, [KeyInput(vk, false), KeyInput(vk, true)], Marshal.SizeOf<INPUT>());

    private static void SendCombo(int vk, bool ctrl, bool alt, bool shift)
    {
        if (vk == 0) return;
        var list = new List<INPUT>();
        if (ctrl) list.Add(KeyInput(0x11, false));
        if (alt) list.Add(KeyInput(0x12, false));
        if (shift) list.Add(KeyInput(0x10, false));
        list.Add(KeyInput(vk, false));
        list.Add(KeyInput(vk, true));
        if (shift) list.Add(KeyInput(0x10, true));
        if (alt) list.Add(KeyInput(0x12, true));
        if (ctrl) list.Add(KeyInput(0x11, true));
        SendInput((uint)list.Count, [.. list], Marshal.SizeOf<INPUT>());
    }

    // ───────────── Tastennamen (deutsch) ─────────────

    public static string Describe(int vk, bool ctrl, bool alt, bool shift, bool win)
    {
        if (vk == 0) return "—";
        var parts = new List<string>();
        if (ctrl) parts.Add("Strg");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Umschalt");
        if (win) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join(" + ", parts);
    }

    public static string KeyName(int vk)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key switch
        {
            Key.PageUp => "Bild ↑",
            Key.PageDown => "Bild ↓",
            Key.Home => "Pos1",
            Key.End => "Ende",
            Key.Insert => "Einfg",
            Key.Delete => "Entf",
            Key.Up => "↑",
            Key.Down => "↓",
            Key.Left => "←",
            Key.Right => "→",
            Key.Space => "Leertaste",
            Key.Return => "Enter",
            Key.Back => "Rücktaste",
            Key.Escape => "Esc",
            Key.Tab => "Tab",
            Key.CapsLock => "Feststell",
            Key.Scroll => "Rollen",
            Key.Pause => "Pause",
            Key.PrintScreen => "Druck",
            Key.NumLock => "Num",
            Key.Apps => "Menü",
            Key.MediaNextTrack => "Medien ⏭",
            Key.MediaPreviousTrack => "Medien ⏮",
            Key.MediaPlayPause => "Medien ⏯",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
            Key.Multiply => "Num *",
            Key.Add => "Num +",
            Key.Subtract => "Num −",
            Key.Divide => "Num /",
            Key.Decimal => "Num ,",
            _ => key.ToString(),
        };
    }
}
