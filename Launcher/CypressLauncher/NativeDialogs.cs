#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CypressLauncher;

// Folder / file pickers that work off the WebView2 message thread and under
// Wine. System.Windows.Forms dialogs are COM objects and require an STA
// thread; the launcher calls HandleMessage on a non-STA thread, so the
// original OnSelectGameDir() threw ThreadStateException and the exception was
// swallowed by the dispatcher's outer catch, making Browse look dead.
internal static class NativeDialogs
{
    // Returns null when the user cancelled, or the selected path on success.
    // Throws only if the dialog itself failed, so the caller can report it.
    public static string? PickFolder(string title, string? initialPath)
    {
        return RunSta(() =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = title,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
                dlg.SelectedPath = initialPath!;
            return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
        });
    }

    public static string? PickFile(string title, string filter, string? initialPath)
    {
        return RunSta(() =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
                Multiselect = false
            };
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                try
                {
                    string? dir = Directory.Exists(initialPath)
                        ? initialPath
                        : Path.GetDirectoryName(initialPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        dlg.InitialDirectory = dir;
                }
                catch { }
            }
            return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
        });
    }

    private static string? RunSta(Func<string?> body)
    {
        string? result = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "Cypress.NativeDialog"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(10)))
            throw new TimeoutException("The file dialog did not close.");

        if (failure != null)
            throw new InvalidOperationException(failure.Message, failure);

        return result;
    }
}
