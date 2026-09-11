using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace VMonitor.UI;

/// <summary>
/// usbmuxd の iproxy を使い、Windows のループバックポートを iOS 端末へ転送する。
/// </summary>
/// <remarks>
/// iproxy は Apple Mobile Device Support が提供する usbmuxd と通信する。
/// 配布版は iproxy を tools/ios-usb に同梱する。開発環境ではアプリの隣や
/// PATH にあるものも利用できる。
/// </remarks>
public sealed class IosUsbTunnel : IAsyncDisposable
{
    public const int DevicePort = ConnectionServer.DevicePort;

    private Process? _process;

    /// <summary>見つかった iproxy.exe の絶対パス。無ければ null。</summary>
    public static string? FindIproxy(
        string? appDirectory = null,
        string? path = null,
        bool searchUserTools = true)
    {
        appDirectory ??= AppContext.BaseDirectory;

        var besideApp = Path.Combine(appDirectory, "iproxy.exe");
        if (File.Exists(besideApp)) return Path.GetFullPath(besideApp);

        var toolsDirectory = Path.Combine(appDirectory, "tools", "ios-usb", "iproxy.exe");
        if (File.Exists(toolsDirectory)) return Path.GetFullPath(toolsDirectory);

        if (searchUserTools)
        {
            var userToolsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "vmonitor", "tools", "ios-usb", "iproxy.exe");
            if (File.Exists(userToolsDirectory)) return Path.GetFullPath(userToolsDirectory);
        }

        path ??= Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string directory = entry.Trim().Trim('"');
            if (directory.Length == 0) continue;

            try
            {
                var candidate = Path.Combine(directory, "iproxy.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch
            {
                // 壊れた PATH の一項目で探索全体を止めない。
            }
        }

        return null;
    }

    public static bool IsAvailable => FindIproxy() is not null;

    /// <summary>
    /// 空いているローカルポートを選び、iPhone の待受ポートへ転送する。
    /// </summary>
    /// <param name="udid">対象端末の UDID。null なら最初の USB 端末。</param>
    public async Task<int> StartAsync(string? udid, CancellationToken ct)
    {
        if (_process is not null)
            throw new InvalidOperationException("iOS USB トンネルは既に動作しています。");

        string executable = FindIproxy()
            ?? throw new FileNotFoundException(
                "iproxy.exe が見つかりません。libusbmuxd の iproxy を vmonitor.exe と同じフォルダーに置いてください。");

        int localPort = ReserveLoopbackPort();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        // iproxy の既定も localhost だが、将来の既定変更でもLANへ露出しないよう明示する。
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add(IPAddress.Loopback.ToString());
        startInfo.ArgumentList.Add("--local");

        if (!string.IsNullOrWhiteSpace(udid))
        {
            startInfo.ArgumentList.Add("--udid");
            startInfo.ArgumentList.Add(udid.Trim());
        }

        startInfo.ArgumentList.Add($"{localPort}:{DevicePort}");

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("iproxy.exe を起動できませんでした。");

        try
        {
            await WaitUntilStartedAsync(ct);
            return localPort;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task WaitUntilStartedAsync(CancellationToken ct)
    {
        // 待受確認のために接続すると、その接続がiOS側の「最初の1台」として
        // 消費される。短時間だけ起動直後の終了を監視し、実接続は呼び出し元が行う。
        await Task.Delay(200, ct);

        var process = _process ?? throw new InvalidOperationException("iproxy.exe が停止しました。");
        if (!process.HasExited) return;

        string error = (await process.StandardError.ReadToEndAsync()).Trim();
        throw new InvalidOperationException(
            error.Length == 0
                ? $"iproxy.exe が終了しました（終了コード {process.ExitCode}）。"
                : $"iproxy.exe: {error}");
    }

    private static int ReserveLoopbackPort()
    {
        // iproxy はポート 0 を受け付けない。短時間だけOSに選ばせ、直後に起動する。
        // 解放から iproxy の bind まで競合する可能性は小さいが、失敗時は画面へ理由を返す。
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public ValueTask DisposeAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return ValueTask.CompletedTask;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
            // 終了済み・競合・OS終了中の失敗は後始末なので表へ出さない。
        }
        finally
        {
            process.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
