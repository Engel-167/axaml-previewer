using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AxamlPreview.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Development aid: AXAML_PREVIEW_SNAPSHOT=/path/shot.png saves the window every 3 s (shot-1.png, shot-2.png…).
        if (Environment.GetEnvironmentVariable("AXAML_PREVIEW_SNAPSHOT") is { Length: > 0 } path)
        {
            var count = 0;
            DispatcherTimer.Run(() =>
            {
                var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                if (size.Width > 0)
                {
                    using var bitmap = new RenderTargetBitmap(size);
                    bitmap.Render(this);
#pragma warning disable CS0618 // the encoder-options overload adds nothing for a debug PNG
                    bitmap.Save(Path.ChangeExtension(path, null) + $"-{++count}.png");
#pragma warning restore CS0618
                }
                return count < 40;
            }, TimeSpan.FromSeconds(3));
        }
    }
}
