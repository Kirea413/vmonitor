// vmonitor doctor — 環境診断ツール
//
// 「接続できない」「映像が出ない」「タッチが効かない」といったときに、
// どの段階で止まっているのかを切り分けるために使う。
//
//   vmonitor-doctor            すべての項目を診断する
//   vmonitor-doctor capture    画面キャプチャだけを確認し、結果を BMP に保存する
//   vmonitor-doctor encode     H.264 エンコードの可否とスループットを測る
//   vmonitor-doctor input      ポインター注入 API が使えるかを確認する
//   vmonitor-doctor mdns       mDNS の登録と探索を確認する
//   vmonitor-doctor display    仮想ディスプレイの接続・切断を実際に試す
//   vmonitor-doctor vdisplay   仮想ディスプレイを実際に取り込めるか試す（拡張表示の要）
//   vmonitor-doctor touch      実際にタッチを注入して Windows が受け付けるか試す
//   vmonitor-doctor aoa        USB に繋がった端末を一覧し、AOA で掴めるか確認する
//   vmonitor-doctor aoa switch 実際にアクセサリーモードへ切り替えて掴んでみる

using VMonitor.Diagnostics;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

Console.WriteLine("=== vmonitor doctor ===");
Console.WriteLine($"OS            : {Environment.OSVersion}");
Console.WriteLine($"64bit process : {Environment.Is64BitProcess}");
Console.WriteLine();

bool all = mode == "all";
int problems = 0;

if (all || mode == "input")   problems += Checks.CheckPointerInjection();
if (all || mode == "capture") problems += Checks.CheckScreenCapture();
if (all || mode == "encode")  problems += Checks.CheckEncoder();
if (mode == "display")        problems += Checks.CheckVirtualDisplay();
if (mode == "lag")            problems += Checks.CheckCaptureLag();
if (mode == "vdisplay")
{
    // vmonitor-doctor vdisplay [幅] [高さ]
    int reqW = args.Length > 1 && int.TryParse(args[1], out var w) ? w : 1080;
    int reqH = args.Length > 2 && int.TryParse(args[2], out var h) ? h : 1920;
    problems += Checks.CheckVirtualDisplayCapture(reqW, reqH);
}
if (mode == "touch")          problems += Checks.CheckTouchInjection();
if (mode == "aoa" && args.Length > 1 && args[1].Equals("echo", StringComparison.OrdinalIgnoreCase))
{
    problems += await Checks.CheckAoaEchoAsync(seconds: 20);
}
else if (all || mode == "aoa") problems += Checks.CheckAoa(
                                  performSwitch: mode == "aoa" &&
                                                 args.Length > 1 &&
                                                 args[1].Equals("switch", StringComparison.OrdinalIgnoreCase));
if (all || mode == "mdns")    problems += await Checks.CheckMdnsAsync();

Console.WriteLine();

if (problems == 0)
{
    Console.WriteLine("問題は見つかりませんでした。");
    return 0;
}

Console.WriteLine($"{problems} 件の問題が見つかりました。上の説明に従って対処してください。");
return 1;
