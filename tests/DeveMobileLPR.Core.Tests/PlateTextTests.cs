using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.Tests;

public sealed class PlateTextTests
{
    [Theory]
    [InlineData("xx-12-34", "XX1234")]
    [InlineData(" 12 ab 34 ", "12AB34")]
    [InlineData("a.b_c-1!2", "ABC12")]
    public void Normalize_RemovesDecorationAndUppercases(string input, string expected) =>
        Assert.Equal(expected, PlateText.Normalize(input));

    [Theory]
    [InlineData("AB1234", "AB-12-34")]
    [InlineData("1ABC23", "1-ABC-23")]
    [InlineData("ABC12D", "ABC-12-D")]
    public void FormatDutchPlate_UsesOfficialSidecodeGrouping(string input, string expected) =>
        Assert.Equal(expected, PlateText.FormatDutchPlate(input));

    [Theory]
    [InlineData("AB-12-34")]
    [InlineData("12-34-AB")]
    [InlineData("12-ABC-3")]
    [InlineData("A-123-BC")]
    public void IsPlausibleDutchPlate_AcceptsKnownSidecodeShapes(string value) =>
        Assert.True(PlateText.IsPlausibleDutchPlate(value));

    [Theory]
    [InlineData("ABC1234")]
    [InlineData("AAAAAA")]
    [InlineData("12A34B")]
    public void IsPlausibleDutchPlate_RejectsInvalidShapes(string value) =>
        Assert.False(PlateText.IsPlausibleDutchPlate(value));

    [Theory]
    [InlineData("AB-12-34", "AB1234", 0)]
    [InlineData("AB1234", "AB1235", 1)]
    [InlineData("AB1234", "A1234", 1)]
    [InlineData("AB1234", "AB1299", 2)]
    public void EditDistance_UsesNormalizedLevenshteinDistance(
        string left,
        string right,
        int expected) =>
        Assert.Equal(expected, PlateText.EditDistance(left, right));
}
