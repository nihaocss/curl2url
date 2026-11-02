using System;
using System.Drawing;
using System.Windows.Forms;

namespace Curl2Url
{
    // 新建 / 编辑数据库连接。密码只保留在内存，保存时由 DatabaseConnectionStore 走 DPAPI。
    internal sealed class DatabaseConnectionDialog : Form
    {
        private readonly DatabaseConnectionProfile _profile;
        private readonly TextBox _name;
        private readonly ComboBox _kind;
        private readonly TextBox _host;
        private readonly NumericUpDown _port;
        private readonly TextBox _user;
        private readonly TextBox _password;
        private readonly TextBox _database;
        private readonly NumericUpDown _redisDatabase;
        private readonly ComboBox _sslMode;
        private readonly CheckBox _redisTls;
        private readonly CheckBox _allowInvalidCert;
        private readonly ComboBox _environment;
        private readonly CheckBox _allowWrites;
        private readonly Label _kindHint;
        private readonly Label _writeHint;
        private bool _updatingKind;

        public DatabaseConnectionDialog(DatabaseConnectionProfile profile)
        {
            _profile = profile ?? new DatabaseConnectionProfile();
            Text = string.IsNullOrEmpty(_profile.Name) ? "新建数据库连接" : "编辑数据库连接";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 620);
            BackColor = Color.White;
            Font = AppTheme.UiFont(9F);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(20, 16, 20, 12);
            root.ColumnCount = 2;
            root.RowCount = 15;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 13; i++)
            {
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            }
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            AddLabel(root, "连接名称", 0);
            _name = AddTextBox(root, 0);
            _name.Text = _profile.Name;

            AddLabel(root, "数据库类型", 1);
            _kind = new ComboBox();
            _kind.DropDownStyle = ComboBoxStyle.DropDownList;
            _kind.Items.Add("MySQL");
            _kind.Items.Add("Redis");
            _kind.SelectedIndex = string.Equals(_profile.Kind, DatabaseKinds.Redis,
                StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            root.Controls.Add(_kind, 1, 1);
            _kind.Dock = DockStyle.Fill;
            _kind.SelectedIndexChanged += delegate { ApplyKind(); };

            AddLabel(root, "主机地址", 2);
            _host = AddTextBox(root, 2);
            _host.Text = _profile.Host;

            AddLabel(root, "端口", 3);
            _port = new NumericUpDown();
            _port.Minimum = 1;
            _port.Maximum = 65535;
            _port.Value = Math.Max(1, Math.Min(65535, _profile.Port));
            _port.Dock = DockStyle.Fill;
            root.Controls.Add(_port, 1, 3);

            AddLabel(root, "用户名", 4);
            _user = AddTextBox(root, 4);
            _user.Text = _profile.User;

            AddLabel(root, "密码", 5);
            _password = AddTextBox(root, 5);
            _password.UseSystemPasswordChar = true;
            _password.Text = _profile.Password;
            _password.TextChanged += delegate { _password.UseSystemPasswordChar = _password.Text.Length > 0; };

            AddLabel(root, "默认数据库", 6);
            _database = AddTextBox(root, 6);
            _database.Text = _profile.Database;

            AddLabel(root, "Redis DB", 7);
            _redisDatabase = new NumericUpDown();
            _redisDatabase.Minimum = 0;
            _redisDatabase.Maximum = 65535;
            _redisDatabase.Value = Math.Max(0, Math.Min(65535, _profile.RedisDatabase));
            _redisDatabase.Dock = DockStyle.Fill;
            root.Controls.Add(_redisDatabase, 1, 7);

            AddLabel(root, "SSL 模式", 8);
            _sslMode = new ComboBox();
            _sslMode.DropDownStyle = ComboBoxStyle.DropDown;
            _sslMode.Items.Add("Preferred");
            _sslMode.Items.Add("Required");
            _sslMode.Items.Add("Disabled");
            _sslMode.Items.Add("VerifyCA");
            _sslMode.Items.Add("VerifyFull");
            _sslMode.Text = string.IsNullOrEmpty(_profile.SslMode) ? "Preferred" : _profile.SslMode;
            _sslMode.Dock = DockStyle.Fill;
            root.Controls.Add(_sslMode, 1, 8);

            AddLabel(root, "传输安全", 9);
            FlowLayoutPanel securePanel = new FlowLayoutPanel();
            securePanel.Dock = DockStyle.Fill;
            securePanel.Margin = new Padding(0, 6, 0, 0);
            securePanel.WrapContents = false;
            _redisTls = new CheckBox();
            _redisTls.Text = "Redis TLS";
            _redisTls.AutoSize = true;
            _redisTls.Checked = _profile.RedisTls;
            _allowInvalidCert = new CheckBox();
            _allowInvalidCert.Text = "允许无效证书";
            _allowInvalidCert.AutoSize = true;
            _allowInvalidCert.Margin = new Padding(18, 0, 0, 0);
            _allowInvalidCert.Checked = _profile.AllowInvalidCert;
            securePanel.Controls.Add(_redisTls);
            securePanel.Controls.Add(_allowInvalidCert);
            root.Controls.Add(securePanel, 1, 9);

            AddLabel(root, "环境", 10);
            _environment = new ComboBox();
            _environment.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 0; i < DatabaseEnvironment.All.Length; i++)
                _environment.Items.Add(DatabaseEnvironment.All[i]);
            _environment.SelectedItem = _profile.Environment;
            if (_environment.SelectedIndex < 0) _environment.SelectedIndex = 0;
            _environment.Dock = DockStyle.Fill;
            _environment.SelectedIndexChanged += delegate { UpdateWriteHint(); };
            root.Controls.Add(_environment, 1, 10);

