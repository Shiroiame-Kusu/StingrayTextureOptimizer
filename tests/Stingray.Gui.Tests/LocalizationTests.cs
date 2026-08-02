// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using Stingray.Gui.Localization;
using Xunit;

// The language is a static, so tests that change it must not run beside tests
// that read it. The suite is small enough that serialising it costs nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Stingray.Gui.Tests;

public class LocalizationTests
{
    /// <summary>Every parameterless string on the table, for sweeping both languages.</summary>
    private static IEnumerable<PropertyInfo> Strings =>
        typeof(S).GetProperties(BindingFlags.Public | BindingFlags.Static)
                 .Where(p => p.PropertyType == typeof(string));

    private static T WithLanguage<T>(AppLanguage language, Func<T> read)
    {
        var previous = Language.Current;
        try
        {
            Language.Current = language;
            return read();
        }
        finally
        {
            Language.Current = previous;
        }
    }

    [Theory]
    [InlineData("zh_CN.UTF-8", AppLanguage.ChineseSimplified)]
    [InlineData("zh-CN", AppLanguage.ChineseSimplified)]
    [InlineData("zh", AppLanguage.ChineseSimplified)]
    [InlineData("zh_TW.UTF-8", AppLanguage.ChineseSimplified)]
    [InlineData("zh-Hans", AppLanguage.ChineseSimplified)]
    [InlineData("zh_HK@radical", AppLanguage.ChineseSimplified)]
    [InlineData("ZH_cn", AppLanguage.ChineseSimplified)]
    [InlineData("en_US.UTF-8", AppLanguage.English)]
    [InlineData("en_GB", AppLanguage.English)]
    [InlineData("ja_JP.UTF-8", AppLanguage.English)]
    [InlineData("zhosaurus", AppLanguage.English)]   // must match the tag, not a prefix
    [InlineData("", AppLanguage.English)]
    [InlineData(null, AppLanguage.English)]
    public void LocaleTagsMapToALanguage(string? tag, AppLanguage expected) =>
        Assert.Equal(expected, Language.FromTag(tag));

    /// <summary>
    /// The override exists so anyone whose desktop language is not the one they
    /// read can pick, and it is what renders the screenshots for both languages.
    /// </summary>
    [Fact]
    public void TheOverrideBeatsTheSystem()
    {
        var previous = Environment.GetEnvironmentVariable(Language.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(Language.OverrideVariable, "zh");
            Assert.Equal(AppLanguage.ChineseSimplified, Language.FromSystem());

            Environment.SetEnvironmentVariable(Language.OverrideVariable, "en");
            Assert.Equal(AppLanguage.English, Language.FromSystem());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Language.OverrideVariable, previous);
        }
    }

    [Fact]
    public void NothingIsBlankInEitherLanguage()
    {
        foreach (var language in new[] { AppLanguage.English, AppLanguage.ChineseSimplified })
        foreach (var s in Strings)
        {
            var value = WithLanguage(language, () => (string?)s.GetValue(null));
            Assert.False(string.IsNullOrWhiteSpace(value), $"{s.Name} is blank in {language}");
        }
    }

    /// <summary>
    /// The one failure mode inline translations still allow: pasting the English
    /// into the Chinese slot. Every string should differ between the two, so any
    /// that does not has been left untranslated.
    /// </summary>
    [Fact]
    public void EveryStringIsActuallyTranslated()
    {
        var untranslated = Strings
            .Where(s => WithLanguage(AppLanguage.English, () => (string?)s.GetValue(null))
                     == WithLanguage(AppLanguage.ChineseSimplified, () => (string?)s.GetValue(null)))
            .Select(s => s.Name)
            .ToList();

        Assert.True(untranslated.Count == 0,
                    "identical in both languages: " + string.Join(", ", untranslated));
    }

    /// <summary>The stage names the core reports are matched, not passed through.</summary>
    [Theory]
    [InlineData("Encoding")]
    [InlineData("Writing")]
    [InlineData("Streaming")]
    public void KnownProgressStagesAreTranslated(string stage) =>
        Assert.NotEqual(stage, WithLanguage(AppLanguage.ChineseSimplified, () => S.Stage(stage)));

    /// <summary>An unknown stage is shown as-is rather than swallowed.</summary>
    [Fact]
    public void AnUnknownProgressStageIsPassedThrough() =>
        Assert.Equal("Rebuilding", WithLanguage(AppLanguage.ChineseSimplified, () => S.Stage("Rebuilding")));
}
