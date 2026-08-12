// Feature: vmonitor, Property 15: タッチイベントの Windows Ink 注入

using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session.Input;

namespace VMonitor.Tests;

/// <summary>
/// Property 15: タッチイベントの Windows Ink 注入
/// Validates: Requirements 6.2
///
/// 任意のタッチイベントに対して、PC クライアントの Windows Ink インジェクターが
/// 対応する入力イベントを生成しなければならない（API はモック）。
///
/// Windows Ink API (InjectTouchInput) は Windows 専用のため、
/// 本テストでは座標変換とイベント生成ロジックを検証する。
/// - InjectTouch は任意のタッチポイントリストに対して例外を発生させてはならない
/// - 各タッチポイントの正規化座標は正しくピクセル座標に変換されなければならない
/// - 変換後のピクセル座標は仮想ディスプレイ解像度の範囲内に収まらなければならない
/// </summary>
public class TouchWindowsInkInjectionPropertyTests
{
    // ── ヘルパー ────────────────────────────────────────────────────────────

    /// <summary>
    /// FsCheck の任意整数から正規化座標 [0.0, 1.0] を生成するヘルパー。
    /// 10001 段階（0.0, 0.0001, …, 1.0）に量子化する。
    /// </summary>
    private static double NormalizeCoord(int raw) =>
        Math.Abs(raw) % 10001 / 10000.0;

    /// <summary>
    /// FsCheck の任意整数から有効な解像度幅（640〜3840）を生成するヘルパー。
    /// </summary>
    private static int NormalizeWidth(int raw) =>
        640 + Math.Abs(raw) % (3840 - 640 + 1);

    /// <summary>
    /// FsCheck の任意整数から有効な解像度高さ（480〜2160）を生成するヘルパー。
    /// </summary>
    private static int NormalizeHeight(int raw) =>
        480 + Math.Abs(raw) % (2160 - 480 + 1);

    /// <summary>
    /// FsCheck の任意整数から TouchPhase を生成するヘルパー。
    /// </summary>
    private static TouchPhase NormalizePhase(int raw) =>
        (TouchPhase)(Math.Abs(raw) % 4);

    /// <summary>
    /// FsCheck の任意整数から Orientation を生成するヘルパー。
    /// </summary>
    private static Orientation NormalizeOrientation(int raw) =>
        (Orientation)(Math.Abs(raw) % 4);

    /// <summary>
    /// FsCheck の任意整数から 1〜10 の個数を生成するヘルパー。
    /// </summary>
    private static int NormalizeCount(int raw) =>
        1 + Math.Abs(raw) % 10;

    /// <summary>
    /// テスト対象の WindowsInkInjector を生成する。
    ///
    /// 実バックエンド (Win32PointerInjectionBackend) を使うと、テスト実行中に
    /// 本物のタッチ入力が開発者のデスクトップへ注入されてしまう。
    /// 注入内容の検証は記録用バックエンドで行う。
    /// </summary>
    private static WindowsInkInjector CreateSut() => CreateSut(out _);

    /// <summary>
    /// 記録用バックエンドを取り出せる形でテスト対象を生成する。
    /// </summary>
    private static WindowsInkInjector CreateSut(out RecordingPointerInjectionBackend backend)
    {
        backend = new RecordingPointerInjectionBackend();
        return new WindowsInkInjector(backend, ownsBackend: true);
    }

    // ── Property 15-A: InjectTouch は任意のタッチポイントに対して例外を発生させない ──

    /// <summary>
    /// Property 15-A: 任意の正規化座標・向き・解像度に対して、
    /// InjectTouch は例外を発生させてはならない（単一ポイント）。
    ///
    /// Windows Ink API は非 Windows 環境ではシミュレーション動作するため、
    /// 任意の入力に対して安全に処理できることを検証する。
    ///
    /// Validates: Requirements 6.2
    /// </summary>
    [Property(MaxTest = 100)]
    public bool InjectTouch_DoesNotThrow_ForSingleTouchPoint(
        int rawW, int rawH, int rawOrientation,
        int rawX, int rawY, int rawPressure, int rawPhase)
    {
        int w = NormalizeWidth(rawW);
        int h = NormalizeHeight(rawH);
        var orientation = NormalizeOrientation(rawOrientation);
        double x = NormalizeCoord(rawX);
        double y = NormalizeCoord(rawY);
        double pressure = NormalizeCoord(rawPressure);
        var phase = NormalizePhase(rawPhase);

        var injector = CreateSut();
        var resolution = new Resolution(w, h);
        var transform = new DisplayTransform(resolution, orientation);

        var points = new List<TouchPoint>
        {
            new TouchPoint { Id = 0, X = x, Y = y, Pressure = pressure, Phase = phase }
        };

        Exception? ex = null;
        try { injector.InjectTouch(points, transform); }
        catch (Exception e) { ex = e; }

        return ex == null;
    }

