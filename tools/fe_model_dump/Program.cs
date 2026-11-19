// fe_model_dump — Source 2 FeModel (cloth / softbody) dump & comparison tool.
//
// Commands:
//   scan    --vpk <pak_dir.vpk> [--prefix models/heroes] [--out report.md]
//           Lists every .vmdl_c under the prefix that carries a FeModel, with summary stats.
//   compare --out <dir> <spec> [<spec> ...]
//           spec = label=<pak_dir.vpk>::<entry path | *>   ("*" = every vmdl_c with a FeModel in that VPK)
//           Writes <dir>/femodel_comparison.md (side by side) and <dir>/nodes_<label>.csv (per node).
//
// Units: InitPose positions / radii are Source inches. Gravity in in/s^2.
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.RubikonPhysics.Softbody;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0)
{
    Console.WriteLine("usage: fe_model_dump scan --vpk <pak_dir.vpk> [--prefix models/heroes] [--out report.md]");
    Console.WriteLine("       fe_model_dump compare --out <dir> label=<pak_dir.vpk>::<entry|*> [...]");
    return 1;
}

try
{
    return args[0] switch
    {
        "scan" => Commands.Scan(args.Skip(1).ToArray()),
        "compare" => Commands.Compare(args.Skip(1).ToArray()),
        _ => throw new ArgumentException($"unknown command '{args[0]}'")
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex);
    return 2;
}

static class Commands
{
    static string Opt(string[] a, string name, string def = null)
    {
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
    }

    public static int Scan(string[] a)
    {
        string vpk = Opt(a, "--vpk") ?? throw new ArgumentException("--vpk required");
        string prefix = Opt(a, "--prefix", "models/heroes").Replace('\\', '/').ToLowerInvariant();
        string outPath = Opt(a, "--out");

        using var pkg = VpkIo.Open(vpk);
        var sb = new StringBuilder();
        sb.AppendLine($"# FeModel scan — `{vpk}` (prefix `{prefix}`)\n");
        sb.AppendLine("| Model | Nodes | Static | Dynamic | Rods | Quads | Capsules | Spheres | Boxes | Planes | Jiggle | Cloth pieces | Median goal force (dyn) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        int found = 0, scanned = 0;
        if (!pkg.Entries.TryGetValue("vmdl_c", out var entries)) entries = new List<PackageEntry>();
        foreach (var e in entries.OrderBy(x => x.GetFullPath()))
        {
            string path = e.GetFullPath();
            if (!path.ToLowerInvariant().StartsWith(prefix)) continue;
            scanned++;
            FeModel fe;
            try { fe = VpkIo.LoadFeModel(pkg, e, out _); }
            catch (Exception ex) { Console.Error.WriteLine($"  skip {path}: {ex.Message}"); continue; }
            if (fe == null || fe.NodeCount == 0) continue;
            found++;
            var an = new FeAnalysis(fe);
            var dynForces = an.DynamicNodes.Select(n => an.Integrator(n)?.AnimationForceAttraction ?? float.NaN).Where(float.IsFinite).ToList();
            sb.AppendLine($"| `{path}` | {fe.NodeCount} | {fe.StaticNodes} | {fe.NodeCount - fe.StaticNodes} | {an.RodCount} | {an.QuadCount} | " +
                          $"{fe.TaperedCapsuleRigids?.Length ?? 0} | {fe.SphereRigids?.Length ?? 0} | {fe.BoxRigids?.Length ?? 0} | {fe.CollisionPlanes?.Length ?? 0} | " +
                          $"{fe.JiggleBones?.Length ?? 0} | {an.Components.Count} | {Stats.Median(dynForces):F3} |");
            Console.WriteLine($"  [{found}] {path}  nodes={fe.NodeCount} dyn={fe.NodeCount - fe.StaticNodes}");
        }
        sb.AppendLine($"\nScanned {scanned} models, {found} with FeModel.");
        Emit(sb.ToString(), outPath);
        return 0;
    }

    public static int Compare(string[] a)
    {
        string outDir = Opt(a, "--out") ?? throw new ArgumentException("--out required");
        Directory.CreateDirectory(outDir);
        var specs = a.Where(x => x.Contains('=') && x.Contains("::")).ToList();
        if (specs.Count == 0) throw new ArgumentException("no specs (label=<vpk>::<entry|*>)");

        var models = new List<LoadedModel>();
        var pkgs = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var spec in specs)
            {
                int eq = spec.IndexOf('=');
                string label = spec[..eq];
                string rest = spec[(eq + 1)..];
                int sep = rest.IndexOf("::", StringComparison.Ordinal);
                string vpk = rest[..sep];
                string entry = rest[(sep + 2)..].Replace('\\', '/');
                if (!pkgs.TryGetValue(vpk, out var pkg)) { pkg = VpkIo.Open(vpk); pkgs[vpk] = pkg; }

                var targets = new List<PackageEntry>();
                if (entry == "*")
                {
                    if (pkg.Entries.TryGetValue("vmdl_c", out var list)) targets.AddRange(list);
                }
                else
                {
                    var pe = pkg.FindEntry(entry) ?? throw new FileNotFoundException($"{entry} not in {vpk}");
                    targets.Add(pe);
                }

                int k = 0;
                foreach (var t in targets)
                {
                    var fe = VpkIo.LoadFeModel(pkg, t, out var model);
                    if (fe == null || fe.NodeCount == 0)
                    {
                        if (entry != "*") Console.Error.WriteLine($"  WARN {label}: {t.GetFullPath()} has no FeModel");
                        continue;
                    }
                    string lbl = targets.Count > 1 ? $"{label}_{k++}" : label;
                    models.Add(new LoadedModel(lbl, vpk, t.GetFullPath(), fe, model));
                    Console.WriteLine($"  loaded {lbl}: {t.GetFullPath()} ({fe.NodeCount} nodes)");
                }
            }
        }
        finally
        {
            foreach (var p in pkgs.Values) p.Dispose();
        }

