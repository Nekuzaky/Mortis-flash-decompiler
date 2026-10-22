using System.Text;
using MortisFlash.Decompiler;

namespace MortisFlash.Avm2;

/// <summary>A reconstructed .as file: a class (or interface, or package-level definition).</summary>
public sealed class As3Unit
{
    public required AbcFile Abc { get; init; }
    public required int ScriptIndex { get; init; }
    public required string Package { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public int ClassIndex { get; init; } = -1;
    public string FullName => Package.Length > 0 ? Package + "." + Name : Name;
    public string FilePath => string.Join('/', Package.Split('.', StringSplitOptions.RemoveEmptyEntries).Append(Name).Select(FileSafe)) + ".as";

    CodeWriter? _code;
    public CodeWriter Code => _code ??= As3Printer.Print(this);
    public string Source => Code.ToString();

    public string PCode => As3Printer.PCode(this);

    static string FileSafe(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '_' or '$' or '-' ? c : '_');
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}

public static class As3Printer
{
    /// <summary>Lists the units (classes and package-level definitions) of an ABC file.</summary>
    public static List<As3Unit> Units(AbcFile abc)
    {
        var list = new List<As3Unit>();
        var names = new NameContext(abc, []);
        for (int si = 0; si < abc.Scripts.Count; si++)
        {
            var script = abc.Scripts[si];
            bool hasClass = false;
            foreach (var t in script.Traits)
            {
                if (t.Kind != TraitKind.Class) continue;
                hasClass = true;
                if (t.Index >= abc.Instances.Count) continue;
                var inst = abc.Instances[t.Index];
                list.Add(new As3Unit
                {
                    Abc = abc, ScriptIndex = si, ClassIndex = t.Index,
                    Package = names.PackageOf(inst.Name) ?? "", Name = SafeFile(names.RawName(inst.Name)),
                    Kind = inst.IsInterface ? "interface" : "class",
                });
            }
            foreach (var t in script.Traits)
            {
                if (t.Kind == TraitKind.Class) continue;
                // Package-level functions and variables: one unit per public trait
                list.Add(new As3Unit
                {
                    Abc = abc, ScriptIndex = si, Package = names.PackageOf(t.Name) ?? "", Name = SafeFile(names.RawName(t.Name)),
                    Kind = t.Kind is TraitKind.Method or TraitKind.Function or TraitKind.Getter or TraitKind.Setter ? "function" : "var",
                    ClassIndex = -1 - abc.Scripts[si].Traits.IndexOf(t),
                });
            }
            if (!hasClass && script.Traits.Count == 0)
                list.Add(new As3Unit { Abc = abc, ScriptIndex = si, Package = "", Name = $"script{si}", Kind = "script" });
        }
        return list;
    }

    static string SafeFile(string s) => s.Length == 0 ? "_" : s;

    static Dictionary<string, string> NamespaceConsts(AbcFile abc)
    {
        var d = new Dictionary<string, string>();
        void Scan(IEnumerable<Trait> traits)
        {
            foreach (var t in traits)
                if (t.Kind == TraitKind.Const && t.VKind is 0x08 && t.VIndex > 0)
                {
                    var uri = abc.Ns(t.VIndex).Name;
                    if (uri.Length > 0) d.TryAdd(uri, abc.Str(abc.Mn(t.Name).Name));
                }
        }
        foreach (var s in abc.Scripts) Scan(s.Traits);
        foreach (var c in abc.Classes) Scan(c.Traits);
        return d;
    }

    static readonly Dictionary<AbcFile, Dictionary<string, string>> NsCache = [];

