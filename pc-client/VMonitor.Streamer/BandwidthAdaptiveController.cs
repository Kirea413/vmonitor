using VMonitor.Core.Models;

namespace VMonitor.Streamer;

/// <summary>
/// 帯域推定値に応じてビットレートと解像度を段階的に調整するコントローラー。
///
/// 品質ティアの設計:
///   各ティアは (最小帯域 bps, ビットレート bps, 解像度, fps) の組。
///   帯域推定値がティアの最小帯域を下回ったら、そのティアより低いティアへ降格する。
///   帯域が回復してもすぐには昇格せず、連続した高帯域観測後に段階的に戻る（変動耐性）。
///
/// 重要制約:
///   - 最低品質ティアでも MaxFps は 30fps を保証する。
///   - 出力ビットレートは帯域推定値を超えてはならない（Property 8 の条件）。
/// </summary>
public sealed class BandwidthAdaptiveController
{
    // -------------------------------------------------------------------------
    // 品質ティア定義
    // -------------------------------------------------------------------------

    /// <summary>品質ティアを表す不変レコード。</summary>
    public record QualityTier(
        string Name,
        long MinBandwidthBps,   // このティアを維持するのに必要な最小帯域
        int TargetBitrateBps,   // エンコーダーへの目標ビットレート
        Resolution Resolution,  // 仮想ディスプレイ解像度
        int MaxFps              // フレームレート上限（最低 30 保証）
    );

    /// <summary>品質ティア一覧（高品質→低品質の順）。</summary>
    public static readonly IReadOnlyList<QualityTier> DefaultTiers = new List<QualityTier>
    {
        // Tier 0: 超高品質 (4K 60fps / 20 Mbps+)
        new("Ultra",    20_000_000, 18_000_000, new Resolution(3840, 2160), 60),
        // Tier 1: 高品質 (1080p 60fps / 10 Mbps+)
        new("High",     10_000_000,  8_000_000, new Resolution(1920, 1080), 60),
        // Tier 2: 中品質 (1080p 30fps / 5 Mbps+)
        new("Medium",    5_000_000,  4_000_000, new Resolution(1920, 1080), 30),
        // Tier 3: 低品質 (720p 30fps / 2 Mbps+)
        new("Low",       2_000_000,  1_500_000, new Resolution(1280,  720), 30),
        // Tier 4: 最低品質 (480p 30fps / 500 kbps+) — 最低でも 30fps を維持
        new("Minimum",     500_000,    400_000, new Resolution( 854,  480), 30),
    };

    // -------------------------------------------------------------------------
    // フィールドとロック
    // -------------------------------------------------------------------------

    private readonly object _lock = new();
    private readonly IReadOnlyList<QualityTier> _tiers;

    // 現在のティアインデックス（0 = 最高品質、_tiers.Count-1 = 最低品質）
    private int _currentTierIndex;

    // 帯域回復検出: 昇格前に必要な連続高帯域観測回数
    private int _upgradeCandidateCount;
    private const int UpgradeThreshold = 3; // 3 回連続で上位ティアの帯域を満たせば昇格

    // -------------------------------------------------------------------------
    // 公開プロパティ
    // -------------------------------------------------------------------------

    /// <summary>現在適用中の品質ティア（スレッドセーフ）。</summary>
    public QualityTier CurrentTier
    {
        get { lock (_lock) return _tiers[_currentTierIndex]; }
    }

    /// <summary>現在の目標ビットレート（bps）。</summary>
    public int CurrentBitrateBps => CurrentTier.TargetBitrateBps;

    /// <summary>現在の目標解像度。</summary>
    public Resolution CurrentResolution => CurrentTier.Resolution;

    /// <summary>現在のフレームレート上限（常に 30 以上）。</summary>
    public int CurrentMaxFps => CurrentTier.MaxFps;

    // -------------------------------------------------------------------------
    // コンストラクター
    // -------------------------------------------------------------------------

    /// <summary>デフォルトのティアテーブルでコントローラーを作成する。</summary>
    public BandwidthAdaptiveController()
        : this(DefaultTiers, initialTierIndex: 1) { }

