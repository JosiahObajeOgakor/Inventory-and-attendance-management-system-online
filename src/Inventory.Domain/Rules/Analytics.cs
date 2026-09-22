namespace Inventory.Domain.Rules;

/// <summary>
/// Ports of the desktop app's analytics (Forecasting.vb, DemandModel.vb, MarketBasket.vb, Learning.vb). Everything is deterministic:
/// the same history always gives the same numbers, so a flag or a forecast that changes between runs never has to be explained.
/// </summary>
public static class Forecasting
{
    public sealed record Forecast(double[] Values, string Basis);

    /// <summary>Holt-Winters triple exponential smoothing: a level, a trend and, when there are enough repeats, a seasonal pattern.</summary>
    public static Forecast HoltWinters(IReadOnlyList<double> series, int periodsAhead, int season = 0, double alpha = 0.4, double beta = 0.1, double gamma = 0.3)
    {
        if (series.Count == 0) return new(new double[Math.Max(0, periodsAhead)], "No sales history yet.");
        if (series.Count < 3) return new(Enumerable.Repeat(series.Average(), Math.Max(0, periodsAhead)).ToArray(), $"Only {series.Count} period(s) of history — this is their average, not a trend.");

        var useSeason = season >= 2 && series.Count >= season * 2;
        var level = series[0]; var trend = series[1] - series[0];
        var seasonal = SeasonalStart(series, season, useSeason);

        for (var i = 0; i < series.Count; i++)
        {
            var lastLevel = level; var idx = useSeason ? i % season : 0;
            var deseason = useSeason ? series[i] - seasonal[idx] : series[i];
            level = alpha * deseason + (1 - alpha) * (level + trend);
            trend = beta * (level - lastLevel) + (1 - beta) * trend;
            if (useSeason) seasonal[idx] = gamma * (series[i] - level) + (1 - gamma) * seasonal[idx];
        }

        var output = new double[Math.Max(0, periodsAhead)];
        for (var step = 1; step <= periodsAhead; step++)
        {
            var v = level + step * trend;
            if (useSeason) v += seasonal[(series.Count + step - 1) % season];
            output[step - 1] = Math.Max(0, v);   // demand is never negative
        }
        return new(output, useSeason ? $"{series.Count} periods of history, with a repeating {season}-period pattern."
                                     : $"{series.Count} periods of history, level and trend only — not enough repeats to read a season.");
    }

    private static double[] SeasonalStart(IReadOnlyList<double> series, int season, bool use)
    {
        if (!use) return new double[1];
        var avg = series.Average(); var s = new double[season];
        for (var k = 0; k < season; k++)
        {
            var same = Enumerable.Range(0, series.Count).Where(i => i % season == k).ToList();
            s[k] = same.Count > 0 ? same.Average(i => series[i]) - avg : 0;
        }
        return s;
    }

    /// <summary>The "I" in ARIMA: period-on-period change, which turns a trending series into a flat one.</summary>
    public static List<double> Difference(IReadOnlyList<double> series, int order = 1)
    {
        var cur = series.ToList();
        for (var pass = 0; pass < Math.Max(0, order); pass++)
        {
            if (cur.Count < 2) return [];
            cur = Enumerable.Range(1, cur.Count - 1).Select(i => cur[i] - cur[i - 1]).ToList();
        }
        return cur;
    }

    public static List<double> Undifference(IEnumerable<double> differences, double startValue)
    {
        var running = startValue; var rebuilt = new List<double>();
        foreach (var d in differences) { running += d; rebuilt.Add(running); }
        return rebuilt;
    }

    // ------------------------------------------------------------------ Isolation Forest
    private sealed class Node { public int Feature = -1; public double Split; public int Size; public Node? Left, Right; }

    /// <summary>A small seeded generator, so a given set of movements always scores the same.</summary>
    public sealed class SeededRandom(int seed)
    {
        private ulong _s = ((ulong)(uint)seed * 2654435761UL + 0x9E3779B97F4A7C15UL) | 1UL;
        public int NextInt(int n) { _s ^= _s << 13; _s ^= _s >> 7; _s ^= _s << 17; return (int)((_s >> 11) % (ulong)Math.Max(1, n)); }
    }

    public sealed class AnomalyModel
    {
        internal List<object> Trees = [];
        internal int SampleSize;

        /// <summary>0..1, higher is stranger. About 0.5 is ordinary; above roughly 0.62 a point was isolated much faster than the rest.</summary>
        public double Score(double[] point)
        {
            if (Trees.Count == 0) return 0;
            var avg = Trees.Average(t => Path((Node)t, point, 0));
            var expected = Expected(SampleSize);
            return expected <= 0 ? 0 : Math.Pow(2, -avg / expected);
        }

        private static double Path(Node? n, double[] p, int depth)
        {
            if (n is null) return depth;
            if (n.Feature < 0) return depth + Expected(n.Size);
            return p[n.Feature] < n.Split ? Path(n.Left, p, depth + 1) : Path(n.Right, p, depth + 1);
        }
    }

