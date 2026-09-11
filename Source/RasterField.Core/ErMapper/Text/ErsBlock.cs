using System;
using System.Collections.Generic;
using System.Linq;

namespace RasterField.ErMapper.Text
{
    /// <summary>
    /// A <c>Name Begin</c> / <c>Name End</c> block from an ER Mapper ASCII file.
    /// Blocks form a tree: each block owns an ordered list of scalar entries
    /// (<c>key = value</c>) and an ordered list of nested child blocks. Lookups are
    /// case-insensitive because ER Mapper ignores character case in parameter names.
    /// </summary>
    public sealed class ErsBlock
    {
        private readonly List<KeyValuePair<string, ErsValue>> _entries = new List<KeyValuePair<string, ErsValue>>();
        private readonly List<ErsBlock> _children = new List<ErsBlock>();

        /// <summary>Creates a new, empty block.</summary>
        public ErsBlock(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }

        /// <summary>The block name (without the <c>Begin</c>/<c>End</c> keyword).</summary>
        public string Name { get; }

        /// <summary>The parent block, or <see langword="null"/> for the synthetic root.</summary>
        public ErsBlock? Parent { get; private set; }

        /// <summary>Ordered scalar entries declared directly in this block.</summary>
        public IReadOnlyList<KeyValuePair<string, ErsValue>> Entries => _entries;

        /// <summary>Ordered child blocks declared directly in this block.</summary>
        public IReadOnlyList<ErsBlock> Children => _children;

        /// <summary>Adds a scalar entry, preserving declaration order and allowing duplicates.</summary>
        public void AddEntry(string key, ErsValue value) =>
            _entries.Add(new KeyValuePair<string, ErsValue>(key, value));

        /// <summary>Adds (and re-parents) a child block.</summary>
        public void AddChild(ErsBlock child)
        {
            if (child == null) throw new ArgumentNullException(nameof(child));
            child.Parent = this;
            _children.Add(child);
        }

        /// <summary>Tries to read a scalar entry by name (case-insensitive, first match wins).</summary>
        public bool TryGet(string key, out ErsValue value)
        {
            foreach (var e in _entries)
            {
                if (string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = e.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        /// <summary>Returns a scalar entry by name, or <see langword="null"/> when absent.</summary>
        public ErsValue? Get(string key) => TryGet(key, out var v) ? v : (ErsValue?)null;

        /// <summary>Indexer form of <see cref="Get(string)"/>; throws when the entry is missing.</summary>
        public ErsValue this[string key] =>
            TryGet(key, out var v) ? v : throw new KeyNotFoundException($"Entry '{key}' not found in block '{Name}'.");

        /// <summary>All direct child blocks with the given name (case-insensitive).</summary>
        public IEnumerable<ErsBlock> Blocks(string name) =>
            _children.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The first direct child block with the given name, or <see langword="null"/>.</summary>
        public ErsBlock? Block(string name) => Blocks(name).FirstOrDefault();

        /// <summary>Depth-first search for the first descendant (or self) block with the given name.</summary>
        public ErsBlock? FindBlock(string name)
        {
            if (string.Equals(Name, name, StringComparison.OrdinalIgnoreCase)) return this;
            foreach (var c in _children)
            {
                var hit = c.FindBlock(name);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <inheritdoc />
        public override string ToString() => $"{Name} ({_entries.Count} entries, {_children.Count} blocks)";
    }
}
