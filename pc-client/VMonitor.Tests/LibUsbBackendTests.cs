using System.Reflection;
using VMonitor.Session.Transport;
using Xunit;

namespace VMonitor.Tests;

/// <summary>
/// UsbDk を使うかどうかの判断を確かめる。
///
/// 通常モードの Android は MTP ドライバの持ち物になっていることが多く、
/// 既定の経路では開けない。UsbDk はそこを迂回できるが、入っていない
/// のに指定すると libusb の初期化そのものが失敗する。いま使えている
/// 人を巻き添えにしてはいけない。
/// </summary>
[Collection("LibUsbBackend")]
public sealed class LibUsbBackendTests : IDisposable
{
    private readonly Func<bool> _original = LibUsbBackend.UsbDkDetector;
    private readonly Func<string?> _originalProbe = LibUsbBackend.UsbDkProbe;
    private readonly Func<int> _originalSetter = LibUsbBackend.UsbDkOptionSetter;

    public void Dispose()
    {
        LibUsbBackend.UsbDkDetector = _original;
        LibUsbBackend.UsbDkProbe = _originalProbe;
        LibUsbBackend.UsbDkOptionSetter = _originalSetter;
        ResetDecision();
    }

    /// <summary>判断は一度きりなので、テストごとに巻き戻す。</summary>
    private static void ResetDecision()
    {
        var type = typeof(LibUsbBackend);

        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;

        type.GetField("_decided",      flags)!.SetValue(null, false);
        type.GetField("_usbDkEnabled", flags)!.SetValue(null, false);
        type.GetField("_fallbackReason", flags)!.SetValue(null, null);
    }

    [Fact]
    public void 入っていなければ既定の経路のまま()
    {
        ResetDecision();
        LibUsbBackend.UsbDkDetector = () => false;

        LibUsbBackend.Prepare();

        Assert.False(LibUsbBackend.IsUsbDkEnabled);
        Assert.Contains("WinUSB", LibUsbBackend.Describe());
    }

    [Fact]
    public void 調べる途中で投げても既定の経路へ倒れる()
    {
        ResetDecision();
        LibUsbBackend.UsbDkDetector = () => throw new InvalidOperationException("壊れた");

        // ここで投げると USB 接続そのものが始まらない。飲み込むこと。
        var thrown = Record.Exception(LibUsbBackend.Prepare);

        Assert.Null(thrown);
        Assert.False(LibUsbBackend.IsUsbDkEnabled);
    }

    [Fact]
    public void 判断は一度だけ()
    {
        ResetDecision();

        int calls = 0;
        LibUsbBackend.UsbDkDetector = () => { calls++; return false; };

        LibUsbBackend.Prepare();
        LibUsbBackend.Prepare();
        LibUsbBackend.Prepare();

        // libusb のオプションは文脈を作る前に決める必要がある。
        // 毎回調べ直すと、文脈を作るたびに判断が変わりうる。
        Assert.Equal(1, calls);
    }

    [Fact]
    public void InstalledButUnusableUsbDk_DoesNotPoisonDefaultContext()
    {
        ResetDecision();
        LibUsbBackend.UsbDkDetector = () => true;
        LibUsbBackend.UsbDkProbe = () => "UsbDk初期化エラー -5";
        int setters = 0;
        LibUsbBackend.UsbDkOptionSetter = () => { setters++; return 0; };
        LibUsbBackend.Prepare();
        Assert.False(LibUsbBackend.IsUsbDkEnabled);
        Assert.Equal(0, setters);
        Assert.Contains("WinUSB", LibUsbBackend.Describe());
        Assert.Contains("-5", LibUsbBackend.Describe());
    }

    [Fact]
    public void WorkingUsbDk_IsEnabledOnlyAfterProbe()
    {
        ResetDecision();
        LibUsbBackend.UsbDkDetector = () => true;
        bool probed = false;
        LibUsbBackend.UsbDkProbe = () => { probed = true; return null; };
        LibUsbBackend.UsbDkOptionSetter = () => { Assert.True(probed); return 0; };
        LibUsbBackend.Prepare();
        Assert.True(LibUsbBackend.IsUsbDkEnabled);
    }

    [Fact]
    public void ProbeThrows_FallsBackWithoutSettingGlobalOption()
    {
        ResetDecision();
        LibUsbBackend.UsbDkDetector = () => true;
        LibUsbBackend.UsbDkProbe = () => throw new EntryPointNotFoundException("libusb_init_context");
        LibUsbBackend.UsbDkOptionSetter = () => throw new Exception("must not be called");
        Assert.Null(Record.Exception(LibUsbBackend.Prepare));
        Assert.False(LibUsbBackend.IsUsbDkEnabled);
        Assert.Contains("libusb_init_context", LibUsbBackend.Describe());
    }
}
