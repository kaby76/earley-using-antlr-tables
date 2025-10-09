// MIT License
// Earley parser over Antlr4 C# runtime ATN
// Recognition + single-derivation parse tree construction (generic ParserRuleContext).
//
// Requirements:
// - Antlr4.Runtime (C#)  https://github.com/antlr/antlr4/tree/dev/runtime/CSharp/src
// - Parser ATN (from a generated parser): parser.Atn (ATNType.PARSER)
//
// Notes:
// - We build ONE successful derivation (if ambiguous, the first found is chosen).
// - We return a tree of GenericRuleContext nodes (subclass of ParserRuleContext)
//   that reports the correct RuleIndex values, so ToStringTree(parser) prints rule names.
// - Semantic predicates and actions are treated as epsilon (not evaluated).
// - We do not add an explicit EOF terminal node.
//
// Usage example:
//   var atn = parser.Atn;
//   var tree = EarleyATN.EarleyAtnRecognizer.ParseToTree(atn, parser.TokenStream, MyParser.RULE_program);
//   Console.WriteLine(tree != null ? tree.ToStringTree(parser) : "REJECT");
//
//   // Legacy recognizer API (bool):
//   bool ok = EarleyATN.EarleyAtnRecognizer.Parse(atn, parser.TokenStream, MyParser.RULE_program);

using System;
using System.Collections.Generic;
using System.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Tree;

namespace EarleyATN
{
    public static class EarleyAtnRecognizer
    {
	    static bool debug = false;
	    
        // === Public APIs ===

        public static bool Parse(ATN atn, ITokenStream tokenStream, int startRuleIndex)
            => ParseToTree(atn, tokenStream, startRuleIndex) != null;

        /// <summary>
        /// Run Earley over the ATN and return a single-derivation parse tree (GenericRuleContext).
        /// Returns null on rejection.
        /// </summary>
        public static ParserRuleContext ParseToTree(ATN atn, ITokenStream tokenStream, int startRuleIndex)
        {
            if (atn == null) throw new ArgumentNullException(nameof(atn));
//            if (atn.GrammarType != ATNType.Parser)
//                throw new ArgumentException("ATN must be a parser ATN.", nameof(atn));
            if (startRuleIndex < 0 || startRuleIndex >= atn.ruleToStartState.Length)
                throw new ArgumentOutOfRangeException(nameof(startRuleIndex));
            if (tokenStream == null) throw new ArgumentNullException(nameof(tokenStream));

            var tokens = MaterializeTokens(tokenStream); // includes EOF as last token
            var types = new List<int>(tokens.Count);
            foreach (var t in tokens) types.Add(t.Type);

            var n = tokens.Count; // position of EOF

            // Chart S[0..n], items and backpointers
            var chart = new List<HashSet<Item>>(n + 1);
            var backs = new Dictionary<Item, List<Back>>(Item.Comparer);
            for (int k = 0; k <= n; k++) chart.Add(new HashSet<Item>(Item.Comparer));

            // Seed
            var start = atn.ruleToStartState[startRuleIndex];
            var startItem = new Item(start, origin: 0, callStack: CallStack.Empty);
            if (debug) System.Console.WriteLine("seed " + startItem);
            chart[0].Add(startItem);
            AddBack(backs, startItem, Back.Seed()); // sentinel
            Closure(atn, chart[0], backs);

            // Standard Earley loop
            for (int k = 0; k < n; k++)
            {
	        if (debug) System.Console.WriteLine("input " + k + " " + tokens[k]);

                var next = chart[k + 1];
                var a = tokens[k]; // next token type

                // Scan: for each item at S[k] whose next is terminal matching a
                foreach (var it in chart[k])
                {
                    foreach (var tr in it.State.TransitionsArray())
                    {
                        if (IsTerminalTransition(tr) && TerminalMatches(tr, a))
                        {
                            var advanced = new Item(tr.target, it.Origin, it.CallStack);
                            if (next.Add(advanced))
                            {
                                // will be expanded by closure
                                if (debug) System.Console.WriteLine("scan " + advanced + " from " + it);
                                AddBack(backs, advanced, Back.Scan(it, k));
                            }
                            else
                            {
                                // Even if item already existed, keep first back only (one-derivation)
                                if (debug) System.Console.WriteLine("back " + advanced + " from " + it);
                                if (!backs.ContainsKey(advanced)) AddBack(backs, advanced, Back.Scan(it, k));
                            }
                        }
                    }
                }
                Closure(atn, next, backs);
            }

            // Accept: RuleStopState of start rule with empty stack at position n
            Item? accept = null;
            foreach (var it in chart[n])
            {
                if (it.CallStack.IsEmpty &&
                    it.State is RuleStopState rss &&
                    rss.ruleIndex == startRuleIndex)
                {
                    accept = it;
                    break;
                }
            }
            if (accept == null) return null;

            // Reconstruct a single path of events by walking backpointers.
            var events = ReconstructEvents(accept.Value, n, backs, start);
            if (events == null) return null; // shouldn't happen

            // Build a generic ParserRuleContext tree from events.
            var tree = BuildTree(events, tokens, startRuleIndex);
            return tree;
        }

