#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

#if WINDOWS
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
#endif

namespace CypressLauncher;

internal sealed record EaLoginWindowResult(string? Code, string? Error, bool Cancelled);

#if WINDOWS
/// <summary>
/// Cypress-owned EA OAuth window. WebView2 intercepts EA's qrc callback before
/// the URI can be handed to the operating system, avoiding an external-browser
/// hand-off on hosts where qrc is not registered as an application link.
/// </summary>
internal sealed class EaLoginWindow : Form
{
	private readonly string m_authUrl;
	private readonly string m_redirectUri;
	private readonly WebView2 m_webView;
	private readonly Label m_status;
	private readonly TaskCompletionSource<EaLoginWindowResult> m_tcs;
	private readonly Action<string>? m_progress;
	private readonly string m_logPath;
	private readonly System.Windows.Forms.Timer m_timeoutTimer;
	private bool m_completed;

	private EaLoginWindow(string authUrl, string redirectUri, TaskCompletionSource<EaLoginWindowResult> tcs, Action<string>? progress)
	{
		m_authUrl = authUrl;
		m_redirectUri = redirectUri;
		m_tcs = tcs;
		m_progress = progress;
		m_logPath = Path.Combine(Path.GetTempPath(), "cypress_ea_auth.log");

		Text = "EA Sign In";
		StartPosition = FormStartPosition.CenterScreen;
		MinimumSize = new Size(720, 560);
		Size = new Size(980, 760);
		ShowInTaskbar = true;
		// GameNative can leave newly-created Win32 windows behind its browser
		// surface. Briefly pin the login window to the foreground so the user
		// sees that the sign-in request actually opened.
		TopMost = true;
		Shown += (_, _) =>
		{
			try
			{
				Activate();
				BringToFront();
				BeginInvoke(new Action(() => TopMost = false));
			}
			catch { }
		};

		string iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "cypressicons", "ico", "Burbank-CypressIcon.ico");
		if (File.Exists(iconPath))
			Icon = new Icon(iconPath);

		m_webView = new WebView2 { Dock = DockStyle.Fill };
		m_status = new Label
		{
			Dock = DockStyle.Bottom,
			Height = 30,
			Padding = new Padding(10, 6, 10, 4),
			Text = "Connecting to EA securely…",
			AutoEllipsis = true,
			BackColor = SystemColors.Control
		};
		Controls.Add(m_webView);
		Controls.Add(m_status);

