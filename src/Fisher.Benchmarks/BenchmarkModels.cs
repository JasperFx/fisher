// The document, event and aggregate types every scenario shares. Deliberately small and boring:
// the harness measures Fisher's write/append/projection machinery, not serialization of a large
// document graph.

namespace Fisher.Benchmarks;

/// <summary>The document type the save and concurrency scenarios write.</summary>
public sealed class BenchDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Number { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public sealed record BenchCheckIn(int Value);

public sealed record BenchCheckOut(int Value);

/// <summary>
///     A self-aggregating snapshot for the daemon rebuild scenario, the same shape as the test
///     suite's <c>AsyncQuestTally</c>. Dispatch is source-generated (see the csproj's analyzer
///     reference); there is no runtime fallback.
/// </summary>
public sealed class BenchTally
{
    public Guid Id { get; set; }
    public int CheckIns { get; set; }
    public int CheckOuts { get; set; }
    public int Balance { get; set; }

    public void Apply(BenchCheckIn e)
    {
        CheckIns++;
        Balance += e.Value;
    }

    public void Apply(BenchCheckOut e)
    {
        CheckOuts++;
        Balance -= e.Value;
    }
}

// The cold-start scenario needs T *distinct* CLR document types, because the first-use table
// ensure runs once per type. 32 concrete classes rather than closed generics, so each gets an
// ordinary alias and an ordinary fi_doc_* table name.
public sealed class ColdDoc00 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc01 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc02 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc03 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc04 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc05 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc06 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc07 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc08 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc09 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc10 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc11 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc12 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc13 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc14 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc15 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc16 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc17 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc18 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc19 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc20 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc21 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc22 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc23 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc24 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc25 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc26 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc27 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc28 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc29 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc30 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }
public sealed class ColdDoc31 { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; }

public static class ColdDocs
{
    /// <summary>
    ///     One statically-typed writer per cold-start type, in a fixed order, so the scenario can
    ///     store "one document of each of the first T types" without reflection over the generic
    ///     <c>Store&lt;T&gt;</c>.
    /// </summary>
    public static readonly Action<IDocumentSession>[] Writers =
    [
        s => s.Store(new ColdDoc00 { Name = "cold-00" }),
        s => s.Store(new ColdDoc01 { Name = "cold-01" }),
        s => s.Store(new ColdDoc02 { Name = "cold-02" }),
        s => s.Store(new ColdDoc03 { Name = "cold-03" }),
        s => s.Store(new ColdDoc04 { Name = "cold-04" }),
        s => s.Store(new ColdDoc05 { Name = "cold-05" }),
        s => s.Store(new ColdDoc06 { Name = "cold-06" }),
        s => s.Store(new ColdDoc07 { Name = "cold-07" }),
        s => s.Store(new ColdDoc08 { Name = "cold-08" }),
        s => s.Store(new ColdDoc09 { Name = "cold-09" }),
        s => s.Store(new ColdDoc10 { Name = "cold-10" }),
        s => s.Store(new ColdDoc11 { Name = "cold-11" }),
        s => s.Store(new ColdDoc12 { Name = "cold-12" }),
        s => s.Store(new ColdDoc13 { Name = "cold-13" }),
        s => s.Store(new ColdDoc14 { Name = "cold-14" }),
        s => s.Store(new ColdDoc15 { Name = "cold-15" }),
        s => s.Store(new ColdDoc16 { Name = "cold-16" }),
        s => s.Store(new ColdDoc17 { Name = "cold-17" }),
        s => s.Store(new ColdDoc18 { Name = "cold-18" }),
        s => s.Store(new ColdDoc19 { Name = "cold-19" }),
        s => s.Store(new ColdDoc20 { Name = "cold-20" }),
        s => s.Store(new ColdDoc21 { Name = "cold-21" }),
        s => s.Store(new ColdDoc22 { Name = "cold-22" }),
        s => s.Store(new ColdDoc23 { Name = "cold-23" }),
        s => s.Store(new ColdDoc24 { Name = "cold-24" }),
        s => s.Store(new ColdDoc25 { Name = "cold-25" }),
        s => s.Store(new ColdDoc26 { Name = "cold-26" }),
        s => s.Store(new ColdDoc27 { Name = "cold-27" }),
        s => s.Store(new ColdDoc28 { Name = "cold-28" }),
        s => s.Store(new ColdDoc29 { Name = "cold-29" }),
        s => s.Store(new ColdDoc30 { Name = "cold-30" }),
        s => s.Store(new ColdDoc31 { Name = "cold-31" })
    ];
}
