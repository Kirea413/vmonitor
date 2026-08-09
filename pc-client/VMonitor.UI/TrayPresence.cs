using System.IO;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace VMonitor.UI;

/// <summary>ウィンドウの閉じるボタンを押したときの動き。</summary>
public enum CloseAction
{
    /// <summary>タスクトレイへしまう（既定）。</summary>
    MinimizeToTray,

    /// <summary>終了する。</summary>
    Exit,
}

/// <summary>
/// タスクトレイへの常駐と、Windows 起動時の自動起動。
/// </summary>
/// <remarks>
/// <para>
/// このアプリは「スマホを繋いだら映る」ことを期待されるので、
/// 閉じるボタンで終了してしまうと、次に繋いだときに何も起きない。
/// 既定ではトレイへしまい、待ち受けを続ける。
/// </para>
/// <para>
/// ただし本当に終わらせたい人もいるので、動きは設定で選べるようにしてある。
/// 勝手に居座るソフトは嫌われる。
/// </para>
/// </remarks>
public sealed class TrayPresence : IDisposable
{
    /// <summary>自動起動の登録先。管理者権限が要らない利用者ごとのキー。</summary>
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "vmonitor";

    private readonly Window          _window;
    private readonly Forms.NotifyIcon _icon;

    private bool _reallyExiting;
    private bool _disposed;

    /// <summary>閉じるボタンを押したときの動き。</summary>
    public CloseAction CloseAction { get; set; } = CloseAction.MinimizeToTray;

    public TrayPresence(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));

        _icon = new Forms.NotifyIcon
        {
            Text    = "vmonitor",
            Icon    = LoadIcon(),
            Visible = true,
        };

        // 一覧から選ぶより、アイコンを叩いて戻れるほうが早い
        _icon.DoubleClick += (_, _) => ShowWindow();

        var menu = new Forms.ContextMenuStrip();

        menu.Items.Add("vmonitor を開く", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApplication());

        _icon.ContextMenuStrip = menu;

        _window.Closing += OnWindowClosing;
        _window.StateChanged += OnWindowStateChanged;
    }

    /// <summary>本当に終わらせる（トレイへ逃がさない）。</summary>
    public void ExitApplication()
    {
        _reallyExiting = true;
        Application.Current?.Shutdown();
    }

    /// <summary>ウィンドウを出して前面に持ってくる。</summary>
    public void ShowWindow()
    {
        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
    }

    // ── Windows 起動時の自動起動 ─────────────────────────────────────────

    /// <summary>自動起動が登録されているか。</summary>
    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);

            return key?.GetValue(RunValueName) is string path &&
                   !string.IsNullOrWhiteSpace(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 自動起動の登録を切り替える。
    /// </summary>
    /// <remarks>
    /// 利用者ごとのキーを使うので管理者権限は要らない。
    /// 起動時はトレイに入った状態で始めたいので、引数を添える。
    /// </remarks>
    public static bool SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (!enabled)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                return true;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return false;

            key.SetValue(RunValueName, $"\"{exe}\" --tray");
            return true;
        }
        catch
        {
            // 企業のポリシーなどで書けないことがある
            return false;
        }
    }

    /// <summary>この起動がトレイ常駐として始まったか。</summary>
    public static bool StartedInTray(string[] args) =>
        args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

    // ── 内部 ─────────────────────────────────────────────────────────────

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExiting) return;
        if (CloseAction == CloseAction.Exit) return;

        // 閉じずにしまう。待ち受けは続くので、繋げばまた映る。
        e.Cancel = true;
        _window.Hide();

        ShowFirstTimeHint();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (CloseAction != CloseAction.MinimizeToTray) return;
        if (_window.WindowState != WindowState.Minimized) return;

        _window.Hide();
    }

    /// <summary>
    /// 初めてしまったときだけ、どこへ行ったのかを知らせる。
    /// </summary>
    /// <remarks>
    /// 黙って消えると「落ちた」と思われる。毎回出すと鬱陶しいので一度きり。
    /// </remarks>
    private bool _hintShown;

    private void ShowFirstTimeHint()
    {
        if (_hintShown) return;
        _hintShown = true;

        try
        {
            _icon.BalloonTipTitle = "vmonitor は動いたままです";
            _icon.BalloonTipText  = "通知領域に入りました。スマホを繋げばそのまま映ります。";
            _icon.ShowBalloonTip(4000);
        }
        catch
        {
            // 通知が抑止されている環境では出せないが、実害はない
        }
    }

    /// <summary>
    /// トレイに出すアイコンを読む。
    /// </summary>
    /// <remarks>
    /// 実行ファイルに埋め込んだものを取り出す。別ファイルを置くと
    /// インストール先で見失うことがある。
    /// </remarks>
    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;

            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch
        {
            // 取り出せなければ既定のものにする
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _window.Closing -= OnWindowClosing;
        _window.StateChanged -= OnWindowStateChanged;

        // 消しておかないと、終了後もトレイにアイコンが残る
        _icon.Visible = false;
        _icon.Dispose();
    }
}
