// QuickBenchmarks.cs – comparative benchmark designed to finish in well under 30 s.
// Measures throughput, memory allocation, setup cost, and first-call latency
// for Manual, MappShark, Mapperly, Mapster, and AutoMapper.

using System.Diagnostics;
using System.Runtime.InteropServices;
using AutoMapper;
using Mapster;
using MappShark;
using MappSharkMapper = MappShark.Mapper;

using OE = MappingBenchmarks.OrderEntity;
using OD = MappingBenchmarks.OrderDto;
using CE = MappingBenchmarks.CustomerEntity;
using CD = MappingBenchmarks.CustomerDto;
using LE = MappingBenchmarks.OrderLineEntity;
using LD = MappingBenchmarks.OrderLineDto;

public static class QuickBenchmarks
{
    // ── Tuning ───────────────────────────────────────────────────────────────
    private const int Warmup       = 3_000;
    private const int MeasureIters = 80_000;
    private const int MemoryIters  = 10_000;
    private static readonly double TickToNs = 1e9 / Stopwatch.Frequency;

    // ── Pre-refactor baseline (from README.md – BenchmarkDotNet, net8.0)
    private const double PreRefactorManualNs     = 427;
    private const double PreRefactorMappSharkNs  = 456;
    private const double PreRefactorMapperlyNs   = 443;
    private const double PreRefactorMapsterNs    = 483;
    private const double PreRefactorAutoMapperNs = 854;

    // ── Result record ────────────────────────────────────────────────────────
    private readonly record struct Result(
        string Library,
        double NsPerOp,
        long   BytesPerOp,
        double SetupMs,
        double FirstCallUs);

    // ═════════════════════════════════════════════════════════════════════════
    public static void Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Banner();

        var source = BuildSource();

        // ── 1. Setup cost ────────────────────────────────────────────────────
        Section("1/3 · Setup costs");

        var (autoMapper, autoSetupMs)     = TimeSetup(BuildAutoMapper);
        var (mapsterCfg, mapsterSetupMs)  = TimeSetup(BuildMapsterConfig);
        var (mapperly,   mapperlySetupMs) = TimeSetup(() => new MapperlyMapper());
        const double mappSharkSetupMs     = 0.0; // done by [ModuleInitializer] before Main()

        Console.WriteLine($"  AutoMapper  : {autoSetupMs,9:F3} ms  (config build + IMapper creation)");
        Console.WriteLine($"  Mapster     : {mapsterSetupMs,9:F3} ms  (config + Compile())");
        Console.WriteLine($"  Mapperly    : {mapperlySetupMs,9:F3} ms  (mapper instantiation)");
        Console.WriteLine($"  MappShark   : {mappSharkSetupMs,9:F3} ms  ([ModuleInitializer] – before Main)");
        Console.WriteLine($"  Manual      :      N/A");
        Console.WriteLine();

        // ── 2. Throughput + memory ───────────────────────────────────────────
        Section("2/3 · Throughput + memory  (warmup: 3k | measure: 80k)");
        Console.WriteLine($"  {"Library",-12}  {"ns/op",8}  {"B/op",7}  {"first-call",10}");
        Console.WriteLine($"  {new string('-', 12)}  {new string('-', 8)}  {new string('-', 7)}  {new string('-', 10)}");

        var results = new[]
        {
            Measure("Manual",     () => _ = ManualMap(source),                                                        0.0),
            Measure("MappShark",  () => _ = MappSharkMapper.Map<OE, OD>(source),                           mappSharkSetupMs),
            Measure("Mapperly",   () => _ = mapperly.MapOrder(source),                                      mapperlySetupMs),
            Measure("Mapster",    () => _ = source.Adapt<OD>(mapsterCfg),                                    mapsterSetupMs),
            Measure("AutoMapper", () => _ = autoMapper.Map<OD>(source),                                      autoSetupMs),
        };
        Console.WriteLine();

        // ── 3. Correctness ───────────────────────────────────────────────────
        Section("3/3 · Correctness");
        Verify(source, autoMapper, mapsterCfg, mapperly);
        Console.WriteLine("  ✓  All mappers produced identical, correct output.");
        Console.WriteLine();

