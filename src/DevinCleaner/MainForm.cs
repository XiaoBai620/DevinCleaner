using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DevinCleaner.Engine;
using Microsoft.Data.Sqlite;

namespace DevinCleaner;

internal sealed class MainForm : Form
{
    private readonly Cleaner cleaner;
    private readonly IUpdateChecker updateChecker;
    private readonly Action<Uri> openReleasePage;
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly Button checkUpdates = DesktopTheme.Button("检查更新");
    private readonly Button downloadUpdate = DesktopTheme.Button("前往更新");
    private readonly Label updateNotice = DesktopTheme.Label("", 9.5f, true);
    private readonly SurfacePanel updateBanner = new() { Dock = DockStyle.Fill, Padding = new Padding(14, 6, 10, 6), Margin = new Padding(0, 4, 0, 8), CornerRadius = 10, Visible = false };
    private readonly RowStyle updateRowStyle = new(SizeType.Absolute, 0);
    private AvailableUpdate? availableUpdate;
    private bool checkingUpdate;
    private readonly HashSet<string> selected = [];
    private readonly Label processTitle = DesktopTheme.Label("正在检查本地状态", 9f);
    private readonly Label processDetail = DesktopTheme.Label("请稍候…", 8.5f);
    private readonly Label processDot = DesktopTheme.Label("●", 14f, true);
    private readonly Panel processBanner = new() { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = new Padding(24, 0, 12, 0), BackColor = Color.Transparent };
    private readonly Button diagnosticsButton = DesktopTheme.Button("查看检查详情");
    private readonly TextBox search = new() { PlaceholderText = "搜索标题、工作目录或会话 ID", BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 9.5f), AccessibleName = "搜索标题、工作目录或会话 ID" };
    private readonly Button clearSearch = new ClearSearchButton();
    private readonly ComboBox filter = new FilterComboBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Microsoft YaHei UI", 9f), AccessibleName = "筛选会话状态" };
    private readonly DataGridView list = new BufferedGrid();
    private readonly Label listCount = DesktopTheme.Label("读取中", 8.5f);
    private readonly Label emptyState = DesktopTheme.Label("正在读取会话…", 10f);
    private readonly Button selectAll = DesktopTheme.Button("全选结果");
    private readonly Button clearSelection = DesktopTheme.Button("清空选择");
    private readonly Button refresh = DesktopTheme.Button("刷新");
    private readonly Button backups = DesktopTheme.Button("备份与恢复");
    private readonly Button preview = DesktopTheme.Button("预览清理范围  →", primary: true);
    private readonly Label summary = DesktopTheme.Label("尚未选择清理内容", 11f, true);
    private readonly Label selectionCount = DesktopTheme.Label("勾选会话后，先核对范围，再确认清理。", 8.5f);
    private readonly Label detailStatus = DesktopTheme.Label("点击会话查看详情", 9f, true);
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = Color.White, ForeColor = DesktopTheme.Ink, Font = new Font("Microsoft YaHei UI", 9f), AccessibleName = "会话完整详情，可选择并复制" };
    private readonly Button copyDetails = DesktopTheme.Button("复制");
    private readonly Button expandDetails = DesktopTheme.Button("展开");
    private readonly Button viewConversation = DesktopTheme.Button("查看对话", primary: true);
    private readonly CheckBox cleanLocks = new() { Text = "孤立锁文件", AutoSize = true, ForeColor = DesktopTheme.Ink, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), AccessibleDescription = "可选，删除已没有对应会话的锁文件" };
    private readonly Label locksHint = DesktopTheme.Label(LocksHintText, 8f);
    private const string LocksHintText = "会话删除后遗留的占用标记，删了不影响对话";
    private readonly ToolTip tip = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };
    private readonly System.Windows.Forms.Timer processTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer busyTimer = new() { Interval = 500 };
    private readonly Stopwatch busyClock = new();
    private Snapshot? state;
    private List<string> diagnostics = [];
    private string? focusedId;
    private bool busy, rendering, checkingProcess;
    private string activity = "正在处理…";

    public MainForm(Cleaner cleaner, IUpdateChecker? updateChecker = null, Action<Uri>? openReleasePage = null)
    {
        this.cleaner = cleaner;
        this.updateChecker = updateChecker ?? new UpdateChecker();
        this.openReleasePage = openReleasePage ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        var version = UpdateChecker.CurrentVersion;
        Text = $"Devin 对话清理台 {version.Major}.{version.Minor}.{version.Build}";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(960, 680);
        Size = new Size(1180, 780);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = DesktopTheme.Canvas;
        ForeColor = DesktopTheme.Ink;
        Font = new Font("Microsoft YaHei UI", 9f);
        KeyPreview = true;

        var root = CreateLayout(1, 4);
        root.Padding = new Padding(20, 8, 20, 14);
        root.BackColor = DesktopTheme.Canvas;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(updateRowStyle);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.Controls.Add(BuildMasthead(version), 0, 0);
        root.Controls.Add(BuildUpdateBanner(), 0, 1);
        var body = CreateLayout(2, 1);
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(BuildListPanel(), 0, 0);
        body.Controls.Add(BuildSidebar(), 1, 0);
        root.Controls.Add(body, 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        Controls.Add(root);

        filter.Items.AddRange(["全部会话", "可清理", "运行中", "已隐藏"]);
        filter.SelectedIndex = 0;
        search.TextChanged += (_, _) => RenderSessions();
        clearSearch.Click += (_, _) => { search.Clear(); search.Focus(); };
        filter.SelectedIndexChanged += (_, _) => RenderSessions();
        list.CellClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) ToggleRow(e.RowIndex); };
        list.CurrentCellChanged += (_, _) => { if (!rendering) RenderDetails(); };
        list.CellPainting += PaintStatus;
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Space || list.CurrentRow is null) return;
            e.Handled = e.SuppressKeyPress = true;
            ToggleRow(list.CurrentRow.Index);
        };
        selectAll.Click += (_, _) => ToggleVisible();
        clearSelection.Click += (_, _) => { selected.Clear(); RenderSessions(); };
        cleanLocks.CheckedChanged += (_, _) => RenderSelection();
        list.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != 0) ShowConversation(); };
        viewConversation.Click += (_, _) => ShowConversation();
        refresh.Click += async (_, _) => await Reload();
        backups.Click += async (_, _) => await ShowBackups();
        checkUpdates.Click += async (_, _) => await CheckForUpdates(manual: true);
        downloadUpdate.Click += (_, _) => OpenUpdate();
        preview.Click += async (_, _) => await PreviewCleanup();
        diagnosticsButton.Click += (_, _) => ShowTextDialog("本地检查详情", string.Join("\r\n\r\n", diagnostics.Select((d, i) => $"{i + 1}. {d}")));
        expandDetails.Click += (_, _) => ShowTextDialog("会话完整详情", details.Text);
        copyDetails.Click += (_, _) =>
        {
            try { if (details.TextLength > 0) Clipboard.SetText(details.Text); }
            catch (ExternalException) { MessageBox.Show(this, "剪贴板暂时被其他程序占用，请稍后重试。", "无法复制", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        processTimer.Tick += async (_, _) => await RefreshProcessState();
        busyTimer.Tick += (_, _) => RenderSelection();
        Shown += async (_, _) =>
        {
            ApplyGridMetrics();
            var updateTask = CheckForUpdates(manual: false);
            await Reload();
            if (!IsDisposed) { ActiveControl = search; processTimer.Start(); }
            await updateTask;
        };
        DpiChanged += (_, _) => BeginInvoke(() =>
        {
            ApplyGridMetrics();
            if (availableUpdate is not null) updateRowStyle.Height = 56 * DeviceDpi / 96f;
        });
        KeyDown += async (_, e) =>
        {
            if (busy) return;
            if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; search.Focus(); search.SelectAll(); }
            else if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; await Reload(); }
            else if (e.KeyCode == Keys.Escape && search.ContainsFocus) { e.SuppressKeyPress = true; search.Clear(); }
        };
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            MessageBox.Show(this, "正在处理本地数据，请等待完成后再关闭。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        tip.SetToolTip(search, "搜索标题、工作目录或会话 ID · Ctrl+F");
        tip.SetToolTip(refresh, "重新读取本地记录 · F5");
        tip.SetToolTip(clearSearch, "清除搜索");
        tip.SetToolTip(backups, $"查看删除前自动生成的备份，并恢复其中的会话。\n备份目录：{cleaner.BackupRoot}");
        tip.SetToolTip(expandDetails, "在独立窗口查看完整详情");
        tip.SetToolTip(viewConversation, "以聊天窗口查看这个会话的对话内容（只读）· 也可双击列表行");
        tip.SetToolTip(cleanLocks, ConfirmDialog.OrphanLocksHelp);
        tip.SetToolTip(locksHint, ConfirmDialog.OrphanLocksHelp);
        tip.SetToolTip(checkUpdates, "启动时自动检查 GitHub 正式版；也可点击重新检查。不发送本地会话数据。");
        RenderSelection();
    }

    private static TableLayoutPanel CreateLayout(int columns, int rows)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty, BackColor = Color.Transparent };
        if (columns == 1) panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }

    private Control BuildMasthead(Version version)
    {
        var header = CreateLayout(4, 1);
        header.Padding = new Padding(0, 0, 0, 8);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var brand = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
        brand.Controls.Add(new DevinLogo { Size = new Size(24, 24), Margin = new Padding(0, 4, 8, 0) });
        var title = DesktopTheme.Label("DevinCleaner", 11.5f, true);
        title.AutoSize = true;
        title.Margin = new Padding(0, 5, 0, 0);
        brand.Controls.Add(title);
        header.Controls.Add(brand, 0, 0);
        var badge = DesktopTheme.Label($"v{version.Major}.{version.Minor}.{version.Build}", 8f);
        badge.AutoSize = true;
        badge.Anchor = AnchorStyles.Left;
        badge.Margin = new Padding(10, 0, 0, 0);
        header.Controls.Add(badge, 1, 0);
        header.Controls.Add(BuildProcessBanner(), 2, 0);
        checkUpdates.Anchor = AnchorStyles.Right;
        checkUpdates.Margin = Padding.Empty;
        header.Controls.Add(checkUpdates, 3, 0);
        return header;
    }

    private Control BuildUpdateBanner()
    {
        var row = CreateLayout(2, 1);
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        updateNotice.Dock = DockStyle.Fill;
        updateNotice.TextAlign = ContentAlignment.MiddleLeft;
        updateNotice.AutoEllipsis = true;
        updateNotice.ForeColor = DesktopTheme.Ink;
        downloadUpdate.Anchor = AnchorStyles.Right;
        row.Controls.Add(updateNotice, 0, 0);
        row.Controls.Add(downloadUpdate, 1, 0);
        updateBanner.Controls.Add(row);
        return updateBanner;
    }

    private async Task CheckForUpdates(bool manual)
    {
        if (checkingUpdate || IsDisposed || updateCancellation.IsCancellationRequested) return;
        checkingUpdate = true;
        checkUpdates.Enabled = false;
        checkUpdates.Text = "检查中…";
        try
        {
            var update = await updateChecker.CheckAsync(updateCancellation.Token);
            if (IsDisposed || updateCancellation.IsCancellationRequested) return;
            availableUpdate = update;
            updateBanner.Visible = update is not null;
            updateRowStyle.Height = update is null ? 0 : 56 * DeviceDpi / 96f;
            if (update is not null)
            {
                updateNotice.Text = $"发现新版本 v{update.Version} · 当前 v{UpdateChecker.CurrentVersion}";
                tip.SetToolTip(updateNotice, updateNotice.Text);
                tip.SetToolTip(downloadUpdate, "打开 GitHub 发布页，下载新版后退出并替换程序。");
            }
            checkUpdates.Text = update is null && manual ? "已是最新版本" : "检查更新";
            tip.SetToolTip(checkUpdates, update is null ? "当前已是最新正式版。点击可重新检查。" : "发现新版本，点击「前往更新」查看发布说明和下载。");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            if (IsDisposed || updateCancellation.IsCancellationRequested) return;
            checkUpdates.Text = manual ? "检查失败，重试" : "检查更新";
            tip.SetToolTip(checkUpdates, "暂时无法检查更新，请检查网络后重试。本地清理可继续使用。");
        }
        finally
        {
            checkingUpdate = false;
            if (!IsDisposed && !updateCancellation.IsCancellationRequested) checkUpdates.Enabled = true;
        }
    }

    private void OpenUpdate()
    {
        if (availableUpdate is null || busy) return;
        try { openReleasePage(availableUpdate.ReleaseUri); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"无法打开浏览器，请访问发布页：\n{availableUpdate.ReleaseUri.AbsoluteUri}", "前往更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private Control BuildProcessBanner()
    {
        var row = CreateLayout(4, 1);
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        processDot.Dock = DockStyle.Fill;
        processDot.TextAlign = ContentAlignment.MiddleLeft;
        row.Controls.Add(processDot, 0, 0);
        processTitle.AutoSize = true;
        processTitle.Anchor = AnchorStyles.Left;
        processTitle.Margin = new Padding(0, 0, 12, 0);
        processDetail.Dock = DockStyle.Fill;
        processDetail.TextAlign = ContentAlignment.MiddleLeft;
        processDetail.AutoEllipsis = true;
        row.Controls.Add(processTitle, 1, 0);
        row.Controls.Add(processDetail, 2, 0);
        diagnosticsButton.Visible = false;
        diagnosticsButton.Anchor = AnchorStyles.Right;
        diagnosticsButton.Padding = new Padding(7, 2, 7, 2);
        diagnosticsButton.Font = new Font("Microsoft YaHei UI", 8f);
        row.Controls.Add(diagnosticsButton, 3, 0);
        processBanner.Controls.Add(row);
        return processBanner;
    }

    private Control BuildListPanel()
    {
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 8), Margin = new Padding(0, 0, 12, 0), CornerRadius = 12 };
        var panel = CreateLayout(1, 4);
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var titleRow = CreateLayout(3, 1);
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = DesktopTheme.Label("会话", 11f, true);
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        listCount.Dock = DockStyle.Fill;
        listCount.TextAlign = ContentAlignment.MiddleLeft;
        listCount.AutoEllipsis = true;
        refresh.Anchor = AnchorStyles.Right;
        titleRow.Controls.Add(title, 0, 0);
        titleRow.Controls.Add(listCount, 1, 0);
        titleRow.Controls.Add(refresh, 2, 0);
        panel.Controls.Add(titleRow, 0, 0);

        var searchRow = CreateLayout(2, 1);
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
        searchRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        searchRow.Controls.Add(new SearchField(search, clearSearch) { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 10, 6) }, 0, 0);
        filter.Margin = new Padding(0, 2, 0, 6);
        searchRow.Controls.Add(filter, 1, 0);
        panel.Controls.Add(searchRow, 0, 1);

        var actions = CreateLayout(2, 1);
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var selectionActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
        selectAll.Margin = new Padding(0, 0, 7, 0);
        selectionActions.Controls.Add(selectAll);
        selectionActions.Controls.Add(clearSelection);
        backups.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        actions.Controls.Add(selectionActions, 0, 0);
        actions.Controls.Add(backups, 1, 0);
        panel.Controls.Add(actions, 0, 2);

        ConfigureGrid();
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
        emptyState.Dock = DockStyle.Fill;
        emptyState.TextAlign = ContentAlignment.MiddleCenter;
        emptyState.BackColor = Color.White;
        host.Controls.Add(list);
        host.Controls.Add(emptyState);
        emptyState.BringToFront();
        panel.Controls.Add(host, 0, 3);
        card.Controls.Add(panel);
        return card;
    }

    private void ConfigureGrid()
    {
        list.Dock = DockStyle.Fill;
        list.BackgroundColor = Color.White;
        list.BorderStyle = BorderStyle.None;
        list.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        list.GridColor = Color.FromArgb(242, 242, 242);
        list.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        list.EnableHeadersVisualStyles = false;
        list.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        list.ColumnHeadersHeight = 36;
        list.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = DesktopTheme.Faint, Font = new Font("Microsoft YaHei UI", 8.5f), SelectionBackColor = Color.White, Padding = new Padding(5, 0, 0, 0) };
        list.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = DesktopTheme.Ink, Font = new Font("Microsoft YaHei UI", 9.5f), SelectionBackColor = DesktopTheme.Hover, SelectionForeColor = DesktopTheme.Ink, Padding = new Padding(5, 0, 0, 0) };
        list.RowTemplate.Height = 43;
        list.RowHeadersVisible = false;
        list.ReadOnly = true;
        list.AllowUserToAddRows = list.AllowUserToDeleteRows = list.AllowUserToResizeRows = list.AllowUserToOrderColumns = false;
        list.AllowUserToResizeColumns = false;
        list.MultiSelect = false;
        list.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        list.AutoGenerateColumns = false;
        list.ShowCellToolTips = true;
        list.AccessibleName = "Devin 会话列表，点击会话查看详情，勾选或按空格选择清理";
        var small = new DataGridViewCellStyle { Font = new Font("Microsoft YaHei UI", 8f), ForeColor = DesktopTheme.Muted };
        list.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "", Width = 34, SortMode = DataGridViewColumnSortMode.NotSortable, Resizable = DataGridViewTriState.False, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = Padding.Empty } });
        list.Columns.Add(new DataGridViewTextBoxColumn { Name = "Title", HeaderText = "会话标题", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 130, SortMode = DataGridViewColumnSortMode.NotSortable });
        list.Columns.Add(new DataGridViewTextBoxColumn { Name = "Updated", HeaderText = "最后活动", Width = 120, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = small });
        list.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "消息大小", Width = 80, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = small });
        list.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "状态", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable });
    }

    private Control BuildSidebar()
    {
        var sidebar = CreateLayout(1, 2);
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        var detailCard = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 12, 13), Margin = new Padding(0, 0, 0, 12), CornerRadius = 12 };
        var detailLayout = CreateLayout(1, 4);
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var detailHeading = CreateLayout(3, 1);
        detailHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailHeading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var detailTitle = DesktopTheme.Label("会话详情", 10f, true);
        detailTitle.Dock = DockStyle.Fill;
        detailTitle.TextAlign = ContentAlignment.MiddleLeft;
        copyDetails.Anchor = AnchorStyles.Right;
        copyDetails.Padding = new Padding(8, 4, 8, 4);
        expandDetails.Anchor = AnchorStyles.Right;
        expandDetails.Font = copyDetails.Font;
        expandDetails.Padding = copyDetails.Padding;
        expandDetails.Margin = new Padding(0, 0, 6, 0);
        expandDetails.AccessibleName = "展开会话完整详情";
        detailHeading.Controls.Add(detailTitle, 0, 0);
        detailHeading.Controls.Add(expandDetails, 1, 0);
        detailHeading.Controls.Add(copyDetails, 2, 0);
        detailLayout.Controls.Add(detailHeading, 0, 0);
        detailStatus.Dock = DockStyle.Fill;
        detailStatus.TextAlign = ContentAlignment.MiddleLeft;
        detailStatus.AutoEllipsis = true;
        detailLayout.Controls.Add(detailStatus, 0, 1);
        details.Margin = new Padding(0, 5, 0, 0);
        detailLayout.Controls.Add(details, 0, 2);
        viewConversation.Dock = DockStyle.Fill;
        viewConversation.Margin = new Padding(0, 10, 4, 0);
        detailLayout.Controls.Add(viewConversation, 0, 3);
        detailCard.Controls.Add(detailLayout);
        sidebar.Controls.Add(detailCard, 0, 0);

        var options = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 11), Margin = Padding.Empty, CornerRadius = 12 };
        var optionLayout = CreateLayout(1, 4);
        optionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        optionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        optionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        optionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var optionTitle = DesktopTheme.Label("附加维护", 10f, true);
        optionTitle.Dock = DockStyle.Fill;
        optionLayout.Controls.Add(optionTitle, 0, 0);
        var optionHint = DesktopTheme.Label("可选 · 可单独执行，不必勾选会话", 8f);
        optionHint.Dock = DockStyle.Fill;
        optionLayout.Controls.Add(optionHint, 0, 1);
        cleanLocks.Margin = new Padding(0, 3, 0, 0);
        optionLayout.Controls.Add(cleanLocks, 0, 2);
        locksHint.Dock = DockStyle.Fill;
        locksHint.Padding = new Padding(20, 0, 0, 0);
        locksHint.AutoEllipsis = true;
        optionLayout.Controls.Add(locksHint, 0, 3);
        options.Controls.Add(optionLayout);
        sidebar.Controls.Add(options, 0, 1);
        return sidebar;
    }

    private Control BuildFooter()
    {
        var footer = CreateLayout(2, 1);
        footer.Padding = new Padding(3, 13, 0, 0);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var description = CreateLayout(1, 2);
        description.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        description.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        summary.Dock = selectionCount.Dock = DockStyle.Fill;
        summary.AutoEllipsis = selectionCount.AutoEllipsis = true;
        description.Controls.Add(summary, 0, 0);
        description.Controls.Add(selectionCount, 0, 1);
        footer.Controls.Add(description, 0, 0);
        preview.Padding = new Padding(20, 10, 20, 10);
        preview.Margin = new Padding(16, 0, 0, 0);
        preview.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        footer.Controls.Add(preview, 1, 0);
        return footer;
    }

    private IEnumerable<SessionInfo> VisibleSessions()
    {
        var q = search.Text.Trim();
        return (state?.Sessions ?? []).Where(s =>
            (q.Length == 0 || s.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || s.WorkingDirectory.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Id.Contains(q, StringComparison.OrdinalIgnoreCase)) &&
            (filter.SelectedIndex switch { 1 => s.CanDelete, 2 => s.Running, 3 => s.Hidden, _ => true }));
    }

    private static string StatusText(SessionInfo s) => s.Running ? "运行中" : !s.CanDelete ? "受保护" : s.Hidden ? "已隐藏" : "本地会话";

    private static (Color Ink, Color Fill) StatusColors(SessionInfo s) => s.Running
        ? (DesktopTheme.Warning, Color.FromArgb(254, 247, 232))
        : !s.CanDelete ? (Color.FromArgb(185, 28, 28), Color.FromArgb(254, 242, 242))
        : s.Hidden ? (DesktopTheme.Muted, DesktopTheme.Hover)
        : (Color.FromArgb(21, 128, 61), Color.FromArgb(240, 253, 244));

    private void PaintStatus(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 4 || list.Rows[e.RowIndex].Tag is not SessionInfo session || e.Graphics is null) return;
        e.PaintBackground(e.ClipBounds, true);
        var (ink, fill) = StatusColors(session);
        var scale = DeviceDpi / 96f;
        var inset = Math.Max(2, (int)(e.CellBounds.Height * 0.24f));
        var pill = new Rectangle(e.CellBounds.Left + (int)(5 * scale), e.CellBounds.Top + inset, e.CellBounds.Width - (int)(13 * scale), e.CellBounds.Height - 2 * inset);
        if (pill.Width > 0 && pill.Height > 0)
        {
            var smoothing = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(fill);
            using var path = DesktopTheme.RoundedRect(pill, pill.Height / 2f);
            e.Graphics.FillPath(brush, path);
            e.Graphics.SmoothingMode = smoothing;
            TextRenderer.DrawText(e.Graphics, StatusText(session), e.CellStyle?.Font ?? list.Font, pill, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        e.Handled = true;
    }

    private void ApplyGridMetrics()
    {
        if (IsDisposed || list.Columns.Count < 5) return;
        int ScaleSize(int value) => (int)Math.Round(value * DeviceDpi / 96f);
        list.RowTemplate.Height = ScaleSize(43);
        list.ColumnHeadersHeight = ScaleSize(36);
        list.Columns[0].Width = ScaleSize(34);
        list.Columns[1].MinimumWidth = ScaleSize(130);
        list.Columns[2].Width = ScaleSize(128);
        list.Columns[3].Width = ScaleSize(80);
        list.Columns[4].Width = ScaleSize(90);
        foreach (DataGridViewRow row in list.Rows) row.Height = list.RowTemplate.Height;
        list.Invalidate();
    }

    private void ToggleRow(int index)
    {
        if (busy || index < 0 || index >= list.Rows.Count || list.Rows[index].Tag is not SessionInfo session) return;
        list.CurrentCell = list.Rows[index].Cells[1];
        if (!session.CanDelete) { RenderDetails(); return; }
        if (!selected.Add(session.Id)) selected.Remove(session.Id);
        list.Rows[index].Cells[0].Value = selected.Contains(session.Id);
        RenderSelection();
    }

    private void ToggleVisible()
    {
        var selectable = VisibleSessions().Where(s => s.CanDelete).ToList();
        var clear = selectable.Count > 0 && selectable.All(s => selected.Contains(s.Id));
        foreach (var s in selectable)
            if (clear) selected.Remove(s.Id); else selected.Add(s.Id);
        RenderSessions();
    }

    private void RenderSessions()
    {
        if (IsDisposed) return;
        rendering = true;
        list.SuspendLayout();
        try
        {
            list.Rows.Clear();
            foreach (var s in VisibleSessions())
            {
                var index = list.Rows.Add(selected.Contains(s.Id), s.Title, s.LastActivityAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), Cleaner.FormatBytes(s.Bytes), StatusText(s));
                var row = list.Rows[index];
                row.Tag = s;
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = $"{s.Title}\n{s.WorkingDirectory}\n{s.Id}" + (s.BlockedReason is null ? "" : "\n" + s.BlockedReason);
            }
            var focus = list.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is SessionInfo s && s.Id == focusedId);
            list.ClearSelection();
            if (focus is not null) { list.CurrentCell = focus.Cells[1]; focus.Selected = true; }
            else list.CurrentCell = null;
        }
        finally { list.ResumeLayout(); rendering = false; }
        emptyState.Text = state is null ? "暂时无法读取会话\n点击「刷新」重新检查" : state.Sessions.Count == 0 ? "还没有本地会话\nDevin 的会话会显示在这里" : "没有匹配的会话\n试试其他关键词或筛选条件";
        emptyState.Visible = list.Rows.Count == 0;
        list.Visible = list.Rows.Count > 0;
        listCount.Text = state is null ? "尚未加载" : $"{state.Sessions.Count} 个会话 · 显示 {list.Rows.Count} 个 · 数据库 {Cleaner.FormatBytes(state.DatabaseBytes)}";
        cleanLocks.Text = state is null ? "孤立锁文件" : $"孤立锁文件（{state.OrphanLocks.Count} 个）";
        RenderDetails();
        RenderSelection();
    }

    private void RenderDetails()
    {
        if (list.CurrentRow?.Tag is not SessionInfo s)
        {
            focusedId = null;
            detailStatus.Text = "选择一个会话";
            detailStatus.ForeColor = DesktopTheme.Muted;
            details.Text = "点击列表中的会话，在这里查看完整标题、工作目录和消息统计。\r\n\r\n勾选方框才会加入清理范围。";
            details.SelectionStart = details.SelectionLength = 0;
            copyDetails.Enabled = expandDetails.Enabled = viewConversation.Enabled = false;
            return;
        }
        focusedId = s.Id;
        detailStatus.Text = StatusText(s);
        detailStatus.ForeColor = StatusColors(s).Ink;
        details.Text = $"{s.Title}"
            + (s.BlockedReason is null ? "" : $"\r\n\r\n检查说明\r\n{s.BlockedReason}")
            + $"\r\n\r\n会话 ID\r\n{s.Id}\r\n\r\n工作目录\r\n{s.WorkingDirectory}\r\n\r\n模型\r\n{s.Model}"
            + $"\r\n\r\n消息\r\n{s.Messages:N0} 条 · {Cleaner.FormatBytes(s.Bytes)}"
            + $"\r\n\r\n创建时间\r\n{s.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n\r\n最后活动\r\n{s.LastActivityAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            + (s.Hidden ? "\r\n\r\n可见性\r\n已在 Devin 中隐藏" : "");
        details.SelectionStart = details.SelectionLength = 0;
        copyDetails.Enabled = expandDetails.Enabled = true;
        viewConversation.Enabled = !busy && s.Messages > 0;
        viewConversation.Text = s.Messages > 0 ? "查看对话" : "没有对话内容";
    }

    private void RenderSelection()
    {
        if (IsDisposed) return;
        var visible = VisibleSessions().ToList();
        var selectable = visible.Where(s => s.CanDelete).ToList();
        var hidden = selected.Count - visible.Count(s => selected.Contains(s.Id));
        var bytes = state?.Sessions.Where(s => selected.Contains(s.Id)).Sum(s => s.Bytes) ?? 0;
        var parts = new List<string>();
        if (selected.Count > 0) parts.Add($"已选 {selected.Count} 个会话（{Cleaner.FormatBytes(bytes)}）");
        if (cleanLocks.Checked) parts.Add("孤立锁文件");
        summary.Text = busy ? $"{activity}…  {busyClock.Elapsed.TotalSeconds:0} 秒" : parts.Count > 0 ? string.Join("  +  ", parts) : "尚未选择清理内容";
        selectionCount.Text = hidden > 0 ? $"筛选外仍有 {hidden} 个已选" : "先预览，再确认 · 删除前自动备份，可在「备份与恢复」中找回";
        selectionCount.ForeColor = hidden > 0 ? DesktopTheme.Warning : DesktopTheme.Muted;
        tip.SetToolTip(summary, summary.Text);
        tip.SetToolTip(selectionCount, selectionCount.Text);
        var supported = state?.Supported == true;
        preview.Enabled = !busy && state is not null && parts.Count > 0 && (selected.Count == 0 || supported);
        preview.Text = busy ? "正在处理…" : "预览清理范围  →";
        refresh.Enabled = search.Enabled = filter.Enabled = list.Enabled = backups.Enabled = !busy;
        clearSearch.Enabled = !busy && search.TextLength > 0;
        clearSearch.Visible = search.TextLength > 0;
        cleanLocks.Enabled = !busy && state is not null;
        viewConversation.Enabled = !busy && list.CurrentRow?.Tag is SessionInfo { Messages: > 0 };
        selectAll.Enabled = !busy && selectable.Count > 0;
        selectAll.Text = selectable.Count > 0 && selectable.All(s => selected.Contains(s.Id)) ? "取消结果选择" : "全选结果";
        clearSelection.Enabled = !busy && selected.Count > 0;
        downloadUpdate.Enabled = !busy;
    }

    private async Task Run(string label, Func<Task> action)
    {
        if (busy) return;
        busy = true;
        activity = label;
        busyClock.Restart();
        busyTimer.Start();
        UseWaitCursor = true;
        RenderSelection();
        try { await action(); }
        catch (Exception e) when (e is CleanerException or IOException or UnauthorizedAccessException or SqliteException)
        {
            if (!IsDisposed) { busyTimer.Stop(); MessageBox.Show(this, e.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        finally
        {
            busy = false;
            busyTimer.Stop();
            if (!IsDisposed) { UseWaitCursor = false; RenderSelection(); }
        }
    }

    private void SetActivity(string label)
    {
        activity = label;
        busyClock.Restart();
        if (!IsDisposed) RenderSelection();
    }

    private Task Reload() => Run("正在读取本地会话", LoadState);

    private async Task LoadState()
    {
        Snapshot snapshot;
        ProcessState process;
        try { (snapshot, process) = await Task.Run(() => (cleaner.Snapshot(), cleaner.CheckProcesses())); }
        catch
        {
            state = null;
            selected.Clear();
            RenderSessions();
            throw;
        }
        if (IsDisposed) return;
        state = snapshot;
        selected.IntersectWith(snapshot.Sessions.Where(s => s.CanDelete).Select(s => s.Id));
        diagnostics = [.. snapshot.Warnings];
        diagnosticsButton.Text = $"查看检查详情（{diagnostics.Count}）";
        diagnosticsButton.Visible = diagnostics.Count > 0;
        SetProcess(process);
        RenderSessions();
    }

    private async Task RefreshProcessState()
    {
        if (busy || checkingProcess || IsDisposed) return;
        checkingProcess = true;
        try
        {
            var process = await Task.Run(cleaner.CheckProcesses);
            if (!IsDisposed && !busy) SetProcess(process);
        }
        catch (Exception e)
        {
            if (!IsDisposed)
            {
                processTitle.Text = "状态待确认";
                processDetail.Text = "执行前会逐个检查会话锁。";
                processDot.ForeColor = DesktopTheme.Warning;
                tip.SetToolTip(processDetail, e.Message);
            }
        }
        finally { checkingProcess = false; }
    }

    private void SetProcess(ProcessState process)
    {
        var running = state?.Sessions.Count(s => s.Running) ?? 0;
        processDot.ForeColor = process.Running ? DesktopTheme.Warning : DesktopTheme.Success;
        processTitle.Text = process.Running ? "Devin 正在运行" : "准备就绪";
        processDetail.Text = process.Running ? $"{running} 个会话运行中，将被跳过 · 其他会话可直接清理" : "未检测到 Devin 进程";
        tip.SetToolTip(processTitle, process.Running ? string.Join(Environment.NewLine, process.Processes) : "未检测到 Devin 相关进程。");
        tip.SetToolTip(processDetail, $"{processDetail.Text}。\n数据目录：{cleaner.DataRoot}");
    }

    private void ShowTextDialog(string title, string content)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(720, 460), MinimumSize = new Size(540, 340), Font = Font, BackColor = DesktopTheme.Canvas, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Padding = new Padding(18) };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = Color.White, ForeColor = DesktopTheme.Ink, Text = content, AccessibleName = title };
        var close = DesktopTheme.Button("关闭");
        close.Dock = DockStyle.Bottom;
        close.DialogResult = DialogResult.Cancel;
        dialog.Controls.Add(text);
        dialog.Controls.Add(close);
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    private Task PreviewCleanup() => Run("正在生成清理预览", async () =>
    {
        var plan = await Task.Run(() => cleaner.PlanCleanup(new Selection([.. selected], cleanLocks.Checked)));
        busyTimer.Stop();
        if (!Confirm(plan)) return;
        busyTimer.Start();
        var stages = new Progress<string>(SetActivity);
        SetActivity("正在执行清理");
        var result = await Task.Run(() => cleaner.Execute(plan.Id, stages));
        selected.Clear();
        cleanLocks.Checked = false;
        SetActivity("正在刷新会话列表");
        await LoadState();
        busyTimer.Stop();
        var message = new List<string>();
        if (result.DeletedSessions > 0) message.Add($"已删除 {result.DeletedSessions} 个会话。\n备份：{result.BackupPath}");
        if (plan.Selection.CleanOrphanLocks) message.Add($"已删除 {result.RemovedLocks} 个孤立锁文件。");
        MessageBox.Show(this, string.Join("\n\n", message), "处理完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    });

    private void ShowConversation()
    {
        if (busy || list.CurrentRow?.Tag is not SessionInfo { Messages: > 0 } session) return;
        using var dialog = new ConversationDialog(cleaner, session);
        dialog.ShowDialog(this);
    }

    private bool Confirm(Plan plan)
    {
        UseWaitCursor = false;
        try { using var dialog = new ConfirmDialog(plan, cleaner.IsRunning); return dialog.ShowDialog(this) == DialogResult.OK; }
        finally { if (!IsDisposed) UseWaitCursor = true; }
    }

    private Task ShowBackups() => Run("正在使用备份与恢复", async () =>
    {
        UseWaitCursor = false;
        busyTimer.Stop();
        bool changed;
        using (var dialog = new BackupDialog(cleaner)) { dialog.ShowDialog(this); changed = dialog.Changed; }
        if (changed) await LoadState();
    });

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            updateCancellation.Cancel();
            updateCancellation.Dispose();
            processTimer.Stop();
            processTimer.Dispose();
            busyTimer.Dispose();
            tip.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class BufferedGrid : DataGridView
    {
        public BufferedGrid() { DoubleBuffered = true; }
    }
}