        if (models.Count == 0) throw new InvalidOperationException("no FeModels loaded");
        var report = Report.Build(models, outDir);
        File.WriteAllText(Path.Combine(outDir, "femodel_comparison.md"), report);
        Console.WriteLine($"Wrote {Path.Combine(outDir, "femodel_comparison.md")}");
        return 0;
    }

    static void Emit(string text, string outPath)
    {
        if (string.IsNullOrEmpty(outPath)) { Console.WriteLine(text); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, text);
        Console.WriteLine($"Wrote {outPath}");
    }
}

record LoadedModel(string Label, string Vpk, string Entry, FeModel Fe, Model Model);

static class VpkIo
{
    public static Package Open(string path)
    {
        var pkg = new Package();
        pkg.Read(path);
        return pkg;
    }

    static Resource ReadResource(Package pkg, PackageEntry e)
    {
        pkg.ReadEntry(e, out byte[] data);
        var r = new Resource { FileName = e.GetFullPath() };
        r.Read(new MemoryStream(data));
        return r;
    }

    public static FeModel LoadFeModel(Package pkg, PackageEntry e, out Model model)
    {
        var res = ReadResource(pkg, e);
        model = res.DataBlock as Model;
        if (model == null) return null;
        var phys = model.GetEmbeddedPhys();
        if (phys?.FeModel != null) return phys.FeModel;
        foreach (var name in model.GetReferencedPhysNames() ?? Enumerable.Empty<string>())
        {
            string p = name.EndsWith("_c", StringComparison.Ordinal) ? name : name + "_c";
            var pe = pkg.FindEntry(p);
            if (pe == null) continue;
            var pr = ReadResource(pkg, pe);
            if (pr.DataBlock is PhysAggregateData pd && pd.FeModel != null) return pd.FeModel;
        }
        return null;
    }
}

