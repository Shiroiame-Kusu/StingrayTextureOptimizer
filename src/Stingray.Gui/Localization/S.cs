// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Textures;

namespace Stingray.Gui.Localization;

/// <summary>
/// Every string the window can show, in each language it can show them in.
/// </summary>
/// <remarks>
/// Deliberately not resources and satellite assemblies: those resolve through
/// <c>CultureInfo</c>, which this build has none of — see <see cref="Language"/>.
/// Keeping the translations inline also means a missing one is a compile error
/// rather than a key that silently falls back to English at run time.
///
/// Adding a language means adding a parameter to <see cref="T"/> and answering
/// the compiler until it stops complaining.
/// </remarks>
public static class S
{
    private static string T(string en, string zh) =>
        Language.Current == AppLanguage.ChineseSimplified ? zh : en;

    private const string Nl = "\n";

    // ---- toolbar -----------------------------------------------------------

    public static string OpenBundle => T("Open bundle…", "打开资源包…");
    public static string OpenFolder => T("Open folder…", "打开文件夹…");
    public static string OpenFolderHint => T(
        "Scan a folder for mods, so you can look through them without hunting for bundle files." + Nl
      + "Point it at your mod manager's folder: everything under it is searched, one entry per bundle, "
      + "largest first." + Nl
      + "Any backup folder is skipped, including the ones this tool writes.",
        "扫描一个文件夹里的 mod，这样就不用自己去翻资源包文件了。" + Nl
      + "指向你的 mod 管理器所在的文件夹即可：它下面的所有内容都会被搜索，每个资源包一行，大的排在前面。" + Nl
      + "所有 backup 文件夹都会被跳过，包括这个工具自己写出来的那些。");

    public static string PickAFolder => T("Choose a folder to scan for mods", "选择要扫描 mod 的文件夹");
    public static string ModsHeader => T("Mods", "Mod 列表");
    public static string StreamedSuffix => T("streamed", "流式");

    public static string BundleCount(int bundles, string size) =>
        T($"{bundles} bundles · {size}", $"{bundles} 个资源包 · {size}");

    public static string SelectedForOptimising(int bundles, string size) => T(
        $"{bundles} selected, {size}. Optimize runs them one after another.",
        $"已选 {bundles} 个，共 {size}。点击优化会依次处理。");

    public static string OptimizeSelected(int bundles) =>
        T($"Optimize {bundles}", $"优化 {bundles} 个");

    public static string AnalyseSelected(int bundles) =>
        T($"Analyse {bundles}", $"分析 {bundles} 个");

    public static string AnalysingMany(int done, int total, string name) =>
        T($"{done}/{total}  {name}", $"{done}/{total}  {name}");

    public static string AnalysedMany(int bundles, int textures, int skipped) => T(
        $"{bundles} bundle(s) analysed: {textures} texture(s) can be shrunk, {skipped} skipped. "
      + "Review below, then Optimize.",
        $"已分析 {bundles} 个资源包：{textures} 张纹理可以缩小，{skipped} 张跳过。"
      + "在下面确认后再点优化。");

    public static string AnalysedNothing(int bundles) => T(
        $"{bundles} bundle(s) analysed: nothing to do, they are already optimised.",
        $"已分析 {bundles} 个资源包：无事可做，它们已经优化过了。");

    public static string ColumnMod => T("Mod", "所属 Mod");

    public static string StreamReachesNothing(int chainless) => T(
        $" Stream mips reaches nothing here: {chainless} texture(s) carry no mip chain and none of the "
      + "rest do either. Tick Generate mipmaps to build chains for them first.",
        $" 流式 Mip 在这里没有作用：有 {chainless} 张纹理没有 mip 链，其余的也没有。"
      + "请先勾选“生成 Mipmap”给它们建链。");

    public static string ConfirmBatchHeading(int bundles) =>
        T($"Optimize {bundles} bundles?", $"要优化这 {bundles} 个资源包吗？");

    public static string ConfirmBatchBody => T(
        "The plan above is written as it stands. Each bundle is backed up to a 'backup' folder beside "
      + "it, rewritten and then verified — the same as optimising one at a time. Anything already "
      + "optimised, or holding a backup from a previous run, is reported and left alone.",
        "上面的方案会照原样写入。每个资源包都会备份到它旁边的 backup 文件夹、重写，然后校验 —— "
      + "和一个个处理完全一样。已经优化过的、或者已经存在上一次备份的，会被报告并跳过。");

