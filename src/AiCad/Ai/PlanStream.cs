using System;
using System.Text;

namespace AiCad.Ai
{
    /// <summary>
    /// Counts the operations in a plan as it is being written.
    ///
    /// A plan arrives as one JSON document, so nothing can be reported about it
    /// until the last brace - which for a large drawing is minutes of silence.
    /// The text is not valid JSON until then and cannot be parsed, but it does
    /// not need to be: an op is a complete object inside the "ops" array, and
    /// counting braces is enough to know when one has closed.
    ///
    /// Deliberately forgiving. It is driving a progress message, so an
    /// undercount while the model is mid-object is harmless and a miscount never
    /// affects the drawing - the finished text is parsed properly as before.
    /// </summary>
    public sealed class PlanStream
    {
        private readonly StringBuilder _text = new StringBuilder();

        private bool _inOps;          // the "ops" array has started
        private int _depth;           // brace depth inside that array
        private bool _inString;
        private bool _escaped;
        private int _opsClosed;

        /// <summary>Complete operations seen so far.</summary>
        public int OpsComplete { get { return _opsClosed; } }

        /// <summary>Everything received, for the final parse.</summary>
        public string Text { get { return _text.ToString(); } }

        /// <summary>Feeds another fragment of the reply. Returns the new count.</summary>
        public int Append(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return _opsClosed;

            int from = _text.Length;
            _text.Append(fragment);

            string all = _text.ToString();
            for (int i = from; i < all.Length; i++) Consume(all, i);

            return _opsClosed;
        }

        private void Consume(string all, int i)
        {
            char c = all[i];

            // Inside a string literal nothing is structural.
            if (_escaped) { _escaped = false; return; }
            if (c == '\\' && _inString) { _escaped = true; return; }
            if (c == '"') { _inString = !_inString; return; }
            if (_inString) return;

            if (!_inOps)
            {
                // The array opens right after the "ops" key.
                if (c == '[' && EndsWithOpsKey(all, i)) { _inOps = true; _depth = 0; }
                return;
            }

            if (c == '{') _depth++;
            else if (c == '}')
            {
                _depth--;
                // Depth back to zero means one whole operation closed. Nested
                // ops - a repeat or a group - close with their parent, which is
                // the honest count: one operation the user asked for.
                if (_depth == 0) _opsClosed++;
                if (_depth < 0) _depth = 0;
            }
            else if (c == ']' && _depth == 0)
            {
                _inOps = false;   // the array finished
            }
        }

        /// <summary>True when "ops" and a colon sit just before this bracket.</summary>
        private static bool EndsWithOpsKey(string all, int bracket)
        {
            int i = bracket - 1;
            while (i >= 0 && char.IsWhiteSpace(all[i])) i--;
            if (i < 0 || all[i] != ':') return false;

            i--;
            while (i >= 0 && char.IsWhiteSpace(all[i])) i--;
            if (i < 0 || all[i] != '"') return false;

            // i is now the closing quote of the key, so the key itself starts
            // key.Length back from it and the opening quote sits before that.
            const string key = "ops";
            int start = i - key.Length;
            if (start < 1) return false;

            return all[start - 1] == '"'
                && string.CompareOrdinal(all, start, key, 0, key.Length) == 0;
        }
    }
}
