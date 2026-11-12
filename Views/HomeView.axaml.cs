using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AxamlPreview.ViewModels;

namespace AxamlPreview.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private async void PickProject_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick a project or solution",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Projects and solutions") { Patterns = ["*.slnx", "*.sln", "*.csproj"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.OpenAsync(path);
    }
}