    private static double Expected(int n) => n <= 1 ? 0 : 2 * (Math.Log(n - 1) + 0.5772156649) - 2.0 * (n - 1) / n;

    public static AnomalyModel TrainIsolationForest(IReadOnlyList<double[]> rows, int treeCount = 50, int sampleSize = 64, int seed = 20260913)
    {
        var model = new AnomalyModel();
        if (rows.Count == 0) return model;
        model.SampleSize = Math.Min(sampleSize, rows.Count);
        var height = (int)Math.Ceiling(Math.Log2(Math.Max(2, model.SampleSize)));
        for (var t = 0; t < treeCount; t++)
        {
            var rng = new SeededRandom(seed + t);
            var sample = Enumerable.Range(0, model.SampleSize).Select(_ => rows[rng.NextInt(rows.Count)]).ToList();
            model.Trees.Add(Grow(sample, rng, 0, height));
        }
        return model;
    }

    private static Node Grow(List<double[]> rows, SeededRandom rng, int depth, int limit)
    {
        if (depth >= limit || rows.Count <= 1) return new Node { Size = rows.Count };
        var f = rng.NextInt(rows[0].Length);
        var lo = rows.Min(r => r[f]); var hi = rows.Max(r => r[f]);
        if (hi <= lo) return new Node { Size = rows.Count };
        var split = lo + (hi - lo) * (rng.NextInt(1000) + 1) / 1001.0;
        var left = rows.Where(r => r[f] < split).ToList(); var right = rows.Where(r => r[f] >= split).ToList();
        if (left.Count == 0 || right.Count == 0) return new Node { Size = rows.Count };
        return new Node { Feature = f, Split = split, Size = rows.Count, Left = Grow(left, rng, depth + 1, limit), Right = Grow(right, rng, depth + 1, limit) };
    }
}

/// <summary>Picks, per product, the method that has actually been most accurate on weeks it never saw (walk-forward), and says how far off it has been.</summary>
public static class DemandModel
{
    public const int Lags = 4, HoldoutWeeks = 3;

    public sealed record Evaluation(string Method, double MeanAbsoluteError, double RelativeError, double Predicted)
    {
        public string Confidence => double.IsNaN(RelativeError) ? "Unknown" : RelativeError <= 0.15 ? "High" : RelativeError <= 0.35 ? "Medium" : "Low";
        public string Summary => double.IsNaN(RelativeError) ? $"{Method}: not enough history to score"
            : $"{Method}: typically out by {MeanAbsoluteError:0.#} units ({RelativeError:P0}) — {Confidence.ToLowerInvariant()} confidence";
    }

    /// <summary>Scores every method on the last few weeks, best (smallest miss) first. Empty when the history is too short to score honestly.</summary>
    public static List<Evaluation> Evaluate(IReadOnlyList<double> series)
    {
        if (series.Count < Lags + HoldoutWeeks + 1) return [];
        var split = series.Count - HoldoutWeeks; var avg = series.Average();
        var methods = new (string Name, Func<List<double>, double> Predict)[]
        {
            ("Last week", h => h[^1]),
            ("Four-week average", h => h.Skip(Math.Max(0, h.Count - Lags)).Average()),
            ("Holt-Winters", h => Forecasting.HoltWinters(h, 1).Values[0]),
            ("ARIMA-style differencing", PredictDifferenced),
        };
        return methods.Select(m =>
        {
            var errors = Enumerable.Range(split, series.Count - split).Select(cut => Math.Abs(m.Predict(series.Take(cut).ToList()) - series[cut])).ToList();
            var mae = errors.Count > 0 ? errors.Average() : double.NaN;
            return new Evaluation(m.Name, mae, avg > 0 ? mae / avg : double.NaN, Math.Max(0, m.Predict(series.ToList())));
        }).OrderBy(e => e.MeanAbsoluteError).ThenBy(e => e.Method, StringComparer.Ordinal).ToList();
    }

    private static double PredictDifferenced(List<double> h)
    {
        if (h.Count < 3) return h[^1];
        var changes = Forecasting.Difference(h);
        if (changes.Count == 0) return h[^1];
        var next = Forecasting.HoltWinters(changes, 1).Values[0];
        if (next == 0) next = changes.Skip(Math.Max(0, changes.Count - 3)).Average();   // Holt-Winters floors at zero; a change can be negative
        return Math.Max(0, Forecasting.Undifference([next], h[^1])[0]);
    }
}

/// <summary>Which products go out of the door together. Deterministic Apriori; lift is the number that matters, confidence alone rediscovers your best seller.</summary>
public static class MarketBasket
{
    public sealed record Rule(int[] Antecedent, int Consequent, double Support, double Confidence, double Lift, int Baskets);

    private static string Key(IEnumerable<int> items) => string.Join(",", items.OrderBy(i => i));

