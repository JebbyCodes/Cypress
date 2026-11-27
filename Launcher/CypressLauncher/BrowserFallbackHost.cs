#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CypressLauncher;

/// <summary>
/// Hosts the launcher's HTML UI on loopback and bridges browser messages to the
/// existing MessageHandler. Wine/Proton uses a standalone WebView2 host because
/// Photino's embedded browser window may fail to render in that environment.
/// </summary>
internal sealed class BrowserFallbackHost : IDisposable
{
	private const int MaxHeaderBytes = 64 * 1024;
	private const int MaxBodyBytes = 2 * 1024 * 1024;
	private const string BrowserDownloadUrl = "https://github.com/felixhaedicke/SimpleWebView2Browser/releases/download/v0.1/SimpleWebView2Browser.zip";
	private const string BrowserDownloadSha256 = "a8bd3c3a609e68434deb06216cee5a434048a7f3e97c5a9cf7411bf867ab11c1";
	private static readonly byte[] s_headerTerminator = { 13, 10, 13, 10 };
	private static readonly HttpClient s_downloadClient = new() { Timeout = TimeSpan.FromSeconds(45) };
	private static readonly object s_downloadLock = new();

	private readonly TcpListener m_listener;
	private readonly CancellationTokenSource m_shutdown = new();
	private readonly MessageHandler m_handler;
	private readonly string m_html;
	private readonly string m_token;
	private readonly object m_messageLock = new();
	private readonly object m_handlerLock = new();
	private readonly List<OutboundMessage> m_outbound = new();
	private TaskCompletionSource<bool> m_messageSignal = NewSignal();
	private long m_nextMessageId;
	private Task? m_acceptTask;
	private Process? m_browserProcess;
	private readonly List<Process> m_externalBrowserProcesses = new();
	private int m_shutdownRequested;
	private readonly object m_lifecycleLock = new();

	private sealed record OutboundMessage(long Id, string Message);
	private sealed record HttpRequest(string Method, string Target, string Path, string Query, byte[] Body);

	public string Url { get; private set; } = string.Empty;

	public BrowserFallbackHost(MessageHandler handler, string html, string token)
	{
		m_handler = handler;
		m_html = html;
		m_token = token;
		m_listener = new TcpListener(IPAddress.Loopback, 0);
	}

	public string Start()
	{
		m_listener.Start();
		int port = ((IPEndPoint)m_listener.LocalEndpoint).Port;
		Url = $"http://127.0.0.1:{port}/{m_token}/";
		Log($"Loopback server is listening on 127.0.0.1:{port}; waiting for the SimpleWebView2Browser page request.");
		m_acceptTask = AcceptLoopAsync(m_shutdown.Token);
		return Url;
	}

	public void SendToBrowser(string message)
	{
		TaskCompletionSource<bool> signal;
		lock (m_messageLock)
		{
			m_outbound.Add(new OutboundMessage(++m_nextMessageId, message));
			if (m_outbound.Count > 2000)
				m_outbound.RemoveRange(0, m_outbound.Count - 2000);
			signal = m_messageSignal;
			m_messageSignal = NewSignal();
		}
		signal.TrySetResult(true);
	}

	public void TryOpenBrowser()
	{
		Process process = StartBrowserProcess(Url);
		lock (m_lifecycleLock)
			m_browserProcess = process;
		Log("Started SimpleWebView2Browser for the Cypress loopback URL; process id=" + process.Id + ".");
		_ = MonitorBrowserExitAsync(process);
	}

