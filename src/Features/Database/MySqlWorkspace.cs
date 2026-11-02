using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace Curl2Url
{
    // MySQL 工作区：左侧库表树，右侧 SQL 编辑器 + 结果 / 表数据 / 表结构 / 消息。
    // 所有网络操作都走 AsyncUi，UI 线程只做绑定；会话本身仍由 MySqlSession 串行化。
    internal sealed class MySqlWorkspace : UserControl, IDatabaseWorkspace
    {
        private const int MaxResultRows = 500;
        private const int QueryTimeoutSeconds = 60;
        private const int TreeRowHeight = 28;
        private static readonly object PlaceholderTag = new object();

        // 数据库节点下的「表」分组；展开时才去加载表列表。
        private sealed class TableGroupTag
        {
            public string Database;
            public bool Loading;
            public bool Loaded;
            public int Count = -1;
        }

        // 树是自绘的，字体只建一次，别在 DrawNode 里反复 new Font 漏 GDI 句柄。
        private static readonly Font TreeFont = AppTheme.UiFont(9F);
        private static readonly Font TreeCountFont = AppTheme.UiFont(8F);
        private static readonly Color TreeHoverBack = Color.FromArgb(245, 247, 250);
        private static readonly Color TreeDbDot = Color.FromArgb(82, 196, 26);
        // 所有库统一用同一个橙色，不按库名轮换颜色。
        private static readonly Color TreeDbColor = Color.FromArgb(230, 162, 60);

        private readonly DatabaseTabHost _host;
        private readonly DatabaseConnectionProfile _profile;
        private readonly MySqlSession _session;

        private readonly SplitContainer _outer;
        private readonly TextBox _searchBox;
        private readonly TreeView _tree;
        private readonly Label _treeHint;

        private readonly ComboBox _dbCombo;
        private readonly ComboBox _historyCombo;
        private readonly Button _executeButton;
        private readonly Button _cancelButton;
        private readonly Button _explainButton;
        private readonly Button _aiButton;
        private readonly SqlHighlightBox _sqlEditor;
        private readonly TextBox _aiQuestion;
        private readonly Button _aiQueryButton;
        private readonly ToolTip _aiTip;

        private readonly TabControl _resultTabs;
        private readonly DataGridView _queryGrid;
        private readonly Label _queryMessage;
        private readonly Button _queryCopyButton;
        private readonly Button _queryExportButton;

        private readonly Label _tableNameLabel;
        private readonly DataGridView _tableGrid;
        private readonly Label _tableMessage;
        private readonly Button _addRowButton;
        private readonly Button _deleteRowButton;
        private readonly Button _saveButton;
        private readonly Button _discardButton;
        private readonly Button _tableCopyButton;

        private readonly DataGridView _schemaGrid;
        private readonly Label _schemaSummary;
        private readonly TabPage _tableTab;

        private readonly TextBox _messages;
        private TreeNode _hotNode;

        private readonly List<string> _history = new List<string>();

        private string _currentDatabase = "";
        private DbTableRef _currentTable;
        private DbTableData _tableData;
        private List<DbColumnInfo> _tableColumns = new List<DbColumnInfo>();
        private string _createSqlText = "";
        private int _tableLoadToken;
        private int _schemaToken;
        private bool _queryBusy;
        private bool _saveBusy;
        private bool _aiBusy;
        private bool _aiSchemaConfirmed;
        private bool _loadingDbCombo;
        private bool _suppressHistory;

        private string _lastSql = "";
        private DataTable _lastResult;
        private string _lastError = "";

        public MySqlWorkspace(DatabaseTabHost host, DatabaseConnectionProfile profile, MySqlSession session)
        {
            _host = host;
            _profile = profile;
            _session = session;
            BackColor = Color.White;
            Font = AppTheme.UiFont(9F);

            _outer = new SplitContainer();
            _outer.Size = new Size(1000, 700);
            _outer.Dock = DockStyle.Fill;
            _outer.Orientation = Orientation.Vertical;
            _outer.SplitterWidth = 6;
            _outer.Panel1MinSize = 200;
            _outer.Panel2MinSize = 400;
            _outer.SplitterDistance = 300;
            _outer.BackColor = AppTheme.LineColor;
            Controls.Add(_outer);

            TableLayoutPanel left = new TableLayoutPanel();
            left.Dock = DockStyle.Fill;
            left.ColumnCount = 1;
            left.RowCount = 4;
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            left.Padding = new Padding(8, 6, 4, 4);
            left.BackColor = Color.White;
            _outer.Panel1.Controls.Add(left);

            Label objectsTitle = new Label();
            objectsTitle.Text = "数据库对象";
            objectsTitle.Dock = DockStyle.Fill;
            objectsTitle.TextAlign = ContentAlignment.MiddleLeft;
            objectsTitle.Font = AppTheme.UiFont(9.5F, FontStyle.Bold);
            objectsTitle.ForeColor = AppTheme.TitleColor;
            left.Controls.Add(objectsTitle, 0, 0);

            TableLayoutPanel searchRow = new TableLayoutPanel();
            searchRow.Dock = DockStyle.Fill;
            searchRow.ColumnCount = 4;
            searchRow.RowCount = 1;
            searchRow.Margin = new Padding(0);
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
            Label searchLabel = new Label();
            searchLabel.Text = "表名";
            searchLabel.Dock = DockStyle.Fill;
            searchLabel.AutoSize = false;
            searchLabel.Margin = new Padding(0, 0, 2, 0);
            searchLabel.TextAlign = ContentAlignment.MiddleLeft;
            searchLabel.ForeColor = AppTheme.LabelColor;
            searchRow.Controls.Add(searchLabel, 0, 0);
            _searchBox = new TextBox();
            _searchBox.Dock = DockStyle.Fill;
            _searchBox.Margin = new Padding(0, 4, 4, 4);
            _searchBox.BorderStyle = BorderStyle.FixedSingle;
            _searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SearchTables();
                }
            };
            searchRow.Controls.Add(_searchBox, 1, 0);
            Button searchButton = AppTheme.MakeSmallButton("搜索", 46);
            searchButton.Dock = DockStyle.Fill;
            searchButton.Margin = new Padding(0, 3, 2, 3);
            searchButton.Click += delegate { SearchTables(); };
            searchRow.Controls.Add(searchButton, 2, 0);
            Button clearButton = AppTheme.MakeSmallButton("清除", 46);
            clearButton.Dock = DockStyle.Fill;
            clearButton.Margin = new Padding(0, 3, 0, 3);
            clearButton.Click += delegate { ClearSearch(); };
            searchRow.Controls.Add(clearButton, 3, 0);
            left.Controls.Add(searchRow, 0, 1);

            _tree = new TreeView();
            _tree.Dock = DockStyle.Fill;
            _tree.HideSelection = false;
            _tree.FullRowSelect = true;
            _tree.ShowNodeToolTips = true;
            // 参考 DBX 的左侧对象树：无连接线、整行选中、自己画图标和展开箭头。
            _tree.ShowLines = false;
            _tree.ShowPlusMinus = false;
            _tree.ShowRootLines = false;
            _tree.HotTracking = false;
            _tree.BorderStyle = BorderStyle.None;
            _tree.BackColor = Color.White;
            _tree.Indent = 16;
            _tree.ItemHeight = TreeRowHeight;
            _tree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
            _tree.DrawNode += TreeDrawNode;
            _tree.MouseMove += TreeMouseMove;
            _tree.MouseLeave += delegate
            {
                if (_hotNode == null) return;
                TreeNode old = _hotNode;
                _hotNode = null;
                _tree.Invalidate(RowBounds(old));
            };
            _tree.MouseDown += TreeMouseDown;
            _tree.BeforeExpand += delegate(object sender, TreeViewCancelEventArgs e) { BeforeExpandNode(e.Node); };
            _tree.NodeMouseDoubleClick += delegate(object sender, TreeNodeMouseClickEventArgs e)
            {
                DbTableRef table = e.Node.Tag as DbTableRef;
                if (table != null) LoadTableAsync(table, true);
            };
            _tree.AfterSelect += delegate(object sender, TreeViewEventArgs e) { AfterSelectNode(e.Node); };
            _tree.NodeMouseClick += delegate(object sender, TreeNodeMouseClickEventArgs e)
            {
                if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node;
            };
            ContextMenuStrip treeMenu = new ContextMenuStrip();
            ToolStripMenuItem copyName = new ToolStripMenuItem("复制名称");
            copyName.Click += delegate { CopyTreeNodeName(); };
            treeMenu.Items.Add(copyName);
            treeMenu.Opening += delegate { copyName.Enabled = _tree.SelectedNode != null; };
            _tree.ContextMenuStrip = treeMenu;
            left.Controls.Add(_tree, 0, 2);

            _treeHint = new Label();
            _treeHint.Dock = DockStyle.Fill;
            _treeHint.ForeColor = AppTheme.FaintColor;
            _treeHint.Text = "双击表查看数据；单击库切换默认库。";
            _treeHint.TextAlign = ContentAlignment.MiddleLeft;
            left.Controls.Add(_treeHint, 0, 3);

            SplitContainer rightSplit = new SplitContainer();
            rightSplit.Size = new Size(700, 700);
            rightSplit.Dock = DockStyle.Fill;
            rightSplit.Orientation = Orientation.Horizontal;
            rightSplit.SplitterWidth = 6;
            rightSplit.Panel1MinSize = 170;
            rightSplit.Panel2MinSize = 200;
            rightSplit.BackColor = AppTheme.LineColor;
            rightSplit.SplitterDistance = 230;
            _outer.Panel2.Controls.Add(rightSplit);

            TableLayoutPanel sqlPanel = new TableLayoutPanel();
            sqlPanel.Dock = DockStyle.Fill;
            sqlPanel.ColumnCount = 1;
            sqlPanel.RowCount = 3;
            sqlPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            sqlPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sqlPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            sqlPanel.Padding = new Padding(4, 4, 4, 0);
            sqlPanel.BackColor = Color.White;
            rightSplit.Panel1.Controls.Add(sqlPanel);

            TableLayoutPanel sqlToolbar = new TableLayoutPanel();
            sqlToolbar.Dock = DockStyle.Fill;
            sqlToolbar.ColumnCount = 9;
            sqlToolbar.RowCount = 1;
            sqlToolbar.Margin = new Padding(0);
            sqlToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            sqlToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            sqlToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            sqlToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            sqlToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            sqlToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            sqlToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sqlToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            sqlToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 4));
            sqlPanel.Controls.Add(sqlToolbar, 0, 0);

            Label dbLabel = new Label();
            dbLabel.Text = "数据库";
            dbLabel.Dock = DockStyle.Fill;
            dbLabel.TextAlign = ContentAlignment.MiddleLeft;
            dbLabel.ForeColor = AppTheme.LabelColor;
            sqlToolbar.Controls.Add(dbLabel, 0, 0);

            _dbCombo = new ComboBox();
            _dbCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _dbCombo.Dock = DockStyle.Fill;
            _dbCombo.Margin = new Padding(0, 5, 8, 5);
            _dbCombo.SelectedIndexChanged += delegate { OnDatabaseComboChanged(); };
            sqlToolbar.Controls.Add(_dbCombo, 1, 0);

            _executeButton = AppTheme.MakeSmallButton("执行 F5", 74);
            _executeButton.Dock = DockStyle.Fill;
            _executeButton.Margin = new Padding(0, 4, 6, 4);
            _executeButton.Click += delegate { ExecuteSql(); };
            sqlToolbar.Controls.Add(_executeButton, 2, 0);

            _cancelButton = AppTheme.MakeSmallButton("取消", 52);
            _cancelButton.Dock = DockStyle.Fill;
            _cancelButton.Margin = new Padding(0, 4, 6, 4);
            _cancelButton.Enabled = false;
            _cancelButton.Click += delegate { CancelQuery(); };
            sqlToolbar.Controls.Add(_cancelButton, 3, 0);

            _explainButton = AppTheme.MakeSmallButton("解释", 52);
            _explainButton.Dock = DockStyle.Fill;
            _explainButton.Margin = new Padding(0, 4, 6, 4);
            _explainButton.Click += delegate { ExplainSql(); };
            sqlToolbar.Controls.Add(_explainButton, 4, 0);

            Button formatButton = AppTheme.MakeSmallButton("格式化", 56);
            formatButton.Dock = DockStyle.Fill;
            formatButton.Margin = new Padding(0, 4, 6, 4);
            formatButton.Click += delegate { FormatSqlEditor(); };
            sqlToolbar.Controls.Add(formatButton, 5, 0);

            _historyCombo = new ComboBox();
            _historyCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _historyCombo.Dock = DockStyle.Fill;
            _historyCombo.Margin = new Padding(0, 5, 8, 5);
            _historyCombo.Items.Add("历史 SQL…");
            _historyCombo.SelectedIndex = 0;
            _historyCombo.SelectedIndexChanged += delegate { OnHistorySelected(); };
            sqlToolbar.Controls.Add(_historyCombo, 6, 0);

            _aiButton = AppTheme.MakeSmallButton("AI 分析", 66);
            _aiButton.Dock = DockStyle.Fill;
            _aiButton.Margin = new Padding(0, 4, 0, 4);
            _aiButton.Click += delegate { AnalyzeWithAi(); };
            sqlToolbar.Controls.Add(_aiButton, 7, 0);

            _sqlEditor = new SqlHighlightBox();
            _sqlEditor.Dock = DockStyle.Fill;
            // 自动换行：AI 生成的宽查询不再需要横向拖着看
            _sqlEditor.WordWrap = true;
            _sqlEditor.Text = "select 1 as ping;";
            _sqlEditor.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    ExecuteSql();
                    return;
                }
                if (e.Control && e.Shift && e.KeyCode == Keys.F)
                {
                    e.SuppressKeyPress = true;
                    FormatSqlEditor();
                }
            };
            sqlPanel.Controls.Add(AppTheme.WrapCodeBox(_sqlEditor, Color.White), 0, 1);

            // 自然语言查询：不会写 SQL 时直接描述要查什么，AI 按当前库表结构生成只读 SQL 并执行。
            TableLayoutPanel aiRow = new TableLayoutPanel();
            aiRow.Dock = DockStyle.Fill;
            aiRow.ColumnCount = 3;
            aiRow.RowCount = 1;
            aiRow.Margin = new Padding(0);
            aiRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            aiRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            aiRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));

            Label aiQueryLabel = new Label();
            aiQueryLabel.Text = "AI 查询";
            aiQueryLabel.Dock = DockStyle.Fill;
            aiQueryLabel.TextAlign = ContentAlignment.MiddleLeft;
            aiQueryLabel.ForeColor = AppTheme.PrimaryColor;
            aiQueryLabel.Font = AppTheme.UiFont(9F, FontStyle.Bold);
            aiRow.Controls.Add(aiQueryLabel, 0, 0);

            _aiQuestion = new TextBox();
            _aiQuestion.Dock = DockStyle.Fill;
            _aiQuestion.BorderStyle = BorderStyle.FixedSingle;
            _aiQuestion.Margin = new Padding(0, 8, 8, 8);
            _aiQuestion.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    RunAiQuery();
                }
            };
            aiRow.Controls.Add(_aiQuestion, 1, 0);

            _aiQueryButton = AppTheme.MakeSmallButton("AI 生成并执行", 104);
            _aiQueryButton.Dock = DockStyle.Fill;
            _aiQueryButton.Margin = new Padding(0, 6, 0, 6);
            _aiQueryButton.Click += delegate { RunAiQuery(); };
            aiRow.Controls.Add(_aiQueryButton, 2, 0);
            sqlPanel.Controls.Add(aiRow, 0, 2);

            _aiTip = new ToolTip();
            _aiTip.SetToolTip(_aiQuestion,
                "用中文描述你想查什么，例如：最近 7 天每个应用有多少条记录\r\n回车直接生成并执行");
            _aiTip.SetToolTip(_aiQueryButton,
                "AI 读取当前库的表名、字段名和注释，生成一条只读 SQL 后直接执行");

            _resultTabs = new TabControl();
            _resultTabs.Dock = DockStyle.Fill;
            _resultTabs.Padding = new Point(12, 4);
            rightSplit.Panel2.Controls.Add(_resultTabs);

            TabPage queryTab = new TabPage("查询结果");
            queryTab.BackColor = Color.White;
            TableLayoutPanel queryLayout = new TableLayoutPanel();
            queryLayout.Dock = DockStyle.Fill;
            queryLayout.ColumnCount = 1;
            queryLayout.RowCount = 3;
            queryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            queryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            queryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            queryLayout.Padding = new Padding(4);
            queryTab.Controls.Add(queryLayout);

            FlowLayoutPanel queryToolbar = new FlowLayoutPanel();
            queryToolbar.Dock = DockStyle.Fill;
            queryToolbar.WrapContents = false;
            queryToolbar.Margin = new Padding(0);
            _queryCopyButton = AppTheme.MakeSmallButton("复制结果", 80);
            _queryCopyButton.Enabled = false;
            _queryCopyButton.Click += delegate { CopyQueryResult(); };
            queryToolbar.Controls.Add(_queryCopyButton);
            _queryExportButton = AppTheme.MakeSmallButton("导出 CSV", 80);
            _queryExportButton.Enabled = false;
            _queryExportButton.Click += delegate { ExportQueryCsv(); };
            queryToolbar.Controls.Add(_queryExportButton);
            Label queryHint = new Label();
            queryHint.Text = "默认最多 " + MaxResultRows + " 行；Ctrl+Enter 执行，Esc 取消。";
            queryHint.AutoSize = true;
            queryHint.ForeColor = AppTheme.FaintColor;
            queryHint.Margin = new Padding(8, 6, 0, 0);
            queryToolbar.Controls.Add(queryHint);
            queryLayout.Controls.Add(queryToolbar, 0, 0);

            _queryGrid = AppTheme.MakeGrid();
            _queryGrid.ReadOnly = true;
            _queryGrid.AutoGenerateColumns = true;
            _queryGrid.ColumnAdded += delegate(object sender, DataGridViewColumnEventArgs e)
            {
                e.Column.SortMode = DataGridViewColumnSortMode.NotSortable;
            };
            DataGridExport.AttachGridMenu(_queryGrid, new Action<string, Color>(Status));
            _queryGrid.CellPainting += PaintRowHeaderCell;
            queryLayout.Controls.Add(_queryGrid, 0, 1);

            _queryMessage = new Label();
            _queryMessage.Dock = DockStyle.Fill;
            _queryMessage.TextAlign = ContentAlignment.MiddleLeft;
            _queryMessage.ForeColor = AppTheme.MutedColor;
            queryLayout.Controls.Add(_queryMessage, 0, 2);
            _resultTabs.TabPages.Add(queryTab);

            _tableTab = new TabPage("表数据");
            _tableTab.BackColor = Color.White;
            TableLayoutPanel tableLayout = new TableLayoutPanel();
            tableLayout.Dock = DockStyle.Fill;
            tableLayout.ColumnCount = 1;
            tableLayout.RowCount = 3;
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            tableLayout.Padding = new Padding(4);
            _tableTab.Controls.Add(tableLayout);

            TableLayoutPanel tableToolbar = new TableLayoutPanel();
            tableToolbar.Dock = DockStyle.Fill;
            tableToolbar.ColumnCount = 9;
            tableToolbar.RowCount = 1;
            tableToolbar.Margin = new Padding(0);
            tableToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tableToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            tableToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            tableToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            tableToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            tableToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            tableToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            tableToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            tableToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 4));
            tableLayout.Controls.Add(tableToolbar, 0, 0);

            _tableNameLabel = new Label();
            _tableNameLabel.Dock = DockStyle.Fill;
            _tableNameLabel.TextAlign = ContentAlignment.MiddleLeft;
            _tableNameLabel.ForeColor = AppTheme.TitleColor;
            _tableNameLabel.Font = AppTheme.UiFont(9.5F, FontStyle.Bold);
            _tableNameLabel.Text = "未选择表";
            tableToolbar.Controls.Add(_tableNameLabel, 0, 0);

            _addRowButton = AppTheme.MakeSmallButton("新增行", 62);
            _addRowButton.Dock = DockStyle.Fill;
            _addRowButton.Margin = new Padding(0, 4, 4, 4);
            _addRowButton.Click += delegate { AddTableRow(); };
            tableToolbar.Controls.Add(_addRowButton, 1, 0);

            _deleteRowButton = AppTheme.MakeSmallButton("删除行", 62);
            _deleteRowButton.Dock = DockStyle.Fill;
            _deleteRowButton.Margin = new Padding(0, 4, 4, 4);
            _deleteRowButton.Click += delegate { DeleteTableRows(); };
            tableToolbar.Controls.Add(_deleteRowButton, 2, 0);

            _saveButton = AppTheme.MakeSmallButton("保存更改", 74);
            _saveButton.Dock = DockStyle.Fill;
            _saveButton.Margin = new Padding(0, 4, 4, 4);
            _saveButton.Click += delegate { SaveTable(); };
            tableToolbar.Controls.Add(_saveButton, 3, 0);

            _discardButton = AppTheme.MakeSmallButton("放弃更改", 74);
            _discardButton.Dock = DockStyle.Fill;
            _discardButton.Margin = new Padding(0, 4, 4, 4);
            _discardButton.Click += delegate { DiscardTableChanges(); };
            tableToolbar.Controls.Add(_discardButton, 4, 0);

            Button reloadButton = AppTheme.MakeSmallButton("刷新", 62);
            reloadButton.Dock = DockStyle.Fill;
            reloadButton.Margin = new Padding(0, 4, 4, 4);
            reloadButton.Click += delegate { ReloadCurrentTable(); };
            tableToolbar.Controls.Add(reloadButton, 5, 0);

            _tableCopyButton = AppTheme.MakeSmallButton("复制数据", 76);
            _tableCopyButton.Dock = DockStyle.Fill;
            _tableCopyButton.Margin = new Padding(0, 4, 4, 4);
            _tableCopyButton.Enabled = false;
            _tableCopyButton.Click += delegate { CopyTableData(); };
            tableToolbar.Controls.Add(_tableCopyButton, 6, 0);

            Button tableExport = AppTheme.MakeSmallButton("导出 CSV", 76);
            tableExport.Dock = DockStyle.Fill;
            tableExport.Margin = new Padding(0, 4, 0, 4);
            tableExport.Click += delegate { ExportTableCsv(); };
            tableToolbar.Controls.Add(tableExport, 7, 0);

            _tableGrid = AppTheme.MakeGrid();
            _tableGrid.AutoGenerateColumns = true;
            _tableGrid.ReadOnly = true;
            _tableGrid.AllowUserToAddRows = false;
            _tableGrid.AllowUserToDeleteRows = false;
            _tableGrid.ColumnAdded += delegate(object sender, DataGridViewColumnEventArgs e)
            {
                e.Column.SortMode = DataGridViewColumnSortMode.NotSortable;
            };
            DataGridExport.AttachGridMenu(_tableGrid, new Action<string, Color>(Status));
            AddNullMenu(_tableGrid);
            _tableGrid.CellPainting += PaintRowHeaderCell;
            tableLayout.Controls.Add(_tableGrid, 0, 1);

            _tableMessage = new Label();
            _tableMessage.Dock = DockStyle.Fill;
            _tableMessage.TextAlign = ContentAlignment.MiddleLeft;
            _tableMessage.ForeColor = AppTheme.MutedColor;
            tableLayout.Controls.Add(_tableMessage, 0, 2);
            _resultTabs.TabPages.Add(_tableTab);

            TabPage schemaTab = new TabPage("表结构");
            schemaTab.BackColor = Color.White;

            // 表结构只要字段清单，直接铺满整页；建表 SQL 仍然会读，留给 AI 生成查询用。
            _schemaGrid = AppTheme.MakeGrid();
            _schemaGrid.ReadOnly = true;
            _schemaGrid.AutoGenerateColumns = true;
            _schemaGrid.RowHeadersVisible = false;
            _schemaGrid.Font = AppTheme.UiFont(9F);
            _schemaGrid.RowTemplate.Height = 28;
            _schemaGrid.ColumnAdded += delegate(object sender, DataGridViewColumnEventArgs e)
            {
                e.Column.SortMode = DataGridViewColumnSortMode.NotSortable;
            };
            DataGridExport.AttachGridMenu(_schemaGrid, new Action<string, Color>(Status));
            schemaTab.Controls.Add(_schemaGrid);

            _schemaSummary = new Label();
            _schemaSummary.Dock = DockStyle.Top;
            _schemaSummary.Height = 32;
            _schemaSummary.Padding = new Padding(10, 0, 8, 0);
            _schemaSummary.TextAlign = ContentAlignment.MiddleLeft;
            _schemaSummary.BackColor = Color.White;
            _schemaSummary.ForeColor = AppTheme.LabelColor;
            _schemaSummary.Font = AppTheme.UiFont(9F);
            _schemaSummary.Text = "未选择表";
            schemaTab.Controls.Add(_schemaSummary);
            _resultTabs.TabPages.Add(schemaTab);

            TabPage messageTab = new TabPage("消息");
            messageTab.BackColor = Color.White;
            _messages = AppTheme.MakeTextBox(true, true, AppTheme.MonoFont(9.5F));
            _messages.ForeColor = AppTheme.CodeText;
            messageTab.Controls.Add(AppTheme.WrapCodeBox(_messages, _messages.BackColor));
            _resultTabs.TabPages.Add(messageTab);

            UpdateTableButtons(false);
            LoadDatabasesAsync();
        }

        public Control View
        {
            get { return this; }
        }

        private void AddNullMenu(DataGridView grid)
        {
            ContextMenuStrip menu = grid.ContextMenuStrip;
            if (menu == null) return;
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem item = new ToolStripMenuItem("将选中单元格设为 NULL");
            item.Click += delegate { SetSelectedCellsNull(); };
            menu.Items.Add(item);
        }

        // 左侧对象树右键：复制库名 / 表名（表节点优先用 db.table 全名）。
        private void CopyTreeNodeName()
        {
            TreeNode node = _tree.SelectedNode;
            if (node == null) return;
            DbTableRef table = node.Tag as DbTableRef;
            string text = table != null
                ? table.FullName
                : (node.Tag as string ?? node.Text);
            try
            {
                Clipboard.SetText(text);
                Status("已复制：" + text, AppTheme.OkColor);
            }
            catch (Exception ex)
            {
                Status("复制失败：" + ex.Message, AppTheme.ErrorColor);
            }
        }

        // 左侧行号列：数据行显示序号，表格自动追加的新行显示「新增」。
        // 默认这里只是新行的一个星号，看起来像一列坏掉的空白。
        private static void PaintRowHeaderCell(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != -1) return;
            DataGridView grid = (DataGridView)sender;
            e.PaintBackground(e.CellBounds, true);
            bool isNewRow = grid.Rows[e.RowIndex].IsNewRow;
            string text = isNewRow ? "新增" : (e.RowIndex + 1).ToString();
            Color color = isNewRow ? AppTheme.PrimaryColor : AppTheme.MutedColor;
            TextRenderer.DrawText(e.Graphics, text, AppTheme.UiFont(8F), e.CellBounds, color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter |
                TextFormatFlags.NoPrefix);
            e.Handled = true;
        }

        private void SetSelectedCellsNull()
        {
            if (_tableData == null || !IsTableEditable())
            {
                Status("当前表不可编辑。", AppTheme.WarnColor);
                return;
            }
            try
            {
                _tableGrid.EndEdit();
                foreach (DataGridViewCell cell in _tableGrid.SelectedCells)
                {
                    if (cell.RowIndex < 0 || cell.ColumnIndex < 0) continue;
                    if (cell.RowIndex >= _tableData.Data.Rows.Count) continue;
                    _tableData.Data.Rows[cell.RowIndex][cell.ColumnIndex] = DBNull.Value;
                }
                _tableMessage.Text = "已把选中单元格设为 NULL（尚未保存）。";
                _tableMessage.ForeColor = AppTheme.WarnColor;
            }
            catch (Exception ex)
            {
                Status("设为 NULL 失败：" + ex.Message, AppTheme.ErrorColor);
            }
        }

        // ---------------- 左侧库表树 ----------------

        private void LoadDatabasesAsync()
        {
            string error = "";
            string version = "";
            List<string> databases = null;
            AsyncUi.Run(this, delegate
            {
                databases = _session.GetDatabases(out error);
                version = _session.ServerVersion;
            },
            delegate(Exception ex)
            {
                if (IsDisposed) return;
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    Status("读取数据库列表失败：" + error, AppTheme.ErrorColor);
                    _tree.Nodes.Clear();
                    TreeNode failed = new TreeNode("读取失败：" + error);
                    failed.ForeColor = AppTheme.ErrorColor;
                    _tree.Nodes.Add(failed);
                    return;
                }

                _tree.Nodes.Clear();
                for (int i = 0; i < databases.Count; i++)
                {
                    TreeNode node = new TreeNode(databases[i]);
                    node.Tag = databases[i];
                    node.ToolTipText = databases[i];
                    node.Nodes.Add(MakeTableGroupNode(databases[i]));
                    _tree.Nodes.Add(node);
                }

                _loadingDbCombo = true;
                _dbCombo.Items.Clear();
                for (int i = 0; i < databases.Count; i++) _dbCombo.Items.Add(databases[i]);
                string select = !string.IsNullOrEmpty(_currentDatabase)
                    ? _currentDatabase
                    : _profile.Option("lastDatabase", _profile.Database);
                if (!string.IsNullOrEmpty(select) && _dbCombo.Items.Contains(select))
                {
                    _dbCombo.SelectedItem = select;
                    _currentDatabase = select;
                }
                else if (_dbCombo.Items.Count > 0)
                {
                    _dbCombo.SelectedIndex = 0;
                    _currentDatabase = Convert.ToString(_dbCombo.SelectedItem);
                }
                _loadingDbCombo = false;

                string initialDatabase = Convert.ToString(_dbCombo.SelectedItem);
                if (!string.IsNullOrEmpty(initialDatabase))
                {
                    UseDatabase(initialDatabase);
                    // 默认把当前库展开，露出「表」分组；表列表要等分组展开时才加载。
                    TreeNode current = FindDatabaseNode(initialDatabase);
                    if (current != null) current.Expand();
                }

                _treeHint.Text = databases.Count + " 个库 · " +
                    (_profile.AllowWrites ? "可写" : "只读") +
                    (string.IsNullOrEmpty(version) ? "" : " · " + version);
                AppendMessage("已连接 " + _profile.Endpoint + (string.IsNullOrEmpty(version) ? "" : "，服务端版本 " + version));
            });
        }

        private static TreeNode MakeTableGroupNode(string database)
        {
            TableGroupTag tag = new TableGroupTag();
            tag.Database = database;
            TreeNode node = new TreeNode("表");
            node.Tag = tag;
            TreeNode placeholder = new TreeNode("加载中…");
            placeholder.Tag = PlaceholderTag;
            node.Nodes.Add(placeholder);
            return node;
        }

        private TreeNode FindDatabaseNode(string database)
        {
            for (int i = 0; i < _tree.Nodes.Count; i++)
            {
                if (string.Equals(_tree.Nodes[i].Tag as string, database, StringComparison.OrdinalIgnoreCase))
                    return _tree.Nodes[i];
            }
            return null;
        }

        private void BeforeExpandNode(TreeNode node)
        {
            if (node == null) return;
            TableGroupTag group = node.Tag as TableGroupTag;
            if (group == null || group.Loading || group.Loaded) return;
            group.Loading = true;
            LoadTablesForGroup(node, group);
        }

        private void LoadTablesForGroup(TreeNode node, TableGroupTag group)
        {
            string database = group.Database;
            List<DbTableRef> tables = null;
            string error = "";
            AsyncUi.Run(this, delegate
            {
                tables = _session.GetTables(database, out error);
            },
            delegate(Exception ex)
            {
                group.Loading = false;
                if (IsDisposed) return;
                if (ex != null && error.Length == 0) error = ex.Message;
                node.Nodes.Clear();
                if (error.Length > 0)
                {
                    TreeNode failed = new TreeNode("加载失败：" + error);
                    failed.ForeColor = AppTheme.ErrorColor;
                    node.Nodes.Add(failed);
                    Status(error, AppTheme.ErrorColor);
                    return;
                }
                group.Loaded = true;
                group.Count = tables.Count;
                for (int i = 0; i < tables.Count; i++)
                {
                    node.Nodes.Add(MakeTableNode(tables[i]));
                }
                if (tables.Count == 0)
                {
                    TreeNode empty = new TreeNode("（没有表）");
                    empty.ForeColor = AppTheme.FaintColor;
                    node.Nodes.Add(empty);
                }
                _tree.Invalidate(RowBounds(node));
            });
        }

        // ---------------- 左侧树的自绘（仿 DBX 的库表样式） ----------------

        private Rectangle RowBounds(TreeNode node)
        {
            Rectangle b = node.Bounds;
            return new Rectangle(0, b.Top, Math.Max(0, _tree.ClientSize.Width), b.Height);
        }

        private void TreeDrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            TreeNode node = e.Node;
            if (node == null) return;
            Graphics g = e.Graphics;
            Rectangle row = new Rectangle(0, e.Bounds.Top,
                Math.Max(0, _tree.ClientSize.Width), TreeRowHeight);

            bool selected = (e.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
            bool hot = ReferenceEquals(node, _hotNode);

            using (SolidBrush white = new SolidBrush(Color.White))
                g.FillRectangle(white, row);

            if (selected || hot)
            {
                Rectangle pill = new Rectangle(4, row.Top + 2, Math.Max(0, row.Width - 10), row.Height - 4);
                using (GraphicsPath path = RoundedRect(pill, 6))
                using (SolidBrush brush = new SolidBrush(selected ? AppTheme.RowSelect : TreeHoverBack))
                {
                    g.FillPath(brush, path);
                }
            }

            SmoothingMode oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int x = 8 + node.Level * _tree.Indent;
            int centerY = row.Top + row.Height / 2;

            // 展开箭头：自己画，隐藏原生 +/- 才能做出 DBX 那种扁平观感。
            if (node.Nodes.Count > 0)
            {
                int cx = x + 4;
                using (Pen pen = new Pen(AppTheme.MutedColor, 1.5f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    Point[] points = node.IsExpanded
                        ? new Point[] { new Point(cx - 2, centerY - 2), new Point(cx + 2, centerY + 2), new Point(cx + 6, centerY - 2) }
                        : new Point[] { new Point(cx - 1, centerY - 4), new Point(cx + 3, centerY), new Point(cx - 1, centerY + 4) };
                    g.DrawLines(pen, points);
                }
            }

            int iconX = x + 16;
            Rectangle icon = new Rectangle(iconX, centerY - 8, 16, 16);
            DbTableRef table = node.Tag as DbTableRef;
            TableGroupTag group = node.Tag as TableGroupTag;
            string database = node.Tag as string;

            string text = node.Text;
            Color textColor = AppTheme.LabelColor;

            if (database != null)
            {
                DrawDatabaseIcon(g, icon, TreeDbColor);
                textColor = AppTheme.TitleColor;
                using (SolidBrush dot = new SolidBrush(TreeDbDot))
                    g.FillEllipse(dot, row.Right - 20, centerY - 3, 6, 6);
            }
            else if (group != null)
            {
                DrawTableIcon(g, icon, Color.FromArgb(103, 194, 58));
                textColor = AppTheme.TitleColor;
            }
            else if (table != null)
            {
                DrawTableIcon(g, icon, Color.FromArgb(120, 170, 140));
            }
            else
            {
                // 占位 / 提示 / 搜索结果标题
                bool faint = ReferenceEquals(node.Tag, PlaceholderTag) ||
                    text.StartsWith("加载中", StringComparison.Ordinal) ||
                    text.StartsWith("（没有表）", StringComparison.Ordinal) ||
                    text.StartsWith("没有匹配的表", StringComparison.Ordinal);
                textColor = faint ? AppTheme.FaintColor
                    : (text.StartsWith("搜索结果：", StringComparison.Ordinal) ? AppTheme.PrimaryColor : node.ForeColor);
            }

            int textLeft = iconX + 22;
            int textRight = row.Right - 24;
            if (group != null && group.Count >= 0)
            {
                string count = group.Count.ToString();
                Size countSize = TextRenderer.MeasureText(count, TreeCountFont);
                Rectangle countRect = new Rectangle(textRight - countSize.Width, row.Top, countSize.Width, row.Height);
                TextRenderer.DrawText(g, count, TreeCountFont, countRect, AppTheme.FaintColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                textRight -= countSize.Width + 8;
            }

            Rectangle textRect = new Rectangle(textLeft, row.Top,
                Math.Max(0, textRight - textLeft), row.Height);
            if (selected) textColor = AppTheme.TitleColor;
            TextRenderer.DrawText(g, text, TreeFont, textRect, textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            g.SmoothingMode = oldMode;
        }

        private void TreeMouseMove(object sender, MouseEventArgs e)
        {
            TreeNode node = _tree.GetNodeAt(e.Location);
            if (ReferenceEquals(node, _hotNode)) return;
            TreeNode old = _hotNode;
            _hotNode = node;
            if (old != null) _tree.Invalidate(RowBounds(old));
            if (node != null) _tree.Invalidate(RowBounds(node));
        }

        private void TreeMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            TreeNode node = _tree.GetNodeAt(e.Location);
            if (node == null || node.Nodes.Count == 0) return;
            // 原生 +/- 已经隐藏，点击箭头那一小片区域时自己切换展开状态。
            int x = 8 + node.Level * _tree.Indent + 4;
            if (e.X >= x - 5 && e.X <= x + 9) node.Toggle();
        }

        private static void DrawDatabaseIcon(Graphics g, Rectangle r, Color color)
        {
            Rectangle top = new Rectangle(r.X + 1, r.Y + 2, r.Width - 2, 5);
            Rectangle bottom = new Rectangle(r.X + 1, r.Y + r.Height - 7, r.Width - 2, 5);
            using (GraphicsPath body = new GraphicsPath())
            {
                body.AddEllipse(top);
                body.AddRectangle(new Rectangle(r.X + 1, r.Y + 4, r.Width - 2, r.Height - 9));
                body.AddEllipse(bottom);
                using (SolidBrush fill = new SolidBrush(color))
                    g.FillPath(fill, body);
                using (Pen pen = new Pen(Darken(color, 0.78f), 1f))
                    g.DrawEllipse(pen, top);
            }
            using (SolidBrush light = new SolidBrush(Color.FromArgb(120, Color.White)))
                g.FillEllipse(light, new Rectangle(r.X + 3, r.Y + 3, r.Width - 8, 2));
        }

        private static void DrawTableIcon(Graphics g, Rectangle r, Color color)
        {
            Rectangle body = new Rectangle(r.X + 1, r.Y + 2, r.Width - 2, r.Height - 4);
            using (GraphicsPath path = RoundedRect(body, 2))
            {
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(247, 251, 248)))
                    g.FillPath(fill, path);
                using (Pen pen = new Pen(color, 1.2f))
                    g.DrawPath(pen, path);
            }
            using (Pen pen = new Pen(color, 1f))
            {
                g.DrawLine(pen, body.Left + 1, body.Top + 5, body.Right - 1, body.Top + 5);
                g.DrawLine(pen, body.Left + 5, body.Top + 5, body.Left + 5, body.Bottom - 1);
            }
        }

        private static Color Darken(Color color, float factor)
        {
            return Color.FromArgb(color.A,
                (int)(color.R * factor), (int)(color.G * factor), (int)(color.B * factor));
        }

        private static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (rect.Width <= d || rect.Height <= d)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static TreeNode MakeTableNode(DbTableRef table)
        {
            TreeNode node = new TreeNode(table.Table + (table.IsView ? "  [视图]" : ""));
            node.Tag = table;
            node.ToolTipText = table.FullName + (table.IsView ? "（视图）" : "");
            return node;
        }

        private void AfterSelectNode(TreeNode node)
        {
            if (node == null) return;
            DbTableRef table = node.Tag as DbTableRef;
            if (table != null)
            {
                LoadSchemaAsync(table);
                return;
            }
            string database = node.Tag as string;
            if (database != null)
            {
                _schemaToken++;
                if (_dbCombo.Items.Contains(database)) _dbCombo.SelectedItem = database;
            }
        }

        private void OnDatabaseComboChanged()
        {
            if (_loadingDbCombo) return;
            string database = Convert.ToString(_dbCombo.SelectedItem);
            if (string.IsNullOrEmpty(database) || database == _currentDatabase) return;
            UseDatabase(database);
        }

        private void UseDatabase(string database)
        {
            string error = "";
            AsyncUi.Run(this, delegate
            {
                _session.Query("USE " + MySqlSession.QuoteIdentifier(database), 0, 15, out error);
            },
            delegate(Exception ex)
            {
                if (IsDisposed) return;
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    Status("切换数据库失败：" + error, AppTheme.ErrorColor);
                    _loadingDbCombo = true;
                    if (_dbCombo.Items.Contains(_currentDatabase)) _dbCombo.SelectedItem = _currentDatabase;
                    _loadingDbCombo = false;
                    return;
                }
                _currentDatabase = database;
                _profile.SetOption("lastDatabase", database);
                if (_host != null && _host.RememberDatabase != null) _host.RememberDatabase(database);
                Status("当前数据库：" + database, AppTheme.OkColor);
            });
        }

        private void SearchTables()
        {
            string pattern = _searchBox.Text.Trim();
            if (pattern.Length == 0)
            {
                ClearSearch();
                return;
            }
            List<DbTableSearchRow> rows = new List<DbTableSearchRow>();
            string error = "";
            Status("正在搜索表：" + pattern + " …", AppTheme.PrimaryColor);
            AsyncUi.Run(this, delegate
            {
                DbQueryResult result = _session.FindTables(pattern, 300, out error);
                if (error.Length > 0) return;
                for (int i = 0; i < result.Table.Rows.Count; i++)
                {
                    DbTableSearchRow row = new DbTableSearchRow();
                    row.Database = Convert.ToString(result.Table.Rows[i][0]);
                    row.Table = Convert.ToString(result.Table.Rows[i][1]);
                    row.Type = Convert.ToString(result.Table.Rows[i][2]);
                    rows.Add(row);
                }
            },
            delegate(Exception ex)
            {
                if (IsDisposed) return;
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    Status("搜索失败：" + error, AppTheme.ErrorColor);
                    return;
                }
                RemoveSearchNode();
                TreeNode root = new TreeNode("搜索结果：" + pattern + " (" + rows.Count + ")");
                root.ForeColor = AppTheme.PrimaryColor;
                root.Tag = null;
                for (int i = 0; i < rows.Count; i++)
                {
                    DbTableRef table = new DbTableRef();
                    table.Database = rows[i].Database;
                    table.Table = rows[i].Table;
                    table.IsView = rows[i].Type.IndexOf("VIEW", StringComparison.OrdinalIgnoreCase) >= 0;
                    TreeNode node = MakeTableNode(table);
                    node.Text = rows[i].Database + "." + rows[i].Table +
                        (table.IsView ? "  [视图]" : "");
                    root.Nodes.Add(node);
                }
                if (rows.Count == 0)
                {
                    TreeNode empty = new TreeNode("没有匹配的表");
                    empty.ForeColor = AppTheme.FaintColor;
                    root.Nodes.Add(empty);
                }
                _tree.Nodes.Insert(0, root);
                root.Expand();
                Status("搜索到 " + rows.Count + " 张表。", AppTheme.OkColor);
            });
        }

        private void RemoveSearchNode()
        {
            for (int i = _tree.Nodes.Count - 1; i >= 0; i--)
            {
                string text = _tree.Nodes[i].Text;
                if (text.StartsWith("搜索结果：", StringComparison.Ordinal)) _tree.Nodes.RemoveAt(i);
            }
        }

        private void ClearSearch()
        {
            _searchBox.Text = "";
            RemoveSearchNode();
        }

        // ---------------- 查询 ----------------

        private void ExecuteSql()
        {
            if (_queryBusy) return;
            string sql = SelectedSql();
            if (sql.Length == 0)
            {
                Status("SQL 为空。", AppTheme.WarnColor);
                return;
            }
            string error;
            if (!SqlGuard.Validate(sql, _session.AllowWrites, out error))
            {
                Status(error, AppTheme.ErrorColor);
                return;
            }

            bool readOnly = SqlGuard.IsReadOnlyStatement(sql);
            if (!readOnly)
            {
                bool production = _profile.Environment == DatabaseEnvironment.Production;
                bool destructive = SqlGuard.IsDestructive(sql);
                if (production || destructive)
                {
                    string summary = "将执行 SQL：\r\n" + Shorten(sql, 1200);
                    if (!_host.ConfirmWrite(_profile, destructive ? "执行破坏性 SQL" : "执行写 SQL", summary))
                        return;
                }
            }
            RunQuery(sql, readOnly);
        }

        private string SelectedSql()
        {
            string selected = _sqlEditor.SelectedText;
            if (!string.IsNullOrEmpty(selected) && selected.Trim().Length > 0) return selected.Trim();
            return _sqlEditor.Text.Trim();
        }

        // 只调整空白：把 SELECT / FROM / WHERE / JOIN / GROUP BY … 拆到不同行。
        // 选中一段时只格式化选中的部分。
        private void FormatSqlEditor()
        {
            string selected = _sqlEditor.SelectedText;
            if (!string.IsNullOrEmpty(selected) && selected.Trim().Length > 0)
            {
                string formatted = SqlFormatter.Format(selected);
                int start = _sqlEditor.SelectionStart;
                _sqlEditor.SelectedText = formatted;
                _sqlEditor.SelectionStart = start;
                _sqlEditor.SelectionLength = formatted.Length;
            }
            else
            {
                string before = _sqlEditor.Text;
                string formatted = SqlFormatter.Format(before);
                if (string.Equals(formatted, before, StringComparison.Ordinal))
                {
                    Status("SQL 已经是格式化后的样子。", AppTheme.MutedColor);
                    return;
                }
                _sqlEditor.Text = formatted;
                _sqlEditor.SelectionStart = 0;
                _sqlEditor.SelectionLength = 0;
            }
            _sqlEditor.Focus();
        }

        private void RunQuery(string sql, bool readOnly)
        {
            DbQueryResult result = null;
            string error = "";
            SetQueryBusy(true);
            _queryGrid.DataSource = null;
            _queryCopyButton.Enabled = false;
            _queryExportButton.Enabled = false;
            _queryMessage.Text = "执行中…";
            _queryMessage.ForeColor = AppTheme.PrimaryColor;
            _resultTabs.SelectedIndex = 0;
            Status("执行 SQL …", AppTheme.PrimaryColor);
            AppendMessage((readOnly ? "[查询] " : "[写] ") + Shorten(sql, 500));

            AsyncUi.Run(this, delegate
            {
                result = _session.Query(sql, MaxResultRows, QueryTimeoutSeconds, out error);
            },
            delegate(Exception ex)
            {
                if (IsDisposed) return;
                SetQueryBusy(false);
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    _lastSql = sql;
                    _lastResult = null;
                    _lastError = error;
                    _queryMessage.Text = error;
                    _queryMessage.ForeColor = AppTheme.ErrorColor;
                    // 完整错误只在结果区显示一次；状态栏给一句简短提示，避免同一段错误重复三遍。
                    Status("查询失败，错误见下方结果区。", AppTheme.ErrorColor);
                    AppendMessage("失败：" + error);
                    return;
                }

                _lastSql = sql;
                _lastResult = result.HasResultSet ? result.Table : null;
                _lastError = "";
                if (result.HasResultSet)
                {
                    _queryGrid.DataSource = result.Table;
                    DataGridExport.AutoSizeColumns(_queryGrid, 320);
                    string message = "返回 " + result.Table.Rows.Count + " 行 · " + result.ElapsedMs + " ms";
                    if (result.Truncated) message += " · 已按 " + MaxResultRows + " 行截断";
                    _queryMessage.Text = message;
                    _queryMessage.ForeColor = result.Truncated ? AppTheme.WarnColor : AppTheme.OkColor;
                    Status("查询完成。", AppTheme.OkColor);
                    _queryCopyButton.Enabled = true;
                    _queryExportButton.Enabled = true;
                    AddHistory(sql);
                }
                else
                {
                    _queryGrid.DataSource = null;
                    _queryMessage.Text = result.Message + " · " + result.ElapsedMs + " ms";
                    _queryMessage.ForeColor = AppTheme.OkColor;
                    Status("执行完成。", AppTheme.OkColor);
                    _queryCopyButton.Enabled = false;
                    _queryExportButton.Enabled = false;
                    AddHistory(sql);
                }
            });
        }

        private void ExplainSql()
        {
            if (_queryBusy) return;
            string sql = SelectedSql();
            if (sql.Length == 0)
            {
                Status("SQL 为空。", AppTheme.WarnColor);
                return;
            }
            if (!SqlGuard.IsReadOnlyStatement(sql))
            {
                Status("解释只支持 SELECT / WITH 查询。", AppTheme.WarnColor);
                return;
            }
            string explain = "EXPLAIN " + sql;
            string error;
            if (!SqlGuard.Validate(explain, false, out error))
            {
                Status(error, AppTheme.ErrorColor);
                return;
            }
            RunQuery(explain, true);
        }

        private void CancelQuery()
        {
            if (!_queryBusy) return;
            _session.CancelCurrent();
            _queryMessage.Text = "正在取消…";
            _queryMessage.ForeColor = AppTheme.WarnColor;
            Status("正在取消查询…", AppTheme.WarnColor);
        }

        private void SetQueryBusy(bool busy)
        {
            _queryBusy = busy;
            _executeButton.Enabled = !busy && !_saveBusy && !_aiBusy;
            _explainButton.Enabled = !busy && !_saveBusy && !_aiBusy;
            _cancelButton.Enabled = busy;
            _aiButton.Enabled = !busy && !_aiBusy;
            _aiQueryButton.Enabled = !busy && !_aiBusy && !_saveBusy;
        }

        private void SetAiBusy(bool busy)
        {
            _aiBusy = busy;
            _aiQuestion.Enabled = !busy;
            _executeButton.Enabled = !busy && !_queryBusy && !_saveBusy;
            _explainButton.Enabled = !busy && !_queryBusy && !_saveBusy;
            _aiButton.Enabled = !busy && !_queryBusy;
            _aiQueryButton.Enabled = !busy && !_queryBusy && !_saveBusy;
        }

        private void AddHistory(string sql)
        {
            string text = sql.Trim();
            if (text.Length == 0) return;
            if (_history.Count == 0 || _history[0] != text) _history.Insert(0, text);
            while (_history.Count > 50) _history.RemoveAt(_history.Count - 1);

            _suppressHistory = true;
            _historyCombo.Items.Clear();
            _historyCombo.Items.Add("历史 SQL…");
            for (int i = 0; i < _history.Count; i++)
            {
                _historyCombo.Items.Add((i + 1) + ". " + Shorten(OneLine(_history[i]), 120));
            }
            _historyCombo.SelectedIndex = 0;
            _suppressHistory = false;
        }

        private void OnHistorySelected()
        {
            if (_suppressHistory) return;
            int index = _historyCombo.SelectedIndex;
            if (index <= 0 || index - 1 >= _history.Count) return;
            _sqlEditor.Text = _history[index - 1];
            _sqlEditor.Focus();
            _suppressHistory = true;
            _historyCombo.SelectedIndex = 0;
            _suppressHistory = false;
        }

        private void CopyQueryResult()
        {
            DataTable table = _queryGrid.DataSource as DataTable;
            if (table == null)
            {
                Status("当前没有查询结果。", AppTheme.MutedColor);
                return;
            }
            CopyTableToClipboard(table, "查询结果");
        }

        // 复制整张结果表（含表头，TSV 格式，可直接粘贴到 Excel / 飞书表格）。
        private void CopyTableToClipboard(DataTable table, string label)
        {
            if (table == null)
            {
                Status("当前没有可复制的内容。", AppTheme.MutedColor);
                return;
            }
            try
            {
                Clipboard.SetText(DataGridExport.ToTsv(table, true));
                if (table.Rows.Count == 0)
                    Status(label + "没有数据，已复制表头。", AppTheme.MutedColor);
                else
                    Status("已复制" + label + " " + table.Rows.Count + " 行（含表头）。", AppTheme.OkColor);
            }
            catch (Exception ex)
            {
                Status("复制失败：" + ex.Message, AppTheme.ErrorColor);
            }
        }

        private void ExportQueryCsv()
        {
            DataTable table = _queryGrid.DataSource as DataTable;
            if (table == null)
            {
                Status("当前没有查询结果。", AppTheme.MutedColor);
                return;
            }
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "导出查询结果";
                dialog.Filter = "CSV 文件|*.csv|所有文件|*.*";
                dialog.FileName = "query-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv";
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                string error;
                if (DataGridExport.SaveCsv(table, dialog.FileName, out error))
                    Status("已导出：" + dialog.FileName, AppTheme.OkColor);
                else
                    Status(error, AppTheme.ErrorColor);
            }
        }

        // ---------------- 表数据 ----------------

        private void LoadTableAsync(DbTableRef table, bool switchToTableTab)
        {
            if (table == null) return;
            if (_tableData != null && _tableData.Data != null && _tableData.Data.GetChanges() != null)
            {
                if (MessageBox.Show(FindForm(), "表 " + _currentTable.FullName + " 有未保存的修改。\r\n" +
                    "继续操作会丢失这些修改，是否继续？", "未保存的修改",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK)
                {
                    return;
                }
            }

            int token = ++_tableLoadToken;
            _currentTable = table;
            _tableData = null;
            _tableColumns = new List<DbColumnInfo>();
            _tableGrid.DataSource = null;
            _tableGrid.ReadOnly = true;
            _tableGrid.AllowUserToAddRows = false;
            _tableGrid.AllowUserToDeleteRows = false;
            UpdateTableButtons(false);
            _tableNameLabel.Text = table.FullName;
            _tableMessage.Text = "正在读取 " + table.FullName + " …";
            _tableMessage.ForeColor = AppTheme.PrimaryColor;
            if (switchToTableTab) _resultTabs.SelectedIndex = 1;

            DbTableData data = null;
            string error = "";
            string createSql = "";
            AsyncUi.Run(this, delegate
            {
                data = _session.LoadTable(table, MaxResultRows, QueryTimeoutSeconds, out error);
                if (error.Length == 0) createSql = _session.GetCreateTable(table, out error);
            },
            delegate(Exception ex)
            {
                if (IsDisposed || token != _tableLoadToken) return;
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    _tableMessage.Text = error;
                    _tableMessage.ForeColor = AppTheme.ErrorColor;
                    Status(error, AppTheme.ErrorColor);
                    AppendMessage("读取表 " + table.FullName + " 失败：" + error);
                    _tableData = null;
                    _tableColumns = new List<DbColumnInfo>();
                    UpdateTableButtons(false);
                    return;
                }

                _tableData = data;
                _tableColumns = data.Columns;
                _createSqlText = createSql;
                if (data.Data != null) data.Data.AcceptChanges();

                _tableGrid.ReadOnly = !data.Editable;
                _tableGrid.AllowUserToAddRows = data.Editable;
                _tableGrid.AllowUserToDeleteRows = data.Editable;
                _tableGrid.DataSource = data.Data;
                DataGridExport.AutoSizeColumns(_tableGrid, 300);

                _tableNameLabel.Text = table.FullName;
                string reason = data.Editable ? "可编辑（基于主键定位）"
                    : (!_profile.AllowWrites ? "只读连接"
                       : (table.IsView ? "视图不可编辑" : (data.PrimaryKeys.Count == 0 ? "没有主键，表格不可编辑" : "不可编辑")));
                _tableMessage.Text = data.Message + " · " + reason +
                    (data.Truncated ? " · 已按 " + MaxResultRows + " 行截断" : "");
                _tableMessage.ForeColor = data.Truncated ? AppTheme.WarnColor : AppTheme.MutedColor;

                FillSchemaGrid(data.Columns);
                UpdateTableButtons(true);
                Status("已读取 " + table.FullName + "：" + data.Data.Rows.Count + " 行", AppTheme.OkColor);
                AppendMessage("读取表 " + table.FullName + "：" + data.Data.Rows.Count + " 行，" + reason);

                _lastSql = "SELECT * FROM " + table.FullName + " LIMIT " + MaxResultRows;
                _lastResult = data.Data;
                _lastError = "";
            });
        }

        private void LoadSchemaAsync(DbTableRef table)
        {
            int token = ++_schemaToken;
            _currentTable = table;
            List<DbColumnInfo> columns = null;
            string createSql = "";
            string error = "";
            AsyncUi.Run(this, delegate
            {
                columns = _session.GetColumns(table.Database, table.Table, out error);
                if (error.Length == 0) createSql = _session.GetCreateTable(table, out error);
            },
            delegate(Exception ex)
            {
                if (IsDisposed || token != _schemaToken) return;
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    AppendMessage("读取表结构失败：" + error);
                    return;
                }
                _tableColumns = columns;
                _createSqlText = createSql;
                FillSchemaGrid(columns);
                _tableNameLabel.Text = table.FullName;
            });
        }

        private void FillSchemaGrid(List<DbColumnInfo> columns)
        {
            DataTable table = new DataTable();
            table.Columns.Add("序号", typeof(string));
            table.Columns.Add("列名", typeof(string));
            table.Columns.Add("类型", typeof(string));
            table.Columns.Add("可空", typeof(string));
            table.Columns.Add("键", typeof(string));
            table.Columns.Add("默认值", typeof(string));
            table.Columns.Add("额外", typeof(string));
            table.Columns.Add("注释", typeof(string));

            List<string> primaryKeys = new List<string>();
            for (int i = 0; i < columns.Count; i++)
            {
                DbColumnInfo column = columns[i];
                string key = "";
                if (column.IsPrimaryKey)
                {
                    key = "主键";
                    primaryKeys.Add(column.Name);
                }
                else if (string.Equals(column.Key, "UNI", StringComparison.OrdinalIgnoreCase))
                {
                    key = "唯一";
                }
                else if (string.Equals(column.Key, "MUL", StringComparison.OrdinalIgnoreCase))
                {
                    key = "索引";
                }

                table.Rows.Add(
                    column.Ordinal > 0 ? column.Ordinal.ToString() : (i + 1).ToString(),
                    column.Name,
                    column.ColumnType,
                    column.Nullable ? "是" : "否",
                    key,
                    column.DefaultValue.Length == 0 ? "—" : column.DefaultValue,
                    DescribeExtra(column.Extra),
                    column.Comment);
            }
            table.AcceptChanges();

            _schemaGrid.DataSource = table;
            StyleSchemaGrid();
            _schemaSummary.Text = _currentTable == null ? "未选择表"
                : _currentTable.FullName + " · " + columns.Count + " 个字段" +
                  (primaryKeys.Count == 0 ? " · 无主键" : " · 主键：" + string.Join("、", primaryKeys.ToArray()));
        }

        private static string DescribeExtra(string extra)
        {
            if (string.IsNullOrEmpty(extra)) return "";
            if (extra.IndexOf("auto_increment", StringComparison.OrdinalIgnoreCase) >= 0) return "自增";
            if (extra.IndexOf("on update CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase) >= 0)
                return "更新时自动刷新";
            if (extra.IndexOf("STORED GENERATED", StringComparison.OrdinalIgnoreCase) >= 0)
                return "存储生成列";
            if (extra.IndexOf("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase) >= 0)
                return "虚拟生成列";
            if (extra.IndexOf("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase) >= 0)
                return "表达式默认值";
            return extra;
        }

        // 列宽和字体固定下来：序号/可空/键居中，类型和默认值用等宽字体，注释占满剩余宽度。
        private void StyleSchemaGrid()
        {
            int[] widths = new int[] { 52, 180, 170, 56, 62, 140, 130 };
            for (int i = 0; i < _schemaGrid.Columns.Count; i++)
            {
                DataGridViewColumn column = _schemaGrid.Columns[i];
                if (i < widths.Length) column.Width = widths[i];
                if (i == 0 || i == 3 || i == 4)
                {
                    column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                    column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                }
                if (i == 0) column.DefaultCellStyle.ForeColor = AppTheme.FaintColor;
                if (i == 2 || i == 5 || i == 6) column.DefaultCellStyle.Font = AppTheme.MonoFont(9F);
                if (i == 1) column.DefaultCellStyle.Font = AppTheme.UiFont(9F, FontStyle.Bold);
            }
            int last = _schemaGrid.Columns.Count - 1;
            if (last >= 0)
            {
                _schemaGrid.Columns[last].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                _schemaGrid.Columns[last].MinimumWidth = 160;
            }
        }

        private bool IsTableEditable()
        {
            return _tableData != null && _tableData.Editable && !_saveBusy;
        }

        private void UpdateTableButtons(bool hasTable)
        {
            bool editable = hasTable && _tableData != null && _tableData.Editable && !_saveBusy;
            _addRowButton.Enabled = editable;
            _deleteRowButton.Enabled = editable;
            _saveButton.Enabled = editable;
            _discardButton.Enabled = hasTable && !_saveBusy;
            _tableCopyButton.Enabled = hasTable && _tableData != null && _tableData.Data != null;
            _tableNameLabel.Text = _currentTable == null ? "未选择表" : _currentTable.FullName;
        }

        private void AddTableRow()
        {
            if (!IsTableEditable()) return;
            try
            {
                _tableGrid.EndEdit();
                if (_tableGrid.Rows.Count == 0) return;
                int newRowIndex = _tableGrid.Rows.Count - 1;
                _tableGrid.CurrentCell = _tableGrid.Rows[newRowIndex].Cells[0];
                _tableGrid.BeginEdit(true);
                _tableMessage.Text = "已新增一行（尚未保存）。";
                _tableMessage.ForeColor = AppTheme.WarnColor;
            }
            catch (Exception ex)
            {
                Status("新增行失败：" + ex.Message, AppTheme.ErrorColor);
            }
        }

        private void DeleteTableRows()
        {
            if (!IsTableEditable()) return;
            try
            {
                _tableGrid.EndEdit();
                List<DataRow> rows = new List<DataRow>();
                foreach (DataGridViewRow gridRow in _tableGrid.SelectedRows)
                {
                    if (gridRow.IsNewRow) continue;
                    if (gridRow.Index < 0 || gridRow.Index >= _tableData.Data.Rows.Count) continue;
                    DataRow row = _tableData.Data.Rows[gridRow.Index];
                    if (!rows.Contains(row)) rows.Add(row);
                }
                if (rows.Count == 0 && _tableGrid.CurrentRow != null && !_tableGrid.CurrentRow.IsNewRow &&
                    _tableGrid.CurrentRow.Index >= 0 && _tableGrid.CurrentRow.Index < _tableData.Data.Rows.Count)
                {
                    rows.Add(_tableData.Data.Rows[_tableGrid.CurrentRow.Index]);
                }
                if (rows.Count == 0)
                {
                    Status("请选择要删除的行（可按住 Ctrl 多选）。", AppTheme.WarnColor);
                    return;
                }
                if (MessageBox.Show(FindForm(), "从表 " + _currentTable.FullName + " 删除选中的 " +
                    rows.Count + " 行？\r\n保存前不会真正写库。", "删除行",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    return;
                }
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].RowState != DataRowState.Deleted) rows[i].Delete();
                }
                _tableMessage.Text = "已标记删除，" + rows.Count + " 行（尚未保存）。";
                _tableMessage.ForeColor = AppTheme.WarnColor;
            }
            catch (Exception ex)
            {
                Status("删除行失败：" + ex.Message, AppTheme.ErrorColor);
            }
        }

        private void SaveTable()
        {
            if (!IsTableEditable()) return;
            try
            {
                _tableGrid.EndEdit();
                DataTable changes = _tableData.Data.GetChanges();
                if (changes == null || (changes.Rows.Count == 0))
                {
                    Status("没有需要保存的修改。", AppTheme.MutedColor);
                    return;
                }
                int added = 0, modified = 0, deleted = 0;
                for (int i = 0; i < changes.Rows.Count; i++)
                {
                    DataRowState state = changes.Rows[i].RowState;
                    if (state == DataRowState.Added) added++;
                    else if (state == DataRowState.Modified) modified++;
                    else if (state == DataRowState.Deleted) deleted++;
                }
                string summary = "表：" + _currentTable.FullName +
                    "\r\n新增 " + added + " 行，修改 " + modified + " 行，删除 " + deleted + " 行。";
                if (!_host.ConfirmWrite(_profile, "保存表数据", summary)) return;

                DbSaveResult saveResult = null;
                string error = "";
                _saveBusy = true;
                _tableGrid.Enabled = false;
                _executeButton.Enabled = false;
                _explainButton.Enabled = false;
                UpdateTableButtons(true);
                _tableMessage.Text = "正在保存…";
                _tableMessage.ForeColor = AppTheme.PrimaryColor;
                Status("正在保存表数据…", AppTheme.PrimaryColor);

                AsyncUi.Run(this, delegate
                {
                    saveResult = _session.SaveTable(_currentTable, changes, _tableColumns, out error);
                },
                delegate(Exception ex)
                {
                    if (IsDisposed) return;
                    _saveBusy = false;
                    _tableGrid.Enabled = true;
                    _executeButton.Enabled = !_queryBusy && !_aiBusy;
                    _explainButton.Enabled = !_queryBusy && !_aiBusy;
                    UpdateTableButtons(true);
                    if (ex != null && error.Length == 0) error = ex.Message;
                    if (error.Length > 0)
                    {
                        _tableMessage.Text = error;
                        _tableMessage.ForeColor = AppTheme.ErrorColor;
                        Status(error, AppTheme.ErrorColor);
                        AppendMessage("保存失败：" + error);
                        return;
                    }
                    _tableData.Data.AcceptChanges();
                    _tableMessage.Text = "保存成功，影响 " + saveResult.Affected + " 行。";
                    _tableMessage.ForeColor = AppTheme.OkColor;
                    Status("保存成功：影响 " + saveResult.Affected + " 行。", AppTheme.OkColor);
                    AppendMessage("保存 " + _currentTable.FullName + "：影响 " + saveResult.Affected + " 行");
                    ReloadCurrentTable();
                });
            }
            catch (Exception ex)
            {
                _saveBusy = false;
                _tableGrid.Enabled = true;
                _executeButton.Enabled = !_queryBusy;
                _explainButton.Enabled = !_queryBusy;
                UpdateTableButtons(true);
                Status("保存失败：" + ex.Message, AppTheme.ErrorColor);
            }
        }

        private void DiscardTableChanges()
        {
            if (_tableData == null || _tableData.Data == null) return;
            if (_tableData.Data.GetChanges() != null)
            {
                if (MessageBox.Show(FindForm(), "放弃表 " + _currentTable.FullName + " 的未保存修改？",
                    "放弃更改", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK)
                {
                    return;
                }
            }
            ReloadCurrentTable();
        }

        private void ReloadCurrentTable()
        {
            if (_currentTable == null) return;
            _tableLoadToken++;
            _tableData = null;
            LoadTableAsync(_currentTable, false);
        }

        private void CopyTableData()
        {
            DataTable table = _tableGrid.DataSource as DataTable;
            if (table == null)
            {
                Status("当前没有表数据。", AppTheme.MutedColor);
                return;
            }
            CopyTableToClipboard(table, "表数据");
        }

        private void ExportTableCsv()
        {
            DataTable table = _tableGrid.DataSource as DataTable;
            if (table == null)
            {
                Status("当前没有表数据。", AppTheme.MutedColor);
                return;
            }
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "导出表数据";
                dialog.Filter = "CSV 文件|*.csv|所有文件|*.*";
                dialog.FileName = (_currentTable == null ? "table" : _currentTable.Table) +
                    "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv";
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                string error;
                if (DataGridExport.SaveCsv(table, dialog.FileName, out error))
                    Status("已导出：" + dialog.FileName, AppTheme.OkColor);
                else
                    Status(error, AppTheme.ErrorColor);
            }
        }

        // ---------------- AI / 日志线索 ----------------

        // 用中文描述需求 → AI 按当前库表结构生成只读 SQL → 写进编辑器并直接执行。
        private void RunAiQuery()
        {
            if (_queryBusy || _aiBusy || _saveBusy) return;
            string question = _aiQuestion.Text.Trim();
            if (question.Length == 0)
            {
                Status("请先用中文描述想查什么，例如：最近 7 天每个应用有多少条记录。", AppTheme.WarnColor);
                _aiQuestion.Focus();
                return;
            }
            if (_host == null) return;

            AiOcrSettings settings = _host.LoadAiSettings == null ? null : _host.LoadAiSettings();
            if (settings == null || !settings.IsConfigured)
            {
                DialogResult answer = MessageBox.Show(FindForm(),
                    "还没有配置 AI。是否现在打开 AI 设置？", "AI 查询",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes && _host.ShowAiSettings != null) _host.ShowAiSettings();
                return;
            }

            string database = _currentDatabase;
            if (string.IsNullOrEmpty(database))
            {
                Status("请先选择数据库。", AppTheme.WarnColor);
                return;
            }

            if (!_aiSchemaConfirmed)
            {
                DialogResult confirm = MessageBox.Show(FindForm(),
                    "AI 查询会把当前库的表名、字段名和字段注释，以及你输入的问题发送给 AI。\r\n" +
                    "不会发送表里的数据行。\r\n\r\n是否继续？",
                    "AI 查询", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (confirm != DialogResult.OK) return;
                _aiSchemaConfirmed = true;
            }

            SetAiBusy(true);
            Status("AI 正在读取表结构并生成 SQL …", AppTheme.PrimaryColor);
            AppendMessage("AI 查询：" + question);

            string schema = "";
            string answerText = "";
            string error = "";
            DbTableRef selectedTable = _currentTable;
            DatabaseConnectionProfile profile = _profile;
            AsyncUi.Run(this, delegate
            {
                schema = DatabaseAiQuery.BuildSchema(_session, database, question, selectedTable, out error);
                if (error.Length > 0) return;
                string prompt = DatabaseAiQuery.BuildUserPrompt(profile, database,
                    _session.ServerVersion, schema, question);
                AiOcrRunner.TryChat(settings, DatabaseAiQuery.SystemPrompt, prompt,
                    out answerText, out error);
                if (error.Length > 0) return;

                // 模型偶尔会误判“没有相关表”或把列名写成字符串常量，按同样表结构重试一次
                string note;
                string candidate = DatabaseAiQuery.ExtractSql(answerText, out note);
                if (candidate.Length == 0 && note.StartsWith("--", StringComparison.Ordinal))
                {
                    string retryPrompt = prompt + "\n\n【提醒】当前库的表结构已经给出，请再仔细检查已有表名、字段和注释；" +
                        "只有确实没有任何可用的表时才用 -- 拒绝。请只输出一条只读 SQL。";
                    string retryAnswer;
                    string retryError;
                    AiOcrRunner.TryChat(settings, DatabaseAiQuery.SystemPrompt, retryPrompt,
                        out retryAnswer, out retryError);
                    if (retryError.Length == 0) answerText = retryAnswer;
                }
                else if (candidate.Length > 0)
                {
                    string problem;
                    if (!DatabaseAiQuery.ValidateGenerated(candidate, out problem))
                    {
                        string retryPrompt = prompt + "\n\n【上一次生成的问题】" + problem +
                            "。请重新生成一条 SQL，严格按照上述规则：列名、表名用反引号，字符串只作为条件值。";
                        string retryAnswer;
                        string retryError;
                        AiOcrRunner.TryChat(settings, DatabaseAiQuery.SystemPrompt, retryPrompt,
                            out retryAnswer, out retryError);
                        if (retryError.Length == 0) answerText = retryAnswer;
                    }
                }
            },
            delegate(Exception ex)
            {
                if (IsDisposed) return;
                SetAiBusy(false);
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    Status("AI 查询失败：" + error, AppTheme.ErrorColor);
                    AppendMessage("AI 查询失败：" + error);
                    return;
                }

                string note;
                string sql = DatabaseAiQuery.ExtractSql(answerText, out note);
                if (sql.Length == 0)
                {
                    string detail = note.Length > 0 ? note : "AI 没有返回可执行的 SQL。";
                    Status("AI 没有生成可执行的 SQL：" + Shorten(OneLine(detail), 200), AppTheme.WarnColor);
                    AppendMessage("AI 返回：" + Shorten(answerText, 2000));
                    return;
                }

                string guardError;
                if (!SqlGuard.ValidateReadOnly(sql, out guardError))
                {
                    _sqlEditor.Text = SqlFormatter.Format(sql);
                    Status("AI 生成的语句不是只读查询，已阻止执行：" + guardError, AppTheme.ErrorColor);
                    AppendMessage("AI 生成的非只读 SQL 已被拦截：" + Shorten(sql, 1000));
                    return;
                }

                _sqlEditor.Text = SqlFormatter.Format(sql);
                _sqlEditor.SelectionStart = 0;
                _sqlEditor.SelectionLength = 0;
                AppendMessage("AI 生成 SQL：" + Shorten(OneLine(sql), 1000));
                Status("AI 已生成只读 SQL，开始执行…", AppTheme.PrimaryColor);
                RunQuery(sql, true);
            });
        }

        // 拼给模型的库表结构：一次 information_schema 查询拿全部列和注释，
        // 按“问题里提到的表 / 当前选中的表”优先排序，并限制总长度。
        private void AnalyzeWithAi()
        {
            if (_host == null) return;
            List<LogClue> clues = new List<LogClue>();
            if (!string.IsNullOrEmpty(_lastError))
            {
                LogClue clue = new LogClue();
                clue.Label = "错误";
                clue.Value = _lastError;
                clues.Add(clue);
            }
            string schema = BuildSchemaSummary();
            DatabaseAi.AnalyzeMySql(_host, _profile, _lastSql, _lastResult, _lastError,
                _currentTable, schema, clues);
        }

        private string BuildSchemaSummary()
        {
            if (_tableColumns == null || _tableColumns.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            sb.Append("表 ").Append(_currentTable == null ? "" : _currentTable.FullName).Append("\r\n");
            for (int i = 0; i < _tableColumns.Count; i++)
            {
                sb.Append(_tableColumns[i].Summary).Append("\r\n");
            }
            if (_createSqlText.Length > 0 && _createSqlText.Length <= 8000)
            {
                sb.Append("\r\n建表语句：\r\n").Append(_createSqlText);
            }
            return sb.ToString();
        }

        public void ApplyLogClue(LogClue clue)
        {
            if (clue == null || string.IsNullOrEmpty(clue.Value)) return;
            _searchBox.Text = clue.Value.Trim();
            SearchTables();
            Status("已按" + clue.Label + "在表名中搜索：" + clue.Value, AppTheme.PrimaryColor);
        }

        // ---------------- 快捷键 / 生命周期 ----------------

        public bool HandleShortcut(Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter) || keyData == Keys.F5)
            {
                ExecuteSql();
                return true;
            }
            if (keyData == (Keys.Control | Keys.S))
            {
                if (_resultTabs.SelectedTab == _tableTab) SaveTable();
                return true;
            }
            if (keyData == (Keys.Control | Keys.F))
            {
                _searchBox.Focus();
                _searchBox.SelectAll();
                return true;
            }
            if (keyData == (Keys.Control | Keys.L))
            {
                _sqlEditor.Focus();
                _sqlEditor.SelectAll();
                return true;
            }
            if (keyData == Keys.Escape && _queryBusy)
            {
                CancelQuery();
                return true;
            }
            return false;
        }

        public bool ConfirmClose()
        {
            if (_tableData != null && _tableData.Data != null && _tableData.Data.GetChanges() != null)
            {
                if (MessageBox.Show(FindForm(), "表 " + _currentTable.FullName +
                    " 有未保存的修改。\r\n关闭会丢弃这些修改，是否继续？",
                    "未保存的修改", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK)
                {
                    return false;
                }
            }
            if (_queryBusy || _saveBusy)
            {
                if (MessageBox.Show(FindForm(), "数据库操作仍在进行中，确定关闭？", "关闭",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK)
                {
                    return false;
                }
                _session.CancelCurrent();
            }
            return true;
        }

        public void Close()
        {
            _session.Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _session.Dispose(); }
                catch (Exception) { }
            }
            base.Dispose(disposing);
        }

        private void Status(string text, Color color)
        {
            if (_host != null) _host.Status(text, color);
        }

        private void AppendMessage(string text)
        {
            if (_messages == null || _messages.IsDisposed) return;
            _messages.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + "\r\n");
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string value = text.Trim();
            if (value.Length <= max) return value;
            return value.Substring(0, max) + "…";
        }

        private static string OneLine(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        }
    }
}
