using System.Runtime.InteropServices;

namespace MotionSFX.Core;

internal static class KeyboardInput
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInputData Mouse;
        [FieldOffset(0)] public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = 0x11, ["CONTROL"] = 0x11, ["ALT"] = 0x12, ["SHIFT"] = 0x10,
        ["WIN"] = 0x5B, ["WINDOWS"] = 0x5B, ["SPACE"] = 0x20, ["ENTER"] = 0x0D,
        ["TAB"] = 0x09, ["ESC"] = 0x1B, ["ESCAPE"] = 0x1B, ["BACKSPACE"] = 0x08,
        ["DELETE"] = 0x2E, ["INSERT"] = 0x2D, ["HOME"] = 0x24, ["END"] = 0x23,
        ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22, ["UP"] = 0x26, ["DOWN"] = 0x28,
        ["LEFT"] = 0x25, ["RIGHT"] = 0x27, ["CAPSLOCK"] = 0x14, ["NUMLOCK"] = 0x90,
        ["SCROLLLOCK"] = 0x91, ["PRINTSCREEN"] = 0x2C, ["APPS"] = 0x5D,
        ["OEM_1"] = 0xBA, ["OEM_PLUS"] = 0xBB, ["OEM_COMMA"] = 0xBC,
        ["OEM_MINUS"] = 0xBD, ["OEM_PERIOD"] = 0xBE, ["OEM_2"] = 0xBF,
        ["OEM_3"] = 0xC0, ["OEM_4"] = 0xDB, ["OEM_5"] = 0xDC,
        ["OEM_6"] = 0xDD, ["OEM_7"] = 0xDE, ["OEM_8"] = 0xDF,
        ["OEM_102"] = 0xE2, ["ADD"] = 0x6B, ["SUBTRACT"] = 0x6D,
        ["MULTIPLY"] = 0x6A, ["DIVIDE"] = 0x6F, ["DECIMAL"] = 0x6E,
        ["NUM0"] = 0x60, ["NUM1"] = 0x61, ["NUM2"] = 0x62,
        ["NUM3"] = 0x63, ["NUM4"] = 0x64, ["NUM5"] = 0x65,
        ["NUM6"] = 0x66, ["NUM7"] = 0x67, ["NUM8"] = 0x68, ["NUM9"] = 0x69
    };

    public static string Validate(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return string.Empty;
        string[] parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4)
            throw new InvalidOperationException("Use a shortcut such as CTRL+ALT+1 (one to four keys).");

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in parts)
        {
            if (!seen.Add(part)) throw new InvalidOperationException("The shortcut contains a duplicate key.");
            _ = ToVirtualKey(part);
        }
        return string.Join("+", parts.Select(p => p.ToUpperInvariant()));
    }

    public static void Send(string shortcut)
    {
        string validated = Validate(shortcut);
        if (validated.Length == 0) throw new InvalidOperationException("Enter a keyboard shortcut first.");
        ushort[] keys = validated.Split('+').Select(ToVirtualKey).ToArray();
        NativeInput[] events = new NativeInput[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
        {
            events[i].Type = InputKeyboard;
            events[i].Data.Keyboard.VirtualKey = keys[i];
        }
        for (int i = 0; i < keys.Length; i++)
        {
            events[keys.Length + i].Type = InputKeyboard;
            events[keys.Length + i].Data.Keyboard.VirtualKey = keys[keys.Length - i - 1];
            events[keys.Length + i].Data.Keyboard.Flags = KeyUp;
        }

        uint accepted = SendInput((uint)events.Length, events, Marshal.SizeOf<NativeInput>());
        if (accepted == (uint)events.Length) return;

        NativeInput[] releases = new NativeInput[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            releases[i].Type = InputKeyboard;
            releases[i].Data.Keyboard.VirtualKey = keys[i];
            releases[i].Data.Keyboard.Flags = KeyUp;
        }
        _ = SendInput((uint)releases.Length, releases, Marshal.SizeOf<NativeInput>());
        throw new InvalidOperationException("Windows did not accept the complete keyboard shortcut. Check whether MotionSFX and the target app run at different privilege levels.");
    }

    private static ushort ToVirtualKey(string keyName)
    {
        string key = keyName.Trim().ToUpperInvariant();
        if (NamedKeys.TryGetValue(key, out ushort named)) return named;
        if (key.Length == 1 && ((key[0] >= '0' && key[0] <= '9') || (key[0] >= 'A' && key[0] <= 'Z')))
            return key[0];
        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key[1..], out int function) && function is >= 1 and <= 24)
            return (ushort)(0x70 + function - 1);
        throw new InvalidOperationException("Unsupported key name: " + keyName);
    }
}
