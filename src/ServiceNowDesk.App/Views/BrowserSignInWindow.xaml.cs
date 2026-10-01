using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using ServiceNowDesk.Client;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.Views;

public partial class BrowserSignInWindow : Window
{
    private const string TokenScript = """
        (() => {
          try {
            if (typeof g_ck === 'string' && g_ck) return g_ck;
          } catch (e) {}
          try {
            const html = document.documentElement ? document.documentElement.innerHTML : '';
            const match = html.match(/g_ck\s*=\s*['"]([^'"]+)['"]/);
            if (match) return match[1];
          } catch (e) {}
          return '';
        })()
        """;

    private readonly Uri _instance;
    private int _classicAttempt;
    private bool _probing;
    private bool _completed;

    public BrowserSignInWindow(Uri instanceUri)
    {
        _instance = instanceUri;
        InitializeComponent();
    }

    public BrowserSignInResult? Result { get; private set; }
    public string? Failure { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServiceNowDesk", "WebView2");
            Directory.CreateDirectory(folder);
            var environment = await CoreWebView2Environment.CreateAsync(null, folder);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target))
                {
                    args.Handled = true;
                    Browser.CoreWebView2.Navigate(target.AbsoluteUri);
                }
            };
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Browser.Source = _instance;
        }
        catch (Exception ex)
        {
            Failure = "The sign-in window could not start. Install the Microsoft Edge WebView2 runtime and try again. " + ex.Message;
            StatusText.Text = Failure;
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _completed || _probing)
            return;

        _probing = true;
        try
        {
            await ProbeAsync(allowClassicNavigation: true);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _probing = false;
        }
    }

    private async void UseSignIn_Click(object sender, RoutedEventArgs e)
    {
        _classicAttempt = 2;
        try
        {
            await ProbeAsync(allowClassicNavigation: false);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private async Task ProbeAsync(bool allowClassicNavigation)
    {
        if (_completed || Browser.CoreWebView2 is null)
            return;

        var source = Browser.Source;
        if (source is null || !source.Host.Equals(_instance.Host, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Finish signing in with your company account.";
            return;
        }

        var raw = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(_instance.AbsoluteUri);
        var cookies = raw.Select(cookie => new BrowserCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path)).ToArray();
        if (!BrowserSessionCookies.HasServiceNowSession(cookies))
        {
            StatusText.Text = "Waiting for the company sign-in to finish.";
            return;
        }

        var token = await ReadTokenAsync();
        if (token.Length == 0 && allowClassicNavigation && _classicAttempt < 2)
        {
            _classicAttempt++;
            StatusText.Text = "Signed in. Opening the incident list to read the form token.";
            var path = _classicAttempt == 1 ? "incident_list.do?sysparm_stack=no" : "navpage.do";
            _probing = false;
            Browser.CoreWebView2.Navigate(new Uri(_instance, path).AbsoluteUri);
            return;
        }

        if (token.Length == 0)
        {
            StatusText.Text = "ServiceNow accepted the sign-in, but did not share a session token. Open an incident list in this window, then click Use this sign-in.";
            return;
        }

        try
        {
            var header = BrowserSessionCookies.BuildHeader(cookies, _instance);
            Result = new BrowserSignInResult(header, BrowserSessionCookies.NormalizeToken(token));
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        _completed = true;
        DialogResult = true;
    }

    private async Task<string> ReadTokenAsync()
    {
        try
        {
            var json = await Browser.CoreWebView2.ExecuteScriptAsync(TokenScript);
            if (string.IsNullOrWhiteSpace(json) || json == "null")
                return "";
            return JsonSerializer.Deserialize<string>(json)?.Trim() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
