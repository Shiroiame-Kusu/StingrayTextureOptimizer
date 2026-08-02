// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Stingray.Gui.Localization;

namespace Stingray.Gui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Before anything is constructed: the window resolves its strings as it
        // is built, so the language has to be settled first.
        Language.Detect();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(Screenshot.Parse(args));
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
