# earley-using-antlr-tables

This repo contains two implementations of [Earley parsing](https://en.wikipedia.org/wiki/Earley_parser).

The first example, [csharp](https://github.com/kaby76/earley-using-antlr-tables/tree/main/csharp),
is a straight-forward implementation of the algorithm pseudo-code at https://en.wikipedia.org/wiki/Earley_parser#Pseudocode.
This implementation contains the methods Predictor(), Scanner(), and Completor(), and are
called in code that mirrors exactly the pseudo-code.

The second example, [antlr](https://github.com/kaby76/earley-using-antlr-tables/tree/main/antlr), is a proof-of-concept
Implementation of Earley using the Antlr4 parser [ATNs](https://en.wikipedia.org/wiki/Augmented_transition_network).
There is an equivalence between a grammar and a collection of the Antlr4 ATNs. In other words, it makes little sense to
start with an Antlr4 grammar and not utilize the ATN representation. The main issue is that some Antlr4 grammars
are not `ALL(*)`, but are context-free.
