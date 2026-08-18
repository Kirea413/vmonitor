using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session.Input;
using Xunit;

namespace VMonitor.Tests;

/// <summary>
/// 接触が積み残らないことを確かめる。
///
/// 実機で「しばらく使うとタッチが丸ごと効かなくなる。端末を回すと直るが、
/// またすぐ死ぬ」という壊れかたをした。原因は、知らない指を離す知らせを
/// 「触れた」に読み替えていたこと。離すはずの知らせで接触が 1 つ増え、
/// 二度と離されないまま積み残る。上限の 10 本に達した時点で、新しい指が
/// すべて捨てられる。回すと全解放が走るので、そこだけ復活する。
/// </summary>
// 実時間の経過を待つテストが含まれる。並行に走らせると、負荷で
// タイマーの発火が遅れて落ちることがある。ここだけ直列にする。
[Collection("実時間に依存するテスト")]
public sealed class TouchContactLeakTests
{
    private static readonly DisplayTransform Screen =
        new(new Resolution(1920, 1080), Orientation.Portrait);

    private static WindowsInkInjector Create() =>
        new(new RecordingPointerInjectionBackend(), ownsBackend: true);

    private static TouchPoint Point(int id, TouchPhase phase) => new()
    {
        Id = id, X = 0.5, Y = 0.5, Pressure = 0.5, Phase = phase,
    };

    [Fact]
    public void 知らない指を離しても接触は増えない()
    {
        using var injector = Create();

        // 触れた覚えのない指の「離した」。
        // 画面側が接触をまとめて解放したあとに、利用者が指を離すと届く。
        injector.InjectTouch(new[] { Point(1, TouchPhase.Ended) }, Screen);

        Assert.Equal(0, injector.ActiveContactCount);
    }

    [Fact]
    public void 知らない指の取り消しでも接触は増えない()
    {
        using var injector = Create();

        injector.InjectTouch(new[] { Point(7, TouchPhase.Cancelled) }, Screen);

        Assert.Equal(0, injector.ActiveContactCount);
    }

    [Fact]
    public void 離す知らせを繰り返してもタッチが死なない()
    {
        using var injector = Create();

        // 上限を超える回数だけ、知らない指を離す知らせを送る。
        // 積み残っていれば、ここで打ち止めになる。
        for (int i = 0; i < WindowsInkInjector.MaxContacts * 3; i++)
            injector.InjectTouch(new[] { Point(i, TouchPhase.Ended) }, Screen);

        Assert.Equal(0, injector.ActiveContactCount);

        // そのあとで普通に触れれば、ちゃんと接触として扱われる
        injector.InjectTouch(new[] { Point(100, TouchPhase.Began) }, Screen);

        Assert.Equal(1, injector.ActiveContactCount);
    }

    [Fact]
    public void 触れて離せば接触は残らない()
    {
        using var injector = Create();

        injector.InjectTouch(new[] { Point(3, TouchPhase.Began) }, Screen);
        Assert.Equal(1, injector.ActiveContactCount);

        injector.InjectTouch(new[] { Point(3, TouchPhase.Moved) }, Screen);
        Assert.Equal(1, injector.ActiveContactCount);

        injector.InjectTouch(new[] { Point(3, TouchPhase.Ended) }, Screen);
        Assert.Equal(0, injector.ActiveContactCount);
    }

    [Fact]
    public void 触れたあと同じ指を二重に離しても壊れない()
    {
        using var injector = Create();

        injector.InjectTouch(new[] { Point(5, TouchPhase.Began) }, Screen);
        injector.InjectTouch(new[] { Point(5, TouchPhase.Ended) }, Screen);

        // 2 回目の「離した」は知らない指として捨てられる
        injector.InjectTouch(new[] { Point(5, TouchPhase.Ended) }, Screen);

        Assert.Equal(0, injector.ActiveContactCount);

        injector.InjectTouch(new[] { Point(5, TouchPhase.Began) }, Screen);
        Assert.Equal(1, injector.ActiveContactCount);
    }

    [Fact]
    public async Task 知らせが途絶えたら接触を離す()
    {
        using var injector = Create();

        injector.InjectTouch(new[] { Point(2, TouchPhase.Began) }, Screen);
        Assert.Equal(1, injector.ActiveContactCount);

        // 端末は触れているあいだ 200ms ごとに知らせてくる。
        // それが止まったということは、離したのに「離した」が
        // 届かなかったということ。押しっぱなしにしない。
        //
        // 見切りは 1.2 秒。余裕を持って待つ。
        await Task.Delay(4_000);

        Assert.Equal(0, injector.ActiveContactCount);
    }

    [Fact]
    public void 知らせが続いているあいだは離さない()
    {
        using var injector = Create();

        // 時間はこちらで進める。
        //
        // 実時間で待つと、他のテストと並行に走ったときに待ち時間が
        // 伸び、触れ続けているはずの接触が見切られてしまう。
        // 間隔を詰めても負荷次第で落ちるため、時計ごと差し替える。
        long now = 0;
        injector.TimeSource = () => now;

        injector.InjectTouch(new[] { Point(4, TouchPhase.Began) }, Screen);

        // 長押しの最中。指は動かないが、端末は 200ms ごとに送ってくる。
        // 見切り (1.2 秒) より短い間隔なので、離れてはいけない。
        for (int i = 0; i < 20; i++)
        {
            now += 200;
            injector.InjectTouch(new[] { Point(4, TouchPhase.Moved) }, Screen);
        }

        Assert.Equal(1, injector.ActiveContactCount);
        Assert.Equal(4_000, now);
    }

    [Fact]
    public void 知らせの間隔が見切りを超えたら離す()
    {
        using var injector = Create();

        long now = 0;
        injector.TimeSource = () => now;

        injector.InjectTouch(new[] { Point(6, TouchPhase.Began) }, Screen);
        Assert.Equal(1, injector.ActiveContactCount);

        // 見切りを跨いでから次の知らせが来た。もう離れている。
        now += 1_500;

        // 送り直しのタイマーは実時間で動く。ここでは待たずに、
        // 同じ判断をする道（次の注入）で確かめる。
        injector.InjectTouch(new[] { Point(7, TouchPhase.Began) }, Screen);

        Assert.DoesNotContain(
            ((RecordingPointerInjectionBackend)injector.Backend).Frames[^1],
            p => p.Phase == TouchPhase.Moved && p.Id == 6);
    }
}
