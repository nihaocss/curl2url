using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Curl2Url
{
    // 功能页注册点：主窗口只认这个接口，以后加新页签不用再往 MainForm 里塞 UI 代码。
    internal sealed class AppFeatureContext
    {
        public IWin32Window Owner;
        public Action<string, Color> SetStatus;
        public Func<string> GetLogText;
        public Func<AiOcrSettings> LoadAiSettings;
        public Action ShowAiSettings;
    }

    internal interface IAppFeature
    {
        string Id { get; }
        string Title { get; }
        bool UsesFullHeight { get; }
        Control CreatePage(AppFeatureContext context);
    }

    // 功能页可选实现：主窗口会把快捷键和关闭确认转交给当前页。
    internal interface IAppFeaturePage
    {
        bool HandleShortcut(Keys keyData);
        bool ConfirmClose();
        void Shutdown();
    }

    internal static class AppFeatures
    {
        public static List<IAppFeature> CreateAll()
        {
            List<IAppFeature> features = new List<IAppFeature>();
            features.Add(new DatabaseFeature());
            return features;
        }
    }
}
