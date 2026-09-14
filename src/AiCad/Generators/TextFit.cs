using System;

namespace AiCad.Generators
{
    /// <summary>
    /// Sizing text to a fixed space.
    ///
    /// A title block cell is a fixed width, but the value going into it is
    /// whatever the model wrote. A long name used to run straight through the
    /// divider and into the next field, which is how "DRAWN: AutoCAD Electrical"
    /// ended up sitting on top of "DATE:".
    ///
    /// Shrinking is preferred to cutting - the whole value stays readable - but
    /// only down to a floor, below which the text would be too small to read on
    /// a print. Past that the value is truncated instead.
    ///
    /// Kept free of AutoCAD types so it can be tested without one.
    /// </summary>
    public static class TextFit
    {
        /// <summary>
        /// Advance per character as a fraction of the text height, for the shape
        /// fonts AutoCAD uses by default. Deliberately a little generous:
        /// leaving a millimetre spare looks fine, overflowing the cell does not.
        /// </summary>
        public const double WidthFactor = 0.72;

        /// <summary>Smaller than this stops being readable on a print.</summary>
        public const double MinHeight = 1.8;

        public static double Width(string text, double height)
        {
            if (string.IsNullOrEmpty(text) || height <= 0) return 0;
            return text.Length * height * WidthFactor;
        }

        /// <summary>
        /// Returns the text and height to actually draw so the result stays
        /// within <paramref name="available"/>. An available width of zero or
        /// less means "unconstrained", and the input is returned unchanged.
        /// </summary>
        public static void Fit(string text, double height, double available,
                               out string fitted, out double fittedHeight)
        {
            fitted = text ?? "";
            fittedHeight = height;

            if (available <= 0 || fitted.Length == 0 || height <= 0) return;

            // A hair of tolerance. Shrinking lands exactly on the limit, and
            // without this the width recomputed from the new height can come out
            // a whole rounding step over and trim a character for nothing.
            double slack = available * 1e-9;

            double needed = Width(fitted, height);
            if (needed <= available + slack) return;

            // Shrink first, never below the floor.
            fittedHeight = Math.Max(MinHeight, height * (available / needed));
            if (Width(fitted, fittedHeight) <= available + slack) return;

            // Still over at the floor: keep as many characters as fit, with an
            // ellipsis standing in for what was dropped.
            double perChar = fittedHeight * WidthFactor;
            if (perChar <= 0) return;

            int fits = (int)Math.Floor((available + slack) / perChar);
            if (fits >= fitted.Length) return;

            fitted = fits > 1 ? fitted.Substring(0, fits - 1) + "…" : "";
        }
    }
}