static class Stats
{
    public static float Median(IEnumerable<float> xs)
    {
        var l = xs.Where(float.IsFinite).OrderBy(x => x).ToList();
        if (l.Count == 0) return float.NaN;
        return l.Count % 2 == 1 ? l[l.Count / 2] : 0.5f * (l[l.Count / 2 - 1] + l[l.Count / 2]);
    }

    public static string Mmm(IEnumerable<float> xs, string fmt = "F3")
    {
        var l = xs.Where(float.IsFinite).ToList();
        if (l.Count == 0) return "–";
        return $"{l.Min().ToString(fmt)} / {Median(l).ToString(fmt)} / {l.Max().ToString(fmt)}";
    }
}

/// <summary>Topology + per-node analysis of one FeModel.</summary>
sealed class FeAnalysis
{
    public readonly FeModel Fe;
    public readonly List<int> DynamicNodes = new();
    public readonly List<Component> Components = new();
    public readonly int[] ComponentOf;
    public readonly float[] DistToAnchor;
    public readonly float[] TNorm;
    public int RodCount, QuadCount, TriCount, SpringCount;
    readonly List<(int A, int B)> _edges = new();

    public sealed class Component
    {
        public int Index;
        public string Name = "";
        public readonly List<int> Nodes = new();
        public readonly HashSet<int> Anchors = new();
        public float MaxDist;
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
    }

    public FeAnalysis(FeModel fe)
    {
        Fe = fe;
        int n = fe.NodeCount;
        for (int i = fe.StaticNodes; i < n; i++) DynamicNodes.Add(i);
        CollectEdges();

        // Union-find over dynamic-dynamic edges; static nodes become anchors of adjacent components.
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var (a, b) in _edges)
        {
            if (a < fe.StaticNodes || b < fe.StaticNodes) continue;
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        ComponentOf = Enumerable.Repeat(-1, n).ToArray();
        var byRoot = new Dictionary<int, Component>();
        foreach (int d in DynamicNodes)
        {
            int r = Find(d);
            if (!byRoot.TryGetValue(r, out var c)) { c = new Component { Index = byRoot.Count }; byRoot[r] = c; Components.Add(c); }
            c.Nodes.Add(d);
            ComponentOf[d] = c.Index;
            var p = Pos(d);
            c.Min = Vector3.Min(c.Min, p); c.Max = Vector3.Max(c.Max, p);
        }
        foreach (var (a, b) in _edges)
        {
            bool sa = a < fe.StaticNodes, sb = b < fe.StaticNodes;
            if (sa == sb) continue;
            int s = sa ? a : b, d = sa ? b : a;
            if (ComponentOf[d] >= 0) Components[ComponentOf[d]].Anchors.Add(s);
        }

        DistToAnchor = Enumerable.Repeat(float.NaN, n).ToArray();
        TNorm = Enumerable.Repeat(float.NaN, n).ToArray();
        foreach (var c in Components)
        {
            var anchors = c.Anchors.Count > 0 ? c.Anchors.ToList() : Enumerable.Range(0, fe.StaticNodes).ToList();
            foreach (int d in c.Nodes)
            {
                var p = Pos(d);
                float best = anchors.Count == 0 ? 0f : anchors.Min(s => Vector3.Distance(p, Pos(s)));
                DistToAnchor[d] = best;
                c.MaxDist = MathF.Max(c.MaxDist, best);
            }
            foreach (int d in c.Nodes) TNorm[d] = c.MaxDist > 1e-4f ? DistToAnchor[d] / c.MaxDist : 0f;
            c.Name = NameComponent(c);
        }
    }

    public Vector3 Pos(int node) => Fe.InitPose != null && node < Fe.InitPose.Length ? Fe.InitPose[node].Position : Vector3.Zero;
    public string Name(int node) => Fe.CtrlName != null && node < Fe.CtrlName.Length ? Fe.CtrlName[node] ?? $"#{node}" : $"#{node}";

