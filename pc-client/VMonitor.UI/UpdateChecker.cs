using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VMonitor.UI;

/// <summary>見つかった新版。</summary>
/// <param name="Version">タグから読み取ったバージョン。</param>
/// <param name="TagName">Releases のタグ名（表示用）。</param>
/// <param name="DownloadUrl">インストーラーの取得先。</param>
/// <param name="SizeBytes">インストーラーの大きさ。</param>
/// <param name="Notes">リリースノート（先頭のみ表示に使う）。</param>
public sealed record AvailableUpdate(
    Version Version,
    string  TagName,
    string  DownloadUrl,
    long    SizeBytes,
    string  Notes);

/// <summary>
/// GitHub Releases を見て、新しい版が出ていないか調べる。
/// </summary>
/// <remarks>
/// <para>
/// 見つけても勝手には適用しない。ドライバの入れ替えと管理者への昇格が
/// 絡むため、作業中に黙って始まると困る。知らせて、押されたら進める。
/// </para>
/// <para>
/// 公開リポジトリの Releases は認証なしで読める。ただし未認証の
/// GitHub API には 1 時間あたりの回数制限があるので、
/// 起動時に一度だけ見るくらいに留めること。
/// </para>
/// </remarks>
public sealed class UpdateChecker
{
    /// <summary>配布元のリポジトリ。</summary>
    public const string Owner = "Kirea413";
    public const string Repo  = "vmonitor";

    private const string LatestReleaseUrl =
        $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>この Releases のページ（利用者に見せる用）。</summary>
    public const string ReleasesPageUrl =
        $"https://github.com/{Owner}/{Repo}/releases";

    private readonly HttpClient _http;
    private readonly Version    _currentVersion;

    public UpdateChecker(Version currentVersion, HttpClient? http = null)
    {
        _currentVersion = currentVersion;

        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // GitHub API は User-Agent が無いと 403 を返す
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("vmonitor", _currentVersion.ToString()));
        }
    }

    /// <summary>
    /// 新版が出ているか調べる。出ていなければ null。
    /// </summary>
    /// <remarks>
    /// 通信に失敗しても例外は投げない。更新の確認ができないことは
    /// アプリが使えないことを意味しないので、起動を妨げない。
    /// </remarks>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(LatestReleaseUrl, ct);

            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var root = document.RootElement;

            // 下書きや事前公開版は拾わない
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

            if (!root.TryGetProperty("tag_name", out var tag)) return null;

            var tagName = tag.GetString();
            if (string.IsNullOrWhiteSpace(tagName)) return null;

            var version = ParseVersion(tagName);
            if (version is null) return null;
            if (version <= _currentVersion) return null;

            var asset = FindInstallerAsset(root);
            if (asset is null) return null;

            var notes = root.TryGetProperty("body", out var body)
                ? body.GetString() ?? string.Empty
                : string.Empty;

            return new AvailableUpdate(
                Version:     version,
                TagName:     tagName,
                DownloadUrl: asset.Value.Url,
                SizeBytes:   asset.Value.Size,
                Notes:       notes.Trim());
        }
        catch
        {
            // 圏外、制限に当たった、形が変わった、など。
            // 分からないなら「新版なし」として扱う。
            return null;
        }
    }

    /// <summary>
    /// インストーラーを受け取る。
    /// </summary>
    /// <param name="update">取得する版。</param>
    /// <param name="onProgress">0〜1 の進み具合。分からない場合は呼ばれない。</param>
    /// <returns>保存した場所。</returns>
    public async Task<string> DownloadAsync(
        AvailableUpdate      update,
        Action<double>?      onProgress = null,
        CancellationToken    ct = default)
    {
        // 一時フォルダに置く。インストール先に直接落とすと、
        // 実行中のアプリのフォルダを触ることになる。
        var directory = Path.Combine(Path.GetTempPath(), "vmonitor-update");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"vmonitor-{update.TagName}-setup.exe");

        using var response = await _http.GetAsync(
            update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);

        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? update.SizeBytes;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            long copied = 0;
            int  read;

            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);

                copied += read;
                if (total > 0) onProgress?.Invoke(Math.Min(1.0, (double)copied / total));
            }
        }

        // 中身が来ていないのに成功扱いにしない
        if (new FileInfo(path).Length == 0)
        {
            File.Delete(path);
            throw new InvalidOperationException("ダウンロードした更新ファイルが空でした。");
        }

        return path;
    }

    // ── 内部 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// タグ名からバージョンを読む。"v1.2.0" のような接頭辞を許す。
    /// </summary>
    internal static Version? ParseVersion(string tagName)
    {
        var text = tagName.Trim();

        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        // "1.2.0-beta" のような後ろ足しは切る
        int dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];

        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>
    /// 未指定の桁を 0 として揃える。
    /// </summary>
    /// <remarks>
    /// Version は未指定の桁を -1 で持つため、"1.1" と "1.1.0" が
    /// そのままでは違うものとして比較されてしまう。
    /// </remarks>
    private static Version Normalize(Version version) => new(
        version.Major,
        version.Minor,
        version.Build    < 0 ? 0 : version.Build,
        version.Revision < 0 ? 0 : version.Revision);

    /// <summary>Releases の添付から setup.exe を探す。</summary>
    private static (string Url, long Size)? FindInstallerAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets)) return null;
        if (assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;

            if (name is null) continue;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            // 添付には署名ファイルなども混じりうる。インストーラーを選ぶ。
            if (!name.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var u)
                ? u.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(url)) continue;

            long size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

            return (url, size);
        }

        return null;
    }
}
