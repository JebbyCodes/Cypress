#if WINDOWS
#nullable enable
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CypressLauncher;

/// <summary>Initialises WinForms process-wide once before any fallback dialogs are created.</summary>
internal static class WinFormsBootstrap
{
    private static readonly object s_gate = new();
    private static bool s_initialised;

    public static void Initialize()
    {
        lock (s_gate)
        {
            if (s_initialised) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            s_initialised = true;
        }
    }
}

/// <summary>
/// File-system pickers implemented with ordinary WinForms controls rather than
/// the Windows shell's COM-based common dialogs. The latter are unreliable in
/// Wine/Proton/GameNative and may silently fail to display.
/// </summary>
internal static class NativeDialogs
{
    public static string? PickFolder(
        string title,
        string? initialPath,
        Func<string, bool>? validator = null,
        string? validationMessage = null)
    {
        return RunSta(() =>
        {
            using var dialog = new FolderPickerForm(title, initialPath, validator, validationMessage);
            return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
        });
    }

    private static string? RunSta(Func<string?> body)
    {
        string? result = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                WinFormsBootstrap.Initialize();
                result = body();
            }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "Cypress.FolderPicker"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(15)))
            throw new TimeoutException("The folder picker did not close within 15 minutes.");

        if (failure != null)
            throw new InvalidOperationException("The folder picker failed: " + failure.Message, failure);

        return result;
    }

    public static string? PickImageFile(string title, string? initialFile = null)
    {
        return RunSta(() =>
        {
            using var dialog = new FilePickerForm(title, initialFile,
                new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff" });
            return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedFile : null;
        });
    }

    private sealed class FilePickerForm : Form
    {
        private sealed class Entry
        {
            public string Path { get; }
            public string Name { get; }
            public bool IsDirectory { get; }
            public Entry(string path, string name, bool isDirectory)
            {
                Path = path;
                Name = name;
                IsDirectory = isDirectory;
            }
            public override string ToString() => IsDirectory ? "[Folder]  " + Name : Name;
        }

        private readonly TextBox m_pathBox;
        private readonly ListBox m_entries;
        private readonly Label m_status;
        private readonly Button m_openButton;
        private readonly string[] m_extensions;
        private string? m_currentPath;
        public string? SelectedFile { get; private set; }

        public FilePickerForm(string title, string? initialFile, string[] extensions)
        {
            m_extensions = extensions;
            Text = string.IsNullOrWhiteSpace(title) ? "Choose image" : title;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(820, 600);
            MinimumSize = new Size(620, 420);
            ShowInTaskbar = true;
            TopMost = true;
            Shown += (_, _) => BeginInvoke(new Action(() => TopMost = false));
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12)
            };
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));

            var pathPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathPanel.Controls.Add(new Label { Text = "Location:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 0) }, 0, 0);

            m_pathBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
            m_pathBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    NavigateToPathBox();
                }
            };
            var go = new Button { Text = "Go", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            go.Click += (_, _) => NavigateToPathBox();
            var up = new Button { Text = "Up", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            up.Click += (_, _) => NavigateUp();
            var refresh = new Button { Text = "Refresh", AutoSize = true };
            refresh.Click += (_, _) => RefreshCurrentPath();
            pathPanel.Controls.Add(m_pathBox, 1, 0);
            pathPanel.Controls.Add(go, 2, 0);
            pathPanel.Controls.Add(up, 3, 0);
            pathPanel.Controls.Add(refresh, 4, 0);

            m_entries = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                HorizontalScrollbar = true,
                Margin = new Padding(0)
            };
            m_entries.SelectedIndexChanged += (_, _) => UpdateSelectionState();
            m_entries.DoubleClick += (_, _) => OpenSelectedEntry();

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 10, 0, 0) };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            m_status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 0, 4) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            m_openButton = new Button { Text = "Choose image", AutoSize = true, Enabled = false };
            m_openButton.Click += (_, _) => ChooseSelectedFile();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(m_openButton);
            footer.Controls.Add(m_status, 0, 0);
            footer.Controls.Add(buttons, 0, 1);

            outer.Controls.Add(pathPanel, 0, 0);
            outer.Controls.Add(m_entries, 0, 1);
            outer.Controls.Add(footer, 0, 2);
            Controls.Add(outer);
            AcceptButton = go;
            CancelButton = cancel;

            if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
                NavigateTo(Path.GetDirectoryName(initialFile)!);
            else if (!string.IsNullOrWhiteSpace(initialFile) && Directory.Exists(initialFile))
                NavigateTo(initialFile);
            else
                ShowDriveRoots();
        }

        private void NavigateToPathBox()
        {
            string path = m_pathBox.Text.Trim().Trim('"');
            if (path.Length == 0) { ShowDriveRoots(); return; }
            path = Environment.ExpandEnvironmentVariables(path);
            if (File.Exists(path))
            {
                if (IsAllowedFile(path))
                {
                    SelectedFile = Path.GetFullPath(path);
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else m_status.Text = "Choose an image file (PNG, JPG, WEBP, BMP, GIF or TIFF).";
                return;
            }
            NavigateTo(path);
        }

        private void NavigateTo(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!Directory.Exists(fullPath))
                {
                    m_currentPath = null;
                    m_pathBox.Text = path;
                    m_entries.Items.Clear();
                    m_openButton.Enabled = false;
                    m_status.Text = "That folder does not exist or cannot be accessed. Type another path above.";
                    return;
                }
                m_currentPath = fullPath;
                m_pathBox.Text = fullPath;
                m_entries.BeginUpdate();
                m_entries.Items.Clear();
                try
                {
                    foreach (string directory in Directory.GetDirectories(fullPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        m_entries.Items.Add(new Entry(directory, string.IsNullOrEmpty(name) ? directory : name, true));
                    }
                    foreach (string file in Directory.GetFiles(fullPath)
                        .Where(IsAllowedFile)
                        .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
                    {
                        m_entries.Items.Add(new Entry(file, Path.GetFileName(file), false));
                    }
                    m_status.Text = "Choose an image file. Double-click a folder to open it.";
                }
                catch (Exception ex) { m_status.Text = "Cannot list this folder: " + ex.Message; }
                finally { m_entries.EndUpdate(); }
                UpdateSelectionState();
            }
            catch (Exception ex)
            {
                m_status.Text = "Invalid folder path: " + ex.Message;
                m_openButton.Enabled = false;
            }
        }

        private void ShowDriveRoots()
        {
            m_currentPath = null;
            m_pathBox.Text = string.Empty;
            m_entries.BeginUpdate();
            m_entries.Items.Clear();
            try
            {
                foreach (string drive in Directory.GetLogicalDrives().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(drive))
                        m_entries.Items.Add(new Entry(drive, drive, true));
                }
                m_status.Text = m_entries.Items.Count > 0
                    ? "Select a drive to browse, or type a full file or folder path above."
                    : "No accessible drives were found. Type a full image path above.";
            }
            catch (Exception ex) { m_status.Text = "Could not enumerate drives: " + ex.Message; }
            finally { m_entries.EndUpdate(); }
            m_openButton.Enabled = false;
        }

        private void OpenSelectedEntry()
        {
            if (m_entries.SelectedItem is not Entry entry) return;
            if (entry.IsDirectory) NavigateTo(entry.Path);
            else ChooseSelectedFile();
        }

        private void NavigateUp()
        {
            if (string.IsNullOrEmpty(m_currentPath)) { ShowDriveRoots(); return; }
            try
            {
                DirectoryInfo? parent = Directory.GetParent(m_currentPath);
                if (parent == null) ShowDriveRoots();
                else NavigateTo(parent.FullName);
            }
            catch (Exception ex) { m_status.Text = "Cannot move to the parent folder: " + ex.Message; }
        }

        private void RefreshCurrentPath()
        {
            if (string.IsNullOrEmpty(m_currentPath)) ShowDriveRoots();
            else NavigateTo(m_currentPath);
        }

        private void UpdateSelectionState()
        {
            bool selected = m_entries.SelectedItem is Entry entry && !entry.IsDirectory && IsAllowedFile(entry.Path);
            m_openButton.Enabled = selected;
            if (selected && m_entries.SelectedItem is Entry selectedEntry)
                m_status.Text = "Selected: " + selectedEntry.Name;
        }

        private void ChooseSelectedFile()
        {
            if (m_entries.SelectedItem is not Entry entry || entry.IsDirectory || !File.Exists(entry.Path)) return;
            SelectedFile = entry.Path;
            DialogResult = DialogResult.OK;
            Close();
        }

        private bool IsAllowedFile(string path)
        {
            string extension = Path.GetExtension(path);
            return m_extensions.Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class FolderPickerForm : Form
    {
        private sealed class FolderEntry
        {
            public string Path { get; }
            public string Name { get; }
            public FolderEntry(string path, string name) { Path = path; Name = name; }
            public override string ToString() => Name;
        }

        private readonly TextBox m_pathBox;
        private readonly ListBox m_folderList;
        private readonly Label m_status;
        private readonly Button m_selectButton;
        private readonly Func<string, bool>? m_validator;
        private readonly string m_validationMessage;
        private string? m_currentPath;

        public string? SelectedPath { get; private set; }

        public FolderPickerForm(
            string title,
            string? initialPath,
            Func<string, bool>? validator,
            string? validationMessage)
        {
            m_validator = validator;
            m_validationMessage = string.IsNullOrWhiteSpace(validationMessage)
                ? "Choose a valid folder."
                : validationMessage!;

            Text = string.IsNullOrWhiteSpace(title) ? "Choose folder" : title;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(860, 620);
            MinimumSize = new Size(640, 420);
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = true;
            TopMost = true;
            Shown += (_, _) => BeginInvoke(new Action(() => TopMost = false));
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = SystemColors.Control;
            KeyPreview = true;

            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12),
                BackColor = SystemColors.Control
            };
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));

            var pathPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var pathLabel = new Label
            {
                Text = "Location:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 7, 8, 0)
            };
            m_pathBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 8, 0)
            };
            m_pathBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    NavigateToPathBox();
                }
            };

            var goButton = new Button { Text = "Go", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            goButton.Click += (_, _) => NavigateToPathBox();
            var upButton = new Button { Text = "Up", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            upButton.Click += (_, _) => NavigateUp();
            var refreshButton = new Button { Text = "Refresh", AutoSize = true };
            refreshButton.Click += (_, _) => RefreshCurrentPath();

            pathPanel.Controls.Add(pathLabel, 0, 0);
            pathPanel.Controls.Add(m_pathBox, 1, 0);
            pathPanel.Controls.Add(goButton, 2, 0);
            pathPanel.Controls.Add(upButton, 3, 0);
            pathPanel.Controls.Add(refreshButton, 4, 0);

            m_folderList = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                HorizontalScrollbar = true,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Margin = new Padding(0)
            };
            m_folderList.DoubleClick += (_, _) => OpenSelectedFolder();

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 10, 0, 0)
            };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            m_status = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SystemColors.ControlText,
                Margin = new Padding(0, 0, 0, 4)
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0)
            };
            var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            m_selectButton = new Button { Text = "Use this folder", AutoSize = true, Enabled = false };
            m_selectButton.Click += (_, _) => SelectCurrentFolder();
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(m_selectButton);

            footer.Controls.Add(m_status, 0, 0);
            footer.Controls.Add(buttons, 0, 1);
            outer.Controls.Add(pathPanel, 0, 0);
            outer.Controls.Add(m_folderList, 0, 1);
            outer.Controls.Add(footer, 0, 2);
            Controls.Add(outer);

            AcceptButton = goButton;
            CancelButton = cancelButton;

            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
                NavigateTo(initialPath!);
            else
                ShowDriveRoots();
        }

        private void NavigateToPathBox()
        {
            string path = m_pathBox.Text.Trim().Trim('"');
            if (path.Length == 0)
            {
                ShowDriveRoots();
                return;
            }
            NavigateTo(Environment.ExpandEnvironmentVariables(path));
        }

        private void NavigateTo(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!Directory.Exists(fullPath))
                {
                    m_status.Text = "That folder does not exist or cannot be accessed. You can enter another path above.";
                    m_currentPath = null;
                    m_pathBox.Text = path;
                    m_folderList.Items.Clear();
                    m_selectButton.Enabled = false;
                    return;
                }

                m_currentPath = fullPath;
                m_pathBox.Text = fullPath;
                m_folderList.BeginUpdate();
                m_folderList.Items.Clear();
                try
                {
                    foreach (string directory in Directory.GetDirectories(fullPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (string.IsNullOrEmpty(name)) name = directory;
                        m_folderList.Items.Add(new FolderEntry(directory, name));
                    }
                    m_status.Text = m_folderList.Items.Count == 0
                        ? "This folder contains no subfolders. You can still select it if it is valid."
                        : $"{m_folderList.Items.Count} subfolder(s). Double-click a folder to open it.";
                }
                catch (Exception ex)
                {
                    m_status.Text = "Cannot list this folder: " + ex.Message;
                }
                finally
                {
                    m_folderList.EndUpdate();
                }
                UpdateSelectionState();
            }
            catch (Exception ex)
            {
                m_status.Text = "Invalid folder path: " + ex.Message;
                m_selectButton.Enabled = false;
            }
        }

        private void ShowDriveRoots()
        {
            m_currentPath = null;
            m_pathBox.Text = string.Empty;
            m_folderList.BeginUpdate();
            m_folderList.Items.Clear();
            try
            {
                foreach (string drive in Directory.GetLogicalDrives().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(drive))
                        m_folderList.Items.Add(new FolderEntry(drive, drive));
                }
            }
            catch (Exception ex)
            {
                m_status.Text = "Could not enumerate available drives: " + ex.Message;
            }
            finally
            {
                m_folderList.EndUpdate();
            }
            if (m_folderList.Items.Count > 0)
                m_status.Text = "Select a drive to browse. You can also type a full path above.";
            else if (string.IsNullOrEmpty(m_status.Text))
                m_status.Text = "No accessible drives were found. Type the folder path above.";
            m_selectButton.Enabled = false;
        }

        private void OpenSelectedFolder()
        {
            if (m_folderList.SelectedItem is not FolderEntry entry)
                return;
            NavigateTo(entry.Path);
        }

        private void NavigateUp()
        {
            if (string.IsNullOrEmpty(m_currentPath))
            {
                ShowDriveRoots();
                return;
            }

            try
            {
                DirectoryInfo? parent = Directory.GetParent(m_currentPath);
                if (parent == null)
                    ShowDriveRoots();
                else
                    NavigateTo(parent.FullName);
            }
            catch (Exception ex)
            {
                m_status.Text = "Cannot move to the parent folder: " + ex.Message;
            }
        }

        private void RefreshCurrentPath()
        {
            if (string.IsNullOrEmpty(m_currentPath)) ShowDriveRoots();
            else NavigateTo(m_currentPath);
        }

        private void UpdateSelectionState()
        {
            bool valid = !string.IsNullOrEmpty(m_currentPath) && Directory.Exists(m_currentPath);
            if (valid && m_validator != null)
            {
                try { valid = m_validator(m_currentPath!); }
                catch { valid = false; }
            }
            m_selectButton.Enabled = valid;
            if (!valid && !string.IsNullOrEmpty(m_currentPath))
                m_status.Text = m_validationMessage;
        }

        private void SelectCurrentFolder()
        {
            if (string.IsNullOrEmpty(m_currentPath) || !Directory.Exists(m_currentPath))
                return;

            if (m_validator != null)
            {
                bool valid;
                try { valid = m_validator(m_currentPath); }
                catch { valid = false; }
                if (!valid)
                {
                    m_status.Text = m_validationMessage;
                    return;
                }
            }

            SelectedPath = m_currentPath;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
#endif
