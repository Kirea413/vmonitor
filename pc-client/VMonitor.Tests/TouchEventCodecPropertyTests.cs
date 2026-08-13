using System.Buffers.Binary;
// Feature: vmonitor, Property 14: 繧ｿ繝・メ繧､繝吶Φ繝医・螳悟・霆｢騾・
using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Models;
using VMonitor.Session.Input;

namespace VMonitor.Tests;

/// <summary>
/// Property 14: 繧ｿ繝・メ繧､繝吶Φ繝医・螳悟・霆｢騾・/// Validates: Requirements 6.1, 6.4
///
/// 繧ｹ繝槭・蛛ｴ縺碁∽ｿ｡縺励◆繧ｿ繝・メ繧､繝吶Φ繝医・縲￣C 蛛ｴ縺ｧ谺關ｽ縺ｪ縺丞ｾｩ蜈・＆繧後↑縺代ｌ縺ｰ縺ｪ繧峨↑縺・・/// 1 轤ｹ縺ｧ繧り誠縺｡繧九→縲√・繝ｫ繝√ち繝・メ縺ｮ繧ｸ繧ｧ繧ｹ繝√Ε繝ｼ縺悟挨縺ｮ繧ｸ繧ｧ繧ｹ繝√Ε繝ｼ縺ｨ縺励※
/// 隗｣驥医＆繧後◆繧翫∵欠縺梧款縺輔ｌ縺｣縺ｱ縺ｪ縺励↓縺ｪ縺｣縺溘ｊ縺吶ｋ縲・///
/// 騾∽ｿ｡蛛ｴ縺ｮ蠖｢蠑上・ mobile-app/lib/touch/touch_input_proxy.dart 縺ｮ
/// _serializeEvent 縺悟ｮ夂ｾｩ縺励※縺翫ｊ縲∵悽繝・せ繝医・縺昴・蠖｢蠑上→縺ｮ謨ｴ蜷医ｒ蝗ｺ螳壹☆繧九・/// </summary>
public class TouchEventCodecPropertyTests
{
    // 笏笏 繝倥Ν繝代・ 笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏

    private static double NormalizeCoord(int raw) => Math.Abs(raw) % 10001 / 10000.0;

    private static TouchPhase NormalizePhase(int raw) => (TouchPhase)(Math.Abs(raw) % 4);

    private static Orientation NormalizeOrientation(int raw) => (Orientation)(Math.Abs(raw) % 4);

    /// <summary>
    /// float32 縺ｧ蠕蠕ｩ縺吶ｋ縺溘ａ縲∵ｯ碑ｼ・・蜊倡ｲｾ蠎ｦ縺ｮ荳ｸ繧∬ｪ､蟾ｮ繧定ｨｱ螳ｹ縺吶ｋ縲・    /// 豁｣隕丞喧蠎ｧ讓吶・ [0,1] 縺ｪ縺ｮ縺ｧ邨ｶ蟇ｾ隱､蟾ｮ 1e-6 縺ｧ蜊∝・縲・    /// </summary>
    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 1e-6;

    // 笏笏 Property 14-A: 繝ｩ繧ｦ繝ｳ繝峨ヨ繝ｪ繝・・ 笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏

    /// <summary>
    /// Property 14-A: 莉ｻ諢上・繧ｿ繝・メ繧､繝吶Φ繝医ｒ繧ｨ繝ｳ繧ｳ繝ｼ繝峨＠縺ｦ繝・さ繝ｼ繝峨☆繧九→縲・    /// 縺吶∋縺ｦ縺ｮ繧ｿ繝・メ繝昴う繝ｳ繝医′蜷後§ ID繝ｻ蠎ｧ讓吶・蝨ｧ蜉帙・繝輔ぉ繝ｼ繧ｺ縺ｧ蠕ｩ蜈・＆繧後↑縺代ｌ縺ｰ縺ｪ繧峨↑縺・・    ///
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

    // 笏笏 Property 14-B: 螢翫ｌ縺溷・蜉帙〒關ｽ縺｡縺ｪ縺・笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏笏

