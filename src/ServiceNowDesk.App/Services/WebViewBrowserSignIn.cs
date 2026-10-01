using System.Windows;
using ServiceNowDesk.Views;

namespace ServiceNowDesk.Services;

public sealed class WebViewBrowserSignIn : IBrowserSignIn
{
    public Task<BrowserSignInResult> SignInAsync(Uri instanceUri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = new BrowserSignInWindow(instanceUri);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
            window.Owner = owner;

        var accepted = window.ShowDialog() == true && window.Result is not null;
        if (!accepted)
        {
            if (string.IsNullOrWhiteSpace(window.Failure))
                throw new BrowserSignInCanceledException();
            throw new InvalidOperationException(window.Failure);
        }

        return Task.FromResult(window.Result!);
    }
}
