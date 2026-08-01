namespace DeveMobileLPR.Recognition;

internal static class MaximumWeightBipartiteMatcher
{
    private const double ForbiddenCost = 1_000_000;

    public static IReadOnlyList<(int Row, int Column)> Match(float?[,] scores)
    {
        var rowCount = scores.GetLength(0);
        var columnCount = scores.GetLength(1);
        if (rowCount == 0 || columnCount == 0)
        {
            return [];
        }

        // One zero-cost dummy column per row lets every observation remain
        // unmatched without consuming a real track.
        var augmentedColumnCount = columnCount + rowCount;
        var costs = new double[rowCount, augmentedColumnCount];
        for (var row = 0; row < rowCount; row++)
        {
            for (var column = 0; column < columnCount; column++)
            {
                costs[row, column] = scores[row, column] is { } score
                    ? -score
                    : ForbiddenCost;
            }
        }

        var assignment = Minimize(costs);
        var matches = new List<(int Row, int Column)>();
        for (var row = 0; row < assignment.Length; row++)
        {
            var column = assignment[row];
            if (column >= 0
                && column < columnCount
                && scores[row, column] is not null)
            {
                matches.Add((row, column));
            }
        }

        return matches;
    }

    // Rectangular Hungarian algorithm. There are always at least as many
    // columns as rows because Match adds one dummy column per row.
    private static int[] Minimize(double[,] costs)
    {
        var rowCount = costs.GetLength(0);
        var columnCount = costs.GetLength(1);
        var rowPotential = new double[rowCount + 1];
        var columnPotential = new double[columnCount + 1];
        var columnMatching = new int[columnCount + 1];
        var previousColumn = new int[columnCount + 1];

        for (var row = 1; row <= rowCount; row++)
        {
            columnMatching[0] = row;
            var minimum = Enumerable.Repeat(double.PositiveInfinity, columnCount + 1).ToArray();
            var used = new bool[columnCount + 1];
            var currentColumn = 0;
            do
            {
                used[currentColumn] = true;
                var currentRow = columnMatching[currentColumn];
                var delta = double.PositiveInfinity;
                var nextColumn = 0;
                for (var column = 1; column <= columnCount; column++)
                {
                    if (used[column])
                    {
                        continue;
                    }

                    var reducedCost = costs[currentRow - 1, column - 1]
                        - rowPotential[currentRow]
                        - columnPotential[column];
                    if (reducedCost < minimum[column])
                    {
                        minimum[column] = reducedCost;
                        previousColumn[column] = currentColumn;
                    }

                    if (minimum[column] < delta)
                    {
                        delta = minimum[column];
                        nextColumn = column;
                    }
                }

                for (var column = 0; column <= columnCount; column++)
                {
                    if (used[column])
                    {
                        rowPotential[columnMatching[column]] += delta;
                        columnPotential[column] -= delta;
                    }
                    else
                    {
                        minimum[column] -= delta;
                    }
                }

                currentColumn = nextColumn;
            }
            while (columnMatching[currentColumn] != 0);

            do
            {
                var nextColumn = previousColumn[currentColumn];
                columnMatching[currentColumn] = columnMatching[nextColumn];
                currentColumn = nextColumn;
            }
            while (currentColumn != 0);
        }

        var assignment = Enumerable.Repeat(-1, rowCount).ToArray();
        for (var column = 1; column <= columnCount; column++)
        {
            if (columnMatching[column] != 0)
            {
                assignment[columnMatching[column] - 1] = column - 1;
            }
        }

        return assignment;
    }
}
