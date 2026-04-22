using System.Collections.Generic;

namespace MdTranslatorViewer.Services;

internal enum ViewerColorThemePreset
{
    DarkModern,
    DarkPlus,
    LightModern,
    LightPlus,
}

internal sealed record ViewerDocumentTheme
{
    public required string ColorScheme { get; init; }
    public required string Background { get; init; }
    public required string Panel { get; init; }
    public required string Ink { get; init; }
    public required string Muted { get; init; }
    public required string Line { get; init; }
    public required string LineSoft { get; init; }
    public required string Accent { get; init; }
    public required string AccentStrong { get; init; }
    public required string AccentSoft { get; init; }
    public required string AccentUnderline { get; init; }
    public required string AccentUnderlineHover { get; init; }
    public required string CodeBackground { get; init; }
    public required string CodeForeground { get; init; }
    public required string PreForeground { get; init; }
    public required string QuoteBorder { get; init; }
    public required string QuoteBackground { get; init; }
    public required string QuoteForeground { get; init; }
    public required string Heading1 { get; init; }
    public required string Heading2 { get; init; }
    public required string Heading3 { get; init; }
    public required string LineNumber { get; init; }
    public required string Selection { get; init; }
    public required string ScrollbarThumb { get; init; }
    public required string ScrollbarThumbHover { get; init; }
    public required string DropOverlayBackground { get; init; }
    public required string DropPillBorder { get; init; }
    public required string DropPillBackground { get; init; }
    public required string DropDot { get; init; }
    public required string DropTitle { get; init; }
    public required string DropCopy { get; init; }
}

internal sealed record ViewerThemePreset
{
    public required ViewerColorThemePreset Id { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyDictionary<string, string> BrushColors { get; init; }
    public required ViewerDocumentTheme DocumentTheme { get; init; }
}

internal static class ViewerThemeCatalog
{
    private static readonly IReadOnlyDictionary<ViewerColorThemePreset, ViewerThemePreset> Presets =
        new Dictionary<ViewerColorThemePreset, ViewerThemePreset>
        {
            [ViewerColorThemePreset.DarkModern] = CreateDarkModernPreset(),
            [ViewerColorThemePreset.DarkPlus] = CreateDarkPlusPreset(),
            [ViewerColorThemePreset.LightModern] = CreateLightModernPreset(),
            [ViewerColorThemePreset.LightPlus] = CreateLightPlusPreset(),
        };

    public static ViewerThemePreset Get(ViewerColorThemePreset preset)
    {
        return Presets.TryGetValue(preset, out var theme)
            ? theme
            : Presets[ViewerColorThemePreset.DarkModern];
    }

    private static ViewerThemePreset CreateDarkModernPreset()
    {
        return new ViewerThemePreset
        {
            Id = ViewerColorThemePreset.DarkModern,
            DisplayName = "Dark Modern",
            BrushColors = new Dictionary<string, string>
            {
                ["AppBackgroundBrush"] = "#1F1F1F",
                ["ChromeBrush"] = "#181818",
                ["ContentBrush"] = "#1F1F1F",
                ["LineBrush"] = "#2B2B2B",
                ["WindowFrameBrush"] = "#2B2B2B80",
                ["PrimaryTextBrush"] = "#CCCCCC",
                ["StrongTextBrush"] = "#FFFFFF",
                ["SecondaryTextBrush"] = "#9D9D9D",
                ["MutedTextBrush"] = "#989898",
                ["StatusTextBrush"] = "#CCCCCC",
                ["MenuBackgroundBrush"] = "#1F1F1F",
                ["MenuBorderBrush"] = "#2B2B2B",
                ["MenuSeparatorBrush"] = "#2B2B2B",
                ["MenuForegroundBrush"] = "#CCCCCC",
                ["MenuHighlightBrush"] = "#0078D4",
                ["MenuHighlightForegroundBrush"] = "#FFFFFF",
                ["TabInactiveBackgroundBrush"] = "#181818",
                ["TabActiveBackgroundBrush"] = "#1F1F1F",
                ["TabBorderBrush"] = "#2B2B2B",
                ["TabInactiveForegroundBrush"] = "#9D9D9D",
                ["TabActiveForegroundBrush"] = "#FFFFFF",
                ["TabAccentBrush"] = "#0078D4",
                ["ControlHoverBrush"] = "#F1F1F133",
                ["ToggleTrackBrush"] = "#313131",
                ["ToggleTrackBorderBrush"] = "#3C3C3C",
                ["ToggleThumbBrush"] = "#FFFFFF",
                ["AccentBrush"] = "#4DAAFC",
                ["AccentStrongBrush"] = "#0078D4",
                ["AccentSoftBrush"] = "#2489DB82",
                ["WindowControlForegroundBrush"] = "#CCCCCC",
                ["LoadingOverlayBrush"] = "#181818B8",
                ["DropOverlayBrush"] = "#10121478",
                ["DropPillBrush"] = "#181818F4",
                ["DropPillBorderBrush"] = "#0078D4CC",
                ["DropPillTitleBrush"] = "#FFFFFFF2",
                ["DropPillCopyBrush"] = "#9D9D9D",
                ["DropPillDotBrush"] = "#0078D4",
                ["TabScrollTrackBrush"] = "#FFFFFF14",
                ["TabScrollThumbBrush"] = "#CCCCCC66",
                ["TabScrollThumbHoverBrush"] = "#FFFFFF99",
            },
            DocumentTheme = new ViewerDocumentTheme
            {
                ColorScheme = "dark",
                Background = "#1F1F1F",
                Panel = "#2B2B2B",
                Ink = "#CCCCCC",
                Muted = "#9D9D9D",
                Line = "#2B2B2B",
                LineSoft = "#3C3C3C",
                Accent = "#4DAAFC",
                AccentStrong = "#0078D4",
                AccentSoft = "#2489DB82",
                AccentUnderline = "#4DAAFC61",
                AccentUnderlineHover = "#4DAAFC6B",
                CodeBackground = "#2B2B2B",
                CodeForeground = "#CE9178",
                PreForeground = "#D0D0D0",
                QuoteBorder = "#616161",
                QuoteBackground = "#2B2B2B",
                QuoteForeground = "#D0D0D0",
                Heading1 = "#4FC1FF",
                Heading2 = "#4FC1FF",
                Heading3 = "#4EC9B0",
                LineNumber = "#6E7681",
                Selection = "#2489DB82",
                ScrollbarThumb = "#6E768166",
                ScrollbarThumbHover = "#6E768199",
                DropOverlayBackground = "#10121478",
                DropPillBorder = "#0078D4CC",
                DropPillBackground = "#181818F0",
                DropDot = "#0078D4",
                DropTitle = "#FFFFFFF2",
                DropCopy = "#9D9D9D",
            },
        };
    }

