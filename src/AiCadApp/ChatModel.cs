namespace AiCadApp
{
    public enum ChatRole { User, Assistant, System, Error, Success }

    /// <summary>
    /// One line of a transcript. Kept free of UI types so the WinForms app, the
    /// local web server and the chat store can all share it.
    /// </summary>
    public class ChatMessage
    {
        public ChatRole Role;
        public string Text;

        /// <summary>Text with over-long tokens broken so it fits the current width.</summary>
        internal string Display;

        public ChatMessage(ChatRole role, string text)
        {
            Role = role;
            Text = text ?? "";
            Display = Text;
        }
    }
}
