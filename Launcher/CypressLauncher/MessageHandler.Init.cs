#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CypressLauncher;

public partial class MessageHandler
{
	private static readonly string s_tosMarkerFilename = "dontdeleteme.cypress";

	private void OnCheckTos()
	{
		string markerPath = Path.Combine(AppContext.BaseDirectory, "assets", s_tosMarkerFilename);
		bool accepted = File.Exists(markerPath);
		Send(new JObject { ["type"] = "tosStatus", ["accepted"] = accepted });
	}

	private void OnAcceptTos()
	{
		string assetsDir = Path.Combine(AppContext.BaseDirectory, "assets");
		Directory.CreateDirectory(assetsDir);
		string markerPath = Path.Combine(assetsDir, s_tosMarkerFilename);
		try
		{
			File.WriteAllText(markerPath, "tos accepted - do not delete this file");
		}
		catch { }
	}

	private void OnInit()
	{
		// The in-page picker is independent of WinForms, WebView2 native file
		// chooser support and OS shell integration. It is available in every host.
		Send(new JObject { ["type"] = "capabilities", ["filesystemPicker"] = true });
		GetLastSelectedGame(out PVZGame lastGame);
		m_selectedGame = lastGame;
		LoadAndSendUserData(lastGame.ToString());
	}

	private void OnGameChanged(string gameName)
	{
		if (Enum.TryParse<PVZGame>(gameName, out PVZGame game))
		{
			string previousGame = m_selectedGame.ToString();
			SaveUserData(previousGame);
			m_selectedGame = game;
			LoadAndSendUserData(game.ToString());
		}
	}

	private void OnGetMapBg(string key)
	{
		string? b64 = GetAssetBackground("mapbgs", key);
		Send(new JObject { ["type"] = "mapBg", ["key"] = key, ["data"] = b64 ?? "" });
	}

	private void OnGetModeBg(string key)
	{
		string? b64 = GetAssetBackground("modebgs", key);
		Send(new JObject { ["type"] = "modeBg", ["key"] = key, ["data"] = b64 ?? "" });
	}

	private void OnGetCharIcon(string key)
	{
		string? b64 = GetAssetIconAspectPng(key, 64);
		Send(new JObject { ["type"] = "charIcon", ["key"] = key, ["data"] = b64 ?? "" });
	}

	private void OnGetAiSetBg(string key)
	{
		string? b64 = GetAssetIconJpeg(key, 320);
		Send(new JObject { ["type"] = "aiSetBg", ["key"] = key, ["data"] = b64 ?? "" });
	}

	private string? GetAssetBackground(string folder, string key)
	{
		if (string.IsNullOrEmpty(key) || key.Contains(".."))
			return null;

		string safePath = key.Replace('/', Path.DirectorySeparatorChar);
		string baseDir = Path.Combine(AppContext.BaseDirectory, "assets", folder, safePath);
		foreach (string ext in new[] { ".webp", ".jpg", ".png" })
		{
			string path = baseDir + ext;
			if (!File.Exists(path)) continue;
			try { return ImageHelper.ResizeByWidthToJpegBase64(path, 4096, 85); }
			catch { }
		}
		return null;
	}

	private string? GetAssetIconAspectPng(string key, int maxHeight)
	{
		if (string.IsNullOrEmpty(key) || key.Contains(".."))
			return null;

		string safePath = key.Replace('/', Path.DirectorySeparatorChar);
		string baseDir = Path.Combine(AppContext.BaseDirectory, "assets", safePath);
		foreach (string ext in new[] { ".webp", ".png" })
		{
			string path = baseDir + ext;
			if (!File.Exists(path)) continue;
			try { return ImageHelper.ResizeByHeightToPngBase64(path, maxHeight); }
			catch { }
		}
		return null;
	}

	private string? GetAssetIconPng(string key, int size)
	{
		if (string.IsNullOrEmpty(key) || key.Contains(".."))
			return null;

		string safePath = key.Replace('/', Path.DirectorySeparatorChar);
		string baseDir = Path.Combine(AppContext.BaseDirectory, "assets", safePath);
		foreach (string ext in new[] { ".webp", ".png" })
		{
			string path = baseDir + ext;
			if (!File.Exists(path)) continue;
			try { return ImageHelper.ResizeToSquarePngBase64(path, size); }
			catch { }
		}
		return null;
	}

