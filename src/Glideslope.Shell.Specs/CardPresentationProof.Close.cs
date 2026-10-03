using System.Reflection;
using Avalonia.Controls;
using Glideslope.App;
using Glideslope.Domain;

namespace Glideslope.Shell.Specs;

internal static partial class CardPresentationProof
{
    /// <summary>
    /// Session-end and application-shutdown closes must bypass the user-close side effect. Drives the card's
    /// real Closing handler with each close reason through Window.HandleClosing, the method Avalonia's platform calls
    /// (private protected, so it is reached by reflection; a rename in a later Avalonia fails this proof loudly).
    /// A user close is cancelled and asks the coordinator to turn the provider off; a session-end or
    /// application-shutdown close goes through and asks nothing.
    /// </summary>
    private static void AssertShutdownCloseIsNotUserClose()
    {
        var handleClosing = typeof(Window).GetMethod("HandleClosing", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(WindowCloseReason)]);
        Assert(handleClosing is not null && handleClosing.ReturnType == typeof(bool),
            "Avalonia still has Window.HandleClosing(WindowCloseReason) returning whether the close was cancelled");
        var window = new ProviderUsageCardWindow(ProviderIds.Codex, showMark: true, _ => { });
        var userCloseRequests = 0;
        window.UserCloseRequested += (_, _) => userCloseRequests++;
        try
        {
            window.Show();
            bool Close(WindowCloseReason reason) => (bool)handleClosing!.Invoke(window, [reason])!;

            Assert(Close(WindowCloseReason.WindowClosing) && userCloseRequests == 1,
                "the card's X (or Alt+F4) is cancelled and asks the coordinator to turn the provider off");
            Assert(!Close(WindowCloseReason.OSShutdown) && userCloseRequests == 1,
                "a Windows restart or shutdown closes the card without asking to turn the provider off");
            Assert(!Close(WindowCloseReason.ApplicationShutdown) && userCloseRequests == 1,
                "a sign-out or lifetime shutdown closes the card without asking to turn the provider off");
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }
}