    public static CodeWriter Print(As3Unit unit)
    {
        var abc = unit.Abc;
        Dictionary<string, string> nsNames;
        lock (NsCache)
        {
            if (!NsCache.TryGetValue(abc, out nsNames!)) NsCache[abc] = nsNames = NamespaceConsts(abc);
        }
        var names = new NameContext(abc, nsNames) { Package = unit.Package };
        var script = abc.Scripts[unit.ScriptIndex];
        // The body is written first (it collects the imports), then the package header
        var body = new CodeWriter();
        body.Push();
        if (unit.ClassIndex >= 0) PrintClass(body, abc, names, script, unit.ClassIndex);
        else if (unit.Kind == "script") PrintScriptInit(body, abc, names, script);
        else PrintPackageTrait(body, abc, names, script, script.Traits[-1 - unit.ClassIndex]);
        body.Pop();

        var w = new CodeWriter();
        w.Keyword("package");
        if (unit.Package.Length > 0)
        {
            w.Text(" ");
            w.Identifier(unit.Package);
        }
        w.Line();
        w.Punct("{");
        w.Line();
        var imports = names.Imports.Where(i => !IsSamePackage(i, unit.Package) && i != unit.FullName).ToList();
        foreach (var imp in imports)
        {
            w.Text(new string(' ', 4));
            w.Keyword("import");
            w.Text(" ");
            w.Identifier(imp);
            w.Punct(";");
            w.Line();
        }
        if (imports.Count > 0) w.Line();
        // Copy the body while preserving the highlighting tokens
        Append(w, body);
        w.Punct("}");
        w.Line();
        return w;
    }

    static bool IsSamePackage(string import, string pkg)
    {
        int dot = import.LastIndexOf('.');
        return dot >= 0 && import[..dot] == pkg;
    }

    static void Append(CodeWriter target, CodeWriter src)
    {
        string text = src.ToString();
        int pos = 0;
        foreach (var t in src.Tokens)
        {
            if (t.Start > pos) target.Text(text[pos..t.Start]);
            string s = text.Substring(t.Start, t.Length);
            switch (t.Kind)
            {
                case TokenKind.Keyword: target.Keyword(s); break;
                case TokenKind.Identifier: target.Identifier(s); break;
                case TokenKind.String: target.String(s); break;
                case TokenKind.Number: target.Number(s); break;
                case TokenKind.Comment: target.Comment(s); break;
                case TokenKind.Type: target.Type(s); break;
                default: target.Punct(s); break;
            }
            pos = t.Start + t.Length;
        }
        if (pos < text.Length) target.Text(text[pos..]);
    }

    // ------------------------------------------------------------------ classes

