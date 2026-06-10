namespace Floss.App.Tests;

internal static class TestAssertions
{
    public static void True(bool value, string? message = null)
    {
        if (!value) throw new InvalidOperationException(message ?? "Expected true.");
    }

    public static void False(bool value, string? message = null) => True(!value, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"Expected {expected}, got {actual}.");
    }

    public static void Near(double expected, double actual, double tolerance = 0.0001, string? message = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException(message ?? $"Expected {expected}, got {actual}.");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
    {
        var left = expected.ToArray();
        var right = actual.ToArray();
        if (!left.SequenceEqual(right))
            throw new InvalidOperationException(message ?? $"Expected [{string.Join(", ", left)}], got [{string.Join(", ", right)}].");
    }
}
