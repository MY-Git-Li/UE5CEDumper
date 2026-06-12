using System.Runtime.InteropServices;
using System.Threading;
using UE5DumpUI.Core;

namespace UE5DumpUI.Services;

/// <summary>
/// Win32 RegisterHotKey-based global hotkey, on a dedicated message-loop thread.
/// AOT-safe: classic blittable DllImports (no LibraryImport / unsafe).
///
/// RegisterHotKey with a NULL hwnd posts WM_HOTKEY to the registering thread's
/// queue, so registration AND the GetMessage loop both run on one owned thread.
/// A combo already claimed elsewhere makes RegisterHotKey fail — that's exactly
/// the "is this combo free?" probe the auto-detect ladder relies on.
/// </summary>
public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    public IGlobalHotkeyRegistration? RegisterCursorHotkey(Action onPressed)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var reg = new Registration(onPressed);
            if (reg.Label != null) return reg;
            reg.Dispose();   // failed to claim a combo — tear the thread down
            return null;
        }
        catch
        {
            // Never let hotkey setup take down the app — degrade to "no hotkey".
            return null;
        }
    }

    // Candidate ladder (docs/teleport-spec.md cursor hotkey): Ctrl+F8..F5 first
    // (ARPGs rarely bind Ctrl+F-keys), then Alt+F8..F5 as the fallback rung.
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;
    private const int VK_F5 = 0x74, VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77;

    private static readonly (uint Mod, int Vk, string Label)[] Candidates =
    {
        (MOD_CONTROL, VK_F8, "Ctrl+F8"),
        (MOD_CONTROL, VK_F7, "Ctrl+F7"),
        (MOD_CONTROL, VK_F6, "Ctrl+F6"),
        (MOD_CONTROL, VK_F5, "Ctrl+F5"),
        (MOD_ALT,     VK_F8, "Alt+F8"),
        (MOD_ALT,     VK_F7, "Alt+F7"),
        (MOD_ALT,     VK_F6, "Alt+F6"),
        (MOD_ALT,     VK_F5, "Alt+F5"),
    };

    private sealed class Registration : IGlobalHotkeyRegistration
    {
        private const int HotkeyId = 0xB1F0;   // arbitrary, unique within our thread
        private readonly Thread _thread;
        // IMPORTANT: this event is NOT disposed in the ctor. ManualResetEventSlim
        // forbids Dispose() concurrent with Set() (docs), and the worker calls
        // Set() right as the ctor's Wait() returns — a `using` here raced Set()
        // against Dispose() and crashed the process natively (the "CTD right
        // after 'cursor hotkey bound'" symptom). It is disposed in Dispose()
        // only AFTER the worker thread has joined, so Set() is fully finished.
        private readonly ManualResetEventSlim _ready = new(false);
        private uint _threadId;
        private volatile bool _disposed;

        public string? Label { get; private set; }
        string IGlobalHotkeyRegistration.Label => Label ?? "";

        public Registration(Action onPressed)
        {
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();

                int chosen = -1;
                for (int i = 0; i < Candidates.Length; i++)
                {
                    if (RegisterHotKey(IntPtr.Zero, HotkeyId,
                            Candidates[i].Mod | MOD_NOREPEAT, (uint)Candidates[i].Vk))
                    {
                        chosen = i;
                        Label = Candidates[i].Label;
                        break;
                    }
                }
                _ready.Set();
                if (chosen < 0) return;

                // Message loop: WM_HOTKEY → fire; WM_QUIT (from Dispose) → exit.
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_HOTKEY)
                    {
                        try { onPressed(); } catch { /* never let a callback kill the loop */ }
                    }
                }
                UnregisterHotKey(IntPtr.Zero, HotkeyId);
            })
            {
                IsBackground = true,
                Name = "UE5CEDumper-CursorHotkey",
            };
            _thread.Start();
            _ready.Wait(2000);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Break the GetMessage loop (no-op if the worker already exited after
            // a failed registration). Then join so the worker is fully done
            // before we dispose the event it touched.
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
            _ready.Dispose();
        }
    }

    // --- Win32 (blittable DllImports — AOT-safe, no AllowUnsafeBlocks) ---

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
}
