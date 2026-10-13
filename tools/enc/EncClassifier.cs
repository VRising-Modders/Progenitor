// Rude-edit classifier for the Edit-and-Continue watcher (roadmap M4 item 5, job FENC2).
//
// Roslyn's EmitDifference is the emitter, not the analyzer: handing it a signature change walks
// off the end of the baseline metadata and throws BadImageFormatException instead of reporting a
// diagnostic (measured; internal research notes). Real rude-edit
// analysis lives in Microsoft.CodeAnalysis.CSharp.Features, a large package we do not take; the
// research note's §8 conclusion is that the conservative comparison below — "only a method body
// changed, and the member's signature, containing type and modifiers are identical" — is enough
// for the inner loop.
//
// Deliberately syntax-only: no semantic model, no references, no compilation. That keeps it
// game-free, makes it fast enough to run on every save, and lets tests/Progenitor.Tests link
// this file and drive the table without a V Rising install.
//
// The classifier is CONSERVATIVE in one direction only: anything it cannot prove is a body edit
// or a plain insert is called rude, and rude means "rebuild and redeploy for a generation swap".
// A false rude costs a swap the developer was going to do anyway; a false delta costs a
// BadImageFormatException or a corrupt method table.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Progenitor.Enc;

public enum EncEditKind
{
    /// <summary>Nothing the classifier can see changed.</summary>
    None,
    /// <summary>Method bodies only. The delta case.</summary>
    BodyOnly,
    /// <summary>Bodies plus new members or types, all of which EnC supports.</summary>
    BodyAndInsert,
    /// <summary>Needs a generation swap.</summary>
    Rude,
}

/// <summary>One classified compare of two source snapshots of the same project.</summary>
public sealed class EncEdit
{
    public EncEditKind Kind = EncEditKind.None;

    /// <summary>Empty unless <see cref="Kind"/> is <see cref="EncEditKind.Rude"/>.</summary>
    public string RudeReason = "";

    /// <summary>Signature keys of members whose body changed, in source order.</summary>
    public readonly List<string> ChangedBodies = new();

    /// <summary>Signature keys of members that are new in the second snapshot.</summary>
    public readonly List<string> Inserted = new();

    public bool IsDelta => Kind == EncEditKind.BodyOnly || Kind == EncEditKind.BodyAndInsert;

    /// <summary>The one line the watcher prints for every save.</summary>
    public string Describe() => Kind switch
    {
        EncEditKind.None => "no change",
        EncEditKind.BodyOnly => "method body only: " + string.Join(", ", ChangedBodies),
        EncEditKind.BodyAndInsert =>
            "method body plus insert: " + string.Join(", ", ChangedBodies.Concat(Inserted)),
        _ => "rude: needs a generation swap (" + RudeReason + ")",
    };
}

