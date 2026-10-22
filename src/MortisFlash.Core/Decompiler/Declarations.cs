namespace MortisFlash.Decompiler;

/// <summary>
/// Places "var" declarations once the method has been fully structured: the first assignment statement of each
/// local becomes its declaration, and locals that are only written inside expressions are declared at the top.
/// Doing this after structuring keeps declarations correct even when blocks are translated more than once.
/// </summary>
public static class Declarations
{
    /// <summary>Declaration info for an assignable expression, or null when it is not a declarable local.</summary>
    public delegate (object Key, string Name, string? Type)? Declarable(Expr target);

    public static List<Stmt> Apply(List<Stmt> body, Declarable declarable)
    {
        var declared = new HashSet<object>();
        var seen = new Dictionary<object, (string Name, string? Type)>();
        Block(body, declarable, declared, seen);
        // Locals only ever written inside an expression still need a declaration
        var missing = seen.Where(k => !declared.Contains(k.Key)).Select(k => (Stmt)new VarStmt(k.Value.Name, k.Value.Type, null)).ToList();
        return missing.Count == 0 ? body : [.. missing, .. body];
    }

    static void Block(List<Stmt> list, Declarable d, HashSet<object> declared, Dictionary<object, (string, string?)> seen)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s is ExprStmt { Expr: AssignExpr { Op: "=" } a } && d(a.Target) is { } info && !declared.Contains(info.Key))
            {
                declared.Add(info.Key);
                Visit(a.Value, d, seen);
                list[i] = new VarStmt(info.Name, info.Type, a.Value) { Var = a.Target is LocalExpr l ? l.Var : null };
                continue;
            }
            Stmt(s, d, declared, seen);
        }
    }

    static void Stmt(Stmt s, Declarable d, HashSet<object> declared, Dictionary<object, (string, string?)> seen)
    {
        switch (s)
        {
            case ExprStmt e:
                Visit(e.Expr, d, seen);
                break;
            case VarStmt v:
                if (v.Init != null) Visit(v.Init, d, seen);
                break;
            case ReturnStmt { Value: { } rv }:
                Visit(rv, d, seen);
                break;
            case ThrowStmt t:
                Visit(t.Value, d, seen);
                break;
            case IfStmt i:
                Visit(i.Cond, d, seen);
                Block(i.Then, d, declared, seen);
                if (i.Else != null) Block(i.Else, d, declared, seen);
                break;
            case ForStmt f:
                if (f.Init != null)
                {
                    var init = new List<Stmt> { f.Init };
                    Block(init, d, declared, seen);
                    f.Init = init[0];
                }
                if (f.Cond != null) Visit(f.Cond, d, seen);
                Block(f.Body, d, declared, seen);
                foreach (var u in f.Update) Stmt(u, d, declared, seen);
                break;
            case ForInStmt fi:
                if (!fi.Declare && d(fi.Variable) is { } info && declared.Add(info.Key))
                {
                    fi.Declare = true;
                    fi.DeclareType = info.Type;
                    seen.Remove(info.Key);
                }
                else Visit(fi.Variable, d, seen);
                Visit(fi.Object, d, seen);
                Block(fi.Body, d, declared, seen);
                break;
            case WhileStmt w:
                Visit(w.Cond, d, seen);
                Block(w.Body, d, declared, seen);
                break;
            case DoWhileStmt dw:
                Block(dw.Body, d, declared, seen);
                Visit(dw.Cond, d, seen);
                break;
            case SwitchStmt sw:
                Visit(sw.Discriminant, d, seen);
                foreach (var c in sw.Cases)
                {
                    foreach (var l in c.Labels) if (l != null) Visit(l, d, seen);
                    Block(c.Body, d, declared, seen);
                }
                break;
            case TryStmt t:
                Block(t.Body, d, declared, seen);
                foreach (var c in t.Catches) Block(c.Body, d, declared, seen);
                if (t.Finally != null) Block(t.Finally, d, declared, seen);
                break;
            case WithStmt w:
                Visit(w.Object, d, seen);
                Block(w.Body, d, declared, seen);
                break;
            case BlockStmt b:
                Block(b.Body, d, declared, seen);
                break;
        }
    }

    /// <summary>Records every declarable local read or written inside an expression (not inside nested functions).</summary>
    static void Visit(Expr e, Declarable d, Dictionary<object, (string, string?)> seen)
    {
        if (d(e) is { } info)
        {
            seen.TryAdd(info.Key, (info.Name, info.Type));
            return;
        }
        switch (e)
        {
            case MemberExpr m: Visit(m.Object, d, seen); break;
            case IndexExpr ix: Visit(ix.Object, d, seen); Visit(ix.Index, d, seen); break;
            case CallExpr c: Visit(c.Function, d, seen); foreach (var a in c.Args) Visit(a, d, seen); break;
            case NewExpr n: Visit(n.Type, d, seen); foreach (var a in n.Args) Visit(a, d, seen); break;
            case BinaryExpr b: Visit(b.Left, d, seen); Visit(b.Right, d, seen); break;
            case UnaryExpr u: Visit(u.Operand, d, seen); break;
            case PostfixExpr p: Visit(p.Operand, d, seen); break;
            case AssignExpr a: Visit(a.Target, d, seen); Visit(a.Value, d, seen); break;
            case TernaryExpr t: Visit(t.Cond, d, seen); Visit(t.Then, d, seen); Visit(t.Else, d, seen); break;
            case ArrayLitExpr arr: foreach (var x in arr.Items) Visit(x, d, seen); break;
            case ObjectLitExpr o: foreach (var (k, v) in o.Props) { Visit(k, d, seen); Visit(v, d, seen); } break;
            default:
                if (!ReferenceEquals(e.Core, e)) Visit(e.Core, d, seen);
                break;
        }
    }
}
