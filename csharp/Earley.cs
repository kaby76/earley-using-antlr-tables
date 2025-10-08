// See https://chatgpt.com/share/68d52d98-0a24-8007-9f34-1a5610a6c897

using System;
using System.Collections.Generic;
using System.Linq;

namespace Earley
{
    // ----- Grammar model -----

    public abstract record Symbol(string Name)
    {
        public override string ToString() => Name;
    }

    public sealed record NonTerminal(string Id) : Symbol(Id);
    public sealed record Terminal(string Literal) : Symbol(Literal);

    public sealed record Production(NonTerminal Lhs, IReadOnlyList<Symbol> Rhs)
    {
        public override string ToString()
            => $"{Lhs} -> {string.Join(" ", Rhs.Select(s => s.ToString()))}";
    }

    public sealed class Grammar
    {
        public NonTerminal Start { get; }
        private readonly List<Production> _productions = new();
        private readonly Dictionary<NonTerminal, List<Production>> _byLhs = new();

        public Grammar(NonTerminal start) => Start = start;

        public Grammar Add(NonTerminal lhs, params Symbol[] rhs)
        {
            var p = new Production(lhs, rhs);
            _productions.Add(p);
            if (!_byLhs.TryGetValue(lhs, out var list))
            {
                list = new List<Production>();
                _byLhs[lhs] = list;
            }
            list.Add(p);
            return this;
        }

        public IEnumerable<Production> RulesFor(NonTerminal lhs)
            => _byLhs.TryGetValue(lhs, out var list) ? list : Enumerable.Empty<Production>();

        public IEnumerable<Production> AllProductions => _productions;
    }

    // ----- Earley state & helpers -----

    /// <summary>
    /// Earley state: (production, dotIndex, origin)
    /// Visual: A -> α • β , origin = i
    /// </summary>
    public sealed record State(Production Prod, int Dot, int Origin)
    {
        public bool Finished => Dot >= Prod.Rhs.Count;

        public Symbol? NextSymbol => Finished ? null : Prod.Rhs[Dot];

        public State Advance() => this with { Dot = Dot + 1 };

        public override string ToString()
        {
            var rhsWithDot = Prod.Rhs
                .Select((s, i) => i == Dot ? $"• {s}" : s.ToString())
                .ToList();
            if (Dot == Prod.Rhs.Count) rhsWithDot.Add("•");
            return $"({Prod.Lhs} -> {string.Join(" ", rhsWithDot)}, {Origin})";
        }
    }

    // For ordered-set behavior with dedup.
    internal sealed class StateSet
    {
        private readonly List<State> _ordered = new();
        private readonly HashSet<State> _seen = new();

        public int Count => _ordered.Count;
        public State this[int i] => _ordered[i];
        public IEnumerable<State> Items => _ordered;

        public bool Add(State s)
        {
            if (_seen.Add(s))
            {
                _ordered.Add(s);
                return true;
            }
            return false;
        }
    }

    public sealed class EarleyRecognizer
    {
        private readonly Grammar _grammar;

        public EarleyRecognizer(Grammar grammar) => _grammar = grammar;

        /// <summary>
        /// Returns true if tokens are in the language defined by the grammar.
        /// Tokens are matched by exact string equality with Terminal.Literal.
        /// </summary>
        public bool Recognize(IReadOnlyList<string> tokens)
        {
            // S[0..n], each S[k] is a StateSet
            var S = new StateSet[tokens.Count + 1];
            for (int k = 0; k <= tokens.Count; k++) S[k] = new StateSet();

            // Augmented start: γ -> • Start
            var gamma = new NonTerminal("γ");
            var startProd = new Production(gamma, new Symbol[] { _grammar.Start });

            S[0].Add(new State(startProd, 0, 0));

            for (int k = 0; k <= tokens.Count; k++)
            {
                // We will iterate over S[k] as it expands (classic Earley loop).
                for (int i = 0; i < S[k].Count; i++)
                {
                    var state = S[k][i];

                    if (!state.Finished)
                    {
                        var next = state.NextSymbol!;
                        if (next is NonTerminal nt)
                        {
                            Predictor(state, k, nt, S); // add B -> • γ at S[k]
                        }
                        else if (next is Terminal term)
                        {
                            Scanner(state, k, term, tokens, S); // if token matches, add advanced state to S[k+1]
                        }
                    }
                    else
                    {
                        Completer(state, k, S); // for (A -> α•, j) complete waiting states from S[j]
                    }
                }
            }

            // Accept if (γ -> Start •, 0) ∈ S[n]
            var accept = new State(startProd, 1, 0);
            return S[tokens.Count].Items.Contains(accept);
        }

        private void Predictor(State state, int k, NonTerminal nextNt, StateSet[] S)
        {
            foreach (var rule in _grammar.RulesFor(nextNt))
            {
                var predicted = new State(rule, 0, k);
                S[k].Add(predicted);
            }
        }

        private void Scanner(State state, int k, Terminal term, IReadOnlyList<string> input, StateSet[] S)
        {
            if (k < input.Count && input[k] == term.Literal)
            {
                S[k + 1].Add(state.Advance());
            }
        }

        private void Completer(State state, int k, StateSet[] S)
        {
            // state is (B -> γ•, x)
            int origin = state.Origin;
            foreach (var st in S[origin].Items)
            {
                var next = st.NextSymbol;
                if (next is NonTerminal nt && ReferenceEquals(nt, state.Prod.Lhs))
                {
                    S[k].Add(st.Advance());
                }
            }
        }
    }

    // ----- Minimal demo -----
    internal static class Demo
    {
        /*
         Example grammar:
           S -> S + M | M
           M -> M * T | T
           T -> "1" | "2" | "3" | "4"
        */
        public static void Main()
        {
            var S = new NonTerminal("S");
            var M = new NonTerminal("M");
            var T = new NonTerminal("T");

            Terminal t(string lit) => new Terminal(lit);

            var g = new Grammar(S)
                .Add(S, S, t("+"), M)
                .Add(S, M)
                .Add(M, M, t("*"), T)
                .Add(M, T)
                .Add(T, t("1"))
                .Add(T, t("2"))
                .Add(T, t("3"))
                .Add(T, t("4"));

            var recognizer = new EarleyRecognizer(g);

            var tokens1 = new[] { "2", "+", "3", "*", "4" };
            var tokens2 = new[] { "2", "+", "*", "4" };

            Console.WriteLine(string.Join(" ", tokens1) + " => " + recognizer.Recognize(tokens1)); // True
            Console.WriteLine(string.Join(" ", tokens2) + " => " + recognizer.Recognize(tokens2)); // False
        }
    }
}
