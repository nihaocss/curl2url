using System;
using System.Drawing;
using System.Windows.Forms;

namespace Curl2Url
{
    // 新功能页共用的视觉常量和小控件工厂；主窗口以后可以逐步迁移到这里。
    internal static class AppTheme
    {
        public static readonly Color PageBack = Color.FromArgb(245, 246, 248);
        public static readonly Color TitleColor = Color.FromArgb(48, 49, 51);
        public static readonly Color LabelColor = Color.FromArgb(96, 98, 102);
        public static readonly Color MutedColor = Color.FromArgb(144, 147, 153);
        public static readonly Color FaintColor = Color.FromArgb(192, 196, 204);
        public static readonly Color LineColor = Color.FromArgb(220, 223, 230);
        public static readonly Color PrimaryColor = Color.FromArgb(64, 158, 255);
        public static readonly Color PrimaryHover = Color.FromArgb(51, 137, 230);
        public static readonly Color OkColor = Color.FromArgb(46, 125, 50);
        public static readonly Color ErrorColor = Color.FromArgb(183, 28, 28);
        public static readonly Color WarnColor = Color.FromArgb(230, 126, 34);
        public static readonly Color HeaderBack = Color.FromArgb(242, 244, 248);
        public static readonly Color ZebraBack = Color.FromArgb(250, 251, 252);
        public static readonly Color RowSelect = Color.FromArgb(230, 240, 255);
        public static readonly Color GridLine = Color.FromArgb(219, 224, 232);
        public static readonly Color CodeText = Color.FromArgb(45, 48, 55);
        public static readonly Color CodeBorder = Color.FromArgb(201, 207, 217);
        public static readonly Color SqlKeyword = Color.FromArgb(31, 95, 191);
        public static readonly Color SqlString = Color.FromArgb(15, 123, 77);
        public static readonly Color SqlNumber = Color.FromArgb(138, 77, 168);
        public static readonly Color SqlComment = Color.FromArgb(139, 149, 165);

        public static Font UiFont(float size)
        {
            return new Font("Microsoft YaHei UI", size, FontStyle.Regular);
        }

        public static Font UiFont(float size, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", size, style);
        }

        public static Font MonoFont(float size)
        {
            return new Font("Consolas", size, FontStyle.Regular);
        }

        public static Label MakeSectionLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.ForeColor = TitleColor;
            label.Font = UiFont(9.5F, FontStyle.Bold);
            label.Margin = new Padding(0, 0, 0, 6);
            return label;
        }

        public static Label MakeHintLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.ForeColor = FaintColor;
            label.Margin = new Padding(6, 8, 0, 0);
            return label;
        }

        public static Button MakeButton(string text, bool primary, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 30;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = primary ? PrimaryColor : LineColor;
            button.BackColor = primary ? PrimaryColor : Color.White;
            button.ForeColor = primary ? Color.White : TitleColor;
            button.Cursor = Cursors.Hand;
            button.Margin = new Padding(0, 0, 8, 0);
            button.UseVisualStyleBackColor = false;
            return button;
        }

        public static Button MakeSmallButton(string text, int width)
        {
            Button button = MakeButton(text, false, width);
            button.Height = 27;
            button.Font = UiFont(8.5F);
            button.Margin = new Padding(0, 0, 6, 0);
            return button;
        }

        public static TextBox MakeTextBox(bool multiline, bool readOnly, Font font)
        {
            TextBox box = new TextBox();
            box.Multiline = multiline;
            box.ReadOnly = readOnly;
            box.Font = font;
            box.BackColor = readOnly ? Color.FromArgb(250, 250, 251) : Color.White;
            box.ForeColor = TitleColor;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0);
            if (multiline)
            {
                box.AcceptsReturn = true;
                box.AcceptsTab = true;
                box.WordWrap = false;
                box.ScrollBars = ScrollBars.Both;
                box.MaxLength = int.MaxValue;
            }
            return box;
        }

        // 数据库页的表格统一走这里：平铺表头、横向分隔线、斑马纹、浅蓝选中。
        // 之前用的是 DataGridView 默认样式，表头是灰色 3D 块，和界面其他部分不搭。
        public static DataGridView MakeGrid()
        {
            DataGridView grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0);
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.Font = MonoFont(9F);
            grid.RowHeadersWidth = 46;
            grid.ColumnHeadersHeight = 32;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToOrderColumns = true;
            grid.AllowUserToResizeRows = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.MultiSelect = true;
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable;
            grid.RowTemplate.Height = 26;

            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.Single;
            grid.GridColor = GridLine;

            grid.ColumnHeadersDefaultCellStyle.BackColor = HeaderBack;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = LabelColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderBack;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = LabelColor;
            grid.ColumnHeadersDefaultCellStyle.Font = UiFont(8.75F, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

            grid.RowHeadersDefaultCellStyle.BackColor = HeaderBack;
            grid.RowHeadersDefaultCellStyle.ForeColor = MutedColor;
            grid.RowHeadersDefaultCellStyle.SelectionBackColor = HeaderBack;
            grid.RowHeadersDefaultCellStyle.SelectionForeColor = MutedColor;
            grid.RowHeadersDefaultCellStyle.Font = UiFont(8F);
            grid.RowHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            grid.DefaultCellStyle.BackColor = Color.White;
            grid.DefaultCellStyle.ForeColor = TitleColor;
            grid.DefaultCellStyle.SelectionBackColor = RowSelect;
            grid.DefaultCellStyle.SelectionForeColor = TitleColor;
            grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = ZebraBack;
            return grid;
        }

        // WinForms 的编辑框没有内边距，代码贴边很难看；外面套一层带留白和自绘边框的 Panel。
        public static Panel WrapCodeBox(TextBoxBase box, Color back)
        {
            return new CodeBoxHost(box, back);
        }

        public static string EnvColorName(string environment)
        {
            if (environment == DatabaseEnvironment.Production) return "生产";
            if (environment == DatabaseEnvironment.Staging) return "预发";
            if (environment == DatabaseEnvironment.Test) return "测试";
            return "本地";
        }
    }
}