    /// <summary>
    /// Property 14-B: 莉ｻ諢上・繝舌う繝亥・繧呈ｸ｡縺励※繧ゅョ繧ｳ繝ｼ繝縺ｯ萓句､悶ｒ謚輔￡縺ｦ縺ｯ縺ｪ繧峨↑縺・・    ///
    /// 繧ｿ繝・メ繝√Ε繝ｳ繝阪Ν縺ｯ菫｡鬆ｼ縺ｧ縺阪↑縺・ロ繝・ヨ繝ｯ繝ｼ繧ｯ雜翫＠縺ｫ螻翫￥縺溘ａ縲・    /// 蛻・ｊ隧ｰ繧√ｉ繧後◆繝代こ繝・ヨ繧・喧縺代◆繝代こ繝・ヨ縺ｧ謗･邯壼・菴薙′關ｽ縺｡縺ｦ縺ｯ縺・￠縺ｪ縺・・    ///
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
    /// 螳｣險縺輔ｌ縺溽せ謨ｰ縺ｫ蟇ｾ縺励※繝舌う繝亥・縺瑚ｶｳ繧翫↑縺・ヱ繧ｱ繝・ヨ縺ｯ縲・    /// 驛ｨ蛻・噪縺ｫ隱ｭ縺ｾ縺壹↓荳ｸ縺斐→謐ｨ縺ｦ縺ｪ縺代ｌ縺ｰ縺ｪ繧峨↑縺・・    /// 騾比ｸｭ縺ｾ縺ｧ隱ｭ繧縺ｨ蟄伜惠縺励↑縺・欠縺ｮ蠎ｧ讓吶ｒ豕ｨ蜈･縺励※縺励∪縺・・    ///
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

        // 譛ｫ蟆ｾ繧・1 繝舌う繝亥炎縺｣縺ｦ縲・ 轤ｹ縺ゅｋ縺ｨ螳｣險縺励※縺・ｋ縺ｮ縺ｫ雜ｳ繧翫↑縺・咲憾諷九↓縺吶ｋ
        var truncated = full.AsSpan(0, full.Length - 1).ToArray();