    /// <summary>Frequent itemsets up to <paramref name="maxSize"/> items. At least two baskets always: one basket is an anecdote.</summary>
    public static Dictionary<string, int> Apriori(IReadOnlyList<int[]> baskets, double minSupport, int maxSize = 3)
    {
        var min = Math.Max(2, (int)Math.Ceiling(minSupport * baskets.Count));
        var sets = baskets.Select(b => new HashSet<int>(b)).ToList();
        var frequent = new Dictionary<string, int>();
        var current = sets.SelectMany(s => s).GroupBy(i => i).Where(g => g.Count() >= min).ToDictionary(g => Key([g.Key]), g => g.Count());
        var level = 1;
        while (current.Count > 0)
        {
            foreach (var kv in current) frequent[kv.Key] = kv.Value;
            if (++level > maxSize) break;
            var keys = current.Keys.Select(k => k.Split(',').Select(int.Parse).ToArray()).OrderBy(a => Key(a), StringComparer.Ordinal).ToList();
            var next = new Dictionary<string, int>();
            for (var a = 0; a < keys.Count; a++)
                for (var b = a + 1; b < keys.Count; b++)
                {
                    if (!keys[a].Take(level - 2).SequenceEqual(keys[b].Take(level - 2))) continue;
                    var cand = keys[a].Union(keys[b]).OrderBy(i => i).ToArray();
                    if (cand.Length != level) continue;
                    // prune: every subset one item smaller must itself be frequent
                    if (cand.Any(drop => !current.ContainsKey(Key(cand.Where(i => i != drop))))) continue;
                    var count = sets.Count(s => cand.All(s.Contains));
                    if (count >= min) next[Key(cand)] = count;
                }
            current = next;
        }
        return frequent;
    }

    /// <summary>"Buy A, expect B" rules with a single-product consequent, strongest lift first.</summary>
    public static List<Rule> RulesFrom(Dictionary<string, int> frequent, int basketCount, double minConfidence = 0.3)
    {
        var rules = new List<Rule>();
        if (basketCount == 0) return rules;
        foreach (var e in frequent.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var set = e.Key.Split(',').Select(int.Parse).ToArray();
            if (set.Length < 2) continue;
            foreach (var cons in set)
            {
                var ante = set.Where(i => i != cons).ToArray();
                if (!frequent.TryGetValue(Key(ante), out var aCount) || !frequent.TryGetValue(Key([cons]), out var cCount)) continue;
                var conf = e.Value / (double)aCount;
                if (conf < minConfidence) continue;
                var cSupport = cCount / (double)basketCount;
                rules.Add(new Rule(ante, cons, e.Value / (double)basketCount, conf, cSupport > 0 ? conf / cSupport : 0, e.Value));
            }
        }
        return rules.OrderByDescending(r => r.Lift).ThenByDescending(r => r.Confidence).ThenBy(r => r.Consequent).ToList();
    }
}

/// <summary>Groups customers by how they buy (orders, average order, total spend) so a clerk can see what kind of customer they are serving.</summary>
public static class Learning
{
    public static double[][] Normalise(IReadOnlyList<double[]> rows)
    {
        var w = rows[0].Length; var lo = new double[w]; var hi = new double[w];
        for (var j = 0; j < w; j++) { lo[j] = rows.Min(r => r[j]); hi[j] = rows.Max(r => r[j]); }
        return rows.Select(r => Enumerable.Range(0, w).Select(j => hi[j] > lo[j] ? (r[j] - lo[j]) / (hi[j] - lo[j]) : 0).ToArray()).ToArray();
    }

    public sealed record Cluster(double[] Centre, List<int> Members);

    /// <summary>Deterministic k-means: starts from customers spread evenly through the order of total spend, so no random seed is involved.</summary>
    public static List<Cluster> KMeans(double[][] points, int k, int maxIterations = 50)
    {
        k = Math.Min(k, points.Length);
        var order = Enumerable.Range(0, points.Length).OrderBy(i => points[i][^1]).ThenBy(i => i).ToArray();
        var centres = Enumerable.Range(0, k).Select(c => (double[])points[order[(int)((c + 0.5) * points.Length / k)]].Clone()).ToArray();
        var assign = new int[points.Length];
        for (var it = 0; it < maxIterations; it++)
        {
            var changed = false;
            for (var i = 0; i < points.Length; i++)
            {
                var best = Enumerable.Range(0, k).OrderBy(c => Dist(points[i], centres[c])).ThenBy(c => c).First();
                if (assign[i] != best) { assign[i] = best; changed = true; }
            }
            for (var c = 0; c < k; c++)
            {
                var mine = Enumerable.Range(0, points.Length).Where(i => assign[i] == c).ToList();
                if (mine.Count == 0) continue;
                for (var j = 0; j < centres[c].Length; j++) centres[c][j] = mine.Average(i => points[i][j]);
            }
            if (!changed && it > 0) break;
        }
        return Enumerable.Range(0, k).Select(c => new Cluster(centres[c], Enumerable.Range(0, points.Length).Where(i => assign[i] == c).ToList())).Where(c => c.Members.Count > 0).ToList();
    }

    private static double Dist(double[] a, double[] b) => a.Zip(b, (x, y) => (x - y) * (x - y)).Sum();
}
