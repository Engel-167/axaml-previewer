using Avalonia.Controls;
using Avalonia.Remote.Protocol.Viewport;
using AxamlPreview.Services;
using AxamlPreview.ViewModels;

namespace AxamlPreview.Views;

public partial class PreviewView : UserControl
{
    private MainViewModel? _vm;

    public PreviewView()
    {
        InitializeComponent();
        FileList.SelectionChanged += (_, _) =>
        {
            if (FileList.SelectedItem is FileEntry entry && _vm != null)
            {
                _vm.Select(entry.Path);
                FileButton.Flyout?.Hide();
                FileList.SelectedItem = null;
            }
        };
        if (FileButton.Flyout is { } flyout)
            flyout.Opened += (_, _) => FilterBox.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.Frame -= OnFrame;
            _vm.ResetView -= Surface.Recenter;
            _vm.FitRequested -= Surface.Fit;
            _vm.SessionChanged -= OnSessionChanged;
        }
        _vm = DataContext as MainViewModel;
        if (_vm != null)
        {
            _vm.Frame += OnFrame;
            _vm.ResetView += Surface.Recenter;
            _vm.FitRequested += Surface.Fit;
            _vm.SessionChanged += OnSessionChanged;
        }
    }

    private void OnFrame(FrameMessage frame) => Surface.ShowFrame(frame);

    private void OnSessionChanged(PreviewSession? session) => Surface.Session = session;
}