    public static string AnalysisRetired => T(
        "Settings or selection changed, so that analysis no longer applies. Press Analyse again.",
        "设置或所选内容已更改，之前的分析不再适用。请重新点击分析。");

    public static string BatchProgress(int done, int total, string name) =>
        T($"{done}/{total}  {name}", $"{done}/{total}  {name}");

    public static string BatchDone(int optimised, int skipped, int failed, string saved) => T(
        $"Done: {optimised} optimised, {skipped} left alone, {failed} failed. Saved {saved}.",
        $"完成：优化了 {optimised} 个，跳过 {skipped} 个，失败 {failed} 个。共省下 {saved}。");

    public static string Scanning(string folder) =>
        T($"Scanning {folder}…", $"正在扫描 {folder}…");

    public static string FoundMods(int bundles, string total) => T(
        $"Found {bundles} bundle(s), {total} of GPU data. Click one to see its plan, or tick several "
      + "to optimise them together.",
        $"找到 {bundles} 个资源包，共 {total} 的 GPU 数据。点一个查看它的处理方案，"
      + "或者勾选多个一起优化。");

    public static string FoundNothing(string folder) => T(
        $"No bundles under {folder}. Point this at the folder your mod manager keeps mods in.",
        $"{folder} 下面没有找到资源包。请指向你的 mod 管理器存放 mod 的那个文件夹。");

    public static string NoBundleLoaded => T("no bundle loaded", "未打开资源包");

    public static string FormatLabel => T("Format", "格式");
    public static string FormatHint => T(
        "Which block format each texture is encoded to." + Nl
      + "BC1 is half the size of BC7 but has 5:6:5 colour endpoints, so it bands on gradients." + Nl
      + "BC7 reproduces any 8-bit RGBA almost exactly." + Nl
      + "Smallest size also uses BC1 for pure cutout masks, since BC1 carries one alpha bit.",
        "每张纹理编码成哪种块压缩格式。" + Nl
      + "BC1 只有 BC7 一半大，但颜色端点是 5:6:5，在渐变上会出现色带。" + Nl
      + "BC7 几乎能精确还原任何 8 位 RGBA。" + Nl
      + "最小体积还会把纯镂空遮罩也交给 BC1，因为 BC1 正好带一位 alpha。");

    public static string EffortLabel => T("Effort", "力度");
    public static string EffortHint => T(
        "How hard the chosen encoder tries. Relative to that encoder, not an absolute quality target: "
      + "Compressonator at Balanced beats BCnEncoder at Best." + Nl
      + "It never changes the output size, only quality and time." + Nl
      + "Balanced suits both encoders. Best buys very little for a lot of time.",
        "所选编码器要试多久。这是相对于该编码器而言的，不是绝对质量目标："
      + "Compressonator 的均衡比 BCnEncoder 的最佳还好。" + Nl
      + "它绝不改变输出体积，只影响质量和耗时。" + Nl
      + "均衡对两个编码器都合适。最佳花很多时间却几乎买不到什么。");

    public static string ThreadsLabel => T("Threads", "线程");
    public static string ThreadsHint => T(
        "Encoder worker threads." + Nl
      + "Defaults to leaving four cores free so the desktop stays responsive during long encodes.",
        "编码器的工作线程数。" + Nl
      + "默认留出四个核心不用，好让长时间编码时桌面还能用。");

    public static string MaxSizeLabel => T("Max size", "最大尺寸");
    public static string MaxSizeHint => T(
        "Halve textures larger than this until they fit." + Nl
      + "The only lossy option here, so it is off by default." + Nl
      + "Each affected texture shows its measured detail cost in the Resize cost column.",
        "把大于这个尺寸的纹理不断减半直到放得下。" + Nl
      + "这是这里唯一有损的选项，所以默认关闭。" + Nl
      + "每张受影响的纹理都会在“缩放代价”一列显示实测的细节损失。");

    public static string MipLevelsLabel => T("Mip levels", "Mip 层级");

