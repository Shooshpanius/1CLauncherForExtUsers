using Avalonia.Controls;
using Avalonia.Interactivity;
using System.IO;

namespace _1CLauncher.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private async void BrowsePlatform_Click(object? sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Filters.Add(new FileDialogFilter { Name = "Executables", Extensions = { "exe" } });
            dlg.AllowMultiple = false;

            var res = await dlg.ShowAsync(this);
            if (res != null && res.Length > 0)
            {
                var path = res[0];
                if (DataContext is _1CLauncher.ViewModels.MainWindowViewModel vm)
                {
                    vm.AddPlatformFromPath(path);
                }
            }
        }

        private void RefreshPlatforms_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is _1CLauncher.ViewModels.MainWindowViewModel vm)
            {
                vm.RefreshPlatforms();
            }
        }
    }
}