using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LocalDisplayHost.Services;

/// <summary>
/// Captures the primary screen (or all screens) and returns JPEG bytes for streaming.
/// Includes the host PC mouse cursor on the captured image when visible.
/// </summary>
public class ScreenCapture
{
    private const int CursorShowing = 0x00000001;
    private const int DiNormal = 0x0003;

    private int _quality = 75; // JPEG quality 1-100

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo pci);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int CbSize;
        public int Flags;
        public IntPtr HCursor;
        public Point32 PtScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int FIcon;      // 1 = icon, 0 = cursor
        public int XHotspot;
        public int YHotspot;
        public IntPtr HbmMask;
        public IntPtr HbmColor;
    }

    public int Quality
    {
        get => _quality;
        set => _quality = Math.Clamp(value, 1, 100);
    }

    /// <summary>Capture the primary screen.</summary>
    public byte[]? CapturePrimary()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? default;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        return CaptureBounds(bounds);
    }

    /// <summary>Capture a specific monitor by index (0 = primary, 1 = second, etc.). Returns null if index invalid.</summary>
    public byte[]? CaptureMonitor(int index)
    {
        var bounds = GetMonitorBounds(index);
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        return CaptureBounds(bounds);
    }

    /// <summary>Get the bounds of a monitor by index (0 = primary, 1 = second, etc.). Empty if invalid.</summary>
    public static Rectangle GetMonitorBounds(int index)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (index < 0 || index >= screens.Length) return default;
        return screens[index].Bounds;
    }

    /// <summary>Number of displays (for UI).</summary>
    public static int MonitorCount => System.Windows.Forms.Screen.AllScreens.Length;

    /// <summary>Bounds for streaming: index -1 = all screens (union), 0 = primary, 1 = second, etc.</summary>
    public static Rectangle GetStreamedBounds(int monitorIndex)
    {
        if (monitorIndex < 0)
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            return screens.Length == 0 ? default : screens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
        }
        return GetMonitorBounds(monitorIndex);
    }

    /// <summary>Capture by selection: -1 = all screens, 0 = primary, 1 = second, etc.</summary>
    public byte[]? CaptureBySelection(int monitorIndex)
    {
        if (monitorIndex < 0) return CaptureAllScreens();
        return CaptureMonitor(monitorIndex);
    }

    /// <summary>Capture all screens (virtual full desktop).</summary>
    public byte[]? CaptureAllScreens()
    {
        var bounds = System.Windows.Forms.Screen.AllScreens
            .Select(s => s.Bounds)
            .Aggregate(Rectangle.Union);
        return CaptureBounds(bounds);
    }

    /// <summary>Capture a specific rectangle (e.g. primary screen). Includes host cursor when visible.</summary>
    public byte[]? CaptureBounds(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            DrawCursorOnto(g, bounds);
        }

        return BitmapToJpeg(bitmap);
    }

    /// <summary>Draw the host PC cursor onto the bitmap when it is visible and within the captured bounds.</summary>
    private static void DrawCursorOnto(Graphics g, Rectangle bounds)
    {
        var ci = new CursorInfo { CbSize = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref ci) || (ci.Flags & CursorShowing) == 0 || ci.HCursor == IntPtr.Zero)
            return;

        int hotspotX = 0, hotspotY = 0;
        if (GetIconInfo(ci.HCursor, out var ii))
        {
            hotspotX = ii.XHotspot;
            hotspotY = ii.YHotspot;
            if (ii.HbmMask != IntPtr.Zero) DeleteObject(ii.HbmMask);
            if (ii.HbmColor != IntPtr.Zero) DeleteObject(ii.HbmColor);
        }

        int x = ci.PtScreenPos.X - bounds.Left - hotspotX;
        int y = ci.PtScreenPos.Y - bounds.Top - hotspotY;
        // Allow drawing slightly outside (cursor can extend past bounds)
        if (x + 32 < 0 || y + 32 < 0 || x >= bounds.Width || y >= bounds.Height)
            return;

        IntPtr hdc = g.GetHdc();
        try
        {
            DrawIconEx(hdc, x, y, ci.HCursor, 0, 0, 0, IntPtr.Zero, DiNormal);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    private byte[]? BitmapToJpeg(Bitmap bitmap)
    {
        try
        {
            var encoder = GetEncoder(ImageFormat.Jpeg);
            if (encoder == null) return null;

            var qualityParam = new EncoderParameter(Encoder.Quality, _quality);
            using var codecParams = new EncoderParameters(1);
            codecParams.Param![0] = qualityParam;

            using var ms = new MemoryStream();
            bitmap.Save(ms, encoder, codecParams);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        return ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == format.Guid);
    }
}
