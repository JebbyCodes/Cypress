#nullable enable
using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CypressLauncher;

// Native replacement for BrowserFallbackHost on Wine/GameNative.
// Avalonia + Skia software rendering avoids the DirectComposition/D3D11
// shared-texture present that fails inside WebView2/Chromium under Wine and
// causes the white window. The backend (MessageHandler) is unchanged: this
// class only replaces the rendering and message-pump layer.
internal sealed class AvaloniaFallbackHost : IDisposable
{
	private readonly MessageHandler m_handler;
	private readonly CancellationTokenSource m_shutdown = new();
	private CypressWindow? m_window;
	private int m_disposed;

	public AvaloniaFallbackHost(MessageHandler handler)
	{
		m_handler = handler;
	}

	// Runs the Avalonia UI loop. Blocks until the window is closed.
	public void Start()
	{
		Log("Starting Avalonia native host (Skia software rendering).");
		HostAccessor.Current = this;
		BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
		Log("Avalonia host exited.");
	}

	public void TryOpenBrowser() { /* window is created inside Start() */ }

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
		Dispatcher.UIThread.Post(() =>
		{
			try { m_window?.Close(); }
			catch { }
		});
	}

	// Backend -> UI. Called from arbitrary backend threads.
	public void SendToBrowser(string message)
	{
		Dispatcher.UIThread.Post(() =>
		{
			try { m_window?.HandleBackendMessage(message); }
			catch (Exception ex) { Console.Error.WriteLine("[Cypress] UI delivery failed: " + ex.Message); }
		});
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

	private static AppBuilder BuildAvaloniaApp() =>
		AppBuilder.Configure<CypressApp>()
			.UsePlatformDetect()
			// Force software rendering so Wine never sees a D3D/ANGLE present.
			.With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
			.LogToTrace();

	public void Dispose()
	{
		RequestShutdown();
		try { m_shutdown.Dispose(); }
		catch { }
	}

	private sealed class CypressApp : Application
	{
		public override void Initialize() { }

		public override void OnFrameworkInitializationCompleted()
		{
			if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			{
				AvaloniaFallbackHost? host = HostAccessor.Current;
				if (host != null)
				{
					host.m_window = new CypressWindow(host);
					desktop.MainWindow = host.m_window;
					desktop.Exit += (_, _) => host.RequestShutdown();
				}
			}
			base.OnFrameworkInitializationCompleted();
		}
	}

	private static class HostAccessor
	{
		public static AvaloniaFallbackHost? Current;
	}

	private static void Log(string message)
	{
		try
		{
			string directory = System.IO.Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cypress");
			System.IO.Directory.CreateDirectory(directory);
			System.IO.File.AppendAllText(
				System.IO.Path.Combine(directory, "avalonia-host.log"),
				$"[{DateTime.Now:O}] {message}{Environment.NewLine}");
		}
		catch { }
		try { Console.Error.WriteLine("[Cypress avalonia] " + message); }
		catch { }
	}

	// Minimal but functional native shell. It talks to the real backend through
	// MessageHandler.HandleMessage, so it is not a mock: it is a reduced-scope
	// control surface that proves the render path works. The full tabbed HTML UI
	// is ported separately.
	internal sealed class CypressWindow : Window
	{
		private readonly AvaloniaFallbackHost m_host;
		private readonly TextBlock m_status;
		private readonly TextBox m_log;
		private readonly TextBox m_rawAction;

		public CypressWindow(AvaloniaFallbackHost host)
		{
			m_host = host;
			Title = "Cypress Launcher - GameNative native mode";
			Width = 1040;
			Height = 700;
			MinWidth = 720;
			MinHeight = 480;
			Background = Brushes.White;

			m_status = new TextBlock
			{
				Text = "Cypress native host ready. Backend bridge connected.",
				Margin = new Thickness(12),
				FontSize = 14,
				FontWeight = FontWeight.SemiBold
			};

			var buttons = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Spacing = 8,
				Margin = new Thickness(12, 0, 12, 8)
			};
			buttons.Children.Add(MakeButton("Check Auth", "{\"type\":\"checkAuth\"}"));
			buttons.Children.Add(MakeButton("EA Login", "{\"type\":\"startEALogin\"}"));
			buttons.Children.Add(MakeButton("Instances", "{\"type\":\"getInstances\"}"));
			buttons.Children.Add(MakeButton("Browser List", "{\"type\":\"getBrowserList\"}"));
			buttons.Children.Add(MakeButton("Quit", null));

			m_rawAction = new TextBox
			{
				Watermark = "Raw action JSON, e.g. {\"type\":\"checkAuth\"}",
				Margin = new Thickness(12, 0, 12, 8)
			};
			var sendRaw = new Button
			{
				Content = "Send JSON",
				Padding = new Thickness(10, 6),
				Margin = new Thickness(12, 0, 12, 8)
			};
			sendRaw.Click += (_, _) => SendRaw(m_rawAction.Text ?? string.Empty);

			m_log = new TextBox
			{
				IsReadOnly = true,
				AcceptsReturn = true,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(12, 0, 12, 12)
			};

			Content = new DockPanel
			{
				LastChildFill = true,
				Children =
				{
					Dock(m_status, Dock.Top),
					Dock(buttons, Dock.Top),
					Dock(m_rawAction, Dock.Top),
					Dock(sendRaw, Dock.Top),
					m_log
				}
			};
		}

		private static Control Dock(Control c, Dock dock)
		{
			DockPanel.SetDock(c, dock);
			return c;
		}

		private Button MakeButton(string text, string? json)
		{
			var b = new Button
			{
				Content = text,
				Padding = new Thickness(10, 6)
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
				m_host.m_handler.HandleMessage(null, json);
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
			m_log.Text = (m_log.Text ?? string.Empty) +
				"[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine;
			m_log.CaretIndex = m_log.Text?.Length ?? 0;
		}
	}
}