    static void PrintClass(CodeWriter w, AbcFile abc, NameContext names, ScriptInfo script, int ci)
    {
        var inst = abc.Instances[ci];
        var cls = abc.Classes[ci];
        string className = names.Short(inst.Name);
        var classTrait = script.Traits.FirstOrDefault(t => t.Kind == TraitKind.Class && t.Index == ci);
        if (classTrait != null) PrintMetadata(w, abc, classTrait);

        var staticOwner = new MethodOwner { Instance = inst, Class = cls, Script = script, IsStatic = true, ClassName = className };
        var instOwner = new MethodOwner { Instance = inst, Class = cls, Script = script, IsStatic = false, ClassName = className };

        // Decompile the constructors first to extract field initializers from them
        var staticInits = new Dictionary<string, Expr>();
        var instanceInits = new Dictionary<string, Expr>();
        var cinit = Decompile(abc, names, cls.CInit, staticOwner, out string? cinitErr);
        var iinit = inst.IsInterface ? [] : Decompile(abc, names, inst.IInit, instOwner, out _);
        if (cinit != null) ExtractStaticInits(cinit, cls.Traits, names, className, staticInits);
        if (iinit != null) ExtractInstanceInits(iinit, inst.Traits, names, instanceInits);

        w.Indent();
        string access = names.Access(inst.Name, out _);
        w.Keyword(access == "internal" ? "internal" : "public");
        w.Text(" ");
        if (inst.IsFinal) { w.Keyword("final"); w.Text(" "); }
        if (!inst.IsSealed && !inst.IsInterface) { w.Keyword("dynamic"); w.Text(" "); }
        w.Keyword(inst.IsInterface ? "interface" : "class");
        w.Text(" ");
        w.Identifier(className);
        if (inst.IsInterface)
        {
            if (inst.Interfaces.Length > 0)
            {
                w.Text(" ");
                w.Keyword("extends");
                w.Text(" " + string.Join(", ", inst.Interfaces.Select(names.TypeName)));
            }
        }
        else
        {
            if (inst.SuperName != 0 && names.RawName(inst.SuperName) != "Object")
            {
                w.Text(" ");
                w.Keyword("extends");
                w.Text(" ");
                w.Type(names.TypeName(inst.SuperName));
            }
            if (inst.Interfaces.Length > 0)
            {
                w.Text(" ");
                w.Keyword("implements");
                w.Text(" " + string.Join(", ", inst.Interfaces.Select(names.TypeName)));
            }
        }
        w.Line();
        w.Indent();
        w.Punct("{");
        w.Line();
        w.Push();

        bool any = false;
        void Gap()
        {
            if (any) w.Line();
            any = true;
        }

        // Static fields, then instance fields
        foreach (var t in cls.Traits.Where(t => t.Kind is TraitKind.Slot or TraitKind.Const))
        {
            Gap();
            PrintSlot(w, abc, names, t, true, staticInits.GetValueOrDefault(names.Short(t.Name)));
            any = true;
        }
        foreach (var t in inst.Traits.Where(t => t.Kind is TraitKind.Slot or TraitKind.Const))
        {
            Gap();
            PrintSlot(w, abc, names, t, false, instanceInits.GetValueOrDefault(names.Short(t.Name)));
        }

        // Remaining static initializer code
        if (cinit is { Count: > 0 })
        {
            Gap();
            w.Indent();
            w.Comment("// static initializer");
            w.Line();
            w.Indent();
            w.Punct("{");
            w.Line();
            w.Push();
            foreach (var s in cinit) s.Write(w);
            w.Pop();
            w.Indent();
            w.Punct("}");
            w.Line();
        }
        else if (cinitErr != null)
        {
            Gap();
            w.Indent();
            w.Comment("// static initializer: " + cinitErr);
            w.Line();
        }

        // Constructor
        if (!inst.IsInterface)
        {
            Gap();
            PrintMethod(w, abc, names, inst.IInit, instOwner, className, "public", null, false, false, iinit, isCtor: true);
        }

        foreach (var t in cls.Traits.Where(IsMethod))
        {
            Gap();
            PrintMethodTrait(w, abc, names, t, staticOwner, true, inst.IsInterface);
        }
        foreach (var t in inst.Traits.Where(IsMethod))
        {
            Gap();
            PrintMethodTrait(w, abc, names, t, instOwner, false, inst.IsInterface);
        }

        w.Pop();
        w.Indent();
        w.Punct("}");
        w.Line();
    }

    static bool IsMethod(Trait t) => t.Kind is TraitKind.Method or TraitKind.Getter or TraitKind.Setter or TraitKind.Function;

    static void PrintMetadata(CodeWriter w, AbcFile abc, Trait t)
    {
        foreach (var mi in t.Metadata)
        {
            if (mi >= abc.Metadata.Count) continue;
            var md = abc.Metadata[mi];
            string name = abc.Str(md.Name);
            if (name is "__go_to_definition_help" or "__go_to_ctor_definition_help") continue;
            w.Indent();
            w.Punct("[");
            w.Identifier(name);
            if (md.Items.Count > 0)
            {
                w.Punct("(");
                for (int i = 0; i < md.Items.Count; i++)
                {
                    if (i > 0) w.Punct(",");
                    var (k, v) = md.Items[i];
                    if (k != 0)
                    {
                        w.Identifier(abc.Str(k));
                        w.Punct("=");
                    }
                    w.String(LitExpr.Quote(abc.Str(v)));
                }
                w.Punct(")");
            }
            w.Punct("]");
            w.Line();
        }
    }

