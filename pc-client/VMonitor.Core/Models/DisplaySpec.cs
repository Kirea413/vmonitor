namespace VMonitor.Core.Models;

/// <summary>仮想ディスプレイの仕様（解像度・リフレッシュレート・向き・モード）。</summary>
public record DisplaySpec(
    Resolution Resolution,
    int RefreshRateHz,
    Orientation Orientation,
    DisplayMode Mode
);

/// <summary>画面解像度（ピクセル単位）。</summary>
public record Resolution(int Width, int Height)
{
    /// <summary>仮想ディスプレイがサポートする最小解像度。</summary>
    public static readonly Resolution MinSupported = new(640, 480);

    /// <summary>仮想ディスプレイがサポートする最大解像度。</summary>
    public static readonly Resolution MaxSupported = new(3840, 2160);
}

/// <summary>画面の向き。</summary>
public enum Orientation
{
    /// <summary>縦向き（通常）。</summary>
    Portrait,

    /// <summary>横向き（通常）。</summary>
    Landscape,

    /// <summary>縦向き（180° 回転）。</summary>
    PortraitFlipped,

    /// <summary>横向き（180° 回転）。</summary>
    LandscapeFlipped
}

/// <summary>複数ディスプレイのモード設定。</summary>
public enum DisplayMode
{
    /// <summary>複製モード：仮想ディスプレイにメインディスプレイと同じ内容を表示する。</summary>
    Clone,

    /// <summary>拡張モード：仮想ディスプレイをデスクトップの一部として使用する。</summary>
    Extend,

    /// <summary>セカンダリのみ表示モード：メインディスプレイを無効化し仮想ディスプレイのみ使用する。</summary>
    SecondaryOnly
}