        Assert.Null(TouchEventCodec.Decode(truncated));
    }

    /// <summary>
    /// 遽・峇螟悶・蛻玲嫌蟄舌さ繝ｼ繝峨ｒ蜷ｫ繧繝代こ繝・ヨ縺ｯ謐ｨ縺ｦ縺ｪ縺代ｌ縺ｰ縺ｪ繧峨↑縺・・    /// 譛ｪ遏･縺ｮ繝輔ぉ繝ｼ繧ｺ繧偵◎縺ｮ縺ｾ縺ｾ豬√☆縺ｨ豕ｨ蜈･蛛ｴ縺ｮ迥ｶ諷区ｩ滓｢ｰ縺悟｣翫ｌ繧九・    ///
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
    /// NaN 繧・ｯ・峇螟悶・蠎ｧ讓吶・ [0.0, 1.0] 縺ｫ荳ｸ繧√ｉ繧後↑縺代ｌ縺ｰ縺ｪ繧峨↑縺・・    /// 縺昴・縺ｾ縺ｾ螟画鋤陦悟・縺ｫ騾壹☆縺ｨ豕ｨ蜈･蠎ｧ讓吶′荳榊ｮ壹↓縺ｪ繧九・    ///
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

        // x = NaN, y = 5.0・育ｯ・峇螟厄ｼ峨↓蟾ｮ縺玲崛縺医ｋ
        BitConverter.TryWriteBytes(payload.AsSpan(TouchEventCodec.HeaderSize + 4, 4), float.NaN);
        BitConverter.TryWriteBytes(payload.AsSpan(TouchEventCodec.HeaderSize + 8, 4), 5.0f);

        var decoded = TouchEventCodec.Decode(payload);

        Assert.NotNull(decoded);
        Assert.Equal(0.0, decoded!.Points[0].X);
        Assert.Equal(1.0, decoded.Points[0].Y);
    }

    /// <summary>
    /// Flutter 蛛ｴ縺悟ｮ夂ｾｩ縺吶ｋ繝倥ャ繝繝ｼ 10 繝舌う繝医・1 轤ｹ 18 繝舌う繝医→縺・≧
    /// 繝ｬ繧､繧｢繧ｦ繝医°繧牙､悶ｌ縺ｦ縺・↑縺・％縺ｨ繧貞崋螳壹☆繧九・    /// ・・7 縺縺｣縺溘′縲√・繝ｳ縺九←縺・°縺ｮ 1 繝舌う繝医ｒ雜ｳ縺励※ 18 縺ｫ縺ｪ縺｣縺滂ｼ・    /// 縺薙％縺後★繧後ｋ縺ｨ蜈ｨ繧ｿ繝・メ繧､繝吶Φ繝医′隗｣驥医〒縺阪↑縺上↑繧九・    ///
    /// Validates: Requirements 6.1
    /// </summary>
    [Fact]
    public void EncodedSize_MatchesWireFormatDefinedByMobileApp()
    {
        Assert.Equal(10, TouchEventCodec.HeaderSize);
        Assert.Equal(20, TouchEventCodec.PointSize);
        Assert.Equal(18, TouchEventCodec.PointSizeWithoutTilt);
        Assert.Equal(17, TouchEventCodec.LegacyPointSize);

        var threePoints = TouchEventCodec.Encode(new TouchEvent
        {
            Points = Enumerable.Range(0, 3).Select(i => new TouchPoint
            { Id = i, X = 0.1, Y = 0.2, Pressure = 0.3, Phase = TouchPhase.Moved }).ToList(),
            TimestampUs = 0,
            CurrentOrientation = Orientation.Landscape,
        });

        Assert.Equal(10 + 3 * TouchEventCodec.PointSize, threePoints.Length);
    }

    /// <summary>
    /// 繝壹Φ縺九←縺・°縺悟ｾ蠕ｩ縺吶ｋ縺薙→縲・    ///
    /// Windows 縺ｯ繧ｿ繝・メ縺ｨ繝壹Φ繧貞挨縺ｮ蜈･蜉帙→縺励※謇ｱ縺・◆繧√√％縺薙′關ｽ縺｡繧九→
    /// Apple Pencil 縺ｧ謠上＞縺ｦ繧ゅ悟､ｪ縺・欠縲阪↓縺励°縺ｪ繧峨↑縺・・    /// </summary>
    [Fact]
    public void Decode_PreservesPenFlag()
    {
        var encoded = TouchEventCodec.Encode(new TouchEvent
        {
            Points = new[]
            {
                new TouchPoint
                {
                    Id = 1, X = 0.5, Y = 0.5, Pressure = 0.8,
                    Phase = TouchPhase.Moved, IsPen = true,
                },
            },
            TimestampUs = 0,
            CurrentOrientation = Orientation.Portrait,
        });

        var decoded = TouchEventCodec.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.True(decoded!.Points[0].IsPen);
    }

    /// <summary>
    /// 繝壹Φ縺ｮ蛹ｺ蛻･縺檎┌縺九▲縺滄・・荳ｦ縺ｳ・・ 轤ｹ 17 繝舌う繝茨ｼ峨ｂ隱ｭ繧√ｋ縺薙→縲・    ///
    /// 遶ｯ譛ｫ縺ｨ繧､繝ｳ繧ｹ繝医・繝ｩ繝ｼ繧貞酔譎ゅ↓蜈･繧梧崛縺医ｉ繧後ｋ縺ｨ縺ｯ髯舌ｉ縺ｪ縺・・    /// 迚・婿縺縺第眠縺励＞迥ｶ諷九〒郢九′繧峨↑縺上↑繧九→縲∵峩譁ｰ縺ｮ鬆・分縺ｫ豌励ｒ驕｣繧上○繧九・    /// </summary>
    [Fact]
    public void Decode_AcceptsLegacyLayoutWithoutPenByte()
    {
        const int pointCount = 2;

        var payload = new byte[TouchEventCodec.HeaderSize
                               + pointCount * TouchEventCodec.LegacyPointSize];

        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), 123);
        payload[8] = (byte)Orientation.Portrait;
        payload[9] = pointCount;

        for (int i = 0; i < pointCount; i++)
        {
            var slice = payload.AsSpan(
                TouchEventCodec.HeaderSize + i * TouchEventCodec.LegacyPointSize,
                TouchEventCodec.LegacyPointSize);

            BinaryPrimitives.WriteInt32LittleEndian(slice[..4], i);
            BinaryPrimitives.WriteSingleLittleEndian(slice.Slice(4, 4), 0.25f);
            BinaryPrimitives.WriteSingleLittleEndian(slice.Slice(8, 4), 0.75f);
            BinaryPrimitives.WriteSingleLittleEndian(slice.Slice(12, 4), 0.5f);
            slice[16] = (byte)TouchPhase.Moved;
        }

        var decoded = TouchEventCodec.Decode(payload);

        Assert.NotNull(decoded);
        Assert.Equal(pointCount, decoded!.Points.Count);

        // 遞ｮ蛻･縺檎┌縺・ｸｦ縺ｳ縺ｯ謖・→縺励※謇ｱ縺・        Assert.All(decoded.Points, p => Assert.False(p.IsPen));
    }
}