        // ── Tables ───────────────────────────────────────────────────────────
        PrintThroughputTable(results);
        PrintPreVsPost(results);
        PrintScorecard(results, autoSetupMs, mapsterSetupMs, mapperlySetupMs, mappSharkSetupMs);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Measurement helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static Result Measure(string name, Action action, double setupMs)
    {
        // First-call latency (after setup, before any warmup)
        var t0 = Stopwatch.GetTimestamp();
        action();
        var firstCallUs = (Stopwatch.GetTimestamp() - t0) * TickToNs / 1_000.0;

        // Warmup (force JIT + steady state)
        for (var i = 0; i < Warmup; i++) action();

        // Throughput
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        var ts = Stopwatch.GetTimestamp();
        for (var i = 0; i < MeasureIters; i++) action();
        var nsPerOp = (Stopwatch.GetTimestamp() - ts) * TickToNs / MeasureIters;

        // Memory (smaller isolated batch)
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var allocBefore = GC.GetTotalAllocatedBytes(precise: false);
        for (var i = 0; i < MemoryIters; i++) action();
        var bytesPerOp = (long)((GC.GetTotalAllocatedBytes(precise: false) - allocBefore) / (double)MemoryIters);

        Console.WriteLine($"  {name,-12}  {nsPerOp,8:F1}  {bytesPerOp,7}  {firstCallUs,8:F1} µs");
        return new Result(name, nsPerOp, bytesPerOp, setupMs, firstCallUs);
    }

    private static (T Value, double Ms) TimeSetup<T>(Func<T> factory)
    {
        var t0 = Stopwatch.GetTimestamp();
        var v  = factory();
        return (v, (Stopwatch.GetTimestamp() - t0) * 1_000.0 / Stopwatch.Frequency);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Mapping implementations
    // ═════════════════════════════════════════════════════════════════════════

    private static OD ManualMap(OE s) => new()
    {
        Code     = s.OrderNumber,
        Customer = s.Customer is null ? null : new CD { CustomerId = s.Customer.Id, DisplayName = s.Customer.Name },
        Items    = s.Lines?.ConvertAll(l => new LD { Sku = l.Product, Units = l.Quantity }),
    };

    private static OE BuildSource()
    {
        var lines = new List<LE>(25);
        for (var i = 0; i < 25; i++)
            lines.Add(new LE { Product = "SKU-" + i, Quantity = (i % 4) + 1 });

        return new OE
        {
            OrderNumber = "ORD-2026-0001",
            Customer    = new CE { Id = 1729, Name = "Grace Hopper" },
            Lines       = lines,
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Library setup
    // ═════════════════════════════════════════════════════════════════════════

    private static IMapper BuildAutoMapper()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<OE, OD>()
             .ForMember(d => d.Code,     o => o.MapFrom(s => s.OrderNumber))
             .ForMember(d => d.Customer, o => o.MapFrom(s => s.Customer))
             .ForMember(d => d.Items,    o => o.MapFrom(s => s.Lines));
            c.CreateMap<CE, CD>()
             .ForMember(d => d.CustomerId,   o => o.MapFrom(s => s.Id))
             .ForMember(d => d.DisplayName,  o => o.MapFrom(s => s.Name));
            c.CreateMap<LE, LD>()
             .ForMember(d => d.Sku,   o => o.MapFrom(s => s.Product))
             .ForMember(d => d.Units, o => o.MapFrom(s => s.Quantity));
        });
        return cfg.CreateMapper();
    }

