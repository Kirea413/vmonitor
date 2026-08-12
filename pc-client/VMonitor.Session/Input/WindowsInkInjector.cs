using System.Numerics;
using VMonitor.Core.Interfaces;
using System.Linq;
using VMonitor.Core.Models;

namespace VMonitor.Session.Input;

/// <summary>
/// <see cref="IWindowsInkInjector"/> の実装。
/// スマホから受信したタッチイベントを Windows のポインター注入 API を通じて
/// 実際の入力として Windows に送り込む。
/// </summary>
/// <remarks>
/// <para>
/// 座標変換は <see cref="UpdateTransform"/> で設定された行列を使用する。
/// スマホの正規化座標 [0.0, 1.0] → 仮想ディスプレイのピクセル座標への変換を、
/// 画面向きに応じた回転・スケーリング行列で行う。
/// </para>
/// <para>
/// Windows のポインター注入 API は「そのフレーム時点で接触している全コンタクト」を
/// 毎回まとめて要求し、かつ接触は必ず DOWN → UPDATE… → UP の順で送る必要がある。
/// スマホ側は移動のなかった指のイベントを送ってこないため、
/// このクラスが接触中のコンタクトを追跡して差分を補完する。
/// </para>
/// </remarks>
public sealed class WindowsInkInjector : IWindowsInkInjector, IDisposable
{
    /// <summary>同時に追跡するコンタクト数の上限。</summary>
    public const int MaxContacts = 10;

    // ── 依存 ───────────────────────────────────────────────────────────────

    private readonly IPointerInjectionBackend _backend;
    private readonly bool _ownsBackend;

    // ── 変換状態 ───────────────────────────────────────────────────────────

    /// <summary>現在の座標変換行列。UpdateTransform で更新される。</summary>
    private Matrix3x2 _transformMatrix = Matrix3x2.Identity;

    /// <summary>現在の変換に使用する仮想ディスプレイ解像度。</summary>
    private Resolution _currentResolution = new(1920, 1080);

    /// <summary>現在の変換に使用する画面向き。</summary>
    private Orientation _currentOrientation = Orientation.Portrait;

    /// <summary>変換行列の読み書きを保護するロック。</summary>
    private readonly object _transformLock = new();

    // ── 接触追跡 ───────────────────────────────────────────────────────────

    /// <summary>接触中のコンタクト。キーはスマホ側のタッチ ID。</summary>
    private readonly Dictionary<int, ActiveContact> _activeContacts = new();

    /// <summary>触れ続けている接触を送り直すためのタイマー。</summary>
    /// <remarks>
    /// <para>
    /// Windows へ注入した接触は、放っておくと勝手に消える。一定の間隔で
    /// 更新を送り続けないと、システム側が「もう触れていない」とみなす。
    /// </para>
    /// <para>
    /// 端末は指が動いたときにしか知らせを送らない。長押しのように指を
    /// 止めていると、こちらへ何も届かない時間ができる。その間に接触が
    /// 消え、あとから届く「離した」は DOWN の無い UP として拒まれる。
    /// そしてその拒否は「アクティブな全接触の取り消し」を伴うため、
    /// 触れている他の指まで巻き添えで壊れる。
    /// </para>
    /// <para>
    /// 実機では「長押ししたあとタッチが丸ごと効かなくなる」という形で
    /// 出た。届かない間はこちらが同じ位置を送り直して繋ぎ止める。
    /// </para>
    /// </remarks>
    private Timer? _holdTimer;

    /// <summary>送り直す間隔。Windows の見切りより十分に短くする。</summary>
    private static readonly TimeSpan HoldInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 端末からの知らせがこれだけ途絶えたら、その接触は離れたとみなす。
    /// </summary>
    /// <remarks>
    /// 端末は触れているあいだ 200ms ごとに送ってくる。多少の遅れや
    /// 取りこぼしで離してしまわないよう、その数倍を待つ。
    /// </remarks>
    private const long StaleContactMs = 1_200;

    /// <summary>接触追跡の状態を保護するロック。</summary>
    private readonly object _contactLock = new();

    private bool _disposed;

