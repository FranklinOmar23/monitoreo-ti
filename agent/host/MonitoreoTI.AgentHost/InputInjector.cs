using System.Runtime.InteropServices;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Control remoto (sección 10.2): mouse y teclado con SendInput desde el asistente de sesión. El teclado se envía por
/// tecla física (KeyboardEvent.code → código de exploración): el carácter lo decide la distribución del equipo remoto.
/// </summary>
public static class InputInjector
{
    /// <summary>Botón del navegador (0 izquierdo, 1 central, 2 derecho) → banderas de presionar y soltar.</summary>
    private static readonly (uint Down, uint Up)[] Buttons = [(0x0002, 0x0004), (0x0020, 0x0040), (0x0008, 0x0010)];

    /// <summary>Mueve el cursor a (x, y) del escritorio virtual y, si se indica, presiona o suelta un botón o gira la rueda.</summary>
    public static void Mouse(int x, int y, string action, int button, int delta)
    {
        var vx = Native.GetSystemMetrics(76);
        var vy = Native.GetSystemMetrics(77);
        var vw = Math.Max(2, Native.GetSystemMetrics(78));
        var vh = Math.Max(2, Native.GetSystemMetrics(79));
        var flags = 0x0001u | 0x8000u | 0x4000u; // MOVE | ABSOLUTE | VIRTUALDESK
        var data = 0u;
        var b = Buttons[Math.Clamp(button, 0, 2)];
        switch (action)
        {
            case "abajo": flags |= b.Down; break;
            case "arriba": flags |= b.Up; break;
            case "rueda": flags |= 0x0800; data = unchecked((uint)Math.Clamp(delta, -1200, 1200)); break;
            case "rueda_h": flags |= 0x1000; data = unchecked((uint)Math.Clamp(delta, -1200, 1200)); break;
        }

        var input = new Native.Input
        {
            Type = 0,
            Mouse = new Native.MouseInput
            {
                Dx = (int)((long)(x - vx) * 65535 / (vw - 1)),
                Dy = (int)((long)(y - vy) * 65535 / (vh - 1)),
                MouseData = data,
                Flags = flags,
            },
        };
        Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>());
    }

    /// <summary>Presiona o suelta una tecla física. false si el código no se conoce.</summary>
    public static bool Key(string code, bool down)
    {
        if (!ScanCodes.TryGetValue(code, out var key))
            return false;
        var flags = 0x0008u | (key.Extended ? 0x0001u : 0) | (down ? 0 : 0x0002u); // SCANCODE | EXTENDED | KEYUP
        var input = new Native.Input { Type = 1, Keyboard = new Native.KeyboardInput { Scan = key.Scan, Flags = flags } };
        Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>());
        return true;
    }

    /// <summary>Escribe un texto carácter por carácter (Unicode, sin depender de la distribución del teclado).</summary>
    public static void TypeText(string text)
    {
        foreach (var ch in text.Length > 10_000 ? text[..10_000] : text)
        {
            if (ch == '\r')
                continue;
            if (ch == '\n')
            {
                Key("Enter", true);
                Key("Enter", false);
                continue;
            }
            var down = new Native.Input { Type = 1, Keyboard = new Native.KeyboardInput { Scan = ch, Flags = 0x0004 } }; // UNICODE
            var up = new Native.Input { Type = 1, Keyboard = new Native.KeyboardInput { Scan = ch, Flags = 0x0004 | 0x0002 } };
            Native.SendInput(2, [down, up], Marshal.SizeOf<Native.Input>());
        }
    }

    /// <summary>
    /// Bloquea (o devuelve) el teclado y el mouse físicos del usuario; lo que inyecta el técnico sigue funcionando. Windows
    /// lo quita solo con Ctrl+Alt+Supr o si el asistente termina.
    /// </summary>
    public static bool Block(bool block) => Native.BlockInput(block);

    /// <summary>Cambia cada vez que algo se copia en el equipo.</summary>
    public static uint ClipboardSequence() => Native.GetClipboardSequenceNumber();

    /// <summary>Teclas físicas (KeyboardEvent.code) → código de exploración del conjunto 1 y si es extendida.</summary>
    public static readonly IReadOnlyDictionary<string, (ushort Scan, bool Extended)> ScanCodes = Build();

    private static Dictionary<string, (ushort, bool)> Build()
    {
        var map = new Dictionary<string, (ushort, bool)>(StringComparer.Ordinal)
        {
            ["Escape"] = (0x01, false), ["Minus"] = (0x0C, false), ["Equal"] = (0x0D, false), ["Backspace"] = (0x0E, false),
            ["Tab"] = (0x0F, false), ["BracketLeft"] = (0x1A, false), ["BracketRight"] = (0x1B, false), ["Enter"] = (0x1C, false),
            ["ControlLeft"] = (0x1D, false), ["Semicolon"] = (0x27, false), ["Quote"] = (0x28, false), ["Backquote"] = (0x29, false),
            ["ShiftLeft"] = (0x2A, false), ["Backslash"] = (0x2B, false), ["Comma"] = (0x33, false), ["Period"] = (0x34, false),
            ["Slash"] = (0x35, false), ["ShiftRight"] = (0x36, false), ["NumpadMultiply"] = (0x37, false), ["AltLeft"] = (0x38, false),
            ["Space"] = (0x39, false), ["CapsLock"] = (0x3A, false), ["NumLock"] = (0x45, true), ["ScrollLock"] = (0x46, false),
            ["Numpad7"] = (0x47, false), ["Numpad8"] = (0x48, false), ["Numpad9"] = (0x49, false), ["NumpadSubtract"] = (0x4A, false),
            ["Numpad4"] = (0x4B, false), ["Numpad5"] = (0x4C, false), ["Numpad6"] = (0x4D, false), ["NumpadAdd"] = (0x4E, false),
            ["Numpad1"] = (0x4F, false), ["Numpad2"] = (0x50, false), ["Numpad3"] = (0x51, false), ["Numpad0"] = (0x52, false),
            ["NumpadDecimal"] = (0x53, false), ["IntlBackslash"] = (0x56, false), ["F11"] = (0x57, false), ["F12"] = (0x58, false),
            ["IntlRo"] = (0x73, false), ["NumpadEnter"] = (0x1C, true), ["ControlRight"] = (0x1D, true), ["NumpadDivide"] = (0x35, true),
            ["PrintScreen"] = (0x37, true), ["AltRight"] = (0x38, true), ["Home"] = (0x47, true), ["ArrowUp"] = (0x48, true),
            ["PageUp"] = (0x49, true), ["ArrowLeft"] = (0x4B, true), ["ArrowRight"] = (0x4D, true), ["End"] = (0x4F, true),
            ["ArrowDown"] = (0x50, true), ["PageDown"] = (0x51, true), ["Insert"] = (0x52, true), ["Delete"] = (0x53, true),
            ["MetaLeft"] = (0x5B, true), ["MetaRight"] = (0x5C, true), ["ContextMenu"] = (0x5D, true),
        };
        // Fila de números (Digit1 = 0x02 ... Digit0 = 0x0B) y F1 a F10 (0x3B a 0x44).
        for (var i = 1; i <= 9; i++)
            map[$"Digit{i}"] = ((ushort)(0x01 + i), false);
        map["Digit0"] = (0x0B, false);
        for (var i = 1; i <= 10; i++)
            map[$"F{i}"] = ((ushort)(0x3A + i), false);
        // Letras por posición física (QWERTY).
        foreach (var (row, start) in new[] { ("QWERTYUIOP", 0x10), ("ASDFGHJKL", 0x1E), ("ZXCVBNM", 0x2C) })
        {
            for (var i = 0; i < row.Length; i++)
                map[$"Key{row[i]}"] = ((ushort)(start + i), false);
        }
        return map;
    }

    // ------------------------------------------------------------------ portapapeles (texto)

    public static void SetClipboard(string text)
    {
        if (!OpenClipboardRetry())
            return;
        try
        {
            Native.EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var memory = Native.GlobalAlloc(0x0002, (UIntPtr)bytes); // GMEM_MOVEABLE
            var target = Native.GlobalLock(memory);
            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * 2, 0);
            Native.GlobalUnlock(memory);
            Native.SetClipboardData(13, memory); // CF_UNICODETEXT: el sistema se queda con la memoria
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    public static string? GetClipboard()
    {
        if (!OpenClipboardRetry())
            return null;
        try
        {
            var handle = Native.GetClipboardData(13);
            if (handle == IntPtr.Zero)
                return null;
            var pointer = Native.GlobalLock(handle);
            try
            {
                return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Native.GlobalUnlock(handle);
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    private static bool OpenClipboardRetry()
    {
        for (var i = 0; i < 10; i++)
        {
            if (Native.OpenClipboard(IntPtr.Zero))
                return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MouseInput
        {
            public int Dx, Dy;
            public uint MouseData, Flags, Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KeyboardInput
        {
            public ushort Vk, Scan;
            public uint Flags, Time;
            public IntPtr ExtraInfo;
        }

        /// <summary>INPUT de x64: tipo (4) + relleno (4) + unión de 32 bytes.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        public struct Input
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public MouseInput Mouse;
            [FieldOffset(8)] public KeyboardInput Keyboard;
        }

        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool BlockInput(bool block);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] public static extern bool CloseClipboard();
        [DllImport("user32.dll")] public static extern bool EmptyClipboard();
        [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint format);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr memory);
    }
}
