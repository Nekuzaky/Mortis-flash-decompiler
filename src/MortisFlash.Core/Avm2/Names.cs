using MortisFlash.Decompiler;

namespace MortisFlash.Avm2;

/// <summary>
/// Formats AVM2 names in the context of an .as file: short name, package, imports.
/// </summary>
public sealed class NameContext
{
    readonly AbcFile _abc;
    public string Package { get; set; } = "";
    public SortedSet<string> Imports { get; } = new(StringComparer.Ordinal);
    /// <summary>Known user namespaces (URI → identifier), derived from Namespace constants.</summary>
    public Dictionary<string, string> NamespaceNames { get; }

    public NameContext(AbcFile abc, Dictionary<string, string> nsNames)
    {
        _abc = abc;
        NamespaceNames = nsNames;
    }

    public AbcFile Abc => _abc;

    public static readonly Dictionary<string, string> WellKnownNamespaces = new()
    {
        ["http://www.adobe.com/2006/flex/mx/internal"] = "mx_internal",
        ["http://adobe.com/AS3/2006/builtin"] = "AS3",
        ["http://www.adobe.com/2006/actionscript/flash/proxy"] = "flash_proxy",
        ["http://www.adobe.com/2008/actionscript/Flash10/"] = "flash10",
    };

    /// <summary>Short name of a multiname (without package), escaped if invalid.</summary>
    public string Short(int mnIndex)
    {
        var mn = _abc.Mn(mnIndex);
        if (mn.Kind == MnKind.TypeName) return TypeName(mnIndex);
        string n = _abc.Str(mn.Name);
        if (mnIndex == 0 || (n == "" && mn.HasRuntimeName)) return "*";
        return n == "void" ? n : CodeWriter.SafeName(n);
    }

    public string RawName(int mnIndex) => _abc.Str(_abc.Mn(mnIndex).Name);

    /// <summary>Package of a QName (null if it is not in a package namespace).</summary>
    public string? PackageOf(int mnIndex)
    {
        var mn = _abc.Mn(mnIndex);
        if (mn.Kind is MnKind.QName or MnKind.QNameA)
        {
            var ns = _abc.Ns(mn.Ns);
            if (ns.Kind is NsKind.Package or NsKind.PackageInternal) return ns.Name;
        }
        return null;
    }

    public string FullName(int mnIndex)
    {
        var pkg = PackageOf(mnIndex);
        string n = Short(mnIndex);
        return string.IsNullOrEmpty(pkg) ? n : pkg + "." + n;
    }

    /// <summary>Type name for a declaration; registers the required import.</summary>
    public string TypeName(int mnIndex)
    {
        if (mnIndex == 0) return "*";
        var mn = _abc.Mn(mnIndex);
        if (mn.Kind == MnKind.TypeName)
        {
            string baseName = TypeName(mn.QName);
            var ps = mn.Params.Select(p => p == 0 ? "*" : TypeName(p));
            return $"{baseName}.<{string.Join(", ", ps)}>";
        }
        Use(mnIndex);
        return Short(mnIndex);
    }

    /// <summary>Name used in code; adds the import if it is a QName from another package.</summary>
    public string Use(int mnIndex)
    {
        var mn = _abc.Mn(mnIndex);
        if (mn.Kind == MnKind.TypeName) return TypeName(mnIndex);
        var pkg = PackageOf(mnIndex);
        string n = Short(mnIndex);
        if (!string.IsNullOrEmpty(pkg) && pkg != Package && _abc.Ns(mn.Ns).Kind == NsKind.Package && !pkg.StartsWith("__AS3__"))
            Imports.Add(pkg + "." + _abc.Str(mn.Name));
        return n;
    }

    /// <summary>Access modifier inferred from a trait's namespace.</summary>
    public string Access(int mnIndex, out string? customNs)
    {
        customNs = null;
        var mn = _abc.Mn(mnIndex);
        if (mn.Kind is not (MnKind.QName or MnKind.QNameA)) return "public";
        var ns = _abc.Ns(mn.Ns);
        switch (ns.Kind)
        {
            case NsKind.Package: return "public";
            case NsKind.PackageInternal: return "internal";
            case NsKind.Private: return "private";
            case NsKind.Protected:
            case NsKind.StaticProtected: return "protected";
            case NsKind.Namespace:
            case NsKind.Explicit:
                customNs = NamespaceIdentifier(ns.Name);
                return customNs;
        }
        return "public";
    }

    public string NamespaceIdentifier(string uri)
    {
        if (NamespaceNames.TryGetValue(uri, out var id)) return id;
        if (WellKnownNamespaces.TryGetValue(uri, out id)) return id;
        // Otherwise, fall back to the last segment of the URI
        var seg = uri.TrimEnd('/').Split('/', ':').LastOrDefault(s => s.Length > 0) ?? "ns";
        return CodeWriter.SafeName(seg);
    }

    public static string NsKindName(NsKind k) => k switch
    {
        NsKind.Package => "PackageNamespace", NsKind.PackageInternal => "PackageInternalNs", NsKind.Private => "PrivateNamespace",
        NsKind.Protected => "ProtectedNamespace", NsKind.StaticProtected => "StaticProtectedNs", NsKind.Explicit => "ExplicitNamespace",
        _ => "Namespace",
    };

    /// <summary>
    /// P-code representation of a namespace. Private namespaces (and duplicates) carry their "#n" index:
    /// two private namespaces with the same name are distinct to the virtual machine.
    /// </summary>
    public string DescribeNs(int i)
    {
        // Index 0 inside a namespace set stands for "any namespace"
        if (i <= 0 || i >= _abc.Namespaces.Length) return "AnyNamespace()";
        var ns = _abc.Ns(i);
        string text = $"{NsKindName(ns.Kind)}({LitExpr.Quote(ns.Name)}";
        bool ambiguous = ns.Kind == NsKind.Private;
        if (!ambiguous)
            for (int k = 1; k < _abc.Namespaces.Length && !ambiguous; k++)
                ambiguous = k != i && _abc.Namespaces[k] == ns;
        return ambiguous ? text + $",#{i})" : text + ")";
    }

    /// <summary>P-code representation of a multiname (parsed back by the assembler).</summary>
    public string Describe(int mnIndex)
    {
        if (mnIndex == 0) return "*";
        var mn = _abc.Mn(mnIndex);
        string Set(int i) => i < _abc.NsSets.Length ? "[" + string.Join(",", _abc.NsSets[i].Select(DescribeNs)) + "]" : "[]";
        string N(int i) => LitExpr.Quote(_abc.Str(i));
        return mn.Kind switch
        {
            MnKind.QName or MnKind.QNameA => $"{mn.Kind}({DescribeNs(mn.Ns)},{N(mn.Name)})",
            MnKind.RTQName or MnKind.RTQNameA => $"{mn.Kind}({N(mn.Name)})",
            MnKind.RTQNameL or MnKind.RTQNameLA => $"{mn.Kind}()",
            MnKind.Multiname or MnKind.MultinameA => $"{mn.Kind}({N(mn.Name)},{Set(mn.NsSet)})",
            MnKind.MultinameL or MnKind.MultinameLA => $"{mn.Kind}({Set(mn.NsSet)})",
            MnKind.TypeName => $"TypeName({Describe(mn.QName)}<{string.Join(",", mn.Params.Select(Describe))}>)",
            _ => "?",
        };
    }
}
