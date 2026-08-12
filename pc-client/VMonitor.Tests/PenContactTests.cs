using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session.Input;
using Xunit;

namespace VMonitor.Tests;

/// <summary>
/// ペンが押されっぱなしにならないことを確かめる。
///
/// Windows のペン注入は 1 本しか扱えず、バックエンドはフレームの先頭
/// 1 点だけを注入する。手のひらが当たって指の点が混ざると、ペンの点が
/// 先頭に来ないことがあり、その回が丸ごと捨てられる。「離した」が
/// 捨てられると、Windows から見てペンは押されたままになる。
/// </summary>
public sealed class PenContactTests
{
    private static readonly DisplayTransform Screen =
        new(new Resolution(1920, 1080), Orientation.Portrait);

    private static WindowsInkInjector Create() =>
        new(new RecordingPointerInjectionBackend(), ownsBackend: true);

    private static TouchPoint Pen(int id, TouchPhase phase) => new()
    {
        Id = id, X = 0.5, Y = 0.5, Pressure = 0.7, Phase = phase, IsPen = true,
    };

    private static TouchPoint Finger(int id, TouchPhase phase) => new()
    {
        Id = id, X = 0.2, Y = 0.2, Pressure = 0.5, Phase = phase, IsPen = false,
    };

    [Fact]
    public void ペンで書くとペンとして扱われる()
    {
        using var injector = Create();

        injector.InjectTouch(new[] { Pen(1, TouchPhase.Began) }, Screen);

        Assert.Equal(PointerInjectionMode.Pen, injector.Mode);
    }