    public FeModel.FeNodeIntegrator? Integrator(int node)
    {
        var arr = Fe.NodeIntegrator;
        if (arr == null || arr.Length == 0) return null;
        if (arr.Length == Fe.NodeCount) return node < arr.Length ? arr[node] : null;
        int di = node - Fe.StaticNodes; // dynamic-indexed variant
        return di >= 0 && di < arr.Length ? arr[di] : null;
    }

    public float DynIndexed(float[] arr, int node)
    {
        if (arr == null || arr.Length == 0) return float.NaN;
        if (arr.Length == Fe.NodeCount) return arr[node];
        int di = node - Fe.StaticNodes;
        return di >= 0 && di < arr.Length ? arr[di] : float.NaN;
    }

    void Add(int a, int b) { if (a != b && a >= 0 && b >= 0 && a < Fe.NodeCount && b < Fe.NodeCount) _edges.Add((a, b)); }

    void CollectEdges()
    {
        foreach (var r in Fe.Rods ?? Array.Empty<FeModel.FeRodConstraint>()) { RodCount++; if (r.Nodes?.Length >= 2) Add(r.Nodes[0], r.Nodes[1]); }
        foreach (var r in Fe.SimdRods ?? Array.Empty<FeModel.FeSimdRodConstraint>())
        {
            if (r.Nodes == null || r.Nodes.Length < 2) continue;
            for (int lane = 0; lane < r.Nodes[0].Length; lane++) { RodCount++; Add(r.Nodes[0][lane], r.Nodes[1][lane]); }
        }
        foreach (var q in Fe.Quads ?? Array.Empty<FeModel.FeQuad>())
        {
            QuadCount++;
            if (q.Nodes == null) continue;
            for (int i = 0; i < q.Nodes.Length; i++) Add(q.Nodes[i], q.Nodes[(i + 1) % q.Nodes.Length]);
        }
        foreach (var q in Fe.SimdQuads ?? Array.Empty<FeModel.FeSimdQuad>())
        {
            if (q.Nodes == null || q.Nodes.Length == 0) continue;
            int corners = q.Nodes.Length;
            for (int lane = 0; lane < q.Nodes[0].Length; lane++)
            {
                QuadCount++;
                for (int c = 0; c < corners; c++) Add(q.Nodes[c][lane], q.Nodes[(c + 1) % corners][lane]);
            }
        }
        foreach (var t in Fe.Tris ?? Array.Empty<FeModel.FeTri>())
        {
            TriCount++;
            if (t.Nodes == null) continue;
            for (int i = 0; i < t.Nodes.Length; i++) Add(t.Nodes[i], t.Nodes[(i + 1) % t.Nodes.Length]);
        }
        foreach (var t in Fe.SimdTris ?? Array.Empty<FeModel.FeSimdTri>())
        {
            if (t.Nodes == null || t.Nodes.Length == 0) continue;
            for (int lane = 0; lane < t.Nodes[0].Length; lane++)
            {
                TriCount++;
                for (int c = 0; c < t.Nodes.Length; c++) Add(t.Nodes[c][lane], t.Nodes[(c + 1) % t.Nodes.Length][lane]);
            }
        }
        foreach (var s in Fe.SpringIntegrator ?? Array.Empty<FeModel.FeSpringIntegrator>()) { SpringCount++; if (s.Nodes?.Length >= 2) Add(s.Nodes[0], s.Nodes[1]); }
        foreach (var s in Fe.SimdSpringIntegrator ?? Array.Empty<FeModel.FeSimdSpringIntegrator>())
        {
            if (s.Nodes == null || s.Nodes.Length < 2) continue;
            for (int lane = 0; lane < s.Nodes[0].Length; lane++) { SpringCount++; Add(s.Nodes[0][lane], s.Nodes[1][lane]); }
        }
        // Tree parents as a last-resort connectivity hint (jiggle-style chains without rods).
        if (Fe.TreeParents != null && _edges.Count == 0)
            for (int i = 0; i < Fe.TreeParents.Length && i < Fe.NodeCount; i++) Add(i, Fe.TreeParents[i]);
    }