    public static string StreamMipsLabel => T("Stream mips", "流式 Mip");
    public static string StreamMipsHint => T(
        "EXPERIMENTAL, off by default." + Nl
      + "The value is the largest mip level that stays in video memory permanently. Everything above "
      + "it moves to the .stream file and is loaded when the texture is actually on screen, so nothing "
      + "is discarded and nothing is re-encoded." + Nl
      + "This is the only option here that cuts video memory without costing quality — it costs disk "
      + "space instead." + Nl
      + "Which value hardly matters: on a real mod, Off to 512 saves 102 MiB, and going all the way "
      + "down to 64 saves only 6 MiB more. A higher floor keeps a usable image resident, so pop-in is "
      + "less visible. 512 is the suggested choice." + Nl
      + "Turns Generate mipmaps on with it, because a texture with no chain has nothing to stream; "
      + "setting this back to Off turns it off again." + Nl
      + "One field the conversion has to write cannot be worked out from the texture — shipped textures "
      + "identical in every other respect carry both of its values — so what goes there is a best guess. "
      + "Test in game and keep the backup.",
        "实验性功能，默认关闭。" + Nl
      + "这个值是永久留在显存里的最大 mip 层级。比它更大的层级会移到 .stream 文件里，"
      + "等纹理真的出现在屏幕上时再加载，所以没有丢弃任何东西，也没有重新编码任何东西。" + Nl
      + "这是这里唯一不损失画质就能减少显存的选项 —— 代价是硬盘空间。" + Nl
      + "具体取值几乎无所谓：在一个真实 mod 上，从关闭到 512 省下 102 MiB，一路降到 64 也只多省 6 MiB。"
      + "常驻层级越高，常驻的图像越可用，由模糊变清晰的过程就越不显眼。推荐用 512。" + Nl
      + "它会连带打开“生成 Mipmap”，因为没有 mip 链的纹理没有东西可以流式化；把它改回“关闭”时也会一起关掉。" + Nl
      + "转换过程必须写的一个字段无法从纹理本身推出来 —— 随游戏发布的纹理里，其他方面完全相同的"
      + "两张会带着这个字段的两种取值 —— 所以写进去的只是一个最佳猜测。请在游戏里测试，并保留备份。");

    public static string EncoderLabel => T("Encoder", "编码器");
    public static string EncoderHint => T(
        "Which compressor does the work." + Nl
      + "Fast uses the bundled Compressonator CMP_Core library: several times quicker and slightly "
      + "better quality." + Nl
      + "Portable uses the pure-managed encoder, which always works." + Nl
      + "Automatic prefers Fast and falls back to Portable when the native library is missing.",
        "由哪个压缩器干活。" + Nl
      + "“快速”用随附的 Compressonator CMP_Core 库：快好几倍，质量还略好。" + Nl
      + "“通用”用纯托管编码器，任何情况下都能用。" + Nl
      + "“自动”优先用快速的，缺少原生库时回退到通用的。");

    public static string GenerateMipmaps => T("Generate mipmaps", "生成 Mipmap");
    public static string GenerateMipmapsHint => T(
        "Builds a mip chain for textures that shipped without one, which many mods do." + Nl
      + "Those textures shimmer and crawl when seen at an angle or from a distance, because the GPU has "
      + "no smaller level to sample. This fixes that." + Nl
      + "On its own it costs about a third more video memory, since the chain below the top level adds "
      + "up to that." + Nl
      + "It pays for itself with Stream mips: once a chain exists the texture can be streamed, and only "
      + "the small levels need to stay resident. Picking a Stream mips level ticks this for you for that "
      + "reason." + Nl
      + "Unticking it sets Stream mips back to Off when nothing in this bundle has a chain of its own, "
      + "since there would be nothing left for a floor to reach. Where some textures do have one, the "
      + "floor stays: those stream without any re-encoding." + Nl
      + "This one re-encodes, so it is not lossless.",
        "为出厂时没有 mip 链的纹理建一条链 —— 很多 mod 都是这样。" + Nl
      + "这类纹理在斜视或拉远时会闪烁、爬行，因为 GPU 没有更小的层级可以采样。这个选项能解决它。" + Nl
      + "单独用它会多花大约三分之一的显存，因为顶层以下的链加起来正好是这么多。" + Nl
      + "它靠“流式 Mip”把成本赚回来：有了链纹理才能流式化，也就只需要让小的那几级常驻。"
      + "正因为如此，选择流式 Mip 层级时会自动替你勾上这一项。" + Nl
      + "反过来，如果这个资源包里没有任何纹理自带 mip 链，取消勾选会把流式 Mip 一并改回“关闭”，"
      + "因为已经没有东西可供它作用了。如果确实有纹理自带链，常驻层级会保留：那些纹理不用重新编码就能流式化。" + Nl
      + "这个选项会重新编码，所以不是无损的。");

