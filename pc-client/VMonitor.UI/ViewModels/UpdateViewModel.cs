using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace VMonitor.UI.ViewModels;

/// <summary>
/// 更新の確認と適用。
/// </summary>
/// <remarks>
/// 見つけても勝手には適用しない。インストーラーはドライバを入れ替え、
/// 管理者への昇格を求める。作業中に黙って始まると困るので、
/// 知らせて、押されたら進める。
/// </remarks>
public sealed class UpdateViewModel : INotifyPropertyChanged
{
    private readonly UpdateChecker _checker;

    private AvailableUpdate? _available;
    private string _status = string.Empty;
    private bool   _isBusy;
    private double _progress;

    public UpdateViewModel(UpdateChecker? checker = null)
    {
        _checker = checker ?? new UpdateChecker(CurrentVersion);

        CheckCommand = new RelayCommand(
            execute:    _ => _ = CheckAsync(announceNoUpdate: true),
            canExecute: _ => !_isBusy);

        InstallCommand = new RelayCommand(
            execute:    _ => _ = InstallAsync(),
            canExecute: _ => !_isBusy && _available is not null);

        OpenReleasesCommand = new RelayCommand(execute: _ => OpenReleasesPage());
    }

    /// <summary>いま動いている版。</summary>
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>画面に出す現在の版。</summary>
    public string CurrentVersionText =>
        $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    /// <summary>確認の結果や進み具合。</summary>
    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    /// <summary>新版が見つかっているか。</summary>
    public bool HasUpdate => _available is not null;

    /// <summary>新版の説明（見つかっているときだけ意味がある）。</summary>
    public string UpdateSummary => _available is null
        ? string.Empty
        : $"{_available.TagName}（{_available.SizeBytes / 1024.0 / 1024.0:N1} MB）";

    /// <summary>ダウンロードの進み具合（0〜1）。</summary>
    public double Progress
    {
        get => _progress;
        private set { _progress = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            RaiseCanExecute();
        }
    }

    public ICommand CheckCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand OpenReleasesCommand { get; }

    /// <summary>
    /// 新版が出ていないか調べる。
    /// </summary>
    /// <param name="announceNoUpdate">
    /// 新版が無いときも結果を出すか。起動時の自動確認では黙らせる
    /// （毎回「最新です」と出ても邪魔なだけなので）。
    /// </param>
    public async Task CheckAsync(bool announceNoUpdate)
    {
        IsBusy = true;
        Status = "更新を確認しています…";

        try
        {
            var found = await _checker.CheckAsync();

            _available = found;

            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateSummary));

            Status = found is not null
                ? $"新しい版があります: {found.TagName}"
                : announceNoUpdate ? "お使いの版が最新です。" : string.Empty;
        }
        catch (Exception ex)
        {
            Status = $"更新を確認できませんでした: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 新版を受け取ってインストーラーを起動する。
    /// </summary>
    /// <remarks>
    /// インストーラーは管理者権限を求めるので、起動すると UAC が出る。
    /// 上書きインストールになるため、こちらは終了しておく。
    /// </remarks>
    private async Task InstallAsync()
    {
        var update = _available;
        if (update is null) return;

        var confirmed = MessageBox.Show(
            $"新しい版 {update.TagName} をインストールします。\n\n" +
            "・vmonitor はいったん終了します\n" +
            "・管理者の確認（UAC）が出ます\n" +
            "・ドライバも入れ替わります\n\n" +
            "続けますか？",
            "更新", MessageBoxButton.OKCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        if (confirmed != MessageBoxResult.OK) return;

        IsBusy = true;
        Progress = 0;
        Status = "ダウンロードしています…";

        try
        {
            var path = await _checker.DownloadAsync(
                update,
                onProgress: value =>
                {
                    var dispatcher = Application.Current?.Dispatcher;

                    if (dispatcher is null) { Progress = value; return; }

                    dispatcher.Invoke(() => Progress = value);
                });

            Status = "インストーラーを起動しています…";

            Process.Start(new ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true,   // UAC の昇格に必要
            });

            // 上書きインストールになるので、掴んでいるものを放して終わる。
            // 残っているとファイルの置き換えに失敗する。
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            Status = $"更新に失敗しました: {ex.Message}";
            IsBusy = false;
        }
    }

    private static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = UpdateChecker.ReleasesPageUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 既定のブラウザが無い場合など
        }
    }

    private void RaiseCanExecute()
    {
        ((RelayCommand)CheckCommand).RaiseCanExecuteChanged();
        ((RelayCommand)InstallCommand).RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
