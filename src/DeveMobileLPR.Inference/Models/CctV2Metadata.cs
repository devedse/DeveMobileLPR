namespace DeveMobileLPR.Inference.Models;

internal static class CctV2Metadata
{
    public const int Width = 128;
    public const int Height = 64;
    public const int Channels = 3;
    public const int MaximumSlots = 10;
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_";
    public const char PaddingCharacter = '_';

    public static IReadOnlyList<string> Regions { get; } =
    [
        "Albania", "Andorra", "Argentina", "Armenia", "Australia", "Austria", "Azerbaijan", "Bahrain",
        "Belarus", "Belgium", "Bosnia and Herzegovina", "Brazil", "Bulgaria", "Cambodia", "Canada", "Croatia",
        "Cyprus", "Czech Republic", "Denmark", "Estonia", "Finland", "France", "Georgia", "Germany",
        "Gibraltar", "Greece", "Guernsey", "Hungary", "Iceland", "Indonesia", "Ireland", "Israel", "Italy",
        "Latvia", "Liechtenstein", "Lithuania", "Luxembourg", "Malaysia", "Malta", "Mexico", "Moldova",
        "Monaco", "Montenegro", "Netherlands", "New Zealand", "North Macedonia", "Norway", "Poland",
        "Portugal", "Qatar", "Romania", "San Marino", "Serbia", "Singapore", "Slovakia", "Slovenia", "Spain",
        "Sweden", "Switzerland", "Thailand", "Turkey", "United States", "Ukraine", "United Kingdom", "Vietnam",
        "Unknown"
    ];
}
