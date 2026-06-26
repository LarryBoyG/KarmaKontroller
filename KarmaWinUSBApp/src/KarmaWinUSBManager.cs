using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KarmaWinUSBApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "--force-driver", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = DriverInstallerCommand.Run(args);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "--force-libwdi-driver", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = DriverInstallerCommand.RunLibwdi(args);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private const string AppName = "Karma Kontroller";
        private const string AppVersion = "2.1";
        private const string IssueUrl = "https://github.com/LarryBoyG/KarmaKontroller/issues";
        private const string VidPid = "VID_1B8E&PID_C003";
        private const long SystemPartitionBytes = 0x40000000L;
        private const long DataPartitionBytes = 0x51bf0000L;

        private Label titleLabel;
        private Label subtitleLabel;
        private TabPage backupTab;
        private TabPage imageToolsTab;
        private Label backupFolderLabel;
        private Label backupPartitionsLabel;
        private Label backupNoteLabel;
        private Label originalSystemLabel;
        private Label patchedOutputLabel;
        private Label systemFlashLabel;
        private Label expectedCurrentLabel;
        private Label expectedHelpLabel;
        private Label dataBackupLabel;
        private Label dataBackupHelpLabel;
        private Label dataRestoreLabel;
        private TextBox activityBox;
        private Label controllerValue;
        private Label driverValue;
        private Label statusValue;
        private Label progressText;
        private TextBox backupFolderBox;
        private TextBox sourceImageBox;
        private TextBox patchedImageBox;
        private TextBox flashImageBox;
        private TextBox expectedCurrentImageBox;
        private TextBox dataBackupBox;
        private TextBox dataRestoreBox;
        private Button refreshButton;
        private Button driverButton;
        private Button identifyButton;
        private Button partitionsButton;
        private Button backupButton;
        private Button openBackupFolderButton;
        private Button patchButton;
        private Button flashSystemButton;
        private Button restoreDataButton;
        private Button backupBrowseButton;
        private Button sourceBrowseButton;
        private Button patchedBrowseButton;
        private Button flashBrowseButton;
        private Button expectedBrowseButton;
        private Button expectedClearButton;
        private Button dataBackupBrowseButton;
        private Button dataRestoreBrowseButton;
        private Button languageButton;
        private Button aboutButton;
        private Button logsButton;
        private ProgressBar progressBar;
        private System.Windows.Forms.Timer refreshTimer;
        private readonly Dictionary<string, CheckBox> backupChecks = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);

        private bool busy;
        private bool askedBackupThisSession;
        private bool autoDriverPromptShown;
        private bool autoRefreshEnabled = true;
        private int lastActivityProgress = -1;
        private string lastActivityStatus = "";
        private string currentLanguage = "en";
        private DeviceInfo currentDevice;

        public MainForm()
        {
            Text = AppTitle();
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(920, 720);
            Size = new Size(1040, 800);
            Font = new Font("Segoe UI", 9F);
            TrySetIcon();

            var main = new TableLayoutPanel();
            main.Dock = DockStyle.Fill;
            main.Padding = new Padding(14);
            main.ColumnCount = 1;
            main.RowCount = 7;
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 292));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(main);

            var header = new Panel();
            header.Dock = DockStyle.Fill;
            titleLabel = new Label();
            titleLabel.Text = AppTitle();
            titleLabel.Font = new Font(Font.FontFamily, 18F, FontStyle.Bold);
            titleLabel.AutoSize = true;
            titleLabel.Location = new Point(0, 0);
            header.Controls.Add(titleLabel);

            subtitleLabel = new Label();
            subtitleLabel.Text = "WinUSB controller tools, image patching, flashing, and recovery backups.";
            subtitleLabel.AutoSize = true;
            subtitleLabel.Location = new Point(2, 38);
            header.Controls.Add(subtitleLabel);
            main.Controls.Add(header, 0, 0);

            var statusGrid = new TableLayoutPanel();
            statusGrid.Dock = DockStyle.Fill;
            statusGrid.ColumnCount = 2;
            statusGrid.RowCount = 3;
            statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statusGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            statusGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            statusGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34F));
            main.Controls.Add(statusGrid, 0, 1);

            AddStatusRow(statusGrid, 0, "Controller", out controllerValue);
            AddStatusRow(statusGrid, 1, "Driver", out driverValue);
            AddStatusRow(statusGrid, 2, "Status", out statusValue);

            var commandRow = new TableLayoutPanel();
            commandRow.Dock = DockStyle.Fill;
            commandRow.ColumnCount = 7;
            commandRow.RowCount = 1;
            commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            main.Controls.Add(commandRow, 0, 2);

            refreshButton = MakeButton("Refresh");
            refreshButton.Dock = DockStyle.Fill;
            refreshButton.Click += delegate { RefreshController(true); };
            commandRow.Controls.Add(refreshButton, 0, 0);

            driverButton = MakeButton("Switch Driver");
            driverButton.Dock = DockStyle.Fill;
            driverButton.Click += delegate { PromptAndInstallDriver(); };
            commandRow.Controls.Add(driverButton, 1, 0);

            identifyButton = MakeButton("Identify");
            identifyButton.Dock = DockStyle.Fill;
            identifyButton.Click += delegate { RunBackend("identify", "Identify", true, null); };
            commandRow.Controls.Add(identifyButton, 2, 0);

            partitionsButton = MakeButton("Partitions");
            partitionsButton.Dock = DockStyle.Fill;
            partitionsButton.Click += delegate { RunBackend("partitions", "Partitions", true, null); };
            commandRow.Controls.Add(partitionsButton, 3, 0);

            openBackupFolderButton = MakeButton("Open Backup");
            openBackupFolderButton.Dock = DockStyle.Fill;
            openBackupFolderButton.Click += delegate { OpenBackupFolder(); };
            commandRow.Controls.Add(openBackupFolderButton, 6, 0);

            var tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            backupTab = BuildBackupTab();
            imageToolsTab = BuildImageTab();
            tabs.Controls.Add(backupTab);
            tabs.Controls.Add(imageToolsTab);
            main.Controls.Add(tabs, 0, 3);

            var progressPanel = new TableLayoutPanel();
            progressPanel.Dock = DockStyle.Fill;
            progressPanel.RowCount = 2;
            progressPanel.ColumnCount = 1;
            progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            main.Controls.Add(progressPanel, 0, 4);

            progressText = new Label();
            progressText.Text = "0% - Ready";
            progressText.Dock = DockStyle.Fill;
            progressText.TextAlign = ContentAlignment.MiddleLeft;
            progressPanel.Controls.Add(progressText, 0, 0);

            progressBar = new ProgressBar();
            progressBar.Dock = DockStyle.Fill;
            progressBar.Minimum = 0;
            progressBar.Maximum = 100;
            progressBar.Value = 0;
            progressBar.Style = ProgressBarStyle.Blocks;
            progressPanel.Controls.Add(progressBar, 0, 1);

            activityBox = new TextBox();
            activityBox.Dock = DockStyle.Fill;
            activityBox.Multiline = true;
            activityBox.ScrollBars = ScrollBars.Vertical;
            activityBox.WordWrap = false;
            activityBox.ReadOnly = true;
            activityBox.Font = new Font("Consolas", 9F);
            main.Controls.Add(activityBox, 0, 5);

            main.Controls.Add(BuildFooter(), 0, 6);

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 3000;
            refreshTimer.Tick += delegate
            {
                if (!busy && autoRefreshEnabled && currentDevice == null)
                {
                    RefreshController(false);
                }
            };
            refreshTimer.Start();

            Shown += delegate { RefreshController(false); };
            ApplyLanguage();
        }

        private TabPage BuildBackupTab()
        {
            var tab = new TabPage("Backup");
            var layout = CreateTabLayout(tab, 7);

            backupFolderLabel = AddFormLabel(layout, "Backup folder", 0);
            backupFolderBox = MakePathTextBox();
            backupFolderBox.Text = DefaultBackupFolder();
            backupFolderBox.TextChanged += delegate { SetDataRestoreFromBackupFolder(); };
            layout.Controls.Add(backupFolderBox, 0, 1);

            backupBrowseButton = MakeTableButton("Browse");
            backupBrowseButton.Click += delegate { BrowseBackupFolder(); };
            layout.Controls.Add(backupBrowseButton, 1, 1);

            backupPartitionsLabel = AddFormLabel(layout, "Partitions", 3);
            var partitionPanel = new FlowLayoutPanel();
            partitionPanel.Dock = DockStyle.Fill;
            partitionPanel.AutoSize = true;
            partitionPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            partitionPanel.WrapContents = true;
            partitionPanel.FlowDirection = FlowDirection.LeftToRight;
            partitionPanel.Margin = new Padding(0, 0, 0, 8);
            AddBackupCheck(partitionPanel, "bootloader", "Bootloader");
            AddBackupCheck(partitionPanel, "boot", "Boot");
            AddBackupCheck(partitionPanel, "recovery", "Recovery");
            AddBackupCheck(partitionPanel, "system", "System");
            AddBackupCheck(partitionPanel, "data", "Data");
            AddBackupCheck(partitionPanel, "gopro", "GoPro");
            layout.Controls.Add(partitionPanel, 0, 4);
            layout.SetColumnSpan(partitionPanel, 2);

            var actionPanel = new FlowLayoutPanel();
            actionPanel.Dock = DockStyle.Fill;
            actionPanel.AutoSize = true;
            actionPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actionPanel.WrapContents = true;
            actionPanel.FlowDirection = FlowDirection.LeftToRight;
            actionPanel.Margin = new Padding(0, 8, 0, 0);

            backupButton = MakeButton("Backup Controller");
            backupButton.AutoSize = true;
            backupButton.MinimumSize = new Size(170, 34);
            backupButton.Click += delegate { StartSelectedBackup(); };
            actionPanel.Controls.Add(backupButton);

            backupNoteLabel = new Label();
            backupNoteLabel.Text = "Data is selected by default because it is the recovery safety net before system flashing.";
            backupNoteLabel.AutoSize = true;
            backupNoteLabel.TextAlign = ContentAlignment.MiddleLeft;
            backupNoteLabel.Margin = new Padding(18, 9, 0, 0);
            backupNoteLabel.ForeColor = SystemColors.GrayText;
            actionPanel.Controls.Add(backupNoteLabel);
            layout.Controls.Add(actionPanel, 0, 5);
            layout.SetColumnSpan(actionPanel, 2);
            return tab;
        }

        private TabPage BuildImageTab()
        {
            var tab = new TabPage("Image Tools");
            var layout = CreateTabLayout(tab, 22);

            originalSystemLabel = AddFormLabel(layout, "Original system.img", 0);
            sourceImageBox = MakePathTextBox();
            layout.Controls.Add(sourceImageBox, 0, 1);
            sourceBrowseButton = MakeTableButton("Browse");
            sourceBrowseButton.Click += delegate { BrowseSourceImage(); };
            layout.Controls.Add(sourceBrowseButton, 1, 1);

            patchedOutputLabel = AddFormLabel(layout, "Patched output image", 3);
            patchedImageBox = MakePathTextBox();
            layout.Controls.Add(patchedImageBox, 0, 4);
            patchedBrowseButton = MakeTableButton("Save As");
            patchedBrowseButton.Click += delegate { BrowsePatchedImage(); };
            layout.Controls.Add(patchedBrowseButton, 1, 4);

            patchButton = MakeButton("Patch Image");
            patchButton.AutoSize = true;
            patchButton.MinimumSize = new Size(150, 34);
            patchButton.Margin = new Padding(0, 8, 0, 16);
            patchButton.Click += delegate { StartPatchImage(); };
            layout.Controls.Add(patchButton, 0, 5);
            layout.SetColumnSpan(patchButton, 2);

            systemFlashLabel = AddFormLabel(layout, "System image to flash", 7);
            flashImageBox = MakePathTextBox();
            layout.Controls.Add(flashImageBox, 0, 8);
            flashBrowseButton = MakeTableButton("Browse");
            flashBrowseButton.Click += delegate { BrowseFlashImage(); };
            layout.Controls.Add(flashBrowseButton, 1, 8);

            expectedCurrentLabel = AddFormLabel(layout, "Optional expected-current system image", 10);
            expectedCurrentImageBox = MakePathTextBox();
            layout.Controls.Add(expectedCurrentImageBox, 0, 11);

            var expectedButtons = new FlowLayoutPanel();
            expectedButtons.Dock = DockStyle.Fill;
            expectedButtons.AutoSize = true;
            expectedButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            expectedButtons.WrapContents = false;
            expectedButtons.Margin = new Padding(0, 0, 0, 8);
            expectedBrowseButton = MakeButton("Browse");
            expectedBrowseButton.MinimumSize = new Size(82, 28);
            expectedBrowseButton.AutoSize = true;
            expectedBrowseButton.Click += delegate { BrowseExpectedCurrentImage(); };
            expectedButtons.Controls.Add(expectedBrowseButton);
            expectedClearButton = MakeButton("Clear");
            expectedClearButton.MinimumSize = new Size(68, 28);
            expectedClearButton.AutoSize = true;
            expectedClearButton.Click += delegate { expectedCurrentImageBox.Text = ""; };
            expectedButtons.Controls.Add(expectedClearButton);
            layout.Controls.Add(expectedButtons, 1, 11);

            expectedHelpLabel = new Label();
            expectedHelpLabel.Text = "Leave blank for normal flashing. Only choose a systemBU.img if you want preflight verification that the controller currently matches that exact image before writing.";
            expectedHelpLabel.AutoSize = true;
            expectedHelpLabel.MaximumSize = new Size(760, 0);
            expectedHelpLabel.ForeColor = SystemColors.GrayText;
            expectedHelpLabel.Margin = new Padding(0, 0, 0, 8);
            layout.Controls.Add(expectedHelpLabel, 0, 12);
            layout.SetColumnSpan(expectedHelpLabel, 2);

            dataBackupLabel = AddFormLabel(layout, "Recommended data backup (dataBU.img)", 13);
            dataBackupBox = MakePathTextBox();
            layout.Controls.Add(dataBackupBox, 0, 14);
            dataBackupBrowseButton = MakeTableButton("Browse");
            dataBackupBrowseButton.Click += delegate { BrowseDataBackupImage(); };
            layout.Controls.Add(dataBackupBrowseButton, 1, 14);

            dataBackupHelpLabel = new Label();
            dataBackupHelpLabel.Text = "Recommended before flashing, but not required for WinUSB system-only flashes. It is a recovery safety net if /data ever needs to be restored.";
            dataBackupHelpLabel.AutoSize = true;
            dataBackupHelpLabel.MaximumSize = new Size(760, 0);
            dataBackupHelpLabel.ForeColor = SystemColors.GrayText;
            dataBackupHelpLabel.Margin = new Padding(0, 0, 0, 8);
            layout.Controls.Add(dataBackupHelpLabel, 0, 15);
            layout.SetColumnSpan(dataBackupHelpLabel, 2);

            flashSystemButton = MakeButton("Flash System");
            flashSystemButton.AutoSize = true;
            flashSystemButton.MinimumSize = new Size(150, 34);
            flashSystemButton.Margin = new Padding(0, 8, 0, 16);
            flashSystemButton.Click += delegate { StartFlashSystem(); };
            layout.Controls.Add(flashSystemButton, 0, 16);
            layout.SetColumnSpan(flashSystemButton, 2);

            dataRestoreLabel = AddFormLabel(layout, "Data image to restore", 18);
            dataRestoreBox = MakePathTextBox();
            layout.Controls.Add(dataRestoreBox, 0, 19);
            dataRestoreBrowseButton = MakeTableButton("Browse");
            dataRestoreBrowseButton.Click += delegate { BrowseDataRestoreImage(); };
            layout.Controls.Add(dataRestoreBrowseButton, 1, 19);

            restoreDataButton = MakeButton("Restore Data");
            restoreDataButton.AutoSize = true;
            restoreDataButton.MinimumSize = new Size(150, 34);
            restoreDataButton.Margin = new Padding(0, 8, 0, 0);
            restoreDataButton.Click += delegate { StartRestoreData(); };
            layout.Controls.Add(restoreDataButton, 0, 20);
            layout.SetColumnSpan(restoreDataButton, 2);

            SetDataRestoreFromBackupFolder();
            return tab;
        }

        private void AddBackupCheck(FlowLayoutPanel panel, string key, string text)
        {
            var check = new CheckBox();
            check.Text = text;
            check.Checked = true;
            check.AutoSize = true;
            check.MinimumSize = new Size(120, 28);
            check.Margin = new Padding(0, 4, 18, 4);
            panel.Controls.Add(check);
            backupChecks[key] = check;
        }

        private static TableLayoutPanel CreateTabLayout(TabPage tab, int rows)
        {
            var scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            tab.Controls.Add(scroll);

            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Top;
            layout.AutoSize = true;
            layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            layout.Padding = new Padding(12);
            layout.ColumnCount = 2;
            layout.RowCount = rows;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
            for (int i = 0; i < rows; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            scroll.Controls.Add(layout);
            return layout;
        }

        private Control BuildFooter()
        {
            var footer = new FlowLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.FlowDirection = FlowDirection.LeftToRight;
            footer.WrapContents = false;
            footer.Padding = new Padding(0, 6, 0, 0);

            languageButton = MakeButton("Language");
            languageButton.AutoSize = true;
            languageButton.MinimumSize = new Size(104, 28);
            languageButton.Click += delegate { ShowLanguageDialog(); };
            footer.Controls.Add(languageButton);

            aboutButton = MakeButton("About");
            aboutButton.AutoSize = true;
            aboutButton.MinimumSize = new Size(88, 28);
            aboutButton.Click += delegate { ShowAboutDialog(); };
            footer.Controls.Add(aboutButton);

            logsButton = MakeButton("Logs");
            logsButton.AutoSize = true;
            logsButton.MinimumSize = new Size(88, 28);
            logsButton.Click += delegate { OpenLogsFolder(); };
            footer.Controls.Add(logsButton);

            return footer;
        }

        private static Label AddFormLabel(TableLayoutPanel layout, string text, int row)
        {
            var label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Margin = new Padding(0, row == 0 ? 0 : 10, 0, 4);
            label.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(label, 0, row);
            layout.SetColumnSpan(label, 2);
            return label;
        }

        private static TextBox MakePathTextBox()
        {
            var box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 0, 8, 8);
            return box;
        }

        private static Button MakeTableButton(string text)
        {
            var button = MakeButton(text);
            button.Dock = DockStyle.Fill;
            button.MinimumSize = new Size(100, 28);
            button.Margin = new Padding(0, 0, 0, 8);
            return button;
        }

        private void ShowLanguageDialog()
        {
            using (var dialog = new Form())
            {
                dialog.Text = T("Language");
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ClientSize = new Size(360, 130);
                dialog.Font = Font;

                var label = new Label();
                label.Text = T("Choose Language");
                label.SetBounds(18, 18, 320, 24);
                dialog.Controls.Add(label);

                var combo = new ComboBox();
                combo.DropDownStyle = ComboBoxStyle.DropDownList;
                combo.SetBounds(18, 46, 320, 26);
                combo.Items.Add(new LanguageChoice("en", "English"));
                combo.Items.Add(new LanguageChoice("es", "Español"));
                combo.SelectedIndex = string.Equals(currentLanguage, "es", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                dialog.Controls.Add(combo);

                var ok = new Button();
                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(170, 88, 80, 28);
                dialog.Controls.Add(ok);

                var cancel = new Button();
                cancel.Text = T("Cancel");
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(258, 88, 80, 28);
                dialog.Controls.Add(cancel);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    var choice = combo.SelectedItem as LanguageChoice;
                    if (choice != null)
                    {
                        currentLanguage = choice.Code;
                        ApplyLanguage();
                    }
                }
            }
        }

        private void ApplyLanguage()
        {
            Text = AppTitle();
            titleLabel.Text = AppTitle();
            subtitleLabel.Text = T("Subtitle");

            backupTab.Text = T("Backup");
            imageToolsTab.Text = T("Image Tools");

            refreshButton.Text = T("Refresh");
            driverButton.Text = T("Switch Driver");
            identifyButton.Text = T("Identify");
            partitionsButton.Text = T("Partitions");
            openBackupFolderButton.Text = T("Open Backup");
            backupBrowseButton.Text = T("Browse");
            sourceBrowseButton.Text = T("Browse");
            patchedBrowseButton.Text = T("Save As");
            flashBrowseButton.Text = T("Browse");
            expectedBrowseButton.Text = T("Browse");
            expectedClearButton.Text = T("Clear");
            dataBackupBrowseButton.Text = T("Browse");
            dataRestoreBrowseButton.Text = T("Browse");
            backupButton.Text = T("Backup Controller");
            patchButton.Text = T("Patch Image");
            flashSystemButton.Text = T("Flash System");
            restoreDataButton.Text = T("Restore Data");
            languageButton.Text = T("Language");
            aboutButton.Text = T("About");
            logsButton.Text = T("Logs");

            backupFolderLabel.Text = T("Backup Folder");
            backupPartitionsLabel.Text = T("Partitions");
            backupNoteLabel.Text = T("Backup Note");
            originalSystemLabel.Text = T("Original System");
            patchedOutputLabel.Text = T("Patched Output");
            systemFlashLabel.Text = T("System To Flash");
            expectedCurrentLabel.Text = T("Expected Current");
            expectedHelpLabel.Text = T("Expected Help");
            dataBackupLabel.Text = T("Recommended Data Backup");
            dataBackupHelpLabel.Text = T("Data Backup Help");
            dataRestoreLabel.Text = T("Data Restore");

            SetCheckText("bootloader", T("Bootloader"));
            SetCheckText("boot", T("Boot"));
            SetCheckText("recovery", T("Recovery"));
            SetCheckText("system", T("System"));
            SetCheckText("data", T("Data"));
            SetCheckText("gopro", T("GoPro"));
        }

        private void SetCheckText(string key, string text)
        {
            CheckBox check;
            if (backupChecks.TryGetValue(key, out check))
            {
                check.Text = text;
            }
        }

        private string T(string key)
        {
            if (!string.Equals(currentLanguage, "es", StringComparison.OrdinalIgnoreCase))
            {
                return EnglishText(key);
            }

            switch (key)
            {
                case "Subtitle": return "Herramientas WinUSB para respaldar, parchear, flashear y recuperar el controlador.";
                case "Backup": return "Respaldo";
                case "Image Tools": return "Herramientas de imagen";
                case "Refresh": return "Actualizar";
                case "Switch Driver": return "Cambiar driver";
                case "Identify": return "Identificar";
                case "Partitions": return "Particiones";
                case "Open Backup": return "Abrir respaldo";
                case "Browse": return "Buscar";
                case "Save As": return "Guardar como";
                case "Clear": return "Borrar";
                case "Backup Controller": return "Respaldar controlador";
                case "Patch Image": return "Parchear imagen";
                case "Flash System": return "Flashear System";
                case "Restore Data": return "Restaurar Data";
                case "Language": return "Idioma";
                case "About": return "Acerca de";
                case "Logs": return "Registros";
                case "Backup Folder": return "Carpeta de respaldo";
                case "Backup Note": return "Data está seleccionado por defecto porque es la copia de recuperación antes de flashear System.";
                case "Original System": return "system.img original";
                case "Patched Output": return "Imagen parcheada de salida";
                case "System To Flash": return "Imagen System para flashear";
                case "Expected Current": return "Imagen System actual esperada (opcional)";
                case "Expected Help": return "Déjelo vacío para flasheo normal. Elija un systemBU.img solo si quiere verificar antes de escribir que el controlador coincide exactamente con esa imagen.";
                case "Recommended Data Backup": return "Respaldo Data recomendado (dataBU.img)";
                case "Data Backup Help": return "Recomendado antes de flashear, pero no requerido para flasheos WinUSB solo de System. Sirve como copia de recuperación si alguna vez necesita restaurar /data.";
                case "Data Restore": return "Imagen Data para restaurar";
                case "Choose Language": return "Seleccione idioma";
                case "Cancel": return "Cancelar";
                default: return EnglishText(key);
            }
        }

        private static string EnglishText(string key)
        {
            switch (key)
            {
                case "Subtitle": return "WinUSB controller tools, image patching, flashing, and recovery backups.";
                case "Backup": return "Backup";
                case "Image Tools": return "Image Tools";
                case "Refresh": return "Refresh";
                case "Switch Driver": return "Switch Driver";
                case "Identify": return "Identify";
                case "Partitions": return "Partitions";
                case "Open Backup": return "Open Backup";
                case "Browse": return "Browse";
                case "Save As": return "Save As";
                case "Clear": return "Clear";
                case "Backup Controller": return "Backup Controller";
                case "Patch Image": return "Patch Image";
                case "Flash System": return "Flash System";
                case "Restore Data": return "Restore Data";
                case "Language": return "Language";
                case "About": return "About";
                case "Logs": return "Logs";
                case "Backup Folder": return "Backup folder";
                case "Backup Note": return "Data is selected by default because it is the recovery safety net before system flashing.";
                case "Original System": return "Original system.img";
                case "Patched Output": return "Patched output image";
                case "System To Flash": return "System image to flash";
                case "Expected Current": return "Optional expected-current system image";
                case "Expected Help": return "Leave blank for normal flashing. Only choose a systemBU.img if you want preflight verification that the controller currently matches that exact image before writing.";
                case "Recommended Data Backup": return "Recommended data backup (dataBU.img)";
                case "Data Backup Help": return "Recommended before flashing, but not required for WinUSB system-only flashes. It is a recovery safety net if /data ever needs to be restored.";
                case "Data Restore": return "Data image to restore";
                case "Bootloader": return "Bootloader";
                case "Boot": return "Boot";
                case "Recovery": return "Recovery";
                case "System": return "System";
                case "Data": return "Data";
                case "GoPro": return "GoPro";
                case "Choose Language": return "Choose language";
                case "Cancel": return "Cancel";
                default: return key;
            }
        }

        private sealed class LanguageChoice
        {
            public readonly string Code;
            private readonly string name;

            public LanguageChoice(string code, string name)
            {
                Code = code;
                this.name = name;
            }

            public override string ToString()
            {
                return name;
            }
        }

        private static void AddStatusRow(TableLayoutPanel grid, int row, string label, out Label valueLabel)
        {
            var key = new Label();
            key.Text = label;
            key.AutoSize = true;
            key.Anchor = AnchorStyles.Left;
            key.Font = new Font(key.Font, FontStyle.Bold);
            grid.Controls.Add(key, 0, row);

            valueLabel = new Label();
            valueLabel.Text = "-";
            valueLabel.AutoEllipsis = true;
            valueLabel.Dock = DockStyle.Fill;
            valueLabel.TextAlign = ContentAlignment.MiddleLeft;
            grid.Controls.Add(valueLabel, 1, row);
        }

        private static Button MakeButton(string text)
        {
            var button = new Button();
            button.Text = text;
            button.Margin = new Padding(3);
            return button;
        }

        private void RefreshController(bool userInitiated)
        {
            if (busy)
            {
                return;
            }

            SetBusy(true, "Checking controller and driver...", true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                DeviceInfo device = null;
                try
                {
                    device = DeviceInfo.FindKarmaController();
                }
                catch (Exception ex)
                {
                    AppendActivity("Driver query failed: " + ex.Message);
                }

                BeginInvoke(new MethodInvoker(delegate
                {
                    ApplyControllerState(device, userInitiated);
                    SetBusy(false, statusValue.Text, false);

                    if (device == null)
                    {
                        autoDriverPromptShown = false;
                    }

                    if (device != null && !device.IsWinUsb && !userInitiated && !autoDriverPromptShown)
                    {
                        autoDriverPromptShown = true;
                        PromptAndInstallDriver();
                    }
                    else if (device != null && device.IsWinUsb && !askedBackupThisSession)
                    {
                        askedBackupThisSession = true;
                        AskForInitialBackup();
                    }
                }));
            });
        }

        private void ApplyControllerState(DeviceInfo device, bool userInitiated)
        {
            currentDevice = device;
            if (device == null)
            {
                if (userInitiated)
                {
                    autoRefreshEnabled = true;
                }
                controllerValue.Text = "Not detected. Connect USB, then start controller in update mode.";
                driverValue.Text = "-";
                statusValue.Text = "Waiting for controller.";
            }
            else
            {
                autoRefreshEnabled = false;
                controllerValue.Text = device.Name + " (" + device.InstanceId + ")";
                driverValue.Text = device.DriverSummary;
                statusValue.Text = device.IsWinUsb ? "Controller is ready." : "Driver change recommended.";
            }

            UpdateButtonState();
        }

        private void PromptAndInstallDriver()
        {
            if (busy || currentDevice == null)
            {
                return;
            }

            string driver = currentDevice.DriverSummary;
            DialogResult result = MessageBox.Show(
                this,
                "The Karma Controller is connected, but Windows is not reporting the WinUSB driver for USB\\" + VidPid + ".\r\n\r\nCurrent driver:\r\n" + driver + "\r\n\r\nSwitch this device to WinUSB now? Windows will ask for administrator permission.",
                "Switch Controller Driver",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (result != DialogResult.OK)
            {
                AppendActivity("Driver change canceled.");
                return;
            }

            InstallWinUsbDriver();
        }

        private void InstallWinUsbDriver()
        {
            SetBusy(true, "Installing WinUSB driver...", true);
            string instanceId = currentDevice.InstanceId;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string logPath = Path.Combine(Path.GetTempPath(), "karma-winusb-driver-install.log");
                string infPath = EnsureDriverInf();
                string libwdiHelperPath = FindLibwdiHelperExe();
                int exitCode = -1;
                Exception error = null;
                DeviceInfo verifiedDevice = null;
                bool usedPlainBundledFallback = false;
                bool usedLibwdiHelper = false;

                try
                {
                    if (File.Exists(logPath))
                    {
                        File.Delete(logPath);
                    }

                    DriverInstallPlan plan = DriverInstallPlan.Create(instanceId, infPath, libwdiHelperPath);
                    usedPlainBundledFallback = plan.IsPlainBundledFallback;
                    usedLibwdiHelper = plan.Mode == DriverInstallMode.LibwdiHelper;
                    AppendActivity("Selected driver package: " + plan.Description);

                    string args;
                    if (plan.Mode == DriverInstallMode.LibwdiHelper)
                    {
                        string workDir = Path.Combine(Path.GetTempPath(), "karma-winusb-driver");
                        args = "--force-libwdi-driver " + QuoteArg(plan.HelperPath) + " " + QuoteArg(workDir) + " " + QuoteArg(logPath);
                    }
                    else
                    {
                        args = "--force-driver " + QuoteArg(plan.InfPath) + " " + QuoteArg("USB\\" + VidPid) + " " + QuoteArg(logPath);
                    }
                    var psi = new ProcessStartInfo(Application.ExecutablePath, args);
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    psi.WindowStyle = ProcessWindowStyle.Hidden;

                    AppendActivity("Requesting administrator approval for driver switch.");
                    using (Process process = Process.Start(psi))
                    {
                        process.WaitForExit();
                        exitCode = process.ExitCode;
                    }
                }
                catch (Win32Exception ex)
                {
                    error = ex;
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (File.Exists(logPath))
                {
                    AppendActivity("Driver log: " + logPath);
                }

                if (error != null)
                {
                    AppendActivity("Driver switch failed: " + error.Message);
                }
                else
                {
                    AppendActivity("Driver switch exited with code " + exitCode.ToString() + ".");
                }

                Thread.Sleep(1200);
                try
                {
                    verifiedDevice = DeviceInfo.FindKarmaController();
                }
                catch (Exception ex)
                {
                    AppendActivity("Driver verification failed: " + ex.Message);
                }

                BeginInvoke(new MethodInvoker(delegate
                {
                    ApplyControllerState(verifiedDevice, true);
                    SetBusy(false, statusValue.Text, false);

                    if (verifiedDevice != null && verifiedDevice.IsWinUsb)
                    {
                        MessageBox.Show(this, "The controller is now using WinUSB.", "Driver Switch Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        if (!askedBackupThisSession)
                        {
                            askedBackupThisSession = true;
                            AskForInitialBackup();
                        }
                    }
                    else
                    {
                        string current = verifiedDevice == null ? "Controller not detected after the driver switch attempt." : verifiedDevice.DriverSummary;
                        string message =
                            "Windows still is not reporting WinUSB for this controller.\r\n\r\nCurrent driver:\r\n" + current + "\r\n\r\nThe driver log may have more detail:\r\n" + logPath;
                        if (usedLibwdiHelper)
                        {
                            message +=
                                "\r\n\r\nKarma Kontroller tried to generate and install a WinUSB driver package with its bundled libwdi helper. Check the driver log above and Windows SetupAPI device log for the exact Windows driver-install reason:\r\nC:\\Windows\\INF\\setupapi.dev.log";
                        }
                        else if (usedPlainBundledFallback)
                        {
                            message +=
                                "\r\n\r\nThis fallback uses the plain bundled INF because the libwdi helper was not found. A fresh Windows install may reject that plain INF because it is not a catalog-backed driver package.";
                        }
                        MessageBox.Show(
                            this,
                            message,
                            "Driver Switch Not Active",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                }));
            });
        }

        private string EnsureDriverInf()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string infPath = Path.Combine(appDir, "drivers\\KarmaWinUSB.inf");
            if (!File.Exists(infPath))
            {
                throw new FileNotFoundException("Bundled driver INF was not found.", infPath);
            }
            return infPath;
        }

        private static string FindLibwdiHelperExe()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string helperPath = Path.Combine(appDir, "drivers\\karma-winusb-driver.exe");
            string dllPath = Path.Combine(appDir, "drivers\\libwdi.dll");
            if (File.Exists(helperPath) && File.Exists(dllPath))
            {
                return helperPath;
            }
            return null;
        }

        private void AskForInitialBackup()
        {
            DialogResult result = MessageBox.Show(
                this,
                "The controller is ready over WinUSB.\r\n\r\nWould you like to make a full controller backup now?",
                "Make Full Backup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                StartSelectedBackup();
            }
        }

        private void StartSelectedBackup()
        {
            if (busy)
            {
                return;
            }

            string folder = backupFolderBox.Text.Trim();
            if (folder.Length == 0)
            {
                MessageBox.Show(this, "Choose a backup folder first.", "Backup Folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<string> selected = SelectedBackupPartitions();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Choose at least one partition to back up.", "Backup Partitions", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!selected.Contains("data"))
            {
                DialogResult answer = MessageBox.Show(
                    this,
                    "The Data partition is the recovery safety net before flashing system.\r\n\r\nContinue without backing up Data?",
                    "Backup Without Data",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    return;
                }
            }

            Directory.CreateDirectory(folder);
            string args = "backup " + QuoteArg(folder);
            foreach (string part in selected)
            {
                args += " --part " + QuoteArg(part);
            }

            RunBackend(args, "Backup", false, delegate(int exitCode)
            {
                if (exitCode == 0)
                {
                    SetDataRestoreFromBackupFolder();
                }
            });
        }

        private List<string> SelectedBackupPartitions()
        {
            var selected = new List<string>();
            string[] order = { "bootloader", "boot", "recovery", "system", "data", "gopro" };
            foreach (string part in order)
            {
                CheckBox check;
                if (backupChecks.TryGetValue(part, out check) && check.Checked)
                {
                    selected.Add(part);
                }
            }
            return selected;
        }

        private void BrowseBackupFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose where controller backups should be saved.";
                dialog.ShowNewFolderButton = true;
                string current = backupFolderBox.Text.Trim();
                if (current.Length > 0)
                {
                    string existing = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
                    if (!string.IsNullOrEmpty(existing) && Directory.Exists(existing))
                    {
                        dialog.SelectedPath = existing;
                    }
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    backupFolderBox.Text = dialog.SelectedPath;
                    SetDataRestoreFromBackupFolder();
                }
            }
        }

        private void OpenBackupFolder()
        {
            string folder = backupFolderBox.Text.Trim();
            if (folder.Length == 0)
            {
                return;
            }

            string openPath = Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder);
            if (string.IsNullOrEmpty(openPath) || !Directory.Exists(openPath))
            {
                MessageBox.Show(this, "Backup folder does not exist yet.", "Open Folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Process.Start(new ProcessStartInfo(openPath) { UseShellExecute = true });
        }

        private void OpenLogsFolder()
        {
            string dir = LogsDirectory();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }

        private void ShowAboutDialog()
        {
            using (var dialog = new Form())
            {
                dialog.Text = "About " + AppName;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimumSize = new Size(660, 500);
                dialog.Size = new Size(720, 560);
                dialog.Font = Font;
                TrySetDialogIcon(dialog);

                var layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(16);
                layout.ColumnCount = 1;
                layout.RowCount = 5;
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                dialog.Controls.Add(layout);

                var heading = new Label();
                heading.Text = "Karma Kontroller - the backup, patching, flashing utility to restore abandoned features.";
                heading.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
                heading.AutoSize = true;
                heading.MaximumSize = new Size(660, 0);
                heading.Margin = new Padding(0, 0, 0, 12);
                layout.Controls.Add(heading, 0, 0);

                var body = new TextBox();
                body.Multiline = true;
                body.ReadOnly = true;
                body.ScrollBars = ScrollBars.Vertical;
                body.Dock = DockStyle.Fill;
                body.WordWrap = true;
                body.Text = AboutText();
                layout.Controls.Add(body, 0, 1);

                var issue = new LinkLabel();
                issue.Text = "Report issues on GitHub: " + IssueUrl;
                issue.AutoSize = true;
                issue.Margin = new Padding(0, 12, 0, 8);
                issue.LinkClicked += delegate { OpenUrl(IssueUrl); };
                layout.Controls.Add(issue, 0, 2);

                var buttons = new FlowLayoutPanel();
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.Dock = DockStyle.Fill;
                buttons.AutoSize = true;

                var close = new Button();
                close.Text = "Close";
                close.DialogResult = DialogResult.OK;
                close.MinimumSize = new Size(86, 30);
                buttons.Controls.Add(close);

                var license = new Button();
                license.Text = "License";
                license.MinimumSize = new Size(86, 30);
                license.Click += delegate { ShowLicenseDialog(dialog); };
                buttons.Controls.Add(license);

                layout.Controls.Add(buttons, 0, 4);
                dialog.AcceptButton = close;
                dialog.ShowDialog(this);
            }
        }

        private static string AboutText()
        {
            return
                AppName + " " + AppVersion + Environment.NewLine + Environment.NewLine +
                "Implemented and restored features:" + Environment.NewLine +
                "- WinUSB controller detection and driver switching for USB\\VID_1B8E&PID_C003." + Environment.NewLine +
                "- Full and selected partition backups without the unsigned WorldCup driver path." + Environment.NewLine +
                "- Stock system.img patching for the public Mapbox compatibility proxy, online config, trusted certificate, file browser, and startup hooks." + Environment.NewLine +
                "- Automatic system image padding before raw WinUSB flashing." + Environment.NewLine +
                "- System partition flashing with post-write verification." + Environment.NewLine +
                "- Optional expected-current preflight verification when a known matching systemBU.img is supplied." + Environment.NewLine +
                "- Optional dataBU.img recovery backup selection and separate Data restore flow." + Environment.NewLine + Environment.NewLine +
                "This project is independent and unofficial. It is not affiliated with, endorsed by, or supported by GoPro, Mapbox, Amlogic, Microsoft, or any drone manufacturer. Use it only on hardware you own or are authorized to service.";
        }

        private void ShowLicenseDialog(IWin32Window owner)
        {
            using (var dialog = new Form())
            {
                dialog.Text = "Licenses and notices";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimumSize = new Size(720, 520);
                dialog.Size = new Size(820, 620);
                dialog.Font = Font;
                TrySetDialogIcon(dialog);

                var text = new TextBox();
                text.Dock = DockStyle.Fill;
                text.Multiline = true;
                text.ReadOnly = true;
                text.ScrollBars = ScrollBars.Both;
                text.WordWrap = false;
                text.Font = new Font("Consolas", 9F);
                text.Text = BuildLicenseText();
                dialog.Controls.Add(text);

                var close = new Button();
                close.Text = "Close";
                close.Dock = DockStyle.Bottom;
                close.Height = 34;
                close.DialogResult = DialogResult.OK;
                dialog.Controls.Add(close);
                dialog.AcceptButton = close;
                dialog.ShowDialog(owner);
            }
        }

        private static string BuildLicenseText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Karma Kontroller license summary");
            sb.AppendLine();
            sb.AppendLine("Project source code: Apache License 2.0.");
            sb.AppendLine("Third-party notices: see THIRD_PARTY_NOTICES.md below.");
            sb.AppendLine();
            sb.AppendLine("Bundled Go and .NET-built project binaries use project source in this workspace. The Go module currently declares no third-party Go module dependencies beyond the Go standard library.");
            sb.AppendLine();
            sb.AppendLine("Important redistribution note: old Amlogic update-tool bundles and libusb-win32/WorldCup driver packages are separately licensed and are not covered by the Karma Kontroller Apache 2.0 license.");
            sb.AppendLine();
            sb.AppendLine("===== LICENSE =====");
            sb.AppendLine(ReadBundledText("LICENSE", "Apache License 2.0 text was not found next to the application."));
            sb.AppendLine();
            sb.AppendLine("===== THIRD_PARTY_NOTICES.md =====");
            sb.AppendLine(ReadBundledText("THIRD_PARTY_NOTICES.md", "Third-party notices were not found next to the application."));
            return sb.ToString();
        }

        private static string ReadBundledText(string fileName, string fallback)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
            return fallback;
        }

        private static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private void BrowseSourceImage()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select original system.img";
                dialog.Filter = "Android system image (*.img)|*.img|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    sourceImageBox.Text = dialog.FileName;
                    patchedImageBox.Text = DefaultPatchedPath(dialog.FileName);
                }
            }
        }

        private void BrowsePatchedImage()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Save patched system image";
                dialog.Filter = "Android system image (*.img)|*.img|All files (*.*)|*.*";
                dialog.OverwritePrompt = true;
                if (patchedImageBox.Text.Trim().Length > 0)
                {
                    dialog.FileName = patchedImageBox.Text.Trim();
                }
                else if (sourceImageBox.Text.Trim().Length > 0)
                {
                    dialog.FileName = DefaultPatchedPath(sourceImageBox.Text.Trim());
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    patchedImageBox.Text = dialog.FileName;
                }
            }
        }

        private void BrowseFlashImage()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select system image to flash";
                dialog.Filter = "Android system image (*.img)|*.img|All files (*.*)|*.*";
                if (flashImageBox.Text.Trim().Length > 0)
                {
                    dialog.FileName = flashImageBox.Text.Trim();
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    flashImageBox.Text = dialog.FileName;
                }
            }
        }

        private void BrowseExpectedCurrentImage()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select expected current system image";
                dialog.Filter = "Android system image (*.img)|*.img|All files (*.*)|*.*";
                if (expectedCurrentImageBox.Text.Trim().Length > 0)
                {
                    dialog.FileName = expectedCurrentImageBox.Text.Trim();
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    expectedCurrentImageBox.Text = dialog.FileName;
                }
            }
        }

        private void BrowseDataBackupImage()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select recommended dataBU.img backup";
                dialog.Filter = "Karma data backup (dataBU.img)|dataBU.img|Karma data image (*.img)|*.img|All files (*.*)|*.*";
                if (dataBackupBox.Text.Trim().Length > 0)
                {
                    dialog.FileName = dataBackupBox.Text.Trim();
                }
                else
                {
                    string candidate = GetDataBackupPath();
                    string dir = string.IsNullOrEmpty(candidate) ? "" : Path.GetDirectoryName(candidate);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        dialog.InitialDirectory = dir;
                    }
                    if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                    {
                        dialog.FileName = candidate;
                    }
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    dataBackupBox.Text = dialog.FileName;
                    if (string.IsNullOrWhiteSpace(dataRestoreBox.Text) || !File.Exists(dataRestoreBox.Text.Trim()))
                    {
                        dataRestoreBox.Text = dialog.FileName;
                    }
                }
            }
        }

        private void BrowseDataRestoreImage()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select data image to restore";
                dialog.Filter = "Karma data image (*.img)|*.img|All files (*.*)|*.*";
                if (dataRestoreBox.Text.Trim().Length > 0)
                {
                    dialog.FileName = dataRestoreBox.Text.Trim();
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    dataRestoreBox.Text = dialog.FileName;
                }
            }
        }

        private void StartPatchImage()
        {
            if (busy)
            {
                return;
            }

            string source = sourceImageBox.Text.Trim();
            string dest = patchedImageBox.Text.Trim();
            if (!File.Exists(source))
            {
                MessageBox.Show(this, "Select an original system.img first.", "Patch Image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (dest.Length == 0)
            {
                dest = DefaultPatchedPath(source);
                patchedImageBox.Text = dest;
            }

            string patchTool = FindPatchToolExe();
            if (patchTool == null)
            {
                MessageBox.Show(this, "KarmaKontrollerPatchTool.exe was not found next to this application.", "Patch Tool Missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            RunExternalTool(patchTool, "--patch-system " + QuoteArg(source) + " " + QuoteArg(dest), "Patch", false, delegate(int exitCode)
            {
                if (exitCode == 0)
                {
                    flashImageBox.Text = dest;
                }
            });
        }

        private void StartFlashSystem()
        {
            if (busy)
            {
                return;
            }

            string image = flashImageBox.Text.Trim();
            if (!File.Exists(image))
            {
                MessageBox.Show(this, "Select a system image to flash first.", "Flash System", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string dataBackupProblem;
            if (!TryValidateDataImage(GetDataBackupPath(), out dataBackupProblem))
            {
                DialogResult dataAnswer = MessageBox.Show(
                    this,
                    "No valid dataBU.img recovery backup is selected.\r\n\r\n" + dataBackupProblem + "\r\n\r\nThis is recommended, but it is not required for a WinUSB system-only flash. Continue without a data backup?",
                    "Continue Without Data Backup?",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (dataAnswer != DialogResult.Yes)
                {
                    return;
                }
            }

            string flashImage = PrepareSystemImageForFlash(image, "system image to flash");
            if (flashImage == null)
            {
                return;
            }
            flashImageBox.Text = flashImage;

            string expected = expectedCurrentImageBox.Text.Trim();
            string expectedForFlash = "";
            if (expected.Length > 0)
            {
                if (!File.Exists(expected))
                {
                    MessageBox.Show(
                        this,
                        "The optional expected-current system image does not exist.\r\n\r\nClear the field for normal flashing, or browse to a valid systemBU.img backup.\r\n\r\nSelected path:\r\n" + expected,
                        "Expected Current Image",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                expectedForFlash = PrepareSystemImageForFlash(expected, "expected current image");
                if (expectedForFlash == null)
                {
                    return;
                }
                expectedCurrentImageBox.Text = expectedForFlash;
            }

            string preflightLine = File.Exists(expectedForFlash)
                ? "\r\n\r\nPreflight expected-current image:\r\n" + expectedForFlash
                : "\r\n\r\nNo expected-current image selected. The app will skip the before-write match check and verify after writing.";

            DialogResult answer = MessageBox.Show(
                this,
                "Flashing rewrites the controller system partition.\r\n\r\nIf USB or power is interrupted, the controller may become unrecoverable.\r\n\r\nImage to flash:\r\n" + flashImage + preflightLine + "\r\n\r\nContinue?",
                "Flash System",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            string args = "flash-partition system " + QuoteArg(flashImage) + " --i-understand-this-can-brick --verify-after-write";
            if (File.Exists(expectedForFlash))
            {
                args += " --expect-current " + QuoteArg(expectedForFlash);
            }
            RunBackend(args, "Flash System", false, null);
        }

        private string PrepareSystemImageForFlash(string path, string label)
        {
            FileInfo info = new FileInfo(path);
            if (info.Length == SystemPartitionBytes)
            {
                return info.FullName;
            }

            if (info.Length > SystemPartitionBytes)
            {
                MessageBox.Show(
                    this,
                    "The " + label + " is larger than the Karma system partition and cannot be flashed.\r\n\r\nSelected path:\r\n" + info.FullName + "\r\n\r\nActual size: " + info.Length.ToString("N0") + " bytes\r\nExpected size: " + SystemPartitionBytes.ToString("N0") + " bytes",
                    "System Image Size",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return null;
            }

            string paddedPath = PaddedImagePath(info.FullName);
            try
            {
                AppendActivity("Creating padded " + label + ": " + paddedPath);
                CopyAndPadFile(info.FullName, paddedPath, SystemPartitionBytes);
                AppendActivity("Padded " + label + " ready.");
                return paddedPath;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "The " + label + " is smaller than the raw system partition, and the app could not create a padded flash copy.\r\n\r\nSelected path:\r\n" + info.FullName + "\r\n\r\nError:\r\n" + ex.Message,
                    "System Image Padding Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return null;
            }
        }

        private void StartRestoreData()
        {
            if (busy)
            {
                return;
            }

            string image = dataRestoreBox.Text.Trim();
            if (!File.Exists(image))
            {
                MessageBox.Show(this, "Select a data image to restore first.", "Restore Data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string dataProblem;
            if (!TryValidateDataImage(image, out dataProblem))
            {
                MessageBox.Show(this, dataProblem, "Restore Data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult answer = MessageBox.Show(
                this,
                "Restoring Data rewrites the controller /data partition and replaces controller settings and pairing data.\r\n\r\nContinue restoring this image?\r\n\r\n" + image,
                "Restore Data",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            string args = "flash-partition data " + QuoteArg(image) + " --i-understand-this-can-brick --verify-after-write";
            RunBackend(args, "Restore Data", false, null);
        }

        private bool TryValidateDataImage(string path, out string problem)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                problem = "No dataBU.img path is selected.";
                return false;
            }
            if (!File.Exists(path))
            {
                problem = "The selected dataBU.img does not exist:\r\n" + path;
                return false;
            }

            long length = new FileInfo(path).Length;
            if (length != DataPartitionBytes)
            {
                problem = "The selected data image has the wrong size.\r\n\r\nSelected path:\r\n" + path + "\r\n\r\nActual size: " + length.ToString("N0") + " bytes\r\nExpected size: " + DataPartitionBytes.ToString("N0") + " bytes";
                return false;
            }

            problem = "";
            return true;
        }

        private string GetDataBackupPath()
        {
            if (dataBackupBox != null && dataBackupBox.Text.Trim().Length > 0)
            {
                return dataBackupBox.Text.Trim();
            }
            if (backupFolderBox == null)
            {
                return "";
            }
            return Path.Combine(backupFolderBox.Text.Trim(), "dataBU.img");
        }

        private void SetDataRestoreFromBackupFolder()
        {
            if (backupFolderBox == null)
            {
                return;
            }
            string path = Path.Combine(backupFolderBox.Text.Trim(), "dataBU.img");
            if (dataBackupBox != null)
            {
                string current = dataBackupBox.Text.Trim();
                if ((current.Length == 0 || !File.Exists(current)) && File.Exists(path))
                {
                    dataBackupBox.Text = path;
                }
            }
            if (dataRestoreBox == null)
            {
                return;
            }
            if (File.Exists(path))
            {
                dataRestoreBox.Text = path;
            }
        }

        private void RunBackend(string arguments, string label, bool showRawOutput, OperationComplete complete)
        {
            string backend = FindBackendExe();
            if (backend == null)
            {
                MessageBox.Show(this, "KarmaWinUSB.exe was not found next to this application.", "Backend Missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            RunExternalTool(backend, arguments, label, showRawOutput, complete);
        }

        private void RunExternalTool(string exe, string arguments, string label, bool showRawOutput, OperationComplete complete)
        {
            if (busy)
            {
                return;
            }

            string logPath = CreateOperationLog(label, exe, arguments);
            ResetProgress(label + " starting...");
            SetBusy(true, label + " running...", false);
            AppendActivity(label + " started. Detailed log: " + logPath);

            ThreadPool.QueueUserWorkItem(delegate
            {
                int exitCode = RunProcess(exe, arguments, Path.GetDirectoryName(exe), logPath, showRawOutput);
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (exitCode == 0)
                    {
                        UpdateProgress(100, label + " complete");
                        AppendActivity(label + " complete.");
                    }
                    else
                    {
                        AppendActivity(label + " failed with code " + exitCode.ToString() + ".");
                    }

                    SetBusy(false, exitCode == 0 ? label + " complete." : label + " failed.", false);
                    RefreshController(true);
                    if (complete != null)
                    {
                        complete(exitCode);
                    }
                }));
            });
        }

        private int RunProcess(string exe, string arguments, string workingDirectory, string logPath, bool showRawOutput)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, arguments);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = workingDirectory;

                using (Process process = new Process())
                {
                    bool toolReportedError = false;
                    process.StartInfo = psi;
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                        {
                            if (e.Data.StartsWith("KK_RESULT|1|", StringComparison.OrdinalIgnoreCase))
                            {
                                toolReportedError = true;
                            }
                            HandleProcessLine(e.Data, logPath, showRawOutput, false);
                        }
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                        {
                            if (e.Data.StartsWith("KK_RESULT|1|", StringComparison.OrdinalIgnoreCase))
                            {
                                toolReportedError = true;
                            }
                            HandleProcessLine(e.Data, logPath, true, true);
                        }
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    if (process.ExitCode == 0 && toolReportedError)
                    {
                        return 1;
                    }
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                AppendActivity("Command failed: " + ex.Message);
                WriteOperationLog(logPath, "Command failed: " + ex);
                return -1;
            }
        }

        private void HandleProcessLine(string line, string logPath, bool showRawOutput, bool isError)
        {
            WriteOperationLog(logPath, line);
            int percent;
            string status;
            if (TryParseProgress(line, out percent, out status))
            {
                UpdateProgress(percent, status);
                return;
            }

            if (line.StartsWith("KK_RESULT|0|", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (line.StartsWith("KK_RESULT|1|", StringComparison.OrdinalIgnoreCase))
            {
                AppendActivity("Tool reported an error. See detailed log.");
                return;
            }

            if (showRawOutput || isError || line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                AppendActivity(line);
            }
        }

        private static bool TryParseProgress(string line, out int percent, out string status)
        {
            percent = 0;
            status = "";
            if (!(line.StartsWith("KW_PROGRESS|", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("KK_PROGRESS|", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            string[] parts = line.Split(new[] { '|' }, 3);
            if (parts.Length < 3)
            {
                return false;
            }
            if (!int.TryParse(parts[1], out percent))
            {
                return false;
            }
            status = parts[2];
            return true;
        }

        private void ResetProgress(string status)
        {
            lastActivityProgress = -1;
            lastActivityStatus = "";
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = 0;
            progressText.Text = "0% - " + status;
        }

        private void UpdateProgress(int percent, string status)
        {
            if (IsDisposed)
            {
                return;
            }
            if (InvokeRequired)
            {
                BeginInvoke(new MethodInvoker(delegate { UpdateProgress(percent, status); }));
                return;
            }

            percent = Math.Max(0, Math.Min(100, percent));
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = percent;
            progressText.Text = percent.ToString() + "% - " + status;
            statusValue.Text = status;

            bool statusChanged = !string.Equals(status, lastActivityStatus, StringComparison.Ordinal);
            bool percentStep = lastActivityProgress < 0 || percent == 100 || percent / 10 != lastActivityProgress / 10;
            if (statusChanged || percentStep)
            {
                lastActivityStatus = status;
                lastActivityProgress = percent;
                AppendActivity(percent.ToString() + "% - " + status);
            }
        }

        private string CreateOperationLog(string label, string exe, string arguments)
        {
            string dir = LogsDirectory();
            Directory.CreateDirectory(dir);
            string safe = SafeFilePart(label);
            string path = Path.Combine(dir, safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            WriteOperationLog(path, Path.GetFileName(exe) + " " + arguments);
            return path;
        }

        private static string SafeFilePart(string text)
        {
            var sb = new StringBuilder();
            foreach (char ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                }
                else if (ch == '-' || ch == '_')
                {
                    sb.Append(ch);
                }
            }
            return sb.Length == 0 ? "operation" : sb.ToString();
        }

        private static void WriteOperationLog(string logPath, string line)
        {
            try
            {
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + Environment.NewLine);
            }
            catch
            {
            }
        }

        private void SetBusy(bool value, string status, bool indeterminate)
        {
            busy = value;
            statusValue.Text = status;
            if (value && indeterminate)
            {
                progressBar.Style = ProgressBarStyle.Marquee;
                progressBar.MarqueeAnimationSpeed = 22;
                progressText.Text = status;
            }
            else
            {
                progressBar.MarqueeAnimationSpeed = 0;
                progressBar.Style = ProgressBarStyle.Blocks;
            }
            UpdateButtonState();
        }

        private void UpdateButtonState()
        {
            bool backendReady = FindBackendExe() != null;
            bool patchReady = FindPatchToolExe() != null;
            bool deviceReady = currentDevice != null;
            bool winusbReady = deviceReady && currentDevice.IsWinUsb;

            refreshButton.Enabled = !busy;
            driverButton.Enabled = !busy && deviceReady && !winusbReady;
            identifyButton.Enabled = !busy && backendReady && winusbReady;
            partitionsButton.Enabled = !busy && backendReady;
            backupButton.Enabled = !busy && backendReady && winusbReady;
            backupBrowseButton.Enabled = !busy;
            openBackupFolderButton.Enabled = !busy;
            sourceBrowseButton.Enabled = !busy;
            patchedBrowseButton.Enabled = !busy;
            patchButton.Enabled = !busy && patchReady;
            flashBrowseButton.Enabled = !busy;
            expectedBrowseButton.Enabled = !busy;
            expectedClearButton.Enabled = !busy;
            dataBackupBrowseButton.Enabled = !busy;
            flashSystemButton.Enabled = !busy && backendReady && winusbReady;
            dataRestoreBrowseButton.Enabled = !busy;
            restoreDataButton.Enabled = !busy && backendReady && winusbReady;
            languageButton.Enabled = !busy;
            aboutButton.Enabled = !busy;
            logsButton.Enabled = true;

            foreach (CheckBox check in backupChecks.Values)
            {
                check.Enabled = !busy;
            }
        }

        private void AppendActivity(string text)
        {
            if (IsDisposed)
            {
                return;
            }
            if (InvokeRequired)
            {
                BeginInvoke(new MethodInvoker(delegate { AppendActivity(text); }));
                return;
            }

            activityBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
        }

        private static string QuoteArg(string text)
        {
            return "\"" + text.Replace("\"", "\\\"") + "\"";
        }

        private static string FindBackendExe()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string local = Path.Combine(appDir, "KarmaWinUSB.exe");
            if (File.Exists(local))
            {
                return local;
            }
            string renamed = Path.Combine(appDir, "winusbupdate.exe");
            if (File.Exists(renamed))
            {
                return renamed;
            }
            return null;
        }

        private static string FindPatchToolExe()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string local = Path.Combine(appDir, "KarmaKontrollerPatchTool.exe");
            if (File.Exists(local))
            {
                return local;
            }
            return null;
        }

        private static string AppTitle()
        {
            return AppName + " " + AppVersion;
        }

        private static string LogsDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KarmaKontroller Logs");
        }

        private static string DefaultBackupFolder()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(docs, "KarmaKontroller Backups\\controller-full-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        private static string DefaultPatchedPath(string source)
        {
            string dir = Path.GetDirectoryName(source);
            string name = Path.GetFileNameWithoutExtension(source);
            string ext = Path.GetExtension(source);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".img";
            }
            if (string.IsNullOrEmpty(name))
            {
                name = "system";
            }
            return Path.Combine(dir, name + ".karma-patched" + ext);
        }

        private static string PaddedImagePath(string source)
        {
            string dir = Path.GetDirectoryName(source);
            string name = Path.GetFileNameWithoutExtension(source);
            string ext = Path.GetExtension(source);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".img";
            }
            if (name.EndsWith(".padded", StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
            return Path.Combine(dir, name + ".padded" + ext);
        }

        private static void CopyAndPadFile(string source, string dest, long targetBytes)
        {
            string sourceFull = Path.GetFullPath(source);
            string destFull = Path.GetFullPath(dest);
            if (string.Equals(sourceFull, destFull, StringComparison.OrdinalIgnoreCase))
            {
                using (FileStream existing = new FileStream(destFull, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    existing.SetLength(targetBytes);
                }
                return;
            }

            string dir = Path.GetDirectoryName(destFull);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = destFull + ".tmp";
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }

            byte[] buffer = new byte[1024 * 1024];
            using (FileStream input = new FileStream(sourceFull, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                }
                output.SetLength(targetBytes);
            }

            if (File.Exists(destFull))
            {
                File.Delete(destFull);
            }
            File.Move(tmp, destFull);
        }

        private void TrySetIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "karma_k.ico");
                if (File.Exists(iconPath))
                {
                    Icon = new Icon(iconPath);
                }
            }
            catch
            {
            }
        }

        private static void TrySetDialogIcon(Form dialog)
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "karma_k.ico");
                if (File.Exists(iconPath))
                {
                    dialog.Icon = new Icon(iconPath);
                }
            }
            catch
            {
            }
        }

        private delegate void OperationComplete(int exitCode);
    }

    internal enum DriverInstallMode
    {
        InstalledInf,
        LibwdiHelper,
        PlainBundledInf
    }

    internal sealed class DriverInstallPlan
    {
        public DriverInstallMode Mode;
        public string InfPath;
        public string HelperPath;
        public string Description;
        public bool IsPlainBundledFallback;

        public static DriverInstallPlan Create(string instanceId, string bundledInfPath, string libwdiHelperPath)
        {
            DriverCandidate candidate = DriverCandidate.FindInstalledWinUsbCandidate(instanceId);
            if (candidate != null)
            {
                string windowsInfPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF\\" + candidate.DriverName);
                if (File.Exists(windowsInfPath))
                {
                    return new DriverInstallPlan
                    {
                        Mode = DriverInstallMode.InstalledInf,
                        InfPath = windowsInfPath,
                        Description = candidate.DriverName + " (" + candidate.ProviderName + ", " + candidate.ClassName + ")",
                        IsPlainBundledFallback = false
                    };
                }
            }

            if (!string.IsNullOrEmpty(libwdiHelperPath) && File.Exists(libwdiHelperPath))
            {
                return new DriverInstallPlan
                {
                    Mode = DriverInstallMode.LibwdiHelper,
                    HelperPath = libwdiHelperPath,
                    Description = Path.GetFileName(libwdiHelperPath) + " (bundled libwdi WinUSB installer)",
                    IsPlainBundledFallback = false
                };
            }

            return new DriverInstallPlan
            {
                Mode = DriverInstallMode.PlainBundledInf,
                InfPath = bundledInfPath,
                Description = Path.GetFileName(bundledInfPath) + " (bundled fallback)",
                IsPlainBundledFallback = true
            };
        }
    }

    internal sealed class DriverCandidate
    {
        public string DriverName;
        public string ProviderName;
        public string ClassName;

        public static DriverCandidate FindInstalledWinUsbCandidate(string instanceId)
        {
            foreach (DriverCandidate candidate in Enumerate(instanceId))
            {
                if (candidate.IsInstalledWinUsbCandidate)
                {
                    return candidate;
                }
            }
            return null;
        }

        private bool IsInstalledWinUsbCandidate
        {
            get
            {
                return !string.IsNullOrEmpty(DriverName)
                    && string.Equals(ClassName, "USBDevice", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(ProviderName)
                    && ProviderName.IndexOf("libwdi", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static DriverCandidate[] Enumerate(string instanceId)
        {
            string pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32\\pnputil.exe");
            var psi = new ProcessStartInfo(pnputil, "/enum-devices /instanceid " + QuoteForPnPUtil(instanceId) + " /drivers");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;

            using (Process process = Process.Start(psi))
            {
                string output = process.StandardOutput.ReadToEnd();
                output += process.StandardError.ReadToEnd();
                process.WaitForExit();
                return Parse(output);
            }
        }

        private static DriverCandidate[] Parse(string output)
        {
            var list = new List<DriverCandidate>();
            DriverCandidate current = null;
            using (var reader = new StringReader(output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    int separator = trimmed.IndexOf(':');
                    if (separator < 0)
                    {
                        continue;
                    }

                    string key = trimmed.Substring(0, separator).Trim();
                    string value = trimmed.Substring(separator + 1).Trim();
                    if (string.Equals(key, "Driver Name", StringComparison.OrdinalIgnoreCase))
                    {
                        if (current != null)
                        {
                            list.Add(current);
                        }
                        current = new DriverCandidate();
                        current.DriverName = value;
                    }
                    else if (current != null && string.Equals(key, "Provider Name", StringComparison.OrdinalIgnoreCase))
                    {
                        current.ProviderName = value;
                    }
                    else if (current != null && string.Equals(key, "Class Name", StringComparison.OrdinalIgnoreCase))
                    {
                        current.ClassName = value;
                    }
                }
            }

            if (current != null)
            {
                list.Add(current);
            }
            return list.ToArray();
        }

        private static string QuoteForPnPUtil(string text)
        {
            return "\"" + text.Replace("\"", "\\\"") + "\"";
        }
    }

    internal static class DriverInstallerCommand
    {
        private const uint InstallFlagForce = 0x00000001;

        [DllImport("newdev.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool UpdateDriverForPlugAndPlayDevices(
            IntPtr hwndParent,
            string hardwareId,
            string fullInfPath,
            uint installFlags,
            out bool rebootRequired);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint GetShortPathName(
            string longPath,
            StringBuilder shortPath,
            uint bufferLength);

        public static int Run(string[] args)
        {
            if (args.Length < 4)
            {
                return 2;
            }

            string infPath = args[1];
            string hardwareId = args[2];
            string logPath = args[3];

            try
            {
                AppendLog(logPath, "Driver helper started.");
                AppendLog(logPath, "INF: " + infPath);
                AppendLog(logPath, "Hardware ID: " + hardwareId);

                bool rebootRequired;
                bool ok = UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, infPath, InstallFlagForce, out rebootRequired);
                int lastError = Marshal.GetLastWin32Error();

                AppendLog(logPath, "UpdateDriverForPlugAndPlayDevices result: " + ok.ToString());
                AppendLog(logPath, "Last Win32 error: " + lastError.ToString());
                AppendLog(logPath, "Win32 message: " + new Win32Exception(lastError).Message);
                AppendLog(logPath, "Reboot required: " + rebootRequired.ToString());
                AppendLog(logPath, @"SetupAPI device log: C:\Windows\INF\setupapi.dev.log");
                if (!ok)
                {
                    AppendLog(logPath, "Plain INF note: Windows may reject a bundled INF unless it is packaged with a trusted signed catalog.");
                }

                return ok ? 0 : (lastError == 0 ? 1 : lastError);
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "Driver helper exception: " + ex);
                return -1;
            }
        }

        public static int RunLibwdi(string[] args)
        {
            if (args.Length < 4)
            {
                return 2;
            }

            string helperPath = args[1];
            string workDir = args[2];
            string logPath = args[3];

            try
            {
                AppendLog(logPath, "libwdi driver helper started.");
                AppendLog(logPath, "Helper: " + helperPath);
                AppendLog(logPath, "Working directory: " + workDir);

                if (!File.Exists(helperPath))
                {
                    AppendLog(logPath, "Helper executable was not found.");
                    return 3;
                }

                Directory.CreateDirectory(workDir);
                string shortHelperPath = GetShortPathOrOriginal(helperPath);
                string shortWorkDir = GetShortPathOrOriginal(workDir);
                AppendLog(logPath, "Helper short path: " + shortHelperPath);
                AppendLog(logPath, "Working directory short path: " + shortWorkDir);

                var psi = new ProcessStartInfo(shortHelperPath, "--dest " + QuoteForProcess(shortWorkDir));
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(helperPath);

                using (Process process = Process.Start(psi))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    AppendMultiline(logPath, "libwdi stdout", stdout);
                    AppendMultiline(logPath, "libwdi stderr", stderr);
                    AppendLog(logPath, "libwdi exit code: " + process.ExitCode.ToString());
                    AppendLog(logPath, @"SetupAPI device log: C:\Windows\INF\setupapi.dev.log");
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                AppendLog(logPath, "libwdi helper exception: " + ex);
                return -1;
            }
        }

        private static string GetShortPathOrOriginal(string path)
        {
            var buffer = new StringBuilder(1024);
            uint length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
            if (length > 0 && length < buffer.Capacity)
            {
                return buffer.ToString();
            }
            return path;
        }

        private static string QuoteForProcess(string text)
        {
            return "\"" + text.Replace("\"", "\\\"") + "\"";
        }

        private static void AppendMultiline(string logPath, string label, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                AppendLog(logPath, label + ": (empty)");
                return;
            }

            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    AppendLog(logPath, label + ": " + line);
                }
            }
        }

        private static void AppendLog(string logPath, string text)
        {
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + text + Environment.NewLine);
        }
    }

    internal sealed class DeviceInfo
    {
        public string InstanceId;
        public string Name;
        public string Service;
        public string DriverProvider;
        public string DriverVersion;
        public string InfName;

        public bool IsWinUsb
        {
            get { return string.Equals(Service, "WinUSB", StringComparison.OrdinalIgnoreCase); }
        }

        public string DriverSummary
        {
            get
            {
                var parts = new StringBuilder();
                parts.Append(string.IsNullOrEmpty(Service) ? "Service: unknown" : "Service: " + Service);
                if (!string.IsNullOrEmpty(InfName))
                {
                    parts.Append(", INF: " + InfName);
                }
                if (!string.IsNullOrEmpty(DriverProvider))
                {
                    parts.Append(", Provider: " + DriverProvider);
                }
                if (!string.IsNullOrEmpty(DriverVersion))
                {
                    parts.Append(", Version: " + DriverVersion);
                }
                return parts.ToString();
            }
        }

        public static DeviceInfo FindKarmaController()
        {
            DeviceInfo info = null;
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity"))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    string id = Convert.ToString(device["PNPDeviceID"]);
                    if (id == null || !id.StartsWith(MainDeviceIdPrefix(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    info = new DeviceInfo();
                    info.InstanceId = id;
                    info.Name = "Karma Controller";
                    info.Service = ReadService(id);
                    break;
                }
            }

            if (info == null)
            {
                return null;
            }

            FillSignedDriverInfo(info);
            return info;
        }

        private static string MainDeviceIdPrefix()
        {
            return "USB\\VID_1B8E&PID_C003";
        }

        private static string ReadService(string instanceId)
        {
            string keyPath = "SYSTEM\\CurrentControlSet\\Enum\\" + instanceId;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyPath))
            {
                if (key == null)
                {
                    return "";
                }
                return Convert.ToString(key.GetValue("Service"));
            }
        }

        private static void FillSignedDriverInfo(DeviceInfo info)
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPSignedDriver"))
            {
                foreach (ManagementObject driver in searcher.Get())
                {
                    string id = Convert.ToString(driver["DeviceID"]);
                    if (!string.Equals(id, info.InstanceId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    info.DriverProvider = Convert.ToString(driver["DriverProviderName"]);
                    info.DriverVersion = Convert.ToString(driver["DriverVersion"]);
                    info.InfName = Convert.ToString(driver["InfName"]);
                    break;
                }
            }
        }
    }
}