    /// <summary>カスタムティアテーブルと開始ティアでコントローラーを作成する（テスト用）。</summary>
    public BandwidthAdaptiveController(IReadOnlyList<QualityTier> tiers, int initialTierIndex = 0)
    {
        if (tiers == null || tiers.Count == 0)
            throw new ArgumentException("tiers must not be empty", nameof(tiers));
        if (initialTierIndex < 0 || initialTierIndex >= tiers.Count)
            throw new ArgumentOutOfRangeException(nameof(initialTierIndex));

        // 最低ティアの fps が 30 以上であることをアサート
        var lowestTier = tiers[tiers.Count - 1];
        if (lowestTier.MaxFps < 30)
            throw new ArgumentException(
                $"The lowest quality tier '{lowestTier.Name}' must guarantee at least 30 fps, " +
                $"but MaxFps={lowestTier.MaxFps}.",
                nameof(tiers));

        _tiers = tiers;
        _currentTierIndex = initialTierIndex;
    }

    // -------------------------------------------------------------------------
    // コアロジック
    // -------------------------------------------------------------------------

    /// <summary>
    /// 帯域推定値（bps）を受け取り、最適なティアへ遷移する。
    /// 帯域低下時は即座に降格、帯域回復時は一定回数観測後に昇格する。
    /// </summary>
    /// <param name="estimatedBandwidthBps">RTCP/RTT 計測などによる帯域推定値（bps）。0 以上。</param>
    /// <returns>
    /// ティア遷移が発生した場合の新しいティア設定。遷移がなければ null。
    /// 呼び出し元はこの戻り値を使ってエンコーダー設定を更新する。
    /// </returns>
    public QualityTier? Update(long estimatedBandwidthBps)
    {
        if (estimatedBandwidthBps < 0)
            estimatedBandwidthBps = 0;

        lock (_lock)
        {
            int previousIndex = _currentTierIndex;

            // ── 降格チェック ────────────────────────────────────────────────
            // 現在のティアに必要な最小帯域を下回ったら、帯域を満たす最初のティアへ降格する。
            // 帯域が 0 の場合は最低ティアへ降格する。
            if (estimatedBandwidthBps < _tiers[_currentTierIndex].MinBandwidthBps)
            {
                _upgradeCandidateCount = 0;

                // 最低ティアから昇順に探索し、帯域を満たす最高品質ティアを選択する
                int newIndex = _tiers.Count - 1; // 見つからなければ最低ティア
                for (int i = _tiers.Count - 1; i >= 0; i--)
                {
                    if (estimatedBandwidthBps >= _tiers[i].MinBandwidthBps)
                    {
                        newIndex = i;
                        break;
                    }
                }
                _currentTierIndex = newIndex;
            }
            // ── 昇格チェック ────────────────────────────────────────────────
            // 現在のティアより 1 段上のティアの最小帯域を連続して満たしたら昇格する。
            else if (_currentTierIndex > 0)
            {
                int candidateIndex = _currentTierIndex - 1;
                if (estimatedBandwidthBps >= _tiers[candidateIndex].MinBandwidthBps)
                {
                    _upgradeCandidateCount++;
                    if (_upgradeCandidateCount >= UpgradeThreshold)
                    {
                        _upgradeCandidateCount = 0;
                        _currentTierIndex = candidateIndex;
                    }
                }
                else
                {
                    _upgradeCandidateCount = 0;
                }
            }
            else
            {
                // すでに最高ティア
                _upgradeCandidateCount = 0;
            }

            // 遷移があった場合のみ新しいティアを返す
            return _currentTierIndex != previousIndex ? _tiers[_currentTierIndex] : null;
        }
    }

    /// <summary>
    /// 帯域推定値に対して上限を設けたビットレートを計算する。
    /// ターゲットビットレートが帯域推定値を超えないよう制限する。
    /// </summary>
    /// <param name="estimatedBandwidthBps">帯域推定値（bps）。</param>
    /// <returns>実際にエンコーダーへ設定すべきビットレート（bps）。</returns>
    public int CalculateCappedBitrate(long estimatedBandwidthBps)
    {
        lock (_lock)
        {
            int tierBitrate = _tiers[_currentTierIndex].TargetBitrateBps;
            // ビットレートは帯域推定値を超えてはならない（Property 8 の条件）
            long capped = Math.Min(tierBitrate, estimatedBandwidthBps);
            return (int)Math.Max(0, capped);
        }
    }
}