    string NameComponent(Component c)
    {
        var names = c.Nodes.Select(Name).Where(s => !string.IsNullOrEmpty(s) && !s.StartsWith('#')).ToList();
        if (names.Count == 0) return $"piece{c.Index}";
        string Token(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s) { if (char.IsDigit(ch)) break; sb.Append(ch); }
            return sb.ToString().TrimEnd('_', '.', ' ', '$');
        }
        var top = names.GroupBy(Token).OrderByDescending(g => g.Count()).First().Key;
        string anchors = string.Join(",", c.Anchors.Select(Name).Distinct().Take(3));
        return $"{(string.IsNullOrEmpty(top) ? "piece" : top)} (anchors: {(anchors.Length > 0 ? anchors : "none")})";
    }
}

static class Report
{
    static string F(float v, string fmt = "F3") => float.IsFinite(v) ? v.ToString(fmt) : "–";

    static string Scalar(object v) => v switch
    {
        null => "null",
        float f => F(f, "G5"),
        double d => d.ToString("G5"),
        uint u => $"{u} (0x{u:X})",
        _ => v.ToString()
    };

    public static string Build(List<LoadedModel> models, string outDir)
    {
        var analyses = models.Select(m => new FeAnalysis(m.Fe)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# FeModel Comparison Report");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `tools/fe_model_dump`. Units: Source inches, gravity in in/s².");
        sb.AppendLine();
        sb.AppendLine("| Label | VPK | Model |");
        sb.AppendLine("|---|---|---|");
        foreach (var m in models) sb.AppendLine($"| **{m.Label}** | `{m.Vpk}` | `{m.Entry}` |");
        sb.AppendLine();

        // 1. Global scalar parameters (reflected — every scalar on FeModel)
        sb.AppendLine("## 1. Global FeModel parameters (ClothParams → compiled)");
        sb.AppendLine();
        sb.AppendLine("Rows marked ⚠ differ between models.");
        sb.AppendLine();
        sb.Append("| Field |"); foreach (var m in models) sb.Append($" {m.Label} |"); sb.AppendLine();
        sb.Append("|---|"); foreach (var _ in models) sb.Append("---|"); sb.AppendLine();
        var props = typeof(FeModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => IsScalar(p.PropertyType)).OrderBy(p => p.Name).ToList();
        foreach (var p in props)
        {
            var vals = models.Select(m => Scalar(p.GetValue(m.Fe))).ToList();
            bool diff = vals.Distinct().Count() > 1;
            sb.Append($"| {(diff ? "⚠ " : "")}`{p.Name}` |");
            foreach (var v in vals) sb.Append($" {v} |");
            sb.AppendLine();
        }
        sb.AppendLine();

        // 2. Array sizes (topology / feature usage)
        sb.AppendLine("## 2. Feature usage (array lengths)");
        sb.AppendLine();
        sb.Append("| Array |"); foreach (var m in models) sb.Append($" {m.Label} |"); sb.AppendLine();
        sb.Append("|---|"); foreach (var _ in models) sb.Append("---|"); sb.AppendLine();
        var arrProps = typeof(FeModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsArray).OrderBy(p => p.Name).ToList();
        foreach (var p in arrProps)
        {
            var lens = models.Select(m => (p.GetValue(m.Fe) as Array)?.Length ?? 0).ToList();
            if (lens.All(l => l == 0)) continue;
            bool zeroVsNonZero = lens.Any(l => l == 0) && lens.Any(l => l > 0);
            sb.Append($"| {(zeroVsNonZero ? "⚠ " : "")}`{p.Name}` |");
            foreach (var l in lens) sb.Append($" {l} |");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("⚠ = used by one model but absent in another (e.g. `CollisionPlanes`, `AnimStrayRadii`, `SphereRigids`).");
        sb.AppendLine();

        // 3. Per-model detail
        for (int mi = 0; mi < models.Count; mi++)
        {
            var m = models[mi];
            var an = analyses[mi];
            var fe = m.Fe;
            sb.AppendLine($"## 3.{mi + 1} `{m.Label}` — {m.Entry}");
            sb.AppendLine();
            sb.AppendLine($"Nodes {fe.NodeCount} (static {fe.StaticNodes}, dynamic {fe.NodeCount - fe.StaticNodes}), rods {an.RodCount}, quads {an.QuadCount}, tris {an.TriCount}, springs {an.SpringCount}, cloth pieces {an.Components.Count}.");
            sb.AppendLine();

            // Pieces
            sb.AppendLine("### Cloth pieces (connected dynamic node groups)");
            sb.AppendLine();
            sb.AppendLine("| # | Piece | Dyn nodes | Anchors | Length (in) | Bounds size (in) |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var c in an.Components.OrderByDescending(c => c.Nodes.Count))
            {
                var size = c.Max - c.Min;
                sb.AppendLine($"| {c.Index} | {c.Name} | {c.Nodes.Count} | {c.Anchors.Count} | {F(c.MaxDist, "F1")} | {F(size.X, "F1")} × {F(size.Y, "F1")} × {F(size.Z, "F1")} |");
            }
            sb.AppendLine();

            // Per-piece binned node parameters (root → tip)
            sb.AppendLine("### Per-node parameters, root → tip (median per 20% band of distance-to-anchor)");
            sb.AppendLine();
            sb.AppendLine("`goalF` = AnimationForceAttraction, `goalV` = AnimationVertexAttraction, `damp` = PointDamping, `grav` = Gravity, `colR` = NodeCollisionRadii, `fric` = DynNodeFriction, `invM` = NodeInvMasses.");
            sb.AppendLine();
            foreach (var c in an.Components.OrderByDescending(c => c.Nodes.Count).Take(12))
            {
                sb.AppendLine($"**Piece {c.Index}: {c.Name}** ({c.Nodes.Count} nodes, {F(c.MaxDist, "F1")} in)");
                sb.AppendLine();
                sb.AppendLine("| Band | n | goalF | goalV | damp | grav | colR | fric | invM |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
                for (int b = 0; b < 5; b++)
                {
                    float lo = b * 0.2f, hi = (b + 1) * 0.2f;
                    var ns = c.Nodes.Where(nd => an.TNorm[nd] >= lo && (an.TNorm[nd] < hi || (b == 4 && an.TNorm[nd] <= 1.0001f))).ToList();
                    if (ns.Count == 0) { sb.AppendLine($"| {lo:P0}–{hi:P0} | 0 | | | | | | | |"); continue; }
                    float Med(Func<int, float> f) => Stats.Median(ns.Select(f));
                    sb.AppendLine($"| {lo:P0}–{hi:P0} | {ns.Count} | {F(Med(nd => an.Integrator(nd)?.AnimationForceAttraction ?? float.NaN))} | " +
                                  $"{F(Med(nd => an.Integrator(nd)?.AnimationVertexAttraction ?? float.NaN))} | {F(Med(nd => an.Integrator(nd)?.PointDamping ?? float.NaN))} | " +
                                  $"{F(Med(nd => an.Integrator(nd)?.Gravity ?? float.NaN), "F1")} | {F(Med(nd => an.DynIndexed(fe.NodeCollisionRadii, nd)), "F2")} | " +
                                  $"{F(Med(nd => an.DynIndexed(fe.DynNodeFriction, nd)), "F2")} | {F(Med(nd => an.DynIndexed(fe.NodeInvMasses, nd)), "F3")} |");
                }
                sb.AppendLine();
            }

            // Rods
            var rodRelax = new List<float>(); var rodW = new List<float>(); var rodSlack = new List<float>();
            foreach (var r in fe.Rods ?? Array.Empty<FeModel.FeRodConstraint>())
            { rodRelax.Add(r.RelaxationFactor); rodW.Add(r.Weight0); if (r.MaxDist > 1e-4f) rodSlack.Add(r.MinDist / r.MaxDist); }
            foreach (var r in fe.SimdRods ?? Array.Empty<FeModel.FeSimdRodConstraint>())
                for (int l = 0; l < (r.MaxDist?.Length ?? 0); l++)
                { rodRelax.Add(r.RelaxationFactor[l]); rodW.Add(r.Weight0[l]); if (r.MaxDist[l] > 1e-4f) rodSlack.Add(r.MinDist[l] / r.MaxDist[l]); }
            sb.AppendLine("### Rods (min / median / max)");
            sb.AppendLine();
            sb.AppendLine($"- RelaxationFactor: {Stats.Mmm(rodRelax)}");
            sb.AppendLine($"- Weight0: {Stats.Mmm(rodW)}");
            sb.AppendLine($"- MinDist/MaxDist (compressibility, 1 = rigid): {Stats.Mmm(rodSlack)}");
            sb.AppendLine();

            // Colliders
            sb.AppendLine("### Colliders");
            sb.AppendLine();
            sb.AppendLine("| Type | Bone/node | r0 | r1 | Length | CollisionMask | Flags | Stickiness |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var c in fe.TaperedCapsuleRigids ?? Array.Empty<FeModel.FeTaperedCapsuleRigid>())
            {
                var s0 = c.Spheres[0]; var s1 = c.Spheres[1];
                float len = Vector3.Distance(new Vector3(s0.X, s0.Y, s0.Z), new Vector3(s1.X, s1.Y, s1.Z));
                sb.AppendLine($"| TaperedCapsuleRigid | {an.Name(c.Node)} | {F(s0.W, "F2")} | {F(s1.W, "F2")} | {F(len, "F1")} | 0x{c.CollisionMask:X} | 0x{c.Flags:X} | {c.Stickiness?.ToString() ?? "–"} |");
            }
            foreach (var c in fe.SphereRigids ?? Array.Empty<FeModel.FeSphereRigid>())
                sb.AppendLine($"| SphereRigid | {an.Name(c.Node)} | {F(c.Sphere?.W ?? float.NaN, "F2")} | | | 0x{c.CollisionMask:X} | 0x{c.Flags:X} | {c.Stickiness?.ToString() ?? "–"} |");
            foreach (var c in fe.BoxRigids ?? Array.Empty<FeModel.FeBoxRigid>())
                sb.AppendLine($"| BoxRigid | {an.Name(c.Node)} | size {c.Size} | | | 0x{c.CollisionMask:X} | 0x{c.Flags:X} | {c.Stickiness?.ToString() ?? "–"} |");
            foreach (var c in fe.CollisionPlanes ?? Array.Empty<FeModel.FeCollisionPlane>())
                sb.AppendLine($"| CollisionPlane | parent {an.Name(c.CtrlParent)} → child {an.Name(c.ChildNode)} | plane {c.Plane} | | | | strength {F(c.Strength)} | {F(c.Stickiness)} |");
            foreach (var c in fe.TaperedCapsuleStretches ?? Array.Empty<FeModel.FeTaperedCapsuleStretch>())
                sb.AppendLine($"| TaperedCapsuleStretch | {string.Join("→", (c.Nodes ?? Array.Empty<int>()).Select(an.Name))} | {F(c.Radii?.ElementAtOrDefault(0) ?? float.NaN, "F2")} | {F(c.Radii?.ElementAtOrDefault(1) ?? float.NaN, "F2")} | | 0x{c.CollisionMask:X} | | |");
            sb.AppendLine();
            if (fe.TreeCollisionMasks?.Length > 0)
            {
                var hist = fe.TreeCollisionMasks.GroupBy(x => x).OrderByDescending(g => g.Count()).Take(8).Select(g => $"0x{g.Key:X}×{g.Count()}");
                sb.AppendLine($"TreeCollisionMasks histogram: {string.Join(", ", hist)}");
                sb.AppendLine();
            }
            if (fe.RigidColliderPriorities?.Length > 0)
            {
                sb.AppendLine($"RigidColliderPriorities: {fe.RigidColliderPriorities.Length} entries");
                sb.AppendLine();
            }

            // Jiggle bones
            if (fe.JiggleBones?.Length > 0)
            {
                sb.AppendLine("### Jiggle bones");
                sb.AppendLine();
                sb.AppendLine("| Node | Flags | Length | TipMass | Yaw k/d | Pitch k/d | Along k/d | AngleLimit | CollisionMask |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
                foreach (var j in fe.JiggleBones)
                {
                    var b = j.JiggleBone;
                    sb.AppendLine($"| {an.Name(j.Node)} | {b.BoneFlags} | {F(b.Length, "F2")} | {F(b.TipMass, "F2")} | {F(b.YawStiffness, "F1")}/{F(b.YawDamping, "F2")} | {F(b.PitchStiffness, "F1")}/{F(b.PitchDamping, "F2")} | {F(b.AlongStiffness, "F1")}/{F(b.AlongDamping, "F2")} | {F(b.AngleLimit, "F1")} | 0x{b.CollisionMask:X} |");
                }
                sb.AppendLine();
            }

            WriteNodeCsv(Path.Combine(outDir, $"nodes_{Sanitize(m.Label)}.csv"), m, an);
            sb.AppendLine($"Per-node CSV: `nodes_{Sanitize(m.Label)}.csv`");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static bool IsScalar(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u.IsPrimitive || u.IsEnum;
    }

    static string Sanitize(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_'));

    static void WriteNodeCsv(string path, LoadedModel m, FeAnalysis an)
    {
        var fe = m.Fe;
        var sb = new StringBuilder();
        sb.AppendLine("node,name,static,piece,pos_x,pos_y,pos_z,dist_to_anchor_in,t_norm,inv_mass,col_radius,friction,point_damping,goal_force,goal_vertex,gravity");
        for (int i = 0; i < fe.NodeCount; i++)
        {
            var p = an.Pos(i);
            var integ = an.Integrator(i);
            bool isStatic = i < fe.StaticNodes;
            sb.Append(i).Append(',').Append('"').Append(an.Name(i).Replace("\"", "'")).Append('"').Append(',')
              .Append(isStatic ? 1 : 0).Append(',').Append(an.ComponentOf[i]).Append(',')
              .Append(F(p.X, "F3")).Append(',').Append(F(p.Y, "F3")).Append(',').Append(F(p.Z, "F3")).Append(',')
              .Append(F(an.DistToAnchor[i], "F3")).Append(',').Append(F(an.TNorm[i], "F3")).Append(',')
              .Append(F(an.DynIndexed(fe.NodeInvMasses, i), "F4")).Append(',')
              .Append(isStatic ? "" : F(an.DynIndexed(fe.NodeCollisionRadii, i), "F3")).Append(',')
              .Append(isStatic ? "" : F(an.DynIndexed(fe.DynNodeFriction, i), "F3")).Append(',')
              .Append(F(integ?.PointDamping ?? float.NaN, "F4")).Append(',')
              .Append(F(integ?.AnimationForceAttraction ?? float.NaN, "F4")).Append(',')
              .Append(F(integ?.AnimationVertexAttraction ?? float.NaN, "F4")).Append(',')
              .Append(F(integ?.Gravity ?? float.NaN, "F2")).AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }
}
