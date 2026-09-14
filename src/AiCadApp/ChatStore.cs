using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AiCad.Ai;
using AiCad.Config;
using AiCad.Json;

namespace AiCadApp
{
    /// <summary>One saved conversation, as listed in the sidebar.</summary>
    public class ChatSummary
    {
        public string Id;
        public string Title;
        public DateTime Updated;
        /// <summary>"2d" or "3d" - what the composer was set to when it was saved.</summary>
        public string Mode;

        public override string ToString()
        {
            return Title;
        }
    }

    /// <summary>
    /// Conversations on disk, one JSON file each, beside the settings. Saves
    /// both the visible transcript and the model-facing history, so reopening a
    /// chat restores the context the model had.
    /// </summary>
    public static class ChatStore
    {
        public static string Folder
        {
            get { return Path.Combine(AiCadConfig.Folder, "chats"); }
        }

        public static string NewId()
        {
            return DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                   "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static string PathFor(string id)
        {
            return Path.Combine(Folder, id + ".json");
        }

        /// <summary>First line of the first user message makes a usable title.</summary>
        public static string DeriveTitle(List<ChatMessage> messages)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role != ChatRole.User) continue;
                string t = (messages[i].Text ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
                if (t.Length == 0) continue;
                return t.Length <= 46 ? t : t.Substring(0, 44) + "...";
            }
            return "New chat";
        }

        public static void Save(string id, List<ChatMessage> messages, List<ChatTurn> history)
        {
            Save(id, messages, history, null);
        }

        public static void Save(string id, List<ChatMessage> messages, List<ChatTurn> history,
                                string mode)
        {
            if (string.IsNullOrEmpty(id) || messages == null || messages.Count == 0) return;

            try
            {
                Directory.CreateDirectory(Folder);

                JsonValue root = JsonValue.NewObject();
                root["id"] = JsonValue.New(id);
                root["title"] = JsonValue.New(DeriveTitle(messages));
                root["updated"] = JsonValue.New(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                root["mode"] = JsonValue.New(mode == "3d" ? "3d" : "2d");

                JsonValue list = JsonValue.NewArray();
                for (int i = 0; i < messages.Count; i++)
                {
                    JsonValue m = JsonValue.NewObject();
                    m["role"] = JsonValue.New(messages[i].Role.ToString());
                    m["text"] = JsonValue.New(messages[i].Text ?? "");
                    list.Add(m);
                }
                root["messages"] = list;

                JsonValue turns = JsonValue.NewArray();
                if (history != null)
                {
                    for (int i = 0; i < history.Count; i++)
                    {
                        JsonValue t = JsonValue.NewObject();
                        t["role"] = JsonValue.New(history[i].Role ?? "user");
                        t["content"] = JsonValue.New(history[i].Content ?? "");
                        turns.Add(t);
                    }
                }
                root["history"] = turns;

                string path = PathFor(id);
                string temp = path + ".tmp";
                File.WriteAllText(temp, root.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception)
            {
                // Losing a saved transcript must never interrupt drawing.
            }
        }

        public static bool Load(string id, out List<ChatMessage> messages, out List<ChatTurn> history)
        {
            messages = new List<ChatMessage>();
            history = new List<ChatTurn>();
            try
            {
                string path = PathFor(id);
                if (!File.Exists(path)) return false;

                JsonValue root = JsonValue.ParseLenient(File.ReadAllText(path, Encoding.UTF8));
                if (root == null) return false;

                List<JsonValue> list = root.GetArray("messages");
                for (int i = 0; i < list.Count; i++)
                {
                    ChatRole role = ParseRole(list[i].GetString("role", "System"));
                    messages.Add(new ChatMessage(role, list[i].GetString("text", "")));
                }

                List<JsonValue> turns = root.GetArray("history");
                for (int i = 0; i < turns.Count; i++)
                {
                    history.Add(new ChatTurn(turns[i].GetString("role", "user"),
                                             turns[i].GetString("content", "")));
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ChatRole ParseRole(string name)
        {
            if (string.Equals(name, "User", StringComparison.OrdinalIgnoreCase)) return ChatRole.User;
            if (string.Equals(name, "Assistant", StringComparison.OrdinalIgnoreCase)) return ChatRole.Assistant;
            if (string.Equals(name, "Error", StringComparison.OrdinalIgnoreCase)) return ChatRole.Error;
            if (string.Equals(name, "Success", StringComparison.OrdinalIgnoreCase)) return ChatRole.Success;
            return ChatRole.System;
        }

        /// <summary>Most recently updated first.</summary>
        public static List<ChatSummary> List(int max)
        {
            List<ChatSummary> all = new List<ChatSummary>();
            try
            {
                if (!Directory.Exists(Folder)) return all;

                string[] files = Directory.GetFiles(Folder, "*.json");
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        JsonValue root = JsonValue.ParseLenient(File.ReadAllText(files[i], Encoding.UTF8));
                        if (root == null) continue;

                        ChatSummary summary = new ChatSummary();
                        summary.Id = root.GetString("id", Path.GetFileNameWithoutExtension(files[i]));
                        summary.Title = root.GetString("title", "Chat");
                        summary.Updated = File.GetLastWriteTimeUtc(files[i]);
                        summary.Mode = root.GetString("mode", "2d");
                        all.Add(summary);
                    }
                    catch (Exception) { }
                }

                all.Sort(delegate(ChatSummary a, ChatSummary b)
                {
                    return b.Updated.CompareTo(a.Updated);
                });

                if (all.Count > max) all.RemoveRange(max, all.Count - max);
            }
            catch (Exception)
            {
            }
            return all;
        }

        public static void Delete(string id)
        {
            try
            {
                string path = PathFor(id);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }
}
