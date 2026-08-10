using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// vmonitor 仮想ディスプレイドライバへの制御チャンネル。
/// スマホの接続状態に合わせて、仮想モニターを出したり消したりする。
/// </summary>
/// <remarks>
/// <para>
/// 仮想モニターを常設にすると、スマホを繋いでいない間も Windows からは
/// ディスプレイが 1 枚多く見えたままになり、ウィンドウがそちらへ移動したり
/// マウスが画面外へ抜けたりする。実際に映せる相手がいるときだけ
/// 「接続」状態にするため、ドライバへ明示的に指示する。
/// </para>
/// <para>
/// 通信は名前付きパイプで行う。IddCx がデバイスのリクエスト振り分けを
/// 占有するためカスタム IOCTL が使えず（IddCxDeviceInitialize と
/// WdfDeviceConfigureRequestDispatching が互いに STATUS_WDF_BUSY で
/// 弾き合う）、UMDF には制御デバイスオブジェクトも無い。
/// 一方 UMDF ドライバはユーザーモードで動くので、通常の
/// プロセス間通信がそのまま使える。
/// </para>
/// <para>
/// 定義はドライバ側の <c>ControlServer.h</c> と対になっている。
/// 変更するときは両方を揃えること。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class VirtualDisplayControl : IDisposable
{
    /// <summary>ドライバが待ち受けているパイプ名。</summary>
    private const string PipeName = "vmonitor-control";

    /// <summary>ドライバへの接続を待つ上限（ミリ秒）。</summary>
    private const int ConnectTimeoutMs = 2000;

    // 操作コード（ドライバ側の VMonitorControl::Op と対応）
    private const uint OpConnect    = 1;
    private const uint OpDisconnect = 2;
    private const uint OpGetState   = 3;
    private const uint OpKeepAlive  = 4;

    private bool _disposed;

    /// <summary>
    /// ドライバへ「まだ生きている」ことを示すための、繋ぎっぱなしの接続。
    /// </summary>
    /// <remarks>
    /// 強制終了されると、こちらのコードは一切動かない。後始末を頼む
    /// 手立てが無いので、モニターが出たまま残る。
    /// この接続だけは OS が閉じてくれるため、ドライバはそれを見て
    /// 自分でモニターを外せる。
    /// </remarks>
    private NamedPipeClientStream? _keepAlive;

    /// <summary>直近の操作でドライバに届かなかった場合の理由。</summary>
    public string? LastError { get; private set; }

    private VirtualDisplayControl() { }

    /// <summary>
    /// ドライバへの制御チャンネルが使えるか確認して開く。
    /// ドライバが入っていない場合は null を返す（ミラーモードでは不要なため）。
    /// </summary>
    public static VirtualDisplayControl? TryOpen()
    {
        var control = new VirtualDisplayControl();

        // 実際に問い合わせてみて、応答があるときだけ有効とみなす
        var state = control.GetState();

        return state.Reachable ? control : null;
    }

    /// <summary>
    /// 仮想モニターを接続状態にする（スマホが繋がったときに呼ぶ）。
    /// </summary>
    public bool Connect(int width, int height, int refreshRate = 60)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var response = Send(OpConnect, width, height, refreshRate);

        if (response is not { Succeeded: not 0 })
            return false;

        OpenKeepAlive();

        // モニターが到着しただけでは画面数は増えない。
        // デスクトップの構成に「拡張」として組み込むところまで行う。
        ApplyExtendTopology();

        return true;
    }

    /// <summary>
    /// 仮想モニターを拡張デスクトップとして有効にする。
    /// </summary>
    /// <remarks>
    /// モニターの到着（IddCxMonitorArrival）と、それをデスクトップ構成へ
    /// 組み込むことは別の操作。到着だけでは Windows から見える画面数は
    /// 増えず、「ディスプレイ設定」に未使用のディスプレイが並ぶだけになる。
    /// </remarks>
    private static void ApplyExtendTopology()
    {
        try
        {
            new WindowsDisplayApiAdapter()
                .ApplyDisplayMode(VirtualDisplayHandle.NewHandle(), DisplayMode.Extend);
        }
        catch (Exception)
        {
            // 構成の適用に失敗してもモニター自体は繋がっている。
            // 利用者が「ディスプレイ設定」から手動で拡張することもできる。
        }
    }

    /// <summary>
    /// 仮想モニターを切断する（スマホが離れたときに呼ぶ）。
    /// </summary>
    public bool Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 見張りを先に畳む。残したままだと、ドライバ側が切断の直後に
        // 「持ち主が死んだ」と受け取って二重に外しにくる。
        CloseKeepAlive();

        var response = Send(OpDisconnect, 0, 0, 0);
        return response is { Succeeded: not 0 };
    }

    // ── 死活の見張り ─────────────────────────────────────────────────────

    /// <summary>ドライバへ繋ぎっぱなしの接続を 1 本張る。</summary>
    /// <remarks>
    /// 張れなくてもモニター自体は使える。強制終了への備えが無くなるだけ
    /// なので、失敗しても接続の成否には響かせない。
    /// </remarks>
    private void OpenKeepAlive()
    {
        CloseKeepAlive();

        try
        {
            var pipe = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.InOut, PipeOptions.None);

            pipe.Connect(ConnectTimeoutMs);
            pipe.ReadMode = PipeTransmissionMode.Message;

            pipe.Write(ToBytes(new Command
            {
                Operation      = OpKeepAlive,
                OwnerProcessId = (uint)Environment.ProcessId,
            }));
            pipe.Flush();

            // 引き取れたという返事を確かめてから握り続ける
            var buffer = new byte[Marshal.SizeOf<Response>()];

            if (pipe.Read(buffer, 0, buffer.Length) != buffer.Length)
            {
                pipe.Dispose();
                return;
            }

            _keepAlive = pipe;
        }
        catch (Exception)
        {
            _keepAlive = null;
        }
    }

    /// <summary>見張りの接続を閉じる。</summary>
    private void CloseKeepAlive()
    {
        var pipe = _keepAlive;
        _keepAlive = null;

        try { pipe?.Dispose(); } catch { /* 既に切れていることがある */ }
    }

    /// <summary>現在の接続状態を取得する。</summary>
    /// <returns>
    /// Reachable はドライバと通信できたかどうか。
    /// false の場合、他の値には意味がない。
    /// </returns>
    public (bool Reachable, bool Connected, int Width, int Height) GetState()
    {
        var response = Send(OpGetState, 0, 0, 0);

        return response is null
            ? (false, false, 0, 0)
            : (true, response.Value.Connected != 0, (int)response.Value.Width, (int)response.Value.Height);
    }

    // ── 通信 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// コマンドを 1 つ送って応答を受け取る。
    /// 接続は都度張り直す（常時つないでおく必要がない）。
    /// </summary>
    private Response? Send(uint operation, int width, int height, int refreshRate)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.InOut, PipeOptions.None);

            pipe.Connect(ConnectTimeoutMs);
            pipe.ReadMode = PipeTransmissionMode.Message;

            var command = new Command
            {
                Operation   = operation,
                Width       = (uint)Math.Max(0, width),
                Height      = (uint)Math.Max(0, height),
                RefreshRate = (uint)Math.Max(0, refreshRate),

                // 強制終了されたときにモニターを外してもらうため、
                // 自分の ID を渡しておく。
                OwnerProcessId = (uint)Environment.ProcessId,
            };

            pipe.Write(ToBytes(command));
            pipe.Flush();

            var buffer = new byte[Marshal.SizeOf<Response>()];
            int read = pipe.Read(buffer, 0, buffer.Length);

            if (read != buffer.Length)
            {
                LastError = $"応答が短すぎます ({read} バイト)";
                return null;
            }

            LastError = null;
            return FromBytes<Response>(buffer);
        }
        catch (TimeoutException)
        {
            LastError = "ドライバが応答しません（仮想ディスプレイドライバが未導入か停止しています）";
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static byte[] ToBytes<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var bytes = new byte[size];

        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, fDeleteOld: false);
            Marshal.Copy(ptr, bytes, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return bytes;
    }

    private static T FromBytes<T>(byte[] bytes) where T : struct
    {
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return Marshal.PtrToStructure<T>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        // 接続したままアプリが終わると、モニターが出たまま残ってしまう。
        //
        // 印を立てるのは切断を済ませてから。先に立てると Disconnect が
        // 自分で ObjectDisposedException を投げて即座に抜けてしまい、
        // ここは何もしないまま終わる。実際それでモニターが残っていた。
        try { Disconnect(); } catch { /* 後始末は best-effort */ }

        _disposed = true;

        CloseKeepAlive();
    }

    // ── ドライバと共有する構造体 ─────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Command
    {
        public uint Operation;
        public uint Width;
        public uint Height;
        public uint RefreshRate;

        /// <summary>
        /// この接続を頼んでいるプロセスの ID。
        /// </summary>
        /// <remarks>
        /// ドライバはこれを見て持ち主の終了を待つ。強制終了された場合は
        /// こちらのコードが一切動かないため、後始末を頼めない。
        /// 見張ってもらわないとモニターが出たまま残る。
        /// </remarks>
        public uint OwnerProcessId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Response
    {
        public uint Succeeded;
        public uint Connected;
        public uint Width;
        public uint Height;
    }
}
