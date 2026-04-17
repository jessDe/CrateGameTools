using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CrateGameTools
{
    public partial class OverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        private const uint GW_HWNDPREV = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private DispatcherTimer _trackTimer;
        private IntPtr _vrcHwnd = IntPtr.Zero;

        public OverlayWindow()
        {
            InitializeComponent();
            this.SourceInitialized += OverlayWindow_SourceInitialized;
            
            _trackTimer = new DispatcherTimer();
            _trackTimer.Interval = TimeSpan.FromMilliseconds(500);
            _trackTimer.Tick += TrackTimer_Tick;
            _trackTimer.Start();
        }

        private void TrackTimer_Tick(object? sender, EventArgs e)
        {
            UpdatePosition();
        }

        private void UpdatePosition()
        {
            if (_vrcHwnd == IntPtr.Zero || !IsWindow(_vrcHwnd))
            {
                _vrcHwnd = FindVrcWindow();
            }

            if (_vrcHwnd != IntPtr.Zero)
            {
                if (GetWindowRect(_vrcHwnd, out RECT rect))
                {
                    double width = rect.Right - rect.Left;
                    // Only update if visible and has size
                    if (width > 0)
                    {
                        this.Left = rect.Left;
                        this.Top = rect.Top;
                        this.Width = width;
                        this.Visibility = Visibility.Visible;

                        // Ensure we are placed right above VRChat in the Z-order
                        // We use SetWindowPos to insert our window after the window that is immediately above VRChat
                        // OR we can just try to insert it after VRChat if we want it BEHIND VRChat, 
                        // but the user said "in front of vrchat and behind everything else if thats infront of vrchat"
                        // This means we want to be EXACTLY one step above VRChat.
                        
                        IntPtr hwnd = new WindowInteropHelper(this).Handle;
                        IntPtr prevHwnd = GetWindow(_vrcHwnd, GW_HWNDPREV);
                        
                        // If there's a window above VRChat, we want to be behind it but in front of VRChat.
                        // Actually, SetWindowPos(hwnd, HWND_VRC, ...) puts hwnd ABOVE HWND_VRC? 
                        // No, SetWindowPos(hWnd, hWndInsertAfter, ...) puts hWnd BEHIND hWndInsertAfter.
                        // To be in front of VRChat, we need to find what's in front of VRChat and put ourselves behind it.
                        // Or use the window handle of VRChat as hWndInsertAfter for our window, but that puts us BEHIND it.
                        
                        // Correction: SetWindowPos documentation says:
                        // "The window will be placed according to its position in the Z order. 
                        //  hWndInsertAfter: A handle to the window to precede the positioned window in the Z order."
                        // This means the window will be placed AFTER (behind) hWndInsertAfter.
                        
                        // So to be IN FRONT of VRChat, we need to find the window that is immediately IN FRONT of VRChat (prev in Z-order)
                        // and place ourselves AFTER that window.
                        
                        if (prevHwnd != IntPtr.Zero && prevHwnd != hwnd)
                        {
                            SetWindowPos(hwnd, prevHwnd, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                        }
                        else
                        {
                            // Fallback: if we can't find a window in front, maybe we are at the top or something.
                            // But usually VRChat is not the absolute top.
                            // We can also try placing it at the top (HWND_TOP) but that might be too much.
                            // Let's try to just be on top if no prev window.
                            SetWindowPos(hwnd, (IntPtr)0, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                        }
                    }
                    else
                    {
                        this.Visibility = Visibility.Collapsed;
                    }
                }
            }
            else
            {
                // VRChat not found, center on primary screen as fallback or hide?
                // The user wants it on VRChat window, so let's hide if not found.
                this.Visibility = Visibility.Collapsed;
            }
        }

        private IntPtr FindVrcWindow()
        {
            Process[] processes = Process.GetProcessesByName("VRChat");
            if (processes.Length > 0)
            {
                return processes[0].MainWindowHandle;
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        private void OverlayWindow_SourceInitialized(object? sender, EventArgs e)
        {
            // Make the window click-through and a tool window (hidden from Alt+Tab)
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
        }
    }
}
