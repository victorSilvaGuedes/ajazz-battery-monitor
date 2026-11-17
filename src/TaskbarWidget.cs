using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AjazzBatteryMonitor
{
    /// <summary>
    /// Layered child window embedded directly into the Windows 11 Taskbar (Shell_TrayWnd),
    /// positioned adjacent to the system tray notification area, displaying a minimalist
    /// Segoe Fluent Icons mouse glyph, percentage, and charging bolt.
    /// </summary>
    public sealed class TaskbarWidget : NativeWindow, IDisposable
    {
        #region Win32 P/Invoke

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string title);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            IntPtr hdcSrc,
            ref POINT pptSrc,
            uint crKey,
            [In] ref BLENDFUNCTION pblend,
            uint dwFlags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        private const int WS_CHILD = 0x40000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x, y;
            public POINT(int x, int y) { this.x = x; this.y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx, cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        #endregion

        private readonly ContextMenuStrip _contextMenu;
        private readonly Action _onRefreshRequested;
        private readonly System.Windows.Forms.Timer _watchdogTimer;

        private BatteryStatus _lastStatus;
        private int _lastWidth;
        private int _lastHeight;
        private int _lastX;
        private bool _disposed;

        public TaskbarWidget(ContextMenuStrip contextMenu, Action onRefreshRequested)
        {
            _contextMenu = contextMenu;
            _onRefreshRequested = onRefreshRequested;

            _watchdogTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _watchdogTimer.Tick += (s, e) => EnsureWidget();
            _watchdogTimer.Start();

            EnsureWidget();
        }

        public void UpdateStatus(BatteryStatus status)
        {
            _lastStatus = status;
            Redraw();
        }

        private void EnsureWidget()
        {
            if (_disposed) return;

            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;

            if (Handle == IntPtr.Zero || !IsWindow(Handle))
            {
                var cp = new CreateParams
                {
                    Style = WS_CHILD | WS_CLIPSIBLINGS,
                    ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                    Parent = tray,
                    X = 0,
                    Y = 0,
                    Width = 1,
                    Height = 1
                };
                CreateHandle(cp);
                Redraw();
            }
            else
            {
                CheckPosition();
            }
        }

        private bool IsLightTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("SystemUsesLightTheme");
                        if (val is int)
                        {
                            return ((int)val) != 0;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private void Redraw()
        {
            if (Handle == IntPtr.Zero || !IsWindow(Handle)) return;

            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;

            RECT tr;
            if (!GetWindowRect(tray, out tr)) return;
            int height = Math.Max(32, tr.Bottom - tr.Top);

            uint dpi = 96;
            try { dpi = Math.Max(96, GetDpiForWindow(tray)); } catch { }
            float scale = dpi / 96.0f;

            bool light = IsLightTheme();
            Color fgNormal = light ? Color.FromArgb(20, 20, 20) : Color.White;
            Color fgDimmed = light ? Color.FromArgb(120, 120, 120) : Color.FromArgb(140, 140, 140);
            Color fgCharging = Color.FromArgb(250, 204, 21); // Amarelo
            Color fgFull = light ? Color.FromArgb(0, 120, 215) : Color.FromArgb(56, 189, 248); // Azul
            Color fgLow = light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 107, 107); // Vermelho
            Color boltColor = light ? Color.FromArgb(15, 123, 15) : Color.FromArgb(108, 203, 95); // Verde

            Color primaryColor = fgDimmed;
            string label = "—";
            bool showBolt = false;
            int opacity = 255;

            BatteryStatus status = _lastStatus;
            if (status != null && status.Connected)
            {
                bool isWired = string.Equals(status.ConnectionMode, "USB", StringComparison.OrdinalIgnoreCase);
                bool isCharging = status.IsCharging || (isWired && status.Percent < 100);

                if (status.Percent == 100 && isWired)
                {
                    label = "100%";
                    primaryColor = fgFull;
                }
                else if (isCharging)
                {
                    label = "CB";
                    primaryColor = fgCharging;
                    showBolt = true;
                }
                else if (status.Percent <= 20)
                {
                    label = status.Percent + "%";
                    primaryColor = fgLow;
                }
                else
                {
                    label = status.Percent + "%";
                    primaryColor = fgNormal;
                }
            }
            else
            {
                label = "—";
                primaryColor = fgDimmed;
                opacity = 140;
            }

            float iconPx = 15.0f * scale;
            float textPx = 13.0f * scale;
            float boltPx = 12.0f * scale;

            using (var iconFont = new Font("Segoe Fluent Icons", iconPx, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var textFont = new Font("Segoe UI Semibold", textPx, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var boltFont = new Font("Segoe Fluent Icons", boltPx, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var measureBmp = new Bitmap(1, 1))
            using (var gMeasure = Graphics.FromImage(measureBmp))
            {
                string mouseGlyph = "\uE962";
                string boltGlyph = "\uE945";

                SizeF sizeMouse = gMeasure.MeasureString(mouseGlyph, iconFont);
                SizeF sizeLabel = gMeasure.MeasureString(label, textFont);
                SizeF sizeBolt = showBolt ? gMeasure.MeasureString(boltGlyph, boltFont) : SizeF.Empty;

                int pad = (int)(6 * scale);
                int gap = (int)(4 * scale);
                int contentWidth = (int)(sizeMouse.Width + gap + sizeLabel.Width + (showBolt ? (gap + sizeBolt.Width) : 0));
                int totalWidth = contentWidth + (pad * 2);

                _lastWidth = Math.Max(totalWidth, (int)(40 * scale));
                _lastHeight = height;

                int x = CalculateTargetX(_lastWidth);
                _lastX = x;

                using (var bmp = new Bitmap(_lastWidth, _lastHeight, PixelFormat.Format32bppPArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.Clear(Color.Transparent);

                    float curX = pad;
                    float centerY = _lastHeight / 2.0f;

                    // Mouse Icon
                    Color mouseCol = Color.FromArgb((opacity * primaryColor.A) / 255, primaryColor.R, primaryColor.G, primaryColor.B);
                    using (var b = new SolidBrush(mouseCol))
                    {
                        float y = centerY - (sizeMouse.Height / 2.0f);
                        g.DrawString(mouseGlyph, iconFont, b, curX, y);
                    }
                    curX += sizeMouse.Width + gap;

                    // Label
                    using (var b = new SolidBrush(mouseCol))
                    {
                        float y = centerY - (sizeLabel.Height / 2.0f);
                        g.DrawString(label, textFont, b, curX, y);
                    }
                    curX += sizeLabel.Width;

                    // Bolt
                    if (showBolt)
                    {
                        curX += gap;
                        Color bCol = Color.FromArgb(opacity, boltColor.R, boltColor.G, boltColor.B);
                        using (var b = new SolidBrush(bCol))
                        {
                            float y = centerY - (sizeBolt.Height / 2.0f) + (1 * scale);
                            g.DrawString(boltGlyph, boltFont, b, curX, y);
                        }
                    }

                    // Push via UpdateLayeredWindow
                    IntPtr screenDc = GetDC(IntPtr.Zero);
                    IntPtr memDc = CreateCompatibleDC(screenDc);
                    IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                    IntPtr oldBmp = SelectObject(memDc, hBmp);

                    var blend = new BLENDFUNCTION
                    {
                        BlendOp = AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255,
                        AlphaFormat = AC_SRC_ALPHA
                    };

                    var ptDst = new POINT(x, 0);
                    var size = new SIZE(_lastWidth, _lastHeight);
                    var ptSrc = new POINT(0, 0);

                    UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, ULW_ALPHA);
                    SetWindowPos(Handle, HWND_TOP, x, 0, _lastWidth, _lastHeight, SWP_NOACTIVATE | SWP_SHOWWINDOW);

                    SelectObject(memDc, oldBmp);
                    DeleteObject(hBmp);
                    DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        private int CalculateTargetX(int width)
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return 0;

            RECT tr;
            GetWindowRect(tray, out tr);
            int right = tr.Right;

            IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero)
            {
                RECT nr;
                if (GetWindowRect(notify, out nr) && nr.Right > nr.Left)
                {
                    right = nr.Left;
                }
            }

            uint dpi = 96;
            try { dpi = Math.Max(96, GetDpiForWindow(tray)); } catch { }
            int margin = (int)(4 * dpi / 96);

            return Math.Max(0, right - tr.Left - width - margin);
        }

        private void CheckPosition()
        {
            if (Handle == IntPtr.Zero || !IsWindow(Handle)) return;
            if (_lastWidth <= 0) return;

            int targetX = CalculateTargetX(_lastWidth);
            if (targetX != _lastX)
            {
                Redraw();
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_RBUTTONUP || m.Msg == WM_CONTEXTMENU)
            {
                if (_contextMenu != null)
                {
                    Point pt = Cursor.Position;
                    _contextMenu.Show(pt);
                }
                return;
            }
            else if (m.Msg == WM_LBUTTONDBLCLK)
            {
                if (_onRefreshRequested != null)
                {
                    _onRefreshRequested();
                }
                return;
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _watchdogTimer.Stop();
            _watchdogTimer.Dispose();

            if (Handle != IntPtr.Zero && IsWindow(Handle))
            {
                DestroyWindow(Handle);
            }
        }
    }
}
