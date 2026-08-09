using System.Runtime.InteropServices;
using VMonitor.Session.Input;
using static VMonitor.Session.Input.PointerInjectionNative;

namespace VMonitor.Tests;

/// <summary>
/// Windows のポインター注入 API に渡す構造体のレイアウト検証。
///
/// これらの構造体は user32.dll にそのままのメモリ配置で渡される。
/// サイズやオフセットが Win32 の定義とずれると、注入が黙って失敗するか、
/// 最悪の場合カーネルへ不正なデータを渡すことになる。
/// マネージド側からは検出できないため、既知の正解値で固定しておく。
///
/// 期待値は x64 (8 バイトポインター・既定パッキング) の Win32 定義から算出:
///   POINTER_INFO       = 96 バイト
///   POINTER_TOUCH_INFO = 96 + 8 + 16 + 16 + 8       = 144 バイト
///   POINTER_PEN_INFO   = 96 + 8 + 16                = 120 バイト
///   POINTER_TYPE_INFO  = 8 (type + パディング) + 144 = 152 バイト
/// </summary>
public class PointerInjectionInteropTests
{
    /// <summary>
    /// これらの期待値は 64 ビットプロセスを前提としている。
    /// 32 ビットではポインター幅が変わりレイアウトも変わる。
    /// </summary>
    private static bool Is64Bit => IntPtr.Size == 8;

    [Fact]
    public void PointerInfo_HasExpectedSize()
    {
        if (!Is64Bit) return;
        Assert.Equal(96, Marshal.SizeOf<POINTER_INFO>());
    }

    [Fact]
    public void PointerTouchInfo_HasExpectedSize()
    {
        if (!Is64Bit) return;
        Assert.Equal(144, Marshal.SizeOf<POINTER_TOUCH_INFO>());
    }

    [Fact]
    public void PointerPenInfo_HasExpectedSize()
    {
        if (!Is64Bit) return;
        Assert.Equal(120, Marshal.SizeOf<POINTER_PEN_INFO>());
    }

    [Fact]
    public void PointerTypeInfo_HasExpectedSize()
    {
        if (!Is64Bit) return;
        Assert.Equal(152, Marshal.SizeOf<POINTER_TYPE_INFO>());
    }

    /// <summary>
    /// POINTER_TYPE_INFO の共用体は type (UINT32) の直後ではなく、
    /// POINTER_INFO のアラインメント要件によりオフセット 8 から始まる。
    /// ここがずれると pointerType が読まれず注入が拒否される。
    /// </summary>
    [Fact]
    public void PointerTypeInfo_UnionStartsAtOffsetEight()
    {
        if (!Is64Bit) return;

        Assert.Equal(8, Marshal.OffsetOf<POINTER_TYPE_INFO>(nameof(POINTER_TYPE_INFO.touchInfo)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<POINTER_TYPE_INFO>(nameof(POINTER_TYPE_INFO.penInfo)).ToInt32());
    }

    /// <summary>
    /// POINTER_INFO 内でポインター幅のフィールドが 8 バイト境界に載っていること。
    /// ここが崩れると以降のフィールドがすべてずれる。
    /// </summary>
    [Fact]
    public void PointerInfo_PointerFieldsAreEightByteAligned()
    {
        if (!Is64Bit) return;

        Assert.Equal(16, Marshal.OffsetOf<POINTER_INFO>(nameof(POINTER_INFO.sourceDevice)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<POINTER_INFO>(nameof(POINTER_INFO.hwndTarget)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<POINTER_INFO>(nameof(POINTER_INFO.ptPixelLocation)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<POINTER_INFO>(nameof(POINTER_INFO.PerformanceCount)).ToInt32());
    }

    /// <summary>
    /// 筆圧の写像が Windows の有効範囲 (1〜1024) に収まること。
    /// 0 は「筆圧情報なし」を意味するため、下限は 1 でなければならない。
    /// </summary>
    [Theory]
    [InlineData(-5.0)]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(1.0)]
    [InlineData(99.0)]
    [InlineData(double.NaN)]
    public void ToNativePressure_StaysWithinValidRange(double input)
    {
        uint pressure = Win32PointerInjectionBackend.ToNativePressure(input);

        Assert.InRange(pressure, 1u, PressureMax);
    }
}
