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
}
