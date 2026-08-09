// Feature: vmonitor, Property 14: タッチイベントの完全転送

using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Models;
using VMonitor.Session.Input;

namespace VMonitor.Tests;

/// <summary>
/// Property 14: タッチイベントの完全転送
/// Validates: Requirements 6.1, 6.4
///
/// スマホ側が送信したタッチイベントは、PC 側で欠落なく復元されなければならない。
/// 1 点でも落ちると、マルチタッチのジェスチャーが別のジェスチャーとして
/// 解釈されたり、指が押されっぱなしになったりする。
///
/// 送信側の形式は mobile-app/lib/touch/touch_input_proxy.dart の
/// _serializeEvent が定義しており、本テストはその形式との整合を固定する。
/// </summary>
public class TouchEventCodecPropertyTests
{
    // ── ヘルパー ────────────────────────────────────────────────────────────

    private static double NormalizeCoord(int raw) => Math.Abs(raw) % 10001 / 10000.0;

    private static TouchPhase NormalizePhase(int raw) => (TouchPhase)(Math.Abs(raw) % 4);

    private static Orientation NormalizeOrientation(int raw) => (Orientation)(Math.Abs(raw) % 4);

    /// <summary>
    /// float32 で往復するため、比較は単精度の丸め誤差を許容する。
    /// 正規化座標は [0,1] なので絶対誤差 1e-6 で十分。
    /// </summary>
    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 1e-6;

    // ── Property 14-A: ラウンドトリップ ─────────────────────────────────────

    /// <summary>
    /// Property 14-A: 任意のタッチイベントをエンコードしてデコードすると、
    /// すべてのタッチポイントが同じ ID・座標・圧力・フェーズで復元されなければならない。
    ///
    /// Validates: Requirements 6.1, 6.4
    /// </summary>
    [Property(MaxTest = 100)]
    public bool EncodeDecode_RoundTripsAllPoints(
        int rawCount, long timestampUs, int rawOrientation, int seed)
    {
        int count = 1 + Math.Abs(rawCount) % 10;
        var orientation = NormalizeOrientation(rawOrientation);

        var points = Enumerable.Range(0, count).Select(i => new TouchPoint
        {
            Id       = i,
            X        = NormalizeCoord(seed + i * 31),
            Y        = NormalizeCoord(seed + i * 57),
            Pressure = NormalizeCoord(seed + i * 13),
            Phase    = NormalizePhase(seed + i),
        }).ToList();

        var original = new TouchEvent
        {
            Points             = points,
            TimestampUs        = timestampUs,
            CurrentOrientation = orientation,
        };

        var decoded = TouchEventCodec.Decode(TouchEventCodec.Encode(original));

        if (decoded is null) return false;
        if (decoded.TimestampUs != original.TimestampUs) return false;
        if (decoded.CurrentOrientation != original.CurrentOrientation) return false;
        if (decoded.Points.Count != original.Points.Count) return false;

        for (int i = 0; i < count; i++)
        {
            var a = original.Points[i];
            var b = decoded.Points[i];

            if (a.Id != b.Id) return false;
            if (a.Phase != b.Phase) return false;
            if (!NearlyEqual(a.X, b.X)) return false;
            if (!NearlyEqual(a.Y, b.Y)) return false;
            if (!NearlyEqual(a.Pressure, b.Pressure)) return false;
        }

        return true;
    }

    // ── Property 14-B: 壊れた入力で落ちない ─────────────────────────────────