	private string? GetAssetIconJpeg(string key, int maxWidth)
	{
		if (string.IsNullOrEmpty(key) || key.Contains(".."))
			return null;

		string safePath = key.Replace('/', Path.DirectorySeparatorChar);
		string baseDir = Path.Combine(AppContext.BaseDirectory, "assets", safePath);
		foreach (string ext in new[] { ".webp", ".jpg", ".png" })
		{
			string path = baseDir + ext;
			if (!File.Exists(path)) continue;
			try { return ImageHelper.ResizeByWidthToJpegBase64(path, maxWidth, 80); }
			catch { }
		}
		return null;
	}

	private void OnOpenExternal(string? url)
	{
		if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
		{
			SendStatus("Invalid link.", "error");
			return;
		}

		if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
		{
			SendStatus("Only http/https links are allowed.", "error");
			return;
		}

		try
		{
			if (Program.IsBrowserFallback && m_browserExternalUrlOpener != null)
			{
				m_browserExternalUrlOpener(uri.AbsoluteUri);
				return;
			}

			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = uri.AbsoluteUri,
				UseShellExecute = true,
			});
		}
		catch (Exception ex)
		{
			SendStatus("Failed to open browser: " + ex.Message, "error");
		}
	}

	private void OnSelectGameDir()
	{
		OnBrowseFileSystem("folder", null);
	}

	private void OnSelectServerIcon()
	{
		OnBrowseFileSystem("image", null);
	}

	private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"
	};

	private void OnBrowseFileSystem(string? modeValue, string? requestedPath)
	{
		string mode = string.Equals(modeValue, "image", StringComparison.OrdinalIgnoreCase) ? "image" : "folder";
		try
		{
			string path = requestedPath?.Trim() ?? "";
			// A dedicated sentinel distinguishes the virtual drives view from the
			// initial empty request, which should open the user's home directory.
			bool showRoots = string.Equals(path, "__CYPRESS_ROOTS__", StringComparison.Ordinal);
			if (path.Length == 0)
			{
				string preferred = mode == "folder" && Directory.Exists(m_gameDirectory)
					? m_gameDirectory
					: mode == "image"
						? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
						: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				path = Directory.Exists(preferred) ? preferred : "";
			}

			var entries = new JArray();
			string currentPath = "";
			string? parentPath = null;
			bool truncated = false;

			if (showRoots || path.Length == 0)
			{
				string[] roots;
				try { roots = Directory.GetLogicalDrives(); }
				catch { roots = Array.Empty<string>(); }
				if (roots.Length == 0)
				{
					string rootPath = Path.GetPathRoot(Path.GetFullPath(Path.DirectorySeparatorChar.ToString())) ?? Path.DirectorySeparatorChar.ToString();
					roots = new[] { rootPath };
				}
				foreach (string drive in roots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
					entries.Add(new JObject { ["name"] = drive, ["path"] = drive, ["isDirectory"] = true });
			}
			else
			{
				currentPath = Path.GetFullPath(path);
				if (!Directory.Exists(currentPath))
					throw new DirectoryNotFoundException("That folder does not exist or cannot be accessed.");
				try { parentPath = Directory.GetParent(currentPath)?.FullName; } catch { }

				var dirs = new List<DirectoryInfo>();
				try
				{
					dirs.AddRange(new DirectoryInfo(currentPath).EnumerateDirectories()
						.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
						.Take(500));
				}
				catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException("Cypress cannot access this folder. Try one of its parent folders or another drive."); }
				catch (IOException) { throw; }

				foreach (DirectoryInfo dir in dirs)
					entries.Add(new JObject { ["name"] = dir.Name, ["path"] = dir.FullName, ["isDirectory"] = true });

				if (mode == "image")
				{
					var files = new List<FileInfo>();
					try
					{
						files.AddRange(new DirectoryInfo(currentPath).EnumerateFiles()
							.Where(f => s_imageExtensions.Contains(f.Extension))
							.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
							.Take(500));
					}
					catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException("Cypress cannot list files in this folder."); }
					foreach (FileInfo file in files)
						entries.Add(new JObject { ["name"] = file.Name, ["path"] = file.FullName, ["isDirectory"] = false });
				}
				truncated = entries.Count >= 500;
			}

			Send(new JObject
			{
				["type"] = "fileSystemListing",
				["mode"] = mode,
				["path"] = currentPath,
				["parent"] = parentPath,
				["entries"] = entries,
				["truncated"] = truncated,
				["ok"] = true
			});
		}
		catch (Exception ex)
		{
			Send(new JObject
			{
				["type"] = "fileSystemListing",
				["mode"] = mode,
				["path"] = string.Equals(requestedPath, "__CYPRESS_ROOTS__", StringComparison.Ordinal) ? "" : requestedPath ?? "",
				["entries"] = new JArray(),
				["ok"] = false,
				["error"] = ex.Message
			});
		}
	}

	private void OnSetGameDirectory(string? candidatePath)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(candidatePath))
				throw new InvalidOperationException("Choose a game folder first.");

			string fullPath = Path.GetFullPath(candidatePath.Trim());
			string executableName = s_gameToExecutableName[m_selectedGame];
			if (!Directory.Exists(fullPath))
				throw new DirectoryNotFoundException("That folder does not exist.");
			if (!File.Exists(Path.Combine(fullPath, executableName)))
				throw new InvalidOperationException("This folder does not contain " + executableName + ". Open the game's installation folder, not a parent folder.");

			m_gameDirectory = fullPath;
			SaveUserData(m_selectedGame.ToString());
			Send(new JObject { ["type"] = "gameDir", ["path"] = fullPath });
			SendStatus("Game directory set to: " + fullPath, "success");
		}
		catch (Exception ex)
		{
			SendStatus("Could not use that game folder: " + ex.Message, "error");
		}
	}

	private void OnSelectServerIconPath(string? filePath)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(filePath))
				throw new InvalidOperationException("Choose an image first.");
			string fullPath = Path.GetFullPath(filePath.Trim());
			if (!File.Exists(fullPath) || !s_imageExtensions.Contains(Path.GetExtension(fullPath)))
				throw new InvalidOperationException("Choose a PNG, JPG, WEBP, BMP, GIF or TIFF image.");

			string imageData = ImageHelper.ResizeToSquareJpegBase64(fullPath, 64, 85);
			if (string.IsNullOrEmpty(imageData))
				throw new InvalidDataException("That image could not be decoded.");
			Send(new JObject { ["type"] = "serverIconSelected", ["data"] = imageData });
			SendStatus("Server icon selected.", "success");
		}
		catch (Exception ex)
		{
			SendStatus("Could not use that image: " + ex.Message, "error");
		}
	}

	private string? TrySilentAutoFindDir()
	{
#if WINDOWS
		return WindowsSilentAutoFindDir();
#else
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string[] searchPaths = {
			Path.Combine(home, ".steam", "steam", "steamapps", "common"),
			Path.Combine(home, ".local", "share", "Steam", "steamapps", "common"),
		};
		string gameDirName = m_selectedGame switch
		{
			PVZGame.GW1 => "Plants vs Zombies Garden Warfare",
			PVZGame.GW2 => "Plants vs. Zombies Garden Warfare 2",
			PVZGame.BFN => "Plants vs. Zombies Battle for Neighborville",
			_ => ""
		};
		foreach (string basePath in searchPaths)
		{
			string candidate = Path.Combine(basePath, gameDirName);
			if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, s_gameToExecutableName[m_selectedGame])))
				return candidate;
		}
		return null;
