using Avalonia.Controls;
using Avalonia.Input;
using AxamlPreview.ViewModels;

namespace AxamlPreview.Views;

public partial class SettingsSheet : UserControl
{
    public SettingsSheet() => InitializeComponent();

    private void Scrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.IsOpen = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is SettingsViewModel vm)
        {
            vm.IsOpen = false;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
