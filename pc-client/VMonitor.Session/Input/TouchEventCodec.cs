using System.Buffers.Binary;
using VMonitor.Core.Models;

namespace VMonitor.Session.Input;

/// <summary>
/// スマホアプリ (Flutter) が送信するタッチイベントのバイナリ形式を読み書きする。
/// </summary>
/// <remarks>
/// <para>形式（リトルエンディアン）:</para>
/// <code>
/// ヘッダー (10 バイト)
///   timestamp_us  int64   (8)
///   orientation   uint8   (1)   Orientation の序数
///   point_count   uint8   (1)
/// ポイントごと (17 バイト)
///   id            int32   (4)
///   x             float32 (4)   正規化 [0.0, 1.0]
///   y             float32 (4)   正規化 [0.0, 1.0]
///   pressure      float32 (4)   正規化 [0.0, 1.0]
///   phase         uint8   (1)   TouchPhase の序数
/// </code>
/// <para>
/// 送信側の実装は <c>mobile-app/lib/touch/touch_input_proxy.dart</c> の
/// <c>_serializeEvent</c>。両者は同じ並びの列挙子に依存している。
/// </para>
/// </remarks>
public static class TouchEventCodec
{
    /// <summary>ヘッダー部のバイト数。</summary>
    public const int HeaderSize = 10;

    /// <summary>タッチポイント 1 点あたりのバイト数。</summary>
    /// <remarks>
    /// ペンかどうかを表す 1 バイトを足したため 18 になった。
    /// 古い端末は 17 バイトで送ってくるので、長さを見て両方読む。
    /// </remarks>
    public const int PointSize = 18;

    /// <summary>ペンの区別が無かった頃の 1 点あたりのバイト数。</summary>
    public const int LegacyPointSize = 17;

    /// <summary>
    /// 受信したバイト列をタッチイベントとして読み取る。
    /// 形式が不正な場合は null を返す（不正なパケットで接続を落とさないため）。
    /// </summary>
    public static TouchEvent? Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize)
            return null;

        long timestampUs     = BinaryPrimitives.ReadInt64LittleEndian(payload[..8]);
        byte orientationCode = payload[8];
        byte pointCount      = payload[9];

        // 宣言された点数と実際のバイト数が食い違うパケットは捨てる。
        //
        // 相手が古ければ 1 点 17 バイトで送ってくる。長さから判断して
        // どちらでも読めるようにする。揃わないうちは繋がらない、では
        // 更新の順番に気を遣わせることになる。
        int pointSize = PointSize;

        if (payload.Length < HeaderSize + pointCount * PointSize)
        {
            if (payload.Length < HeaderSize + pointCount * LegacyPointSize)
                return null;

            pointSize = LegacyPointSize;
        }

        if (!TryMapOrientation(orientationCode, out var orientation))
            return null;

        var points = new List<TouchPoint>(pointCount);

        for (int i = 0; i < pointCount; i++)
        {
            var slice = payload.Slice(HeaderSize + i * pointSize, pointSize);

            int   id       = BinaryPrimitives.ReadInt32LittleEndian(slice[..4]);
            float x        = BinaryPrimitives.ReadSingleLittleEndian(slice.Slice(4, 4));
            float y        = BinaryPrimitives.ReadSingleLittleEndian(slice.Slice(8, 4));
            float pressure = BinaryPrimitives.ReadSingleLittleEndian(slice.Slice(12, 4));
            byte  phaseCode = slice[16];

            if (!TryMapPhase(phaseCode, out var phase))
                return null;

            // 古い並びにはこの 1 バイトが無い。その場合は指として扱う。
            bool isPen = pointSize > LegacyPointSize && slice[17] != 0;

            points.Add(new TouchPoint
            {
                Id       = id,
                X        = SanitizeNormalized(x),
                Y        = SanitizeNormalized(y),
                Pressure = SanitizeNormalized(pressure),
                Phase    = phase,
                IsPen    = isPen,
            });
        }

        return new TouchEvent
        {
            Points             = points,
            TimestampUs        = timestampUs,
            CurrentOrientation = orientation,
        };
    }

    /// <summary>
    /// タッチイベントを送信側と同じバイナリ形式へ書き出す。
    /// 主にラウンドトリップ検証と統合テストで使う。
    /// </summary>
    public static byte[] Encode(TouchEvent touchEvent)
    {
        ArgumentNullException.ThrowIfNull(touchEvent);

        int count = touchEvent.Points.Count;
        if (count > byte.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(touchEvent), count, $"タッチポイントは {byte.MaxValue} 点までです。");

        var buffer = new byte[HeaderSize + count * PointSize];
        var span   = buffer.AsSpan();

        BinaryPrimitives.WriteInt64LittleEndian(span[..8], touchEvent.TimestampUs);
        span[8] = (byte)touchEvent.CurrentOrientation;
        span[9] = (byte)count;

        for (int i = 0; i < count; i++)
        {
            var point = touchEvent.Points[i];
            var slice = span.Slice(HeaderSize + i * PointSize, PointSize);

            BinaryPrimitives.WriteInt32LittleEndian(slice[..4], point.Id);
            BinaryPrimitives.WriteSingleLittleEndian(slice.Slice(4, 4),  (float)point.X);
            BinaryPrimitives.WriteSingleLittleEndian(slice.Slice(8, 4),  (float)point.Y);
            BinaryPrimitives.WriteSingleLittleEndian(slice.Slice(12, 4), (float)point.Pressure);
            slice[16] = (byte)point.Phase;
            slice[17] = point.IsPen ? (byte)1 : (byte)0;
        }

        return buffer;
    }

    // ── ヘルパー ─────────────────────────────────────────────────────────

    /// <summary>
    /// NaN・無限大・範囲外の値を [0.0, 1.0] に丸める。
    /// 壊れた値がそのまま座標変換に流れると、注入座標が不定になる。
    /// </summary>
    private static double SanitizeNormalized(float value)
    {
        if (float.IsNaN(value)) return 0.0;
        return Math.Clamp(value, 0f, 1f);
    }

    private static bool TryMapOrientation(byte code, out Orientation orientation)
    {
        if (code <= (byte)Orientation.LandscapeFlipped)
        {
            orientation = (Orientation)code;
            return true;
        }

        orientation = Orientation.Portrait;
        return false;
    }

    private static bool TryMapPhase(byte code, out TouchPhase phase)
    {
        if (code <= (byte)TouchPhase.Cancelled)
        {
            phase = (TouchPhase)code;
            return true;
        }

        phase = TouchPhase.Cancelled;
        return false;
    }
}