    public static string CollapseSolidColours => T("Collapse solid colours", "折叠纯色");
    public static string CollapseSolidColoursHint => T(
        "A texture whose every pixel is identical is rewritten at 16x16." + Nl
      + "Visually lossless: a constant texture samples the same at any resolution." + Nl
      + "Saves both file size and video memory.",
        "每个像素都相同的纹理会被以 16x16 重写。" + Nl
      + "视觉无损：常量纹理在任何分辨率下采样结果都一样。" + Nl
      + "同时节省文件体积和显存。");

    public static string ShareDuplicates => T("Share duplicates", "共享重复数据");
    public static string ShareDuplicatesHint => T(
        "Byte-identical payloads are stored once and referenced by every entry that uses them." + Nl
      + "Completely lossless, and usually the largest saving in an already-compressed bundle." + Nl
      + "Note: this shrinks the file only. Each entry is still its own GPU resource, so video memory is "
      + "unchanged.",
        "逐字节相同的数据只存一份，由所有用到它的条目共同引用。" + Nl
      + "完全无损，而且在已经压缩过的资源包里通常是最大的一笔节省。" + Nl
      + "注意：它只缩小文件。每个条目仍然是各自独立的 GPU 资源，显存不变。");

    public static string Optimize => T("Optimize", "开始优化");

    // ---- grid --------------------------------------------------------------

    public static string ColumnTexture => T("Texture", "纹理");
    public static string ColumnDimensions => T("Dimensions", "尺寸");
    public static string ColumnFrom => T("From", "原格式");
    public static string ColumnTo => T("To", "目标格式");
    public static string ColumnSize => T("Size", "当前");
    public static string ColumnNew => T("New", "之后");
    public static string ColumnSaved => T("Saved", "节省");
    public static string ColumnContent => T("Content", "内容");
    public static string ColumnResizeCost => T("Resize cost", "缩放代价");

    public static string ResizeCostHint => T(
        "Detail lost by halving this texture, measured on its own pixels." + Nl
      + "It does not depend on the encoder, and does not need to: block compression contributes 54-78 dB "
      + "while resizing contributes 30-50 dB, so the resize term dominates the total." + Nl
      + "Measured against the full pipeline it is accurate to about 1 dB.",
        "把这张纹理减半会损失多少细节，是在它自己的像素上测出来的。" + Nl
      + "它不取决于编码器，也不需要：块压缩贡献 54-78 dB，而缩放贡献 30-50 dB，所以缩放这一项主导了总误差。" + Nl
      + "与完整流程对照，误差约在 1 dB 以内。");

    public static string Skipped => T("Skipped", "已跳过");

    // ---- status ------------------------------------------------------------

    public static string OpenABundleToBegin => T("Open a bundle to begin.", "打开一个资源包开始。");

    public static string Analysing(string file) =>
        T($"Analysing {file}...", $"正在分析 {file}…");

    public static string CouldNotOpen(string reason) =>
        T($"Could not open: {reason}", $"无法打开：{reason}");

    public static string Failed(string reason) =>
        T($"Failed: {reason}", $"失败：{reason}");

    public static string AlreadyOptimised =>
        T("Nothing to do: this bundle is already optimised.",
          "无事可做：这个资源包已经优化过了。");

    public static string OnlyDuplicates(int duplicates) => T(
        $"Every texture is already compressed, but {duplicates} duplicate payloads can be shared.",
        $"所有纹理都已经压缩过了，但有 {duplicates} 份重复数据可以共享。");

    public static string CanShrink(int textures, int skipped) => T(
        $"{textures} texture(s) can be shrunk, {skipped} skipped.",
        $"{textures} 张纹理可以缩小，{skipped} 张跳过。");

    public static string CanShrinkAndShare(int textures, int duplicates, int skipped) => T(
        $"{textures} texture(s) can be shrunk and {duplicates} duplicate payloads shared, {skipped} skipped.",
        $"{textures} 张纹理可以缩小，另有 {duplicates} 份重复数据可以共享，{skipped} 张跳过。");

