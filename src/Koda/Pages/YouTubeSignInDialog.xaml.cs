using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Koda.Pages;

/// <summary>
/// Signing in to YouTube Music on Google's own page, in a WebView2 with a fresh temporary
/// profile: a second sign-in can never silently be the first one again, and nothing of Google's
/// stays behind once the dialog closes. Koda never sees the password; once Google sends the
/// browser back to YouTube Music, the youtube.com cookies it set are the session.
/// </summary>
public sealed partial class YouTubeSignInDialog : ContentDialog
{
    /// <summary>Through youtube.com's own sign-in handler, as the YouTube website does, which
    /// then sends the browser on to YouTube Music.</summary>
    private const string StartUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true"
        + "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue"
        + "%26app%3Ddesktop%26hl%3Den%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    /// <summary>Google offers passkeys to the page, but the passkey flow dead-ends in an
    /// embedded browser, so hiding the API before Google's scripts look sends it down the
    /// password path. The same script the Mac's sign-in view injects.</summary>
    private const string HidePasskeys = """
        (function () {
            try {
                delete window.PublicKeyCredential;
                Object.defineProperty(window, "PublicKeyCredential", { value: undefined, writable: false, configurable: false });
            } catch (e) {}
            try {
                var credentials = window.navigator && window.navigator.credentials;
                if (!credentials) { return; }
                var prototype = Object.getPrototypeOf(credentials);
                var wrap = function (original) {
                    if (typeof original !== "function") { return original; }
                    return function (options) {
                        if (options && options.publicKey) {
                            return Promise.reject(new DOMException("Passkeys are not available in this app.", "NotAllowedError"));
                        }
                        return original.apply(this, arguments);
                    };
                };
                prototype.get = wrap(prototype.get);
                prototype.create = wrap(prototype.create);
            } catch (e) {}
        })();
        """;

    /// <summary>
    /// Presented as Chrome on Windows, the page gets Google's device-bound session: the
    /// <c>*PSIDTS</c> cookies are short-lived and only renewed by a proof signed with a key kept
    /// in this browser's profile. The profile is deleted when the dialog closes, so the copied
    /// session could never be renewed and was signed out within days. Without the feature the
    /// browser never registers the binding and Google issues an ordinary session, as it does for
    /// the Mac's and Android's sign-in views. Unknown feature names are ignored by the runtime.
    /// </summary>
    private const string NoBoundSessions =
        "--disable-features=DeviceBoundSessions,EnableBoundSessionCredentials,"
        + "EnableBoundSessionCredentialsSoftwareKeysForManualTesting";

    private readonly string _profileFolder;
    private bool _initialized;
    private bool _finishing;
    private bool _closed;
    private bool _switchMode;
    private string? _switchUrl;
    private string? _switchPageId;
    private YouTubeCookies? _switchCookies;
    private int _switchIdentityReads;

    /// <summary>The cookies a successful channel switch produced, when this dialog was opened
    /// in switch mode. Null after a cancel.</summary>
    public YouTubeCookies? Result { get; private set; }

    /// <summary>A switch that produced working cookies but whose playback identity YouTube did
    /// not confirm. The channel's library still moves; history may lag one channel.</summary>
    public string? Warning { get; private set; }

    /// <summary>The outcome of the channel-switch dialog.</summary>
    public sealed record ChannelSwitchResult(YouTubeCookies? Cookies, string? Warning);

    public YouTubeSignInDialog()
    {
        InitializeComponent();
        _profileFolder = Path.Combine(Path.GetTempPath(), "Koda",
            "youtube-signin-" + Guid.NewGuid().ToString("N"));
        Browser.Loaded += OnBrowserLoaded;
        Browser.NavigationCompleted += OnNavigationCompleted;
        Closed += OnClosed;
    }

