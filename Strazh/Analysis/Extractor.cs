using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Generic;
using Strazh.Domain;
using System.IO;

namespace Strazh.Analysis
{
    public static class Extractor
    {
        private static TypeNode? CreateTypeNode(this ISymbol symbol, TypeDeclarationSyntax declaration, string? commitSha)
        {
            (string fullName, string name) = (symbol.ContainingNamespace.ToString() + '.' + symbol.Name, symbol.Name);
            switch (declaration)
            {
                case ClassDeclarationSyntax _:
                    return new ClassNode(fullName, name, declaration.Modifiers.MapModifiers(), commitSha);
                case InterfaceDeclarationSyntax _:
                    return new InterfaceNode(fullName, name, declaration.Modifiers.MapModifiers(), commitSha);
            }
            return null;
        }

        private static ClassNode CreateClassNode(this TypeInfo typeInfo, string? commitSha)
            => new ClassNode(GetFullName(typeInfo), GetName(typeInfo), commitSha: commitSha);

        private static InterfaceNode CreateInterfaceNode(this TypeInfo typeInfo, string? commitSha)
            => new InterfaceNode(GetFullName(typeInfo), GetName(typeInfo), commitSha: commitSha);

        private static string[] MapModifiers(this SyntaxTokenList syntaxTokens)
            => syntaxTokens.Select(x => x.ValueText).ToArray();

        private static TypeNode? CreateTypeNode(this TypeInfo typeInfo, string? commitSha)
        {
            switch (typeInfo.ConvertedType?.TypeKind)
            {
                case TypeKind.Interface:
                    return CreateInterfaceNode(typeInfo, commitSha);

                case TypeKind.Class:
                    return CreateClassNode(typeInfo, commitSha);

                default:
                    return null;
            }
        }

        // The commit sha of the source that defines a symbol: its first in-source location's file,
        // resolved to that file's git-root HEAD. Null for symbols with no source (metadata / NuGet
        // references) — those stay version-agnostic. Because it resolves by the *defining* file, a
        // reference from any repo to a shared dependency lands on the same versioned node.
        private static string? CommitShaOf(this ISymbol? symbol)
        {
            var path = symbol?.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath;
            return path == null ? null : GitHelper.GetCommit(path)?.Sha;
        }

        private static string GetName(this TypeInfo typeInfo)
            => typeInfo.Type?.Name ?? "";

        private static string GetFullName(this TypeInfo typeInfo)
            => (typeInfo.Type?.ContainingNamespace?.ToString() ?? "") + "." + GetName(typeInfo);

        private static string GetNamespaceName(this INamespaceSymbol? namespaceSymbol, string name)
        {
            var nextName = namespaceSymbol?.Name;
            if (string.IsNullOrEmpty(nextName))
            {
                return name;
            }
            // A non-empty nextName means namespaceSymbol was not null.
            return GetNamespaceName(namespaceSymbol!.ContainingNamespace, $"{nextName}.{name}");
        }

        private static MethodNode CreateMethodNode(this IMethodSymbol symbol, string? commitSha, MethodDeclarationSyntax? declaration = null)
        {
            var fullName = symbol.ContainingNamespace.GetNamespaceName($"{symbol.ContainingType.Name}.{symbol.Name}");
            var args = symbol.Parameters.Select(x => (name: x.Name, type: x.Type.ToString() ?? "")).ToArray();
            var returnType = symbol.ReturnType.ToString() ?? "";
            return new MethodNode(fullName,
                symbol.Name,
                args,
                returnType,
                declaration?.Modifiers.MapModifiers(),
                commitSha);
        }

        private static string GetName(string filePath)
            => filePath.Split(Path.DirectorySeparatorChar)[^1];

        private static List<TripleIncludedIn> GetFolderChain(string filePath, FileNode file)
        {
            var triples = new List<TripleIncludedIn>();
            var chain = filePath.Split(Path.DirectorySeparatorChar);
            FolderNode? prev = null;
            var path = string.Empty;
            foreach (var item in chain)
            {
                if (string.IsNullOrEmpty(path))
                {
                    path = item;
                    prev = new FolderNode(path, item);
                    continue;
                }
                if (item == file.Name)
                {
                    // prev is set on the first non-empty segment, which always precedes the file name.
                    triples.Add(new TripleIncludedIn(file, prev!));
                    return triples;
                }
                else
                {
                    path = Path.DirectorySeparatorChar == '/' ? $"{path}/{item}" : $"{path}\\{item}";
                    triples.Add(new TripleIncludedIn(new FolderNode(path, item), new FolderNode(prev!.FullName, prev.Name)));
                    prev = new FolderNode(path, item);
                }
            }
            return triples;
        }

