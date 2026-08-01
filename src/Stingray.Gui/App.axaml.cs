// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Stingray.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            if (Screenshot.Requested)
            {
                window.Opened += async (_, _) =>
                {
                    await window.CaptureScreenshotAsync();

                    // Exit rather than Shutdown: winding the lifetime down stops
                    // the dispatcher while the D-Bus connection is still closing,
                    // and its disconnect handler then posts to a dispatcher that
                    // will never run it — an unhandled TaskCanceledException on a
                    // pool thread, on every run. The image is written and closed
                    // by this point, and this mode exists to render one and
                    // leave, so there is nothing left worth winding down.
                    Environment.Exit(0);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
