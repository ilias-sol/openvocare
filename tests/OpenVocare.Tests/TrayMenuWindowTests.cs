using OpenVocare.Views;

namespace OpenVocare.Tests;

public sealed class TrayMenuWindowTests
{
    [Fact]
    public void Dismiss_IsSafeWhenShutdownRequestsOverlap()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                TrayMenuWindow menu = new(() => { }, () => { }, () => { });
                menu.Show();
                menu.Dismiss();
                menu.Dismiss();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The tray-menu shutdown test did not finish.");
        Assert.Null(failure);
    }
}