    static void PrintSlot(CodeWriter w, AbcFile abc, NameContext names, Trait t, bool isStatic, Expr? init)
    {
        PrintMetadata(w, abc, t);
        w.Indent();
        string access = names.Access(t.Name, out _);
        w.Keyword(access);
        w.Text(" ");
        if (isStatic) { w.Keyword("static"); w.Text(" "); }
        // Namespace constant: public namespace mx_internal = "…"
        if (t.Kind == TraitKind.Const && t.VKind == 0x08)
        {
            w.Keyword("namespace");
            w.Text(" ");
            w.Identifier(names.Short(t.Name));
            w.Punct(" = ");
            w.String(LitExpr.Quote(abc.Ns(t.VIndex).Name));
            w.Punct(";");
            w.Line();
            return;
        }
        w.Keyword(t.Kind == TraitKind.Const ? "const" : "var");
        w.Text(" ");
        w.Identifier(names.Short(t.Name));
        w.Punct(":");
        w.Type(names.TypeName(t.TypeName));
        if (t.VIndex != 0)
        {
            w.Punct(" = ");
            var v = MethodDecompiler.DefaultValue(abc, names, t.VIndex, t.VKind);
            if (v.StartsWith('"')) w.String(v);
            else if (v is "true" or "false" or "null" or "undefined") w.Keyword(v);
            else w.Number(v);
        }
        else if (init != null)
        {
            w.Punct(" = ");
            init.Write(w);
        }
        w.Punct(";");
        w.Line();
    }

    static void PrintMethodTrait(CodeWriter w, AbcFile abc, NameContext names, Trait t, MethodOwner owner, bool isStatic, bool isInterface)
    {
        PrintMetadata(w, abc, t);
        string access = names.Access(t.Name, out _);
        string? accessor = t.Kind == TraitKind.Getter ? "get" : t.Kind == TraitKind.Setter ? "set" : null;
        var mods = new List<string>();
        if (t.IsOverride) mods.Add("override");
        if (t.IsFinal && !isStatic) mods.Add("final");
        if (!isInterface) mods.Add(access);
        if (isStatic) mods.Add("static");
        PrintMethod(w, abc, names, t.Index, owner, names.Short(t.Name), string.Join(' ', mods), accessor, isInterface, false, null, false);
    }

    static void PrintMethod(CodeWriter w, AbcFile abc, NameContext names, int methodIndex, MethodOwner owner, string name,
        string modifiers, string? accessor, bool isInterface, bool _, List<Stmt>? preDecompiled, bool isCtor)
    {
        if (methodIndex >= abc.Methods.Count) return;
        var m = abc.Methods[methodIndex];
        w.Indent();
        if (modifiers.Length > 0)
        {
            foreach (var mod in modifiers.Split(' '))
            {
                w.Keyword(mod);
                w.Text(" ");
            }
        }
        bool native = m.Body == null && !isInterface;
        if (native) { w.Keyword("native"); w.Text(" "); }
        w.Keyword("function");
        w.Text(" ");
        if (accessor != null) { w.Keyword(accessor); w.Text(" "); }
        w.Identifier(name);
        w.Punct("(");
        List<string> sig;
        MethodDecompiler? md = null;
        if (m.Body != null)
        {
            md = new MethodDecompiler(abc, names, m, owner);
            sig = md.Signature();
        }
        else sig = SignatureWithoutBody(abc, names, m);
        w.Text(string.Join(", ", sig));
        w.Punct(")");
        if (!isCtor)
        {
            w.Punct(" : ");
            w.Type(m.ReturnType == 0 ? "*" : names.TypeName(m.ReturnType));
        }
        if (m.Body == null)
        {
            w.Punct(";");
            w.Line();
            return;
        }
        List<Stmt> body;
        if (preDecompiled != null) body = preDecompiled;
        else
        {
            try { body = md!.Decompile(); }
            catch (Exception e)
            {
                body = [new CommentStmt("Decompilation failed: " + e.Message), new CommentStmt(PCodeOf(abc, names, m).TrimEnd())];
            }
        }
        w.Block(body);
    }

    static List<string> SignatureWithoutBody(AbcFile abc, NameContext names, MethodInfo m)
    {
        var list = new List<string>();
        int n = m.ParamTypes.Length;
        int firstOpt = n - m.Options.Count;
        for (int i = 0; i < n; i++)
        {
            string pn = m.HasParamNames && i < m.ParamNames.Length && abc.Str(m.ParamNames[i]).Length > 0 ? abc.Str(m.ParamNames[i]) : $"param{i + 1}";
            string s = $"{pn}:{(m.ParamTypes[i] == 0 ? "*" : names.TypeName(m.ParamTypes[i]))}";
            if (i >= firstOpt) s += " = " + MethodDecompiler.DefaultValue(abc, names, m.Options[i - firstOpt].Value, m.Options[i - firstOpt].Kind);
            list.Add(s);
        }
        if (m.NeedRest) list.Add("...rest");
        return list;
    }

