using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Curl2Url
{
    internal static class DataGridExport
    {
        public static string CellText(object value)
        {
            if (value == null || value == DBNull.Value) return "NULL";
            DbBinaryValue binary = value as DbBinaryValue;
            if (binary != null) return binary.ToString();
            DbTruncatedText truncated = value as DbTruncatedText;
            if (truncated != null) return truncated.ToString();
            byte[] bytes = value as byte[];
            if (bytes != null) return "<BLOB " + bytes.Length + " bytes>";
            DateTime time = default(DateTime);
            if (value is DateTime)
            {
                time = (DateTime)value;
                return time.ToString("yyyy-MM-dd HH:mm:ss.fff");
            }
            return Convert.ToString(value);
        }

        public static string ToCsv(DataTable table, bool includeHeaders)
        {
            if (table == null) return "";
            StringBuilder sb = new StringBuilder();
            if (includeHeaders)
            {
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(CsvField(table.Columns[i].ColumnName));
                }
                sb.Append("\r\n");
            }
            for (int r = 0; r < table.Rows.Count; r++)
            {
                for (int c = 0; c < table.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(CsvField(CellText(table.Rows[r][c])));
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        public static string ToTsv(DataTable table, bool includeHeaders)
        {
            if (table == null) return "";
            StringBuilder sb = new StringBuilder();
            if (includeHeaders)
            {
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    if (i > 0) sb.Append('\t');
                    sb.Append(TsvField(table.Columns[i].ColumnName));
                }
                sb.Append("\r\n");
            }
            for (int r = 0; r < table.Rows.Count; r++)
            {
                for (int c = 0; c < table.Columns.Count; c++)
                {
                    if (c > 0) sb.Append('\t');
                    sb.Append(TsvField(CellText(table.Rows[r][c])));
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        public static bool SaveCsv(DataTable table, string path, out string error)
        {
            error = "";
            if (table == null)
            {
                error = "没有可导出的结果。";
                return false;
            }
            try
            {
                File.WriteAllText(path, ToCsv(table, true), new UTF8Encoding(true));
                return true;
            }
            catch (Exception ex)
            {
                error = "导出 CSV 失败：" + ex.Message;
                return false;
            }
        }

        public static string SelectedCellsToTsv(DataGridView grid, bool includeHeaders)
        {
            if (grid == null || grid.DataSource == null) return "";
            DataTable table = grid.DataSource as DataTable;
            if (table == null) return "";

            List<int> rows = new List<int>();
            List<int> columns = new List<int>();
            foreach (DataGridViewCell cell in grid.SelectedCells)
            {
                if (cell.RowIndex < 0 || cell.ColumnIndex < 0) continue;
                if (!rows.Contains(cell.RowIndex)) rows.Add(cell.RowIndex);
                if (!columns.Contains(cell.ColumnIndex)) columns.Add(cell.ColumnIndex);
            }
            if (rows.Count == 0 && grid.CurrentCell != null)
            {
                rows.Add(grid.CurrentCell.RowIndex);
                columns.Add(grid.CurrentCell.ColumnIndex);
            }
            rows.Sort();
            columns.Sort();

            StringBuilder sb = new StringBuilder();
            if (includeHeaders)
            {
                for (int i = 0; i < columns.Count; i++)
                {
                    if (i > 0) sb.Append('\t');
                    sb.Append(TsvField(grid.Columns[columns[i]].HeaderText));
                }
                sb.Append("\r\n");
            }
            for (int r = 0; r < rows.Count; r++)
            {
                int rowIndex = rows[r];
                if (rowIndex < 0 || rowIndex >= table.Rows.Count) continue;
                for (int c = 0; c < columns.Count; c++)
                {
                    if (c > 0) sb.Append('\t');
                    object value = table.Rows[rowIndex][columns[c]];
                    sb.Append(TsvField(CellText(value)));
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        public static void CopyText(string text, Action<string, Color> status)
        {
            if (string.IsNullOrEmpty(text))
            {
                if (status != null) status("没有可复制的内容。", AppTheme.MutedColor);
                return;
            }
            try
            {
                Clipboard.SetText(text);
                if (status != null) status("已复制 " + text.Length + " 个字符。", AppTheme.OkColor);
            }
            catch (Exception ex)
            {
                if (status != null) status("复制失败：" + ex.Message, AppTheme.ErrorColor);
            }
        }

        public static void AttachGridMenu(DataGridView grid, Action<string, Color> status)
        {
            if (grid == null) return;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("复制单元格", null, delegate { CopyCurrentCell(grid, status); });
            menu.Items.Add("复制当前行", null, delegate { CopyCurrentRow(grid, status); });
            menu.Items.Add("复制当前列", null, delegate { CopyCurrentColumn(grid, status); });
            menu.Items.Add("复制选中区域", null, delegate
            {
                CopyText(SelectedCellsToTsv(grid, false), status);
            });
            menu.Items.Add("复制全部（含表头）", null, delegate
            {
                DataTable table = grid.DataSource as DataTable;
                CopyText(ToTsv(table, true), status);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("全选", null, delegate { grid.SelectAll(); });
            menu.Items.Add("导出 CSV…", null, delegate { ExportCsv(grid, status); });
            grid.ContextMenuStrip = menu;

            grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (e.Value == null || e.Value == DBNull.Value)
                {
                    e.Value = "NULL";
                    e.CellStyle.ForeColor = AppTheme.FaintColor;
                    e.FormattingApplied = true;
                    return;
                }
                DbBinaryValue binary = e.Value as DbBinaryValue;
                if (binary != null)
                {
                    e.Value = binary.HexPreview(24);
                    e.FormattingApplied = true;
                    return;
                }
                DbTruncatedText truncated = e.Value as DbTruncatedText;
                if (truncated != null)
                {
                    string preview = truncated.Preview;
                    if (preview.Length > 160) preview = preview.Substring(0, 160) + "…";
                    e.Value = preview;
                    e.FormattingApplied = true;
                }
            };
            grid.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                object value = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
                string text = CellText(value);
                if (text.Length > 2000) text = text.Substring(0, 2000) + "…";
                e.ToolTipText = text;
            };
            grid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                object value = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
                if (value is DbBinaryValue || value is DbTruncatedText || value is byte[])
                {
                    e.Cancel = true;
                    if (status != null) status("二进制或超大字段不能在表格里编辑，请用 SQL 控制台。", AppTheme.WarnColor);
                }
            };
            grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e)
            {
                e.ThrowException = false;
                if (status != null) status("单元格显示失败：" + e.Exception.Message, AppTheme.ErrorColor);
            };
            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (!e.Control || e.KeyCode != Keys.C || grid.IsCurrentCellInEditMode) return;
                string text = SelectedCellsToTsv(grid, false);
                if (string.IsNullOrEmpty(text)) return;
                CopyText(text, status);
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
        }

        public static void AutoSizeColumns(DataGridView grid, int maxWidth)
        {
            if (grid == null) return;
            for (int i = 0; i < grid.Columns.Count; i++)
            {
                grid.Columns[i].AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
                int width = grid.Columns[i].Width;
                if (width > maxWidth) grid.Columns[i].Width = maxWidth;
                if (width < 70) grid.Columns[i].Width = 70;
            }
        }

        private static void CopyCurrentCell(DataGridView grid, Action<string, Color> status)
        {
            if (grid.CurrentCell == null) return;
            object value = grid.Rows[grid.CurrentCell.RowIndex].Cells[grid.CurrentCell.ColumnIndex].Value;
            CopyText(CellText(value), status);
        }

        private static void CopyCurrentRow(DataGridView grid, Action<string, Color> status)
        {
            if (grid.CurrentRow == null) return;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < grid.Columns.Count; i++)
            {
                if (i > 0) sb.Append('\t');
                sb.Append(TsvField(CellText(grid.Rows[grid.CurrentRow.Index].Cells[i].Value)));
            }
            CopyText(sb.ToString(), status);
        }

        private static void CopyCurrentColumn(DataGridView grid, Action<string, Color> status)
        {
            if (grid.CurrentCell == null) return;
            int column = grid.CurrentCell.ColumnIndex;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(grid.Columns[column].HeaderText);
            for (int r = 0; r < grid.Rows.Count; r++)
            {
                sb.AppendLine(CellText(grid.Rows[r].Cells[column].Value));
            }
            CopyText(sb.ToString(), status);
        }

        private static void ExportCsv(DataGridView grid, Action<string, Color> status)
        {
            DataTable table = grid.DataSource as DataTable;
            if (table == null)
            {
                if (status != null) status("当前没有结果可导出。", AppTheme.MutedColor);
                return;
            }
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "导出查询结果";
                dialog.Filter = "CSV 文件|*.csv|所有文件|*.*";
                dialog.FileName = "query-result-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv";
                if (dialog.ShowDialog() != DialogResult.OK) return;
                string error;
                if (SaveCsv(table, dialog.FileName, out error))
                {
                    if (status != null) status("已导出：" + dialog.FileName, AppTheme.OkColor);
                }
                else if (status != null)
                {
                    status(error, AppTheme.ErrorColor);
                }
            }
        }

        private static string CsvField(string value)
        {
            if (value == null) return "\"\"";
            bool needQuote = value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0 ||
                value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0;
            string text = value.Replace("\"", "\"\"");
            return needQuote ? "\"" + text + "\"" : text;
        }

        private static string TsvField(string value)
        {
            if (value == null) return "";
            return value.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
