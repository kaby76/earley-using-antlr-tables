// MIT License
// Earley parser over Antlr4 C# runtime ATN
// Works as a recognizer: returns true if the token stream is in the language of startRuleIndex.
//
// Requirements:
// - Antlr4.Runtime (C#) – https://github.com/antlr/antlr4/tree/dev/runtime/CSharp/src
// - Parser ATN (from a generated parser): parser.Atn (ATNType.PARSER)
//
// Limitations:
// - Ignores semantic predicates and actions (treats them as epsilon).
// - No parse forest / tree construction (recognition only).
// - Assumes TOKEN transitions compare against token types (not channel/mode).
//
// Usage example:
//   var parser = new MyGrammarParser(new CommonTokenStream(new MyGrammarLexer(input)));
//   var atn = parser.Atn;
//   var ok = EarleyAtnRecognizer.Parse(atn, parser.TokenStream, startRuleIndex: MyGrammarParser.RULE_compilationUnit);
//   Console.WriteLine(ok ? "ACCEPT" : "REJECT");

using System;
using System.Collections.Generic;
using System.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;

namespace EarleyATN
{
    public static class EarleyAtnRecognizer
    {
        // Public entry point
        public static bool Parse(ATN atn, ITokenStream tokenStream, int startRuleIndex)
        {
            if (atn == null) throw new ArgumentNullException(nameof(atn));
//            if (atn.GrammarType != ATNType.Parser)
//                throw new ArgumentException("ATN must be a parser ATN.", nameof(atn));
            if (startRuleIndex < 0 || startRuleIndex >= atn.ruleToStartState.Length)
                throw new ArgumentOutOfRangeException(nameof(startRuleIndex));

            var tokens = MaterializeTokenTypes(tokenStream);
            var n = tokens.Count;

            // Chart: S[0..n], each is a set of Items
            var chart = new List<HashSet<Item>>(n + 1);
            for (int k = 0; k <= n; k++) chart.Add(new HashSet<Item>(Item.Comparer));

            // Seed with start-rule entry
            var start = atn.ruleToStartState[startRuleIndex];
            var startItem = new Item(start, origin: 0, callStack: CallStack.Empty);
            System.Console.WriteLine("seed " + startItem);
            chart[0].Add(startItem);
            Closure(atn, chart[0]);

            // Standard Earley loop
            for (int k = 0; k < n; k++)
            {
                System.Console.WriteLine("input " + k + " " + tokens[k]);

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
                                System.Console.WriteLine("scan " + advanced + " from " + it);
                            }
                        }
                    }
                }

                // Closure at k+1
                Closure(atn, next);
            }

            // Accept: any item at S[n] that represents the start rule fully recognized?
            // Start is recognized when we can complete back to an empty call stack at EOF position.
            // That manifests as reaching a RuleStopState for the start rule with empty stack.
            foreach (var it in chart[n])
            {
                if (it.CallStack.IsEmpty &&
                    it.State is RuleStopState rss &&
                    rss.ruleIndex == startRuleIndex)
                {
                    return true;
                }
            }

            return false;
        }

        // === Core Earley machinery over ATN ===

        private static void Closure(ATN atn, HashSet<Item> set)
        {
            var work = new Stack<Item>(set);
            var visited = new HashSet<Item>(Item.Comparer);
            foreach (var i in set) visited.Add(i);

            while (work.Count > 0)
            {
                var it = work.Pop();

                // Completion: if we're at a RuleStopState, pop the call stack (if any)
                if (it.State is RuleStopState)
                {
                    if (!it.CallStack.IsEmpty)
                    {
                        var (follow, rest) = it.CallStack.Pop();
                        var cont = new Item(follow, it.Origin, rest);
                        if (visited.Add(cont))
                        {
                            System.Console.WriteLine("comp " + cont + " from " + it);
                            set.Add(cont);
                            work.Push(cont);
                        }
                    }
                    // If empty stack, we leave it; accept is checked at the end.
                    continue;
                }

                // Predictor & epsilon-like expansion
                foreach (var tr in it.State.TransitionsArray())
                {
                    switch (tr)
                    {
                        case RuleTransition rt:
                        {
                            // Predict: push return address and enter callee start
                            var pushed = it.CallStack.Push(rt.followState);
                            var enter = new Item(rt.target, it.Origin, pushed);
                            if (visited.Add(enter))
                            {
                                System.Console.WriteLine("pred " + enter + " from " + it);
                                set.Add(enter);
                                work.Push(enter);
                            }
                            break;
                        }
                        case EpsilonTransition _:
                        case ActionTransition _:
                        case PredicateTransition _:
                        case PrecedencePredicateTransition _:
                        {
                            // Treat as epsilon: just move to target
                            var adv = new Item(tr.target, it.Origin, it.CallStack);
                            if (visited.Add(adv))
                            {
                                System.Console.WriteLine("eps " + adv + " from " + it);
                                set.Add(adv);
                                work.Push(adv);
                            }
                            break;
                        }
                        default:
                        {
                            // Terminals (Atom/Set/NotSet/Wildcard): handled during Scan
                            break;
                        }
                    }
                }
            }
        }

        // === Utilities ===

        private static bool IsTerminalTransition(Transition t) =>
            t is AtomTransition || t is SetTransition || t is NotSetTransition || t is WildcardTransition;

        private static bool TerminalMatches(Transition t, int tokenType)
        {
            switch (t)
            {
                case AtomTransition atom:
                    return atom.Label.Contains(tokenType);
                case NotSetTransition notset:
                // NotSetTransition inherits SetTransition, label is the “not” set
                    return notset.Label != null && !notset.Label.Contains(tokenType)
                                      && tokenType != TokenConstants.EOF; // ANTLR typically excludes EOF
                case SetTransition set:
                    return set.Label != null && set.Label.Contains(tokenType);
                case WildcardTransition _:
                    return tokenType != TokenConstants.EOF;
                default:
                    return false;
            }
        }

        private static List<int> MaterializeTokenTypes(ITokenStream stream)
        {
            // Snapshot token types into a list [t0 t1 ... t_(n-1) EOF]
            var list = new List<int>();
            var marker = stream.Index;
            stream.Seek(0);
            for (;;)
            {
                var t = ((CommonTokenStream)stream).LT(1);
                list.Add(t.Type);
                if (t.Type == TokenConstants.EOF) break;
                stream.Consume();
            }
            stream.Seek(marker);
            return list;
        }

        // Fast access without allocations in tight loops
        private static Transition[] TransitionsArray(this ATNState s)
        {
            var n = s.NumberOfTransitions;
            if (n == 0) return Array.Empty<Transition>();
            var arr = new Transition[n];
            for (int i = 0; i < n; i++) arr[i] = s.Transition(i);
            return arr;
        }

        // === Item and CallStack value types ===

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

            public override string ToString() =>
                $"[{State.stateNumber}:{State.GetType().Name} @ {Origin} | stack={CallStack.Depth}]";

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
            // A persistent stack implemented as an interned cons-list node
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

            public static CallStack Empty => default; // null node
            public bool IsEmpty => _node == null;
            public int Depth => _node?.Depth ?? 0;

            private CallStack(Node node) => _node = node;

            public CallStack Push(ATNState ret) => new CallStack(new Node(ret, _node));

            public (ATNState head, CallStack rest) Pop()
            {
                if (_node == null) throw new InvalidOperationException("Empty call stack.");
                return (_node.Head, new CallStack(_node.Tail));
            }

            public override int GetHashCode() => _node?.GetHashCode() ?? 0;

            public bool Equals(CallStack other)
            {
                // Reference equality of nodes is enough because we only construct via Push (persistent)
                return ReferenceEquals(_node, other._node);
            }

            public override bool Equals(object obj) => obj is CallStack cs && Equals(cs);
        }
    }
}
