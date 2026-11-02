using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Curl2Url
{
    // “数据库排查”页的外部框架：连接配置管理 + 连接生命周期。
    // 具体数据库的浏览/查询界面由 IDatabaseWorkspace 提供，新增数据库类型不用改这里。
    internal sealed class DatabasePage : UserControl, IAppFeaturePage
    {
        private readonly AppFeatureContext _context;
        private readonly DatabaseConnectionStore _store = new DatabaseConnectionStore();

        private readonly ComboBox _connections;
        private readonly Button _newButton;
        private readonly Button _editButton;
        private readonly Button _deleteButton;
        private readonly Button _connectButton;
        private readonly Label _environmentLabel;
        private readonly Label _statusLabel;
        private readonly Panel _content;
        private readonly Control _welcome;

        private IDatabaseConnection _connection;
        private IDatabaseWorkspace _workspace;
        private DatabaseConnectionProfile _connectedProfile;
        private bool _busy;
        private bool _autoConnectTried;

        public DatabasePage(AppFeatureContext context)
        {
            _context = context ?? new AppFeatureContext();
            BackColor = AppTheme.PageBack;
            Font = AppTheme.UiFont(9F);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.Padding = new Padding(12, 8, 12, 0);
            Controls.Add(root);

            TableLayoutPanel toolbar = new TableLayoutPanel();
            toolbar.Dock = DockStyle.Fill;
            toolbar.ColumnCount = 7;
            toolbar.RowCount = 1;
            toolbar.Margin = new Padding(0);
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            root.Controls.Add(toolbar, 0, 0);

            Label connectLabel = new Label();
            connectLabel.Text = "连接";
            connectLabel.Dock = DockStyle.Fill;
            connectLabel.TextAlign = ContentAlignment.MiddleLeft;
            connectLabel.ForeColor = AppTheme.LabelColor;
            toolbar.Controls.Add(connectLabel, 0, 0);

            _connections = new ComboBox();
            _connections.DropDownStyle = ComboBoxStyle.DropDownList;
            _connections.Dock = DockStyle.Fill;
            _connections.Margin = new Padding(0, 9, 8, 9);
            _connections.SelectedIndexChanged += delegate { OnProfileSelectionChanged(); };
            toolbar.Controls.Add(_connections, 1, 0);

            _environmentLabel = new Label();
            _environmentLabel.Dock = DockStyle.Fill;
            _environmentLabel.Margin = new Padding(0, 9, 8, 9);
            _environmentLabel.TextAlign = ContentAlignment.MiddleCenter;
            _environmentLabel.BorderStyle = BorderStyle.FixedSingle;
            toolbar.Controls.Add(_environmentLabel, 2, 0);

            _newButton = AppTheme.MakeButton("新建", false, 70);
            _newButton.Margin = new Padding(0, 9, 8, 9);
            _newButton.Click += delegate { NewProfile(); };
            toolbar.Controls.Add(_newButton, 3, 0);

            _editButton = AppTheme.MakeButton("编辑", false, 70);
            _editButton.Margin = new Padding(0, 9, 8, 9);
            _editButton.Click += delegate { EditProfile(); };
            toolbar.Controls.Add(_editButton, 4, 0);

            _deleteButton = AppTheme.MakeButton("删除", false, 70);
            _deleteButton.Margin = new Padding(0, 9, 8, 9);
            _deleteButton.Click += delegate { DeleteProfile(); };
            toolbar.Controls.Add(_deleteButton, 5, 0);

            _connectButton = AppTheme.MakeButton("连接", true, 92);
            _connectButton.Margin = new Padding(0, 9, 0, 9);
            _connectButton.Click += delegate { ToggleConnection(); };
            toolbar.Controls.Add(_connectButton, 6, 0);

            _content = new Panel();
            _content.Dock = DockStyle.Fill;
            _content.BackColor = Color.White;
            _content.BorderStyle = BorderStyle.FixedSingle;
            _content.Margin = new Padding(0);
            root.Controls.Add(_content, 0, 1);

            _welcome = CreateWelcome();
            _content.Controls.Add(_welcome);

            _statusLabel = new Label();
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.ForeColor = AppTheme.MutedColor;
            _statusLabel.Text = "新建连接或选择已有连接后点击“连接”。";
            root.Controls.Add(_statusLabel, 0, 2);

            string error;
            if (!_store.Load(out error))
            {
                Status(error, AppTheme.ErrorColor);
            }
            ReloadProfiles(_store.LastProfileId);

            // 页面在启动时一次性创建，但只有用户真正切到「数据库排查」页时才自动连上次的连接，
            // 避免一打开工具就去连库。
            VisibleChanged += delegate(object sender, EventArgs e) { OnPageVisibleChanged(); };
        }

        private void OnPageVisibleChanged()
        {
            if (!Visible || _autoConnectTried) return;
            _autoConnectTried = true;
            TryAutoConnect();
        }

        // 自动连接上次使用的连接：生产库不自动连，避免打开工具就挂到线上。
        private void TryAutoConnect()
        {
            if (IsDisposed || _busy || _connection != null) return;
            string lastId = _store.LastProfileId;
            if (string.IsNullOrEmpty(lastId)) return;
            DatabaseConnectionProfile last = _store.Find(lastId);
            if (last == null) return;
            if (last.Environment == DatabaseEnvironment.Production) return;

            DatabaseConnectionProfile selected = SelectedProfile;
            if (selected == null || selected.Id != last.Id)
            {
                for (int i = 0; i < _connections.Items.Count; i++)
                {
                    ProfileItem item = _connections.Items[i] as ProfileItem;
                    if (item != null && item.Profile.Id == last.Id)
                    {
                        _connections.SelectedIndex = i;
                        break;
                    }
                }
            }
            ConnectProfile(last, true);
        }

        // 把工作区最近切换到的库记到连接配置里，下次连接直接打开这个库。
        private void RememberDatabase(string profileId, string database)
        {
            if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(database)) return;
            DatabaseConnectionProfile profile = _store.Find(profileId);
            if (profile == null) return;
            if (profile.Option("lastDatabase", "") == database) return;
            profile.SetOption("lastDatabase", database);
            SaveStore();
        }

        private Control CreateWelcome()
        {
            TableLayoutPanel panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.ColumnCount = 1;
            panel.RowCount = 4;
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            panel.BackColor = Color.White;

            Label title = new Label();
            title.Text = "数据库排查台";
            title.Font = AppTheme.UiFont(18F, FontStyle.Bold);
            title.ForeColor = AppTheme.TitleColor;
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.Dock = DockStyle.Fill;
            panel.Controls.Add(title, 0, 1);

            Label hint = new Label();
            hint.Text = "直接连接 MySQL / Redis，浏览表与 Key、执行查询、查看执行计划，\r\n" +
                "把结果、错误和表结构交给 AI 做排查分析。\r\n\r\n" +
                "连接默认只读；写权限按连接单独开启，并带二次确认。";
            hint.ForeColor = AppTheme.MutedColor;
            hint.TextAlign = ContentAlignment.TopCenter;
            hint.Dock = DockStyle.Fill;
            panel.Controls.Add(hint, 0, 2);
            return panel;
        }

        private void ReloadProfiles(string selectId)
        {
            DatabaseConnectionProfile selected = null;
            _connections.Items.Clear();
            List<DatabaseConnectionProfile> profiles = _store.Profiles;
            for (int i = 0; i < profiles.Count; i++)
            {
                _connections.Items.Add(new ProfileItem(profiles[i]));
                if (profiles[i].Id == selectId) selected = profiles[i];
            }
            if (selected == null && profiles.Count > 0) selected = profiles[0];
            if (selected != null)
            {
                for (int i = 0; i < _connections.Items.Count; i++)
                {
                    ProfileItem item = _connections.Items[i] as ProfileItem;
                    if (item != null && item.Profile.Id == selected.Id)
                    {
                        _connections.SelectedIndex = i;
                        break;
                    }
                }
            }
            else if (_connections.Items.Count > 0)
            {
                _connections.SelectedIndex = 0;
            }
            UpdateProfileButtons();
            OnProfileSelectionChanged();
        }

        private DatabaseConnectionProfile SelectedProfile
        {
            get
            {
                ProfileItem item = _connections.SelectedItem as ProfileItem;
                return item == null ? null : item.Profile;
            }
        }

        private void OnProfileSelectionChanged()
        {
            DatabaseConnectionProfile profile = SelectedProfile;
            if (profile == null)
            {
                _environmentLabel.Text = "";
                _environmentLabel.BackColor = Color.White;
                _environmentLabel.ForeColor = AppTheme.MutedColor;
                if (!_busy) _connectButton.Enabled = false;
                return;
            }
            _environmentLabel.Text = profile.Environment;
            ApplyEnvironmentStyle(_environmentLabel, profile.Environment);
            if (!_busy) _connectButton.Enabled = true;
        }

        private static void ApplyEnvironmentStyle(Label label, string environment)
        {
            if (environment == DatabaseEnvironment.Production)
            {
                label.BackColor = Color.FromArgb(255, 235, 235);
                label.ForeColor = AppTheme.ErrorColor;
            }
            else if (environment == DatabaseEnvironment.Staging)
            {
                label.BackColor = Color.FromArgb(255, 244, 229);
                label.ForeColor = Color.FromArgb(180, 95, 6);
            }
            else if (environment == DatabaseEnvironment.Test)
            {
                label.BackColor = Color.FromArgb(232, 244, 255);
                label.ForeColor = Color.FromArgb(24, 100, 171);
            }
            else
            {
                label.BackColor = Color.FromArgb(244, 244, 245);
                label.ForeColor = AppTheme.LabelColor;
            }
        }

        private void UpdateProfileButtons()
        {
            bool hasProfile = SelectedProfile != null;
            bool connected = _connection != null;
            _newButton.Enabled = !_busy;
            _editButton.Enabled = !_busy && hasProfile && !connected;
            _deleteButton.Enabled = !_busy && hasProfile && !connected;
            _connections.Enabled = !_busy && !connected;
            _connectButton.Enabled = !_busy && (connected || hasProfile);
            _connectButton.Text = connected ? "断开" : "连接";
        }

        private void NewProfile()
        {
            DatabaseConnectionProfile profile = new DatabaseConnectionProfile();
            profile.Name = "新连接";
            if (!ShowProfileDialog(profile)) return;
            _store.AddOrUpdate(profile);
            SaveStore();
            ReloadProfiles(profile.Id);
        }

        private void EditProfile()
        {
            DatabaseConnectionProfile selected = SelectedProfile;
            if (selected == null) return;
            DatabaseConnectionProfile edited = selected.Clone();
            if (!ShowProfileDialog(edited)) return;
            _store.AddOrUpdate(edited);
            SaveStore();
            ReloadProfiles(edited.Id);
        }

        private bool ShowProfileDialog(DatabaseConnectionProfile profile)
        {
            using (DatabaseConnectionDialog dialog = new DatabaseConnectionDialog(profile))
            {
                return dialog.ShowDialog(FindForm()) == DialogResult.OK;
            }
        }

        private void DeleteProfile()
        {
            DatabaseConnectionProfile selected = SelectedProfile;
            if (selected == null) return;
            if (MessageBox.Show(FindForm(), "删除连接“" + selected.Name + "”？\r\n只删除本地配置，不影响数据库。",
                "删除连接", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                return;
            }
            _store.Remove(selected.Id);
            SaveStore();
            ReloadProfiles("");
        }

        private void SaveStore()
        {
            string error;
            if (!_store.Save(out error)) Status(error, AppTheme.ErrorColor);
        }

        private void ToggleConnection()
        {
            if (_busy) return;
            if (_connection != null)
            {
                Disconnect(true);
                return;
            }
            ConnectSelected();
        }

        private void ConnectSelected()
        {
            DatabaseConnectionProfile profile = SelectedProfile;
            if (profile == null)
            {
                MessageBox.Show(FindForm(), "还没有可用的连接，请先点击“新建”。",
                    "连接数据库", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ConnectProfile(profile, false);
        }

        private void ConnectProfile(DatabaseConnectionProfile profile, bool auto)
        {
            if (_busy || _connection != null || profile == null) return;
            if (!auto && profile.Environment == DatabaseEnvironment.Production && profile.AllowWrites)
            {
                if (MessageBox.Show(FindForm(),
                    "该连接标记为生产环境并开启了写权限。\r\n" +
                    "每次执行写 SQL 前仍会单独确认，请务必核对语句。是否继续连接？",
                    "生产连接", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK)
                {
                    return;
                }
            }

            IDatabaseProvider provider = DatabaseProviders.Find(profile.Kind);
            if (provider == null)
            {
                Status("不支持的数据库类型：" + profile.Kind, AppTheme.ErrorColor);
                return;
            }

            DatabaseConnectionProfile connecting = profile.Clone();
            SetBusy(true);
            Status("正在连接 " + connecting.DisplayText + " …", AppTheme.PrimaryColor);

            IDatabaseConnection opened = null;
            string error = "";
            string version = "";
            AsyncUi.Run(this, delegate
            {
                opened = provider.Open(connecting, out error);
                if (opened != null) version = opened.ServerVersion;
            },
            delegate(Exception ex)
            {
                if (IsDisposed)
                {
                    if (opened != null) opened.Dispose();
                    return;
                }
                if (ex != null && error.Length == 0) error = ex.Message;
                if (opened == null)
                {
                    SetBusy(false);
                    Status((auto ? "自动连接失败：" : "连接失败：") + error, AppTheme.ErrorColor);
                    // 自动连接失败不弹窗，用户仍可手动点“连接”重试。
                    if (!auto)
                    {
                        MessageBox.Show(FindForm(), error, "连接失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    return;
                }

                try
                {
                    DatabaseTabHost host = new DatabaseTabHost();
                    host.Owner = FindForm();
                    host.SetStatus = new Action<string, Color>(Status);
                    host.GetLogText = _context.GetLogText;
                    host.LoadAiSettings = _context.LoadAiSettings;
                    host.ShowAiSettings = _context.ShowAiSettings;
                    string connectingId = connecting.Id;
                    host.RememberDatabase = delegate(string database)
                    {
                        RememberDatabase(connectingId, database);
                    };
                    IDatabaseWorkspace workspace = provider.CreateWorkspace(host, connecting.Clone(), opened);
                    if (workspace == null || workspace.View == null)
                    {
                        opened.Dispose();
                        SetBusy(false);
                        Status("创建数据库工作区失败。", AppTheme.ErrorColor);
                        return;
                    }

                    _connection = opened;
                    _workspace = workspace;
                    _connectedProfile = connecting;
                    // 工作区必须撑满内容区：不设 Dock 时控件会停在默认 150x150，
                    // SplitContainer 的右侧面板会整个溢出到可见区域外，只剩左边的库表树。
                    Control view = workspace.View;
                    view.Dock = DockStyle.Fill;
                    _content.Controls.Clear();
                    _content.Controls.Add(view);
                    SetBusy(false);
                    UpdateProfileButtons();
                    string suffix = string.IsNullOrEmpty(version) ? "" : " · " + version;
                    Status("已连接 " + connecting.Name + "（" + connecting.Endpoint + "）" + suffix +
                        (connecting.AllowWrites ? " · 可写" : " · 只读"), AppTheme.OkColor);

                    _store.SetLast(connecting.Id);
                    connecting.LastUsedUtc = DateTime.UtcNow;
                    for (int i = 0; i < _store.Profiles.Count; i++)
                    {
                        if (_store.Profiles[i].Id == connecting.Id)
                        {
                            _store.Profiles[i].LastUsedUtc = connecting.LastUsedUtc;
                            break;
                        }
                    }
                    SaveStore();
                }
                catch (Exception createError)
                {
                    opened.Dispose();
                    SetBusy(false);
                    Status("初始化数据库界面失败：" + createError.Message, AppTheme.ErrorColor);
                    MessageBox.Show(FindForm(), createError.Message, "初始化失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        private void Disconnect(bool ask)
        {
            if (_workspace != null && ask && !_workspace.ConfirmClose()) return;
            CloseWorkspace();
            Status("已断开数据库连接。", AppTheme.MutedColor);
        }

        private void CloseWorkspace()
        {
            IDatabaseWorkspace workspace = _workspace;
            IDatabaseConnection connection = _connection;
            _workspace = null;
            _connection = null;
            _connectedProfile = null;

            if (workspace != null)
            {
                try { workspace.Close(); }
                catch (Exception) { }
                try { workspace.Dispose(); }
                catch (Exception) { }
            }
            if (connection != null)
            {
                try { connection.Dispose(); }
                catch (Exception) { }
            }

            _content.Controls.Clear();
            _content.Controls.Add(_welcome);
            _welcome.Visible = true;
            UpdateProfileButtons();
            OnProfileSelectionChanged();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            UseWaitCursor = busy;
            _newButton.Enabled = !busy;
            _editButton.Enabled = !busy && SelectedProfile != null && _connection == null;
            _deleteButton.Enabled = !busy && SelectedProfile != null && _connection == null;
            _connections.Enabled = !busy && _connection == null;
            _connectButton.Enabled = !busy;
        }

        private void Status(string text, Color color)
        {
            if (IsDisposed) return;
            // 数据库页自带状态栏，不再同步到主窗口底栏：否则同一条消息会在页面状态、
            // 查询结果区和主窗口底栏重复出现，看起来像“双重提示”。
            _statusLabel.Text = text;
            _statusLabel.ForeColor = color;
        }

        public bool HandleShortcut(Keys keyData)
        {
            return _workspace != null && _workspace.HandleShortcut(keyData);
        }

        public bool ConfirmClose()
        {
            if (_busy)
            {
                if (MessageBox.Show(FindForm(), "数据库操作仍在进行中，确定关闭窗口？",
                    "关闭", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK)
                {
                    return false;
                }
                return true;
            }
            return _workspace == null || _workspace.ConfirmClose();
        }

        public void Shutdown()
        {
            CloseWorkspace();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseWorkspace();
            }
            base.Dispose(disposing);
        }

        private sealed class ProfileItem
        {
            public readonly DatabaseConnectionProfile Profile;

            public ProfileItem(DatabaseConnectionProfile profile)
            {
                Profile = profile;
            }

            public override string ToString()
            {
                if (Profile == null) return "";
                return Profile.Name + "  ·  " + DatabaseKinds.DisplayName(Profile.Kind) +
                    "  ·  " + Profile.Endpoint + "  ·  " + Profile.Environment +
                    (Profile.AllowWrites ? "  · 可写" : "");
            }
        }
    }

    internal sealed class DatabaseFeature : IAppFeature
    {
        public string Id
        {
            get { return "database"; }
        }

        public string Title
        {
            get { return "数据库排查"; }
        }

        public bool UsesFullHeight
        {
            get { return true; }
        }

        public Control CreatePage(AppFeatureContext context)
        {
            return new DatabasePage(context);
        }
    }
}