    public static string DuplicateSummary(int entries, string wasted) => T(
        $"{entries} entries repeat a payload another entry already has, wasting {wasted}. "
      + "These are stored once and shared.",
        $"有 {entries} 个条目重复了别的条目已经有的数据，浪费了 {wasted}。这些只会存一份并共享。");

    public static string SavingSummary(string before, string after, string percent) =>
        T($"{before} → {after}  (−{percent})", $"{before} → {after}（−{percent}）");

    public static string FootprintSummary(string before, string after) =>
        T($"GPU memory {before} → {after}", $"显存 {before} → {after}");

    public static string EncoderStatus(string backend) =>
        T($"Encoder: {backend}", $"编码器：{backend}");

    public static string Done(string before, string after, string saved, string seconds, string backup) => T(
        $"Done: {before} → {after} (saved {saved}) in {seconds}s. Verified; originals in {backup}/.",
        $"完成：{before} → {after}（省下 {saved}），用时 {seconds} 秒。已校验；原文件在 {backup}/ 里。");

    public static string WrittenButIssues(int count, string details) => T(
        $"Written, but verification found {count} issue(s): {details}",
        $"已写入，但校验发现 {count} 个问题：{details}");

    /// <summary>
    /// The stage names the core reports while applying a plan. Matched rather
    /// than translated at the source, so the command line keeps saying what it
    /// has always said.
    /// </summary>
    public static string Stage(string stage) => stage switch
    {
        "Encoding" => T("Encoding", "编码中"),
        "Writing" => T("Writing", "写入中"),
        "Streaming" => T("Streaming", "写入流式数据"),
        _ => stage,
    };

    public static string Progress(string stage, string detail) =>
        T($"{Stage(stage)}: {detail}", $"{Stage(stage)}：{detail}");

    public static string BackedUp(int count, string directory) => T(
        $"Backed up {count} file(s) to {directory}",
        $"已备份 {count} 个文件到 {directory}");

    // ---- dropdown entries --------------------------------------------------

    public static string StrategyBalanced =>
        T("Balanced (BC1 opaque, BC7 with alpha)", "均衡（不透明用 BC1，带 alpha 用 BC7）");
    public static string StrategyQuality => T("Best quality (BC7 always)", "最佳质量（始终用 BC7）");
    public static string StrategySmallest => T("Smallest size (BC1 wherever it fits)", "最小体积（能用 BC1 就用）");

    public static string QualityFast => T("Fast (lowest quality)", "快速（质量最低）");
    public static string QualityBalanced => T("Balanced", "均衡");
    public static string QualityBest => T("Best (slowest)", "最佳（最慢）");

    public static string MipKeepChain => T("Keep smaller levels", "保留较小的层级");
    public static string MipSingleLevel => T("Keep one level only", "只保留一级");

    public static string BackendAuto => T("Automatic", "自动");
    public static string BackendFast => T("Fast (Compressonator)", "快速（Compressonator）");
    public static string BackendPortable => T("Portable (BCnEncoder)", "通用（BCnEncoder）");

    public static string KeepOriginalSize => T("Keep original", "保持原样");
    public static string SizeCapMax(int size) => T($"{size} max", $"最大 {size}");

    public static string StreamOff => T("Off", "关闭");
    public static string StreamKeep(int size) => T($"Keep {size} resident", $"常驻 {size}");
    public static string StreamKeepSuggested(int size) =>
        T($"Keep {size} resident (suggested)", $"常驻 {size}（推荐）");

    // ---- per-row and per-setting explanations ------------------------------

    public static string MipModeHintActive => T(
        "What to do with a texture that carries a mip chain." + Nl
      + "Keep smaller levels: discard only the levels above the cap. The texture stays mipmapped, so it "
      + "will not shimmer at distance." + Nl
      + "Keep one level only: throw the chain away and keep a single image. Smaller, but minified "
      + "surfaces shimmer and sample less efficiently." + Nl
      + "Either way the pixels are the author's own: nothing is re-encoded.",
        "带 mip 链的纹理该怎么处理。" + Nl
      + "保留较小的层级：只丢弃上限之上的层级。纹理仍然带 mipmap，所以拉远了也不会闪。" + Nl
      + "只保留一级：把整条链扔掉，只留一张图。更小，但缩小显示时会闪烁，采样效率也更低。" + Nl
      + "两种方式保留的都是作者自己的像素：不会重新编码。");