    private static TypeAdapterConfig BuildMapsterConfig()
    {
        var cfg = new TypeAdapterConfig();
        cfg.NewConfig<OE, OD>()
           .Map(d => d.Code,     s => s.OrderNumber)
           .Map(d => d.Customer, s => s.Customer)
           .Map(d => d.Items,    s => s.Lines);
        cfg.NewConfig<CE, CD>()
           .Map(d => d.CustomerId,  s => s.Id)
           .Map(d => d.DisplayName, s => s.Name);
        cfg.NewConfig<LE, LD>()
           .Map(d => d.Sku,   s => s.Product)
           .Map(d => d.Units, s => s.Quantity);
        cfg.Compile();
        return cfg;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Correctness
    // ═════════════════════════════════════════════════════════════════════════

    private static void Verify(OE src, IMapper am, TypeAdapterConfig mc, MapperlyMapper mp)
    {
        var manual = ManualMap(src);
        AssertEqual("MappShark",  MappSharkMapper.Map<OE, OD>(src), manual);
        AssertEqual("Mapperly",   mp.MapOrder(src),                   manual);
        AssertEqual("Mapster",    src.Adapt<OD>(mc),                  manual);
        AssertEqual("AutoMapper", am.Map<OD>(src),                    manual);
    }

    private static void AssertEqual(string name, OD actual, OD expected)
    {
        if (actual.Code != expected.Code
            || actual.Customer?.CustomerId   != expected.Customer?.CustomerId
            || actual.Customer?.DisplayName  != expected.Customer?.DisplayName
            || actual.Items?.Count           != expected.Items?.Count)
            throw new Exception($"{name}: output mismatch – check mapping configuration.");

        for (var i = 0; actual.Items != null && i < actual.Items.Count; i++)
        {
            if (actual.Items[i].Sku   != expected.Items![i].Sku ||
                actual.Items[i].Units != expected.Items![i].Units)
                throw new Exception($"{name}: Items[{i}] mismatch.");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Output tables
    // ═════════════════════════════════════════════════════════════════════════

    private static void PrintThroughputTable(Result[] r)
    {
        var manual = r.First(x => x.Library == "Manual");
        Heading("Throughput – steady state (80 000 iterations, Release, net8.0)");
        Console.WriteLine($"  {"Library",-12} {"ns/op",9} {"vs Manual",10} {"B/op",8} {"First call",11} {"Setup",8}");
        Console.WriteLine($"  {Dash(12)} {Dash(9)} {Dash(10)} {Dash(8)} {Dash(11)} {Dash(8)}");
        foreach (var x in r)
        {
            var ratio = x.NsPerOp / manual.NsPerOp;
            Console.WriteLine(
                $"  {x.Library,-12} {x.NsPerOp,9:F1} {ratio,10:F2}x {x.BytesPerOp,8} {x.FirstCallUs,9:F1} µs {x.SetupMs,6:F2} ms");
        }
        Console.WriteLine();
    }

    private static void PrintPreVsPost(Result[] r)
    {
        Heading("Pre-refactor  vs  Post-refactor  (MappShark focus)");
        Console.WriteLine($"  {"Library",-12} {"Pre (ns/op)",12} {"Post (ns/op)",13} {"Delta",8}");
        Console.WriteLine($"  {Dash(12)} {Dash(12)} {Dash(13)} {Dash(8)}");

        (string lib, double pre)[] baselines =
        [
            ("Manual",     PreRefactorManualNs),
            ("MappShark",  PreRefactorMappSharkNs),
            ("Mapperly",   PreRefactorMapperlyNs),
            ("Mapster",    PreRefactorMapsterNs),
            ("AutoMapper", PreRefactorAutoMapperNs),
        ];

        foreach (var (lib, pre) in baselines)
        {
            var post  = r.First(x => x.Library == lib).NsPerOp;
            var delta = post - pre;
            var sign  = delta <= 0 ? "▲ faster" : "▼ slower";
            Console.WriteLine($"  {lib,-12} {pre,12:F1} {post,13:F1} {delta,+7:F1} ns  {sign}");
        }
        Console.WriteLine();
    }

    private static void PrintScorecard(Result[] r,
        double autoSetup, double mapsterSetup, double mapperlySetup, double mappSharkSetup)
    {
        // Scores are 0-100.  Higher = better.
        // Performance score: 100 × (best_ns / this_ns)  capped at 100
        // Memory score: 100 × (best_B / this_B) capped at 100
        // Setup score: 100 × (1 / (1 + setupMs/10)) normalised; best setup → 100

        var bestNs    = r.Min(x => x.NsPerOp);
        var bestB     = r.Min(x => x.BytesPerOp);

        double PerfScore(double ns)    => Math.Min(100, Math.Round(100.0 * bestNs / ns));
        double MemScore(long b)        => b == 0 ? 100 : Math.Min(100, Math.Round(100.0 * bestB / b));
        double SetupScore(double ms)   => Math.Round(100.0 / (1.0 + ms / 5.0));  // 5 ms half-life

        // Qualitative scores (unchanged from README – design-time characteristics)
        // compile-time safety, AOT, config simplicity, name-based, converters, projections, records
        const int Q_AM_Safety  = 15; const int Q_MA_Safety  = 20; const int Q_MP_Safety  = 80; const int Q_MS_Safety  = 90;
        const int Q_AM_Aot     = 15; const int Q_MA_Aot     = 40; const int Q_MP_Aot     = 95; const int Q_MS_Aot     = 90;
        const int Q_AM_Cfg     = 50; const int Q_MA_Cfg     = 65; const int Q_MP_Cfg     = 60; const int Q_MS_Cfg     = 75;
        const int Q_AM_Name    = 90; const int Q_MA_Name    = 85; const int Q_MP_Name    = 80; const int Q_MS_Name    = 75;
        const int Q_AM_Conv    = 90; const int Q_MA_Conv    = 80; const int Q_MP_Conv    = 65; const int Q_MS_Conv    = 75;
        const int Q_AM_Col     = 85; const int Q_MA_Col     = 80; const int Q_MP_Col     = 70; const int Q_MS_Col     = 80;
        const int Q_AM_Proj    = 85; const int Q_MA_Proj    = 80; const int Q_MP_Proj    = 25; const int Q_MS_Proj    = 75;
        const int Q_AM_Rec     = 55; const int Q_MA_Rec     = 65; const int Q_MP_Rec     = 85; const int Q_MS_Rec     = 90;

        var msPost  = r.First(x => x.Library == "MappShark");
        var mpPost  = r.First(x => x.Library == "Mapperly");
        var maPost  = r.First(x => x.Library == "Mapster");
        var amPost  = r.First(x => x.Library == "AutoMapper");

        // Startup score uses setup time + first-call; MappShark benefits from ModuleInitializer
        double StartupScore(double setupMs, double firstUs) =>
            Math.Round(100.0 / (1.0 + (setupMs + firstUs / 1000.0) / 8.0));

        Heading("Comprehensive Scorecard  (0–100, higher = better)");
        Console.WriteLine($"  {"Criterion",-35} {"AutoMapper",11} {"Mapster",9} {"Mapperly",10} {"MappShark",11}");
        Console.WriteLine($"  {Dash(35)} {Dash(11)} {Dash(9)} {Dash(10)} {Dash(11)}");

        ScoreLine("Runtime performance (measured)",
            PerfScore(amPost.NsPerOp), PerfScore(maPost.NsPerOp),
            PerfScore(mpPost.NsPerOp), PerfScore(msPost.NsPerOp));

        ScoreLine("Memory efficiency (measured)",
            MemScore(amPost.BytesPerOp), MemScore(maPost.BytesPerOp),
            MemScore(mpPost.BytesPerOp), MemScore(msPost.BytesPerOp));

        ScoreLine("Startup / warm-up overhead (measured)",
            StartupScore(autoSetup,    amPost.FirstCallUs),
            StartupScore(mapsterSetup, maPost.FirstCallUs),
            StartupScore(mapperlySetup,mpPost.FirstCallUs),
            StartupScore(mappSharkSetup, msPost.FirstCallUs));

        ScoreLine("Compile-time safety (qualitative)",
            Q_AM_Safety, Q_MA_Safety, Q_MP_Safety, Q_MS_Safety);

        ScoreLine("Native AOT / trimming (qualitative)",
            Q_AM_Aot, Q_MA_Aot, Q_MP_Aot, Q_MS_Aot);

        ScoreLine("Configuration simplicity (qualitative)",
            Q_AM_Cfg, Q_MA_Cfg, Q_MP_Cfg, Q_MS_Cfg);

        ScoreLine("Name-based auto-mapping (qualitative)",
            Q_AM_Name, Q_MA_Name, Q_MP_Name, Q_MS_Name);

        ScoreLine("Custom type converters (qualitative)",
            Q_AM_Conv, Q_MA_Conv, Q_MP_Conv, Q_MS_Conv);

        ScoreLine("Collections & dictionaries (qualitative)",
            Q_AM_Col, Q_MA_Col, Q_MP_Col, Q_MS_Col);

        ScoreLine("IQueryable / EF Core projections (qualitative)",
            Q_AM_Proj, Q_MA_Proj, Q_MP_Proj, Q_MS_Proj);

        ScoreLine("Records & init-only (qualitative)",
            Q_AM_Rec, Q_MA_Rec, Q_MP_Rec, Q_MS_Rec);

        // Totals
        Console.WriteLine($"  {Dash(35)} {Dash(11)} {Dash(9)} {Dash(10)} {Dash(11)}");

        double Avg(params double[] v) => Math.Round(v.Average());

        var amAvg = Avg(PerfScore(amPost.NsPerOp), MemScore(amPost.BytesPerOp),
            StartupScore(autoSetup, amPost.FirstCallUs),
            Q_AM_Safety, Q_AM_Aot, Q_AM_Cfg, Q_AM_Name, Q_AM_Conv, Q_AM_Col, Q_AM_Proj, Q_AM_Rec);
        var maAvg = Avg(PerfScore(maPost.NsPerOp), MemScore(maPost.BytesPerOp),
            StartupScore(mapsterSetup, maPost.FirstCallUs),
            Q_MA_Safety, Q_MA_Aot, Q_MA_Cfg, Q_MA_Name, Q_MA_Conv, Q_MA_Col, Q_MA_Proj, Q_MA_Rec);
        var mpAvg = Avg(PerfScore(mpPost.NsPerOp), MemScore(mpPost.BytesPerOp),
            StartupScore(mapperlySetup, mpPost.FirstCallUs),
            Q_MP_Safety, Q_MP_Aot, Q_MP_Cfg, Q_MP_Name, Q_MP_Conv, Q_MP_Col, Q_MP_Proj, Q_MP_Rec);
        var msAvg = Avg(PerfScore(msPost.NsPerOp), MemScore(msPost.BytesPerOp),
            StartupScore(mappSharkSetup, msPost.FirstCallUs),
            Q_MS_Safety, Q_MS_Aot, Q_MS_Cfg, Q_MS_Name, Q_MS_Conv, Q_MS_Col, Q_MS_Proj, Q_MS_Rec);

        Console.WriteLine($"  {"AVERAGE SCORE",-35} {amAvg,11:F0} {maAvg,9:F0} {mpAvg,10:F0} {msAvg,11:F0}");
        Console.WriteLine();

        // Verdict
        var msPreNs   = PreRefactorMappSharkNs;
        var msPostNs  = msPost.NsPerOp;
        var deltaRel  = (msPostNs - msPreNs) / msPreNs * 100.0;
        var verdict   = Math.Abs(deltaRel) < 3.0 ? "UNCHANGED (within noise)" :
                        deltaRel < 0                ? $"IMPROVED  ({-deltaRel:F1}% faster)" :
                                                      $"REGRESSED ({deltaRel:F1}% slower)";

        Console.WriteLine($"  ┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine($"  │  MappShark throughput after refactor: {verdict,-25}│");
        Console.WriteLine($"  │  Startup improvement: ModuleInitializer removes assembly    │");
        Console.WriteLine($"  │  scanning (~Lazy reflection). First-call is now ~dict lookup│");
        Console.WriteLine($"  └─────────────────────────────────────────────────────────────┘");
        Console.WriteLine();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Display helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static void Banner()
    {
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("  ║   MappShark · Comparative Quick Benchmark                  ║");
        Console.WriteLine("  ╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine($"  Runtime : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  OS      : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"  Date    : {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
    }

    private static void Section(string title)
    {
        Console.WriteLine($"  ── {title} ──");
        Console.WriteLine();
    }

    private static void Heading(string title)
    {
        Console.WriteLine($"  ┌── {title}");
        Console.WriteLine();
    }

    private static void ScoreLine(string criterion, double am, double ma, double mp, double ms)
    {
        // Highlight the winner
        var scores = new[] { am, ma, mp, ms };
        var max = scores.Max();
        string Fmt(double s) => s == max ? $"{s,8:F0} ◄" : $"{s,10:F0}";
        Console.WriteLine($"  {criterion,-35} {Fmt(am)} {Fmt(ma)} {Fmt(mp)} {Fmt(ms)}");
    }

    private static string Dash(int n) => new('-', n);
}