        // === Closure + backpointer capture ===

        private static void Closure(ATN atn, HashSet<Item> set, Dictionary<Item, List<Back>> backs)
        {
            var work = new Stack<Item>(set);
            var visited = new HashSet<Item>(Item.Comparer);
            foreach (var i in set) visited.Add(i);

            while (work.Count > 0)
            {
                var it = work.Pop();

                // Completion: if at RuleStopState, pop stack to continue
                if (it.State is RuleStopState rssStop)
                {
                    if (!it.CallStack.IsEmpty)
                    {
                        var (follow, rest) = it.CallStack.Pop();
                        var cont = new Item(follow, it.Origin, rest);
                        if (visited.Add(cont))
                        {
                            set.Add(cont);
                            work.Push(cont);
                            AddBack(backs, cont, Back.Complete(it, rssStop.ruleIndex));
                        }
                        else
                        {
                            if (!backs.ContainsKey(cont)) AddBack(backs, cont, Back.Complete(it, rssStop.ruleIndex));
                        }
                    }
                    continue;
                }

                foreach (var tr in it.State.TransitionsArray())
                {
                    switch (tr)
                    {
                        case RuleTransition rt:
                        {
                            var pushed = it.CallStack.Push(rt.followState);
                            var enter = new Item(rt.target, it.Origin, pushed);
                            if (visited.Add(enter))
                            {
                                set.Add(enter);
                                work.Push(enter);
                                AddBack(backs, enter, Back.Predict(it, rt.ruleIndex));
                            }
                            else
                            {
                                if (!backs.ContainsKey(enter)) AddBack(backs, enter, Back.Predict(it, rt.ruleIndex));
                            }
                            break;
                        }
                        case EpsilonTransition _:
                        case ActionTransition _:
                        case PredicateTransition _:
                        case PrecedencePredicateTransition _:
                        {
                            var adv = new Item(tr.target, it.Origin, it.CallStack);
                            if (visited.Add(adv))
                            {
                                set.Add(adv);
                                work.Push(adv);
                                AddBack(backs, adv, Back.Epsilon(it));
                            }
                            else
                            {
                                if (!backs.ContainsKey(adv)) AddBack(backs, adv, Back.Epsilon(it));
                            }
                            break;
                        }
                        default:
                            break; // terminals handled in scan
                    }
                }
            }
        }

        // === Reconstruction ===

        private static List<Event> ReconstructEvents(Item accept, int acceptPos, Dictionary<Item, List<Back>> backs, RuleStartState startState)
        {
            var ev = new List<Event>();
            var cur = accept;
            int pos = acceptPos;

            while (!(ReferenceEquals(cur.State, startState) && cur.Origin == 0 && cur.CallStack.IsEmpty))
            {
                if (!backs.TryGetValue(cur, out var blist) || blist.Count == 0)
                    return null;

                var b = blist[0]; // choose first (any succeeds due to Earley closure invariants)
                switch (b.Kind)
                {
                    case BackKind.Scan:
                        ev.Add(Event.Consume(b.TokenIndex));
                        pos = b.TokenIndex; // move back to earlier position
                        cur = b.Prev;
                        break;
                    case BackKind.Epsilon:
                        cur = b.Prev;
                        break;
                    case BackKind.Predict:
                        ev.Add(Event.EnterRule(b.RuleIndex));
                        cur = b.Prev;
                        break;
                    case BackKind.Complete:
                        ev.Add(Event.ExitRule(b.RuleIndex));
                        cur = b.Prev;
                        break;
                    case BackKind.Seed:
                        // reached the seed
                        cur = b.Prev; // null-ish; loop will terminate by start-state check
                        break;
                    default:
                        throw new InvalidOperationException("Unknown back kind");
                }
            }
            ev.Reverse();
            return ev;
        }