public static class EncClassifier
{
    /// <summary>Compare two whole-project snapshots, keyed by file path.</summary>
    public static EncEdit Classify(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var oldMembers = new Dictionary<string, string>(StringComparer.Ordinal);
        var newMembers = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var kv in before) Collect(kv.Value, oldMembers, null);
        foreach (var kv in after) Collect(kv.Value, newMembers, order);
        return Compare(oldMembers, newMembers, order);
    }

    /// <summary>Compare two single source texts. The shape the tests and the matrix use.</summary>
    public static EncEdit Classify(string before, string after)
    {
        var oldMembers = new Dictionary<string, string>(StringComparer.Ordinal);
        var newMembers = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        Collect(before, oldMembers, null);
        Collect(after, newMembers, order);
        return Compare(oldMembers, newMembers, order);
    }

    private static EncEdit Compare(
        Dictionary<string, string> oldMembers, Dictionary<string, string> newMembers, List<string> order)
    {
        var edit = new EncEdit();

        // A key that is gone is a delete, a rename, or a signature change — the three cases the
        // research note lists as rude on .NET 6 — and none of them can be told apart from source
        // without a semantic model. All three need a swap, so the distinction does not matter.
        foreach (var kv in oldMembers)
        {
            if (newMembers.ContainsKey(kv.Key)) continue;
            edit.Kind = EncEditKind.Rude;
            edit.RudeReason = "removed or changed signature: " + kv.Key;
            return edit;
        }

        foreach (string key in order)
        {
            if (!newMembers.TryGetValue(key, out string? body)) continue;
            if (oldMembers.TryGetValue(key, out string? was))
            {
                if (!string.Equals(was, body, StringComparison.Ordinal))
                {
                    if (!key.StartsWith("M ", StringComparison.Ordinal))
                    {
                        // A type header, a field initializer or an attribute list changed. Some of
                        // these are supported by the runtime; none of them are worth the risk of
                        // being wrong about from syntax alone.
                        edit.Kind = EncEditKind.Rude;
                        edit.RudeReason = "not a method body: " + key;
                        return edit;
                    }
                    edit.ChangedBodies.Add(key);
                }
                continue;
            }

            // New in this snapshot. Adding an abstract or virtual member changes a vtable and is
            // rude on .NET 6; a new type or a new ordinary member is supported.
            if (key.Contains(" abstract ", StringComparison.Ordinal) || key.Contains(" virtual ", StringComparison.Ordinal)
                || key.Contains(" override ", StringComparison.Ordinal))
            {
                edit.Kind = EncEditKind.Rude;
                edit.RudeReason = "added a virtual, abstract or override member: " + key;
                return edit;
            }
            edit.Inserted.Add(key);
        }

        if (edit.ChangedBodies.Count == 0 && edit.Inserted.Count == 0) edit.Kind = EncEditKind.None;
        else if (edit.Inserted.Count == 0) edit.Kind = EncEditKind.BodyOnly;
        else edit.Kind = EncEditKind.BodyAndInsert;
        return edit;
    }

    /// <summary>
    /// One entry per declaration: key is the signature (so a signature change is a key change),
    /// value is the body text with whitespace collapsed (so reformatting is not an edit).
    /// Method keys start with "M " because only those may be replaced by a delta.
    /// </summary>
    private static void Collect(string source, Dictionary<string, string> into, List<string>? order)
    {
        var root = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)).GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            string? key = null;
            string body = "";
            switch (node)
            {
                case BaseTypeDeclarationSyntax type:
                    key = "T " + Path(type) + " :: " + Normalize(Header(type));
                    body = "";
                    break;
                case MethodDeclarationSyntax method:
                    key = "M " + Path(method) + " " + Normalize(Signature(method));
                    body = Normalize(BodyOf(method.Body, method.ExpressionBody));
                    break;
                case ConstructorDeclarationSyntax ctor:
                    key = "M " + Path(ctor) + " " + Normalize(Signature(ctor));
                    body = Normalize(BodyOf(ctor.Body, ctor.ExpressionBody));
                    break;
                case PropertyDeclarationSyntax property:
                    key = "P " + Path(property) + " " + Normalize(property.Type.ToString() + " " + property.Identifier);
                    body = Normalize(property.AccessorList?.ToString() ?? property.ExpressionBody?.ToString() ?? "");
                    break;
                case FieldDeclarationSyntax field:
                    foreach (var v in field.Declaration.Variables)
                    {
                        string fieldKey = "F " + Path(field) + " " + Normalize(field.Declaration.Type + " " + v.Identifier);
                        Add(into, order, fieldKey, Normalize(v.Initializer?.ToString() ?? ""));
                    }
                    continue;
            }
            if (key == null) continue;
            Add(into, order, key, body);
        }
    }

    /// <summary>
    /// The method (and constructor) declarations of one tree, keyed exactly as
    /// <see cref="Classify(string,string)"/> keys them, so the watcher can turn a changed-body key
    /// back into the node it must ask the semantic model about. Kept next to the key builder on
    /// purpose: two places computing this key differently is the way this design breaks.
    /// </summary>
    public static Dictionary<string, SyntaxNode> MethodDeclarations(SyntaxTree tree)
    {
        var map = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            string? key = node switch
            {
                MethodDeclarationSyntax m => "M " + Path(m) + " " + Normalize(Signature(m)),
                ConstructorDeclarationSyntax c => "M " + Path(c) + " " + Normalize(Signature(c)),
                _ => null,
            };
            if (key != null && !map.ContainsKey(key)) map[key] = node;
        }
        return map;
    }

    private static void Add(Dictionary<string, string> into, List<string>? order, string key, string body)
    {
        // A duplicate key (two partial halves, or the same overload twice) makes the compare
        // ambiguous; fold it into one entry so a change to either half still shows up.
        if (into.TryGetValue(key, out string? existing)) into[key] = existing + "" + body;
        else
        {
            into[key] = body;
            order?.Add(key);
        }
    }

    private static string Header(BaseTypeDeclarationSyntax type)
    {
        string modifiers = string.Join(" ", type.Modifiers.Select(m => m.Text));
        string bases = type.BaseList?.ToString() ?? "";
        string parameters = type is TypeDeclarationSyntax t ? t.TypeParameterList?.ToString() ?? "" : "";
        string keyword = type is TypeDeclarationSyntax d ? d.Keyword.Text : "enum";
        return modifiers + " " + keyword + " " + type.Identifier + parameters + " " + bases;
    }

    private static string Signature(MethodDeclarationSyntax method) =>
        " " + string.Join(" ", method.Modifiers.Select(m => m.Text)) + " "
        + method.ReturnType + " " + method.Identifier + method.TypeParameterList
        + ParameterTypes(method.ParameterList) + " ";

    private static string Signature(ConstructorDeclarationSyntax ctor) =>
        " " + string.Join(" ", ctor.Modifiers.Select(m => m.Text)) + " .ctor"
        + ParameterTypes(ctor.ParameterList) + " ";

    private static string ParameterTypes(ParameterListSyntax? list) =>
        list == null
            ? "()"
            : "(" + string.Join(",", list.Parameters.Select(p =>
                string.Join(" ", p.Modifiers.Select(m => m.Text)) + " " + p.Type)) + ")";

    private static string BodyOf(BlockSyntax? block, ArrowExpressionClauseSyntax? arrow) =>
        block?.ToString() ?? arrow?.ToString() ?? "";

    /// <summary>Namespace and containing-type path, so two same-named members do not collide.</summary>
    private static string Path(SyntaxNode node)
    {
        var parts = new List<string>();
        for (var p = node.Parent; p != null; p = p.Parent)
        {
            if (p is BaseTypeDeclarationSyntax t) parts.Insert(0, t.Identifier.Text);
            else if (p is BaseNamespaceDeclarationSyntax ns) parts.Insert(0, ns.Name.ToString());
        }
        return string.Join(".", parts);
    }

    private static string Normalize(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
