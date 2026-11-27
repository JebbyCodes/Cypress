#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace CypressLauncher;

// Native replacement for BrowserFallbackHost on Wine/GameNative.
//
// The downloaded SimpleWebView2Browser.exe is a thin WebView2 (Chromium) host.
// Under Wine, Chromium's GPU process cannot present its D3D11 shared-texture
// surface, which produces the white window that only partially repaints after
// a click. The loopback server, the message bridge and the HTML are all fine -
// the failure is the Chromium compositor present step.
//
// WinForms renders through GDI/User32 and never touches DirectComposition or
// D3D, so it sidesteps that failure entirely. It needs no new NuGet packages:
// CypressLauncher.csproj already sets <UseWindowsForms>true</UseWindowsForms>.
//
// The backend (MessageHandler) is unchanged. This class only replaces the
// render and message-pump layer, using the same contract the loopback bridge
// used: SetBrowserMessageSender / HandleMessage(null, json) / external opener.
internal sealed class NativeFallbackHost : IDisposable
{
	private readonly MessageHandler m_handler;
	private readonly CancellationTokenSource m_shutdown = new();
	private readonly object m_formLock = new();
	private CypressForm? m_form;
	private int m_disposed;

	public NativeFallbackHost(MessageHandler handler)
	{
		m_handler = handler;
	}

	// Runs the WinForms message loop. Blocks until the window is closed.
	public void Start()
	{
		Log("Starting native WinForms host (GDI rendering, no Chromium).");
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(false);

		var form = new CypressForm(this);
		lock (m_formLock)
			m_form = form;

		Application.Run(form);

		Log("Native host message loop exited.");
	}

	// Kept for signature parity with BrowserFallbackHost.
	public void TryOpenBrowser()
	{
		// The window is created inside Start(); nothing to launch separately.
	}

	public void WaitForShutdown()
	{
		try { m_shutdown.Token.WaitHandle.WaitOne(); }
		catch (ObjectDisposedException) { }
	}

	public void RequestShutdown()
	{
		if (Interlocked.Exchange(ref m_disposed, 1) != 0)
			return;

		try { m_shutdown.Cancel(); }
		catch { }

		CypressForm? form;
		lock (m_formLock)
			form = m_form;

		if (form == null)
			return;

		try
		{
			if (!form.IsDisposed)
				form.BeginInvoke((Action)(() => { try { form.Close(); } catch { } }));
		}
		catch (ObjectDisposedException)
		{
			// Form already destroyed; nothing to close.
		}
		catch (InvalidOperationException)
		{
			// Handle not created yet; nothing to close.
		}
	}

	// Backend -> UI. Called from arbitrary backend threads.
	public void SendToBrowser(string message)
	{
		CypressForm? form;
		lock (m_formLock)
			form = m_form;

		if (form == null || form.IsDisposed)
			return;

		try
		{
			form.BeginInvoke((Action)(() =>
			{
				try { form.HandleBackendMessage(message); }
				catch (Exception ex) { Console.Error.WriteLine("[Cypress] UI delivery failed: " + ex.Message); }
			}));
		}
		catch (ObjectDisposedException) { }
		catch (InvalidOperationException) { }
	}

