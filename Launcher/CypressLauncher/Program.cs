#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Photino.NET;

namespace CypressLauncher;

internal static class Program
{
	private static readonly bool s_isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
	internal static bool IsBrowserFallback { get; private set; }

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetModuleHandle(string moduleName);
	[DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
	private static extern IntPtr GetProcAddress(IntPtr module, string procName);

	[DllImport("kernel32.dll")]
	private static extern bool AttachConsole(int dwProcessId);
	[DllImport("kernel32.dll")]
	private static extern bool FreeConsole();
	private const int ATTACH_PARENT_PROCESS = -1;

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
	private const int SW_HIDE = 0;
	private const int SW_SHOW = 5;

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int vKey);
	private const int VK_MENU = 0x12;

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
	[DllImport("shcore.dll")]
	private static extern int GetDpiForMonitor(IntPtr hMon, int dpiType, out uint dpiX, out uint dpiY);
	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
	private const uint SWP_NOMOVE = 0x0002;
	private const uint SWP_NOZORDER = 0x0004;

	private static void ApplyPhysicalSize(PhotinoWindow window, int logicalW, int logicalH)
	{
		// Photino's SetSize uses logical pixels. This adjustment is only needed on
		// Windows, but DPI APIs are not consistently implemented by Wine/Proton.
		// In particular, GetDpiForMonitor can fail or return zero there. Never pass
		// a zero-sized rectangle to SetWindowPos: that leaves the launcher invisible.
		var hwnd = window.WindowHandle;
		if (hwnd == IntPtr.Zero)
		{
			Console.Error.WriteLine("[debug] Skipping physical sizing: window handle is not ready.");
			return;
		}

		uint dpiX = 96;
		try
		{
			var mon = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
			if (mon != IntPtr.Zero)
			{
				int result = GetDpiForMonitor(mon, 0, out uint reportedDpiX, out _);
				// 48-768 DPI is a deliberately broad sanity range. An HRESULT failure,
				// a missing Wine export, or a zero DPI all fall back to 96 DPI.
				if (result >= 0 && reportedDpiX is >= 48 and <= 768)
					dpiX = reportedDpiX;
				else
					Console.Error.WriteLine($"[debug] GetDpiForMonitor returned 0x{result:X8} and dpi={reportedDpiX}; using 96 DPI.");
			}
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			Console.Error.WriteLine("[debug] DPI API unavailable; using 96 DPI: " + ex.Message);
		}

		int physW = (int)Math.Round(logicalW * dpiX / 96.0);
		int physH = (int)Math.Round(logicalH * dpiX / 96.0);
		if (physW <= 0 || physH <= 0)
		{
			Console.Error.WriteLine($"[debug] Invalid physical size {physW}x{physH}; falling back to {logicalW}x{logicalH}.");
			physW = logicalW;
			physH = logicalH;
		}

		Console.Error.WriteLine($"[debug] dpi={dpiX} phys={physW}x{physH}");
		try
		{
			if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, physW, physH, SWP_NOMOVE | SWP_NOZORDER))
			{
				int error = Marshal.GetLastWin32Error();
				Console.Error.WriteLine($"[debug] SetWindowPos failed ({error}); falling back to Photino.SetSize.");
				window.SetSize(logicalW, logicalH);
			}
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			Console.Error.WriteLine("[debug] SetWindowPos unavailable; falling back to Photino.SetSize: " + ex.Message);
			window.SetSize(logicalW, logicalH);
		}
	}

	[STAThread]
	private static void Main(string[] args)
	{
		try
		{
			// ensure working directory is the app folder (start menu shortcuts set it to system32)
			Environment.CurrentDirectory = AppContext.BaseDirectory;

			if (args.Length > 0)
			{
				AttachConsole(ATTACH_PARENT_PROCESS);
				// print a newline so output starts on a fresh line after the prompt
				Console.WriteLine();
				Environment.ExitCode = PatchCli.Run(args);
				FreeConsole();
				return;
			}

			var handler = new MessageHandler();
			if (ShouldUseBrowserFallback())
			{
				IsBrowserFallback = true;
				ConfigureWineWebView2Compatibility();
				RunBrowserFallback(handler);
				handler.KillAllInstances();
				return;
			}

#if WINDOWS
			SetCurrentProcessExplicitAppUserModelID("CypressLauncher.App");
#endif

			string html = ResourceBuilder.BuildHtml(Assembly.GetExecutingAssembly());
			string iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "cypressicons", "ico", "Burbank-CypressIcon.ico");

			IDisposable? trayCleanup = null;
#if WINDOWS
			trayCleanup = SetupWindowsTray(handler, iconPath);
#endif

			var window = new PhotinoWindow()
				.SetTitle("Cypress Launcher")
				.SetSize(1280, 800)
				.SetMinSize(900, 600)
				.Center()
				.SetResizable(true)
				.SetContextMenuEnabled(false)
				.SetDevToolsEnabled(true)
				.SetFileSystemAccessEnabled(false)
				.SetGrantBrowserPermissions(false)
				.SetLogVerbosity(2);

			if (File.Exists(iconPath))
				window.SetIconFile(iconPath);

			window.RegisterWebMessageReceivedHandler(handler.HandleMessage);

			window.RegisterWindowCreatedHandler((_, _) =>
			{
				try
				{
					float screenDpi = window.ScreenDpi;
					float scale = screenDpi > 0 ? screenDpi / 96f : 1f;
					Console.Error.WriteLine($"[debug] ScreenDpi={screenDpi} scale={scale} size={window.Size}");
					ApplyPhysicalSize(window, 1280, 800);
					window.Center();
				}
				catch (Exception ex)
				{
					// DPI/positioning is cosmetic. It must never prevent the web UI from
					// being shown on compatibility layers with partial Win32 support.
					Console.Error.WriteLine("[warning] Window sizing failed; preserving the default Photino size: " + ex);
					try { window.SetSize(1280, 800).Center(); } catch (Exception fallbackEx)
					{
						Console.Error.WriteLine("[warning] Photino fallback sizing also failed: " + fallbackEx);
					}
				}
			});

#if WINDOWS
			SetupWindowsWindowHooks(window, handler);
#endif

			foreach (var stale in Directory.GetFiles(AppContext.BaseDirectory, "cypress_launcher_*.html"))
				try { File.Delete(stale); } catch { }

			string tempHtml = Path.Combine(AppContext.BaseDirectory, "cypress_launcher_" + Guid.NewGuid().ToString("N") + ".html");
			try
			{
				File.WriteAllText(tempHtml, html, System.Text.Encoding.UTF8);
				window.Load(tempHtml);
				window.WaitForClose();
			}
			finally
			{
				try { File.Delete(tempHtml); } catch { }
			}

			handler.KillAllInstances();
			trayCleanup?.Dispose();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("Fatal error: " + ex);
#if WINDOWS
			WindowsMessageBox("Fatal error:\n" + ex.ToString(), "Cypress Launcher Error");
#endif
		}
	}

	private static bool IsRunningUnderWine()
	{
		try
		{
			IntPtr ntdll = GetModuleHandle("ntdll.dll");
			return ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
		}
		catch { return false; }
	}

	private static bool IsGameNativeSession()
	{
		// GameNative sets these variables before launching Windows programs so
		// Wine routes shell-opened URLs back to Android. Detect them as an explicit
		// signal as well as checking Wine's ntdll export.
		foreach (string name in new[] { "WINE_OPEN_WITH_ANDROID_BROWSER", "WINE_OPEN_WITH_ANDROID_BROwSER" })
		{
			string? value = Environment.GetEnvironmentVariable(name);
			if (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	private static bool ShouldUseBrowserFallback()
	{
		string? configured = Environment.GetEnvironmentVariable("CYPRESS_BROWSER_MODE");
		if (configured == "1" || string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase))
			return true;
		if (configured == "0" || string.Equals(configured, "false", StringComparison.OrdinalIgnoreCase))
			return false;

		// Wine/Proton can fail to paint Photino's embedded WebView2 window.
		// GameNative is also detected by its Android URL-routing environment flag.
		return IsRunningUnderWine() || IsGameNativeSession();
	}

	private static void ConfigureWineWebView2Compatibility()
	{
#if WINDOWS
		if (!IsRunningUnderWine() && !IsGameNativeSession()) return;
		try
		{
			// Wine currently fails WebView2's DirectComposition initialisation when
			// msedgewebview2.exe reports Windows 8.1 or newer. Its per-application
			// compatibility override set to win8 is the documented workaround.
			using RegistryKey? key = Registry.CurrentUser.CreateSubKey(
				@"Software\Wine\AppDefaults\msedgewebview2.exe");
			if (key == null)
				throw new InvalidOperationException("Wine WebView2 compatibility registry key could not be created.");
			key.SetValue("Version", "win8", RegistryValueKind.String);
			key.Flush();
			Console.Error.WriteLine("[Cypress] Applied the Wine WebView2 win8 compatibility override.");
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("[Cypress] Could not apply the Wine WebView2 compatibility override: " + ex.Message);
		}
#endif
	}

	private static void RunBrowserFallback(MessageHandler handler)
	{
		string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
		string html = ResourceBuilder.BuildHtml(Assembly.GetExecutingAssembly(), token);
		using var browser = new BrowserFallbackHost(handler, html, token);
		handler.SetBrowserMessageSender(browser.SendToBrowser);
		handler.SetBrowserCloseAction(browser.RequestShutdown);
		handler.SetBrowserExternalUrlOpener(browser.OpenExternalUrl);
		string url = browser.Start();

		Console.Error.WriteLine("Cypress is using its GameNative/Wine browser compatibility mode.");
		Console.Error.WriteLine("Starting the bundled SimpleWebView2Browser for the local Cypress UI.");
		Console.Error.WriteLine("Browser fallback diagnostics: " + Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cypress", "browser-fallback.log"));
		Console.Error.WriteLine("Local Cypress UI: " + url);

		try
		{
			browser.TryOpenBrowser();
			browser.WaitForShutdown();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("[Cypress] Could not start the local WebView browser: " + ex);
			throw;
		}
	}

#if WINDOWS
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static void WindowsMessageBox(string text, string caption)
	{
		System.Windows.Forms.MessageBox.Show(text, caption);
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static IDisposable SetupWindowsTray(MessageHandler handler, string iconPath)
	{
		var trayIcon = new System.Windows.Forms.NotifyIcon();
		if (File.Exists(iconPath))
			trayIcon.Icon = new System.Drawing.Icon(iconPath);
		trayIcon.Text = "Cypress Launcher";
		trayIcon.Visible = false;

		var trayMenu = new System.Windows.Forms.ContextMenuStrip();
		trayMenu.Items.Add("Show Cypress Launcher", null, (_, _) =>
		{
			trayIcon.Visible = false;
			if (handler.Window == null) return;
			ShowWindow(handler.Window.WindowHandle, SW_SHOW);
			handler.Window.SetMinimized(false);
		});
		trayMenu.Items.Add("-");
		trayMenu.Items.Add("Exit", null, (_, _) =>
		{
			trayIcon.Visible = false;
			trayIcon.Dispose();
			handler.KillAllInstances();
			Environment.Exit(0);
		});
		trayIcon.ContextMenuStrip = trayMenu;
		trayIcon.DoubleClick += (_, _) =>
		{
			trayIcon.Visible = false;
			if (handler.Window == null) return;
			ShowWindow(handler.Window.WindowHandle, SW_SHOW);
			handler.Window.SetMinimized(false);
		};

		handler.TrayIcon = trayIcon;
		return new TrayCleanup(trayIcon);
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static void SetupWindowsWindowHooks(PhotinoWindow window, MessageHandler handler)
	{
		// alt+f4 -> actually close, X button -> minimize to tray
		window.WindowClosing += (_, _) =>
		{
			bool altHeld = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
			if (altHeld)
			{
				// fire deregisters in background, give them up to 1.5s, then hard exit
				var deregTask = System.Threading.Tasks.Task.Run(() => handler.DeregisterAllHeartbeats());
				deregTask.Wait(TimeSpan.FromMilliseconds(1500));
				handler.KillAllInstances(skipHeartbeatShutdown: true);
				Environment.Exit(0);
				return false;
			}
			ShowWindow(window.WindowHandle, SW_HIDE);
			if (handler.TrayIcon != null)
				((dynamic)handler.TrayIcon).Visible = true;
			return true;
		};
	}

	private sealed class TrayCleanup : IDisposable
	{
		private readonly System.Windows.Forms.NotifyIcon _icon;
		public TrayCleanup(System.Windows.Forms.NotifyIcon icon) => _icon = icon;
		[System.Runtime.Versioning.SupportedOSPlatform("windows")]
		public void Dispose()
		{
			_icon.Visible = false;
			_icon.Dispose();
		}
	}
#endif
}
