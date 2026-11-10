namespace ControllerHelper.Services;

public sealed record DisplayScaleRange(int MinimumRelative, int CurrentRelative, int MaximumRelative)
{
    private static readonly int[] Percentages = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];
    public int CurrentPercent => Percentages[CurrentRelative - MinimumRelative];
    public int[] Choices => Percentages.Take(MaximumRelative - MinimumRelative + 1).ToArray();

    internal void Validate()
    {
        if (MinimumRelative > 0 || MinimumRelative < -11 || MaximumRelative < 0 ||
            (long)MaximumRelative - MinimumRelative >= Percentages.Length ||
            CurrentRelative < MinimumRelative || CurrentRelative > MaximumRelative)
            throw new NotSupportedException("Windows вернула неподдерживаемый диапазон масштаба ТВ.");
    }

    internal int RelativeFor(int percent)
    {
        Validate();
        int index = Array.IndexOf(Percentages, percent);
        if (index < 0 || index > MaximumRelative - MinimumRelative)
            throw new InvalidOperationException($"Масштаб {percent}% недоступен для текущего режима ТВ.");
        return index + MinimumRelative;
    }

    internal static bool IsKnownPercent(int value) => Array.IndexOf(Percentages, value) >= 0;
}
