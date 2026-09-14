using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace AiCadApp
{
    /// <summary>Colours for the whole app, kept in one place.</summary>
    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(38, 38, 36);
        public static readonly Color Surface = Color.FromArgb(48, 48, 46);
        public static readonly Color SurfaceRaised = Color.FromArgb(58, 58, 55);
        public static readonly Color Border = Color.FromArgb(70, 70, 66);

        public static readonly Color TextPrimary = Color.FromArgb(232, 230, 227);
        public static readonly Color TextMuted = Color.FromArgb(150, 147, 141);
        public static readonly Color Accent = Color.FromArgb(201, 100, 66);
        public static readonly Color AccentText = Color.FromArgb(255, 244, 238);

        public static readonly Color UserBubble = Color.FromArgb(60, 62, 72);
        public static readonly Color ErrorText = Color.FromArgb(226, 116, 106);
        public static readonly Color SuccessText = Color.FromArgb(146, 194, 128);

        public static readonly Color ConnectedBg = Color.FromArgb(34, 58, 44);
        public static readonly Color ConnectedText = Color.FromArgb(146, 194, 128);
        public static readonly Color DisconnectedBg = Color.FromArgb(64, 38, 38);
        public static readonly Color DisconnectedText = Color.FromArgb(226, 116, 106);
    }

    /// <summary>
    /// A scrolling transcript of chat bubbles. Custom-drawn rather than a
    /// RichTextBox so messages can be laid out as bubbles, wrapped properly and
    /// themed to match the rest of the window.
    /// </summary>
    public class ChatView : Panel
    {
        private readonly List<ChatMessage> _messages = new List<ChatMessage>();
        private readonly List<Rectangle> _bubbles = new List<Rectangle>();
        private readonly List<Rectangle> _textAreas = new List<Rectangle>();

        private Font _bodyFont;
        private Font _labelFont;
        private int _contentHeight;

        private const int SidePadding = 16;
        private const int BubblePadX = 14;
        private const int BubblePadY = 10;
        private const int GapBetween = 10;
        private const int CornerRadius = 10;
        private const int LabelHeight = 16;

        /// <summary>Sidebar mode: smaller type, no bubbles, no role labels.</summary>
        public bool Compact;

        /// <summary>Shown centred while there are no messages.</summary>
        public string EmptyText;

        public ChatView() : this(false)
        {
        }

        public ChatView(bool compact)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            AutoScroll = true;
            Compact = compact;
            BackColor = compact ? Theme.Surface : Theme.Background;
            _bodyFont = new Font("Segoe UI", compact ? 8.25f : 10f);
            _labelFont = new Font("Segoe UI Semibold", 8f);
        }

        public void AddMessage(ChatRole role, string text)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(delegate { AddMessage(role, text); })); }
                catch (Exception) { }
                return;
            }

            _messages.Add(new ChatMessage(role, text));
            Relayout();
            ScrollToBottom();
            Invalidate();
        }

        /// <summary>Replaces the last message, used for the working indicator.</summary>
        public void ReplaceLast(ChatRole role, string text)
        {
            if (_messages.Count == 0) { AddMessage(role, text); return; }
            _messages[_messages.Count - 1] = new ChatMessage(role, text);
            Relayout();
            ScrollToBottom();
            Invalidate();
        }

        public void RemoveLast()
        {
            if (_messages.Count == 0) return;
            _messages.RemoveAt(_messages.Count - 1);
            Relayout();
            Invalidate();
        }

        public bool LastIs(ChatRole role)
        {
            return _messages.Count > 0 && _messages[_messages.Count - 1].Role == role;
        }

        public void Clear()
        {
            _messages.Clear();
            Relayout();
            Invalidate();
        }

        // ------------------------------------------------------------ layout

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        /// <summary>
        /// Measures every message once per change, so painting stays cheap and
        /// the scrollbar knows the true content height.
        /// </summary>
        private void Relayout()
        {
            // Assigning AutoScrollMinSize below raises OnResize, which calls back
            // in here. Without this guard the cached rectangles were left half
            // rebuilt and messages painted on top of each other.
            if (_layingOut) return;
            _layingOut = true;
            try { RelayoutCore(); }
            finally { _layingOut = false; }
        }

        private bool _layingOut;

        private void RelayoutCore()
        {
            _bubbles.Clear();
            _textAreas.Clear();

            int available = ClientSize.Width - SidePadding * 2;
            if (available < 80) available = 80;
            // Bubbles stop short of the full width so the column stays readable.
            int maxBubble = (int)(available * 0.92);

            int y = GapBetween;
            for (int i = 0; i < _messages.Count; i++)
            {
                ChatMessage m = _messages[i];
                bool bubble = !Compact && m.Role == ChatRole.User;
                int textWidth = maxBubble - BubblePadX * 2;

                // A URL has no spaces to break on, so it would overflow and be
                // clipped. Split anything wider than the column first.
                m.Display = BreakLongTokens(m.Text, textWidth);

                Size measured = TextRenderer.MeasureText(m.Display, _bodyFont,
                    new Size(textWidth, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

                int labelSpace = !Compact && (m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
                    ? LabelHeight : 0;
                int height = measured.Height + labelSpace + (bubble ? BubblePadY * 2 : 4);

                Rectangle box = new Rectangle(SidePadding, y, maxBubble, height);
                _bubbles.Add(box);
                _textAreas.Add(new Rectangle(
                    box.X + (bubble ? BubblePadX : 0),
                    box.Y + (bubble ? BubblePadY : 2) + labelSpace,
                    textWidth, measured.Height));

                y += height + GapBetween;
            }

            _contentHeight = y;
            // Only assign when it actually changes, to avoid a needless resize.
            if (AutoScrollMinSize.Height != _contentHeight)
                AutoScrollMinSize = new Size(0, _contentHeight);
        }

        private void ScrollToBottom()
        {
            if (_contentHeight > ClientSize.Height)
                AutoScrollPosition = new Point(0, _contentHeight);
        }

        // ----------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_messages.Count == 0 && !string.IsNullOrEmpty(EmptyText))
            {
                Rectangle area = new Rectangle(SidePadding, 0,
                    ClientSize.Width - SidePadding * 2, ClientSize.Height);
                TextRenderer.DrawText(g, EmptyText, _bodyFont, area, Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.WordBreak);
                return;
            }

            // Layout can lag a resize by one paint; rebuild rather than draw
            // messages at stale positions.
            if (_bubbles.Count != _messages.Count) Relayout();

            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);

            for (int i = 0; i < _messages.Count && i < _bubbles.Count; i++)
            {
                ChatMessage m = _messages[i];
                Rectangle box = _bubbles[i];
                Rectangle text = _textAreas[i];

                // Skip anything scrolled out of view.
                if (box.Bottom + AutoScrollPosition.Y < 0) continue;
                if (box.Top + AutoScrollPosition.Y > ClientSize.Height) break;

                if (!Compact && m.Role == ChatRole.User)
                {
                    using (GraphicsPath path = Rounded(box, CornerRadius))
                    using (SolidBrush fill = new SolidBrush(Theme.UserBubble))
                        g.FillPath(fill, path);
                }

                string label = null;
                if (!Compact)
                {
                    if (m.Role == ChatRole.User) label = "You";
                    else if (m.Role == ChatRole.Assistant) label = "AiCad";
                }

                if (label != null)
                {
                    Rectangle labelRect = new Rectangle(text.X, box.Y + (m.Role == ChatRole.User ? BubblePadY : 2),
                                                        text.Width, LabelHeight);
                    TextRenderer.DrawText(g, label, _labelFont, labelRect,
                        m.Role == ChatRole.User ? Theme.TextMuted : Theme.Accent,
                        TextFormatFlags.Left | TextFormatFlags.NoPadding);
                }

                TextRenderer.DrawText(g, m.Display ?? m.Text, _bodyFont, text, ColorFor(m.Role),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
        }

        private static Color ColorFor(ChatRole role)
        {
            switch (role)
            {
                case ChatRole.Error: return Theme.ErrorText;
                case ChatRole.Success: return Theme.SuccessText;
                case ChatRole.System: return Theme.TextMuted;
                default: return Theme.TextPrimary;
            }
        }

        /// <summary>
        /// Splits any run of non-space characters that is wider than the column.
        /// URLs and long error strings would otherwise be clipped, because word
        /// wrapping has nowhere to break them.
        /// </summary>
        private string BreakLongTokens(string text, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth < 20) return text;

            StringBuilder result = new StringBuilder(text.Length + 16);
            int tokenStart = 0;
            bool changed = false;

            for (int i = 0; i <= text.Length; i++)
            {
                bool boundary = i == text.Length || char.IsWhiteSpace(text[i]);
                if (!boundary) continue;

                string token = text.Substring(tokenStart, i - tokenStart);
                if (token.Length > 0 &&
                    TextRenderer.MeasureText(token, _bodyFont, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding).Width > maxWidth)
                {
                    // Emit as many characters as fit, then start a new line.
                    int chunkStart = 0;
                    for (int c = 1; c <= token.Length; c++)
                    {
                        string chunk = token.Substring(chunkStart, c - chunkStart);
                        int w = TextRenderer.MeasureText(chunk, _bodyFont,
                            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                        if (w > maxWidth && c - chunkStart > 1)
                        {
                            result.Append(token, chunkStart, c - chunkStart - 1).Append('\n');
                            chunkStart = c - 1;
                        }
                    }
                    result.Append(token, chunkStart, token.Length - chunkStart);
                    changed = true;
                }
                else
                {
                    result.Append(token);
                }

                if (i < text.Length) result.Append(text[i]);
                tokenStart = i + 1;
            }

            return changed ? result.ToString() : text;
        }

        /// <summary>Copy of the transcript, for saving a conversation.</summary>
        public List<ChatMessage> Snapshot()
        {
            return new List<ChatMessage>(_messages);
        }

        /// <summary>Replaces the transcript, for reopening a saved conversation.</summary>
        public void Load(List<ChatMessage> messages)
        {
            _messages.Clear();
            if (messages != null) _messages.AddRange(messages);
            Relayout();
            ScrollToBottom();
            Invalidate();
        }

        internal static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_bodyFont != null) _bodyFont.Dispose();
                if (_labelFont != null) _labelFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Flat dark button, so the window has no grey WinForms chrome.</summary>
    public class FlatButton : Button
    {
        private bool _primary;

        public FlatButton(string text, bool primary)
        {
            _primary = primary;
            Text = text;
            AutoSize = false;
            Height = 30;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI", 9f);
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
            ApplyColours();
        }

        private void ApplyColours()
        {
            BackColor = _primary ? Theme.Accent : Theme.SurfaceRaised;
            ForeColor = _primary ? Theme.AccentText : Theme.TextPrimary;
            FlatAppearance.MouseOverBackColor = _primary
                ? ControlPaint.Light(Theme.Accent, 0.15f)
                : Theme.Border;
            FlatAppearance.MouseDownBackColor = _primary
                ? ControlPaint.Dark(Theme.Accent, 0.05f)
                : Theme.Surface;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            ForeColor = Enabled
                ? (_primary ? Theme.AccentText : Theme.TextPrimary)
                : Theme.TextMuted;
        }
    }
}