    private static ViewerThemePreset CreateDarkPlusPreset()
    {
        return new ViewerThemePreset
        {
            Id = ViewerColorThemePreset.DarkPlus,
            DisplayName = "Dark+",
            BrushColors = new Dictionary<string, string>
            {
                ["AppBackgroundBrush"] = "#1E1E1E",
                ["ChromeBrush"] = "#252526",
                ["ContentBrush"] = "#1E1E1E",
                ["LineBrush"] = "#303031",
                ["WindowFrameBrush"] = "#30303180",
                ["PrimaryTextBrush"] = "#D4D4D4",
                ["StrongTextBrush"] = "#FFFFFF",
                ["SecondaryTextBrush"] = "#A6A6A6",
                ["MutedTextBrush"] = "#A6A6A6",
                ["StatusTextBrush"] = "#D4D4D4",
                ["MenuBackgroundBrush"] = "#252526",
                ["MenuBorderBrush"] = "#454545",
                ["MenuSeparatorBrush"] = "#454545",
                ["MenuForegroundBrush"] = "#CCCCCC",
                ["MenuHighlightBrush"] = "#0078D4",
                ["MenuHighlightForegroundBrush"] = "#FFFFFF",
                ["TabInactiveBackgroundBrush"] = "#252526",
                ["TabActiveBackgroundBrush"] = "#222222",
                ["TabBorderBrush"] = "#303031",
                ["TabInactiveForegroundBrush"] = "#A6A6A6",
                ["TabActiveForegroundBrush"] = "#FFFFFF",
                ["TabAccentBrush"] = "#007ACC",
                ["ControlHoverBrush"] = "#3A3D41",
                ["ToggleTrackBrush"] = "#252526",
                ["ToggleTrackBorderBrush"] = "#6B6B6B",
                ["ToggleThumbBrush"] = "#FFFFFF",
                ["AccentBrush"] = "#569CD6",
                ["AccentStrongBrush"] = "#007ACC",
                ["AccentSoftBrush"] = "#ADD6FF26",
                ["WindowControlForegroundBrush"] = "#D4D4D4",
                ["LoadingOverlayBrush"] = "#1E1E1EB8",
                ["DropOverlayBrush"] = "#10121478",
                ["DropPillBrush"] = "#252526F4",
                ["DropPillBorderBrush"] = "#007ACCCC",
                ["DropPillTitleBrush"] = "#FFFFFFF2",
                ["DropPillCopyBrush"] = "#A6A6A6",
                ["DropPillDotBrush"] = "#007ACC",
                ["TabScrollTrackBrush"] = "#FFFFFF12",
                ["TabScrollThumbBrush"] = "#D4D4D466",
                ["TabScrollThumbHoverBrush"] = "#FFFFFF99",
            },
            DocumentTheme = new ViewerDocumentTheme
            {
                ColorScheme = "dark",
                Background = "#1E1E1E",
                Panel = "#252526",
                Ink = "#D4D4D4",
                Muted = "#A6A6A6",
                Line = "#303031",
                LineSoft = "#454545",
                Accent = "#569CD6",
                AccentStrong = "#007ACC",
                AccentSoft = "#ADD6FF26",
                AccentUnderline = "#569CD661",
                AccentUnderlineHover = "#569CD67A",
                CodeBackground = "#252526",
                CodeForeground = "#CE9178",
                PreForeground = "#D4D4D4",
                QuoteBorder = "#454545",
                QuoteBackground = "#252526",
                QuoteForeground = "#D4D4D4",
                Heading1 = "#569CD6",
                Heading2 = "#569CD6",
                Heading3 = "#4EC9B0",
                LineNumber = "#A6A6A6",
                Selection = "#3A3D41",
                ScrollbarThumb = "#A6A6A666",
                ScrollbarThumbHover = "#A6A6A699",
                DropOverlayBackground = "#10121478",
                DropPillBorder = "#007ACCCC",
                DropPillBackground = "#252526F0",
                DropDot = "#007ACC",
                DropTitle = "#FFFFFFF2",
                DropCopy = "#A6A6A6",
            },
        };
    }