    /// <summary>
    /// Property 14-B: 任意のバイト列を渡してもデコーダは例外を投げてはならない。
    ///
    /// タッチチャンネルは信頼できないネットワーク越しに届くため、
    /// 切り詰められたパケットや化けたパケットで接続全体が落ちてはいけない。
    ///
    /// Validates: Requirements 6.1
    /// </summary>
    [Property(MaxTest = 200)]
    public bool Decode_NeverThrows_ForArbitraryBytes(byte[] payload)
    {
        try
        {
            TouchEventCodec.Decode(payload ?? Array.Empty<byte>());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 宣言された点数に対してバイト列が足りないパケットは、
    /// 部分的に読まずに丸ごと捨てなければならない。
    /// 途中まで読むと存在しない指の座標を注入してしまう。
    ///
    /// Validates: Requirements 6.1
    /// </summary>
    [Fact]
    public void Decode_ReturnsNull_WhenPayloadIsTruncated()
    {
        var full = TouchEventCodec.Encode(new TouchEvent
        {
            Points = new List<TouchPoint>
            {
                new() { Id = 0, X = 0.5, Y = 0.5, Pressure = 1.0, Phase = TouchPhase.Began },
                new() { Id = 1, X = 0.2, Y = 0.8, Pressure = 1.0, Phase = TouchPhase.Began },
            },
            TimestampUs = 12345,
            CurrentOrientation = Orientation.Portrait,
        });

        // 末尾を 1 バイト削って「2 点あると宣言しているのに足りない」状態にする
        var truncated = full.AsSpan(0, full.Length - 1).ToArray();

        Assert.Null(TouchEventCodec.Decode(truncated));
    }

    /// <summary>
    /// 範囲外の列挙子コードを含むパケットは捨てなければならない。
    /// 未知のフェーズをそのまま流すと注入側の状態機械が壊れる。
    ///
    /// Validates: Requirements 6.1
    /// </summary>
    [Fact]
    public void Decode_ReturnsNull_ForOutOfRangeEnumCodes()
    {
        var payload = TouchEventCodec.Encode(new TouchEvent
        {
            Points = new List<TouchPoint>
            { new() { Id = 0, X = 0.5, Y = 0.5, Pressure = 1.0, Phase = TouchPhase.Began } },
            TimestampUs = 1,
            CurrentOrientation = Orientation.Portrait,
        });

        var badOrientation = payload.ToArray();
        badOrientation[8] = 99;
        Assert.Null(TouchEventCodec.Decode(badOrientation));

        var badPhase = payload.ToArray();
        badPhase[TouchEventCodec.HeaderSize + 16] = 99;
        Assert.Null(TouchEventCodec.Decode(badPhase));
    }

    /// <summary>
    /// NaN や範囲外の座標は [0.0, 1.0] に丸められなければならない。
    /// そのまま変換行列に通すと注入座標が不定になる。
    ///
    /// Validates: Requirements 6.1
    /// </summary>
    [Fact]
    public void Decode_SanitizesNaNAndOutOfRangeCoordinates()
    {
        var payload = TouchEventCodec.Encode(new TouchEvent
        {
            Points = new List<TouchPoint>
            { new() { Id = 0, X = 0.5, Y = 0.5, Pressure = 0.5, Phase = TouchPhase.Began } },
            TimestampUs = 1,
            CurrentOrientation = Orientation.Portrait,
        });

        // x = NaN, y = 5.0（範囲外）に差し替える
        BitConverter.TryWriteBytes(payload.AsSpan(TouchEventCodec.HeaderSize + 4, 4), float.NaN);
        BitConverter.TryWriteBytes(payload.AsSpan(TouchEventCodec.HeaderSize + 8, 4), 5.0f);

        var decoded = TouchEventCodec.Decode(payload);

        Assert.NotNull(decoded);
        Assert.Equal(0.0, decoded!.Points[0].X);
        Assert.Equal(1.0, decoded.Points[0].Y);
    }

    /// <summary>
    /// Flutter 側が定義するヘッダー 10 バイト・1 点 17 バイトという
    /// レイアウトから外れていないことを固定する。
    /// ここがずれると全タッチイベントが解釈できなくなる。
    ///
    /// Validates: Requirements 6.1
    /// </summary>
    [Fact]
    public void EncodedSize_MatchesWireFormatDefinedByMobileApp()
    {
        Assert.Equal(10, TouchEventCodec.HeaderSize);
        Assert.Equal(17, TouchEventCodec.PointSize);

        var threePoints = TouchEventCodec.Encode(new TouchEvent
        {
            Points = Enumerable.Range(0, 3).Select(i => new TouchPoint
            { Id = i, X = 0.1, Y = 0.2, Pressure = 0.3, Phase = TouchPhase.Moved }).ToList(),
            TimestampUs = 0,
            CurrentOrientation = Orientation.Landscape,
        });

        Assert.Equal(10 + 3 * 17, threePoints.Length);
    }
}
