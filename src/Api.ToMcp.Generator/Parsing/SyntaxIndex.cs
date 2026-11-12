using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// The invocations of each syntax tree by the name of the method they call, and a semantic model per tree. An IDE
    /// gives the generator a new compilation after every edit, which keeps the trees of the files the edit did not touch,
    /// so the index of such a tree is built once, however many compilations follow.
    /// </summary>
    internal static class SyntaxIndex
    {
        private static readonly ConditionalWeakTable<SyntaxTree, ILookup<string, InvocationExpressionSyntax>> InvocationsByTree =
            new ConditionalWeakTable<SyntaxTree, ILookup<string, InvocationExpressionSyntax>>();

        private static readonly ConditionalWeakTable<SyntaxTree, ILookup<string, InvocationExpressionSyntax>>.CreateValueCallback IndexTree = IndexInvocations;

        /// <summary>
        /// One semantic model per tree of a compilation, which keeps what it binds for every endpoint and call in the file.
        /// </summary>
        private static readonly CompilationCache<SyntaxTree, SemanticModel> SemanticModels =
            new CompilationCache<SyntaxTree, SemanticModel>(EqualityComparer<SyntaxTree>.Default);

        public static ILookup<string, InvocationExpressionSyntax> GetInvocations(SyntaxTree tree)
        {
            return InvocationsByTree.GetValue(tree, IndexTree);
        }

        public static SemanticModel GetSemanticModel(Compilation compilation, SyntaxTree tree)
        {
            return SemanticModels.GetOrAdd(compilation, tree, x => compilation.GetSemanticModel(x));
        }

        /// <summary>
        /// The name a call invokes a method by, whatever form the call takes: <c>M()</c>, <c>x.M()</c>, <c>x?.M()</c> or
        /// <c>M&lt;T&gt;()</c>; null for a call of a delegate an expression gives.
        /// </summary>
        public static string? GetInvokedName(ExpressionSyntax expression)
        {
            var simpleName = expression as SimpleNameSyntax;
            if (simpleName is not null)
            {
                return simpleName.Identifier.ValueText;
            }

            var memberAccess = expression as MemberAccessExpressionSyntax;
            if (memberAccess is not null)
            {
                return memberAccess.Name.Identifier.ValueText;
            }

            var memberBinding = expression as MemberBindingExpressionSyntax;
            return memberBinding?.Name.Identifier.ValueText;
        }

        private static ILookup<string, InvocationExpressionSyntax> IndexInvocations(SyntaxTree tree)
        {
            return tree.GetRoot()
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(x => (Name: GetInvokedName(x.Expression), Invocation: x))
                .Where(x => x.Name is not null)
                .ToLookup(x => x.Name!, x => x.Invocation, StringComparer.Ordinal);
        }
    }
}