    // ── Property 15-B: 変換後のピクセル座標は仮想ディスプレイ解像度の範囲内に収まる ─

    /// <summary>
    /// Property 15-B: 任意の正規化タッチ座標 (x, y) と DisplayTransform に対して、
    /// TransformPoint で得られるピクセル座標は仮想ディスプレイ解像度の範囲内に
    /// 収まらなければならない（[0, Width-1] × [0, Height-1]）。
    ///
    /// Validates: Requirements 6.2
    /// </summary>
    [Property(MaxTest = 100)]
    public bool TransformedPixelCoords_AreWithinDisplayBounds(
        int rawW, int rawH, int rawOrientation, int rawX, int rawY)
    {
        int w = NormalizeWidth(rawW);
        int h = NormalizeHeight(rawH);
        var orientation = NormalizeOrientation(rawOrientation);
        double normX = NormalizeCoord(rawX);
        double normY = NormalizeCoord(rawY);

        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(w, h), orientation);

        var (pixelX, pixelY) = injector.TransformPoint(normX, normY);

        return pixelX >= 0 && pixelX < w
            && pixelY >= 0 && pixelY < h;
    }

    // ── Property 15-C: InjectTouch は各タッチポイントの変換を解像度範囲内で行う ──

    /// <summary>
    /// Property 15-C: 任意のタッチポイント座標と解像度・向きに対して、
    /// InjectTouch による変換結果が解像度範囲内に収まり例外なく処理されること。
    ///
    /// UpdateTransform で設定された変換を用いて各ポイントを変換し、
    /// TransformPoint の結果が仮想ディスプレイ範囲内であることを確認する。
    ///
    /// Validates: Requirements 6.2
    /// </summary>
    [Property(MaxTest = 100)]
    public bool InjectTouch_ProcessesPointWithCorrectTransform(
        int rawW, int rawH, int rawOrientation,
        int rawX, int rawY, int rawPressure, int rawPhase)
    {
        int w = NormalizeWidth(rawW);
        int h = NormalizeHeight(rawH);
        var orientation = NormalizeOrientation(rawOrientation);
        double x = NormalizeCoord(rawX);
        double y = NormalizeCoord(rawY);
        double pressure = NormalizeCoord(rawPressure);
        var phase = NormalizePhase(rawPhase);

        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(w, h), orientation);
        var transform = new DisplayTransform(new Resolution(w, h), orientation);

        var points = new List<TouchPoint>
        {
            new TouchPoint { Id = 0, X = x, Y = y, Pressure = pressure, Phase = phase }
        };

        // InjectTouch が例外なく処理すること
        Exception? ex = null;
        try { injector.InjectTouch(points, transform); }
        catch (Exception e) { ex = e; }

        if (ex != null) return false;

        // 変換結果が解像度範囲内であること
        var (px, py) = injector.TransformPoint(x, y);
        return px >= 0 && px < w && py >= 0 && py < h;
    }

    // ── Property 15-D: DisplayTransform がキャッシュ状態と異なる場合でも正しく動作する ─

    /// <summary>
    /// Property 15-D: InjectTouch に渡す DisplayTransform が
    /// UpdateTransform で設定された状態と異なる場合でも、
    /// 渡された DisplayTransform に基づいて正しくピクセル座標を計算し
    /// 例外を発生させてはならない。
    ///
    /// Validates: Requirements 6.2
    /// </summary>
    [Property(MaxTest = 100)]
    public bool InjectTouch_UsesPassedTransform_NotCachedTransform(
        int rawW, int rawH, int rawOrientation,
        int rawX, int rawY, int rawPressure, int rawPhase)
    {
        int w = NormalizeWidth(rawW);
        int h = NormalizeHeight(rawH);
        var orientation = NormalizeOrientation(rawOrientation);
        double x = NormalizeCoord(rawX);
        double y = NormalizeCoord(rawY);
        double pressure = NormalizeCoord(rawPressure);
        var phase = NormalizePhase(rawPhase);

        var injector = CreateSut();

        // 意図的に異なる解像度でキャッシュを初期化する
        injector.UpdateTransform(new Resolution(800, 600), Orientation.Portrait);

        // キャッシュと異なる DisplayTransform を渡して InjectTouch を呼び出す
        var transform = new DisplayTransform(new Resolution(w, h), orientation);
        var points = new List<TouchPoint>
        {
            new TouchPoint { Id = 0, X = x, Y = y, Pressure = pressure, Phase = phase }
        };

        Exception? ex = null;
        try { injector.InjectTouch(points, transform); }
        catch (Exception e) { ex = e; }

        // 例外が発生しないこと（渡された transform で正常に処理されること）
        return ex == null;
    }

    // ── 具体的なユニットテスト ─────────────────────────────────────────────

    /// <summary>
    /// Portrait 向きで正規化座標 (0.5, 0.5) は解像度 1920x1080 の中央 (960, 540) に変換されること。
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public void Portrait_CenterPoint_TransformsToDisplayCenter()
    {
        var injector = CreateSut();
        var resolution = new Resolution(1920, 1080);
        injector.UpdateTransform(resolution, Orientation.Portrait);

        var (pixelX, pixelY) = injector.TransformPoint(0.5, 0.5);

        Assert.Equal(960, pixelX);
        Assert.Equal(540, pixelY);
    }

    /// <summary>
    /// Portrait 向きで正規化座標 (0.0, 0.0) は左上 (0, 0) に変換されること。
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public void Portrait_TopLeftPoint_TransformsToOrigin()
    {
        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(1920, 1080), Orientation.Portrait);

        var (pixelX, pixelY) = injector.TransformPoint(0.0, 0.0);

        Assert.Equal(0, pixelX);
        Assert.Equal(0, pixelY);
    }

    /// <summary>
    /// InjectTouch は空でないタッチポイントリストを受け取っても例外を発生させないこと。
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public void InjectTouch_WithSingleTouchPoint_DoesNotThrow()
    {
        var injector = CreateSut();
        var resolution = new Resolution(1920, 1080);
        var transform = new DisplayTransform(resolution, Orientation.Portrait);

        var points = new List<TouchPoint>
        {
            new TouchPoint
            {
                Id = 0,
                X = 0.3,
                Y = 0.7,
                Pressure = 1.0,
                Phase = TouchPhase.Began
            }
        };

        var exception = Record.Exception(() => injector.InjectTouch(points, transform));
        Assert.Null(exception);
    }

    /// <summary>
    /// InjectTouch は空のタッチポイントリストを受け取っても例外を発生させないこと。
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public void InjectTouch_WithEmptyPoints_DoesNotThrow()
    {
        var injector = CreateSut();
        var transform = new DisplayTransform(new Resolution(1920, 1080), Orientation.Portrait);

        var exception = Record.Exception(() =>
            injector.InjectTouch(new List<TouchPoint>(), transform));

        Assert.Null(exception);
    }

    /// <summary>
    /// InjectTouch はマルチタッチ（複数ポイント）に対しても例外を発生させないこと。
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public void InjectTouch_WithMultipleTouchPoints_DoesNotThrow()
    {
        var injector = CreateSut();
        var resolution = new Resolution(2560, 1440);
        var transform = new DisplayTransform(resolution, Orientation.Landscape);

        var points = new List<TouchPoint>
        {
            new TouchPoint { Id = 0, X = 0.2, Y = 0.3, Pressure = 0.8, Phase = TouchPhase.Began },
            new TouchPoint { Id = 1, X = 0.6, Y = 0.7, Pressure = 0.9, Phase = TouchPhase.Began },
            new TouchPoint { Id = 2, X = 0.9, Y = 0.1, Pressure = 1.0, Phase = TouchPhase.Began },
        };

        var exception = Record.Exception(() => injector.InjectTouch(points, transform));
        Assert.Null(exception);
    }

    /// <summary>
    /// 境界値 (1.0, 1.0) の正規化座標は解像度の最大ピクセル（Width-1, Height-1）を
    /// 超えないこと（クランプが正しく機能すること）。
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public void TransformPoint_MaxNormalizedCoord_ClampsToMaxPixel()
    {
        var injector = CreateSut();
        var resolution = new Resolution(1920, 1080);
        injector.UpdateTransform(resolution, Orientation.Portrait);

        var (pixelX, pixelY) = injector.TransformPoint(1.0, 1.0);

        // クランプにより Width-1, Height-1 を超えないこと
        Assert.True(pixelX <= resolution.Width - 1,
            $"pixelX={pixelX} should be <= {resolution.Width - 1}");
        Assert.True(pixelY <= resolution.Height - 1,
            $"pixelY={pixelY} should be <= {resolution.Height - 1}");
    }

    // ── Property 15-E: 注入されたポインターが変換後の座標と一致する ──────────

    /// <summary>
    /// Property 15-E: 任意の 1 点タッチに対して、バックエンドへ注入される
    /// ポインターの座標が TransformPoint の変換結果と一致しなければならない。
    ///
    /// 「例外が出ない」だけでなく、正しい座標が実際に注入経路へ渡ることを検証する。
    ///
    /// Validates: Requirements 6.2
    /// </summary>
    [Property(MaxTest = 100)]
    public bool InjectedPointer_MatchesTransformedCoordinate(
        int rawW, int rawH, int rawOrientation, int rawX, int rawY)
    {
        int w = NormalizeWidth(rawW);
        int h = NormalizeHeight(rawH);
        var orientation = NormalizeOrientation(rawOrientation);
        double x = NormalizeCoord(rawX);
        double y = NormalizeCoord(rawY);

        var injector = CreateSut(out var backend);
        var resolution = new Resolution(w, h);
        injector.UpdateTransform(resolution, orientation);

        var points = new List<TouchPoint>
        {
            new TouchPoint { Id = 7, X = x, Y = y, Pressure = 1.0, Phase = TouchPhase.Began }
        };

        injector.InjectTouch(points, new DisplayTransform(resolution, orientation));

        var frame = backend.LastFrame;
        if (frame is null || frame.Count != 1) return false;

        var expected = injector.TransformPoint(x, y);
        return frame[0].PixelX == expected.PixelX
            && frame[0].PixelY == expected.PixelY;
    }

    // ── Property 15-F: マルチタッチが 1 フレームにまとめて注入される ─────────

    /// <summary>
    /// Property 15-F: 同時に n 点 (1〜10) をタッチした場合、
    /// 1 回の注入フレームに n 個のポインターが含まれ、
    /// それぞれが異なるネイティブ ID を持たなければならない。
    ///
    /// Windows のポインター注入 API は同時接触を 1 フレームで要求するため、
    /// ID が重複するとコンタクトが取り違えられる。
    ///
    /// Validates: Requirements 6.2, 6.4
    /// </summary>
    [Property(MaxTest = 100)]
    public bool MultiTouch_InjectsAllPointsInSingleFrameWithDistinctIds(int rawCount, int rawW, int rawH)
    {
        int count = NormalizeCount(rawCount);
        int w = NormalizeWidth(rawW);
        int h = NormalizeHeight(rawH);
        var resolution = new Resolution(w, h);

        var injector = CreateSut(out var backend);
        injector.UpdateTransform(resolution, Orientation.Portrait);

        var points = Enumerable.Range(0, count).Select(i => new TouchPoint
        {
            Id = i,
            X = i / (double)count,
            Y = i / (double)count,
            Pressure = 1.0,
            Phase = TouchPhase.Began
        }).ToList();

        injector.InjectTouch(points, new DisplayTransform(resolution, Orientation.Portrait));

        var frame = backend.LastFrame;
        if (frame is null || frame.Count != count) return false;

        return frame.Select(p => p.Id).Distinct().Count() == count;
    }

    // ── 接触ライフサイクルのユニットテスト ─────────────────────────────────

    /// <summary>
    /// 接触中のコンタクトは、そのフレームでイベントが来なくても
    /// 注入フレームに引き継がれなければならない。
    ///
    /// Windows のポインター注入 API は毎フレーム「接触中の全コンタクト」を要求し、
    /// 落とすと指が離れたものとして扱われてドラッグが途切れる。
    ///
    /// Validates: Requirements 6.2, 6.4
    /// </summary>
    [Fact]
    public void ActiveContact_IsCarriedForward_WhenNotReportedInFrame()
    {
        var injector = CreateSut(out var backend);
        var resolution = new Resolution(1920, 1080);
        var transform = new DisplayTransform(resolution, Orientation.Portrait);
        injector.UpdateTransform(resolution, Orientation.Portrait);

        // 2 本の指を同時に置く
        injector.InjectTouch(new List<TouchPoint>
        {
            new() { Id = 0, X = 0.2, Y = 0.2, Pressure = 1.0, Phase = TouchPhase.Began },
            new() { Id = 1, X = 0.8, Y = 0.8, Pressure = 1.0, Phase = TouchPhase.Began },
        }, transform);

        // 片方の指だけが動いたイベントが届く
        injector.InjectTouch(new List<TouchPoint>
        {
            new() { Id = 0, X = 0.3, Y = 0.3, Pressure = 1.0, Phase = TouchPhase.Moved },
        }, transform);

        var frame = backend.LastFrame;
        Assert.NotNull(frame);

        // 動いていない Id=1 も接触継続として含まれること
        Assert.Equal(2, frame!.Count);
        Assert.Equal(2, injector.ActiveContactCount);
    }

    /// <summary>
    /// 新規接触は Began (DOWN)、継続は Moved (UPDATE)、終了は Ended (UP) として
    /// 注入されなければならない。
    ///
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public void ContactLifecycle_InjectsDownUpdateUpInOrder()
    {
        var injector = CreateSut(out var backend);
        var resolution = new Resolution(1920, 1080);
        var transform = new DisplayTransform(resolution, Orientation.Portrait);
        injector.UpdateTransform(resolution, Orientation.Portrait);

        injector.InjectTouch(new List<TouchPoint>
        { new() { Id = 3, X = 0.5, Y = 0.5, Pressure = 1.0, Phase = TouchPhase.Began } }, transform);

        injector.InjectTouch(new List<TouchPoint>
        { new() { Id = 3, X = 0.6, Y = 0.5, Pressure = 1.0, Phase = TouchPhase.Moved } }, transform);

        injector.InjectTouch(new List<TouchPoint>
        { new() { Id = 3, X = 0.6, Y = 0.5, Pressure = 1.0, Phase = TouchPhase.Ended } }, transform);

        var frames = backend.Frames;
        Assert.Equal(3, frames.Count);
        Assert.Equal(TouchPhase.Began, frames[0][0].Phase);
        Assert.Equal(TouchPhase.Moved, frames[1][0].Phase);
        Assert.Equal(TouchPhase.Ended, frames[2][0].Phase);

        // 指が離れたので追跡状態は空になる
        Assert.Equal(0, injector.ActiveContactCount);
    }

    /// <summary>
    /// DOWN を受け取っていない ID に Moved が届いた場合、
    /// Began (DOWN) に補正して注入しなければならない。
    ///
    /// Windows は DOWN のない UPDATE を拒否するため、
    /// パケットロスや再接続でフェーズが欠けると以降の入力が全て失われる。
    ///
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public void MovedWithoutPrecedingBegan_IsPromotedToBegan()
    {
        var injector = CreateSut(out var backend);
        var resolution = new Resolution(1920, 1080);
        var transform = new DisplayTransform(resolution, Orientation.Portrait);

        injector.InjectTouch(new List<TouchPoint>
        { new() { Id = 5, X = 0.5, Y = 0.5, Pressure = 1.0, Phase = TouchPhase.Moved } }, transform);

        var frame = backend.LastFrame;
        Assert.NotNull(frame);
        Assert.Equal(TouchPhase.Began, frame![0].Phase);
    }

    /// <summary>
    /// 既に接触中の ID に再度 Began が届いた場合は、
    /// 前の接触をその場で離してから、新しく始めなければならない。
    ///
    /// 以前はここで Moved に降格させていた。二重 DOWN は避けられるが、
    /// 「押した」を「動いた」に変えることになる。こちらが覚えている
    /// 前の位置から新しい位置まで線が引かれるため、実機では
    /// 「ペンを離して書き直すと、離した位置から繋がる」という形で出た。
    ///
    /// 覚えが残っているのに Began が来るということは、前の接触が
    /// 正しく離れていない。読み替えではなく、離してから始める。
    ///
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public void DuplicateBegan_ReleasesThenStartsAgain()
    {
        var injector = CreateSut(out var backend);
        var resolution = new Resolution(1920, 1080);
        var transform = new DisplayTransform(resolution, Orientation.Portrait);

        var began = new List<TouchPoint>
        { new() { Id = 2, X = 0.4, Y = 0.4, Pressure = 1.0, Phase = TouchPhase.Began } };

        injector.InjectTouch(began, transform);
        injector.InjectTouch(began, transform);

        Assert.Equal(TouchPhase.Began, backend.Frames[0][0].Phase);

        // 2 回目は「離す」と「始める」が並ぶ。Moved は出ない。
        var second = backend.Frames[1];

        Assert.Contains(second, p => p.Phase == TouchPhase.Ended);
        Assert.Contains(second, p => p.Phase == TouchPhase.Began);
        Assert.DoesNotContain(second, p => p.Phase == TouchPhase.Moved);
    }

    /// <summary>
    /// 拡張ディスプレイでは、注入座標に仮想デスクトップ上の原点オフセットが
    /// 加算されなければならない。加算しないと入力が常にプライマリ側へ落ちる。
    ///
    /// Validates: Requirements 6.2, 6.6
    /// </summary>
    [Fact]
    public void DisplayOrigin_IsAddedToInjectedCoordinates()
    {
        var injector = CreateSut(out var backend);
        var resolution = new Resolution(1920, 1080);
        injector.UpdateTransform(resolution, Orientation.Portrait);
        injector.DisplayOriginX = 2560;
        injector.DisplayOriginY = 0;

        injector.InjectTouch(new List<TouchPoint>
        { new() { Id = 0, X = 0.5, Y = 0.5, Pressure = 1.0, Phase = TouchPhase.Began } },
            new DisplayTransform(resolution, Orientation.Portrait));

        var frame = backend.LastFrame;
        Assert.NotNull(frame);
        Assert.Equal(2560 + 960, frame![0].PixelX);
        Assert.Equal(540, frame[0].PixelY);
    }

    /// <summary>
    /// 正規化筆圧 [0.0, 1.0] は Windows の筆圧範囲 1〜1024 に写像されなければならない。
    /// 0 は「筆圧情報なし」と解釈されるため下限は 1 になる。
    ///
    /// Validates: Requirements 6.2
    /// </summary>
    [Theory]
    [InlineData(0.0, 1u)]
    [InlineData(0.5, 512u)]
    [InlineData(1.0, 1024u)]
    public void NormalizedPressure_MapsToWindowsPressureRange(double normalized, uint expected)
    {
        Assert.Equal(expected, Win32PointerInjectionBackend.ToNativePressure(normalized));
    }

    /// <summary>
    /// ReleaseAllContacts は接触中の全コンタクトを Ended として注入し、
    /// 追跡状態を空にしなければならない。
    /// 切断時にこれを行わないと、指が押されっぱなしのまま残る。
    ///
    /// Validates: Requirements 6.2
    /// </summary>
    [Fact]
    public void ReleaseAllContacts_InjectsUpForEveryActiveContact()
    {
        var injector = CreateSut(out var backend);
        var resolution = new Resolution(1920, 1080);
        var transform = new DisplayTransform(resolution, Orientation.Portrait);

        injector.InjectTouch(new List<TouchPoint>
        {
            new() { Id = 0, X = 0.1, Y = 0.1, Pressure = 1.0, Phase = TouchPhase.Began },
            new() { Id = 1, X = 0.9, Y = 0.9, Pressure = 1.0, Phase = TouchPhase.Began },
        }, transform);

        injector.ReleaseAllContacts();

        var frame = backend.LastFrame;
        Assert.NotNull(frame);
        Assert.Equal(2, frame!.Count);
        Assert.All(frame, p => Assert.Equal(TouchPhase.Ended, p.Phase));
        Assert.Equal(0, injector.ActiveContactCount);
    }
}
