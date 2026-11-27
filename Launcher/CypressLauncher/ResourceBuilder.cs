#nullable enable
using System;
using System.IO;
using System.Reflection;

namespace CypressLauncher;

internal static class ResourceBuilder
{
	private static readonly string[] s_cssOrder =
	{
		"wwwroot.css.base.css",
		"wwwroot.css.titlebar.css",
		"wwwroot.css.layout.css",
		"wwwroot.css.sidebar.css",
		"wwwroot.css.profile.css",
		"wwwroot.css.forms.css",
		"wwwroot.css.motd.css",
		"wwwroot.css.content.css",
		"wwwroot.css.host.css",
		"wwwroot.css.docs.css",
		"wwwroot.css.sections.css",
		"wwwroot.css.pickers.css",
		"wwwroot.css.modifiers.css",
		"wwwroot.css.bfn.css",
		"wwwroot.css.playlist.css",
		"wwwroot.css.steps.css",
		"wwwroot.css.instances.css",
		"wwwroot.css.browser.css",
		"wwwroot.css.auth.css",
		"wwwroot.css.moderator.css",
		"wwwroot.css.filepicker.css",
	};

	private static readonly string[] s_jsOrder =
	{
		"wwwroot.js.i18n.js",
		"wwwroot.js.data.js",
		"wwwroot.js.auth.js",
		"wwwroot.js.core.js",
		"wwwroot.js.tabs.js",
		"wwwroot.js.helpers.js",
		"wwwroot.js.relay.js",
		"wwwroot.js.pickers.js",
		"wwwroot.js.levelmode.js",
		"wwwroot.js.modifiers.js",
		"wwwroot.js.settings.js",
		"wwwroot.js.profile.js",
		"wwwroot.js.actions.js",
		"wwwroot.js.motd.js",
		"wwwroot.js.serverinfo.js",
		"wwwroot.js.filepicker.js",
		"wwwroot.js.browser.js",
		"wwwroot.js.playlist.js",
		"wwwroot.js.instances.js",
		"wwwroot.js.moderator.js",
		"wwwroot.js.updates.js",
		"wwwroot.js.docs.js",
		"wwwroot.js.init.js",
	};

	public static string BuildHtml(Assembly assembly, string? browserBridgeToken = null)
	{
		string html = ReadEmbeddedResource(assembly, "wwwroot.index.html");
		string css = ConcatEmbeddedResources(assembly, s_cssOrder);
		string js = ConcatEmbeddedResources(assembly, s_jsOrder);

		html = html.Replace("{{LOGO_BASE64}}", GetLogoBase64(256));
		html = html.Replace("{{GW1_ICON_BASE64}}", GetGameIconBase64("gw1_icon", 120));
		html = html.Replace("{{GW2_ICON_BASE64}}", GetGameIconBase64("gw2_icon", 120));
		html = html.Replace("{{BFN_ICON_BASE64}}", GetGameIconBase64("bfn_icon", 120));
		html = html.Replace("{{GW1_BG_BASE64}}", GetGameBgBase64("gw1_bg", 960));
		html = html.Replace("{{GW2_BG_BASE64}}", GetGameBgBase64("gw2_bg", 960));
		html = html.Replace("{{BFN_BG_BASE64}}", GetGameBgBase64("bfn_bg", 960));

		string fontFace = GetBurbankFontFace();
		css = fontFace + css;

		html = html.Replace("<link rel=\"stylesheet\" href=\"styles.css\">", "<style>" + css + "</style>");
		html = html.Replace("<script src=\"app.js\"></script>", "<script>" + js + "</script>");

		if (!string.IsNullOrWhiteSpace(browserBridgeToken))
		{
			// The normal Photino page hides its body until its first translations
			// message. In browser mode that message arrives asynchronously over the
			// HTTP bridge, so force the UI visible and expose bridge status instead.
			string browserStyle = "<style>html,body{visibility:visible!important}</style>";
			string bridgeScript = BuildBrowserBridgeScript(browserBridgeToken);
			html = html.Replace("</head>", browserStyle + "\n" + bridgeScript + "\n</head>", StringComparison.OrdinalIgnoreCase);
		}

		return html;
	}

