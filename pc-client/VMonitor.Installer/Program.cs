using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace VMonitor.Installer;

/// <summary>
/// vmonitor セットアップ — インストール / アンインストール / サイレントモード対応
///
/// 使い方:
///   VMonitorSetup.exe                   — GUI インストーラー起動
///   VMonitorSetup.exe /silent           — サイレントインストール
///   VMonitorSetup.exe /uninstall        — アンインストール
///   VMonitorSetup.exe /uninstall /silent — サイレントアンインストール
/// </summary>
internal static class Program
{
    private const string AppName        = "vmonitor";
    private const string AppVersion     = "1.0.0";
    private const string Publisher      = "vmonitor Project";
    private const string InstallDirName = "vmonitor";

    // レジストリ: Programs and Features (Add/Remove Programs) への登録キー
    private const string UninstallRegKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\vmonitor";

    [STAThread]
    static int Main(string[] args)
    {
        bool silent     = args.Contains("/silent",     StringComparer.OrdinalIgnoreCase);
        bool uninstall  = args.Contains("/uninstall",  StringComparer.OrdinalIgnoreCase);
        bool driverOnly = args.Contains("/driver-only", StringComparer.OrdinalIgnoreCase);

        // 管理者権限チェック
        if (!IsRunningAsAdministrator())
        {
            if (!silent)
                ShowError("vmonitor セットアップには管理者権限が必要です。\n右クリック →「管理者として実行」でもう一度起動してください。");
            return 1;
        }

        // 想定外の例外でそのまま落とさない。
        //
        // ここはインストーラーから runhidden で呼ばれる。落ちても画面には
        // 何も出ず、終了コードも見られていなかったため、
        // 「インストールは成功したのにドライバだけ入っていない」という
        // 分かりにくい結果になっていた。理由を残して 0 以外で返す。
        try
        {
            if (uninstall) return RunUninstall(silent);
            if (driverOnly) return RunDriverOnly();

            return RunInstall(silent);
        }
        catch (Exception ex)
        {
            ShowError($"処理中に予期しないエラーが発生しました。\n{ex}");
            WriteFailureLog(ex);
            return 1;
        }
    }

