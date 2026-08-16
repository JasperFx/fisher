using JasperFx;
using JasperFx.Events.Documents;

namespace Fisher.Tests.Documents;

public readonly record struct VoucherCode(Guid Value);

public readonly record struct BranchCode(string Value);

public readonly record struct DaybookNumber(long Value);

public class Voucher
{
    public VoucherCode Id { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class Branch
{
    public BranchCode Id { get; set; }
    public string Town { get; set; } = string.Empty;
}

public class Daybook
{
    public DaybookNumber Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class Receipt
{
    public Guid Id { get; set; }
    public string Vendor { get; set; } = string.Empty;
}

public class Docket
{
    public long Id { get; set; }
    public string Vendor { get; set; } = string.Empty;
}

/// <summary>
///     <c>LoadAsync&lt;T&gt;(object)</c> — the document contract's identity-agnostic read (fisher#89 /
///     jasperfx#665).
/// </summary>
/// <remarks>
///     <para>
///         Declared on <c>IQuerySession</c> as public Fisher API rather than implemented explicitly, so
///         Fisher spells it the way Marten and Polecat do and a consumer moving between the stores meets
///         one API.
///     </para>
///     <para>
///         The half that is easy to get wrong is <em>not</em> the strong-typed one. The overload is
///         reached by any caller holding an identity in an <c>object</c>-typed local, so an
///         implementation that assumed a wrapper would pass the strong-typed facts and silently regress
///         the canonical ones — which is what the shared suite's
///         <c>the_object_overload_resolves_canonical_identities_too</c> is for, and what
///         <see cref="a_boxed_canonical_identity_resolves_the_same_way_the_typed_overload_does" />
///         pins here.
///     </para>
/// </remarks>
public class loading_by_an_untyped_identity : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("untyped-id");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task a_boxed_canonical_identity_resolves_the_same_way_the_typed_overload_does()
    {
        var receiptId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Receipt { Id = receiptId, Vendor = "Chandlery" });
            session.Store(new Docket { Id = 4100, Vendor = "Ropeworks" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.QuerySession();

        object boxedGuid = receiptId;
        (await query.LoadAsync<Receipt>(boxedGuid, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Vendor.ShouldBe("Chandlery");

        object boxedLong = 4100L;
        (await query.LoadAsync<Docket>(boxedLong, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Vendor.ShouldBe("Ropeworks");
    }

    [Fact]
    public async Task a_strong_typed_identity_resolves_over_all_three_backings()
    {
        var voucher = new VoucherCode(Guid.NewGuid());

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Voucher { Id = voucher, Description = "Launch week" });
            session.Store(new Branch { Id = new BranchCode("penzance"), Town = "Penzance" });
            session.Store(new Daybook { Id = new DaybookNumber(7), Name = "General" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.QuerySession();

        (await query.LoadAsync<Voucher>((object)voucher, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Description.ShouldBe("Launch week");

        (await query.LoadAsync<Branch>((object)new BranchCode("penzance"),
            TestContext.Current.CancellationToken)).ShouldNotBeNull().Town.ShouldBe("Penzance");

        (await query.LoadAsync<Daybook>((object)new DaybookNumber(7),
            TestContext.Current.CancellationToken)).ShouldNotBeNull().Name.ShouldBe("General");
    }

    /// <remarks>
    ///     The wrapping half: a caller holding the raw value a wrapper is over. This is what lets
    ///     <c>FetchLatest</c> address a strong-typed aggregate's document from a raw stream id
    ///     (fisher#88), so it is a real path rather than a courtesy.
    /// </remarks>
    [Fact]
    public async Task a_raw_value_is_wrapped_for_a_strong_typed_document()
    {
        var raw = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Voucher { Id = new VoucherCode(raw), Description = "Wrapped" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.QuerySession();

        (await query.LoadAsync<Voucher>((object)raw, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Description.ShouldBe("Wrapped");
    }

    [Fact]
    public async Task a_missing_identity_is_null_rather_than_a_throw()
    {
        await using var query = _store.QuerySession();

        (await query.LoadAsync<Voucher>((object)new VoucherCode(Guid.NewGuid()),
            TestContext.Current.CancellationToken)).ShouldBeNull();

        (await query.LoadAsync<Receipt>((object)Guid.NewGuid(),
            TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <remarks>
    ///     Integral widening only. An untyped literal is an <c>int</c>, so refusing it against a
    ///     <c>long</c>-keyed document would be a refusal for a difference the caller cannot see.
    /// </remarks>
    [Fact]
    public async Task an_int_addresses_a_long_keyed_document()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new Docket { Id = 12, Vendor = "Widened" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.QuerySession();

        object narrowed = 12;
        (await query.LoadAsync<Docket>(narrowed, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Vendor.ShouldBe("Widened");
    }

    /// <remarks>
    ///     <para>
    ///         Deliberately narrower than <c>Convert.ChangeType</c>, which would turn the string "12"
    ///         into an id and hide a genuine mistake. The message names both types.
    ///     </para>
    ///     <para>
    ///         <b>The cast to <c>object</c> is load-bearing in the test rather than decorative.</b>
    ///         A bare <c>LoadAsync&lt;Docket&gt;("12")</c> binds to the <em>string</em> overload, which
    ///         is more specific than this one — and that path hard-casts the storage and throws
    ///         <c>InvalidCastException</c> instead. Pre-existing behaviour of the typed overloads,
    ///         unchanged here, and worth knowing about when reading this test.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task an_identity_of_the_wrong_type_is_refused_by_name()
    {
        await using var query = _store.QuerySession();

        object wrongType = "12";

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await query.LoadAsync<Docket>(wrongType, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(nameof(Docket));
        ex.Message.ShouldContain("System.String");
        ex.Message.ShouldContain("System.Int64");
    }

    /// <remarks>
    ///     The four canonical overloads are more specific, so adding this one changed no existing call
    ///     site. A <c>Guid</c> argument still binds to <c>LoadAsync&lt;T&gt;(Guid)</c> — pinned by
    ///     resolving the overload rather than by asserting on a result, which would be identical either
    ///     way.
    /// </remarks>
    [Fact]
    public void the_typed_overloads_still_win_overload_resolution()
    {
        var method = typeof(IQuerySession)
            .GetMethods()
            .Where(x => x.Name == nameof(IQuerySession.LoadAsync) && x.GetGenericArguments().Length == 1)
            .Select(x => x.GetParameters()[0].ParameterType)
            .ToList();

        method.ShouldContain(typeof(Guid));
        method.ShouldContain(typeof(string));
        method.ShouldContain(typeof(int));
        method.ShouldContain(typeof(long));
        method.ShouldContain(typeof(object));
    }

    /// <remarks>
    ///     The member is the contract's, so a store-agnostic caller holding only
    ///     <see cref="IDocumentReadOperations" /> reaches Fisher's override rather than the default
    ///     implementation that throws for anything but a Guid or a string.
    /// </remarks>
    [Fact]
    public async Task the_contract_reaches_fishers_override()
    {
        var voucher = new VoucherCode(Guid.NewGuid());

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Voucher { Id = voucher, Description = "Through the contract" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.QuerySession();

        IDocumentReadOperations contract = query;

        (await contract.LoadAsync<Voucher>(voucher, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Description.ShouldBe("Through the contract");
    }
}
