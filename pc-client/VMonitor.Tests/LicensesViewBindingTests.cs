using System.Windows;
using VMonitor.UI;

namespace VMonitor.Tests;

public sealed class LicensesViewBindingTests
{
    [Fact]
    public void ReadOnlyNoticeText_CanBeLaidOutWithoutTwoWayBindingException()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new LicensesView();
                view.Measure(new Size(1000, 800));
                view.Arrange(new Rect(0, 0, 1000, 800));
                view.UpdateLayout();
                Assert.NotEmpty(view.NoticeText);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "License view layout timed out");
        Assert.Null(failure);
    }
}
