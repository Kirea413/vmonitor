using System.IO;
using System.Windows.Controls;

namespace VMonitor.UI;

/// <summary>PCアプリが利用する第三者ソフトウェアとライセンスを表示する。</summary>
public partial class LicensesView : UserControl
{
    public sealed record Component(string Name, string License);

    public IReadOnlyList<Component> Components { get; } =
    [
        new("LibUsbDotNet", "LGPL-3.0-or-later"),
        new("Makaretu.Dns.Multicast", "MIT"),
        new("Common.Logging", "Apache-2.0"),
        new("IPNetwork2", "BSD-2-Clause"),
        new("Vortice.Windows", "MIT"),
        new("SharpGen.Runtime", "MIT"),
        new("SimpleBase", "Apache-2.0"),
        new("Tmds.LibC", "MIT"),
        new("Microsoft .NET Runtime", "MIT"),
        new("UsbDk", "Apache-2.0"),
        new("iproxy", "GPL-2.0"),
        new("libusbmuxd", "LGPL-2.1"),
        new("libimobiledevice-glue", "LGPL-2.1"),
        new("libplist", "LGPL-2.1"),
    ];

    public string NoticeText { get; }

    public LicensesView()
    {
        NoticeText = LoadNotices();
        InitializeComponent();
    }

    internal static string LoadNotices(string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        string path = Path.Combine(baseDirectory, "THIRD-PARTY-NOTICES.md");

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "ライセンス情報を読み込めませんでした。\n\n" +
                   $"参照先: {path}\n理由: {ex.Message}";
        }
    }
}
