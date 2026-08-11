using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VMonitor.Core.Models;
using static VMonitor.Session.Input.PointerInjectionNative;

namespace VMonitor.Session.Input;

/// <summary>
/// Win32 のポインター注入 API を実際に呼び出すバックエンド。
/// タッチは <c>InjectTouchInput</c>、ペン (Windows Ink) は
/// <c>InjectSyntheticPointerInput</c> を使う。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32PointerInjectionBackend : IPointerInjectionBackend
{
    private readonly object _lock = new();

    private PointerInjectionMode _mode = PointerInjectionMode.Touch;
    private int  _maxContacts;
    private bool _touchInitialized;
    private IntPtr _penDevice = IntPtr.Zero;
    private bool _disposed;

    /// <summary>直近の注入で失敗した場合の Win32 エラーコード（0 は成功）。</summary>
    public int LastError { get; private set; }

    /// <inheritdoc/>
    public bool SupportsPen => true;

    /// <inheritdoc/>
    public bool Initialize(PointerInjectionMode mode, int maxContacts)
    {
        if (maxContacts < 1) maxContacts = 1;
        if (maxContacts > (int)MaxContacts) maxContacts = (int)MaxContacts;

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // モードが変わった場合は前のデバイスを解放してから作り直す
            if (_mode != mode)
            {
                ReleasePenDeviceNoLock();
                _mode = mode;
            }

            _maxContacts = maxContacts;

            return mode switch
            {
                PointerInjectionMode.Pen   => InitializePenNoLock(maxContacts),
                PointerInjectionMode.Touch => InitializeTouchNoLock(maxContacts),
                _ => false
            };
        }
    }

    /// <inheritdoc/>
    public bool InjectFrame(IReadOnlyList<InjectedPointer> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Count == 0) return true;

        lock (_lock)
        {
            if (_disposed) return false;

            // Initialize が呼ばれていない場合はここで補う
            if (_mode == PointerInjectionMode.Pen ? _penDevice == IntPtr.Zero : !_touchInitialized)
            {
                if (!Initialize_NoLockReentrant(_mode, Math.Max(_maxContacts, frame.Count)))
                    return false;
            }

            return _mode == PointerInjectionMode.Pen
                ? InjectPenNoLock(frame)
                : InjectTouchNoLock(frame);
        }
    }

    // ── 初期化 ───────────────────────────────────────────────────────────

    /// <summary>ロック保持中に呼ぶ Initialize 相当の処理。</summary>
    private bool Initialize_NoLockReentrant(PointerInjectionMode mode, int maxContacts)
    {
        if (maxContacts < 1) maxContacts = 1;
        if (maxContacts > (int)MaxContacts) maxContacts = (int)MaxContacts;
        _maxContacts = maxContacts;

        return mode == PointerInjectionMode.Pen
            ? InitializePenNoLock(maxContacts)
            : InitializeTouchNoLock(maxContacts);
    }

    private bool InitializeTouchNoLock(int maxContacts)
    {
        if (_touchInitialized) return true;

        // InitializeTouchInjection はプロセスにつき 1 回だけ有効。
        // TOUCH_FEEDBACK_DEFAULT で OS 標準のタッチ視覚フィードバックを出す。
        if (!InitializeTouchInjection((uint)maxContacts, POINTER_FEEDBACK_DEFAULT))
        {
            LastError = Marshal.GetLastWin32Error();

            // ERROR_ALREADY_INITIALIZED (1247) など、既に初期化済みの場合は成功扱いにする
            const int ERROR_ALREADY_INITIALIZED = 1247;
            if (LastError == ERROR_ALREADY_INITIALIZED)
            {
                _touchInitialized = true;
                LastError = 0;
                return true;
            }

            return false;
        }

        LastError = 0;
        _touchInitialized = true;
        return true;
    }

    private bool InitializePenNoLock(int maxContacts)
    {
        if (_penDevice != IntPtr.Zero) return true;

        // ペンは同時に 1 本しか存在しないため maxCount は 1 に丸める
        _ = maxContacts;

        IntPtr device;

        try
        {
            device = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_DEFAULT);
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 10 1809 より前には、この関数が user32.dll に無い。
            //
            // 呼ぶと戻り値ではなく例外で失敗するため、ここで受けないと
            // 「ペンは使えません」ではなく異常終了になる。タッチのほうは
            // Windows 8 から在るので、ペンだけ諦めれば操作は成立する。
            LastError = 0;
            return false;
        }
        catch (DllNotFoundException)
        {
            LastError = 0;
            return false;
        }

        if (device == IntPtr.Zero)
        {
            LastError = Marshal.GetLastWin32Error();
            return false;
        }

        LastError = 0;
        _penDevice = device;
        return true;
    }

    // ── 注入 ─────────────────────────────────────────────────────────────

    private bool InjectTouchNoLock(IReadOnlyList<InjectedPointer> frame)
    {
        int count = Math.Min(frame.Count, _maxContacts);
        var contacts = new POINTER_TOUCH_INFO[count];

        for (int i = 0; i < count; i++)
            contacts[i] = BuildTouchInfo(frame[i]);

        if (!InjectTouchInput((uint)count, contacts))
        {
            LastError = Marshal.GetLastWin32Error();
            return false;
        }

        LastError = 0;
        return true;
    }

    private bool InjectPenNoLock(IReadOnlyList<InjectedPointer> frame)
    {
        // ペンは単一ポインターのため、フレーム先頭の 1 点のみを注入する
        var point = frame[0];

        var info = new POINTER_TYPE_INFO
        {
            type = PT_PEN,
            penInfo = new POINTER_PEN_INFO
            {
                pointerInfo = BuildPointerInfo(point, PT_PEN),
                penFlags    = PEN_FLAG_NONE,
                penMask     = PEN_MASK_PRESSURE,
                pressure    = ToNativePressure(point.Pressure, PenPressureMax),
                rotation    = 0,
                tiltX       = 0,
                tiltY       = 0,
            }
        };

        var buffer = new[] { info };

        if (!InjectSyntheticPointerInput(_penDevice, buffer, 1))
        {
            LastError = Marshal.GetLastWin32Error();
            return false;
        }

        LastError = 0;
        return true;
    }

    // ── 構造体の組み立て ─────────────────────────────────────────────────

    private static POINTER_TOUCH_INFO BuildTouchInfo(InjectedPointer p)
    {
        // 埋めるのは必須フィールドだけにする。
        //
        // touchMask はどのオプションフィールドが有効かを示す。ここで
        // CONTACTAREA / ORIENTATION / PRESSURE を立てて値を入れると、
        // InjectTouchInput は ERROR_INVALID_PARAMETER (87) で拒否した。
        // touchMask を 0 のままにして接触面積・向き・筆圧を OS に任せると通る。
        //
        // 実際に必要なのは pointerType・pointerId・ptPixelLocation・pointerFlags の
        // 4 つだけで、これは動作実績のある実装とも一致する。
        return new POINTER_TOUCH_INFO
        {
            pointerInfo = BuildPointerInfo(p, PT_TOUCH),
            touchFlags  = TOUCH_FLAG_NONE,
            touchMask   = TOUCH_MASK_NONE,
        };
    }

    private static POINTER_INFO BuildPointerInfo(InjectedPointer p, uint pointerType)
    {
        return new POINTER_INFO
        {
            pointerType     = pointerType,
            pointerId       = (uint)p.Id,
            pointerFlags    = ToPointerFlags(p.Phase),
            ptPixelLocation = new POINT { X = p.PixelX, Y = p.PixelY },
            // 残りのフィールドは 0 のままで OS が補完する
        };
    }

    /// <summary>
    /// タッチのライフサイクルフェーズを POINTER_FLAGS に対応付ける。
    /// </summary>
    /// <remarks>
    /// 接触の開始は DOWN、継続は UPDATE、終了は UP を立てる。
    /// DOWN と UPDATE では「画面に触れている」ことを示す
    /// INRANGE | INCONTACT が必要で、UP では両方を落とす。
    /// キャンセルは UP に CANCELED を添えて、アプリ側がジェスチャーを
    /// 確定させずに破棄できるようにする。
    /// </remarks>
    private static uint ToPointerFlags(TouchPhase phase) => phase switch
    {
        TouchPhase.Began =>
            POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT,

        TouchPhase.Moved =>
            POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT,

        TouchPhase.Ended =>
            POINTER_FLAG_UP,

        TouchPhase.Cancelled =>
            POINTER_FLAG_UP | POINTER_FLAG_CANCELED,

        // 近づいているが触れていない。
        //
        // INRANGE だけを立て、INCONTACT は落とす。この組み合わせで
        // Windows は「ペンが上にある」と解釈し、触れる前から位置を
        // 示す丸を出す。INCONTACT まで立てると線を引いてしまう。
        TouchPhase.Hovered =>
            POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE,

        _ => POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT
    };

    /// <summary>正規化筆圧 [0.0, 1.0] を Windows の 1〜1024 に変換する。</summary>
    internal static uint ToNativePressure(double pressure)
        => ToNativePressure(pressure, PenPressureMax);

    /// <summary>
    /// 正規化筆圧 [0.0, 1.0] を指定した尺度へ変換する。
    /// タッチとペンでは有効範囲が異なる。
    /// </summary>
    internal static uint ToNativePressure(double pressure, uint maxPressure)
    {
        if (double.IsNaN(pressure)) return maxPressure;

        double clamped = Math.Clamp(pressure, 0.0, 1.0);
        uint value = (uint)Math.Round(clamped * maxPressure);

        // 0 は「筆圧情報なし」と解釈されるため下限を 1 にする
        return Math.Clamp(value, 1, maxPressure);
    }

    // ── 後始末 ───────────────────────────────────────────────────────────

    private void ReleasePenDeviceNoLock()
    {
        if (_penDevice == IntPtr.Zero) return;

        try { DestroySyntheticPointerDevice(_penDevice); }
        catch (DllNotFoundException) { /* 古い Windows では存在しない */ }
        catch (EntryPointNotFoundException) { /* Windows 10 1809 未満 */ }

        _penDevice = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            ReleasePenDeviceNoLock();
        }
    }
}
