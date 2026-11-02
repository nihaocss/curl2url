using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Curl2Url
{
    // 主窗口只负责把「功能页」挂到页签上；具体功能由 AppFeatures 注册。
    // 新增功能页时实现 IAppFeature 并注册即可，不用再改主窗口。
    internal sealed partial class MainForm
    {
        private readonly Dictionary<TabPage, IAppFeature> _featureTabs =
            new Dictionary<TabPage, IAppFeature>();
        private readonly Dictionary<TabPage, Control> _featurePages =
            new Dictionary<TabPage, Control>();

        private TabPage _databaseTab;
        private DatabasePage _databasePage;

        private AppFeatureContext CreateFeatureContext()
        {
            AppFeatureContext context = new AppFeatureContext();
            context.Owner = this;
            context.SetStatus = new Action<string, Color>(SetStatus);
            context.GetLogText = delegate { return _logInput == null ? "" : _logInput.Text; };
            context.LoadAiSettings = delegate
            {
                if (_aiSettings == null) _aiSettings = AiOcrSettings.Load();
                return _aiSettings;
            };
            context.ShowAiSettings = delegate { ShowAiSettings(); };
            return context;
        }

        // 统一追加所有已注册的功能页；单个功能初始化失败不影响其他页签。
        private void AppendFeatureTabs(TabControl tabs)
        {
            if (tabs == null) return;
            AppFeatureContext context = CreateFeatureContext();
            List<IAppFeature> features = AppFeatures.CreateAll();

            for (int i = 0; i < features.Count; i++)
            {
                IAppFeature feature = features[i];
                if (feature == null || string.IsNullOrEmpty(feature.Id)) continue;

                TabPage tab = new TabPage(string.IsNullOrEmpty(feature.Title) ? feature.Id : feature.Title);
                tab.BackColor = PageBack;
                tab.Padding = new Padding(6);

                try
                {
                    Control page = feature.CreatePage(context);
                    if (page == null)
                    {
                        tab.Controls.Add(BuildFeatureFallback("功能没有返回页面：" + feature.Id, MutedColor));
                        continue;
                    }
                    page.Dock = DockStyle.Fill;
                    tab.Controls.Add(page);
                    _featureTabs[tab] = feature;
                    _featurePages[tab] = page;

                    if (feature.Id == "database")
                    {
                        _databasePage = page as DatabasePage;
                    }
                }
                catch (Exception ex)
                {
                    tab.Controls.Add(BuildFeatureFallback(
                        "功能页初始化失败：\r\n" + ex.Message, ErrorColor));
                }
                finally
                {
                    tabs.TabPages.Add(tab);
                    if (feature.Id == "database") _databaseTab = tab;
                }
            }
        }

        private static Label BuildFeatureFallback(string text, Color color)
        {
            Label label = new Label();
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.ForeColor = color;
            label.Text = text;
            return label;
        }

        private Control ActiveFeaturePage()
        {
            if (_tabs == null || _tabs.SelectedTab == null) return null;
            Control page;
            return _featurePages.TryGetValue(_tabs.SelectedTab, out page) ? page : null;
        }

        private bool ActiveFeatureUsesFullHeight()
        {
            if (_tabs == null || _tabs.SelectedTab == null) return false;
            IAppFeature feature;
            return _featureTabs.TryGetValue(_tabs.SelectedTab, out feature) &&
                feature.UsesFullHeight;
        }

        private bool ConfirmFeatureClose()
        {
            foreach (Control page in _featurePages.Values)
            {
                IAppFeaturePage featurePage = page as IAppFeaturePage;
                if (featurePage != null && !featurePage.ConfirmClose()) return false;
            }
            return true;
        }

        private void ShutdownFeaturePages()
        {
            foreach (Control page in _featurePages.Values)
            {
                IAppFeaturePage featurePage = page as IAppFeaturePage;
                if (featurePage != null)
                {
                    try { featurePage.Shutdown(); }
                    catch (Exception) { }
                }
            }
        }
    }
}