    public static string MipModeHintDisabled => T(
        "Not used while Stream mips is on: a streamed texture keeps the chain that survives Max size, "
      + "so there is nothing further to drop. Set Stream mips to Off to choose here.",
        "流式 Mip 打开时用不到：流式纹理会保留在最大尺寸之后幸存的整条链，所以没有更多可丢的了。"
      + "把流式 Mip 设为“关闭”才能在这里选择。");

    public static string FormatChangeFree => T("Target block format.", "目标块压缩格式。");

    public static string FormatChangeFixed(int levels) => T(
        $"Fixed: this texture has {levels} mip levels, so its payload is sliced out of the existing "
      + "chain rather than re-encoded, and keeps the format it was authored in.",
        $"不可更改：这张纹理有 {levels} 个 mip 层级，它的数据是从现有的链里切出来的，"
      + "不会重新编码，因此保持作者当初使用的格式。");

    // ---- confirmation ------------------------------------------------------

    public static string ConfirmTitle => T("Confirm", "确认");
    public static string ConfirmCancel => T("Cancel", "取消");
    public static string ConfirmRewrite => T("Rewrite", "重写");
    public static string ConfirmHeading => T("Rewrite this bundle?", "要重写这个资源包吗？");
    public static string ConfirmBody => T(
        "The bundle and its .gpu_resources will be replaced in place. Originals are copied to a "
      + "'backup' folder alongside them first, and the result is verified automatically after writing.",
        "资源包和它的 .gpu_resources 会被就地替换。原文件会先被复制到旁边的 backup 文件夹，"
      + "写入之后还会自动校验结果。");

    // ---- footer ------------------------------------------------------------

    public static string Notice => T(
        "Stingray Texture Optimizer, Copyright (C) 2026 Shiroiame-Kusu. Free software with ABSOLUTELY "
      + "NO WARRANTY, redistributable under the GNU GPL v3 or later. See LICENSE.",
        "Stingray Texture Optimizer，版权所有 (C) 2026 Shiroiame-Kusu。自由软件，不提供任何担保，"
      + "可依据 GNU GPL v3 或更新版本再分发。详见 LICENSE。");

    public static string EncoderFooterHint => T(
        "The fast encoder is a small native library bundled with release downloads. Building from "
      + "source? See native/README.md.",
        "快速编码器是随发布版一起提供的一个小型原生库。从源码构建？见 native/README.md。");

    // ---- what the analyser found, said in the reader's language ------------

    /// <summary>
    /// The core writes these in English for the command line, whose output is
    /// documented and scripted against. The window re-says them from the same
    /// measurements rather than translating prose.
    /// </summary>
    public static string Content(TextureAnalysis a)
    {
        if (a.IsSolidColour)
        {
            var c = a.SolidColour;
            return T($"solid RGBA({c[0]},{c[1]},{c[2]},{c[3]})",
                     $"纯色 RGBA({c[0]},{c[1]},{c[2]},{c[3]})");
        }

        if (a.IsFlatColourWithAlphaDetail)
            return T($"flat RGB({a.Red.Minimum},{a.Green.Minimum},{a.Blue.Minimum}), "
                   + $"alpha has {a.Alpha.DistinctValues} values",
                     $"RGB 恒为 ({a.Red.Minimum},{a.Green.Minimum},{a.Blue.Minimum})，"
                   + $"alpha 有 {a.Alpha.DistinctValues} 种取值");

        return a.HasAlphaDetail
            ? T("full detail with alpha", "完整细节，带 alpha")
            : T("full detail, opaque", "完整细节，不透明");
    }

    /// <summary>Plain-language reading of the measured resize cost.</summary>
    public static string DetailVerdict(double db) => db switch
    {
        double.PositiveInfinity => T("nothing lost", "没有损失"),
        var d when double.IsNaN(d) => T("not measured", "未测量"),
        >= 45 => T($"nothing visible lost ({db:F0} dB)", $"没有可见损失（{db:F0} dB）"),
        >= 36 => T($"slight softening ({db:F0} dB)", $"略微变糊（{db:F0} dB）"),
        >= 30 => T($"visible softening ({db:F0} dB)", $"可见变糊（{db:F0} dB）"),
        _ => T($"real detail lost ({db:F0} dB)", $"真的丢了细节（{db:F0} dB）"),
    };
}
