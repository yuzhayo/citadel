namespace Module.Mangareader.Logic;

/// <summary>Orders Chapter 2 before Chapter 10 without assuming a filename pattern.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer OrdinalIgnoreCase { get; } = new();

    private NaturalStringComparer()
    {
    }

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        var leftIndex = 0;
        var rightIndex = 0;

        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftIsDigit = char.IsAsciiDigit(left[leftIndex]);
            var rightIsDigit = char.IsAsciiDigit(right[rightIndex]);

            if (leftIsDigit && rightIsDigit)
            {
                var numberResult = CompareNumberRuns(left, ref leftIndex, right, ref rightIndex);
                if (numberResult != 0) return numberResult;
                continue;
            }

            var leftCharacter = char.ToUpperInvariant(left[leftIndex]);
            var rightCharacter = char.ToUpperInvariant(right[rightIndex]);
            if (leftCharacter != rightCharacter) return leftCharacter.CompareTo(rightCharacter);

            leftIndex++;
            rightIndex++;
        }

        var lengthResult = (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        return lengthResult != 0
            ? lengthResult
            : StringComparer.Ordinal.Compare(left, right);
    }

    private static int CompareNumberRuns(
        string left,
        ref int leftIndex,
        string right,
        ref int rightIndex)
    {
        var leftStart = leftIndex;
        var rightStart = rightIndex;

        while (leftIndex < left.Length && char.IsAsciiDigit(left[leftIndex])) leftIndex++;
        while (rightIndex < right.Length && char.IsAsciiDigit(right[rightIndex])) rightIndex++;

        var leftSignificant = leftStart;
        var rightSignificant = rightStart;
        while (leftSignificant < leftIndex && left[leftSignificant] == '0') leftSignificant++;
        while (rightSignificant < rightIndex && right[rightSignificant] == '0') rightSignificant++;

        var leftLength = leftIndex - leftSignificant;
        var rightLength = rightIndex - rightSignificant;
        if (leftLength != rightLength) return leftLength.CompareTo(rightLength);

        for (var offset = 0; offset < leftLength; offset++)
        {
            var result = left[leftSignificant + offset].CompareTo(right[rightSignificant + offset]);
            if (result != 0) return result;
        }

        return (leftIndex - leftStart).CompareTo(rightIndex - rightStart);
    }
}
