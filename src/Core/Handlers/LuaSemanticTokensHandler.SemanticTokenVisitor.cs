﻿using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Loretta.CodeAnalysis;
using Loretta.CodeAnalysis.Lua;
using Loretta.CodeAnalysis.Lua.Syntax;
using Loretta.CodeAnalysis.PooledObjects;
using Loretta.CodeAnalysis.Text;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Loretta.LanguageServer.Handlers
{
    internal partial class LuaSemanticTokensHandler
    {
        private class SemanticTokenVisitor : LuaSyntaxWalker
        {
            private readonly Script _script;
            private readonly SourceText _text;
            private readonly SemanticTokensBuilder _builder;
            private readonly CancellationToken _cancellationToken;
            private readonly Stack<SemanticTokenModifier> _modifiers;

            private SemanticTokenVisitor(
                Script script,
                SourceText text,
                SemanticTokensBuilder builder,
                CancellationToken cancellationToken)
                : base(SyntaxWalkerDepth.Trivia)
            {
                _script = script;
                _text = text;
                _builder = builder;
                _cancellationToken = cancellationToken;
                _modifiers = new Stack<SemanticTokenModifier>();
            }

            public static void Tokenize(
                Script script,
                SourceText text,
                SemanticTokensBuilder builder,
                SyntaxNode root,
                CancellationToken cancellationToken)
            {
                var visitor = new SemanticTokenVisitor(script, text, builder, cancellationToken);
                visitor.Visit(root);
            }

            private void Push(
                SyntaxToken token,
                SemanticTokenType tokenType,
                IEnumerable<SemanticTokenModifier>? tokenModifiers = null) =>
                Push(token.Span, tokenType, tokenModifiers);

            private void Push(
                SyntaxNode node,
                SemanticTokenType tokenType,
                IEnumerable<SemanticTokenModifier>? tokenModifiers = null) =>
                Push(node.Span, tokenType, tokenModifiers);

            private void Push(
                TextSpan textSpan,
                SemanticTokenType tokenType,
                IEnumerable<SemanticTokenModifier>? tokenModifiers = null)
            {
                var modifiers = _modifiers.AsEnumerable();
                if (tokenModifiers is not null)
                    modifiers = modifiers.Concat(tokenModifiers);

                var lineSpan = _text.Lines.GetLinePositionSpan(textSpan);
                if (lineSpan.Start.Line != lineSpan.End.Line)
                {
                    // We need to push multiple tokens as the underlying LSP library doesn't support multiline tokens.
                    for (var lineNum = lineSpan.Start.Line; lineNum <= lineSpan.End.Line; lineNum++)
                    {
                        int charPos, length;
                        if (lineNum == lineSpan.Start.Line)
                        {
                            charPos = lineSpan.Start.Character;
                            length = _text.Lines[lineNum].Span.Length - charPos;
                        }
                        else if (lineNum == lineSpan.End.Line)
                        {
                            charPos = 0;
                            length = lineSpan.End.Character;
                        }
                        else
                        {
                            charPos = 0;
                            length = _text.Lines[lineNum].Span.Length;
                        }

                        _builder.Push(lineNum, charPos, length, tokenType, modifiers);
                    }
                }
                else
                {
                    _builder.Push(lineSpan.Start.Line, lineSpan.Start.Character, textSpan.Length, tokenType, modifiers);
                }
            }

            public override void Visit(SyntaxNode? node)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                base.Visit(node);
            }

            public override void VisitIdentifierName(IdentifierNameSyntax node)
            {
                if (_script.GetVariable(node) is IVariable variable)
                {
                    var modifiers = ImmutableArray.CreateBuilder<SemanticTokenModifier>();

                    // Standard library hack
                    // This is REALLY BAD and I am NOT pround of it.
                    var isntDefinedAnywhere = variable.Declaration == null && !variable.WriteLocations.Any();
                    if (isntDefinedAnywhere && variable.Kind == VariableKind.Global)
                    {
                        modifiers.Add(SemanticTokenModifier.Readonly);
                        switch (node.Name)
                        {
                            // Functions
                            case "getmetatable":
                            case "ipairs":
                            case "next":
                            case "pairs":
                            case "select":
                            case "setmetatable":
                            case "tonumber":
                            case "tostring":
                            case "type":
                            case "print":
                            case "assert":
                            case "pcall":
                            case "xpcall":
                            case "error":
                            case "collectgarbage":
                                modifiers.Add(SemanticTokenModifier.Static);
                                modifiers.Add(SemanticTokenModifier.DefaultLibrary);
                                Push(node, SemanticTokenType.Function, modifiers.ToImmutable());
                                return;

                            // Libraries
                            case "string":
                            case "math":
                            case "table":
                            case "coroutine":
                            case "io":
                            case "debug":
                                modifiers.Add(SemanticTokenModifier.Static);
                                modifiers.Add(SemanticTokenModifier.DefaultLibrary);
                                Push(node, SemanticTokenType.Type, modifiers.ToImmutable());
                                return;
                        }
                    }

                    if (!isntDefinedAnywhere && variable.WriteLocations.Count() <= 1)
                        modifiers.Add(SemanticTokenModifier.Readonly);
                    if (variable.Kind == VariableKind.Global)
                        modifiers.Add(SemanticTokenModifier.Static);

                    Push(node.Identifier, SemanticTokenType.Variable, modifiers.ToImmutable());
                }
            }

            public override void VisitMethodCallExpression(MethodCallExpressionSyntax node)
            {
                Visit(node.Expression);
                VisitToken(node.ColonToken);
                Push(node.Identifier, SemanticTokenType.Method);
                Visit(node.Argument);
            }

            public override void VisitFunctionCallExpression(FunctionCallExpressionSyntax node)
            {
                switch (node.Expression.Kind())
                {
                    case SyntaxKind.IdentifierName:
                        Push(node.Expression, SemanticTokenType.Function);
                        break;

                    case SyntaxKind.MemberAccessExpression:
                    {
                        var memberAccess = (MemberAccessExpressionSyntax) node.Expression;
                        Visit(memberAccess.Expression);
                        VisitToken(memberAccess.DotSeparator);
                        Push(memberAccess.MemberName, SemanticTokenType.Function);
                    }
                    break;
                }
                Visit(node.Argument);
            }

            public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                Visit(node.Expression);
                VisitToken(node.DotSeparator);
                Push(node.MemberName, SemanticTokenType.Property);
            }

            public override void VisitToken(SyntaxToken token)
            {
                VisitLeadingTrivia(token);
                switch (token.Kind())
                {
                    #region Operators

                    // Unary ops
                    case SyntaxKind.HashToken:
                    case SyntaxKind.BangToken:
                    // Binary ops
                    case SyntaxKind.HatToken:
                    case SyntaxKind.StarToken:
                    case SyntaxKind.SlashToken:
                    case SyntaxKind.PercentToken:
                    case SyntaxKind.PlusToken:
                    case SyntaxKind.DotDotToken:
                    case SyntaxKind.LessThanLessThanToken:
                    case SyntaxKind.GreaterThanGreaterThanToken:
                    case SyntaxKind.AmpersandToken:
                    case SyntaxKind.PipeToken:
                    case SyntaxKind.TildeEqualsToken:
                    case SyntaxKind.LessThanToken:
                    case SyntaxKind.LessThanEqualsToken:
                    case SyntaxKind.GreaterThanToken:
                    case SyntaxKind.GreaterThanEqualsToken:
                    case SyntaxKind.EqualsEqualsToken:
                    case SyntaxKind.BangEqualsToken:
                    case SyntaxKind.AmpersandAmpersandToken:
                    case SyntaxKind.PipePipeToken:
                    // Both unary and binary ops
                    case SyntaxKind.TildeToken:
                    case SyntaxKind.MinusToken:
                        if (token.Parent is not (UnaryExpressionSyntax or BinaryExpressionSyntax))
                            break;
                        Push(token, SemanticTokenType.Operator);
                        break;

                    #endregion Operators

                    #region Keywords

                    case SyntaxKind.AndKeyword:
                    case SyntaxKind.BreakKeyword:
                    case SyntaxKind.ContinueKeyword:
                    case SyntaxKind.DoKeyword:
                    case SyntaxKind.ElseIfKeyword:
                    case SyntaxKind.ElseKeyword:
                    case SyntaxKind.EndKeyword:
                    case SyntaxKind.FalseKeyword:
                    case SyntaxKind.ForKeyword:
                    case SyntaxKind.FunctionKeyword:
                    case SyntaxKind.GotoKeyword:
                    case SyntaxKind.IfKeyword:
                    case SyntaxKind.InKeyword:
                    case SyntaxKind.LocalKeyword:
                    case SyntaxKind.NilKeyword:
                    case SyntaxKind.NotKeyword:
                    case SyntaxKind.OrKeyword:
                    case SyntaxKind.RepeatKeyword:
                    case SyntaxKind.ReturnKeyword:
                    case SyntaxKind.ThenKeyword:
                    case SyntaxKind.TrueKeyword:
                    case SyntaxKind.UntilKeyword:
                    case SyntaxKind.WhileKeyword:
                        Push(token, SemanticTokenType.Keyword);
                        break;

                    #endregion Keywords

                    case SyntaxKind.NumericLiteralToken:
                        Push(token, SemanticTokenType.Number);
                        break;

                    case SyntaxKind.StringLiteralToken:
                        Push(token, SemanticTokenType.String);
                        break;

                    // These need to be in the end to not ruin the jump table optimization.

                    // Keywords go first since some keywords can be operators.
                    case SyntaxKind kwKind when SyntaxFacts.IsKeyword(kwKind):
                        goto case SyntaxKind.DoKeyword;

                    case SyntaxKind opKind when SyntaxFacts.IsOperatorToken(opKind):
                        goto case SyntaxKind.PlusToken;
                }
                VisitTrailingTrivia(token);
            }

            public override void VisitTrivia(SyntaxTrivia trivia)
            {
                switch (trivia.Kind())
                {
                    case SyntaxKind.ShebangTrivia:
                    case SyntaxKind.SingleLineCommentTrivia:
                    case SyntaxKind.MultiLineCommentTrivia:
                        Push(trivia.Span, SemanticTokenType.Comment);
                        break;
                }
            }
        }
    }
}