    static List<Stmt>? Decompile(AbcFile abc, NameContext names, int methodIndex, MethodOwner owner, out string? error)
    {
        error = null;
        if (methodIndex >= abc.Methods.Count || abc.Methods[methodIndex].Body == null) return null;
        try
        {
            return new MethodDecompiler(abc, names, abc.Methods[methodIndex], owner).Decompile();
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Static fields initialized in cinit: "X = value" is moved up into the declaration.</summary>
    static void ExtractStaticInits(List<Stmt> cinit, List<Trait> traits, NameContext names, string className, Dictionary<string, Expr> inits)
    {
        var slotNames = traits.Where(t => t.Kind is TraitKind.Slot or TraitKind.Const && t.VIndex == 0)
            .Select(t => names.Short(t.Name)).ToHashSet();
        for (int i = 0; i < cinit.Count; i++)
        {
            if (cinit[i] is not ExprStmt { Expr: AssignExpr { Op: "=" } a }) continue;
            string? target = a.Target switch
            {
                NameExpr n => n.Name,
                MemberExpr { Object: NameExpr cn } m when cn.Name == className => m.Name,
                _ => null,
            };
            if (target == null || !slotNames.Contains(target) || inits.ContainsKey(target)) continue;
            inits[target] = a.Value;
            cinit.RemoveAt(i);
            i--;
        }
        // ASC's cinit ends with an implicit "return;" that has already been removed
    }

    /// <summary>Fields initialized in the constructor before super() are moved up into their declaration.</summary>
    static void ExtractInstanceInits(List<Stmt> iinit, List<Trait> traits, NameContext names, Dictionary<string, Expr> inits)
    {
        var slotNames = traits.Where(t => t.Kind is TraitKind.Slot or TraitKind.Const && t.VIndex == 0)
            .Select(t => names.Short(t.Name)).ToHashSet();
        int superIdx = iinit.FindIndex(s => s is ExprStmt { Expr: CallExpr { Function: SuperExpr } });
        if (superIdx < 0) return;
        for (int i = 0; i < superIdx; i++)
        {
            if (iinit[i] is not ExprStmt { Expr: AssignExpr { Op: "=", Target: MemberExpr { Object: ThisExpr } m } a }) continue;
            if (!slotNames.Contains(m.Name) || inits.ContainsKey(m.Name)) continue;
            inits[m.Name] = a.Value;
            iinit.RemoveAt(i);
            i--;
            superIdx--;
        }
    }

    // ------------------------------------------------------------------ package-level definitions

    static void PrintPackageTrait(CodeWriter w, AbcFile abc, NameContext names, ScriptInfo script, Trait t)
    {
        var owner = new MethodOwner { Script = script, IsStatic = true };
        if (t.Kind is TraitKind.Slot or TraitKind.Const)
        {
            Expr? init = null;
            var initStmts = Decompile(abc, names, script.Init, owner, out _);
            if (initStmts != null)
            {
                var d = new Dictionary<string, Expr>();
                ExtractStaticInits(initStmts, script.Traits, names, "", d);
                init = d.GetValueOrDefault(names.Short(t.Name));
            }
            PrintSlot(w, abc, names, t, false, init);
            return;
        }
        PrintMetadata(w, abc, t);
        string access = names.Access(t.Name, out _);
        int method = t.Index;
        PrintMethod(w, abc, names, method, owner, names.Short(t.Name), access,
            t.Kind == TraitKind.Getter ? "get" : t.Kind == TraitKind.Setter ? "set" : null, false, false, null, false);
    }

    static void PrintScriptInit(CodeWriter w, AbcFile abc, NameContext names, ScriptInfo script)
    {
        var owner = new MethodOwner { Script = script, IsStatic = true };
        var body = Decompile(abc, names, script.Init, owner, out var err) ?? [new CommentStmt(err ?? "empty script")];
        foreach (var s in body) s.Write(w);
    }

    // ------------------------------------------------------------------ p-code

    public static string PCode(As3Unit unit)
    {
        var abc = unit.Abc;
        var names = new NameContext(abc, []);
        var sb = new StringBuilder();
        var script = abc.Scripts[unit.ScriptIndex];
        void Method(string title, int mi)
        {
            if (mi >= abc.Methods.Count) return;
            sb.Append("// ").Append(title).Append("  (method ").Append(mi).Append(")\n");
            var m = abc.Methods[mi];
            if (m.Body == null) { sb.Append("    (no body)\n\n"); return; }
            sb.Append($"    maxstack {m.Body.MaxStack}  locals {m.Body.LocalCount}  scope {m.Body.InitScopeDepth}..{m.Body.MaxScopeDepth}\n");
            sb.Append(PCodeOf(abc, names, m)).Append('\n');
        }
        if (unit.ClassIndex >= 0)
        {
            var inst = abc.Instances[unit.ClassIndex];
            var cls = abc.Classes[unit.ClassIndex];
            Method("static initializer", cls.CInit);
            Method("constructor", inst.IInit);
            foreach (var t in cls.Traits.Where(IsMethod)) Method("static " + names.Short(t.Name), t.Index);
            foreach (var t in inst.Traits.Where(IsMethod)) Method(KindPrefix(t) + names.Short(t.Name), t.Index);
        }
        else if (unit.Kind == "script") Method("script init", script.Init);
        else
        {
            var t = script.Traits[-1 - unit.ClassIndex];
            if (IsMethod(t)) Method(names.Short(t.Name), t.Index);
            Method("script init", script.Init);
        }
        return sb.ToString();
    }

    /// <summary>Methods of a unit (for editing): display title, method and the context it runs in.</summary>
    public static List<(string Title, MethodInfo Method, MethodOwner Owner)> Methods(As3Unit unit)
    {
        var abc = unit.Abc;
        var names = new NameContext(abc, []);
        var list = new List<(string, MethodInfo, MethodOwner)>();
        var script = abc.Scripts[unit.ScriptIndex];
        void Add(string title, int mi, MethodOwner owner)
        {
            if (mi < abc.Methods.Count && abc.Methods[mi].Body != null) list.Add((title, abc.Methods[mi], owner));
        }
        var scriptOwner = new MethodOwner { Script = script, IsStatic = true };
        if (unit.ClassIndex >= 0)
        {
            var inst = abc.Instances[unit.ClassIndex];
            var cls = abc.Classes[unit.ClassIndex];
            string className = names.Short(inst.Name);
            var instOwner = new MethodOwner { Instance = inst, Class = cls, Script = script, IsStatic = false, ClassName = className };
            var staticOwner = new MethodOwner { Instance = inst, Class = cls, Script = script, IsStatic = true, ClassName = className };
            Add("constructor", inst.IInit, instOwner);
            foreach (var t in inst.Traits.Where(IsMethod)) Add(KindPrefix(t) + names.Short(t.Name), t.Index, instOwner);
            foreach (var t in cls.Traits.Where(IsMethod)) Add("static " + KindPrefix(t) + names.Short(t.Name), t.Index, staticOwner);
            Add("static initializer", cls.CInit, staticOwner);
        }
        else if (unit.Kind != "script")
        {
            var t = script.Traits[-1 - unit.ClassIndex];
            if (IsMethod(t)) Add(names.Short(t.Name), t.Index, scriptOwner);
        }
        Add("script initializer", script.Init, scriptOwner);
        return list;
    }

    /// <summary>Decompiled body of one method (statements only), as edited in the source editor.</summary>
    public static string MethodBodySource(As3Unit unit, MethodInfo method, MethodOwner owner)
    {
        var names = new NameContext(unit.Abc, []) { Package = unit.Package };
        var w = new CodeWriter();
        foreach (var s in new MethodDecompiler(unit.Abc, names, method, owner).Decompile()) s.Write(w);
        return w.ToString();
    }

    /// <summary>Read-only signature line shown above the body editor.</summary>
    public static string MethodSignature(As3Unit unit, MethodInfo method, MethodOwner owner, string title)
    {
        var names = new NameContext(unit.Abc, []) { Package = unit.Package };
        var sig = new MethodDecompiler(unit.Abc, names, method, owner).Signature();
        string ret = method.ReturnType == 0 ? "*" : names.TypeName(method.ReturnType);
        return $"function {title}({string.Join(", ", sig)}) : {ret}";
    }

    static string KindPrefix(Trait t) => t.Kind == TraitKind.Getter ? "get " : t.Kind == TraitKind.Setter ? "set " : "";

    public static string PCodeOf(AbcFile abc, NameContext names, MethodInfo m)
    {
        if (m.Body == null) return "";
        var sb = new StringBuilder();
        var code = Opcodes.Disassemble(m.Body.Code);
        var targets = new HashSet<int>(code.SelectMany(i => i.Targets));
        foreach (var e in m.Body.Exceptions)
        {
            targets.Add(e.From); targets.Add(e.To); targets.Add(e.Target);
            sb.Append($"    try from ofs{e.From:X4} to ofs{e.To:X4} target ofs{e.Target:X4} type {names.Describe(e.ExcType)} name {names.Describe(e.VarName)}\n");
        }
        foreach (var ins in code)
        {
            if (targets.Contains(ins.Offset)) sb.Append($"  ofs{ins.Offset:X4}:\n");
            sb.Append("      ").Append(ins.Op == -1 ? "<invalid bytes>" : ins.Name);
            sb.Append(Operands(abc, names, ins));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static string Operands(AbcFile abc, NameContext names, Instr ins)
    {
        if (ins.Targets.Length > 0)
        {
            if (ins.Op == Opcodes.LookupSwitch)
                return " ofs" + ins.Targets[0].ToString("X4") + ", [" + string.Join(", ", ins.Targets.Skip(1).Select(t => "ofs" + t.ToString("X4"))) + "]";
            return " ofs" + ins.Targets[0].ToString("X4");
        }
        var a = ins.Args;
        if (a.Length == 0) return "";
        switch (ins.Op)
        {
            case Opcodes.PushString:
            case Opcodes.DebugFile:
            case Opcodes.Dxns:
                return " " + LitExpr.Quote(abc.Str(a[0]));
            case Opcodes.PushInt: return " " + (a[0] < abc.Ints.Length ? abc.Ints[a[0]] : 0);
            case Opcodes.PushUInt: return " " + (a[0] < abc.UInts.Length ? abc.UInts[a[0]] : 0);
            case Opcodes.PushDouble: return " " + LitExpr.FormatNumber(a[0] < abc.Doubles.Length ? abc.Doubles[a[0]] : double.NaN);
            case Opcodes.PushNamespace: return " " + names.DescribeNs(a[0]);
            case Opcodes.PushShort: return " " + (short)a[0];
            case Opcodes.GetSuper: case Opcodes.SetSuper: case Opcodes.GetDescendants: case Opcodes.FindPropStrict:
            case Opcodes.FindProperty: case Opcodes.FindDef: case Opcodes.GetLex: case Opcodes.SetProperty:
            case Opcodes.GetProperty: case Opcodes.InitProperty: case Opcodes.DeleteProperty: case Opcodes.Coerce:
            case Opcodes.AsType: case Opcodes.IsType:
                return " " + names.Describe(a[0]);
            case Opcodes.CallProperty: case Opcodes.CallPropLex: case Opcodes.CallPropVoid: case Opcodes.CallSuper:
            case Opcodes.CallSuperVoid: case Opcodes.ConstructProp:
                return " " + names.Describe(a[0]) + ", " + a[1];
            case Opcodes.Debug:
                return $" {a[0]}, {LitExpr.Quote(abc.Str(a[1]))}, {a[2]}, {a[3]}";
            default:
                return " " + string.Join(", ", a);
        }
    }
}
