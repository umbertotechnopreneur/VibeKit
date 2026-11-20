namespace WaterRipple;

internal static class GpuJokes
{
    private static readonly string[] Captions =
    [
        "Family time won. Gaming time lost. The GPU makes ripples now.",
        "Imagine a $1,000 GPU. Now give it ripples.",
        "Built for gaming. Promoted to desktop decoration.",
        "No time for one more level. Always time for one more ripple.",
        "A very serious use of a gaming GPU.",
    ];

    internal static string Caption(uint index) => Captions[index % (uint)Captions.Length];
}
