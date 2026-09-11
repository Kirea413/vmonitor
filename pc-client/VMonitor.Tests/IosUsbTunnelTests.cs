using VMonitor.UI;

namespace VMonitor.Tests;

public sealed class IosUsbTunnelTests
{
    [Fact]
    public void FindIproxy_FindsExecutableBesideApplication()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vmonitor-iproxy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            string expected = Path.Combine(directory, "iproxy.exe");
            File.WriteAllBytes(expected, []);

            Assert.Equal(Path.GetFullPath(expected), IosUsbTunnel.FindIproxy(directory, string.Empty, false));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FindIproxy_FindsExecutableOnPath()
    {
        string appDirectory = Path.Combine(Path.GetTempPath(), $"vmonitor-app-{Guid.NewGuid():N}");
        string pathDirectory = Path.Combine(Path.GetTempPath(), $"vmonitor-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(pathDirectory);

        try
        {
            string expected = Path.Combine(pathDirectory, "iproxy.exe");
            File.WriteAllBytes(expected, []);

            Assert.Equal(Path.GetFullPath(expected), IosUsbTunnel.FindIproxy(appDirectory, pathDirectory, false));
        }
        finally
        {
            Directory.Delete(appDirectory, recursive: true);
            Directory.Delete(pathDirectory, recursive: true);
        }
    }

    [Fact]
    public void FindIproxy_ReturnsNullWhenUnavailable()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vmonitor-no-iproxy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            Assert.Null(IosUsbTunnel.FindIproxy(directory, string.Empty, false));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
