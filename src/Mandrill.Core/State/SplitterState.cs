namespace Mandrill.Core.State;

/// <summary>Splitter positions of a layout as star ratios of the first column/row (architecture §6). Default 0.5 / 0.5.</summary>
/// <param name="Col">Share of the first column, 0–1 exclusive.</param>
/// <param name="Row">Share of the first row, 0–1 exclusive.</param>
public sealed record SplitterState(double Col, double Row)
{
    /// <summary>Equal split (0.5 / 0.5).</summary>
    public static SplitterState Equal { get; } = new(0.5, 0.5);

    internal SplitterState Normalize(ICollection<string> fixes, string owner) => new(
        Fixes.Check(fixes, owner + ".col", Col, IsRatio(Col), Equal.Col),
        Fixes.Check(fixes, owner + ".row", Row, IsRatio(Row), Equal.Row));

    private static bool IsRatio(double value) => double.IsFinite(value) && value > 0 && value < 1;
}
