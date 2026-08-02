// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Stingray.Gui.ViewModels;

namespace Stingray.Gui;

/// <summary>
/// Renders the window to a PNG for the documentation.
/// </summary>
/// <remarks>
/// The app draws itself rather than anything capturing the desktop, so the
/// images are reproducible, contain nothing but this window, and can be
/// regenerated when the UI changes.
/// </remarks>
internal static class Screenshot
{
    public static string? OutputPath { get; private set; }
    public static string? BundlePath { get; private set; }
    public static string? ScanPath { get; private set; }
    public static int SizeCap { get; private set; }
    public static int StreamFloor { get; private set; }
    public static bool Requested => OutputPath is not null;

    /// <summary>Pulls the screenshot arguments out, returning the rest.</summary>
    public static string[] Parse(string[] args)
    {
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--screenshot" when i + 1 < args.Length:
                    OutputPath = args[++i];
                    break;
                case "--bundle" when i + 1 < args.Length:
                    BundlePath = args[++i];
                    break;
                case "--scan" when i + 1 < args.Length:
                    ScanPath = args[++i];
                    break;
                case "--cap" when i + 1 < args.Length:
                    SizeCap = int.TryParse(args[++i], out var cap) ? cap : 0;
                    break;
                case "--stream" when i + 1 < args.Length:
                    StreamFloor = int.TryParse(args[++i], out var floor) ? floor : 0;
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        return [.. rest];
    }

    public static async Task CaptureAsync(MainWindow window, MainWindowViewModel viewModel)
    {
        if (OutputPath is null) return;

        if (SizeCap > 0)
        {
            var choice = viewModel.SizeCaps.FirstOrDefault(c => c.Value == SizeCap);
            if (choice is not null) viewModel.SizeCap = choice;
        }

        if (StreamFloor > 0)
        {
            var floor = viewModel.StreamFloors.FirstOrDefault(f => f.Value == StreamFloor);
            if (floor is not null) viewModel.StreamFloor = floor;
        }

        if (ScanPath is not null)
            await viewModel.ScanAsync(ScanPath);

        if (BundlePath is not null)
            await viewModel.LoadAsync(BundlePath);

        // Let layout and the first render settle before drawing to a bitmap;
        // without this the grid can come out empty.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(700);

        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
        if (size.Width <= 0 || size.Height <= 0)
            size = new PixelSize((int)window.Width, (int)window.Height);

        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputPath))!);
        bitmap.Save(OutputPath);
        Console.WriteLine($"wrote {OutputPath} ({size.Width}x{size.Height})");
    }
}
