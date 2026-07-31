// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core;
using Xunit;

namespace Stingray.Core.Tests;

public class BuildInfoTests
{
    [Fact]
    public void VersionIsPresentAndClean()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Version));
        Assert.NotEqual("unknown", BuildInfo.Version);
        // SourceLink's "+<sha>" suffix is stripped; it is noise in a title bar.
        Assert.DoesNotContain('+', BuildInfo.Version);
    }

    [Fact]
    public void ProductAndVersionReadsAsATitle()
    {
        Assert.StartsWith("Stingray Texture Optimizer ", BuildInfo.ProductAndVersion);
        Assert.EndsWith(BuildInfo.Version, BuildInfo.ProductAndVersion);
    }
}