        private static ParserRuleContext BuildTree(List<Event> events, List<IToken> tokens, int startRuleIndex)
        {
            var root = new GenericRuleContext(null, 0, startRuleIndex);
            if (tokens.Count > 0) root.Start = tokens[0];
            var stack = new Stack<GenericRuleContext>();
            stack.Push(root);

            int cursor = 0; // position in tokens (consumed count)
            foreach (var e in events)
            {
                switch (e.Kind)
                {
                    case EventKind.EnterRule:
                    {
                        var ctx = new GenericRuleContext(stack.Peek(), 0, e.RuleIndex);
                        // Start token at current cursor (if within range)
                        if (cursor < tokens.Count) ctx.Start = tokens[cursor];
                        stack.Peek().AddChild(ctx);
                        stack.Push(ctx);
                        break;
                    }
                    case EventKind.ExitRule:
                    {
                        var done = stack.Pop();
                        if (cursor > 0 && cursor - 1 < tokens.Count) done.Stop = tokens[cursor - 1];
                        else if (cursor < tokens.Count) done.Stop = tokens[cursor];
                        break;
                    }
                    case EventKind.Consume:
                    {
                        int i = e.TokenIndex;
                        // cursor must equal i
                        if (i >= 0 && i < tokens.Count && cursor == i)
                        {
                            var term = new TerminalNodeImpl(tokens[i]);
                            stack.Peek().AddChild(term);
                            stack.Peek().Stop = tokens[i];
                            cursor++;
                        }
                        else
                        {
                            // out of sync; bail (return a partial tree)
                            cursor = Math.Min(cursor + 1, tokens.Count - 1);
                        }
                        break;
                    }
                }
            }

            // Close any unclosed rules
            while (stack.Count > 1)
            {
                var done = stack.Pop();
                if (cursor > 0 && cursor - 1 < tokens.Count) done.Stop = tokens[cursor - 1];
                else if (cursor < tokens.Count) done.Stop = tokens[cursor];
            }
            if (root.Stop == null && tokens.Count > 0)
                root.Stop = tokens[Math.Max(0, Math.Min(cursor, tokens.Count - 1))];

            return root;
        }

        // === Utilities ===

        private static bool IsTerminalTransition(Transition t) =>
            t is AtomTransition || t is SetTransition || t is NotSetTransition || t is WildcardTransition;

        private static bool TerminalMatches(Transition t, IToken tokenType)
        {
	    var tt = tokenType.Type;
            switch (t)
            {
                case AtomTransition atom:
                    return atom.Label.Contains(tt);
                case NotSetTransition notset:
                    return notset.Label != null && !notset.Label.Contains(tt)
                           && tt != TokenConstants.EOF;
		case SetTransition set:
			return set.Label != null && set.Label.Contains(tt);
                case WildcardTransition _:
                    return tt != TokenConstants.EOF;
                default:
                    return false;
            }
        }

        private static List<IToken> MaterializeTokens(ITokenStream stream)
        {
            var list = new List<IToken>();
            int marker = stream.Index;
            stream.Seek(0);
            for (;;)
            {
                var t = ((CommonTokenStream)stream).LT(1);
                list.Add(t);
                if (t.Type == TokenConstants.EOF) break;
		stream.Consume();
            }
            stream.Seek(marker);
            return list;
        }

	private static Transition[] TransitionsArray(this ATNState s)
	{
		var n = s.NumberOfTransitions;
		if (n == 0) return Array.Empty<Transition>();
		var arr = new Transition[n];
		for (int i = 0; i < n; i++) arr[i] = s.Transition(i);
		return arr;
	}

        // === Item / CallStack (value types) ===

