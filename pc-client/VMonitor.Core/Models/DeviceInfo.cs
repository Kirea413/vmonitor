namespace VMonitor.Core.Models;

/// <summary>スマートフォンのデバイス情報。</summary>
public record DeviceInfo(
    DeviceIdentifier Id,
    string Name,
    DevicePlatform Platform,
    Resolution PhysicalResolution,
    float PixelDensity
);

/// <summary>信頼済みとして登録されたデバイスの情報。</summary>
public record TrustedDevice(
    DeviceIdentifier Id,
    string Name,
    DateTimeOffset TrustedAt,
    DateTimeOffset? LastConnectedAt
);

/// <summary>デバイスを一意に識別する UUID ラッパー。スマホが生成・保存する。</summary>
public readonly record struct DeviceIdentifier(Guid Value)
{
    /// <summary>新しいランダムな DeviceIdentifier を生成する。</summary>
    public static DeviceIdentifier NewIdentifier() => new(Guid.NewGuid());

    /// <summary>
    /// 文字列から、いつ呼んでも同じになる DeviceIdentifier を作る。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 同じ端末を挿し直したときに同じ識別子になってほしい場面で使う。
    /// 毎回 <see cref="NewIdentifier"/> を呼ぶと、繋ぎ直すたびに別の端末として
    /// 扱われ、接続候補の一覧に同じスマホが何台も並んでしまう。
    /// </para>
    /// <para>
    /// USB のシリアル番号のように、その端末について安定している値を渡すこと。
    /// </para>
    /// </remarks>
    public static DeviceIdentifier FromKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // 名前から決まる UUID が欲しいだけで、秘密は扱わない。
        // 16 バイトをそのまま Guid にできる MD5 を使う。
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(key));

        return new DeviceIdentifier(new Guid(hash));
    }

    /// <summary>文字列形式の UUID から DeviceIdentifier を解析する。</summary>
    public static DeviceIdentifier Parse(string value) => new(Guid.Parse(value));

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}

/// <summary>デバイスのプラットフォーム種別。</summary>
public enum DevicePlatform
{
    /// <summary>iOS デバイス。</summary>
    iOS,

    /// <summary>Android デバイス。</summary>
    Android
}
