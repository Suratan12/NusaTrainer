using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NusaTrainer.Core.Util;

namespace NusaTrainer.App.Services;

public interface IHotkeyService : IDisposable
{
    /// <summary>Daftarkan hotkey global, mis. "Num1", "Ctrl+F2". Callback dipanggil di thread hotkey.</summary>
    IDisposable? Register(string combo, Action callback);
}

public sealed class NullHotkeyService : IHotkeyService
{
    public IDisposable? Register(string combo, Action callback) => null;
    public void Dispose() { }
}

public static class HotkeyParser
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    public static bool TryParse(string combo, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": modifiers |= MOD_CONTROL; continue;
                case "SHIFT": modifiers |= MOD_SHIFT; continue;
                case "ALT": modifiers |= MOD_ALT; continue;
            }
            if (vk != 0) return false;
            vk = part switch
            {
                _ when part.Length == 1 && part[0] is >= 'A' and <= 'Z' or >= '0' and <= '9' => part[0],
                _ when part.StartsWith('F') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24 => (uint)(0x70 + f - 1),
                _ when part.StartsWith("NUM") && int.TryParse(part[3..], out var n) && n is >= 0 and <= 9 => (uint)(0x60 + n),
                "NUMPLUS" or "NUMADD" => 0x6B,
                "NUMMINUS" or "NUMSUBTRACT" => 0x6D,
                "NUMMULTIPLY" => 0x6A,
                "NUMDIVIDE" => 0x6F,
                "INSERT" => 0x2D, "DELETE" => 0x2E, "HOME" => 0x24, "END" => 0x23,
                "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
                "UP" => 0x26, "DOWN" => 0x28, "LEFT" => 0x25, "RIGHT" => 0x27,
                _ => 0,
            };
            if (vk == 0) return false;
        }
        return vk != 0;
    }

    /// <summary>Teks yang ramah dibaca, mis. "Num1" → "Numpad 1".</summary>
    public static string Pretty(string combo) =>
        string.Join(" + ", combo.Split('+', StringSplitOptions.TrimEntries).Select(p =>
            p.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && p.Length > 3 ? "Numpad " + p[3..] : p));
}

/// <summary>
/// Hotkey global Windows via RegisterHotKey pada thread khusus dengan message loop sendiri,
/// sehingga bekerja walau jendela game sedang aktif (fokus).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsHotkeyService : IHotkeyService
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP_WAKE = 0x8001;
    private const uint WM_QUIT = 0x0012;

    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private int _nextId = 1;
    private bool _disposed;

    public WindowsHotkeyService()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "NusaTrainer Hotkeys" };
        _thread.Start();
        _ready.Wait();
    }

    public IDisposable? Register(string combo, Action callback)
    {
        if (_disposed || !HotkeyParser.TryParse(combo, out var mods, out var vk))
        {
            Log.Warn($"Hotkey tidak valid: {combo}");
            return null;
        }
        int id = Interlocked.Increment(ref _nextId);
        Post(() =>
        {
            if (RegisterHotKey(IntPtr.Zero, id, mods | HotkeyParser.MOD_NOREPEAT, vk))
                _callbacks[id] = callback;
            else
                Log.Warn($"Hotkey {combo} sudah dipakai aplikasi lain.");
        });
        return new Registration(this, id);
    }

    private void Unregister(int id) => Post(() =>
    {
        if (_callbacks.Remove(id)) UnregisterHotKey(IntPtr.Zero, id);
    });

    private void Post(Action a)
    {
        _work.Enqueue(a);
        PostThreadMessageW(_threadId, WM_APP_WAKE, IntPtr.Zero, IntPtr.Zero);
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        PeekMessageW(out _, IntPtr.Zero, 0, 0, 0); // buat message queue untuk thread ini
        _ready.Set();
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            while (_work.TryDequeue(out var a))
            {
                try { a(); } catch (Exception ex) { Log.Error("Hotkey", ex); }
            }
            if (msg.message == WM_HOTKEY && _callbacks.TryGetValue((int)msg.wParam, out var cb))
            {
                try { cb(); } catch (Exception ex) { Log.Error("Hotkey callback", ex); }
            }
        }
        foreach (var id in _callbacks.Keys) UnregisterHotKey(IntPtr.Zero, id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private sealed class Registration(WindowsHotkeyService owner, int id) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Unregister(id);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