	public void OpenExternalUrl(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
			(uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			throw new ArgumentException("Only absolute HTTP and HTTPS URLs can be opened.", nameof(url));

		Process process = StartBrowserProcess(uri.AbsoluteUri);
		lock (m_lifecycleLock)
		{
			if (Volatile.Read(ref m_shutdownRequested) != 0)
			{
				try { process.Kill(entireProcessTree: true); } catch { }
				process.Dispose();
				throw new ObjectDisposedException(nameof(BrowserFallbackHost));
			}
			m_externalBrowserProcesses.Add(process);
		}
		Log("Opened an external HTTP(S) link in a second SimpleWebView2Browser window.");
		_ = MonitorExternalBrowserExitAsync(process);
	}

	private static Process StartBrowserProcess(string targetUrl)
	{
		string browserPath = ResolveConfiguredBrowser() ?? DownloadBrowser();
		var startInfo = new ProcessStartInfo
		{
			FileName = browserPath,
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(browserPath) ?? AppContext.BaseDirectory,
			WindowStyle = ProcessWindowStyle.Normal
		};
		// SimpleWebView2Browser reads argv[1] as its initial navigation URL.
		startInfo.ArgumentList.Add(targetUrl);
		return Process.Start(startInfo)
			?? throw new InvalidOperationException("SimpleWebView2Browser did not start.");
	}

	private async Task MonitorBrowserExitAsync(Process process)
	{
		try
		{
			await process.WaitForExitAsync().ConfigureAwait(false);
			Log("SimpleWebView2Browser exited with code " + process.ExitCode + ".");
			if (!m_shutdown.IsCancellationRequested)
				RequestShutdown();
		}
		catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
		{
			Log("Could not monitor the browser process: " + ex.Message);
		}
	}

	private async Task MonitorExternalBrowserExitAsync(Process process)
	{
		try { await process.WaitForExitAsync().ConfigureAwait(false); }
		catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
		{
			Log("Could not monitor an external browser window: " + ex.Message);
		}
		finally
		{
			lock (m_lifecycleLock) m_externalBrowserProcesses.Remove(process);
			try { process.Dispose(); } catch { }
		}
	}

	public void RequestShutdown()
	{
		if (Interlocked.Exchange(ref m_shutdownRequested, 1) != 0) return;
		try { m_shutdown.Cancel(); } catch (ObjectDisposedException) { }
		try { m_listener.Stop(); } catch { }

		Process? mainProcess;
		Process[] externalProcesses;
		lock (m_lifecycleLock)
		{
			mainProcess = m_browserProcess;
			externalProcesses = m_externalBrowserProcesses.ToArray();
			m_externalBrowserProcesses.Clear();
		}

		CloseBrowserProcess(mainProcess, "main");
		foreach (Process process in externalProcesses)
			CloseBrowserProcess(process, "external");
	}

	private static void CloseBrowserProcess(Process? process, string label)
	{
		if (process == null) return;
		try
		{
			if (process.HasExited) return;
			try { process.CloseMainWindow(); } catch { }
			if (!process.WaitForExit(1500) && !process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (Exception ex)
		{
			Log("Could not close " + label + " SimpleWebView2Browser window: " + ex.Message);
		}
	}

	private static string? ResolveConfiguredBrowser()
	{
		// An explicit override is useful for development, but normal users never
		// need to configure a browser path. The release build bundles the browser.
		string? configured = Environment.GetEnvironmentVariable("CYPRESS_BROWSER_EXECUTABLE");
		if (!string.IsNullOrWhiteSpace(configured))
		{
			string? resolved = ResolveBrowserPath(configured);
			if (resolved != null) return resolved;
			Log("CYPRESS_BROWSER_EXECUTABLE was not found: " + configured);
		}

		string bundledBrowser = Path.Combine(AppContext.BaseDirectory, "Browser", "SimpleWebView2Browser.exe");
		if (IsCompleteBrowserInstall(bundledBrowser)) return Path.GetFullPath(bundledBrowser);

		// Keep compatibility with older development builds that placed the files
		// directly beside CypressLauncher.exe.
		string besideLauncher = Path.Combine(AppContext.BaseDirectory, "SimpleWebView2Browser.exe");
		if (IsCompleteBrowserInstall(besideLauncher)) return Path.GetFullPath(besideLauncher);

		string appDataBrowser = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Cypress", "Browser", "SimpleWebView2Browser.exe");
		if (IsCompleteBrowserInstall(appDataBrowser)) return Path.GetFullPath(appDataBrowser);

		// Preserve support for a developer-managed portable browser.
		string configPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Cypress", "browser-executable.txt");
		try
		{
			if (File.Exists(configPath))
			{
				foreach (string rawLine in File.ReadAllLines(configPath))
				{
					string line = rawLine.Trim();
					if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
					string? resolved = ResolveBrowserPath(line);
					if (resolved != null) return resolved;
					Log("Browser path in " + configPath + " was not found: " + line);
				}
			}
		}
		catch (Exception ex) { Log("Could not read browser configuration: " + ex.Message); }

		string? pathValue = Environment.GetEnvironmentVariable("PATH");
		if (!string.IsNullOrWhiteSpace(pathValue))
		{
			foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
			{
				try
				{
					string candidate = Path.Combine(directory.Trim().Trim('"'), "SimpleWebView2Browser.exe");
					if (IsCompleteBrowserInstall(candidate)) return Path.GetFullPath(candidate);
				}
				catch { }
			}
		}
		return null;
	}

	private static string? ResolveBrowserPath(string value)
	{
		string candidate = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
		if (candidate.Length == 0) return null;
		try
		{
			if (IsCompleteBrowserInstall(candidate)) return Path.GetFullPath(candidate);
			if (!Path.IsPathRooted(candidate))
			{
				string besideLauncher = Path.Combine(AppContext.BaseDirectory, candidate);
				if (IsCompleteBrowserInstall(besideLauncher)) return Path.GetFullPath(besideLauncher);
			}
		}
		catch { }
		return null;
	}

	private static bool IsCompleteBrowserInstall(string executablePath)
	{
		try
		{
			return File.Exists(executablePath) &&
				File.Exists(Path.Combine(Path.GetDirectoryName(executablePath) ?? string.Empty, "WebView2Loader.dll"));
		}
		catch { return false; }
	}

	private static string DownloadBrowser()
	{
		lock (s_downloadLock)
		{
			string browserDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Cypress", "Browser");
			Directory.CreateDirectory(browserDirectory);
			string executablePath = Path.Combine(browserDirectory, "SimpleWebView2Browser.exe");
			string loaderPath = Path.Combine(browserDirectory, "WebView2Loader.dll");
			if (IsCompleteBrowserInstall(executablePath))
				return executablePath;

			Log("Bundled browser is missing; downloading the pinned SimpleWebView2Browser v0.1 release.");
			byte[] archiveBytes = s_downloadClient.GetByteArrayAsync(BrowserDownloadUrl).GetAwaiter().GetResult();
			string actualHash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
			if (!actualHash.Equals(BrowserDownloadSha256, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("SimpleWebView2Browser download failed SHA-256 verification.");

			using var archiveStream = new MemoryStream(archiveBytes, writable: false);
			using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
			ZipArchiveEntry? executableEntry = archive.Entries.FirstOrDefault(e => e.Name.Equals("SimpleWebView2Browser.exe", StringComparison.OrdinalIgnoreCase));
			ZipArchiveEntry? loaderEntry = archive.Entries.FirstOrDefault(e => e.Name.Equals("WebView2Loader.dll", StringComparison.OrdinalIgnoreCase));
			if (executableEntry == null || loaderEntry == null)
				throw new InvalidDataException("The downloaded browser archive is missing its executable or WebView2Loader.dll.");

			string tempExecutable = executablePath + ".new";
			string tempLoader = loaderPath + ".new";
			try
			{
				CopyArchiveEntry(executableEntry, tempExecutable);
				CopyArchiveEntry(loaderEntry, tempLoader);
				File.Move(tempExecutable, executablePath, overwrite: true);
				File.Move(tempLoader, loaderPath, overwrite: true);
			}
			finally
			{
				try { File.Delete(tempExecutable); } catch { }
				try { File.Delete(tempLoader); } catch { }
			}
			Log("Downloaded and verified SimpleWebView2Browser into the Cypress application data directory.");
			return executablePath;
		}
	}

	private static void CopyArchiveEntry(ZipArchiveEntry entry, string destination)
	{
		using Stream source = entry.Open();
		using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
		source.CopyTo(target);
	}

	public void WaitForShutdown()
	{
		try { m_shutdown.Token.WaitHandle.WaitOne(); }
		catch (ObjectDisposedException) { }
	}

	private async Task AcceptLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await m_listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { break; }
			catch (ObjectDisposedException) { break; }
			catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
			catch (Exception ex)
			{
				Log("Browser connection failed: " + ex.Message);
				continue;
			}

			_ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
		}
	}

	private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
	{
		using (client)
		{
			try
			{
				client.NoDelay = true;
				NetworkStream stream = client.GetStream();
				HttpRequest? request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
				if (request == null) return;

				string prefix = "/" + m_token;
				string safePath = request.Path.StartsWith(prefix, StringComparison.Ordinal)
					? request.Path.Substring(prefix.Length)
					: "<outside-token-path>";
				Log("HTTP " + request.Method + " " + safePath);

				if ((request.Path == prefix || request.Path == prefix + "/") && request.Method == "GET")
				{
					byte[] htmlBytes = Encoding.UTF8.GetBytes(m_html);
					Log($"Served the Cypress UI document ({htmlBytes.Length:N0} bytes) to SimpleWebView2Browser.");
					await WriteResponseAsync(stream, 200, "text/html; charset=utf-8", htmlBytes, cancellationToken).ConfigureAwait(false);
					return;
				}

				if (request.Path == prefix + "/api/health" && request.Method == "GET")
				{
					await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}"), cancellationToken).ConfigureAwait(false);
					return;
				}

				if (request.Path == prefix + "/api/send" && request.Method == "POST")
				{
					string message = Encoding.UTF8.GetString(request.Body);
					if (message.Length == 0 || message.Length > MaxBodyBytes)
					{
						await WriteResponseAsync(stream, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Invalid message."), cancellationToken).ConfigureAwait(false);
						return;
					}

					lock (m_handlerLock)
						m_handler.HandleMessage(null, message);

					await WriteResponseAsync(stream, 204, "text/plain; charset=utf-8", Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
					return;
				}

				if (request.Path == prefix + "/api/poll" && request.Method == "GET")
				{
					long after = 0;
					if (long.TryParse(GetQueryValue(request.Query, "after"), out long parsed))
						after = Math.Max(0, parsed);

					IReadOnlyList<OutboundMessage> messages = await WaitForMessagesAsync(after, TimeSpan.FromSeconds(18), cancellationToken).ConfigureAwait(false);
					var payload = new JArray(messages.Select(x => new JObject
					{
						["id"] = x.Id,
						["message"] = x.Message
					}));
					await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None)), cancellationToken).ConfigureAwait(false);
					return;
				}

				await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found."), cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { }
			catch (IOException) { }
			catch (Exception ex)
			{
				Log("Browser request failed: " + ex);
				try
				{
					if (client.Connected)
						await WriteResponseAsync(client.GetStream(), 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Cypress browser bridge error."), cancellationToken).ConfigureAwait(false);
				}
				catch { }
			}
		}
	}

	private async Task<IReadOnlyList<OutboundMessage>> WaitForMessagesAsync(long after, TimeSpan timeout, CancellationToken cancellationToken)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		while (!cancellationToken.IsCancellationRequested)
		{
			Task signal;
			lock (m_messageLock)
			{
				OutboundMessage[] pending = m_outbound.Where(x => x.Id > after).ToArray();
				if (pending.Length > 0) return pending;
				signal = m_messageSignal.Task;
			}

			TimeSpan remaining = deadline - DateTime.UtcNow;
			if (remaining <= TimeSpan.Zero) return Array.Empty<OutboundMessage>();
			Task delay = Task.Delay(remaining, cancellationToken);
			Task completed = await Task.WhenAny(signal, delay).ConfigureAwait(false);
			if (completed == delay)
			{
				cancellationToken.ThrowIfCancellationRequested();
				return Array.Empty<OutboundMessage>();
			}
		}
		return Array.Empty<OutboundMessage>();
	}

	private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		using var buffer = new MemoryStream();
		byte[] chunk = new byte[4096];
		byte[] all = Array.Empty<byte>();
		int headerEnd = -1;
		while (headerEnd < 0)
		{
			int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
			if (read == 0) return null;
			buffer.Write(chunk, 0, read);
			if (buffer.Length > MaxHeaderBytes) throw new InvalidDataException("HTTP headers are too large.");
			all = buffer.ToArray();
			headerEnd = FindSequence(all, s_headerTerminator);
		}

		string headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
		string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
		string[] firstLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
		if (firstLine.Length < 2) throw new InvalidDataException("Invalid HTTP request line.");

		int contentLength = 0;
		for (int i = 1; i < lines.Length; i++)
		{
			int colon = lines[i].IndexOf(':');
			if (colon <= 0) continue;
			if (lines[i].Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
				int.TryParse(lines[i].Substring(colon + 1).Trim(), out int parsedLength))
				contentLength = parsedLength;
		}

		if (contentLength < 0 || contentLength > MaxBodyBytes)
			throw new InvalidDataException("HTTP request body is too large.");

		int bodyOffset = headerEnd + s_headerTerminator.Length;
		while (buffer.Length - bodyOffset < contentLength)
		{
			int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
			if (read == 0) throw new EndOfStreamException("HTTP request body ended early.");
			buffer.Write(chunk, 0, read);
		}

		all = buffer.ToArray();
		byte[] body = new byte[contentLength];
		if (contentLength > 0)
			Buffer.BlockCopy(all, bodyOffset, body, 0, contentLength);

		string target = firstLine[1];
		if (!Uri.TryCreate("http://127.0.0.1" + target, UriKind.Absolute, out Uri? uri))
			throw new InvalidDataException("Invalid HTTP request target.");

		return new HttpRequest(firstLine[0].ToUpperInvariant(), target, uri.AbsolutePath, uri.Query, body);
	}

	private static int FindSequence(byte[] source, byte[] sequence)
	{
		for (int i = 0; i <= source.Length - sequence.Length; i++)
		{
			bool match = true;
			for (int j = 0; j < sequence.Length; j++)
			{
				if (source[i + j] == sequence[j]) continue;
				match = false;
				break;
			}
			if (match) return i;
		}
		return -1;
	}

	private static string? GetQueryValue(string query, string key)
	{
		if (string.IsNullOrEmpty(query)) return null;
		foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] parts = pair.Split('=', 2);
			if (Uri.UnescapeDataString(parts[0].Replace('+', ' ')).Equals(key, StringComparison.Ordinal))
				return parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
		}
		return null;
	}

	private static async Task WriteResponseAsync(NetworkStream stream, int status, string contentType, byte[] body, CancellationToken cancellationToken)
	{
		string reason = status switch
		{
			200 => "OK",
			204 => "No Content",
			400 => "Bad Request",
			404 => "Not Found",
			500 => "Internal Server Error",
			_ => "OK"
		};
		string headers = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
		byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
		await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), cancellationToken).ConfigureAwait(false);
		if (body.Length > 0)
			await stream.WriteAsync(body.AsMemory(0, body.Length), cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

	private static void Log(string message)
	{
		try
		{
			string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cypress");
			Directory.CreateDirectory(directory);
			File.AppendAllText(Path.Combine(directory, "browser-fallback.log"), $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
		}
		catch { }
		try { Console.Error.WriteLine("[Cypress browser fallback] " + message); } catch { }
	}

	public void Dispose()
	{
		RequestShutdown();
		try { m_acceptTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
		lock (m_lifecycleLock)
		{
			try { m_browserProcess?.Dispose(); } catch { }
			m_browserProcess = null;
		}
		m_shutdown.Dispose();
	}
}
