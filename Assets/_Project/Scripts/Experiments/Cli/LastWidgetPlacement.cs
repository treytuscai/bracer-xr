namespace Experiments.Cli
{
    /// <summary>
    /// Tracks the most recent widget placement for expctl <c>placed</c>.
    /// Grid experiments record content scale and rotation degrees at bake time; edits via the
    /// resize/rotate panel update <see cref="CurrentSize"/> / <see cref="CurrentRotationDegrees"/>
    /// when the edited cell matches the last placed cell.
    /// </summary>
    public static class LastWidgetPlacement
    {
        struct Snapshot
        {
            public bool HasValue;
            public bool TracksCell;
            public int Col;
            public int Row;
            public float OriginalSize;
            public float OriginalRotationDegrees;
            public float CurrentSize;
            public float CurrentRotationDegrees;
        }

        static Snapshot _last;

        public static void RecordAtCell(int col, int row, float size, float rotationDegrees)
        {
            _last = new Snapshot
            {
                HasValue = true,
                TracksCell = true,
                Col = col,
                Row = row,
                OriginalSize = size,
                OriginalRotationDegrees = rotationDegrees,
                CurrentSize = size,
                CurrentRotationDegrees = rotationDegrees
            };
        }

        public static void RecordCanvas(float size, float rotationDegrees)
        {
            _last = new Snapshot
            {
                HasValue = true,
                TracksCell = false,
                OriginalSize = size,
                OriginalRotationDegrees = rotationDegrees,
                CurrentSize = size,
                CurrentRotationDegrees = rotationDegrees
            };
        }

        public static void UpdateCurrentAtCell(int col, int row, float size, float rotationDegrees)
        {
            if (!_last.HasValue || !_last.TracksCell) return;
            if (col != _last.Col || row != _last.Row) return;
            _last.CurrentSize = size;
            _last.CurrentRotationDegrees = rotationDegrees;
        }

        public static void UpdateCurrentCanvas(float size, float rotationDegrees)
        {
            if (!_last.HasValue || _last.TracksCell) return;
            _last.CurrentSize = size;
            _last.CurrentRotationDegrees = rotationDegrees;
        }

        public static void Clear() => _last = default;

        public static string FormatReply()
        {
            if (!_last.HasValue)
                return "no placement yet";

            return
                $"original_size={_last.OriginalSize:F2} original_rotation={_last.OriginalRotationDegrees:F2} " +
                $"current_size={_last.CurrentSize:F2} current_rotation={_last.CurrentRotationDegrees:F2}";
        }
    }
}
