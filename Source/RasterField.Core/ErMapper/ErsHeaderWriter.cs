using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RasterField.ErMapper.Text;

namespace RasterField.ErMapper
{
    /// <summary>
    /// Small helper that emits the indented <c>Name Begin</c> / <c>key = value</c> /
    /// <c>Name End</c> layout ER Mapper uses when it writes header files. Also knows how to
    /// re-emit an untyped <see cref="ErsBlock"/> subtree verbatim, which is how the writer
    /// preserves blocks the typed model does not cover (SensorInfo, WarpControl, RegionInfo,
    /// FFTInfo, vendor extensions, …).
    /// </summary>
    public sealed class ErsHeaderWriter
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private int _indent;

        private string Pad => new string('\t', _indent);

        /// <summary>Opens a block and increases the indent.</summary>
        public void BeginBlock(string name)
        {
            _sb.Append(Pad).Append(name).Append(" Begin").Append('\n');
            _indent++;
        }

        /// <summary>Closes a block and decreases the indent.</summary>
        public void EndBlock(string name)
        {
            if (_indent > 0) _indent--;
            _sb.Append(Pad).Append(name).Append(" End").Append('\n');
        }

        /// <summary>Writes <c>key = "value"</c> (with embedded quotes escaped).</summary>
        public void Quoted(string key, string value) =>
            _sb.Append(Pad).Append(key).Append(" = \"").Append(value.Replace("\"", "\\\"")).Append("\"").Append('\n');

        /// <summary>Writes <c>key = value</c> verbatim (no quoting).</summary>
        public void Raw(string key, string value) =>
            _sb.Append(Pad).Append(key).Append(" = ").Append(value).Append('\n');

        /// <summary>Writes an unquoted keyword value.</summary>
        public void Keyword(string key, string value) => Raw(key, value);

        /// <summary>Writes a numeric value using invariant culture with a round-trip format.</summary>
        public void Number(string key, double value) =>
            Raw(key, value.ToString("R", CultureInfo.InvariantCulture));

        /// <summary>Writes an integral numeric value.</summary>
        public void Number(string key, long value) =>
            Raw(key, value.ToString(CultureInfo.InvariantCulture));

        /// <summary>Re-emits a scalar entry using its original text (arrays are written on one line).</summary>
        public void RawEntry(string key, ErsValue value)
        {
            if (value.IsArray)
            {
                _sb.Append(Pad).Append(key).Append(" = { ");
                for (int i = 0; i < value.ArrayItems.Count; i++)
                {
                    if (i > 0) _sb.Append(' ');
                    _sb.Append(value.ArrayItems[i]);
                }
                _sb.Append(" }").Append('\n');
            }
            else
            {
                Raw(key, value.Raw);
            }
        }

        /// <summary>Re-emits an entire block subtree verbatim (entries then child blocks, in declared order).</summary>
        public void RawBlock(ErsBlock block)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            BeginBlock(block.Name);
            foreach (var e in block.Entries) RawEntry(e.Key, e.Value);
            foreach (var child in block.Children) RawBlock(child);
            EndBlock(block.Name);
        }

        /// <summary>
        /// Re-emits every entry and child block of <paramref name="source"/> that the typed
        /// model did not already write, identified by <paramref name="handledEntries"/> and
        /// <paramref name="handledBlocks"/> (both matched case-insensitively).
        /// </summary>
        public void EmitUnhandled(ErsBlock? source, ISet<string> handledEntries, ISet<string> handledBlocks)
        {
            if (source == null) return;

            foreach (var e in source.Entries)
                if (!handledEntries.Contains(e.Key))
                    RawEntry(e.Key, e.Value);

            foreach (var child in source.Children)
                if (!handledBlocks.Contains(child.Name))
                    RawBlock(child);
        }

        /// <inheritdoc />
        public override string ToString() => _sb.ToString();
    }
}