    /// <summary>接触中の 1 コンタクトの状態。</summary>
    /// <param name="RefreshedAtMs">
    /// 端末から最後に知らせが届いた時刻。届かなくなった接触を
    /// こちらで離すために持つ。
    /// </param>
    private readonly record struct ActiveContact(
        uint NativeId, int PixelX, int PixelY, double Pressure, long RefreshedAtMs);

    // ── 構築 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 実行環境に適したバックエンドでインジェクターを構築する。
    /// Windows では実際の注入を行い、それ以外では記録のみ行う。
    /// </summary>
    public WindowsInkInjector()
        : this(CreateDefaultBackend(), ownsBackend: true) { }

    /// <summary>バックエンドを指定してインジェクターを構築する（テスト用）。</summary>
    /// <param name="backend">注入の実行部。</param>
    /// <param name="ownsBackend">true の場合、Dispose でバックエンドも破棄する。</param>
    public WindowsInkInjector(IPointerInjectionBackend backend, bool ownsBackend = false)
    {
        _backend     = backend ?? throw new ArgumentNullException(nameof(backend));
        _ownsBackend = ownsBackend;
        _backend.Initialize(Mode, MaxContacts);
    }

    private static IPointerInjectionBackend CreateDefaultBackend()
    {
        if (!OperatingSystem.IsWindows())
            return new RecordingPointerInjectionBackend();

        try
        {
            return new Win32PointerInjectionBackend();
        }
        catch (Exception)
        {
            // API が使えない環境ではフォールバックして入力を捨てる
            return new RecordingPointerInjectionBackend();
        }
    }

    // ── 設定 ───────────────────────────────────────────────────────────────

    private PointerInjectionMode _mode = PointerInjectionMode.Touch;

    /// <summary>
    /// 注入するポインター種別。既定はタッチ。
    /// <see cref="PointerInjectionMode.Pen"/> にすると筆圧付きの
    /// Windows Ink ペン入力として注入される。
    /// </summary>
    public PointerInjectionMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;

            // モード切り替え時に接触が残っていると宙ぶらりんになるため解放する
            ReleaseAllContacts();

