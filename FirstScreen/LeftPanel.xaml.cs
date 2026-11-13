using System;
using System.Windows;
using System.Windows.Controls;

namespace Configurator
{
    public partial class LeftPanel : UserControl
    {
        public event Action AddDatabaseClicked;

        public LeftPanel()
        {
            InitializeComponent();
        }

        private void BtnAddDb_Click(object sender, RoutedEventArgs e)
        {
            AddDatabaseClicked?.Invoke();
        }

        public void HidePlaceholder()
        {
            if (this.FindName("EmptyTreePlaceholder") is Border placeholder)
            {
                placeholder.Visibility = Visibility.Collapsed;
            }
        }

        public void ShowPlaceholder()
        {
            if (this.FindName("EmptyTreePlaceholder") is Border placeholder)
            {
                placeholder.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Включает или выключает кнопку "Добавить базу".
        /// Вызывается из MainWindow при добавлении/удалении баз.
        /// </summary>

        public void SetLoadingStatus(string text, bool loading)
        {
            if (DbLoadingStatus == null) return;
            DbLoadingStatus.Text = text ?? "";
            DbLoadingStatus.FontWeight = loading ? FontWeights.SemiBold : FontWeights.Normal;
            DbLoadingStatus.SetResourceReference(TextBlock.ForegroundProperty, loading ? "BrushAccent" : "BrushTextSecondary");
        }

        public void SetAddDbButtonText(string text)
        {
            if (BtnAddDb != null) BtnAddDb.Content = text ?? "＋  Добавить базу";
        }

        public void SetAddDbButtonEnabled(bool enabled)
        {
            if (BtnAddDb != null)
            {
                BtnAddDb.IsEnabled = enabled;
                BtnAddDb.Opacity = enabled ? 1.0 : 0.4;
            }
        }

    }
}
