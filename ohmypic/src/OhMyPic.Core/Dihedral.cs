namespace OhMyPic.Core;

/// <summary>
/// One of the 8 lossless orthogonal transforms: optional horizontal flip followed by <see cref="Quarter"/> clockwise
/// quarter turns. Used to compose EXIF orientation with the user's rotate/flip into a single transform.
/// </summary>
internal readonly record struct Dihedral(int Quarter, bool Flip)
{
    public static readonly Dihedral Identity = new(0, false);

    public bool IsIdentity => Quarter == 0 && !Flip;

    /// <summary>True when width and height are exchanged.</summary>
    public bool SwapsAxes => Quarter % 2 == 1;

    public static Dihedral From(RotateFlip op) => op switch
    {
        RotateFlip.None => Identity,
        RotateFlip.Rotate90 => new(1, false),
        RotateFlip.Rotate180 => new(2, false),
        RotateFlip.Rotate270 => new(3, false),
        RotateFlip.FlipHorizontal => new(0, true),
        RotateFlip.FlipVertical => new(2, true),
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    /// <summary>Transform that turns stored pixels into display pixels for an EXIF orientation (1..8; others = identity).</summary>
    public static Dihedral FromExifOrientation(int orientation) => orientation switch
    {
        2 => new(0, true),
        3 => new(2, false),
        4 => new(2, true),
        5 => new(3, true),
        6 => new(1, false),
        7 => new(1, true),
        8 => new(3, false),
        _ => Identity,
    };

    /// <summary>This transform followed by <paramref name="next"/>.</summary>
    public Dihedral Then(Dihedral next)
    {
        var m = Multiply(next.Matrix(), Matrix());
        for (int q = 0; q < 4; q++)
        {
            foreach (bool f in new[] { false, true })
            {
                var candidate = new Dihedral(q, f);
                if (candidate.Matrix() == m)
                    return candidate;
            }
        }
        throw new InvalidOperationException("Unreachable: dihedral group is closed.");
    }

    // Point transform in image coordinates (x right, y down). R = 90° clockwise: (x, y) → (-y, x). F: (x, y) → (-x, y).
    private (int A, int B, int C, int D) Matrix()
    {
        (int A, int B, int C, int D) m = Flip ? (-1, 0, 0, 1) : (1, 0, 0, 1);
        for (int i = 0; i < Quarter; i++)
            m = Multiply((0, -1, 1, 0), m);
        return m;
    }

    private static (int A, int B, int C, int D) Multiply((int A, int B, int C, int D) l, (int A, int B, int C, int D) r) =>
        (l.A * r.A + l.B * r.C, l.A * r.B + l.B * r.D, l.C * r.A + l.D * r.C, l.C * r.B + l.D * r.D);
}
