using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AiCad.Config;

namespace AiCad.UI
{
    /// <summary>
    /// Settings dialog. Keys are typed by the user and stored under their own
    /// AppData folder; nothing is written into the project.
    /// </summary>
    public class SettingsForm : Form
    {
        private readonly AiCadConfig _config;

        private ComboBox _provider;
        private TextBox _claudeKey;
        private TextBox _claudeModel;
        private TextBox _geminiKey;
        private TextBox _geminiModel;
        private TextBox _units;
        private TextBox _maxTokens;
        private TextBox _timeout;
        private TextBox _thinking;
        private TextBox _serverUrl;
        private TextBox _serverModel;
        private TextBox _serverKey;
        private TextBox _fallback;
        private TextBox _extra;
        private CheckBox _showKeys;
        private Label _keyStatus;

        /// <summary>
        /// The caller's copy is deliberately ignored in favour of a fresh read.
        /// A cached config can predate changes made in the other UI or in the
        /// file itself, and saving it would silently wipe a newer API key.
        /// </summary>
        public SettingsForm(AiCadConfig config)
        {
            _config = AiCadConfig.Load();
            BuildUi();
            LoadValues();
        }

        private void BuildUi()
        {
            Text = "AiCad settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 610);

            int y = 14;
            const int labelX = 14;
            const int fieldX = 150;
            const int fieldW = 350;
            const int rowH = 30;

            AddLabel("AI provider", labelX, y + 3);
            _provider = new ComboBox();
            _provider.DropDownStyle = ComboBoxStyle.DropDownList;
            _provider.Items.Add("claude");
            _provider.Items.Add("gemini");
            _provider.Items.Add("groq");
            _provider.Items.Add("openrouter");
            _provider.Items.Add("ollama");
            _provider.Items.Add("openai");
            _provider.SetBounds(fieldX, y, 160, 22);
            Controls.Add(_provider);
            y += rowH;

            AddLabel("Anthropic API key", labelX, y + 3);
            _claudeKey = AddField(fieldX, y, fieldW);
            _claudeKey.UseSystemPasswordChar = true;
            y += rowH;

            AddLabel("Claude model", labelX, y + 3);
            _claudeModel = AddField(fieldX, y, fieldW);
            y += rowH;

            AddLabel("Gemini API key", labelX, y + 3);
            _geminiKey = AddField(fieldX, y, fieldW);
            _geminiKey.UseSystemPasswordChar = true;
            y += rowH;

            AddLabel("Gemini model", labelX, y + 3);
            _geminiModel = AddField(fieldX, y, fieldW);
            y += rowH;

            AddLabel("Server URL", labelX, y + 3);
            _serverUrl = AddField(fieldX, y, fieldW);
            y += rowH;

            AddLabel("Server model", labelX, y + 3);
            _serverModel = AddField(fieldX, y, fieldW);
            y += rowH;

            AddLabel("Server key", labelX, y + 3);
            _serverKey = AddField(fieldX, y, fieldW);
            _serverKey.UseSystemPasswordChar = true;
            y += rowH;

            AddLabel("Fallback models", labelX, y + 3);
            _fallback = AddField(fieldX, y, fieldW);
            y += rowH;

            Label chainHint = new Label();
            chainHint.Text = "Fallbacks are tried in order when the main model is out of quota. " +
                             "Comma separated, e.g. gemini-3.8-flash, gemini-flash-latest";
            chainHint.SetBounds(fieldX, y, fieldW, 30);
            chainHint.ForeColor = SystemColors.GrayText;
            Controls.Add(chainHint);
            y += 34;

            _showKeys = new CheckBox();
            _showKeys.Text = "Show keys";
            _showKeys.SetBounds(fieldX, y, 120, 22);
            _showKeys.CheckedChanged += delegate
            {
                _claudeKey.UseSystemPasswordChar = !_showKeys.Checked;
                _geminiKey.UseSystemPasswordChar = !_showKeys.Checked;
                _serverKey.UseSystemPasswordChar = !_showKeys.Checked;
            };
            Controls.Add(_showKeys);

            // Confirms a key is already stored, so it never needs retyping.
            _keyStatus = new Label();
            _keyStatus.SetBounds(fieldX + 130, y + 3, 220, 20);
            _keyStatus.ForeColor = SystemColors.GrayText;
            Controls.Add(_keyStatus);
            y += rowH;

            AddLabel("Drawing units", labelX, y + 3);
            _units = AddField(fieldX, y, 160);
            y += rowH;

            AddLabel("Max tokens", labelX, y + 3);
            _maxTokens = AddField(fieldX, y, 100);
            // Narrow label here: the default 130px width overlapped the field.
            AddLabel("Timeout (s)", fieldX + 120, y + 3, 75);
            _timeout = AddField(fieldX + 200, y, 95);
            y += rowH;

            AddLabel("Thinking budget", labelX, y + 3);
            _thinking = AddField(fieldX, y, 100);
            Label thinkHint = new Label();
            thinkHint.Text = "Gemini only. 0 = off, -1 = model decides.";
            thinkHint.SetBounds(fieldX + 110, y + 3, 260, 20);
            thinkHint.ForeColor = SystemColors.GrayText;
            Controls.Add(thinkHint);
            y += rowH;

            AddLabel("House standards", labelX, y + 3);
            _extra = new TextBox();
            _extra.Multiline = true;
            _extra.ScrollBars = ScrollBars.Vertical;
            _extra.SetBounds(fieldX, y, fieldW, 90);
            Controls.Add(_extra);
            y += 100;

            Label hint = new Label();
            hint.Text = "Extra instructions sent with every request, e.g. preferred layer names, " +
                        "text heights, or company drafting conventions.";
            hint.SetBounds(labelX, y, 490, 34);
            hint.ForeColor = SystemColors.GrayText;
            Controls.Add(hint);
            y += 40;

            Button ok = new Button();
            ok.Text = "Save";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(330, y, 80, 26);
            ok.Click += OnSave;
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(420, y, 80, 26);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void AddLabel(string text, int x, int y)
        {
            AddLabel(text, x, y, 130);
        }

        private void AddLabel(string text, int x, int y, int width)
        {
            Label label = new Label();
            label.Text = text;
            label.SetBounds(x, y, width, 20);
            Controls.Add(label);
        }

        private TextBox AddField(int x, int y, int width)
        {
            TextBox box = new TextBox();
            box.SetBounds(x, y, width, 22);
            Controls.Add(box);
            return box;
        }

        private void LoadValues()
        {
                        string current = (_config.Provider ?? "claude").Trim().ToLowerInvariant();
            if (!_provider.Items.Contains(current)) current = _config.IsGemini ? "gemini" : "claude";
            _provider.SelectedItem = current;
            _claudeKey.Text = _config.ClaudeApiKey;
            _claudeModel.Text = _config.ClaudeModel;
            _geminiKey.Text = _config.GeminiApiKey;
            _geminiModel.Text = _config.GeminiModel;
            _serverUrl.Text = _config.OpenAiBaseUrl;
            _serverModel.Text = _config.OpenAiModel;
            _serverKey.Text = _config.OpenAiApiKey;
            _fallback.Text = _config.FallbackModels;
            _units.Text = _config.Units;
            _maxTokens.Text = _config.MaxTokens.ToString(CultureInfo.InvariantCulture);
            _timeout.Text = _config.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            _thinking.Text = _config.ThinkingBudget.ToString(CultureInfo.InvariantCulture);
            _extra.Text = _config.ExtraInstructions;

            // Report presence and length only - never echo the key itself.
            string active = _config.ActiveKey;
            _keyStatus.Text = string.IsNullOrEmpty(active)
                ? "No key stored for this provider."
                : "Key stored (" + active.Length.ToString(CultureInfo.InvariantCulture) + " chars).";
        }

        private void OnSave(object sender, EventArgs e)
        {
            _config.Provider = _provider.SelectedItem as string;
            _config.ClaudeApiKey = _claudeKey.Text.Trim();
            _config.ClaudeModel = _claudeModel.Text.Trim();
            _config.GeminiApiKey = _geminiKey.Text.Trim();
            _config.GeminiModel = _geminiModel.Text.Trim();
            _config.OpenAiBaseUrl = _serverUrl.Text.Trim();
            _config.OpenAiModel = _serverModel.Text.Trim();
            _config.OpenAiApiKey = _serverKey.Text.Trim();
            _config.FallbackModels = _fallback.Text.Trim();
            _config.Units = _units.Text.Trim();
            _config.ExtraInstructions = _extra.Text;

            int n;
            if (int.TryParse(_maxTokens.Text.Trim(), out n)) _config.MaxTokens = n;
            if (int.TryParse(_timeout.Text.Trim(), out n)) _config.TimeoutSeconds = n;
            if (int.TryParse(_thinking.Text.Trim(), out n)) _config.ThinkingBudget = n;

            try
            {
                _config.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save settings: " + ex.Message, "AiCad",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        }
    }
}
