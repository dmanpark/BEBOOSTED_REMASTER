using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// Guards the bundled-font setup: every design weight must resolve to a real glyph
/// typeface of the requested weight (static instances use GDI per-weight family names
/// internally, so a regression here would silently render everything in Regular).
/// </summary>
public sealed class FontResolutionTests
{
    [AvaloniaTheory]
    [InlineData("avares://BeBoosted/Assets/Fonts/InstrumentSans#Instrument Sans", FontWeight.Normal)]
    [InlineData("avares://BeBoosted/Assets/Fonts/InstrumentSans#Instrument Sans", FontWeight.Medium)]
    [InlineData("avares://BeBoosted/Assets/Fonts/InstrumentSans#Instrument Sans", FontWeight.SemiBold)]
    [InlineData("avares://BeBoosted/Assets/Fonts/InstrumentSans#Instrument Sans", FontWeight.Bold)]
    [InlineData("avares://BeBoosted/Assets/Fonts/IBMPlexMono#IBM Plex Mono", FontWeight.Normal)]
    [InlineData("avares://BeBoosted/Assets/Fonts/IBMPlexMono#IBM Plex Mono", FontWeight.Medium)]
    [InlineData("avares://BeBoosted/Assets/Fonts/IBMPlexMono#IBM Plex Mono", FontWeight.SemiBold)]
    [InlineData("avares://BeBoosted/Assets/Fonts/Newsreader#Newsreader 14pt", FontWeight.Normal)]
    [InlineData("avares://BeBoosted/Assets/Fonts/Newsreader#Newsreader 14pt", FontWeight.Medium)]
    [InlineData("avares://BeBoosted/Assets/Fonts/Newsreader#Newsreader 14pt", FontWeight.SemiBold)]
    public void BundledFont_ResolvesRequestedWeight(string family, FontWeight weight)
    {
        var typeface = new Typeface(new FontFamily(family), weight: weight);

        Assert.True(
            FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface),
            $"No glyph typeface for {family} at {weight}");
        Assert.Equal(weight, glyphTypeface.Weight);
    }
}
