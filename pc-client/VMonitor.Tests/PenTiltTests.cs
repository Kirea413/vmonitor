using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session.Input;
using Xunit;

namespace VMonitor.Tests;

/// <summary>
/// ペンの傾きが端末から Windows まで通ることを確かめる。
///
/// 傾きが無いと、筆先の向きを見るアプリは常に垂直に立てている扱いに
/// なる。線の太さや形が変わらず、実物のペンと同じようには描けない。
/// </summary>
public sealed class PenTiltTests
{
    private static readonly DisplayTransform Screen =
        new(new Resolution(1920, 1080), Orientation.Portrait);

    private static TouchPoint Pen(int id, TouchPhase phase, int tiltX, int tiltY) => new()
    {
        Id = id, X = 0.5, Y = 0.5, Pressure = 0.7, Phase = phase,
        IsPen = true, TiltX = tiltX, TiltY = tiltY,
    };

    [Fact]
    public void 傾きが往復しても変わらない()
    {
        var original = new TouchEvent
        {
            TimestampUs        = 1234,
            CurrentOrientation = Orientation.Portrait,
            Points             = new List<TouchPoint> { Pen(1, TouchPhase.Moved, 35, -20) },
        };

        var decoded = TouchEventCodec.Decode(TouchEventCodec.Encode(original));

        Assert.NotNull(decoded);
        Assert.Equal(35,  decoded!.Points[0].TiltX);
        Assert.Equal(-20, decoded.Points[0].TiltY);
    }

    [Fact]
    public void 手前と奥の区別が付く()
    {
        // 符号付きで運ぶ。符号無しで運ぶと、手前へ倒したのか
        // 奥へ倒したのかが混ざる。
        var original = new TouchEvent
        {
            TimestampUs        = 1,
            CurrentOrientation = Orientation.Portrait,
            Points             = new List<TouchPoint>
            {
                Pen(1, TouchPhase.Moved, -90, -90),
                Pen(2, TouchPhase.Moved,  90,  90),
            },
        };

        var decoded = TouchEventCodec.Decode(TouchEventCodec.Encode(original));

        Assert.NotNull(decoded);
        Assert.Equal(-90, decoded!.Points[0].TiltX);
        Assert.Equal(-90, decoded.Points[0].TiltY);
        Assert.Equal(90,  decoded.Points[1].TiltX);
        Assert.Equal(90,  decoded.Points[1].TiltY);
    }

    [Fact]
    public void 傾きを送ってこない端末とも繋がる()
    {
        // 18 バイトの並び（ペンの区別はあるが傾きは無い）を組み立てる。
        // 揃わないうちは繋がらない、では更新の順番に気を遣わせる。
        var full  = TouchEventCodec.Encode(new TouchEvent
        {
            TimestampUs        = 7,
            CurrentOrientation = Orientation.Portrait,
            Points             = new List<TouchPoint> { Pen(3, TouchPhase.Began, 40, 40) },
        });

        // 末尾の傾き 2 バイトを落とす
        var older = full[..(TouchEventCodec.HeaderSize + TouchEventCodec.PointSizeWithoutTilt)];

        var decoded = TouchEventCodec.Decode(older);

        Assert.NotNull(decoded);
        Assert.True(decoded!.Points[0].IsPen);
        Assert.Equal(0, decoded.Points[0].TiltX);
        Assert.Equal(0, decoded.Points[0].TiltY);
    }

    [Fact]
    public void 注入するフレームに傾きが乗る()
    {
        var backend = new RecordingPointerInjectionBackend();
        using var injector = new WindowsInkInjector(backend, ownsBackend: true);

        injector.InjectTouch(new[] { Pen(1, TouchPhase.Began, 25, -15) }, Screen);

        var injected = backend.Frames[^1][0];

        Assert.Equal(25,  injected.TiltX);
        Assert.Equal(-15, injected.TiltY);
    }

    [Fact]
    public void 送り直しでも傾きが保たれる()
    {
        var backend = new RecordingPointerInjectionBackend();
        using var injector = new WindowsInkInjector(backend, ownsBackend: true);

        injector.InjectTouch(new[] { Pen(1, TouchPhase.Began, 30, 10) }, Screen);

        // 別の指が触れた回。ペンは報告されていないので、覚えている
        // 状態から補われる。ここで傾きが 0 に戻ると、指を添えた
        // 瞬間だけペンが立ったことになる。
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Moved, 30, 10) }, Screen);
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Ended, 30, 10) }, Screen);

        var release = backend.Frames[^1][0];

        Assert.Equal(TouchPhase.Ended, release.Phase);
        Assert.Equal(30, release.TiltX);
        Assert.Equal(10, release.TiltY);
    }

    [Theory]
    [InlineData(200, 90)]
    [InlineData(-200, -90)]
    [InlineData(45, 45)]
    public void 範囲の外は丸める(int given, int expected)
    {
        // 範囲外を渡すと注入そのものが弾かれ、その拒否は
        // 「アクティブな全接触の取り消し」を伴う。巻き添えで
        // 触れている指まで壊れる。
        Assert.Equal(expected, Win32PointerInjectionBackend.ClampTilt(given));
    }
}
