using System;
using System.Drawing;
using System.Windows.Forms;

namespace Curl2Url
{
    // 代码框的外壳：留白 + 1px 边框 + 聚焦变色。
    // WinForms 的 TextBox / RichTextBox 只能画系统边框，颜色改不了，
    // 所以把边框交给外层 Panel 自己画。
    internal sealed class CodeBoxHost : Panel
    {
        private readonly TextBoxBase _content;

        public CodeBoxHost(TextBoxBase content, Color back)
        {
            _content = content;

            Dock = DockStyle.Fill;
            Margin = new Padding(0);
            Padding = new Padding(8, 6, 6, 6);
            BackColor = back;
            BorderStyle = BorderStyle.None;
            SetStyle(ControlStyles.ResizeRedraw |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);

            content.BorderStyle = BorderStyle.None;
            content.BackColor = back;
            content.Dock = DockStyle.Fill;
            content.Margin = new Padding(0);
            Controls.Add(content);

            content.GotFocus += delegate { Invalidate(); };
            content.LostFocus += delegate { Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color color = _content.Focused ? AppTheme.PrimaryColor : AppTheme.CodeBorder;
            using (Pen pen = new Pen(color))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }
}