            _mode = value;
            _backend.Initialize(value, MaxContacts);
        }
    }

    /// <summary>
    /// 仮想ディスプレイの左上が仮想デスクトップ上のどこにあるか（X）。
    /// 注入座標は仮想デスクトップ基準のため、拡張表示ではこのオフセットが必要になる。
    /// </summary>
    public int DisplayOriginX { get; set; }

    /// <summary>仮想ディスプレイの左上の仮想デスクトップ上 Y 座標。</summary>
    public int DisplayOriginY { get; set; }

    /// <summary>使用中のバックエンド。</summary>
    public IPointerInjectionBackend Backend => _backend;

    /// <summary>現在接触中のコンタクト数。</summary>
    public int ActiveContactCount
    {
        get { lock (_contactLock) return _activeContacts.Count; }
    }

    // ── IWindowsInkInjector ────────────────────────────────────────────────

    /// <inheritdoc/>
    public void InjectTouch(IReadOnlyList<TouchPoint> points, DisplayTransform transform)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(transform);

        if (points.Count == 0)
            return;

        // ペンか指かは、届いた点そのものから決める。
        //
        // 以前はこの判断を呼び出し側（ConnectionServer）に置いていた。
        // 種別と注入の仕方が別々の場所にあると、片方だけ直したときに
        // 食い違う。注入する側が自分で決めれば、どこから呼ばれても
        // 辻褄が合う。
        //
        // 種別が変わるときは、掴んだままの接触を解放してから切り替わる
        // （Mode の setter が面倒を見る）。
        var wanted = points.Any(p => p.IsPen)
            ? PointerInjectionMode.Pen
            : PointerInjectionMode.Touch;

        if (Mode != wanted) Mode = wanted;

        // ペンで書いている間は、ペンの点だけを送る。
        //
        // Windows のペン注入は 1 本しか扱えず、バックエンドはフレームの
        // 先頭 1 点だけを注入する。指の接触が混じっていると、ペンの点が
        // 先頭に来ないことがあり、その回の注入が丸ごと捨てられる。
        // 「離した」が捨てられると、Windows から見てペンは押されたまま
        // になる。実機では「タッチペンが押されっぱなしになる」という
        // 形で出た。
        //
        // 手のひらが当たって指の点が混ざるのは、ペンを使えば普通に
        // 起きる。捨てるのは指のほうでよい（ペンで書いている最中に
        // 指の接触を送っても、どのみち注入されない）。
        if (Mode == PointerInjectionMode.Pen && points.Any(p => p.IsPen))
            points = points.Where(p => p.IsPen).ToList();

        var matrix = ResolveMatrix(transform);
        var frame  = BuildFrame(points, transform, matrix);

        if (frame.Count == 0)
            return;

        LastInjectionSucceeded = _backend.InjectFrame(frame);

        if (!LastInjectionSucceeded)
            LastInjectedFrameSize = frame.Count;

        UpdateHoldTimer();
    }

    /// <summary>触れている接触があるあいだだけ、送り直しを動かす。</summary>
    private void UpdateHoldTimer()
    {
        bool needed;
        lock (_contactLock) needed = _activeContacts.Count > 0;

        if (needed)
        {
            _holdTimer ??= new Timer(_ => ResendHeldContacts(), null,
                                     Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            _holdTimer.Change(HoldInterval, HoldInterval);
            return;
        }

        // 触れていないのに送り続けない
        _holdTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>触れ続けている接触を、同じ位置のまま送り直す。</summary>
    private void ResendHeldContacts()
    {
        try
        {
            List<InjectedPointer> frame;

            lock (_contactLock)
            {
                if (_disposed || _activeContacts.Count == 0) return;

                long now = Environment.TickCount64;

                frame = new List<InjectedPointer>(_activeContacts.Count);

                // 知らせが途絶えた接触は、こちらで離す。
                //
                // 端末は触れているあいだ、動かなくても定期的に知らせを
                // 送ってくる。それが止まったということは、離したのに
                // 「離した」が届かなかったということ。
                //
                // これが無いと、送り直しが永久に押し続ける。実機では
                // 「指を離しているのに離した判定にならない」という形で出た。
                var stale = new List<int>();

                foreach (var (id, contact) in _activeContacts)
                {
                    if (now - contact.RefreshedAtMs > StaleContactMs)
                    {
                        stale.Add(id);

                        frame.Add(new InjectedPointer(
                            Id:       (int)contact.NativeId,
                            PixelX:   contact.PixelX,
                            PixelY:   contact.PixelY,
                            Pressure: 0,
                            Phase:    TouchPhase.Ended));

                        continue;
                    }

                    frame.Add(new InjectedPointer(
                        Id:       (int)contact.NativeId,
                        PixelX:   contact.PixelX,
                        PixelY:   contact.PixelY,
                        Pressure: contact.Pressure,
                        Phase:    TouchPhase.Moved));
                }

                foreach (var id in stale) _activeContacts.Remove(id);
            }

            _backend.InjectFrame(frame);
        }
        catch
        {
            // 送り直しに失敗しても、次の指の動きで復帰する。
            // ここで投げるとタイマーのスレッドごと落ちる。
        }
    }

    /// <summary>直近の注入が成功したか（診断用）。</summary>
    public bool LastInjectionSucceeded { get; private set; } = true;

    /// <summary>直近に失敗した注入のポインター数（診断用）。</summary>
    public int LastInjectedFrameSize { get; private set; }

    /// <summary>
    /// バックエンドが報告する直近の Win32 エラーコード（診断用）。
    /// 実バックエンド以外では常に 0。
    /// </summary>
    public int LastBackendError
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return 0;

#pragma warning disable CA1416
            return _backend is Win32PointerInjectionBackend win32 ? win32.LastError : 0;
#pragma warning restore CA1416
        }
    }

    /// <inheritdoc/>
    public void UpdateTransform(Resolution displayResolution, Orientation orientation)
    {
        ArgumentNullException.ThrowIfNull(displayResolution);

        var matrix = BuildTransformMatrix(displayResolution, orientation);

        lock (_transformLock)
        {
            _transformMatrix    = matrix;
            _currentResolution  = displayResolution;
            _currentOrientation = orientation;
        }
    }

    // ── フレーム構築 ───────────────────────────────────────────────────────

    /// <summary>
    /// 受信したタッチポイントと現在の接触状態から、
    /// 注入 API に渡す 1 フレーム分のポインター列を組み立てる。
    /// </summary>
    /// <remarks>
    /// 3 つの補正を行う。
    /// <list type="number">
    ///   <item>既に接触中の ID に Began が来たら Moved として扱う（二重 DOWN の防止）。</item>
    ///   <item>未知の ID に Moved/Ended が来たら Began として扱う（DOWN 抜けの防止）。</item>
    ///   <item>今回のイベントに含まれない接触中コンタクトを、直前の位置で Moved として補完する。</item>
    /// </list>
    /// </remarks>
    private List<InjectedPointer> BuildFrame(
        IReadOnlyList<TouchPoint> points,
        DisplayTransform transform,
        Matrix3x2 matrix)
    {
        var frame    = new List<InjectedPointer>(Math.Min(points.Count + MaxContacts, MaxContacts * 2));
        var reported = new HashSet<int>();

        lock (_contactLock)
        {
            // ペンが浮いたなら、触れている接触は残っていないはず。
            //
            // Windows のペン注入は 1 本しか扱えない。その 1 本が
            // 浮きながら同時に触れていることは、物理的に起きない。
            //
            // 端末側のホバーは、押した・離したとは別のポインター ID で
            // 届く。Flutter が触れるたびに新しい ID を振るため、ID では
            // 突き合わせられない。ID 一致を待っていると、離したはずの
            // ペンが 1.2 秒の時間切れまで残り、その間ホバーで位置が
            // 動くので、次の一筆が前の位置から繋がる。
            if (_mode == PointerInjectionMode.Pen
                && points.Any(p => p.Phase is TouchPhase.Hovered))
            {
                foreach (var (id, contact) in _activeContacts.ToList())
                {
                    // 同じ ID のものは、この後のループで面倒を見る
                    if (points.Any(p => p.Id == id))
                        continue;

                    frame.Add(new InjectedPointer(
                        Id:       (int)contact.NativeId,
                        PixelX:   contact.PixelX,
                        PixelY:   contact.PixelY,
                        Pressure: contact.Pressure,
                        Phase:    TouchPhase.Ended));

                    _activeContacts.Remove(id);
                }
            }

            foreach (var tp in points)
            {
                if (!reported.Add(tp.Id))
                    continue; // 同一フレーム内の重複 ID は先勝ちで無視する

                var (px, py) = TransformToPixels(tp.X, tp.Y, matrix, transform.DisplayResolution);

                bool wasActive = _activeContacts.TryGetValue(tp.Id, out var existing);

                // 知らない指を離す知らせは、何もせず捨てる。
                //
                // 以前はこれを Began に読み替えていた。離すはずの知らせで
                // 接触が 1 つ増え、二度と離されないまま積み残る。10 本ぶん
                // 溜まると新しい指が全部捨てられ、タッチが丸ごと効かなく
                // なっていた。端末を回すと releaseAllPointers で全消し
                // されるため、「回すと直るが、しばらくするとまた死ぬ」
                // という出かたをする。
                //
                // かといって、そのまま UP として注入してもいけない。
                // Windows は DOWN の無い UP を拒み、その拒否は
                // 「アクティブな全接触の取り消し」を伴う。巻き添えで
                // 触れている指まで壊れる。
                //
                // この知らせ自体は普通に届く。画面側の都合で接触を
                // まとめて解放したあと、利用者が指を離せばこれが来る。
                // 知らない指の知らせは、押した以外すべて捨てる。
                //
                // 離す知らせを Began に読み替えると接触が積み残る。
                // 動いた知らせを Began に読み替えると、離した直後に
                // もう一度押されてしまう。
                //
                // 端末は触れているあいだ 200ms ごとに現在の指を送る。
                // 離した直後、その心拍が 1 通あとから届くことがある。
                // これを「押した」と読むと、離したはずのペンが復活し、
                // 1.2 秒の時間切れまで押されたまま残る。実機では
                // 「離してちょっとしてから離れる。だから線が繋がる」
                // という形で出た。
                //
                // 本物のストロークは必ず Began から始まる。知らない指の
                // 続きは、取りこぼしではなく遅れて届いた残りとみなす。
                //
                // ただしホバーは別。触れる前の知らせなので、Began より
                // 先に来るのが当たり前で、「知らない指」なのが正常な姿。
                // ここで一緒に捨てていたため、浮かせたペンの位置が
                // 一度も届いていなかった。
                if (!wasActive && tp.Phase is not TouchPhase.Began
                                           and not TouchPhase.Hovered)
                    continue;

                // 触れている指がホバーになった＝画面から浮いた。
                //
                // ホバーは接触ではないので追跡から外す。ただ外すだけだと
                // Windows には UP を送らないまま忘れることになり、
                // 向こうではペンが触れたままになる。こちらは追跡を
                // やめているので 1.2 秒の時間切れも働かず、あとから
                // 届く「離した」も知らない指として捨てられる。
                // 永久に押されっぱなしになる。
                //
                // 先に前の位置で離してから、ホバーとして送り直す。
                if (wasActive && tp.Phase is TouchPhase.Hovered)
                {
                    frame.Add(new InjectedPointer(
                        Id:       (int)existing.NativeId,
                        PixelX:   existing.PixelX,
                        PixelY:   existing.PixelY,
                        Pressure: existing.Pressure,
                        Phase:    TouchPhase.Ended));

                    _activeContacts.Remove(tp.Id);
                    wasActive = false;
                }

                // 覚えが残っているのに「押した」が来た。
                //
                // 前の接触が離れないまま次が始まっている。前の位置で
                // いったん離してから、新しい位置で始める。こうしないと
                // 「離した位置から線が繋がる」ことになる。
                if (wasActive && tp.Phase is TouchPhase.Began)
                {
                    frame.Add(new InjectedPointer(
                        Id:       (int)existing.NativeId,
                        PixelX:   existing.PixelX,
                        PixelY:   existing.PixelY,
                        Pressure: existing.Pressure,
                        Phase:    TouchPhase.Ended));

                    _activeContacts.Remove(tp.Id);
                    wasActive = false;
                }

                var phase = NormalizePhase(tp.Phase, wasActive);

                if (phase is TouchPhase.Began && _activeContacts.Count >= MaxContacts)
                    continue; // 追跡上限を超えた新規接触は捨てる

                uint nativeId = wasActive ? existing.NativeId : AllocateNativeIdNoLock();

                // 指を離すときは、直前に送った位置をそのまま使う。
                //
                // Windows は POINTER_FLAG_UP のフレームに、直前の UPDATE と
                // 同じ ptPixelLocation を要求する。違う座標を送ると
                // ERROR_INVALID_PARAMETER になるだけでなく、
                // 「アクティブな全接触がキャンセルされる」ため、
                // 以降のタッチもまとめて壊れる。
                bool isRelease = phase is TouchPhase.Ended or TouchPhase.Cancelled;

                // ホバーは接触ではない。覚えておかない。
                //
                // 覚えると、ペンが画面から離れて通知が絶えたあとも
                // 「まだそこに居る」として送り続け、幻のカーソルが
                // 残る。ホバーは届いたその瞬間だけ位置を示せばよく、
                // 続いている間は次々に届く。
                bool isHover = phase is TouchPhase.Hovered;

                int sendX = (isRelease && wasActive) ? existing.PixelX : px;
                int sendY = (isRelease && wasActive) ? existing.PixelY : py;

                frame.Add(new InjectedPointer(
                    Id:       (int)nativeId,
                    PixelX:   sendX,
                    PixelY:   sendY,
                    Pressure: tp.Pressure,
                    Phase:    phase));

                if (isRelease || isHover)
                    _activeContacts.Remove(tp.Id);
                else
                    _activeContacts[tp.Id] = new ActiveContact(
                        nativeId, px, py, tp.Pressure, Environment.TickCount64);
            }

            // 今回報告されなかった接触中コンタクトを直前の位置で維持する
            foreach (var (id, contact) in _activeContacts)
            {
                if (reported.Contains(id))
                    continue;

                frame.Add(new InjectedPointer(
                    Id:       (int)contact.NativeId,
                    PixelX:   contact.PixelX,
                    PixelY:   contact.PixelY,
                    Pressure: contact.Pressure,
                    Phase:    TouchPhase.Moved));
            }
        }

        return frame;
    }

    /// <summary>
    /// 接触状態と受信フェーズの食い違いを補正する。
    /// Windows は DOWN のない UPDATE / UP を受け付けないため、
    /// パケットロスや再接続で欠けたフェーズをここで埋める。
    /// </summary>
    private static TouchPhase NormalizePhase(TouchPhase reported, bool wasActive) => reported switch
    {
        // Began を Moved に読み替えてはいけない。
        //
        // 「押した」を「動いた」にすると、こちらが覚えている前の位置から
        // 新しい位置まで線が引かれる。実機では「ペンを離して書き直すと、
        // 離した位置から繋がる」という形で出た。
        //
        // 覚えが残っているなら、それは前の接触が正しく離れていない。
        // 読み替えではなく、先に離してから始める（BuildFrame を参照）。
        // Moved を Began に読み替えない（上で捨てている）。

        // 知らない指の「離した」「取り消した」はここへ来ない。
        // フレームに載せる前に捨てている（BuildFrame を参照）。
        _ => reported
    };

    /// <summary>
    /// 未使用のネイティブポインター ID を割り当てる。
    /// 同時接触中のコンタクト同士で重複しなければよいので、
    /// 0〜<see cref="MaxContacts"/>-1 のスロットを使い回す。
    /// </summary>
    private uint AllocateNativeIdNoLock()
    {
        var inUse = new HashSet<uint>(_activeContacts.Values.Select(c => c.NativeId));

        for (uint slot = 0; slot < MaxContacts; slot++)
        {
            if (!inUse.Contains(slot))
                return slot;
        }

        return 0; // 上限に達している場合（呼び出し側で弾かれる想定）
    }

    /// <summary>
    /// 接触中のコンタクトをすべて UP として注入し、追跡状態を空にする。
    /// モード切り替えや破棄で指が押されっぱなしになるのを防ぐ。
    /// </summary>
    public void ReleaseAllContacts()
    {
        List<InjectedPointer> release;

        lock (_contactLock)
        {
            if (_activeContacts.Count == 0) return;

            release = _activeContacts.Values
                .Select(c => new InjectedPointer((int)c.NativeId, c.PixelX, c.PixelY, c.Pressure, TouchPhase.Ended))
                .ToList();

            _activeContacts.Clear();
        }

        try { _backend.InjectFrame(release); }
        catch (Exception) { /* 解放は best-effort */ }
    }

    // ── 座標変換 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 呼び出し元から渡された <see cref="DisplayTransform"/> が現在の状態と
    /// 異なる場合はその場で行列を計算し、一致していればキャッシュを使う。
    /// </summary>
    private Matrix3x2 ResolveMatrix(DisplayTransform transform)
    {
        lock (_transformLock)
        {
            if (transform.DisplayResolution != _currentResolution ||
                transform.Orientation != _currentOrientation)
            {
                return BuildTransformMatrix(transform.DisplayResolution, transform.Orientation);
            }

            return _transformMatrix;
        }
    }

    /// <summary>
    /// 正規化座標を、仮想デスクトップ基準のピクセル座標へ変換する。
    /// ディスプレイ範囲でクランプしたうえで原点オフセットを加える。
    /// </summary>
    private (int X, int Y) TransformToPixels(
        double normX, double normY, Matrix3x2 matrix, Resolution resolution)
    {
        var pixel = Vector2.Transform(new Vector2((float)normX, (float)normY), matrix);

        int x = (int)Math.Clamp(pixel.X, 0f, resolution.Width  - 1);
        int y = (int)Math.Clamp(pixel.Y, 0f, resolution.Height - 1);

        return (x + DisplayOriginX, y + DisplayOriginY);
    }

    /// <summary>
    /// 指定した正規化座標 (x, y) を現在の変換行列でピクセル座標に変換する。
    /// ディスプレイ原点オフセットは加えない（ディスプレイ内のローカル座標を返す）。
    /// </summary>
    /// <param name="normalizedX">スマホ画面上の正規化 X 座標 [0.0, 1.0]。</param>
    /// <param name="normalizedY">スマホ画面上の正規化 Y 座標 [0.0, 1.0]。</param>
    /// <returns>変換後のピクセル座標。</returns>
    public (int PixelX, int PixelY) TransformPoint(double normalizedX, double normalizedY)
    {
        Matrix3x2 matrix;
        Resolution resolution;
        lock (_transformLock)
        {
            matrix     = _transformMatrix;
            resolution = _currentResolution;
        }

        var pixel = Vector2.Transform(new Vector2((float)normalizedX, (float)normalizedY), matrix);

        return (
            PixelX: (int)Math.Clamp(pixel.X, 0f, resolution.Width  - 1),
            PixelY: (int)Math.Clamp(pixel.Y, 0f, resolution.Height - 1));
    }

    /// <summary>現在の内部変換行列を返す（テスト検証用）。</summary>
    public Matrix3x2 CurrentTransformMatrix
    {
        get { lock (_transformLock) return _transformMatrix; }
    }

    /// <summary>現在の仮想ディスプレイ解像度を返す（テスト検証用）。</summary>
    public Resolution CurrentResolution
    {
        get { lock (_transformLock) return _currentResolution; }
    }

    /// <summary>現在の画面向きを返す（テスト検証用）。</summary>
    public Orientation CurrentOrientation
    {
        get { lock (_transformLock) return _currentOrientation; }
    }

    /// <summary>
    /// 向きと解像度から座標変換行列を構築する。
    ///
    /// 変換式:
    /// <list type="bullet">
    ///   <item>Portrait:         (x, y) → (x * W,       y * H)</item>
    ///   <item>Landscape:        (x, y) → (y * W,       (1-x) * H)</item>
    ///   <item>PortraitFlipped:  (x, y) → ((1-x) * W,  (1-y) * H)</item>
    ///   <item>LandscapeFlipped: (x, y) → ((1-y) * W,  x * H)</item>
    /// </list>
    ///
    /// Matrix3x2 を使って上記アフィン変換を表現する。
    /// Vector2.Transform(v, m) = (m.M11*v.X + m.M21*v.Y + m.M31,
    ///                            m.M12*v.X + m.M22*v.Y + m.M32)
    /// </summary>
    private static Matrix3x2 BuildTransformMatrix(Resolution resolution, Orientation orientation)
    {
        float w = resolution.Width;
        float h = resolution.Height;

        return orientation switch
        {
            // Portrait: x' = x * W, y' = y * H
            Orientation.Portrait => new Matrix3x2(
                m11: w, m12: 0f,
                m21: 0f, m22: h,
                m31: 0f, m32: 0f),

            // Landscape: x' = y * W, y' = (1 - x) * H = -x * H + H
            Orientation.Landscape => new Matrix3x2(
                m11: 0f, m12: -h,
                m21: w, m22: 0f,
                m31: 0f, m32: h),

            // PortraitFlipped: x' = (1 - x) * W, y' = (1 - y) * H
            Orientation.PortraitFlipped => new Matrix3x2(
                m11: -w, m12: 0f,
                m21: 0f, m22: -h,
                m31: w, m32: h),

            // LandscapeFlipped: x' = (1 - y) * W, y' = x * H
            Orientation.LandscapeFlipped => new Matrix3x2(
                m11: 0f, m12: h,
                m21: -w, m22: 0f,
                m31: w, m32: 0f),

            _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "未知の向きです。")
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 送り直しを先に止める。残すと解放したあとに動く。
        _holdTimer?.Dispose();
        _holdTimer = null;

        ReleaseAllContacts();

        if (_ownsBackend)
            _backend.Dispose();
    }
}