    /// <summary>
    /// 失敗の記録を残す。
    /// </summary>
    /// <remarks>
    /// 画面に出せない状況（runhidden で呼ばれた場合）でも
    /// あとから理由を追えるようにする。
    /// </remarks>
    private static void WriteFailureLog(Exception ex)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "vmonitor", "setup-error.log");

            var directory = Path.GetDirectoryName(path);
            if (directory is not null) Directory.CreateDirectory(directory);

            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // 記録できなくても、終了コードで失敗は伝わる
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // ドライバのみの再実行
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 既にインストール済みの環境で、ドライバの導入だけをやり直す。
    /// </summary>
    /// <remarks>
    /// ドライバの導入は環境差が出やすく、失敗したときに切り分けたくなる。
    /// そのたびに Program Files を書き直す必要はないので、
    /// ドライバ部分だけを再実行できるようにしておく。
    /// </remarks>
    private static int RunDriverOnly()
    {
        // この setup.exe に同梱されているドライバを使う。
        //
        // インストール済みフォルダにあるドライバは前回の実行時にコピーされたもので、
        // 修正版を持ってきても古いままになる。ドライバを入れ直す目的で実行しているのに
        // 古いファイルを掴んでは意味がないので、同梱物のほうを正とする。
        var bundledDriverDir = Path.Combine(AppContext.BaseDirectory, "driver");
        var installDir       = GetInstalledDir();

        Console.WriteLine("=== vmonitor ドライバ再インストール ===");

        string sourceDir;

        if (File.Exists(Path.Combine(bundledDriverDir, "VMonitorVDD.inf")))
        {
            sourceDir = AppContext.BaseDirectory;
            Console.WriteLine($"ドライバ: {bundledDriverDir}（同梱）");

            // インストール済みフォルダにも反映して、次回以降ズレないようにする。
            //
            // ただし、この exe 自身がインストール先から動いている場合は
            // コピー元とコピー先が同じになる。インストーラーはまさにその形で
            // {app}\VMonitorSetup.exe を呼ぶため、自分自身へのコピーになって
            // 「別のプロセスが使用中」で落ちていた。
            // 落ちる場所がドライバ導入の手前なので、インストールしても
            // ドライバだけ入らない状態になっていた。
            if (installDir != null && Directory.Exists(installDir))
            {
                var target = Path.Combine(installDir, "driver");

                if (IsSameDirectory(bundledDriverDir, target))
                {
                    Console.WriteLine("同梱ドライバはインストール先と同じ場所です（コピー不要）。");
                }
                else
                {
                    CopyDirectory(bundledDriverDir, target);
                    Console.WriteLine($"インストール先のドライバも更新しました: {target}");
                }
            }
        }
        else if (installDir != null)
        {
            sourceDir = installDir;
            Console.WriteLine($"ドライバ: {Path.Combine(installDir, "driver")}（インストール済み）");
        }
        else
        {
            ShowError("ドライバファイルが見つかりません。");
            return 1;
        }

        Console.WriteLine();

        var result = InstallDriver(sourceDir);

        if (result.Success)
        {
            Console.WriteLine();
            Console.WriteLine("✓ ドライバのインストールが完了しました。");
            Console.WriteLine("  Windows の「ディスプレイ設定」に仮想ディスプレイが追加されているか確認してください。");
            return 0;
        }

        ShowError(result.ErrorMessage);
        return 1;
    }

    // ─────────────────────────────────────────────────────────────────────
    // インストール
    // ─────────────────────────────────────────────────────────────────────

    private static int RunInstall(bool silent)
    {
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            InstallDirName);

        if (!silent)
        {
            Console.WriteLine("=== vmonitor セットアップ ===");
            Console.WriteLine($"インストール先: {installDir}");
            Console.WriteLine("インストールを開始しますか？ [Y/n]");
            var key = Console.ReadLine()?.Trim().ToUpperInvariant();
            if (key == "N") { Console.WriteLine("キャンセルしました。"); return 0; }
        }

        Log("インストール開始...");

        try
        {
            // 1. インストールディレクトリ作成
            Log("ディレクトリを作成しています...");
            Directory.CreateDirectory(installDir);

            // 2. アプリケーションファイルをコピー
            Log("アプリケーションファイルをコピーしています...");
            CopyPayloadFiles(installDir);

            // 3. VMonitorVDD ドライバのインストール
            Log("仮想ディスプレイドライバをインストールしています...");
            var driverResult = InstallDriver(installDir);
            if (!driverResult.Success)
            {
                var msg = $"ドライバのインストールに失敗しました。\n{driverResult.ErrorMessage}\n\n" +
                          "拡張モード（スマホを 2 枚目のディスプレイにする）は使えませんが、\n" +
                          "ミラーモード（PC 画面をスマホに映す）はこのまま利用できます。";
                if (!silent) ShowError(msg);
                else Log($"ERROR: {msg}");
                // ドライバ失敗はクリティカルエラーとするがファイルコピーは完了しているため続行
            }

            // 4. ファイアウォール規則の登録
            Log("ファイアウォール規則を登録しています...");
            ConfigureFirewall(installDir);

            // 5. スタートメニューショートカット作成
            Log("スタートメニューショートカットを作成しています...");
            CreateStartMenuShortcut(installDir);

            // 6. デスクトップショートカット作成
            Log("デスクトップショートカットを作成しています...");
            CreateDesktopShortcut(installDir);

            // 7. レジストリ登録 (Programs and Features)
            Log("レジストリに登録しています...");
            RegisterUninstallEntry(installDir);

            Log("インストールが完了しました。");
            if (!silent)
            {
                Console.WriteLine("\n✓ vmonitor のインストールが完了しました。");
                Console.WriteLine("スタートメニューまたはデスクトップのショートカットから起動できます。");
                Console.WriteLine("任意のキーを押して終了...");
                Console.ReadKey();
            }

            return 0;
        }
        catch (Exception ex)
        {
            var msg = $"インストール中にエラーが発生しました:\n{ex.Message}";
            if (!silent) ShowError(msg);
            else Log($"FATAL: {msg}");
            return 1;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // アンインストール
    // ─────────────────────────────────────────────────────────────────────

    private static int RunUninstall(bool silent)
    {
        var installDir = GetInstalledDir();

        if (!silent)
        {
            Console.WriteLine("=== vmonitor アンインストール ===");
            if (installDir != null)
                Console.WriteLine($"インストール先: {installDir}");
            Console.WriteLine("vmonitor をアンインストールしますか？ [Y/n]");
            var key = Console.ReadLine()?.Trim().ToUpperInvariant();
            if (key == "N") { Console.WriteLine("キャンセルしました。"); return 0; }
        }

        Log("アンインストール開始...");

        try
        {
            // 1. vmonitor プロセスを停止する
            Log("vmonitor を終了しています...");
            StopVMonitorProcess();

            // 2. VMonitorVDD ドライバのアンインストール
            Log("仮想ディスプレイドライバをアンインストールしています...");
            UninstallDriver(installDir);

            // 3. ファイアウォール規則の削除
            Log("ファイアウォール規則を削除しています...");
            RemoveFirewallRules();

            // 4. ショートカット削除
            Log("ショートカットを削除しています...");
            RemoveShortcuts();

            // 4. インストールディレクトリ削除
            //
            // ただし、同じフォルダを別の登録（Inno Setup 側）も使っている場合は
            // 消さない。消すと、そちらから見れば「入れたはずのファイルが
            // 勝手に消えた」ことになる。
            //
            // 実際にこれが起きた。追加と削除に vmonitor の登録が 2 つ並び、
            // 片方を消した結果、アプリ本体もドライバも証明書も失われた。
            if (installDir != null && Directory.Exists(installDir))
            {
                if (IsDirectoryOwnedByAnotherInstaller(installDir))
                {
                    Log("インストール先は別の登録でも使われているため残します: " + installDir);
                    Log("完全に削除するには、そちらのアンインストールを実行してください。");
                }
                else
                {
                    Log("アプリケーションファイルを削除しています...");
                    Directory.Delete(installDir, recursive: true);
                }
            }

            // 5. レジストリキー削除
            Log("レジストリを削除しています...");
            Registry.LocalMachine.DeleteSubKey(UninstallRegKey, throwOnMissingSubKey: false);

            Log("アンインストールが完了しました。");
            if (!silent)
            {
                Console.WriteLine("\n✓ vmonitor のアンインストールが完了しました。");
                Console.WriteLine("任意のキーを押して終了...");
                Console.ReadKey();
            }

            return 0;
        }
        catch (Exception ex)
        {
            var msg = $"アンインストール中にエラーが発生しました:\n{ex.Message}";
            if (!silent) ShowError(msg);
            else Log($"FATAL: {msg}");
            return 1;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // ドライバ インストール / アンインストール
    // ─────────────────────────────────────────────────────────────────────

    private record DriverResult(bool Success, string ErrorMessage = "");

    /// <summary>
    /// pnputil /add-driver で VMonitorVDD.inf を Driver Store に登録し、
    /// デバイスノード Root\VMonitorVDD を作成してドライバを読み込む。
    /// テスト署名環境では証明書を先にインポートする。
    /// </summary>
    private static DriverResult InstallDriver(string installDir)
    {
        var driverDir = Path.Combine(installDir, "driver");
        var infPath   = Path.Combine(driverDir, "VMonitorVDD.inf");
        var catPath   = Path.Combine(driverDir, "vmonitorvdd.cat");
        var cerPath   = Path.Combine(driverDir, "MyTestCert.cer");

        if (!File.Exists(infPath))
            return new DriverResult(false, $"ドライバファイルが見つかりません: {infPath}");

        // Step 1: 署名に使った証明書をこの PC の信頼ストアに取り込む
        //
        // VMonitorVDD は UMDF (ユーザーモード) ドライバで、WUDFHost.exe 上で動く。
        // カーネルには読み込まれないため、テスト署名モードやセキュアブートが
        // 制御しているカーネルモードの署名強制 (DSE) の対象外になる。
        //
        // 導入に必要なのは「カタログの署名者をこの PC が信頼していること」だけなので、
        // 証明書を信頼されたルートと信頼された発行元に入れれば条件を満たせる。
        // セキュアブートを無効にする必要はない。
        if (!File.Exists(cerPath))
        {
            return new DriverResult(false,
                $"署名証明書が見つかりません: {cerPath}\n" +
                "driver\\build-and-sign.ps1 を実行してドライバ一式を生成してください。");
        }

        Log("署名証明書を信頼ストアに取り込んでいます...");

        var certRoot = RunProcess("certutil.exe", $"-addstore -f Root \"{cerPath}\"");
        if (certRoot.ExitCode != 0)
            return new DriverResult(false,
                $"証明書を信頼されたルートに取り込めませんでした。\n{certRoot.StdOut}\n{certRoot.StdErr}");

        var certPublisher = RunProcess("certutil.exe", $"-addstore -f TrustedPublisher \"{cerPath}\"");
        if (certPublisher.ExitCode != 0)
            return new DriverResult(false,
                $"証明書を信頼された発行元に取り込めませんでした。\n{certPublisher.StdOut}\n{certPublisher.StdErr}");

        Log("証明書の取り込みが完了しました。");

        // Step 2: 古い vmonitor ドライバパッケージを DriverStore から取り除く
        //
        // 同じハードウェア ID に対する候補が複数あると、Windows は
        // その中から一つを選ぶ。修正版を追加しても古い方が選ばれ続けることがあり、
        // 直したはずの不具合がそのまま再現して原因が分からなくなる。
        RemoveOldDriverPackages();

        // Step 3: DriverStore に追加してデバイスをインストール
        Log("DriverStore にドライバを追加しています...");
        var addResult = RunProcess("pnputil.exe",
            $"/add-driver \"{infPath}\" /install");

        // pnputil の終了コード 3010 は「再起動が必要」を意味する（成功扱い）
        if (addResult.ExitCode == 0 || addResult.ExitCode == 3010)
        {
            Log($"DriverStore への登録完了: {addResult.StdOut.Trim()}");

            // Step 3: 仮想ディスプレイのデバイスノードを作る
            //
            // 対応する物理ハードウェアが無いので、誰かが明示的に
            // ルート列挙デバイスを作らないとドライバは読み込まれない。
            // pnputil の /install は既存デバイスにドライバを当てるだけで、
            // ノード自体は作ってくれない。
            Log("仮想ディスプレイのデバイスを作成しています...");

            var displayClass = new Guid("4D36E968-E325-11CE-BFC1-08002BE10318");
            var error = RootDeviceInstaller.CreateDevice(
                "Root\\VMonitorVDD", infPath, displayClass, out bool rebootRequired);

            if (error != null)
            {
                return new DriverResult(false,
                    $"デバイスの作成に失敗しました。\n{error}\n\n" +
                    "ドライバ自体は DriverStore に登録されています。\n" +
                    "%SystemRoot%\\INF\\setupapi.dev.log に詳しい失敗理由が記録されています。");
            }

            Log(rebootRequired
                ? "仮想ディスプレイを作成しました（反映には再起動が必要です）。"
                : "仮想ディスプレイを作成しました。");

            // Step 4: USB 直結 (AOA) 用のドライバを入れる
            InstallAoaDriver(driverDir);

            return new DriverResult(true);
        }

        return new DriverResult(false,
            $"pnputil /add-driver 失敗 (終了コード: {addResult.ExitCode})\n" +
            $"{addResult.StdOut}\n{addResult.StdErr}\n\n" +
            "確認する点:\n" +
            "  1. 証明書が Root と TrustedPublisher の両方に入っているか\n" +
            "     certutil -store Root  |  findstr vmonitor\n" +
            "  2. カタログが署名済みか（driver\\build-and-sign.ps1 を再実行）\n" +
            "  3. %SystemRoot%\\INF\\setupapi.dev.log に詳しい失敗理由が記録されています\n\n" +
            "それでも署名が拒否される場合に限り、テスト署名モードが必要になります。\n" +
            "その場合はセキュアブートを無効にしたうえで次を実行してください:\n" +
            "  bcdedit /set testsigning on   （実行後に再起動）");
    }

    /// <summary>
    /// USB 直結 (AOA) 用のドライバを DriverStore に登録する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 端末をアクセサリーモードへ切り替えると、Windows からは新しい USB デバイスとして
    /// 見え直す。このとき現れる「Android Accessory Interface」には Windows 標準の
    /// ドライバが無く、当たっていないインターフェースは開けないので通信できない。
    /// </para>
    /// <para>
    /// この INF は Windows 内蔵の winusb.sys をそのインターフェースに割り当てるだけで、
    /// 新しいカーネルモジュールは一切持ち込まない。
    /// </para>
    /// <para>
    /// 失敗しても Wi-Fi 接続は使えるので、導入全体は止めない。
    /// </para>
    /// </remarks>
    private static void InstallAoaDriver(string driverDir)
    {
        var infPath = Path.Combine(driverDir, "VMonitorAOA.inf");

        if (!File.Exists(infPath))
        {
            Log("USB 直結用のドライバが同梱されていないため、この手順を飛ばします。");
            return;
        }

        Log("USB 直結 (AOA) 用のドライバを登録しています...");

        var result = RunProcess("pnputil.exe", $"/add-driver \"{infPath}\" /install");

        if (result.ExitCode is 0 or 3010)
        {
            Log("USB 直結用のドライバを登録しました。");
            return;
        }

        Log($"USB 直結用のドライバの登録に失敗しました (終了コード {result.ExitCode})。\n" +
            $"{result.StdOut}\n{result.StdErr}\n" +
            "Wi-Fi 接続には影響しません。USB 直結を使う場合は再度インストールしてください。");
    }

    /// <summary>
    /// DriverStore に残っている vmonitor のドライバパッケージをすべて削除する。
    /// </summary>
    private static void RemoveOldDriverPackages()
    {
        foreach (var oemInf in FindVMonitorDriverPackages())
        {
            Log($"古いドライバパッケージを削除しています: {oemInf}");

            var result = RunProcess("pnputil.exe", $"/delete-driver {oemInf} /uninstall /force");

            Log(result.ExitCode is 0 or 3010
                ? $"  {oemInf} を削除しました。"
                : $"  {oemInf} の削除に失敗しました (コード {result.ExitCode})。続行します。");
        }
    }

    /// <summary>
    /// pnputil の出力から vmonitor のドライバパッケージ（oemNN.inf）を探す。
    /// </summary>
    private static List<string> FindVMonitorDriverPackages()
    {
        var found = new List<string>();

        var enumResult = RunProcess("pnputil.exe", "/enum-drivers");
        var lines = enumResult.StdOut.Split('\n');

        string? currentOem = null;

        foreach (var line in lines)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                line, @"(oem\d+\.inf)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                currentOem = match.Groups[1].Value;
                continue;
            }

            // 元の INF 名が vmonitorvdd.inf のエントリを、直前の公開名に結び付ける
            if (currentOem != null &&
                line.Contains("vmonitorvdd", StringComparison.OrdinalIgnoreCase))
            {
                if (!found.Contains(currentOem, StringComparer.OrdinalIgnoreCase))
                    found.Add(currentOem);

                currentOem = null;
            }
        }

        return found;
    }

    /// <summary>
    /// pnputil /delete-driver でドライバを DriverStore から削除する。
    /// </summary>
    private static void UninstallDriver(string? installDir)
    {
        // 先に仮想ディスプレイのデバイスノードを取り除く。
        // ドライバだけ消してノードを残すと、デバイスマネージャーに
        // ドライバ不明のデバイスが残り続ける。
        var displayClass = new Guid("4D36E968-E325-11CE-BFC1-08002BE10318");
        int removed = RootDeviceInstaller.RemoveDevices("Root\\VMonitorVDD", displayClass);
        Log(removed > 0
            ? $"仮想ディスプレイのデバイスを {removed} 個削除しました。"
            : "仮想ディスプレイのデバイスは見つかりませんでした（スキップ）。");

        // pnputil でインストール済みドライバを列挙して VMonitorVDD を探す
        var enumResult = RunProcess("pnputil.exe", "/enum-drivers");
        var lines = enumResult.StdOut.Split('\n');

        string? oemInfName = null;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("oem", StringComparison.OrdinalIgnoreCase) &&
                lines[i].Contains(".inf", StringComparison.OrdinalIgnoreCase))
            {
                // 直後の数行に VMonitorVDD が含まれるか確認する
                var chunk = string.Join("\n", lines.Skip(i).Take(8));
                if (chunk.Contains("VMonitorVDD", StringComparison.OrdinalIgnoreCase) ||
                    chunk.Contains("vmonitor", StringComparison.OrdinalIgnoreCase))
                {
                    // "Published Name: oemXX.inf" の形式から oem名を抽出する
                    var match = System.Text.RegularExpressions.Regex.Match(
                        lines[i], @"(oem\d+\.inf)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        oemInfName = match.Groups[1].Value;
                        break;
                    }
                }
            }
        }

        if (oemInfName != null)
        {
            Log($"ドライバを削除しています: {oemInfName}");
            var delResult = RunProcess("pnputil.exe", $"/delete-driver {oemInfName} /uninstall /force");
            Log(delResult.ExitCode == 0
                ? "ドライバの削除が完了しました。"
                : $"ドライバ削除の警告 (コード: {delResult.ExitCode}): {delResult.StdErr}");
        }
        else
        {
            Log("インストール済みの vmonitor ドライバは見つかりませんでした（スキップ）。");
        }

        RemoveSigningCertificate();
    }

    /// <summary>
    /// インストール時に取り込んだ署名証明書を信頼ストアから取り除く。
    /// </summary>
    /// <remarks>
    /// 入れたものは戻す。信頼されたルートに証明書を残したままにすると、
    /// その秘密鍵を持つ相手の署名をこの PC が信じ続けることになる。
    /// </remarks>
    private static void RemoveSigningCertificate()
    {
        Log("署名証明書を信頼ストアから削除しています...");

        // 証明書のサブジェクト名で該当するものだけを消す
        const string subject = "vmonitor Test Certificate";

        foreach (var store in new[] { "Root", "TrustedPublisher" })
        {
            var result = RunProcess("certutil.exe", $"-delstore {store} \"{subject}\"");

            Log(result.ExitCode == 0
                ? $"  {store} から削除しました。"
                : $"  {store} には見つかりませんでした（スキップ）。");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // ファイアウォール
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>ファイアウォール規則名の接頭辞（アンインストール時の削除にも使う）。</summary>
    private const string FirewallRulePrefix = "vmonitor";

    /// <summary>スマホとの通信に使う TCP ポート。</summary>
    private const int TransportPort = 7979;

    /// <summary>mDNS (マルチキャスト DNS) の標準ポート。</summary>
    private const int MdnsPort = 5353;

    /// <summary>
    /// スマホから PC へ接続できるよう、Windows ファイアウォールに受信規則を追加する。
    /// </summary>
    /// <remarks>
    /// これを入れないと、スマホからの接続要求も mDNS の問い合わせも
    /// OS に届く前に落とされる。ユーザーからは「PC が見つからない」
    /// あるいは「接続できない」としか見えず、原因が分からない。
    ///
    /// 規則はプログラム単位で絞り、ポートを無条件に開けたままにしない。
    /// </remarks>
    private static void ConfigureFirewall(string installDir)
    {
        var exePath = Path.Combine(installDir, "VMonitor.UI.exe");

        // 再インストール時に規則が重複しないよう、先に既存の規則を消す
        RemoveFirewallRules();

        var rules = new (string Name, string Protocol, int Port, string Description)[]
        {
            ($"{FirewallRulePrefix} - 映像・タッチ転送 (TCP {TransportPort})",
             "TCP", TransportPort, "スマホからの接続を受け付ける"),

            ($"{FirewallRulePrefix} - デバイス探索 (UDP {MdnsPort})",
             "UDP", MdnsPort, "mDNS でスマホから PC を見つけられるようにする"),
        };

        foreach (var (name, protocol, port, description) in rules)
        {
            var result = RunProcess("netsh.exe",
                $"advfirewall firewall add rule name=\"{name}\" " +
                $"dir=in action=allow protocol={protocol} localport={port} " +
                $"program=\"{exePath}\" enable=yes profile=private,domain");

            if (result.ExitCode == 0)
                Log($"  規則を追加: {name}");
            else
                Log($"  規則追加の警告 ({name}): 終了コード {result.ExitCode} {result.StdErr.Trim()}");
        }
    }

    /// <summary>vmonitor が追加したファイアウォール規則をすべて削除する。</summary>
    private static void RemoveFirewallRules()
    {
        // 規則名は接頭辞で始まるので、名前を指定して個別に削除する
        var names = new[]
        {
            $"{FirewallRulePrefix} - 映像・タッチ転送 (TCP {TransportPort})",
            $"{FirewallRulePrefix} - デバイス探索 (UDP {MdnsPort})",
        };

        foreach (var name in names)
        {
            // 存在しない規則の削除は失敗するが、そのまま進めてよい
            RunProcess("netsh.exe", $"advfirewall firewall delete rule name=\"{name}\"");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // ファイルコピー
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 実行ファイルと同じディレクトリにある payload ファイルを installDir にコピーする。
    /// 実際の配布では全ファイルがこのセットアップ EXE と同じフォルダにある想定。
    /// </summary>
    private static void CopyPayloadFiles(string installDir)
    {
        var sourceDir = AppContext.BaseDirectory;

        // 同梱ファイルはすべてコピーする。
        //
        // 以前はファイル名のホワイトリストで選んでいたが、依存パッケージ
        // (Vortice.*, Makaretu.*, SharpGen.Runtime など) が漏れていて、
        // インストールしたアプリが起動できなかった。
        // 依存関係が増えるたびに一覧を直す方式は必ず取りこぼすので、
        // 「除外するものだけ挙げて残りは全部持っていく」ようにする。
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VMonitorSetup.exe",          // 後段で別途コピーする
            "VMonitor.Installer.exe",
            "VMonitor.Installer.dll",
            "VMonitor.Installer.pdb",
            "VMonitor.Installer.deps.json",
            "VMonitor.Installer.runtimeconfig.json",
        };

        int copied = 0;

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);

            if (excluded.Contains(name)) continue;
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;

            File.Copy(file, Path.Combine(installDir, name), overwrite: true);
            copied++;
        }

        Log($"  {copied} 個のファイルをコピーしました。");

        // サブフォルダ（driver、各言語のリソースなど）を丸ごとコピーする
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            CopyDirectory(dir, Path.Combine(installDir, dirName));
            Log($"  コピー: {dirName}\\");
        }

        // このセットアップ EXE 自体もコピー（アンインストール用）
        var setupExe = Environment.ProcessPath;
        if (setupExe != null && File.Exists(setupExe))
            File.Copy(setupExe, Path.Combine(installDir, "VMonitorSetup.exe"), overwrite: true);
    }

    /// <summary>2 つのパスが同じディレクトリを指しているか。</summary>
    /// <remarks>
    /// 末尾の区切り文字や大文字小文字の違いを吸収する。
    /// Windows のパスは大文字小文字を区別しない。
    /// </remarks>
    private static bool IsSameDirectory(string a, string b)
    {
        try
        {
            var left  = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            var right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>ディレクトリを再帰的にコピーする。</summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        // 自分自身へのコピーは何もしない。
        // File.Copy は元と先が同じだと「使用中」で失敗する。
        if (IsSameDirectory(sourceDir, destDir)) return;

        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    // ─────────────────────────────────────────────────────────────────────
    // ショートカット
    // ─────────────────────────────────────────────────────────────────────

    private static void CreateStartMenuShortcut(string installDir)
    {
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "vmonitor");
        Directory.CreateDirectory(startMenu);

        CreateShortcut(
            Path.Combine(startMenu, "vmonitor.lnk"),
            Path.Combine(installDir, "VMonitor.UI.exe"),
            installDir);

        CreateShortcut(
            Path.Combine(startMenu, "vmonitor をアンインストール.lnk"),
            Path.Combine(installDir, "VMonitorSetup.exe"),
            installDir,
            arguments: "/uninstall");
    }

    private static void CreateDesktopShortcut(string installDir)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        CreateShortcut(
            Path.Combine(desktop, "vmonitor.lnk"),
            Path.Combine(installDir, "VMonitor.UI.exe"),
            installDir);
    }

    private static void RemoveShortcuts()
    {
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "vmonitor");

        if (Directory.Exists(startMenu))
            Directory.Delete(startMenu, recursive: true);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var desktopLink = Path.Combine(desktop, "vmonitor.lnk");
        if (File.Exists(desktopLink))
            File.Delete(desktopLink);
    }

    /// <summary>COM IShellLink を使って .lnk ショートカットを作成する。</summary>
    private static void CreateShortcut(
        string lnkPath,
        string targetPath,
        string workingDir,
        string arguments = "")
    {
        try
        {
            // PowerShell 経由でショートカットを作成する（COM 依存を避ける）
            var ps = $"$s=(New-Object -COM WScript.Shell).CreateShortcut('{lnkPath}');" +
                     $"$s.TargetPath='{targetPath}';" +
                     $"$s.WorkingDirectory='{workingDir}';" +
                     $"$s.Arguments='{arguments}';" +
                     "$s.Save()";
            RunProcess("powershell.exe", $"-NoProfile -NonInteractive -Command \"{ps}\"");
        }
        catch (Exception ex)
        {
            Log($"ショートカット作成の警告: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // レジストリ
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Inno Setup 側が作るアンインストール登録のキー。</summary>
    private const string InnoUninstallRegKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" +
        "{F41484CA-0A01-4733-A9A7-C5A730D3A5CE}_is1";

    /// <summary>
    /// 指定フォルダを、別のインストーラーの登録も使っているか。
    /// </summary>
    /// <remarks>
    /// 同じフォルダに 2 つの登録がぶら下がっていると、片方のアンインストールが
    /// もう片方のファイルを持っていく。消してよいかの判断に使う。
    /// </remarks>
    private static bool IsDirectoryOwnedByAnotherInstaller(string installDir)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(InnoUninstallRegKey);
            if (key?.GetValue("InstallLocation") is not string location) return false;
            if (string.IsNullOrWhiteSpace(location)) return false;

            var left  = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            var right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 判断が付かないなら、消さない側に倒す方が安全
            return true;
        }
    }

    private static void RegisterUninstallEntry(string installDir)
    {
        // Inno Setup が既に登録しているなら、こちらは登録しない。
        //
        // 並べると「追加と削除」に vmonitor が 2 つ出て、利用者はどちらを
        // 消せばよいか分からない。しかも同じフォルダを指すので、
        // 片方を消すともう片方が壊れる。
        if (IsDirectoryOwnedByAnotherInstaller(installDir))
        {
            Log("インストーラー側で登録済みのため、追加と削除への登録は省きます。");
            return;
        }

        using var key = Registry.LocalMachine.CreateSubKey(UninstallRegKey);
        if (key == null) return;

        var uninstallExe = Path.Combine(installDir, "VMonitorSetup.exe");
        key.SetValue("DisplayName",          $"{AppName} {AppVersion}");
        key.SetValue("DisplayVersion",       AppVersion);
        key.SetValue("Publisher",            Publisher);
        key.SetValue("InstallLocation",      installDir);
        key.SetValue("UninstallString",      $"\"{uninstallExe}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstallExe}\" /uninstall /silent");
        key.SetValue("NoModify",             1, RegistryValueKind.DWord);
        key.SetValue("NoRepair",             1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize",        EstimateInstallSize(installDir), RegistryValueKind.DWord);
    }

    private static int EstimateInstallSize(string dir)
    {
        try
        {
            long bytes = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                                  .Sum(f => new FileInfo(f).Length);
            return (int)(bytes / 1024); // KB 単位
        }
        catch { return 0; }
    }

    private static string? GetInstalledDir()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallRegKey);
            return key?.GetValue("InstallLocation") as string;
        }
        catch { return null; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // プロセス管理
    // ─────────────────────────────────────────────────────────────────────

    private static void StopVMonitorProcess()
    {
        foreach (var proc in Process.GetProcessesByName("VMonitor.UI"))
        {
            try
            {
                proc.CloseMainWindow();
                if (!proc.WaitForExit(3000))
                    proc.Kill();
            }
            catch { /* 強制終了失敗は無視 */ }
        }
    }

    private record ProcessResult(int ExitCode, string StdOut, string StdErr);

    private static ProcessResult RunProcess(string fileName, string arguments)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            }
        };

        proc.Start();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        return new ProcessResult(proc.ExitCode, stdout, stderr);
    }

    // ─────────────────────────────────────────────────────────────────────
    // ユーティリティ
    // ─────────────────────────────────────────────────────────────────────

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void Log(string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        Console.WriteLine($"[{ts}] {message}");
    }

    private static void ShowError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n[ERROR] {message}");
        Console.ResetColor();
    }
}