    private static ViewerThemePreset CreateLightModernPreset()
    {
        return new ViewerThemePreset
        {
            Id = ViewerColorThemePreset.LightModern,
            DisplayName = "Light Modern",
            BrushColors = new Dictionary<string, string>
            {
                ["AppBackgroundBrush"] = "#FFFFFF",
                ["ChromeBrush"] = "#F8F8F8",
                ["ContentBrush"] = "#FFFFFF",
                ["LineBrush"] = "#E5E5E5",
                ["WindowFrameBrush"] = "#E5E5E580",
                ["PrimaryTextBrush"] = "#3B3B3B",
                ["StrongTextBrush"] = "#1E1E1E",
                ["SecondaryTextBrush"] = "#868686",
                ["MutedTextBrush"] = "#767676",
                ["StatusTextBrush"] = "#3B3B3B",
                ["MenuBackgroundBrush"] = "#FFFFFF",
                ["MenuBorderBrush"] = "#CECECE",
                ["MenuSeparatorBrush"] = "#E5E5E5",
                ["MenuForegroundBrush"] = "#3B3B3B",
                ["MenuHighlightBrush"] = "#005FB8",
                ["MenuHighlightForegroundBrush"] = "#FFFFFF",
                ["TabInactiveBackgroundBrush"] = "#F8F8F8",
                ["TabActiveBackgroundBrush"] = "#FFFFFF",
                ["TabBorderBrush"] = "#E5E5E5",
                ["TabInactiveForegroundBrush"] = "#868686",
                ["TabActiveForegroundBrush"] = "#3B3B3B",
                ["TabAccentBrush"] = "#005FB8",
                ["ControlHoverBrush"] = "#F2F2F2",
                ["ToggleTrackBrush"] = "#F8F8F8",
                ["ToggleTrackBorderBrush"] = "#CECECE",
                ["ToggleThumbBrush"] = "#FFFFFF",
                ["AccentBrush"] = "#005FB8",
                ["AccentStrongBrush"] = "#005FB8",
                ["AccentSoftBrush"] = "#BED6ED",
                ["WindowControlForegroundBrush"] = "#1E1E1E",
                ["LoadingOverlayBrush"] = "#FFFFFFD8",
                ["DropOverlayBrush"] = "#BED6EDA8",
                ["DropPillBrush"] = "#FFFFFFF6",
                ["DropPillBorderBrush"] = "#005FB8CC",
                ["DropPillTitleBrush"] = "#1E1E1EF2",
                ["DropPillCopyBrush"] = "#767676",
                ["DropPillDotBrush"] = "#005FB8",
                ["TabScrollTrackBrush"] = "#00000010",
                ["TabScrollThumbBrush"] = "#76767666",
                ["TabScrollThumbHoverBrush"] = "#3B3B3B8A",
            },
            DocumentTheme = new ViewerDocumentTheme
            {
                ColorScheme = "light",
                Background = "#FFFFFF",
                Panel = "#F8F8F8",
                Ink = "#3B3B3B",
                Muted = "#767676",
                Line = "#E5E5E5",
                LineSoft = "#CECECE",
                Accent = "#005FB8",
                AccentStrong = "#005FB8",
                AccentSoft = "#BED6ED",
                AccentUnderline = "#005FB85E",
                AccentUnderlineHover = "#005FB87A",
                CodeBackground = "#F8F8F8",
                CodeForeground = "#A31515",
                PreForeground = "#3B3B3B",
                QuoteBorder = "#E5E5E5",
                QuoteBackground = "#F8F8F8",
                QuoteForeground = "#3B3B3B",
                Heading1 = "#800000",
                Heading2 = "#800000",
                Heading3 = "#267F99",
                LineNumber = "#6E7681",
                Selection = "#E5EBF1",
                ScrollbarThumb = "#AAB2BD80",
                ScrollbarThumbHover = "#AAB2BDAA",
                DropOverlayBackground = "#BED6EDA8",
                DropPillBorder = "#005FB8CC",
                DropPillBackground = "#FFFFFFF2",
                DropDot = "#005FB8",
                DropTitle = "#1E1E1EF2",
                DropCopy = "#767676",
            },
        };
    }