#endif
	}

#if WINDOWS
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private string? WindowsSilentAutoFindDir()
	{
		Microsoft.Win32.RegistryKey? registryKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\PopCap")
			?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SOFTWARE\\PopCap");
		if (registryKey != null)
		{
			Microsoft.Win32.RegistryKey? gameKey = m_selectedGame switch
			{
				PVZGame.GW1 => registryKey.OpenSubKey("Plants vs Zombies Garden Warfare"),
				PVZGame.GW2 => registryKey.OpenSubKey("Plants vs Zombies GW2"),
				PVZGame.BFN => registryKey.OpenSubKey("PVZ Battle for Neighborville"),
				_ => null
			};
			if (gameKey?.GetValue("Install Dir") is string path
				&& Directory.Exists(path)
				&& File.Exists(Path.Combine(path, s_gameToExecutableName[m_selectedGame])))
				return path;
		}
		return null;
	}
#endif

	private void OnAutoFindDir()
	{
#if WINDOWS
		WindowsAutoFindDir();
#else
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string[] searchPaths = {
			Path.Combine(home, ".steam", "steam", "steamapps", "common"),
			Path.Combine(home, ".local", "share", "Steam", "steamapps", "common"),
		};
		string gameDirName = m_selectedGame switch
		{
			PVZGame.GW1 => "Plants vs Zombies Garden Warfare",
			PVZGame.GW2 => "Plants vs. Zombies Garden Warfare 2",
			PVZGame.BFN => "Plants vs. Zombies Battle for Neighborville",
			_ => ""
		};

		foreach (string basePath in searchPaths)
		{
			string candidate = Path.Combine(basePath, gameDirName);
			if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, s_gameToExecutableName[m_selectedGame])))
			{
				m_gameDirectory = candidate;
				Send(new JObject { ["type"] = "gameDir", ["path"] = candidate });
				SendStatus($"Found directory for {m_selectedGame}: {candidate}", "success");
				return;
			}
		}

		SendStatus("Could not automatically find directory", "error");