    /// <summary>The dialog over the shell, sized to the room the window has.</summary>
    public static async Task ShowSignInAsync()
    {
        if (App.Shell?.Content?.XamlRoot is not { } root)
        {
            return;
        }

        var dialog = new YouTubeSignInDialog { XamlRoot = root };
        dialog.Fit(root.Size);
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // Another dialog is up: the sign-in steps aside rather than crashing the app.
            dialog.Cleanup();
        }
    }

    /// <summary>
    /// Selects another channel of the signed-in account. YouTube's own switch URL is loaded in
    /// a fresh WebView with this account's cookies injected; the updated cookies come back if
    /// the page settles on YouTube.
    /// </summary>
    public static async Task<ChannelSwitchResult> SwitchChannelAsync(string signinUrl, YouTubeCookies cookies, string? expectedPageId)
    {
        if (App.Shell?.Content?.XamlRoot is not { } root)
        {
            return new ChannelSwitchResult(null, null);
        }

        var dialog = new YouTubeSignInDialog
        {
            XamlRoot = root,
            Title = "Switch channel",
        };
        dialog._switchMode = true;
        dialog._switchUrl = signinUrl;
        dialog._switchPageId = expectedPageId;
        dialog._switchCookies = cookies;
        dialog.IntroText.Text = "Switching this account to another channel. Koda keeps the same sign-in.";
        dialog.HostLine.Visibility = Visibility.Collapsed;
        dialog.Fit(root.Size);
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            dialog.Cleanup();
            return new ChannelSwitchResult(null, null);
        }

        return new ChannelSwitchResult(dialog.Result, dialog.Warning);
    }

    private void Fit(Windows.Foundation.Size size)
    {
        // The dialog's template caps its border at 548 DIP, so the page is kept under that:
        // Google's two-column sign-in clips on the right when the content is wider than the room.
        var width = Math.Clamp(size.Width - 160, 360, 500);
        var height = Math.Clamp(size.Height - 240, 260, 640);
        Layout.Width = width;
        Layout.Height = height;
        MaxWidth = width + 80;
        MaxHeight = height + 180;
    }

    private async void OnBrowserLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _closed) return;
        _initialized = true;
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            string version;
            try
            {
                version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception)
            {
                ShowError("The sign-in page needs Microsoft Edge WebView2, which isn't installed. "
                    + "Install it from Microsoft's website, then try again.");
                return;
            }

            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null!,
                userDataFolder: _profileFolder,
                options: new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = NoBoundSessions,
                });
            if (_closed) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (_closed || Browser.CoreWebView2 is null) return;

            var settings = Browser.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            // Google turns away browsers it takes for embedded ones. The WebView2 engine is
            // Chromium, so it presents Chrome's own Windows identity, as the Mac's view presents
            // Safari's. The major comes from the installed runtime, not a version that ages out.
            if (version.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } major)
            {
                settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                    + "AppleWebKit/537.36 (KHTML, like Gecko) "
                    + $"Chrome/{major}.0.0.0 Safari/537.36";
            }

            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                Browser.CoreWebView2?.Navigate(args.Uri);
            };
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HidePasskeys);
            if (_closed) return;

            if (_switchMode)
            {
                if (_switchCookies is not null)
                {
                    foreach (var cookie in _switchCookies.Cookies)
                    {
                        try
                        {
                            var injected = Browser.CoreWebView2.CookieManager.CreateCookie(
                                cookie.Name, cookie.Value, ".youtube.com", "/");
                            injected.IsSecure = true;
                            injected.IsHttpOnly = true;
                            Browser.CoreWebView2.CookieManager.AddOrUpdateCookie(injected);
                        }
                        catch (Exception)
                        {
                            // A cookie the engine won't take is not worth failing the switch over.
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(_switchUrl))
                {
                    Browser.CoreWebView2.Navigate(_switchUrl);
                }

                return;
            }

            Browser.CoreWebView2.Navigate(StartUrl);
        }
        catch (Exception)
        {
            if (!_closed)
            {
                ShowError("The sign-in page couldn't open. Check the connection and try again.");
            }
        }
    }

    /// <summary>Each page that finishes on YouTube is a chance the session's cookies are there.</summary>
    private async void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_closed || _finishing || !args.IsSuccess || Browser.CoreWebView2 is null) return;
        if (!Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var uri)) return;
        if (!IsYouTubeHost(uri.Host)) return;

        // The switch URL's own page is not the answer; its redirect (or the delegation it set)
        // is. Give that one navigation a moment before reading the jar.
        if (_switchMode && uri.AbsolutePath.Equals("/signin", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(600);
            if (!_closed) await ReadSessionAsync();
            return;
        }

        await ReadSessionAsync();
    }

    private async Task ReadSessionAsync()
    {
        if (_closed || _finishing || Browser.CoreWebView2 is null) return;

        YouTubeCookies cookies;
        try
        {
            var reported = await Browser.CoreWebView2.CookieManager
                .GetCookiesAsync("https://music.youtube.com/");
            cookies = YouTubeCookies.FromWebView(reported.Select(cookie => new YouTubeCookies.WebCookie(
                cookie.Name,
                cookie.Value,
                cookie.Domain,
                !cookie.IsSession && cookie.Expires > 0 && cookie.Expires < 253402300799
                    ? DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires)
                    : null)));
        }
        catch (Exception)
        {
            return;
        }

        // The five cookies Google sets on a real sign-in, not just a jar with look-alikes.
        if (_closed || _finishing || !cookies.HasSignInCookies) return;

        if (_switchMode)
        {
            // The switch is committed by the page's own JavaScript; DATASYNC_ID is the
            // identity the next page will use. Wait for it to name the requested channel.
            if (!await MatchesIdentityAsync())
            {
                if (_switchIdentityReads++ < 40)
                {
                    await Task.Delay(500);
                    if (!_closed) await ReadSessionAsync();
                    return;
                }

                // The cookies still work and onBehalfOfUser carries the library; only the
                // cookie-bound playback history may lag. Say so rather than block the switch.
                Warning = "YouTube didn't confirm the playback identity. The channel's library "
                    + "will switch, but history may still go to the previous channel.";
            }

            Result = cookies;
            _finishing = true;
            if (!_closed) Hide();
            return;
        }

        _finishing = true;
        ShowBusy();
        try
        {
            await EngineHost.Account.CompleteSignInAsync(cookies);
            if (!_closed) Hide();
        }
        catch (YouTubeSignInException error)
        {
            _finishing = false;
            if (!_closed) ShowError(error.Message);
        }
        catch (MusicException error)
        {
            _finishing = false;
            if (!_closed) ShowError(SignInProblem(error));
        }
        catch (Exception)
        {
            _finishing = false;
            if (!_closed) ShowError("YouTube Music didn't accept the sign-in. Try again in a moment.");
        }
    }

    /// <summary>Whether the page's <c>ytcfg.DATASYNC_ID</c> names the requested channel. The
    /// delegated half is the brand page id, or empty for the primary channel.</summary>
    private async Task<bool> MatchesIdentityAsync()
    {
        if (Browser.CoreWebView2 is null)
        {
            return false;
        }

        try
        {
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync(
                "(function(){try{var c=window.ytcfg;if(!c)return null;"
                + "if(c.get){var v=c.get('DATASYNC_ID');if(v)return v;}"
                + "return c.DATASYNC_ID||null}catch(e){return null}})()");
            if (string.IsNullOrEmpty(raw) || raw == "null")
            {
                return false;
            }

            var id = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            var parts = id.Split("||");
            if (string.IsNullOrEmpty(_switchPageId))
            {
                // Primary is "<userSessionId>||": the delegated half is the user, and the
                // second half is empty. A single id is an older primary shape.
                return parts.Length < 2 || string.IsNullOrEmpty(parts[1]);
            }

            return parts.Length >= 2 &&
                string.Equals(parts[0], _switchPageId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string SignInProblem(MusicException error) => error.Kind switch    {
        MusicException.Reason.SignedOut => "YouTube didn't accept the sign-in. Try again in a moment.",
        MusicException.Reason.Network => "Koda couldn't reach YouTube. Check the connection and try again.",
        MusicException.Reason.RateLimited => "YouTube asked Koda to wait a moment. Try again shortly.",
        _ => "YouTube Music didn't accept the sign-in. Try again in a moment.",
    };

    private async void OnTryAgainClick(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        _finishing = false;
        ShowBrowser();

        if (Browser.CoreWebView2 is null)
        {
            _initialized = false;
            await InitializeAsync();
            return;
        }

        try
        {
            // A clean jar, as the first attempt had: a half-finished Google session must not
            // decide the next one.
            await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
            if (_closed) return;
            Browser.CoreWebView2.Navigate(StartUrl);
        }
        catch (Exception)
        {
            if (!_closed)
            {
                ShowError("The sign-in page couldn't be opened again. Close this and try once more.");
            }
        }
    }

    private void ShowBrowser()
    {
        Browser.Visibility = Visibility.Visible;
        BusyShade.Visibility = Visibility.Collapsed;
        ErrorShade.Visibility = Visibility.Collapsed;
    }

    private void ShowBusy()
    {
        Browser.Visibility = Visibility.Collapsed;
        BusyShade.Visibility = Visibility.Visible;
        ErrorShade.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        Browser.Visibility = Visibility.Collapsed;
        BusyShade.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorShade.Visibility = Visibility.Visible;
    }

    private static bool IsYouTubeHost(string host) =>
        host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => Cleanup();

    /// <summary>The browser and its profile go; the profile's files can take a moment to let go.</summary>
    private void Cleanup()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            Browser.Close();
        }
        catch (Exception)
        {
            // Already gone, or never started.
        }
        DeleteLater(_profileFolder);
    }

    private static void DeleteLater(string folder) => _ = Task.Run(async () =>
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception)
            {
                await Task.Delay(300);
            }
        }
    });
}