	// UI -> external browser. Under GameNative the Wine shell routes this to the
	// Android browser, which is the intended path for EA OAuth redirects.
	public void OpenExternalUrl(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
			(uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			throw new ArgumentException("Only absolute HTTP and HTTPS URLs can be opened.", nameof(url));

		try
		{
			Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
			Log("Opened external URL in the system browser: " + uri.Host);
		}
		catch (Exception ex)
		{
			Log("Could not open external URL: " + ex.Message);
		}
	}

	public void Dispose()
	{
		RequestShutdown();
		try { m_shutdown.Dispose(); }
		catch { }
	}

	internal void OnFormClosed()
	{
		lock (m_formLock)
			m_form = null;
		try { m_shutdown.Cancel(); }
		catch { }
	}

	internal MessageHandler Handler => m_handler;

	private static void Log(string message)
	{
		try
		{
			string directory = System.IO.Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cypress");
			System.IO.Directory.CreateDirectory(directory);
			System.IO.File.AppendAllText(
				System.IO.Path.Combine(directory, "native-host.log"),
				$"[{DateTime.Now:O}] {message}{Environment.NewLine}");
		}
		catch { }
		try { Console.Error.WriteLine("[Cypress native] " + message); }
		catch { }
	}

	// Minimal but functional native shell. It talks to the real backend through
	// MessageHandler.HandleMessage, so it is not a mock: it is a reduced-scope
	// control surface that proves the render path works. Full tabbed UI parity is
	// ported separately.
	private sealed class CypressForm : Form
	{
		private readonly NativeFallbackHost m_host;
		private readonly Label m_status;
		private readonly TextBox m_log;
		private readonly TextBox m_rawAction;

		public CypressForm(NativeFallbackHost host)
		{
			m_host = host;

			Text = "Cypress Launcher - GameNative native mode";
			Width = 1040;
			Height = 700;
			MinimumSize = new Size(720, 480);
			StartPosition = FormStartPosition.CenterScreen;
			BackColor = Color.White;
			DoubleBuffered = true;

			m_status = new Label
			{
				Text = "Cypress native host ready. Backend bridge connected.",
				AutoSize = false,
				Height = 28,
				Dock = DockStyle.Top,
				Padding = new Padding(12, 8, 12, 0),
				Font = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold)
			};

			var buttons = new FlowLayoutPanel
			{
				Dock = DockStyle.Top,
				Height = 44,
				Padding = new Padding(12, 6, 12, 6),
				WrapContents = false
			};
			buttons.Controls.Add(MakeButton("Check Auth", "{\"type\":\"checkAuth\"}"));
			buttons.Controls.Add(MakeButton("EA Login", "{\"type\":\"startEALogin\"}"));
			buttons.Controls.Add(MakeButton("Instances", "{\"type\":\"getInstances\"}"));
			buttons.Controls.Add(MakeButton("Browser List", "{\"type\":\"getBrowserList\"}"));
			buttons.Controls.Add(MakeButton("Quit", null));

			m_rawAction = new TextBox
			{
				Dock = DockStyle.Top,
				PlaceholderText = "Raw action JSON, e.g. {\"type\":\"checkAuth\"}"
			};

			var sendRaw = new Button
			{
				Text = "Send JSON",
				Dock = DockStyle.Top,
				Height = 30,
				Padding = new Padding(10, 4, 10, 4)
			};
			sendRaw.Click += (_, _) => SendRaw(m_rawAction.Text ?? string.Empty);

			m_log = new TextBox
			{
				Dock = DockStyle.Fill,
				Multiline = true,
				ReadOnly = true,
				ScrollBars = ScrollBars.Vertical,
				WordWrap = true
			};

			// Dock order matters: add Fill first so Top panels stack above it.
			Controls.Add(m_log);
			Controls.Add(sendRaw);
			Controls.Add(m_rawAction);
			Controls.Add(buttons);
			Controls.Add(m_status);
		}

		private Button MakeButton(string text, string? json)
		{
			var b = new Button
			{
				Text = text,
				AutoSize = true,
				Padding = new Padding(10, 4, 10, 4),
				Margin = new Padding(0, 0, 8, 0)
			};
			b.Click += (_, _) =>
			{
				if (json == null)
					Close();
				else
					SendRaw(json);
			};
			return b;
		}

		private void SendRaw(string json)
		{
			if (string.IsNullOrWhiteSpace(json))
				return;

			try
			{
				// Same entry point the loopback bridge used: (null, message).
				m_host.Handler.HandleMessage(null, json);
				AppendLog(">> " + json);
			}
			catch (Exception ex)
			{
				AppendLog("send error: " + ex.Message);
			}
		}

		public void HandleBackendMessage(string message)
		{
			string type = "?";
			try
			{
				using JsonDocument doc = JsonDocument.Parse(message);
				if (doc.RootElement.TryGetProperty("type", out JsonElement t))
					type = t.GetString() ?? "?";
			}
			catch { }

			AppendLog("<< [" + type + "] " + message);

			switch (type)
			{
				case "authStatus":
				case "authLoginResult":
				case "gameDir":
				case "status":
					m_status.Text = type + ": " + message;
					break;
			}
		}

		private void AppendLog(string line)
		{
			m_log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine);
		}

		protected override void OnFormClosed(FormClosedEventArgs e)
		{
			m_host.OnFormClosed();
			base.OnFormClosed(e);
		}
	}
}