        /// <summary>
        /// Entry to analyze class or interface
        /// </summary>
        public static void AnalyzeTree<T>(IList<Triple> triples, SyntaxTree st, SemanticModel sem, FolderNode rootFolder)
            where T : TypeDeclarationSyntax
        {
            var root = st.GetRoot();
            var absolutePath = root.SyntaxTree.FilePath;
            var filePath = absolutePath;
            var index = filePath.IndexOf(rootFolder.Name);
            filePath = index < 0 ? filePath : filePath[index..];
            var fileName = GetName(filePath);

            // Every declaration in this file shares the file's commit, so resolve it once. Referenced
            // symbols (base types, invoked methods, constructed classes) may live in other files/repos,
            // so those are resolved per symbol below.
            var fileCommit = GitHelper.GetCommitNode(absolutePath);
            var fileSha = fileCommit?.Sha;

            var fileNode = new FileNode(filePath, fileName, fileSha);
            GetFolderChain(filePath, fileNode).ForEach(triples.Add);
            if (fileCommit != null)
            {
                triples.Add(new TripleFromCommit(fileNode, fileCommit));
            }
            var declarations = root.DescendantNodes().OfType<T>();
            foreach (var declaration in declarations)
            {
                var node = sem.GetDeclaredSymbol(declaration)?.CreateTypeNode(declaration, fileSha);
                if (node != null)
                {
                    triples.Add(new TripleDeclaredAt(node, fileNode));
                    if (fileCommit != null)
                    {
                        triples.Add(new TripleFromCommit(node, fileCommit));
                    }
                    GetInherits(triples, declaration, sem, node);
                    GetMethodsAll(triples, declaration, sem, node, fileSha, fileCommit);
                }
            }
        }

        /// <summary>
        /// Member (field, property) initialization
        /// </summary>
        //public static void GetConstructsWithinClass(IList<Triple> triples, ClassDeclarationSyntax declaration, SemanticModel sem, ClassNode classNode)
        //{
        //    var creates = declaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
        //    foreach (var creation in creates)
        //    {
        //        var node = sem.GetTypeInfo(creation).CreateClassNode();
        //        triples.Add(new TripleConstruct(classNode, node));
        //    }
        //}

        /// <summary>
        /// Type inherited from BaseType
        /// </summary>
        public static void GetInherits(IList<Triple> triples, TypeDeclarationSyntax declaration, SemanticModel sem, TypeNode node)
        {
            if (declaration.BaseList != null)
            {
                foreach (var baseTypeSyntax in declaration.BaseList.Types)
                {
                    var baseTypeInfo = sem.GetTypeInfo(baseTypeSyntax.Type);
                    var parentNode = baseTypeInfo.CreateTypeNode(baseTypeInfo.Type.CommitShaOf());
                    if (node is ClassNode classNode && parentNode != null)
                    {
                        triples.Add(new TripleOfType(classNode, parentNode));
                    }
                    if (node is InterfaceNode interfaceNode && parentNode is InterfaceNode parentInterfaceNode)
                    {
                        triples.Add(new TripleOfType(interfaceNode, parentInterfaceNode));
                    }
                }
            }
        }

        /// <summary>
        /// Class or Interface have some method AND some method can call another method AND some method can creates an object of class
        /// </summary>
        public static void GetMethodsAll(IList<Triple> triples, TypeDeclarationSyntax declaration, SemanticModel sem, TypeNode node, string? ownerSha, CommitNode? ownerCommit)
        {
            var methods = declaration.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var method in methods)
            {
                var methodSymbol = sem.GetDeclaredSymbol(method);
                if (methodSymbol is null)
                {
                    continue;
                }
                // The declared method lives in this file, so it shares the owning type's commit.
                var methodNode = methodSymbol.CreateMethodNode(ownerSha, method);
                triples.Add(new TripleHave(node, methodNode));
                if (ownerCommit != null)
                {
                    triples.Add(new TripleFromCommit(methodNode, ownerCommit));
                }

                foreach (var syntax in method.DescendantNodes().OfType<ExpressionSyntax>())
                {
                    switch (syntax)
                    {
                        case ObjectCreationExpressionSyntax creation:
                            // Constructed and invoked targets are defined elsewhere — resolve each
                            // to its own defining commit so the edge lands on that versioned node.
                            var createdTypeInfo = sem.GetTypeInfo(creation);
                            var classNode = createdTypeInfo.CreateClassNode(createdTypeInfo.Type.CommitShaOf());
                            triples.Add(new TripleConstruct(methodNode, classNode));
                            break;

                        case InvocationExpressionSyntax invocation:
                            if (sem.GetSymbolInfo(invocation).Symbol is IMethodSymbol invokedSymbol)
                            {
                                var invokedMethod = invokedSymbol.CreateMethodNode(invokedSymbol.CommitShaOf());
                                triples.Add(new TripleInvoke(methodNode, invokedMethod));
                            }
                            break;
                    }
                }
            }
        }
    }
}