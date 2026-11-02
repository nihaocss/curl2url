using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Curl2Url
{
    // 驱动层只负责“连上、查询、断开”；UI 层通过工作区接口接管具体展示。
    // 以后新增 PostgreSQL / MongoDB / Kafka 时，实现这两个接口并注册到 DatabaseProviders 即可。
    internal interface IDatabaseConnection : IDisposable
    {
        string Kind { get; }
        string Endpoint { get; }
        string ServerVersion { get; }
        bool AllowWrites { get; }
        void Close();
    }

    internal interface IDatabaseWorkspace : IDisposable
    {
        Control View { get; }
        bool HandleShortcut(Keys keyData);
        void ApplyLogClue(LogClue clue);
        bool ConfirmClose();
        void Close();
    }

    internal interface IDatabaseProvider
    {
        string Kind { get; }
        string DisplayName { get; }
        int DefaultPort { get; }
        bool SupportsWrites { get; }
        IDatabaseConnection Open(DatabaseConnectionProfile profile, out string error);
        IDatabaseWorkspace CreateWorkspace(DatabaseTabHost host,
            DatabaseConnectionProfile profile, IDatabaseConnection connection);
    }

    internal static class DatabaseProviders
    {
        private static readonly List<IDatabaseProvider> Providers = new List<IDatabaseProvider>
        {
            new MySqlProvider(),
            new RedisProvider()
        };

        public static List<IDatabaseProvider> All
        {
            get { return Providers; }
        }

        public static IDatabaseProvider Find(string kind)
        {
            for (int i = 0; i < Providers.Count; i++)
            {
                if (string.Equals(Providers[i].Kind, kind, StringComparison.OrdinalIgnoreCase))
                    return Providers[i];
            }
            return null;
        }

        public static IDatabaseProvider FindByDisplayName(string name)
        {
            for (int i = 0; i < Providers.Count; i++)
            {
                if (string.Equals(Providers[i].DisplayName, name, StringComparison.OrdinalIgnoreCase))
                    return Providers[i];
            }
            return null;
        }
    }

    internal sealed class DatabaseTabHost
    {
        public IWin32Window Owner;
        public Action<string, Color> SetStatus;
        public Func<string> GetLogText;
        public Func<AiOcrSettings> LoadAiSettings;
        public Action ShowAiSettings;
        // 工作区切换数据库时回调，由外层把“最近使用的库”写回连接配置。
        public Action<string> RememberDatabase;

        public void Status(string text)
        {
            if (SetStatus != null) SetStatus(text, AppTheme.MutedColor);
        }

        public void Status(string text, Color color)
        {
            if (SetStatus != null) SetStatus(text, color);
        }

        public bool ConfirmWrite(DatabaseConnectionProfile profile, string title, string summary)
        {
            string target = "目标：" + (profile == null ? "未知连接" : profile.Name) +
                "\r\n环境：" + (profile == null ? "未知" : profile.Environment) +
                "\r\n地址：" + (profile == null ? "" : profile.Endpoint) +
                "\r\n\r\n" + summary + "\r\n\r\n确认继续？";
            DialogResult result = MessageBox.Show(Owner, target, title,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return result == DialogResult.OK;
        }
    }
}
