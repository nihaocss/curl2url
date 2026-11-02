using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Curl2Url
{
    // Redis 工作区：SCAN 键浏览 + 值查看 + 命令控制台。
    // Cluster 的槽位路由、MOVED/ASK、各主节点 SCAN 都在 RedisClient 里处理。
    internal sealed class RedisWorkspace : UserControl, IDatabaseWorkspace
    {
        private const string DefaultPattern = "*";

        private readonly DatabaseTabHost _host;
        private readonly DatabaseConnectionProfile _profile;
        private readonly RedisClient _client;

        private readonly Label _headerLabel;
        private readonly TextBox _patternBox;
        private readonly Button _scanButton;
        private readonly Button _continueButton;
        private readonly Label _keyCountLabel;
        private readonly ListView _keyList;
        private readonly Label _listHint;

        private Label _keyNameLabel;
        private Label _keyMetaLabel;
        private Button _refreshButton;
        private Button _editValueButton;
        private Button _ttlButton;
        private Button _renameButton;
        private Button _deleteButton;
        private Button _aiButton;
        private DataGridView _valueGrid;
        private TextBox _stringView;
        private Label _valueMessage;

        private TextBox _commandBox;
        private Button _commandExecuteButton;
        private Button _commandAiButton;
        private TextBox _outputBox;

        private readonly HashSet<string> _listedKeys = new HashSet<string>(StringComparer.Ordinal);
        private RedisServerSummary _summary;
        private RedisScanState _scanState;
        private RedisValueData _currentValue;
        private string _currentKey = "";
        private string _valueError = "";
        private string _lastCommandLine = "";
        private string _lastCommandError = "";
        private bool _initialized;
        private bool _busy;
        private int _valueToken;

        public RedisWorkspace(DatabaseTabHost host, DatabaseConnectionProfile profile, RedisClient client)
        {
            _host = host;
            _profile = profile;
            _client = client;
            BackColor = Color.White;
            Font = AppTheme.UiFont(9F);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Padding = new Padding(0);
            Controls.Add(root);

            _headerLabel = new Label();
            _headerLabel.Dock = DockStyle.Fill;
            _headerLabel.TextAlign = ContentAlignment.MiddleLeft;
            _headerLabel.Padding = new Padding(10, 0, 0, 0);
            _headerLabel.ForeColor = _profile.Environment == DatabaseEnvironment.Production
                ? AppTheme.ErrorColor : AppTheme.MutedColor;
            _headerLabel.Text = "Redis · 正在读取服务器信息…";
            root.Controls.Add(_headerLabel, 0, 0);

            SplitContainer outer = new SplitContainer();
            outer.Size = new Size(1000, 700);
            outer.Dock = DockStyle.Fill;
            outer.Orientation = Orientation.Vertical;
            outer.SplitterWidth = 6;
            outer.Panel1MinSize = 250;
            outer.Panel2MinSize = 420;
            outer.SplitterDistance = 340;
            outer.BackColor = AppTheme.LineColor;
            root.Controls.Add(outer, 0, 1);

            TableLayoutPanel left = new TableLayoutPanel();
            left.Dock = DockStyle.Fill;
            left.ColumnCount = 1;
            left.RowCount = 5;
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            left.Padding = new Padding(8, 6, 4, 4);
            left.BackColor = Color.White;
            outer.Panel1.Controls.Add(left);

            Label keysTitle = new Label();
            keysTitle.Text = "Key 浏览";
            keysTitle.Dock = DockStyle.Fill;
            keysTitle.TextAlign = ContentAlignment.MiddleLeft;
            keysTitle.Font = AppTheme.UiFont(9.5F, FontStyle.Bold);
            keysTitle.ForeColor = AppTheme.TitleColor;
            left.Controls.Add(keysTitle, 0, 0);

            TableLayoutPanel patternRow = new TableLayoutPanel();
            patternRow.Dock = DockStyle.Fill;
            patternRow.ColumnCount = 3;
            patternRow.RowCount = 1;
            patternRow.Margin = new Padding(0);
            patternRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
            patternRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            patternRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            Label patternLabel = new Label();
            patternLabel.Text = "匹配";
            patternLabel.Dock = DockStyle.Fill;
            patternLabel.TextAlign = ContentAlignment.MiddleLeft;
            patternLabel.ForeColor = AppTheme.LabelColor;
            patternRow.Controls.Add(patternLabel, 0, 0);
            _patternBox = new TextBox();
            _patternBox.Dock = DockStyle.Fill;
            _patternBox.Margin = new Padding(0, 4, 4, 4);
            _patternBox.BorderStyle = BorderStyle.FixedSingle;
            _patternBox.Text = DefaultPattern;
            _patternBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    Scan(false);
                }
            };
            patternRow.Controls.Add(_patternBox, 1, 0);
            _scanButton = AppTheme.MakeSmallButton("扫描", 56);
            _scanButton.Dock = DockStyle.Fill;
            _scanButton.Margin = new Padding(0, 3, 0, 3);
            _scanButton.Click += delegate { Scan(false); };
            patternRow.Controls.Add(_scanButton, 2, 0);
            left.Controls.Add(patternRow, 0, 1);

            FlowLayoutPanel scanActions = new FlowLayoutPanel();
            scanActions.Dock = DockStyle.Fill;
            scanActions.WrapContents = false;
            scanActions.Margin = new Padding(0);
            _continueButton = AppTheme.MakeSmallButton("继续扫描", 74);
            _continueButton.Margin = new Padding(0, 3, 6, 3);
            _continueButton.Enabled = false;
            _continueButton.Click += delegate { Scan(true); };
            scanActions.Controls.Add(_continueButton);
            Button resetButton = AppTheme.MakeSmallButton("重新开始", 74);
            resetButton.Margin = new Padding(0, 3, 6, 3);
            resetButton.Click += delegate
            {
                _patternBox.Text = DefaultPattern;
                Scan(false);
            };
            scanActions.Controls.Add(resetButton);
            _keyCountLabel = new Label();
            _keyCountLabel.AutoSize = true;
            _keyCountLabel.Margin = new Padding(6, 8, 0, 0);
            _keyCountLabel.ForeColor = AppTheme.MutedColor;
            _keyCountLabel.Text = "尚未扫描";
            scanActions.Controls.Add(_keyCountLabel);
            left.Controls.Add(scanActions, 0, 2);

            _keyList = new ListView();
            _keyList.Dock = DockStyle.Fill;
            _keyList.View = System.Windows.Forms.View.Details;
            _keyList.FullRowSelect = true;
            _keyList.HideSelection = false;
            _keyList.MultiSelect = false;
            _keyList.ShowItemToolTips = true;
            _keyList.BorderStyle = BorderStyle.FixedSingle;
            _keyList.Columns.Add("Key", 170);
            _keyList.Columns.Add("类型", 62);
            _keyList.Columns.Add("TTL", 82);
            _keyList.Columns.Add("节点", 110);
            _keyList.DoubleClick += delegate { LoadSelectedValue(); };
            _keyList.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    LoadSelectedValue();
                }
            };
            left.Controls.Add(_keyList, 0, 3);

            _listHint = new Label();
            _listHint.Dock = DockStyle.Fill;
            _listHint.ForeColor = AppTheme.FaintColor;
            _listHint.Text = "SCAN 分页遍历，不会阻塞 Redis；双击 Key 查看值。";
            _listHint.TextAlign = ContentAlignment.MiddleLeft;
            left.Controls.Add(_listHint, 0, 4);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            outer.Panel2.Controls.Add(tabs);

            TabPage valueTab = new TabPage("Key 详情");
            valueTab.BackColor = Color.White;
            valueTab.Padding = new Padding(6);
            tabs.TabPages.Add(valueTab);
            BuildValueTab(valueTab);

            TabPage commandTab = new TabPage("命令");
            commandTab.BackColor = Color.White;
            commandTab.Padding = new Padding(6);
            tabs.TabPages.Add(commandTab);
            BuildCommandTab(commandTab);

            Load += delegate { InitializeOnce(); };
            UpdateButtons();
        }

        private void BuildValueTab(TabPage tab)
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.Padding = new Padding(0);
            tab.Controls.Add(layout);

            TableLayoutPanel info = new TableLayoutPanel();
            info.Dock = DockStyle.Fill;
            info.ColumnCount = 2;
            info.RowCount = 1;
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            _keyNameLabel = new Label();
            _keyNameLabel.Dock = DockStyle.Fill;
            _keyNameLabel.TextAlign = ContentAlignment.MiddleLeft;
            _keyNameLabel.Font = AppTheme.UiFont(9.5F, FontStyle.Bold);
            _keyNameLabel.ForeColor = AppTheme.TitleColor;
            _keyNameLabel.AutoEllipsis = true;
            _keyNameLabel.Text = "尚未选择 Key";
            info.Controls.Add(_keyNameLabel, 0, 0);
            _keyMetaLabel = new Label();
            _keyMetaLabel.Dock = DockStyle.Fill;
            _keyMetaLabel.TextAlign = ContentAlignment.MiddleRight;
            _keyMetaLabel.ForeColor = AppTheme.MutedColor;
            info.Controls.Add(_keyMetaLabel, 1, 0);
            layout.Controls.Add(info, 0, 0);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.WrapContents = false;
            actions.Margin = new Padding(0);
            actions.Padding = new Padding(0, 3, 0, 0);
            _refreshButton = AppTheme.MakeSmallButton("刷新", 58);
            _refreshButton.Enabled = false;
            _refreshButton.Click += delegate { LoadSelectedValue(); };
            actions.Controls.Add(_refreshButton);
            _editValueButton = AppTheme.MakeSmallButton("编辑值", 66);
            _editValueButton.Enabled = false;
            _editValueButton.Click += delegate { EditStringValue(); };
            actions.Controls.Add(_editValueButton);
            _ttlButton = AppTheme.MakeSmallButton("设置 TTL", 74);
            _ttlButton.Enabled = false;
            _ttlButton.Click += delegate { EditTtl(); };
            actions.Controls.Add(_ttlButton);
            _renameButton = AppTheme.MakeSmallButton("重命名", 66);
            _renameButton.Enabled = false;
            _renameButton.Click += delegate { RenameSelectedKey(); };
            actions.Controls.Add(_renameButton);
            _deleteButton = AppTheme.MakeSmallButton("删除 Key", 74);
            _deleteButton.Enabled = false;
            _deleteButton.Click += delegate { DeleteSelectedKey(); };
            actions.Controls.Add(_deleteButton);
            _aiButton = AppTheme.MakeSmallButton("AI 分析", 70);
            _aiButton.Enabled = false;
            _aiButton.Click += delegate { AnalyzeValue(); };
            actions.Controls.Add(_aiButton);
            layout.Controls.Add(actions, 0, 1);

            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Color.White;
            layout.Controls.Add(content, 0, 2);

            _valueGrid = AppTheme.MakeGrid();
            _valueGrid.ReadOnly = true;
            _valueGrid.AllowUserToAddRows = false;
            _valueGrid.AllowUserToDeleteRows = false;
            _valueGrid.RowHeadersVisible = false;
            _valueGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _valueGrid.MultiSelect = true;
            _valueGrid.Columns.Add("index", "序号");
            _valueGrid.Columns.Add("field", "字段 / 成员");
            _valueGrid.Columns.Add("value", "值");
            _valueGrid.Columns.Add("score", "分数");
            _valueGrid.Columns[0].Width = 60;
            _valueGrid.Columns[1].Width = 170;
            _valueGrid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _valueGrid.Columns[3].Width = 90;
            _valueGrid.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (!e.Control || e.KeyCode != Keys.C) return;
                if (_valueGrid.CurrentCell == null) return;
                object value = _valueGrid.CurrentCell.Value;
                if (value != null)
                {
                    try { Clipboard.SetText(Convert.ToString(value)); }
                    catch (Exception) { }
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            _valueGrid.ContextMenuStrip = BuildCopyMenu();
            content.Controls.Add(_valueGrid);

            _stringView = new TextBox();
            _stringView.Multiline = true;
            _stringView.ReadOnly = true;
            _stringView.Dock = DockStyle.Fill;
            _stringView.ScrollBars = ScrollBars.Both;
            _stringView.WordWrap = false;
            _stringView.Font = AppTheme.MonoFont(9F);
            _stringView.BackColor = Color.FromArgb(250, 250, 251);
            _stringView.BorderStyle = BorderStyle.FixedSingle;
            _stringView.Visible = false;
            content.Controls.Add(_stringView);

            _valueMessage = new Label();
            _valueMessage.Dock = DockStyle.Fill;
            _valueMessage.TextAlign = ContentAlignment.MiddleLeft;
            _valueMessage.ForeColor = AppTheme.MutedColor;
            _valueMessage.AutoEllipsis = true;
            _valueMessage.Text = "在左侧双击 Key 查看值。";
            layout.Controls.Add(_valueMessage, 0, 3);
        }

        private ContextMenuStrip BuildCopyMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("复制单元格", null, delegate
            {
                if (_valueGrid.CurrentCell == null) return;
                object value = _valueGrid.CurrentCell.Value;
                if (value == null) return;
                try { Clipboard.SetText(Convert.ToString(value)); }
                catch (Exception) { }
            });
            menu.Items.Add("复制全部", null, delegate
            {
                StringBuilder sb = new StringBuilder();
                for (int r = 0; r < _valueGrid.Rows.Count; r++)
                {
                    for (int c = 0; c < _valueGrid.Columns.Count; c++)
                    {
                        if (c > 0) sb.Append('\t');
                        object value = _valueGrid.Rows[r].Cells[c].Value;
                        sb.Append(value == null ? "" : Convert.ToString(value));
                    }
                    sb.Append("\r\n");
                }
                if (sb.Length == 0) return;
                try { Clipboard.SetText(sb.ToString()); }
                catch (Exception) { }
            });
            return menu;
        }

        private void BuildCommandTab(TabPage tab)
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Padding = new Padding(0);
            tab.Controls.Add(layout);

            _commandBox = new TextBox();
            _commandBox.Multiline = true;
            _commandBox.AcceptsReturn = true;
            _commandBox.ScrollBars = ScrollBars.Vertical;
            _commandBox.Dock = DockStyle.Fill;
            _commandBox.Font = AppTheme.MonoFont(9.5F);
            _commandBox.BorderStyle = BorderStyle.FixedSingle;
            _commandBox.Text = "PING";
            _commandBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    ExecuteCommand();
                }
            };
            layout.Controls.Add(_commandBox, 0, 0);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.WrapContents = false;
            actions.Margin = new Padding(0);
            actions.Padding = new Padding(0, 3, 0, 0);
            _commandExecuteButton = AppTheme.MakeSmallButton("执行 (Ctrl+Enter)", 108);
            _commandExecuteButton.Click += delegate { ExecuteCommand(); };
            actions.Controls.Add(_commandExecuteButton);
            Button clearButton = AppTheme.MakeSmallButton("清空输出", 74);
            clearButton.Click += delegate { _outputBox.Clear(); };
            actions.Controls.Add(clearButton);
            _commandAiButton = AppTheme.MakeSmallButton("AI 分析", 70);
            _commandAiButton.Click += delegate { AnalyzeCommand(); };
            actions.Controls.Add(_commandAiButton);
            Label hint = AppTheme.MakeHintLabel("键浏览请用左侧列表；写命令默认关闭。");
            actions.Controls.Add(hint);
            layout.Controls.Add(actions, 0, 1);

            _outputBox = new TextBox();
            _outputBox.Multiline = true;
            _outputBox.ReadOnly = true;
            _outputBox.Dock = DockStyle.Fill;
            _outputBox.ScrollBars = ScrollBars.Both;
            _outputBox.WordWrap = false;
            _outputBox.Font = AppTheme.MonoFont(9F);
            _outputBox.BackColor = Color.FromArgb(250, 250, 251);
            _outputBox.BorderStyle = BorderStyle.FixedSingle;
            layout.Controls.Add(_outputBox, 0, 2);
        }

        // ---------------- 初始化 / SCAN ----------------

        private void InitializeOnce()
        {
            if (_initialized) return;
            _initialized = true;
            SetBusy(true);
            _headerLabel.Text = "Redis · 正在读取服务器信息…";
            RedisServerSummary summary = null;
            RedisScanState state = null;
            RedisScanPage page = null;
            string error = "";
            AsyncUi.Run(this, delegate
            {
                try { summary = _client.GetSummary(); }
                catch (Exception ex) { error = ex.Message; }
                state = _client.CreateScanState();
                RedisScanPage first = _client.ScanPage(state, DefaultPattern, 200, out error);
                page = first;
            },
            delegate(Exception ex)
            {
                SetBusy(false);
                if (IsDisposed) return;
                if (ex != null && error.Length == 0) error = ex.Message;
                _summary = summary;
                _scanState = state;
                UpdateHeader();
                if (page != null) AddKeyItems(page.Keys);
                if (error.Length > 0)
                {
                    Status("Redis 初始化失败：" + error, AppTheme.ErrorColor);
                    _valueMessage.Text = error;
                }
                else
                {
                    Status("已连接：" + _summary.DisplayText, AppTheme.OkColor);
                    _valueMessage.Text = "在左侧双击 Key 查看值。";
                }
                UpdateScanStatus();
                UpdateButtons();
            });
        }

        private void UpdateHeader()
        {
            if (_summary == null)
            {
                _headerLabel.Text = "Redis · " + _profile.Endpoint +
                    " · " + (_profile.AllowWrites ? "可写" : "只读");
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(_summary.DisplayText);
            if (_summary.DbSize >= 0) sb.Append(" · DBSIZE ").Append(_summary.DbSize);
            if (_summary.IsCluster && !string.IsNullOrEmpty(_summary.ClusterState))
                sb.Append(" · ").Append(_summary.ClusterState);
            sb.Append(" · ").Append(_profile.Endpoint);
            sb.Append(" · DB ").Append(_profile.RedisDatabase);
            sb.Append(" · ").Append(_profile.AllowWrites ? "可写" : "只读");
            _headerLabel.Text = sb.ToString();
        }

        private void Scan(bool continueScan)
        {
            if (_busy) return;
            string pattern = _patternBox.Text.Trim();
            if (pattern.Length == 0) pattern = DefaultPattern;

            if (!continueScan)
            {
                _scanState = _client.CreateScanState();
                _keyList.Items.Clear();
                _listedKeys.Clear();
                _keyCountLabel.Text = "扫描中…";
            }
            else if (_scanState == null || _scanState.Done)
            {
                Status("当前扫描已完成，请重新开始。", AppTheme.MutedColor);
                return;
            }

            RedisScanState state = _scanState;
            RedisScanPage page = null;
            string error = "";
            SetBusy(true);
            Status("正在 SCAN：" + pattern, AppTheme.MutedColor);
            AsyncUi.Run(this, delegate
            {
                page = _client.ScanPage(state, pattern, 200, out error);
            },
            delegate(Exception ex)
            {
                SetBusy(false);
                if (IsDisposed) return;
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    Status("SCAN 失败：" + error, AppTheme.ErrorColor);
                    UpdateButtons();
                    return;
                }
                int added = page == null ? 0 : AddKeyItems(page.Keys);
                UpdateScanStatus();
                if (page != null && page.Done)
                    Status("扫描完成，共显示 " + _keyList.Items.Count + " 个 Key。", AppTheme.OkColor);
                else
                    Status("本页新增 " + added + " 个 Key，可继续扫描。", AppTheme.MutedColor);
                UpdateButtons();
            });
        }

        private int AddKeyItems(List<RedisKeyInfo> keys)
        {
            if (keys == null || keys.Count == 0) return 0;
            int added = 0;
            _keyList.BeginUpdate();
            try
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    RedisKeyInfo info = keys[i];
                    if (info == null || string.IsNullOrEmpty(info.Key)) continue;
                    if (!_listedKeys.Add(info.Key)) continue;
                    ListViewItem item = new ListViewItem(info.Key);
                    item.SubItems.Add(string.IsNullOrEmpty(info.Type) ? "?" : info.Type);
                    item.SubItems.Add(FormatTtl(info.Ttl));
                    item.SubItems.Add(info.Node);
                    item.Tag = info;
                    item.ToolTipText = info.Key;
                    _keyList.Items.Add(item);
                    added++;
                }
            }
            finally
            {
                _keyList.EndUpdate();
            }
            return added;
        }

        private void UpdateScanStatus()
        {
            _keyCountLabel.Text = _keyList.Items.Count == 0
                ? "没有匹配的 Key"
                : "已显示 " + _keyList.Items.Count + " 个 Key";
            _continueButton.Enabled = !_busy && _scanState != null && !_scanState.Done &&
                _keyList.Items.Count > 0;
        }

        private void LoadSelectedValue()
        {
            if (_busy) return;
            if (_keyList.SelectedItems.Count == 0)
            {
                Status("请先在左侧选择一个 Key。", AppTheme.MutedColor);
                return;
            }
            RedisKeyInfo info = _keyList.SelectedItems[0].Tag as RedisKeyInfo;
            if (info == null) return;
            LoadValue(info.Key);
        }

        private void LoadValue(string key)
        {
            if (_busy || string.IsNullOrEmpty(key)) return;
            _currentKey = key;
            _currentValue = null;
            _valueError = "";
            _keyNameLabel.Text = key;
            _keyMetaLabel.Text = "读取中…";
            _valueGrid.Rows.Clear();
            _stringView.Text = "";
            _stringView.Visible = false;
            _valueGrid.Visible = true;
            _valueMessage.Text = "正在读取 " + key + " …";
            int token = ++_valueToken;
            RedisValueData value = null;
            string error = "";
            SetBusy(true);
            Status("读取 Key：" + key, AppTheme.MutedColor);
            AsyncUi.Run(this, delegate
            {
                value = _client.GetValue(key, out error);
            },
            delegate(Exception ex)
            {
                SetBusy(false);
                if (IsDisposed || token != _valueToken) return;
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    _valueError = error;
                    _currentValue = null;
                    _keyMetaLabel.Text = "读取失败";
                    _valueMessage.Text = error;
                    Status("读取失败：" + error, AppTheme.ErrorColor);
                    UpdateButtons();
                    return;
                }
                _currentValue = value;
                RenderValue(value);
                Status("已读取 " + key + "（" + value.Type + "）。", AppTheme.OkColor);
                UpdateButtons();
            });
        }

        private void RenderValue(RedisValueData value)
        {
            if (value == null) return;
            _keyNameLabel.Text = value.Key;
            _keyMetaLabel.Text = "类型 " + value.Type + " · TTL " + FormatTtl(value.Ttl) +
                (value.Length >= 0 ? " · 长度 " + value.Length : "");

            _valueGrid.Rows.Clear();
            bool isString = value.Type == "string";
            _stringView.Visible = isString;
            _valueGrid.Visible = !isString;
            if (isString)
            {
                _stringView.Text = value.Scalar ?? "";
                _stringView.SelectionStart = 0;
                _stringView.SelectionLength = 0;
            }
            else
            {
                _valueGrid.SuspendLayout();
                try
                {
                    for (int i = 0; i < value.Entries.Count; i++)
                    {
                        RedisEntry entry = value.Entries[i];
                        _valueGrid.Rows.Add(
                            entry.Index.ToString(),
                            entry.Field,
                            ShortenOneLine(entry.Value, 4000),
                            entry.Score);
                    }
                }
                finally
                {
                    _valueGrid.ResumeLayout();
                }
            }

            if (value.Truncated)
                _valueMessage.Text = "仅显示前 " + Math.Max(value.Entries.Count, 1) +
                    " 项 / 64 KB；完整内容请用命令控制台读取。";
            else if (isString)
                _valueMessage.Text = "字符串长度 " + value.Length + " 字节。";
            else
                _valueMessage.Text = "共 " + value.Entries.Count + " 项（最多显示 200 项）。";
        }

        // ---------------- 写操作 ----------------

        private delegate bool RedisWriteAction(out string error);

        private void RunWrite(string startingStatus, RedisWriteAction action, Action onSuccess)
        {
            if (_busy) return;
            string error = "";
            SetBusy(true);
            Status(startingStatus, AppTheme.MutedColor);
            AsyncUi.Run(this, delegate
            {
                action(out error);
            },
            delegate(Exception ex)
            {
                SetBusy(false);
                if (IsDisposed) return;
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    Status("写入失败：" + error, AppTheme.ErrorColor);
                    MessageBox.Show(FindForm(), error, "Redis 写入失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateButtons();
                    return;
                }
                Status("写入成功。", AppTheme.OkColor);
                if (onSuccess != null) onSuccess();
                UpdateButtons();
            });
        }

        private bool ConfirmWrite(string title, string summary)
        {
            return _host == null || _host.ConfirmWrite(_profile, title, summary);
        }

        private void EditStringValue()
        {
            if (_currentValue == null || _currentValue.Type != "string") return;
            if (!_profile.AllowWrites)
            {
                Status("当前连接是只读模式。", AppTheme.WarnColor);
                return;
            }
            if (_currentValue.Truncated || _currentValue.Scalar.StartsWith("<binary", StringComparison.OrdinalIgnoreCase))
            {
                Status("值已截断或为二进制，不能在 UI 中覆盖；请用 SET 命令。", AppTheme.WarnColor);
                return;
            }
            string newValue;
            using (RedisEditValueForm dialog = new RedisEditValueForm(_currentValue.Key, _currentValue.Scalar))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                newValue = dialog.Value;
            }
            if (!ConfirmWrite("修改 Redis 字符串", "Key：" + _currentValue.Key +
                "\r\n将用新内容覆盖整个字符串。")) return;

            string key = _currentValue.Key;
            RunWrite("正在写入 " + key + " …", delegate(out string error)
            {
                return _client.SetString(key, newValue, out error);
            }, delegate { LoadValue(key); });
        }

        private void EditTtl()
        {
            if (string.IsNullOrEmpty(_currentKey)) return;
            if (!_profile.AllowWrites)
            {
                Status("当前连接是只读模式。", AppTheme.WarnColor);
                return;
            }
            long current = _currentValue == null ? -1 : _currentValue.Ttl;
            long seconds;
            using (RedisTtlForm dialog = new RedisTtlForm(current))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                seconds = dialog.Seconds;
            }
            string summary = seconds < 0
                ? "Key：" + _currentKey + "\r\n将移除过期时间（PERSIST）。"
                : "Key：" + _currentKey + "\r\n将在 " + FormatTtl(seconds) + " 后过期。";
            if (!ConfirmWrite("修改 Redis TTL", summary)) return;
            string key = _currentKey;
            RunWrite("正在设置 TTL：" + key, delegate(out string error)
            {
                return _client.SetTtl(key, seconds, out error);
            }, delegate { LoadValue(key); });
        }

        private void RenameSelectedKey()
        {
            if (string.IsNullOrEmpty(_currentKey)) return;
            if (!_profile.AllowWrites)
            {
                Status("当前连接是只读模式。", AppTheme.WarnColor);
                return;
            }
            string newKey;
            using (RedisRenameForm dialog = new RedisRenameForm(_currentKey))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                newKey = dialog.Value;
            }
            if (newKey == _currentKey) return;
            if (!ConfirmWrite("重命名 Redis Key", "原 Key：" + _currentKey +
                "\r\n新 Key：" + newKey + "\r\n\r\n如果新 Key 已存在，会被覆盖。")) return;

            string oldKey = _currentKey;
            RunWrite("正在重命名 " + oldKey + " …", delegate(out string error)
            {
                return _client.RenameKey(oldKey, newKey, out error);
            }, delegate
            {
                if (_keyList.SelectedItems.Count > 0)
                {
                    ListViewItem item = _keyList.SelectedItems[0];
                    RedisKeyInfo info = item.Tag as RedisKeyInfo;
                    if (info != null) info.Key = newKey;
                    item.Text = newKey;
                    item.ToolTipText = newKey;
                }
                _listedKeys.Remove(oldKey);
                _listedKeys.Add(newKey);
                LoadValue(newKey);
            });
        }

        private void DeleteSelectedKey()
        {
            if (string.IsNullOrEmpty(_currentKey)) return;
            if (!_profile.AllowWrites)
            {
                Status("当前连接是只读模式。", AppTheme.WarnColor);
                return;
            }
            string key = _currentKey;
            if (!ConfirmWrite("删除 Redis Key", "Key：" + key +
                "\r\n\r\n删除后无法恢复，确认继续？")) return;

            RunWrite("正在删除 " + key + " …", delegate(out string error)
            {
                return _client.DeleteKey(key, out error);
            }, delegate
            {
                if (_keyList.SelectedItems.Count > 0) _keyList.Items.Remove(_keyList.SelectedItems[0]);
                else
                {
                    for (int i = 0; i < _keyList.Items.Count; i++)
                    {
                        if (string.Equals(_keyList.Items[i].Text, key, StringComparison.Ordinal))
                        {
                            _keyList.Items.RemoveAt(i);
                            break;
                        }
                    }
                }
                _listedKeys.Remove(key);
                _currentKey = "";
                _currentValue = null;
                _valueToken++;
                _keyNameLabel.Text = "尚未选择 Key";
                _keyMetaLabel.Text = "";
                _valueGrid.Rows.Clear();
                _stringView.Text = "";
                _stringView.Visible = false;
                _valueGrid.Visible = true;
                _valueMessage.Text = "已删除 Key：" + key;
                UpdateScanStatus();
                UpdateButtons();
            });
        }

        // ---------------- 命令控制台 ----------------

        private void ExecuteCommand()
        {
            if (_busy) return;
            string line = _commandBox.Text.Trim();
            string parseError;
            string[] parts = RedisCommandLine.Parse(line, out parseError);
            if (parts == null)
            {
                Status(parseError, AppTheme.WarnColor);
                return;
            }
            string command;
            string validationError;
            if (!RedisCommandGuard.Validate(parts, _profile.AllowWrites, out command, out validationError))
            {
                Status(validationError, AppTheme.WarnColor);
                AppendOutput("(拒绝) " + validationError);
                return;
            }

            string display = RedisCommandLine.Join(parts);
            if (!RedisCommandGuard.IsReadOnlyCommand(command))
            {
                bool needsConfirm = _profile.Environment == DatabaseEnvironment.Production ||
                    RedisCommandGuard.IsDestructive(command);
                if (needsConfirm && !ConfirmWrite("执行 Redis 写命令", "命令：" + display)) return;
            }

            _lastCommandLine = display;
            _lastCommandError = "";
            AppendOutput("> " + display);
            string output = "";
            string error = "";
            SetBusy(true);
            Status("执行命令：" + command, AppTheme.MutedColor);
            AsyncUi.Run(this, delegate
            {
                output = _client.RunCommand(parts, out error);
            },
            delegate(Exception ex)
            {
                SetBusy(false);
                if (IsDisposed) return;
                if (ex != null && error.Length == 0) error = ex.Message;
                if (error.Length > 0)
                {
                    _lastCommandError = error;
                    AppendOutput("(错误) " + error);
                    Status("命令失败：" + error, AppTheme.ErrorColor);
                    UpdateButtons();
                    return;
                }
                if (output.StartsWith("(error) ", StringComparison.Ordinal))
                    _lastCommandError = output.Substring(8).Trim();
                AppendOutput(output);
                Status("命令完成：" + command, AppTheme.OkColor);
                if (!RedisCommandGuard.IsReadOnlyCommand(command) && !string.IsNullOrEmpty(_currentKey))
                    LoadValue(_currentKey);
                else
                    UpdateButtons();
            });
        }

        private void AppendOutput(string text)
        {
            if (_outputBox == null || _outputBox.IsDisposed || string.IsNullOrEmpty(text)) return;
            if (_outputBox.TextLength > 500000)
            {
                string existing = _outputBox.Text;
                int cut = existing.IndexOf('\n', 100000);
                if (cut < 0) cut = 100000;
                _outputBox.Text = "…（已清理早期输出）\r\n" + existing.Substring(cut + 1);
            }
            _outputBox.AppendText(text.TrimEnd() + "\r\n");
            _outputBox.SelectionStart = _outputBox.TextLength;
            _outputBox.ScrollToCaret();
        }

        // ---------------- AI ----------------

        private List<LogClue> BuildClues()
        {
            List<LogClue> clues = new List<LogClue>();
            if (!string.IsNullOrEmpty(_valueError))
            {
                LogClue clue = new LogClue();
                clue.Label = "值读取错误";
                clue.Value = _valueError;
                clues.Add(clue);
            }
            if (!string.IsNullOrEmpty(_lastCommandError))
            {
                LogClue clue = new LogClue();
                clue.Label = "命令错误";
                clue.Value = _lastCommandError;
                clues.Add(clue);
            }
            return clues;
        }

        private void AnalyzeValue()
        {
            if (_host == null) return;
            if (_currentValue == null)
            {
                Status("先选择一个 Key 并读取值。", AppTheme.MutedColor);
                return;
            }
            string command = _currentValue.Type == "string"
                ? "GET " + _currentValue.Key
                : "TYPE " + _currentValue.Key + " → " + _currentValue.Type;
            DatabaseAi.AnalyzeRedis(_host, _profile, command, _currentValue, _valueError, BuildClues());
        }

        private void AnalyzeCommand()
        {
            if (_host == null) return;
            if (string.IsNullOrEmpty(_lastCommandLine))
            {
                Status("先在命令控制台执行一条命令。", AppTheme.MutedColor);
                return;
            }
            DatabaseAi.AnalyzeRedis(_host, _profile, _lastCommandLine, null,
                _lastCommandError, BuildClues());
        }

        // ---------------- 快捷键 / 生命周期 ----------------

        public Control View
        {
            get { return this; }
        }

        public bool HandleShortcut(Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter) ||
                keyData == (Keys.Control | Keys.Return))
            {
                ExecuteCommand();
                return true;
            }
            if (keyData == Keys.F5)
            {
                Scan(false);
                return true;
            }
            if (keyData == (Keys.Control | Keys.F))
            {
                _patternBox.Focus();
                _patternBox.SelectAll();
                return true;
            }
            if (keyData == (Keys.Control | Keys.L))
            {
                _commandBox.Focus();
                _commandBox.SelectAll();
                return true;
            }
            return false;
        }

        public void ApplyLogClue(LogClue clue)
        {
            if (clue == null || string.IsNullOrEmpty(clue.Value)) return;
            string value = clue.Value.Trim();
            _patternBox.Text = "*" + RedisCommandGuard.EscapePattern(value) + "*";
            Scan(false);
            Status("已按" + clue.Label + "在 Redis Key 中搜索：" + value, AppTheme.PrimaryColor);
        }

        public bool ConfirmClose()
        {
            if (_busy)
            {
                if (MessageBox.Show(FindForm(), "Redis 操作仍在进行中，确定关闭？", "关闭",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK)
                {
                    return false;
                }
            }
            return true;
        }

        public void Close()
        {
            try { _client.Close(); }
            catch (Exception) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _client.Dispose(); }
                catch (Exception) { }
            }
            base.Dispose(disposing);
        }

        // ---------------- UI 状态 ----------------

        private void SetBusy(bool busy)
        {
            _busy = busy;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool hasKey = !string.IsNullOrEmpty(_currentKey);
            _scanButton.Enabled = !_busy;
            _patternBox.Enabled = !_busy;
            _continueButton.Enabled = !_busy && _scanState != null && !_scanState.Done &&
                _keyList.Items.Count > 0;
            _commandExecuteButton.Enabled = !_busy;
            _commandAiButton.Enabled = !_busy;
            _refreshButton.Enabled = !_busy && hasKey;
            _aiButton.Enabled = !_busy && _currentValue != null;
            bool write = _profile.AllowWrites && !_busy && hasKey;
            bool stringValue = _currentValue != null && _currentValue.Type == "string" &&
                !_currentValue.Truncated &&
                _currentValue.Scalar.IndexOf("<binary", StringComparison.OrdinalIgnoreCase) != 0;
            _editValueButton.Enabled = write && stringValue;
            _ttlButton.Enabled = write;
            _renameButton.Enabled = write;
            _deleteButton.Enabled = write;
        }

        private void Status(string text, Color color)
        {
            if (_host != null) _host.Status(text, color);
        }

        private static string FormatTtl(long ttl)
        {
            if (ttl == -1) return "永久";
            if (ttl == -2) return "已过期";
            if (ttl < 0) return "-";
            if (ttl < 60) return ttl + " 秒";
            if (ttl < 3600) return (ttl / 60) + " 分 " + (ttl % 60) + " 秒";
            if (ttl < 86400)
            {
                long hours = ttl / 3600;
                long minutes = (ttl % 3600) / 60;
                return hours + " 小时" + (minutes > 0 ? " " + minutes + " 分" : "");
            }
            long days = ttl / 86400;
            long rest = (ttl % 86400) / 3600;
            return days + " 天" + (rest > 0 ? " " + rest + " 小时" : "");
        }

        private static string ShortenOneLine(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string value = text.Replace("\r", " ").Replace("\n", " ");
            if (value.Length <= max) return value;
            return value.Substring(0, max) + "…";
        }
    }

    internal sealed class RedisProvider : IDatabaseProvider
    {
        public string Kind
        {
            get { return DatabaseKinds.Redis; }
        }

        public string DisplayName
        {
            get { return "Redis"; }
        }

        public int DefaultPort
        {
            get { return 6379; }
        }

        public bool SupportsWrites
        {
            get { return true; }
        }

        public IDatabaseConnection Open(DatabaseConnectionProfile profile, out string error)
        {
            return RedisClient.Open(profile, out error);
        }

        public IDatabaseWorkspace CreateWorkspace(DatabaseTabHost host,
            DatabaseConnectionProfile profile, IDatabaseConnection connection)
        {
            RedisClient client = connection as RedisClient;
            if (client == null) throw new ArgumentException("Redis 连接类型不正确。", "connection");
            return new RedisWorkspace(host, profile, client);
        }
    }

    // ---------------- 小对话框 ----------------

    internal sealed class RedisEditValueForm : Form
    {
        private readonly TextBox _text;

        public RedisEditValueForm(string key, string value)
        {
            Text = "编辑字符串值";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(700, 500);
            MinimumSize = new Size(480, 320);
            BackColor = Color.White;
            Font = AppTheme.UiFont(9F);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.Padding = new Padding(12);
            Controls.Add(root);

            Label label = new Label();
            label.Text = "Key：" + key;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = true;
            label.ForeColor = AppTheme.LabelColor;
            root.Controls.Add(label, 0, 0);

            _text = new TextBox();
            _text.Multiline = true;
            _text.Dock = DockStyle.Fill;
            _text.ScrollBars = ScrollBars.Both;
            _text.WordWrap = false;
            _text.Font = AppTheme.MonoFont(9F);
            _text.BorderStyle = BorderStyle.FixedSingle;
            _text.Text = value ?? "";
            root.Controls.Add(_text, 0, 1);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 6, 0, 0);
            Button cancel = AppTheme.MakeButton("取消", false, 80);
            cancel.DialogResult = DialogResult.Cancel;
            Button ok = AppTheme.MakeButton("保存", true, 80);
            ok.DialogResult = DialogResult.OK;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            root.Controls.Add(buttons, 0, 2);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public string Value
        {
            get { return _text.Text; }
        }
    }

    internal sealed class RedisRenameForm : Form
    {
        private readonly TextBox _text;

        public RedisRenameForm(string key)
        {
            Text = "重命名 Key";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 150);
            BackColor = Color.White;
            Font = AppTheme.UiFont(9F);

            Label label = new Label();
            label.Text = "原 Key：" + key + "\r\n新 Key：";
            label.Location = new Point(18, 14);
            label.Size = new Size(480, 36);
            label.AutoEllipsis = true;
            label.ForeColor = AppTheme.LabelColor;
            Controls.Add(label);

            _text = new TextBox();
            _text.Location = new Point(18, 56);
            _text.Width = 480;
            _text.BorderStyle = BorderStyle.FixedSingle;
            _text.Font = AppTheme.MonoFont(9F);
            _text.Text = key;
            Controls.Add(_text);

            Button cancel = AppTheme.MakeButton("取消", false, 80);
            cancel.Location = new Point(418, 102);
            cancel.DialogResult = DialogResult.Cancel;
            Button ok = AppTheme.MakeButton("确定", true, 80);
            ok.Location = new Point(330, 102);
            ok.Click += delegate
            {
                string value = _text.Text.Trim();
                if (value.Length == 0)
                {
                    MessageBox.Show(this, "新 Key 不能为空。", "重命名",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            Controls.Add(cancel);
            Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public string Value
        {
            get { return _text.Text.Trim(); }
        }
    }

    internal sealed class RedisTtlForm : Form
    {
        private readonly NumericUpDown _seconds;
        private readonly CheckBox _persist;

        public RedisTtlForm(long currentTtl)
        {
            Text = "设置 TTL";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 180);
            BackColor = Color.White;
            Font = AppTheme.UiFont(9F);

            Label label = new Label();
            label.Text = "当前 TTL：" + (currentTtl < 0 ? "永久 / 未知" : currentTtl + " 秒") +
                "\r\n设置多少秒后过期？";
            label.Location = new Point(18, 14);
            label.Size = new Size(380, 40);
            label.ForeColor = AppTheme.LabelColor;
            Controls.Add(label);

            _seconds = new NumericUpDown();
            _seconds.Minimum = 1;
            _seconds.Maximum = 2147483647;
            _seconds.Value = currentTtl > 0 ? Math.Min(currentTtl, 2147483647) : 3600;
            _seconds.Location = new Point(18, 62);
            _seconds.Width = 180;
            Controls.Add(_seconds);

            _persist = new CheckBox();
            _persist.Text = "永久（移除过期时间）";
            _persist.AutoSize = true;
            _persist.Location = new Point(214, 64);
            _persist.Checked = currentTtl < 0;
            _persist.CheckedChanged += delegate { _seconds.Enabled = !_persist.Checked; };
            Controls.Add(_persist);
            _seconds.Enabled = !_persist.Checked;

            Button cancel = AppTheme.MakeButton("取消", false, 80);
            cancel.Location = new Point(318, 132);
            cancel.DialogResult = DialogResult.Cancel;
            Button ok = AppTheme.MakeButton("确定", true, 80);
            ok.Location = new Point(230, 132);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(cancel);
            Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public long Seconds
        {
            get { return _persist.Checked ? -1 : (long)_seconds.Value; }
        }
    }
}
