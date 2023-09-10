using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;

namespace Meds.Watchdog.Utils
{
    public sealed class AutoCompleteTree<T>
    {
        private readonly struct PrefixString : IEquatable<PrefixString>
        {
            public int Length { get; }
            private readonly string _str;
            private readonly int _hash;

            public PrefixString(string str, int len = -1)
            {
                _str = str;
                Length = len >= 0 ? len : str.Length;
                // FNV
                _hash = -2128831035;
                for (var i = 0; i < Length; i++)
                {
                    var c = char.ToLowerInvariant(str[i]);
                    _hash = (_hash * 16777619) ^ c;
                }
            }

            public PrefixString WithNewLength(int len) => new PrefixString(_str, len);

            public bool StartsWith(PrefixString other)
            {
                var l = Span;
                var r = other.Span;
                if (r.Length > l.Length)
                    return false;
                for (var i = 0; i < r.Length; i++)
                    if (char.ToLowerInvariant(l[i]) != char.ToLowerInvariant(r[i]))
                        return false;
                return true;
            }

            public bool SharesPrefix(PrefixString other)
            {
                var l = Span;
                var r = other.Span;
                var len = Math.Min(l.Length, r.Length);
                for (var i = 0; i < len; i++)
                    if (char.ToLowerInvariant(l[i]) != char.ToLowerInvariant(r[i]))
                        return false;
                return true;
            }

            private ReadOnlySpan<char> Span => _str.AsSpan().Slice(0, Length);

            public override string ToString() => Length == _str.Length ? _str : Span.ToString();

            public bool Equals(PrefixString other)
            {
                if (Length != other.Length || _hash != other._hash)
                    return false;
                return StartsWith(other);
            }

            public override bool Equals(object obj) => obj is PrefixString other && Equals(other);

            public override int GetHashCode() => _hash;
        }

        private class Node
        {
            public readonly PrefixString Name;
            public List<Node> Children;
            public List<T> Objects;
            public int Count;

            public Node(PrefixString name) => Name = name;
        }

        private readonly Node _root;

        public AutoCompleteTree(IEnumerable<(string key, T value)> entries)
        {
            var working = new Dictionary<PrefixString, Node>();
            var workingPrefixLength = 0;
            foreach (var entry in entries)
            {
                var name = new PrefixString(entry.key);
                if (!working.TryGetValue(name, out var node))
                    working.Add(name, node = new Node(name) { Objects = new List<T>() });
                node.Objects.Add(entry.value);
                node.Count++;
                if (name.Length > workingPrefixLength)
                    workingPrefixLength = name.Length;
            }


            var tempLists = new Queue<List<Node>>();
            var byPrefix = new Dictionary<PrefixString, List<Node>>();
            while (working.Count > 1)
            {
                workingPrefixLength--;

                // Group them by the prefix
                foreach (var node in working.Values)
                    if (node.Name.Length >= workingPrefixLength)
                    {
                        var prefix = node.Name.WithNewLength(workingPrefixLength);
                        if (!byPrefix.TryGetValue(prefix, out var group))
                            byPrefix.Add(prefix, group = tempLists.Count > 0 ? tempLists.Dequeue() : new List<Node>());
                        group.Add(node);
                    }

                // Collect all multi nodes
                foreach (var prefix in byPrefix)
                    if (prefix.Value.Count > 1)
                    {
                        if (!working.TryGetValue(prefix.Key, out var node))
                            working.Add(prefix.Key, node = new Node(prefix.Key));
                        node.Children ??= new List<Node>(prefix.Value.Count);
                        foreach (var child in prefix.Value)
                        {
                            node.Children.Add(child);
                            node.Count += child.Count;
                            working.Remove(child.Name);
                        }
                    }

                // Return and reset shared state
                foreach (var v in byPrefix.Values)
                {
                    v.Clear();
                    tempLists.Enqueue(v);
                }

                byPrefix.Clear();
            }

            _root = working.Values.FirstOrDefault();
        }

