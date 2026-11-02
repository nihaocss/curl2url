using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Curl2Url
{
    // MySQL 语法高亮的代码框。
    //
    // 为什么撤消要自己实现：RichTextBox 的着色操作（SelectionColor）会混进原生
    // 撤消栈，按一次 Ctrl+Z 只会撤掉颜色、文本纹丝不动，要按两次才生效。
    // 所以这里干脆不用原生撤消，改成维护文本快照；着色本身不经过 Text。
    internal sealed class SqlHighlightBox : RichTextBox
    {
        private const int WmSetRedraw = 0x000B;
        private const int MaxHighlightLength = 200000;
        private const int MaxHistory = 200;

        private readonly Timer _highlightTimer;
        private readonly List<string> _undoText = new List<string>();
        private readonly List<string> _redoText = new List<string>();
        private string _lastText = "";
        private string _lastHighlightedText = "";
        private DateTime _lastSnapshot = DateTime.MinValue;
        private bool _restoring;
        private bool _highlighting;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public SqlHighlightBox()
        {
            BorderStyle = BorderStyle.None;
            BackColor = Color.White;
            ForeColor = AppTheme.CodeText;
            Font = AppTheme.MonoFont(10.5F);
            WordWrap = true;
            ScrollBars = RichTextBoxScrollBars.Both;
            AcceptsTab = true;
            MaxLength = int.MaxValue;
            DetectUrls = false;
            ShowSelectionMargin = false;

            _highlightTimer = new Timer();
            _highlightTimer.Interval = 120;
            _highlightTimer.Tick += delegate
            {
                _highlightTimer.Stop();
                HighlightNow();
            };
            TextChanged += OnTextChangedInternal;
            BuildContextMenu();
        }

        // RichTextBox 自带的右键菜单在不同系统上表现不一致，这里自己挂一个，
        // 保证「复制 / 全选 / 粘贴」随时可用（只读框里只有复制和全选）。
        private void BuildContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = AppTheme.UiFont(9F);

            ToolStripMenuItem copy = new ToolStripMenuItem("复制");
            copy.ShortcutKeyDisplayString = "Ctrl+C";
            copy.Click += delegate { CopySelectionOrAll(); };

            ToolStripMenuItem selectAll = new ToolStripMenuItem("全选");
            selectAll.ShortcutKeyDisplayString = "Ctrl+A";
            selectAll.Click += delegate { SelectAll(); };

            ToolStripMenuItem paste = new ToolStripMenuItem("粘贴");
            paste.ShortcutKeyDisplayString = "Ctrl+V";
            paste.Click += delegate { Paste(); };

            menu.Items.Add(copy);
            menu.Items.Add(selectAll);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(paste);
            menu.Opening += delegate
            {
                paste.Enabled = !ReadOnly && Clipboard.ContainsText();
            };
            ContextMenuStrip = menu;
        }

        private void CopySelectionOrAll()
        {
            string text = SelectionLength > 0 ? SelectedText : Text;
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception)
            {
                // 剪贴板被别的程序占用时忽略，用户再点一次即可。
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // TextBox 系控件在 ProcessCmdKey 里就消化了 Ctrl+Z / Ctrl+Y，
            // 走 KeyDown 拦不住，必须在这里截。
            if (!ReadOnly)
            {
                if (keyData == (Keys.Control | Keys.Z))
                {
                    UndoText();
                    return true;
                }
                if (keyData == (Keys.Control | Keys.Y) ||
                    keyData == (Keys.Control | Keys.Shift | Keys.Z))
                {
                    RedoText();
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // 自己接管撤消/重做，避免原生撤消先撤颜色。
            if (!ReadOnly && e.Control && !e.Alt)
            {
                if (!e.Shift && e.KeyCode == Keys.Z)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    UndoText();
                    return;
                }
                if (e.KeyCode == Keys.Y || (e.Shift && e.KeyCode == Keys.Z))
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    RedoText();
                    return;
                }
            }
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _highlightTimer != null) _highlightTimer.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 控件在隐藏的 TabPage 里时 Text 能存住但不触发 TextChanged，
            // 所以等窗口真正建好（切到表结构页）时补一次着色。
            //
            // 同一个原因：构造时赋的初始 SQL 没进过 Undo 栈，要把当前文本
            // 当作基线记下来。否则第一次 Ctrl+Z 会把预置的 "select 1 as ping;"
            // 撤成空字符串（栈里那份快照是构造时的 ""）。
            if (_undoText.Count == 0 && !string.Equals(Text, _lastText, StringComparison.Ordinal))
            {
                _lastText = Text;
                _redoText.Clear();
            }
            _highlightTimer.Stop();
            _highlightTimer.Start();
        }

        private void OnTextChangedInternal(object sender, EventArgs e)
        {
            // RichTextBox 连"改颜色"也会触发 TextChanged；不拦住就会
            // 着色 -> TextChanged -> 再着色 无限循环。
            if (_highlighting) return;
            if (string.Equals(Text, _lastHighlightedText, StringComparison.Ordinal)) return;

            if (!_restoring)
            {
                string text = Text;
                if (!string.Equals(text, _lastText, StringComparison.Ordinal))
                {
                    // 连续单字符输入/删除合并成一步撤消；其余任何改动（粘贴、
                    // 整段替换、格式化）都单独记快照。判断"单字符"必须比对内容，
                    // 不能只看长度差——整段替换也可能正好差 1 个字符，早先按长度
                    // 判断就把 "OLD TEXT;" 换成 "NEW QUERY;" 当成了继续打字，
                    // 导致 Ctrl+Z 直接跳回初始空文本。
                    bool typing = (DateTime.Now - _lastSnapshot).TotalMilliseconds < 1000 &&
                                  IsSingleCharEdit(_lastText, text);
                    if (!typing)
                    {
                        _undoText.Add(_lastText);
                        if (_undoText.Count > MaxHistory) _undoText.RemoveAt(0);
                        _redoText.Clear();
                    }
                    _lastText = text;
                    _lastSnapshot = DateTime.Now;
                }
            }
            _highlightTimer.Stop();
            _highlightTimer.Start();
        }

        private void UndoText()
        {
            if (_undoText.Count == 0) return;
            int caret = SelectionStart;
            _redoText.Add(Text);
            string text = _undoText[_undoText.Count - 1];
            _undoText.RemoveAt(_undoText.Count - 1);
            ApplyHistory(text, caret);
        }

        private void RedoText()
        {
            if (_redoText.Count == 0) return;
            int caret = SelectionStart;
            _undoText.Add(Text);
            string text = _redoText[_redoText.Count - 1];
            _redoText.RemoveAt(_redoText.Count - 1);
            ApplyHistory(text, caret);
        }

        private void ApplyHistory(string text, int caret)
        {
            _restoring = true;
            try
            {
                Text = text;
                _lastText = text;
                _lastSnapshot = DateTime.MinValue;
                int pos = Math.Max(0, Math.Min(caret, TextLength));
                SelectionStart = pos;
                SelectionLength = 0;
            }
            finally
            {
                _restoring = false;
            }
            HighlightNow();
            if (Focused) ScrollToCaret();
        }

        private void HighlightNow()
        {
            if (_highlighting || IsDisposed) return;
            string text = Text;
            _lastHighlightedText = text;
            if (text.Length == 0 || text.Length > MaxHighlightLength) return;

            _highlighting = true;
            int selStart = SelectionStart;
            int selLength = SelectionLength;
            bool focused = Focused;
            SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            try
            {
                SelectAll();
                SelectionColor = AppTheme.CodeText;
                ResetSelection();

                int i = 0;
                while (i < text.Length)
                {
                    char c = text[i];

                    if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                    {
                        int end = text.IndexOfAny(new char[] { '\r', '\n' }, i);
                        if (end < 0) end = text.Length;
                        ColorRange(i, end - i, AppTheme.SqlComment);
                        i = end;
                    }
                    else if (c == '#')
                    {
                        int end = text.IndexOfAny(new char[] { '\r', '\n' }, i);
                        if (end < 0) end = text.Length;
                        ColorRange(i, end - i, AppTheme.SqlComment);
                        i = end;
                    }
                    else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                    {
                        int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        end = end < 0 ? text.Length : end + 2;
                        ColorRange(i, end - i, AppTheme.SqlComment);
                        i = end;
                    }
                    else if (c == '\'' || c == '"')
                    {
                        int end = ScanQuoted(text, i, c);
                        ColorRange(i, end - i, AppTheme.SqlString);
                        i = end;
                    }
                    else if (c == '`')
                    {
                        // 反引号里的内容一律当标识符，不参与关键字着色。
                        int end = ScanQuoted(text, i, c);
                        i = end;
                    }
                    else if (char.IsLetter(c) || c == '_')
                    {
                        int start = i;
                        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '$')) i++;
                        string word = text.Substring(start, i - start);
                        if (SqlKeywords.Set.Contains(word)) ColorRange(start, i - start, AppTheme.SqlKeyword);
                    }
                    else if (char.IsDigit(c))
                    {
                        int start = i;
                        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                        ColorRange(start, i - start, AppTheme.SqlNumber);
                    }
                    else
                    {
                        i++;
                    }
                }
            }
            finally
            {
                SendMessage(Handle, WmSetRedraw, (IntPtr)1, IntPtr.Zero);
                Invalidate();
                _highlighting = false;
            }

            int pos = Math.Max(0, Math.Min(selStart, TextLength));
            int len = Math.Max(0, Math.Min(selLength, TextLength - pos));
            Select(pos, len);
            if (focused) ScrollToCaret();
        }

        // 新旧文本只差一个字符的纯插入/删除才算"打字"。
        // 整段替换哪怕长度只差 1，也会在前缀/后缀比对里露馅。
        private static bool IsSingleCharEdit(string oldText, string newText)
        {
            if (newText.Length == oldText.Length + 1)
            {
                int i = 0;
                while (i < oldText.Length && oldText[i] == newText[i]) i++;
                return i == oldText.Length ||
                       string.CompareOrdinal(oldText, i, newText, i + 1, oldText.Length - i) == 0;
            }
            if (oldText.Length == newText.Length + 1)
            {
                int i = 0;
                while (i < newText.Length && oldText[i] == newText[i]) i++;
                return i == newText.Length ||
                       string.CompareOrdinal(oldText, i + 1, newText, i, newText.Length - i) == 0;
            }
            return false;
        }

        private static int ScanQuoted(string text, int start, char quote)
        {
            int i = start + 1;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    i += 2;
                    continue;
                }
                if (c == quote)
                {
                    // '' / "" / `` 是转义写法，不是结束
                    if (i + 1 < text.Length && text[i + 1] == quote)
                    {
                        i += 2;
                        continue;
                    }
                    return i + 1;
                }
                i++;
            }
            return text.Length;
        }

        private void ColorRange(int start, int length, Color color)
        {
            if (length <= 0 || start < 0 || start >= TextLength) return;
            if (start + length > TextLength) length = TextLength - start;
            Select(start, length);
            SelectionColor = color;
        }

        // Select 之后光标会停在最后一次着色处，先收回到 0，避免中途触发滚动。
        private void ResetSelection()
        {
            SelectionStart = 0;
            SelectionLength = 0;
        }
    }
}