    private static ViewerThemePreset CreateLightPlusPreset()
    {
        return new ViewerThemePreset
        {
            Id = ViewerColorThemePreset.LightPlus,
            DisplayName = "Light+",
            BrushColors = new Dictionary<string, string>
            {
                ["AppBackgroundBrush"] = "#FFFFFF",
                ["ChromeBrush"] = "#F3F3F3",
                ["ContentBrush"] = "#FFFFFF",
                ["LineBrush"] = "#D4D4D4",
                ["WindowFrameBrush"] = "#D4D4D480",
                ["PrimaryTextBrush"] = "#000000",
                ["StrongTextBrush"] = "#000000",
                ["SecondaryTextBrush"] = "#767676",
                ["MutedTextBrush"] = "#767676",
                ["StatusTextBrush"] = "#000000",
                ["MenuBackgroundBrush"] = "#FFFFFF",
                ["MenuBorderBrush"] = "#D4D4D4",
                ["MenuSeparatorBrush"] = "#D4D4D4",
                ["MenuForegroundBrush"] = "#000000",
                ["MenuHighlightBrush"] = "#007ACC",
                ["MenuHighlightForegroundBrush"] = "#FFFFFF",
                ["TabInactiveBackgroundBrush"] = "#F3F3F3",
                ["TabActiveBackgroundBrush"] = "#FFFFFF",
                ["TabBorderBrush"] = "#D4D4D4",
                ["TabInactiveForegroundBrush"] = "#767676",
                ["TabActiveForegroundBrush"] = "#333333",
                ["TabAccentBrush"] = "#007ACC",
                ["ControlHoverBrush"] = "#E8E8E8",
                ["ToggleTrackBrush"] = "#F3F3F3",
                ["ToggleTrackBorderBrush"] = "#919191",
                ["ToggleThumbBrush"] = "#FFFFFF",
                ["AccentBrush"] = "#0451A5",
                ["AccentStrongBrush"] = "#007ACC",
                ["AccentSoftBrush"] = "#ADD6FF80",
                ["WindowControlForegroundBrush"] = "#000000",
                ["LoadingOverlayBrush"] = "#FFFFFFD8",
                ["DropOverlayBrush"] = "#ADD6FFAE",
                ["DropPillBrush"] = "#FFFFFFF6",
                ["DropPillBorderBrush"] = "#007ACCCC",
                ["DropPillTitleBrush"] = "#000000F2",
                ["DropPillCopyBrush"] = "#767676",
                ["DropPillDotBrush"] = "#007ACC",
                ["TabScrollTrackBrush"] = "#00000010",
                ["TabScrollThumbBrush"] = "#76767666",
                ["TabScrollThumbHoverBrush"] = "#0000008A",
            },
            DocumentTheme = new ViewerDocumentTheme
            {
                ColorScheme = "light",
                Background = "#FFFFFF",
                Panel = "#F3F3F3",
                Ink = "#000000",
                Muted = "#767676",
                Line = "#D4D4D4",
                LineSoft = "#CECECE",
                Accent = "#0451A5",
                AccentStrong = "#007ACC",
                AccentSoft = "#ADD6FF80",
                AccentUnderline = "#0451A55E",
                AccentUnderlineHover = "#0451A57A",
                CodeBackground = "#F3F3F3",
                CodeForeground = "#A31515",
                PreForeground = "#000000",
                QuoteBorder = "#D4D4D4",
                QuoteBackground = "#F3F3F3",
                QuoteForeground = "#000000",
                Heading1 = "#800000",
                Heading2 = "#800000",
                Heading3 = "#267F99",
                LineNumber = "#767676",
                Selection = "#E5EBF1",
                ScrollbarThumb = "#B8C0CC80",
                ScrollbarThumbHover = "#A6B0C0AA",
                DropOverlayBackground = "#ADD6FFAE",
                DropPillBorder = "#007ACCCC",
                DropPillBackground = "#FFFFFFF2",
                DropDot = "#007ACC",
                DropTitle = "#000000F2",
                DropCopy = "#767676",
            },
        };
    }
}