        private Queue<Node> RecommendRoots(string prompt)
        {
            var prefixPrompt = new PrefixString(prompt);
            var current = _root;
            var explore = new Queue<Node>();
            while (true)
            {
                if (current.Children == null)
                {
                    explore.Enqueue(current);
                    return explore;
                }

                explore.Clear();
                foreach (var child in current.Children)
                    if (prefixPrompt.SharesPrefix(child.Name))
                        explore.Enqueue(child);
                switch (explore.Count)
                {
                    // No child nodes match, so the only node to visit is the root node.
                    case 0:
                        explore.Enqueue(current);
                        return explore;
                    // Exactly one child node matches, so drill down.
                    case 1:
                        current = explore.Dequeue();
                        continue;
                    // Multiple child nodes match, so explore all of them.
                    default:
                        return explore;
                }
            }
        }

        public readonly struct Result
        {
            public readonly string Key;
            public readonly int Objects;
            public readonly T Data;

            public Result(string key, int objects, T data)
            {
                Key = key;
                Objects = objects;
                Data = data;
            }
        }

        public IEnumerable<Result> Apply(string prompt, int? optionalLimit = null)
        {
            var temp = new List<Result>();
            if (_root == null)
                return temp;

            var limit = optionalLimit ?? 10;
            var nodes = RecommendRoots(prompt);
            while (nodes.Count > 0 && nodes.Count + temp.Count < limit)
            {
                // Below the limit so explode the nodes.
                // Theoretically it would be better to pick the node with the shortest prefix here...
                var node = nodes.Dequeue();

                var fragmentCount = (node.Objects?.Count ?? 0) + (node.Children?.Count ?? 0);
                if (nodes.Count + temp.Count + fragmentCount > limit)
                {
                    // Not enough space for the children, so just add this node.
                    temp.Add(new Result(node.Name.ToString(), node.Count, default));
                    continue;
                }

                // All the children can be added, so go ahead and do that.
                if (node.Objects != null)
                {
                    var key = node.Name.ToString();
                    foreach (var obj in node.Objects)
                        temp.Add(new Result(key, 1, obj));
                }

                if (node.Children != null)
                    foreach (var child in node.Children)
                        nodes.Enqueue(child);
            }

            // Directly add the remaining elements
            while (nodes.Count > 0)
            {
                var node = nodes.Dequeue();
                temp.Add(new Result(node.Name.ToString(), node.Count, node.Objects is { Count: 1 } ? node.Objects[0] : default));
            }

            return temp;
        }
    }

    public abstract class DiscordAutoCompleter<T> : IAutocompleteProvider
    {
        private static readonly IEqualityComparer<T> EqualityComparer = EqualityComparer<T>.Default;

        protected abstract IEnumerable<AutoCompleteTree<T>.Result> Provide(AutocompleteContext ctx, string prefix);

        protected virtual string FormatData(string key, T data) => key;

        protected virtual string FormatPrefix(string key, int count) => $"{key}... ({count})";

        protected virtual string Format(AutoCompleteTree<T>.Result result)
        {
            return EqualityComparer.Equals(result.Data, default)
                ? FormatPrefix(result.Key, result.Objects)
                : FormatData(result.Key, result.Data);
        }

        protected abstract string FormatArgument(T data);

        public Task<IEnumerable<DiscordAutoCompleteChoice>> Provider(AutocompleteContext ctx)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var prefix = ctx.OptionValue?.ToString() ?? "";
            var autoCompleted = Provide(ctx, prefix).OrderBy(x => x.Key);
            var formatted = autoCompleted.Select(result => new DiscordAutoCompleteChoice(
                Format(result),
                EqualityComparer.Equals(result.Data, default) ? result.Key : FormatArgument(result.Data)));
            return Task.FromResult(formatted);
        }
    }
}