		m_timeoutTimer = new System.Windows.Forms.Timer { Interval = 6 * 60 * 1000 };
		m_timeoutTimer.Tick += (_, _) => Complete(new EaLoginWindowResult(null, "EA sign-in timed out. Please try again.", false));
		Shown += (_, _) =>
		{
			ReportProgress("window-opened");
			m_timeoutTimer.Start();
		};
		Load += OnLoadAsync;
		FormClosed += OnFormClosed;
	}

	internal static Task<EaLoginWindowResult> ShowAsync(string authUrl, string redirectUri, Action<string>? progress = null)
	{
		var tcs = new TaskCompletionSource<EaLoginWindowResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			try
			{
				WinFormsBootstrap.Initialize();
				using var form = new EaLoginWindow(authUrl, redirectUri, tcs, progress);
				Application.Run(form);
			}
			catch (Exception ex)
			{
				tcs.TrySetResult(new EaLoginWindowResult(null, "Could not create the EA sign-in window: " + ex.Message, false));
			}
		});

		thread.IsBackground = true;
		thread.Name = "Cypress.EA.SignIn";
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return tcs.Task;
	}

	private async void OnLoadAsync(object? sender, EventArgs e)
	{
		try
		{
			Log("EA sign-in window opened");
			ReportProgress("preparing-browser");
			string userDataDir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Cypress", "WebView2", "ea-auth");
			Directory.CreateDirectory(userDataDir);

			var envOptions = new CoreWebView2EnvironmentOptions();
			if (Program.IsWineOrGameNative)
				envOptions.AdditionalBrowserArguments = "--disable-gpu --disable-gpu-compositing --disable-features=VizDisplayCompositor";

			var env = await CoreWebView2Environment.CreateAsync(null, userDataDir, envOptions)
				.WaitAsync(TimeSpan.FromSeconds(45));
			await m_webView.EnsureCoreWebView2Async(env).WaitAsync(TimeSpan.FromSeconds(45));

			var core = m_webView.CoreWebView2;
			// Keep the EA session in its own Cypress profile so users can complete
			// MFA/password-manager flows without sharing the general browser profile.
			core.Settings.AreDefaultContextMenusEnabled = true;
			core.Settings.AreDevToolsEnabled = false;
			core.Settings.IsStatusBarEnabled = true;
			core.Settings.IsZoomControlEnabled = true;
			core.ProcessFailed += OnProcessFailed;
			core.SourceChanged += OnSourceChanged;
			core.NavigationCompleted += OnNavigationCompleted;
			core.NavigationStarting += OnNavigationStarting;
			core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
			core.NewWindowRequested += OnNewWindowRequested;
			core.WebResourceResponseReceived += OnWebResourceResponseReceived;


			SetStatus("Loading the secure EA sign-in page…");
			Log("Navigating to EA authentication page");
			ReportProgress("loading-ea");
			core.Navigate(m_authUrl);
		}
		catch (Exception ex)
		{
			Log("Embedded EA sign-in initialisation failed: " + ex.GetType().Name + ": " + ex.Message);
			Complete(new EaLoginWindowResult(null, "Could not initialise the EA sign-in browser: " + ex.Message, false));
		}
	}

	private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
	{
		Log("WebView2 process failed: " + e.ProcessFailedKind);
		Complete(new EaLoginWindowResult(null,
			"The embedded EA browser stopped unexpectedly (" + e.ProcessFailedKind + "). Please try signing in again.", false));
	}

	private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
	{
		try { Log("source changed: " + SafeUrlForLog(m_webView.CoreWebView2?.Source)); } catch { }
	}

	private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
	{
		if (e.IsSuccess)
		{
			SetStatus("EA page ready. Complete sign-in; this window will close when EA confirms it.");
			ReportProgress("ready");
		}
		else
		{
			SetStatus("EA page navigation failed: " + e.WebErrorStatus + ". Check your connection or close this window and try again.");
			ReportProgress("navigation-failed");
		}
		Log("navigation completed: success=" + e.IsSuccess + " status=" + e.WebErrorStatus);
	}

	private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
	{
		Log("navigation starting: " + SafeUrlForLog(e.Uri));
		if (string.IsNullOrEmpty(e.Uri) || !IsRedirectUriMatch(e.Uri))
			return;

		e.Cancel = true;
		HandleRedirectUri(e.Uri);
	}

	private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
	{
		Log("external URI scheme requested: " + SafeUrlForLog(e.Uri));
		if (string.IsNullOrEmpty(e.Uri) || !IsRedirectUriMatch(e.Uri))
			return;

		e.Cancel = true;
		HandleRedirectUri(e.Uri);
	}

	private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
	{
		Log("new window requested: " + SafeUrlForLog(e.Uri));
		if (!string.IsNullOrEmpty(e.Uri) && IsRedirectUriMatch(e.Uri))
		{
			e.Handled = true;
			HandleRedirectUri(e.Uri);
			return;
		}

		e.Handled = true;
		if (!string.IsNullOrEmpty(e.Uri))
			m_webView.CoreWebView2.Navigate(e.Uri);
	}

	private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
	{
		if (m_completed) return;
		try
		{
			var response = e.Response;
			if (response == null || response.StatusCode < 300 || response.StatusCode >= 400)
				return;

			string location = "";
			try { location = response.Headers.GetHeader("Location") ?? ""; } catch { }
			if (!string.IsNullOrEmpty(location) && IsRedirectUriMatch(location))
				HandleRedirectUri(location);
		}
		catch { }
	}

	private void HandleRedirectUri(string uri)
	{
		if (m_completed) return;
		if (!TryExtractAuthorizationCode(uri, m_redirectUri, out string? code, out string? error))
		{
			Log("EA callback arrived without an authorization code");
			Complete(new EaLoginWindowResult(null, error ?? "EA did not return an authorization code.", false));
			return;
		}

		Log("EA callback received an authorization code");
		Complete(new EaLoginWindowResult(code, null, false));
	}

	private bool IsRedirectUriMatch(string uri)
	{
		string normalized = NormalizeRedirectUri(uri);
		if (!normalized.StartsWith(m_redirectUri, StringComparison.OrdinalIgnoreCase))
			return false;
		return normalized.Length == m_redirectUri.Length || normalized[m_redirectUri.Length] is '?' or '#';
	}

	private static string NormalizeRedirectUri(string uri)
	{
		if (string.IsNullOrEmpty(uri)) return uri;
		if (uri.StartsWith("qrc:/", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("qrc:///", StringComparison.OrdinalIgnoreCase))
			return "qrc:///" + uri.Substring("qrc:/".Length);
		return uri;
	}

	internal static bool TryExtractAuthorizationCode(string input, string redirectUri, out string? code, out string? error)
	{
		code = null;
		error = null;
		if (string.IsNullOrWhiteSpace(input))
		{
			error = "EA returned an empty sign-in response. Close this window and try again.";
			return false;
		}

		string value = NormalizeRedirectUri(input.Trim().Trim('"', '\''));
		if (!value.StartsWith(redirectUri, StringComparison.OrdinalIgnoreCase) ||
			(value.Length > redirectUri.Length && value[redirectUri.Length] is not '?' and not '#'))
		{
			error = "EA returned an unexpected sign-in redirect. Close this window and try again.";
			return false;
		}

		int fragmentIndex = value.IndexOf('#');
		string fragment = fragmentIndex >= 0 ? value[(fragmentIndex + 1)..] : "";
		string beforeFragment = fragmentIndex >= 0 ? value[..fragmentIndex] : value;
		int queryIndex = beforeFragment.IndexOf('?');
		string query = queryIndex >= 0 ? beforeFragment[(queryIndex + 1)..] : "";
		var queryValues = HttpUtility.ParseQueryString(query);
		var fragmentValues = HttpUtility.ParseQueryString(fragment.TrimStart('?'));
		string? callbackError = queryValues["error"] ?? fragmentValues["error"];
		if (!string.IsNullOrWhiteSpace(callbackError))
		{
			string description = queryValues["error_description"] ?? fragmentValues["error_description"] ?? callbackError;
			error = "EA sign-in was not completed: " + description;
			return false;
		}

		code = queryValues["code"] ?? fragmentValues["code"];
		if (string.IsNullOrWhiteSpace(code))
		{
			error = "The EA return URL did not contain an authorization code. Please retry sign-in.";
			return false;
		}
		return true;
	}

	private void ReportProgress(string stage)
	{
		try { m_progress?.Invoke(stage); } catch { }
	}

	private void SetStatus(string message)
	{
		if (m_status.IsDisposed) return;
		if (m_status.InvokeRequired)
		{
			try { m_status.BeginInvoke(new Action(() => m_status.Text = message)); } catch { }
		}
		else
			m_status.Text = message;
	}

	private void OnFormClosed(object? sender, FormClosedEventArgs e)
	{
		m_timeoutTimer.Stop();
		m_timeoutTimer.Dispose();
		Log("EA sign-in window closed");
		if (!m_completed)
			m_tcs.TrySetResult(new EaLoginWindowResult(null, null, true));
	}

	private void Complete(EaLoginWindowResult result)
	{
		if (m_completed) return;
		m_completed = true;
		m_timeoutTimer.Stop();
		Log("EA sign-in flow ended: cancelled=" + result.Cancelled + " error=" + (!string.IsNullOrEmpty(result.Error)) + " code=" + (!string.IsNullOrEmpty(result.Code)));
		m_tcs.TrySetResult(result);
		try
		{
			if (IsHandleCreated && !IsDisposed)
				BeginInvoke(new Action(Close));
			else if (!IsDisposed)
				Close();
		}
		catch { }
	}

	private static string SafeUrlForLog(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return "(empty)";
		int query = raw.IndexOfAny(new[] { '?', '#' });
		return query >= 0 ? raw[..query] + "?[redacted]" : raw;
	}

	private void Log(string message)
	{
		try { File.AppendAllText(m_logPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}"); }
		catch { }
	}
}
#endif
