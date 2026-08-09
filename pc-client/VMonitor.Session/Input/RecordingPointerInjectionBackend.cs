namespace VMonitor.Session.Input;

/// <summary>
/// 実際には注入せず、注入されるはずだったフレームを記録するバックエンド。
/// テストと、ポインター注入 API が使えない環境でのフォールバックに使う。
/// </summary>
/// <remarks>
/// テストで実バックエンドを使うと、テスト実行中に本物のタッチ入力が
/// デスクトップへ注入されてしまう。注入内容の検証はこのバックエンドで行う。
/// </remarks>
public sealed class RecordingPointerInjectionBackend : IPointerInjectionBackend
{
    private readonly object _lock = new();
    private readonly List<IReadOnlyList<InjectedPointer>> _frames = new();

    /// <inheritdoc/>
    public bool SupportsPen => true;

    /// <summary><see cref="Initialize"/> が呼ばれた回数。</summary>
    public int InitializeCallCount { get; private set; }

    /// <summary>直近に初期化されたモード。</summary>
    public PointerInjectionMode Mode { get; private set; } = PointerInjectionMode.Touch;

    /// <summary>これまでに注入されたフレームの記録。</summary>
    public IReadOnlyList<IReadOnlyList<InjectedPointer>> Frames
    {
        get { lock (_lock) return _frames.ToList().AsReadOnly(); }
    }

    /// <summary>最後に注入されたフレーム。1 度も注入されていなければ null。</summary>
    public IReadOnlyList<InjectedPointer>? LastFrame
    {
        get { lock (_lock) return _frames.Count > 0 ? _frames[^1] : null; }
    }

    /// <summary>これまでに注入されたポインターを時系列順に平坦化して返す。</summary>
    public IReadOnlyList<InjectedPointer> AllPointers
    {
        get { lock (_lock) return _frames.SelectMany(f => f).ToList().AsReadOnly(); }
    }

    /// <summary>記録を消去する。</summary>
    public void Clear()
    {
        lock (_lock) _frames.Clear();
    }

    /// <inheritdoc/>
    public bool Initialize(PointerInjectionMode mode, int maxContacts)
    {
        lock (_lock)
        {
            InitializeCallCount++;
            Mode = mode;
        }
        return true;
    }

    /// <inheritdoc/>
    public bool InjectFrame(IReadOnlyList<InjectedPointer> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_lock) _frames.Add(frame.ToList().AsReadOnly());
        return true;
    }

    public void Dispose() { }
}
