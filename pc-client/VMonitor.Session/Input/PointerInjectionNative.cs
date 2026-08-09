using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VMonitor.Session.Input;

/// <summary>
/// Windows のポインター注入 API (user32.dll) への P/Invoke 定義。
/// </summary>
/// <remarks>
/// 2 系統の API を使い分ける。
/// <list type="bullet">
///   <item>
///     <b>タッチ</b>: <c>InitializeTouchInjection</c> + <c>InjectTouchInput</c>。
///     Windows 8 以降。マルチタッチのジェスチャーがそのまま OS に伝わる。
///   </item>
///   <item>
///     <b>ペン (Windows Ink)</b>: <c>CreateSyntheticPointerDevice</c> +
///     <c>InjectSyntheticPointerInput</c>。Windows 10 1809 以降。
///     筆圧・傾きを伴う本物のペン入力として扱われるため、
///     OneNote や Whiteboard などの Ink 対応アプリで手書きになる。
///   </item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class PointerInjectionNative
{
    private const string User32 = "user32.dll";

    // ── ポインター種別 ───────────────────────────────────────────────────

    internal const uint PT_TOUCH = 0x00000002;
    internal const uint PT_PEN   = 0x00000003;

    // ── POINTER_FLAGS ────────────────────────────────────────────────────

    internal const uint POINTER_FLAG_NONE      = 0x00000000;
    internal const uint POINTER_FLAG_NEW       = 0x00000001;
    internal const uint POINTER_FLAG_INRANGE   = 0x00000002;
    internal const uint POINTER_FLAG_INCONTACT = 0x00000004;
    internal const uint POINTER_FLAG_DOWN      = 0x00010000;
    internal const uint POINTER_FLAG_UPDATE    = 0x00020000;
    internal const uint POINTER_FLAG_UP        = 0x00040000;
    internal const uint POINTER_FLAG_CANCELED  = 0x00008000;

    // ── TOUCH_MASK / TOUCH_FLAGS ─────────────────────────────────────────

    internal const uint TOUCH_FLAG_NONE          = 0x00000000;
    internal const uint TOUCH_MASK_NONE          = 0x00000000;
    internal const uint TOUCH_MASK_CONTACTAREA   = 0x00000001;
    internal const uint TOUCH_MASK_ORIENTATION   = 0x00000002;
    internal const uint TOUCH_MASK_PRESSURE      = 0x00000004;

    // ── PEN_MASK / PEN_FLAGS ─────────────────────────────────────────────

    internal const uint PEN_FLAG_NONE     = 0x00000000;
    internal const uint PEN_MASK_NONE     = 0x00000000;
    internal const uint PEN_MASK_PRESSURE = 0x00000001;
    internal const uint PEN_MASK_ROTATION = 0x00000002;
    internal const uint PEN_MASK_TILT_X   = 0x00000004;
    internal const uint PEN_MASK_TILT_Y   = 0x00000008;

    // ── POINTER_FEEDBACK_MODE ────────────────────────────────────────────

    internal const uint POINTER_FEEDBACK_DEFAULT  = 1;
    internal const uint POINTER_FEEDBACK_INDIRECT = 2;
    internal const uint POINTER_FEEDBACK_NONE     = 3;

    /// <summary>ペン (Windows Ink) の筆圧の最大値。</summary>
    internal const uint PenPressureMax = 1024;

    /// <summary>
    /// タッチの筆圧の最大値。ペンとは尺度が異なり、
    /// Microsoft のタッチ注入サンプルもこの値を使う。
    /// </summary>
    internal const uint TouchPressureMax = 32000;

    /// <summary>筆圧の既定の最大値（ペン基準）。</summary>
    internal const uint PressureMax = PenPressureMax;

    /// <summary>同時に注入できるコンタクト数の上限（API の仕様上 256）。</summary>
    internal const uint MaxContacts = 256;

    // ── 構造体 ───────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTER_INFO
    {
        public uint    pointerType;
        public uint    pointerId;
        public uint    frameId;
        public uint    pointerFlags;
        public IntPtr  sourceDevice;
        public IntPtr  hwndTarget;
        public POINT   ptPixelLocation;
        public POINT   ptHimetricLocation;
        public POINT   ptPixelLocationRaw;
        public POINT   ptHimetricLocationRaw;
        public uint    dwTime;
        public uint    historyCount;
        public int     InputData;
        public uint    dwKeyStates;
        public ulong   PerformanceCount;
        public int     ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint         touchFlags;
        public uint         touchMask;
        public RECT         rcContact;
        public RECT         rcContactRaw;
        public uint         orientation;
        public uint         pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint         penFlags;
        public uint         penMask;
        public uint         pressure;
        public uint         rotation;
        public int          tiltX;
        public int          tiltY;
    }

    /// <summary>
    /// POINTER_TYPE_INFO は type と、種別に応じた共用体からなる。
    /// x64 では POINTER_INFO が 8 バイト境界を要求するため、
    /// 共用体はオフセット 8 から始まる。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)]
        public uint type;

        [FieldOffset(8)]
        public POINTER_TOUCH_INFO touchInfo;

        [FieldOffset(8)]
        public POINTER_PEN_INFO penInfo;
    }

    // ── タッチ注入 API (Windows 8+) ──────────────────────────────────────

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    // ── 合成ポインター API (Windows 10 1809+、ペン/Ink 用) ───────────────

    [DllImport(User32, SetLastError = true)]
    internal static extern IntPtr CreateSyntheticPointerDevice(
        uint pointerType, uint maxCount, uint mode);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InjectSyntheticPointerInput(
        IntPtr device, [In] POINTER_TYPE_INFO[] pointerInfo, uint count);

    [DllImport(User32, SetLastError = true)]
    internal static extern void DestroySyntheticPointerDevice(IntPtr device);
}