            AddLabel(root, "写权限", 11);
            FlowLayoutPanel writePanel = new FlowLayoutPanel();
            writePanel.Dock = DockStyle.Fill;
            writePanel.Margin = new Padding(0, 6, 0, 0);
            writePanel.WrapContents = false;
            _allowWrites = new CheckBox();
            _allowWrites.Text = "允许写操作";
            _allowWrites.AutoSize = true;
            _allowWrites.Checked = _profile.AllowWrites;
            _allowWrites.CheckedChanged += delegate { UpdateWriteHint(); };
            writePanel.Controls.Add(_allowWrites);
            root.Controls.Add(writePanel, 1, 11);

            _writeHint = new Label();
            _writeHint.AutoSize = false;
            _writeHint.Dock = DockStyle.Fill;
            _writeHint.ForeColor = AppTheme.WarnColor;
            _writeHint.TextAlign = ContentAlignment.MiddleLeft;
            root.Controls.Add(_writeHint, 1, 12);

            _kindHint = new Label();
            _kindHint.Dock = DockStyle.Fill;
            _kindHint.ForeColor = AppTheme.MutedColor;
            _kindHint.TextAlign = ContentAlignment.MiddleLeft;
            root.SetColumnSpan(_kindHint, 2);
            root.Controls.Add(_kindHint, 0, 13);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 8, 0, 0);

            Button cancel = AppTheme.MakeButton("取消", false, 80);
            cancel.DialogResult = DialogResult.Cancel;
            Button ok = AppTheme.MakeButton("保存", true, 80);
            ok.Click += delegate { TrySave(); };
            Button test = AppTheme.MakeButton("测试连接", false, 96);
            test.Click += delegate { TestConnection(); };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(test);
            root.SetColumnSpan(buttons, 2);
            root.Controls.Add(buttons, 0, 14);

            AcceptButton = ok;
            CancelButton = cancel;
            ApplyKind();
            UpdateWriteHint();
        }

        public DatabaseConnectionProfile Profile
        {
            get { return _profile; }
        }

        private static void AddLabel(TableLayoutPanel panel, string text, int row)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = AppTheme.LabelColor;
            panel.Controls.Add(label, 0, row);
        }

        private static TextBox AddTextBox(TableLayoutPanel panel, int row)
        {
            TextBox box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 6, 0, 0);
            box.BorderStyle = BorderStyle.FixedSingle;
            panel.Controls.Add(box, 1, row);
            return box;
        }

        private void ApplyKind()
        {
            if (_updatingKind) return;
            _updatingKind = true;
            bool redis = _kind.SelectedIndex == 1;
            int previousDefault = redis ? 3306 : 6379;
            if ((int)_port.Value == previousDefault) _port.Value = redis ? 6379 : 3306;
            _database.Enabled = !redis;
            _redisDatabase.Enabled = redis;
            _redisTls.Enabled = redis;
            _sslMode.Enabled = !redis;
            _kindHint.Text = redis
                ? "Redis：支持单机与 Cluster（自动跟随 MOVED/ASK），默认只读。"
                : "MySQL：支持库表浏览、SQL 查询、表格编辑与 CSV 导出，默认只读。";
            _updatingKind = false;
        }

        private void UpdateWriteHint()
        {
            bool production = Convert.ToString(_environment.SelectedItem) == DatabaseEnvironment.Production;
            if (!_allowWrites.Checked)
            {
                _writeHint.Text = "只读模式：SQL 白名单 + 数据库会话只读。";
                _writeHint.ForeColor = AppTheme.MutedColor;
            }
            else if (production)
            {
                _writeHint.Text = "生产环境写权限：保存时需要输入“生产”二次确认。";
                _writeHint.ForeColor = AppTheme.ErrorColor;
            }
            else
            {
                _writeHint.Text = "允许写操作：执行破坏性 SQL 前会再次确认。";
                _writeHint.ForeColor = AppTheme.WarnColor;
            }
        }

        private void TrySave()
        {
            string name = _name.Text.Trim();
            string host = _host.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "请填写连接名称。", "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _name.Focus();
                return;
            }
            if (host.Length == 0)
            {
                MessageBox.Show(this, "请填写主机地址。", "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _host.Focus();
                return;
            }
            if (_allowWrites.Checked && Convert.ToString(_environment.SelectedItem) == DatabaseEnvironment.Production)
            {
                if (!ConfirmProductionWrites(name)) return;
            }

            _profile.Name = name;
            _profile.Kind = _kind.SelectedIndex == 1 ? DatabaseKinds.Redis : DatabaseKinds.MySql;
            _profile.Host = host;
            _profile.Port = (int)_port.Value;
            _profile.User = _user.Text.Trim();
            _profile.Password = _password.Text;
            _profile.Database = _database.Text.Trim();
            _profile.RedisDatabase = (int)_redisDatabase.Value;
            _profile.SslMode = string.IsNullOrEmpty(_sslMode.Text) ? "Preferred" : _sslMode.Text.Trim();
            _profile.RedisTls = _redisTls.Checked;
            _profile.AllowInvalidCert = _allowInvalidCert.Checked;
            _profile.Environment = Convert.ToString(_environment.SelectedItem);
            _profile.AllowWrites = _allowWrites.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        private bool ConfirmProductionWrites(string name)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = "确认生产写权限";
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(460, 190);
                dialog.BackColor = Color.White;
                dialog.Font = AppTheme.UiFont(9F);

                Label label = new Label();
                label.Text = "连接“" + name + "”标记为生产环境，并且开启写权限。\r\n" +
                    "误操作可能直接影响线上数据。请在下框输入“生产”继续：";
                label.ForeColor = AppTheme.ErrorColor;
                label.Location = new Point(20, 16);
                label.Size = new Size(420, 60);
                dialog.Controls.Add(label);

                TextBox input = new TextBox();
                input.Location = new Point(20, 86);
                input.Width = 420;
                input.BorderStyle = BorderStyle.FixedSingle;
                dialog.Controls.Add(input);

                Button cancel = AppTheme.MakeButton("取消", false, 80);
                cancel.Location = new Point(360, 130);
                cancel.DialogResult = DialogResult.Cancel;
                Button ok = AppTheme.MakeButton("确认", true, 80);
                ok.Location = new Point(272, 130);
                ok.DialogResult = DialogResult.None;
                ok.Click += delegate
                {
                    if (input.Text.Trim() != "生产")
                    {
                        MessageBox.Show(dialog, "请输入“生产”两个字。", "确认失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    dialog.DialogResult = DialogResult.OK;
                };
                dialog.Controls.Add(cancel);
                dialog.Controls.Add(ok);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                return dialog.ShowDialog(this) == DialogResult.OK;
            }
        }

        private void TestConnection()
        {
            DatabaseConnectionProfile test = _profile.Clone();
            test.Name = _name.Text.Trim();
            test.Kind = _kind.SelectedIndex == 1 ? DatabaseKinds.Redis : DatabaseKinds.MySql;
            test.Host = _host.Text.Trim();
            test.Port = (int)_port.Value;
            test.User = _user.Text.Trim();
            test.Password = _password.Text;
            test.Database = _database.Text.Trim();
            test.RedisDatabase = (int)_redisDatabase.Value;
            test.SslMode = _sslMode.Text.Trim();
            test.RedisTls = _redisTls.Checked;
            test.AllowInvalidCert = _allowInvalidCert.Checked;
            test.AllowWrites = false;

            if (test.Host.Length == 0)
            {
                MessageBox.Show(this, "请填写主机地址。", "测试连接",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            IDatabaseProvider provider = DatabaseProviders.Find(test.Kind);
            if (provider == null)
            {
                MessageBox.Show(this, "不支持的数据库类型。", "测试连接",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                string error;
                using (IDatabaseConnection connection = provider.Open(test, out error))
                {
                    if (connection == null)
                    {
                        MessageBox.Show(this, error, "连接失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    string version = connection.ServerVersion;
                    MessageBox.Show(this, "连接成功。\r\n" + test.Endpoint +
                        (string.IsNullOrEmpty(version) ? "" : "\r\n" + version),
                        "测试连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            finally
            {
                Cursor = previous;
            }
        }
    }
}
