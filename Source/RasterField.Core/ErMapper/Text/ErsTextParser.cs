using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RasterField.ErMapper.Text
{
    /// <summary>
    /// Parses the ER Mapper ASCII file syntax into an <see cref="ErsBlock"/> tree.
    /// </summary>
    /// <remarks>
    /// Implements the rules from the <i>ERDAS ER Mapper Customization Guide</i>,
    /// "File Syntax" chapter:
    /// <list type="bullet">
    ///   <item>Blank lines are ignored.</item>
    ///   <item><c>#</c> starts a comment that runs to the end of the line (unless inside a quoted string).</item>
    ///   <item>Entries have the form <c>parameter_name = value</c>; parameter names are case-insensitive.</item>
    ///   <item>Related entries are grouped in <c>Name Begin</c> ... <c>Name End</c> blocks, which may nest.</item>
    ///   <item>Array values are wrapped in <c>{ ... }</c> and may span several lines.</item>
    ///   <item>Indentation is decorative and ignored.</item>
    /// </list>
    /// The parser is deliberately tolerant: a mismatched <c>End</c> keyword closes the
    /// current block rather than throwing, and unrecognised lines are skipped.
    /// </remarks>
    public static class ErsTextParser
    {
        /// <summary>Parses ER Mapper ASCII text held in a string.</summary>
        public static ErsBlock Parse(string text)
        {
            using var reader = new StringReader(text ?? string.Empty);
            return Parse(reader);
        }

        /// <summary>Parses ER Mapper ASCII text from a stream. The stream is not disposed.</summary>
        public static ErsBlock Parse(Stream stream, Encoding? encoding = null)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using var reader = new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
            return Parse(reader);
        }

        /// <summary>Parses ER Mapper ASCII text from an arbitrary <see cref="TextReader"/>.</summary>
        public static ErsBlock Parse(TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            var root = new ErsBlock("Root");
            var stack = new Stack<ErsBlock>();
            stack.Push(root);

            string? raw;
            while ((raw = reader.ReadLine()) != null)
            {
                string line = StripComment(raw).Trim();
                if (line.Length == 0) continue;

                if (TryMatchKeyword(line, "Begin", out string beginName))
                {
                    var child = new ErsBlock(beginName);
                    stack.Peek().AddChild(child);
                    stack.Push(child);
                    continue;
                }

                if (TryMatchKeyword(line, "End", out _))
                {
                    if (stack.Count > 1) stack.Pop();
                    continue;
                }

                int eq = IndexOfTopLevelEquals(line);
                if (eq < 0) continue; // stray token – ignore

                string key = line.Substring(0, eq).Trim();
                string valueText = line.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;

                if (valueText.StartsWith("{", StringComparison.Ordinal))
                {
                    var items = ReadArray(valueText, reader);
                    stack.Peek().AddEntry(key, new ErsValue(items));
                }
                else
                {
                    stack.Peek().AddEntry(key, new ErsValue(valueText));
                }
            }

            return root;
        }

        /// <summary>Removes a trailing <c>#</c> comment, respecting double-quoted strings.</summary>
        private static string StripComment(string line)
        {
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"' && (i == 0 || line[i - 1] != '\\'))
                    inQuotes = !inQuotes;
                else if (c == '#' && !inQuotes)
                    return line.Substring(0, i);
            }
            return line;
        }

        /// <summary>Matches <c>"&lt;name&gt; &lt;keyword&gt;"</c> (e.g. <c>RasterInfo Begin</c>).</summary>
        private static bool TryMatchKeyword(string line, string keyword, out string name)
        {
            name = string.Empty;
            if (line.Length <= keyword.Length) return false;
            if (!line.EndsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;

            string head = line.Substring(0, line.Length - keyword.Length);
            if (head.Length == 0 || !char.IsWhiteSpace(head[head.Length - 1])) return false;
            head = head.TrimEnd();
            if (head.Length == 0) return false;
            foreach (char c in head)
                if (char.IsWhiteSpace(c) || c == '=') return false; // block headers are a single identifier

            name = head;
            return true;
        }

        /// <summary>Index of the first <c>=</c> that is not inside a quoted string.</summary>
        private static int IndexOfTopLevelEquals(string line)
        {
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"' && (i == 0 || line[i - 1] != '\\')) inQuotes = !inQuotes;
                else if (c == '=' && !inQuotes) return i;
            }
            return -1;
        }

        /// <summary>Collects the whitespace separated items of a <c>{ ... }</c> array, possibly spanning lines.</summary>
        private static List<string> ReadArray(string firstLine, TextReader reader)
        {
            var sb = new StringBuilder();
            string current = firstLine;
            while (true)
            {
                int close = current.IndexOf('}');
                if (close >= 0)
                {
                    sb.Append(' ').Append(current.Substring(0, close));
                    break;
                }
                sb.Append(' ').Append(current);
                string? next = reader.ReadLine();
                if (next == null) break;
                current = StripComment(next);
            }

            string body = sb.ToString().Replace("{", " ").Replace("}", " ");
            var items = new List<string>();
            foreach (var tok in body.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                items.Add(tok);
            return items;
        }
    }
}