        private readonly struct Item
        {
            public ATNState State { get; }
            public int Origin { get; }
            public CallStack CallStack { get; }

            public Item(ATNState state, int origin, CallStack callStack)
            {
                State = state ?? throw new ArgumentNullException(nameof(state));
                Origin = origin;
                CallStack = callStack;
            }

            public static IEqualityComparer<Item> Comparer { get; } = new ItemEq();

            private sealed class ItemEq : IEqualityComparer<Item>
            {
                public bool Equals(Item x, Item y) =>
                    ReferenceEquals(x.State, y.State) &&
                    x.Origin == y.Origin &&
                    CallStack.Equals(x.CallStack, y.CallStack);

                public int GetHashCode(Item obj)
                {
                    unchecked
                    {
                        int h = 17;
                        h = h * 31 + obj.State.stateNumber;
                        h = h * 31 + obj.Origin;
                        h = h * 31 + obj.CallStack.GetHashCode();
                        return h;
                    }
                }
            }
        }

        private readonly struct CallStack : IEquatable<CallStack>
        {
            private sealed class Node
            {
                public readonly ATNState Head;
                public readonly Node Tail;
                public readonly int Depth;
                public Node(ATNState head, Node tail)
                {
                    Head = head;
                    Tail = tail;
                    Depth = (tail?.Depth ?? 0) + 1;
                }
            }

            private readonly Node _node;
            public static CallStack Empty => default;
            public bool IsEmpty => _node == null;

            private CallStack(Node node) => _node = node;

            public CallStack Push(ATNState ret) => new CallStack(new Node(ret, _node));

            public (ATNState head, CallStack rest) Pop()
            {
                if (_node == null) throw new InvalidOperationException("Empty call stack.");
                return (_node.Head, new CallStack(_node.Tail));
            }

            public override int GetHashCode() => _node?.GetHashCode() ?? 0;

            public bool Equals(CallStack other) => ReferenceEquals(_node, other._node);

            public override bool Equals(object obj) => obj is CallStack cs && Equals(cs);
        }

        // === Backpointers and Events ===

        private enum BackKind { Seed, Epsilon, Predict, Complete, Scan }

        private sealed class Back
        {
            public BackKind Kind { get; }
            public Item Prev { get; }
            public int RuleIndex { get; }
            public int TokenIndex { get; }

            private Back(BackKind kind, Item prev, int ruleIndex, int tokenIndex)
            {
                Kind = kind; Prev = prev; RuleIndex = ruleIndex; TokenIndex = tokenIndex;
            }

            public static Back Seed() => new Back(BackKind.Seed, default, -1, -1);
            public static Back Epsilon(Item prev) => new Back(BackKind.Epsilon, prev, -1, -1);
            public static Back Predict(Item prev, int ruleIndex) => new Back(BackKind.Predict, prev, ruleIndex, -1);
            public static Back Complete(Item prev, int ruleIndex) => new Back(BackKind.Complete, prev, ruleIndex, -1);
            public static Back Scan(Item prev, int tokenIndex) => new Back(BackKind.Scan, prev, -1, tokenIndex);
        }

        private static void AddBack(Dictionary<Item, List<Back>> map, Item to, Back back)
        {
            if (!map.TryGetValue(to, out var list))
            {
                list = new List<Back>(1);
                map[to] = list;
            }
            // Keep only the first to favor a single-derivation tree.
            if (list.Count == 0) list.Add(back);
        }

        private enum EventKind { EnterRule, ExitRule, Consume }

        private sealed class Event
        {
            public EventKind Kind { get; }
            public int RuleIndex { get; }
            public int TokenIndex { get; }

            private Event(EventKind kind, int ruleIndex, int tokenIndex)
            { Kind = kind; RuleIndex = ruleIndex; TokenIndex = tokenIndex; }

            public static Event EnterRule(int ruleIndex) => new Event(EventKind.EnterRule, ruleIndex, -1);
            public static Event ExitRule(int ruleIndex) => new Event(EventKind.ExitRule, ruleIndex, -1);
            public static Event Consume(int tokenIndex) => new Event(EventKind.Consume, -1, tokenIndex);
        }

        // Generic rule context so ToStringTree(recog) can display rule names via RuleIndex
        private sealed class GenericRuleContext : ParserRuleContext
        {
            private readonly int _ruleIndex;
            public override int RuleIndex => _ruleIndex;
            public GenericRuleContext(ParserRuleContext parent, int invokingState, int ruleIndex)
                : base(parent, invokingState)
            {
                _ruleIndex = ruleIndex;
            }
        }
    }
}