#endif
	}

#if WINDOWS
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private void WindowsAutoFindDir()
	{
		Microsoft.Win32.RegistryKey? registryKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\PopCap")
			?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SOFTWARE\\PopCap");
		if (registryKey != null)
		{
			Microsoft.Win32.RegistryKey? gameKey = m_selectedGame switch
			{
				PVZGame.GW1 => registryKey.OpenSubKey("Plants vs Zombies Garden Warfare"),
				PVZGame.GW2 => registryKey.OpenSubKey("Plants vs Zombies GW2"),
				PVZGame.BFN => registryKey.OpenSubKey("PVZ Battle for Neighborville"),
				_ => null
			};

			if (gameKey?.GetValue("Install Dir") is string path
				&& Directory.Exists(path)
				&& File.Exists(Path.Combine(path, s_gameToExecutableName[m_selectedGame])))
			{
				m_gameDirectory = path;
				Send(new JObject { ["type"] = "gameDir", ["path"] = path });
				SendStatus($"Found directory for {m_selectedGame}: {path}", "success");
				return;
			}
		}
		SendStatus("Could not automatically find directory", "error");
	}
#endif

	private void OnGetModPacks()
	{
		var packs = new JArray();
		string modDataPath = Path.Combine(m_gameDirectory, "ModData");
		if (Directory.Exists(m_gameDirectory) && Directory.Exists(modDataPath))
		{
			foreach (string dir in Directory.GetDirectories(modDataPath))
				packs.Add(dir.Split('\\').Last());
		}
		Send(new JObject { ["type"] = "modPacks", ["packs"] = packs });
	}

	private void OnGetPlaylists()
	{
		var files = new JArray();
		string playlistPath = Path.Combine(m_gameDirectory, "Playlists");
		if (Directory.Exists(playlistPath))
		{
			foreach (string file in Directory.GetFiles(playlistPath))
				files.Add(file.Split('\\').Last());
		}
		Send(new JObject { ["type"] = "playlists", ["files"] = files });
	}

	private void OnSavePlaylist(string name, string content)
	{
		if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
		{
			Send(new JObject { ["type"] = "playlistSaved", ["ok"] = false, ["error"] = "Name and content required" });
			return;
		}

		// sanitize filename
		foreach (char c in Path.GetInvalidFileNameChars())
			name = name.Replace(c, '_');

		if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
			name += ".json";

		try
		{
			string dir = Path.Combine(m_gameDirectory, "Playlists");
			Directory.CreateDirectory(dir);
			File.WriteAllText(Path.Combine(dir, name), content);
			Send(new JObject { ["type"] = "playlistSaved", ["ok"] = true, ["name"] = name });
			OnGetPlaylists(); // refresh the list
		}
		catch (Exception ex)
		{
			Send(new JObject { ["type"] = "playlistSaved", ["ok"] = false, ["error"] = ex.Message });
		}
	}

	private void OnLoadPlaylist(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			Send(new JObject { ["type"] = "playlistLoaded", ["ok"] = false, ["error"] = "No filename" });
			return;
		}

		try
		{
			string filePath = Path.Combine(m_gameDirectory, "Playlists", name);
			if (!File.Exists(filePath))
			{
				Send(new JObject { ["type"] = "playlistLoaded", ["ok"] = false, ["error"] = "File not found" });
				return;
			}
			string content = File.ReadAllText(filePath);
			Send(new JObject { ["type"] = "playlistLoaded", ["ok"] = true, ["name"] = name, ["content"] = content });
		}
		catch (Exception ex)
		{
			Send(new JObject { ["type"] = "playlistLoaded", ["ok"] = false, ["error"] = ex.Message });
		}
	}

	private void OnDeletePlaylist(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			Send(new JObject { ["type"] = "playlistDeleted", ["ok"] = false, ["error"] = "No filename" });
			return;
		}

		try
		{
			string filePath = Path.Combine(m_gameDirectory, "Playlists", name);
			if (File.Exists(filePath))
				File.Delete(filePath);
			Send(new JObject { ["type"] = "playlistDeleted", ["ok"] = true, ["name"] = name });
			OnGetPlaylists();
		}
		catch (Exception ex)
		{
			Send(new JObject { ["type"] = "playlistDeleted", ["ok"] = false, ["error"] = ex.Message });
		}
	}
}
