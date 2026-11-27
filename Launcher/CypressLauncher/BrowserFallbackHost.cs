#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CypressLauncher;

/// <summary>
/// Hosts the launcher's HTML UI on loopback and bridges browser messages to the
/// existing MessageHandler. This avoids the Windows WebView2 dependency in Wine /
/// Proton environments where the embedded browser cannot be started.
/// </summary>
internal sealed class BrowserFallbackHost : IDisposable
{
	private const int MaxHeaderBytes = 64 * 1024;
	private const int MaxBodyBytes = 2 * 1024 * 1024;
	private static readonly byte[] s_headerTerminator = { 13, 10, 13, 10 };

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
		try
		{
			var startInfo = new System.Diagnostics.ProcessStartInfo
			{
				FileName = Url,
				UseShellExecute = true
			};
			System.Diagnostics.Process.Start(startInfo);
			Log("Requested the system browser to open the launcher URL.");
		}
		catch (Exception ex)
		{
			Log("Could not open the system browser automatically: " + ex.Message);
			Log("Open this URL manually in the device browser: " + Url);
		}
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
				if (request.Path == prefix + "/" && request.Method == "GET")
				{
					await WriteResponseAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(m_html), cancellationToken).ConfigureAwait(false);
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
		m_shutdown.Cancel();
		try { m_listener.Stop(); } catch { }
		try { m_acceptTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
		m_shutdown.Dispose();
	}
}