    [Fact]
    public void 手のひらが当たってもペンが押されっぱなしにならない()
    {
        using var injector = Create();

        // ペンで書き始める
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Began) }, Screen);

        // 手のひらが触れた。指の点が先に並ぶことがある。
        injector.InjectTouch(
            new[] { Finger(2, TouchPhase.Began), Pen(1, TouchPhase.Moved) }, Screen);

        // ペンを離す。指はまだ触れている。
        injector.InjectTouch(
            new[] { Finger(2, TouchPhase.Moved), Pen(1, TouchPhase.Ended) }, Screen);

        // ペンの接触が残っていないこと。
        // 残っていると、Windows からは書き続けているように見える。
        Assert.Equal(0, injector.ActiveContactCount);
    }

    [Fact]
    public void ペンを離したあと指で操作できる()
    {
        using var injector = Create();

        injector.InjectTouch(new[] { Pen(1, TouchPhase.Began) }, Screen);
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Ended) }, Screen);

        // 指だけになったらタッチへ戻る
        injector.InjectTouch(new[] { Finger(2, TouchPhase.Began) }, Screen);

        Assert.Equal(PointerInjectionMode.Touch, injector.Mode);
        Assert.Equal(1, injector.ActiveContactCount);
    }

    [Fact]
    public void 浮かせたペンは接触として残らない()
    {
        using var injector = Create();

        // 触れずに近づいただけ。位置は伝わるが、押してはいない。
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Hovered) }, Screen);

        Assert.Equal(0, injector.ActiveContactCount);
    }

    [Fact]
    public void 浮かせたあと下ろして離せば残らない()
    {
        using var injector = Create();

        injector.InjectTouch(new[] { Pen(1, TouchPhase.Hovered) }, Screen);
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Began) }, Screen);
        Assert.Equal(1, injector.ActiveContactCount);

        injector.InjectTouch(new[] { Pen(1, TouchPhase.Ended) }, Screen);
        Assert.Equal(0, injector.ActiveContactCount);

        // 離したあとも浮いている
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Hovered) }, Screen);
        Assert.Equal(0, injector.ActiveContactCount);
    }

    [Fact]
    public void 離した覚えが無いまま押し直しても線が繋がらない()
    {
        var backend = new RecordingPointerInjectionBackend();
        using var injector = new WindowsInkInjector(backend, ownsBackend: true);

        // 書く
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Began) }, Screen);
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Moved) }, Screen);

        // 「離した」が届かなかった。こちらは触れたままだと思っている。
        Assert.Equal(1, injector.ActiveContactCount);

        // 別の場所で書き始める
        var again = new TouchPoint
        {
            Id = 1, X = 0.9, Y = 0.9, Pressure = 0.7,
            Phase = TouchPhase.Began, IsPen = true,
        };

        injector.InjectTouch(new[] { again }, Screen);

        // ここで Moved に読み替えると、前の位置から線が繋がる。
        // 先に離してから始まっていること。
        var last = backend.Frames[^1];

        Assert.Contains(last, p => p.Phase == TouchPhase.Ended);
        Assert.Contains(last, p => p.Phase == TouchPhase.Began);
        Assert.DoesNotContain(last, p => p.Phase == TouchPhase.Moved);
    }

    [Fact]
    public void 浮かせたペンの位置が届く()
    {
        var backend = new RecordingPointerInjectionBackend();
        using var injector = new WindowsInkInjector(backend, ownsBackend: true);

        // 触れる前の知らせなので、その ID の接触はまだ無い。
        // 「知らない指の続き」と一緒に捨てていた時期があり、
        // 浮かせたペンの位置が一度も届いていなかった。
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Hovered) }, Screen);

        Assert.Contains(backend.Frames[^1], p => p.Phase == TouchPhase.Hovered);
    }

    [Fact]
    public void 書いている途中で浮いたら離したことになる()
    {
        var backend = new RecordingPointerInjectionBackend();
        using var injector = new WindowsInkInjector(backend, ownsBackend: true);

        injector.InjectTouch(new[] { Pen(1, TouchPhase.Began) }, Screen);
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Moved) }, Screen);

        // ペン先が画面から浮いた。触れてはいないが、まだ近くにある。
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Hovered) }, Screen);

        // 追跡から外すだけでは駄目。Windows には UP を送っていないので
        // 向こうではペンが触れたままになる。こちらは追跡をやめている
        // ぶん 1.2 秒の時間切れも働かず、永久に押されっぱなしになる。
        Assert.Contains(backend.Frames[^1], p => p.Phase == TouchPhase.Ended);
        Assert.Equal(0, injector.ActiveContactCount);
    }

    [Fact]
    public void 浮いたあとに届いた離したで壊れない()
    {
        var backend = new RecordingPointerInjectionBackend();
        using var injector = new WindowsInkInjector(backend, ownsBackend: true);

        injector.InjectTouch(new[] { Pen(1, TouchPhase.Began) }, Screen);

        // 実機では、離すとホバーと「離した」の両方が届く。順序は選べない。
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Hovered) }, Screen);
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Ended) }, Screen);

        Assert.Equal(0, injector.ActiveContactCount);

        // 別の場所で書き直す。前の位置から線が繋がってはいけない。
        var again = new TouchPoint
        {
            Id = 1, X = 0.9, Y = 0.9, Pressure = 0.7,
            Phase = TouchPhase.Began, IsPen = true,
        };

        injector.InjectTouch(new[] { again }, Screen);

        Assert.Contains(backend.Frames[^1], p => p.Phase == TouchPhase.Began);
        Assert.DoesNotContain(backend.Frames[^1], p => p.Phase == TouchPhase.Ended);
    }

    [Fact]
    public void 別IDのホバーでも書いていたペンが離れる()
    {
        var backend = new RecordingPointerInjectionBackend();
        using var injector = new WindowsInkInjector(backend, ownsBackend: true);

        injector.InjectTouch(new[] { Pen(1, TouchPhase.Began) }, Screen);

        // 端末側のホバーは、押した・離したとは別の ID で届く。
        // Flutter が触れるたびに新しい ID を振るため一致しない。
        // ID の一致を待っていると、離したペンが時間切れまで残り、
        // その間ホバーで位置が動くので次の一筆が前から繋がる。
        injector.InjectTouch(new[] { Pen(7, TouchPhase.Hovered) }, Screen);

        Assert.Equal(0, injector.ActiveContactCount);
        Assert.Contains(backend.Frames[^1], p => p.Phase == TouchPhase.Ended);
    }

    [Fact]
    public void 指で触れている間は別IDのホバーで離れない()
    {
        using var injector = Create();

        // 指はホバーを出さない。タッチ中に紛れ込んだホバーで
        // 触れている指まで離してしまうと、巻き添えになる。
        injector.InjectTouch(new[] { Finger(1, TouchPhase.Began) }, Screen);
        injector.InjectTouch(new[] { Finger(1, TouchPhase.Moved) }, Screen);

        Assert.Equal(1, injector.ActiveContactCount);
    }

    [Fact]
    public void 離した直後に遅れて届いた心拍で復活しない()
    {
        using var injector = Create();

        injector.InjectTouch(new[] { Pen(1, TouchPhase.Began) }, Screen);
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Ended) }, Screen);

        Assert.Equal(0, injector.ActiveContactCount);

        // 端末は触れているあいだ 200ms ごとに送る。離した直後、
        // その 1 通が遅れて届くことがある。これで押し直されると、
        // 次のストロークが前の位置から繋がる。
        injector.InjectTouch(new[] { Pen(1, TouchPhase.Moved) }, Screen);

        Assert.Equal(0, injector.ActiveContactCount);
    }
}