	private static string BuildBrowserBridgeScript(string token)
	{
		// Keep this script self-contained: SimpleWebView2Browser loads the UI
		// over loopback, not through Photino's native window.message API.
		const string tokenPlaceholder = "__CYPRESS_BROWSER_BRIDGE_TOKEN__";
		string script = """
<script>
(() => {
    const apiRoot = new URL('/__CYPRESS_BROWSER_BRIDGE_TOKEN__/api/', window.location.origin).toString();
    let receiveCallback = null;
    let cursor = 0;
    let polling = false;
    let statusElement = null;

    function setBridgeStatus(text, connected) {
        if (statusElement) {
            statusElement.textContent = text;
            statusElement.style.background = connected ? '#3c3836' : '#7c2d12';
        }
    }

    const addFallbackNotice = () => {
        if (document.getElementById('cypressBrowserNotice')) return;
        const notice = document.createElement('div');
        notice.id = 'cypressBrowserNotice';
        notice.style.cssText = 'position:fixed;top:8px;right:8px;z-index:2147483647;display:flex;align-items:center;gap:10px;padding:7px 10px;border-radius:8px;background:#282828;color:#ebdbb2;font:12px system-ui;box-shadow:0 2px 12px #0008;';
        const label = document.createElement('span');
        label.textContent = 'Cypress';
        statusElement = document.createElement('span');
        statusElement.textContent = 'Connecting…';
        statusElement.style.cssText = 'padding:3px 6px;border-radius:5px;background:#7c2d12;color:#fff;';
        const close = document.createElement('button');
        close.type = 'button';
        close.textContent = 'Exit';
        close.style.cssText = 'border:0;border-radius:5px;padding:5px 8px;background:#d65d0e;color:#fff;font:600 12px system-ui;cursor:pointer;';
        close.addEventListener('click', () => window.cypressBridge.sendMessage(JSON.stringify({ type: 'windowClose' })));
        notice.append(label, statusElement, close);
        document.body.appendChild(notice);
    };

    async function probeServer() {
        try {
            const response = await fetch(apiRoot + 'health', { cache: 'no-store' });
            if (!response.ok) throw new Error('HTTP ' + response.status);
            setBridgeStatus('Connected', true);
        } catch (error) {
            setBridgeStatus('Reconnecting…', false);
            console.error('Cypress local server is not reachable.', error);
        }
    }

    async function pollMessages() {
        if (polling) return;
        polling = true;
        while (receiveCallback) {
            try {
                const response = await fetch(apiRoot + 'poll?after=' + cursor, { cache: 'no-store' });
                if (!response.ok) throw new Error('HTTP ' + response.status);
                const messages = await response.json();
                setBridgeStatus('Connected', true);
                for (const item of messages) {
                    cursor = Math.max(cursor, Number(item.id) || cursor);
                    receiveCallback(item.message);
                }
            } catch (error) {
                setBridgeStatus('Reconnecting…', false);
                console.warn('Cypress browser bridge disconnected; retrying.', error);
                await new Promise(resolve => setTimeout(resolve, 1200));
            }
        }
        polling = false;
    }

    window.cypressBridge = {
        sendMessage(message) {
            return fetch(apiRoot + 'send', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json; charset=utf-8' },
                body: String(message)
            }).then(response => {
                if (!response.ok) throw new Error('HTTP ' + response.status);
                setBridgeStatus('Connected', true);
            }).catch(error => {
                setBridgeStatus('Reconnecting…', false);
                console.error('Cypress message could not be delivered.', error);
            });
        },
        receiveMessage(callback) {
            receiveCallback = callback;
            pollMessages();
        }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => { addFallbackNotice(); probeServer(); }, { once: true });
    } else {
        addFallbackNotice();
        probeServer();
    }
})();
</script>
""";
		return script.Replace(tokenPlaceholder, token, StringComparison.Ordinal);
	}

	private static string GetLogoBase64(int size)
	{
		string dir = Path.Combine(AppContext.BaseDirectory, "assets", "cypressicons");
		foreach (string ext in new[] { "webp", "png" })
		{
			string path = Path.Combine(dir, ext, $"Burbank-CypressIcon.{ext}");
			if (File.Exists(path))
				return ImageHelper.ResizeToSquarePngBase64(path, size);
		}
		return string.Empty;
	}

	private static string GetGameIconBase64(string baseName, int maxHeight)
	{
		string dir = Path.Combine(AppContext.BaseDirectory, "assets", "gameicons");
		foreach (string ext in new[] { "webp", "png" })
		{
			string path = Path.Combine(dir, $"{baseName}.{ext}");
			if (File.Exists(path))
				return ImageHelper.ResizeByHeightToPngBase64(path, maxHeight);
		}
		return string.Empty;
	}

	private static string GetGameBgBase64(string baseName, int maxWidth)
	{
		string dir = Path.Combine(AppContext.BaseDirectory, "assets", "gamebgs");
		foreach (string ext in new[] { "webp", "jpg", "png" })
		{
			string path = Path.Combine(dir, $"{baseName}.{ext}");
			if (File.Exists(path))
				return ImageHelper.ResizeByWidthToJpegBase64(path, maxWidth, 90);
		}
		return string.Empty;
	}

	private static string GetBurbankFontFace()
	{
		string fontsDir = Path.Combine(AppContext.BaseDirectory, "assets", "fonts");
		string[] fontNames = { "burbankbigcondensed_bold.otf", "BurbankBigCondensed-Bold.otf", "BurbankBigCondensed-Bold.ttf", "Burbank.ttf", "Burbank.otf" };

		foreach (string fontName in fontNames)
		{
			string fontPath = Path.Combine(fontsDir, fontName);
			if (!File.Exists(fontPath))
				continue;

			try
			{
				byte[] fontBytes = File.ReadAllBytes(fontPath);
				string ext = Path.GetExtension(fontPath).ToLowerInvariant();
				string mime = ext == ".otf" ? "font/otf" : "font/ttf";
				string format = ext == ".otf" ? "opentype" : "truetype";
				string b64 = Convert.ToBase64String(fontBytes);
				return $"@font-face {{ font-family: 'Burbank'; src: url('data:{mime};base64,{b64}') format('{format}'); font-weight: 700; font-style: normal; }}\n";
			}
			catch { }
		}

		return string.Empty;
	}

	public static string ReadEmbeddedResource(Assembly assembly, string resourceSuffix)
	{
		string? fullName = null;
		foreach (string name in assembly.GetManifestResourceNames())
		{
			if (name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase))
			{
				fullName = name;
				break;
			}
		}
		if (fullName == null)
			throw new FileNotFoundException("Embedded resource not found: " + resourceSuffix);

		using var stream = assembly.GetManifestResourceStream(fullName)!;
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}

	private static string ConcatEmbeddedResources(Assembly assembly, string[] suffixes)
	{
		var sb = new System.Text.StringBuilder();
		foreach (string suffix in suffixes)
			sb.AppendLine(ReadEmbeddedResource(assembly, suffix));
		return sb.ToString();
	}
}
