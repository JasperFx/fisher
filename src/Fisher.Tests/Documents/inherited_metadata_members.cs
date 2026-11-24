using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using JasperFx;
using JasperFx.Metadata;
using Fisher.TestUtils;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#677 — the interface conventions map a member the document inherits from a base class.
/// </summary>
/// <remarks>
///     When the base class is in another assembly and its property is not virtual, the compiler cannot use
///     the base setter as the interface implementation and emits a hidden private forwarding method on the
///     derived class instead, with no property behind it. Comparing setters found nothing, so an inherited
///     <c>IRevisioned.Version</c> always loaded as 0 and every <c>UpdateRevision</c> of a loaded document
///     failed its guard. Found through Wolverine sagas (wolverine#5658): <c>Saga.Version</c> is in
///     Wolverine.dll and the saga is in the application. A base class in the same assembly was always
///     mapped, so these tests take their bases from Fisher.TestUtils.
/// </remarks>
public class inherited_metadata_members : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("inherited_metadata");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public void every_convention_finds_an_inherited_member()
    {
        _store.Options.Schema.For<RevisionedSaga>().Mapping.Metadata.Revision.Member.ShouldNotBeNull();
        _store.Options.Schema.For<VersionedChild>().Mapping.Metadata.Version.Member.ShouldNotBeNull();
        _store.Options.Schema.For<SoftDeletedChild>().Mapping.Metadata.IsSoftDeleted.Member.ShouldNotBeNull();
        _store.Options.Schema.For<SoftDeletedChild>().Mapping.Metadata.DeletedAt.Member.ShouldNotBeNull();
        _store.Options.Schema.For<TrackedChild>().Mapping.Metadata.CorrelationId.Member.ShouldNotBeNull();
        _store.Options.Schema.For<TrackedChild>().Mapping.Metadata.LastModifiedBy.Member.ShouldNotBeNull();
        _store.Options.Schema.For<SameAssemblyRevisioned>().Mapping.Metadata.Revision.Member.ShouldNotBeNull();
    }

    /// <summary>
    ///     The premise: across an assembly boundary the interface map's target is a hidden forwarding method,
    ///     not the base property's setter, so no property is found by comparing setters.
    /// </summary>
    [Fact]
    public void a_cross_assembly_base_is_reached_through_a_hidden_forwarder()
    {
        var map = typeof(RevisionedSaga).GetInterfaceMap(typeof(IRevisioned));
        var index = Array.IndexOf(map.InterfaceMethods, typeof(IRevisioned).GetProperty("Version")!.GetSetMethod());

        map.TargetMethods[index].DeclaringType.ShouldBe(typeof(RevisionedSaga));
        map.TargetMethods[index].IsPrivate.ShouldBeTrue();
    }

    /// <summary>The issue's repro, verbatim in shape.</summary>
    [Fact]
    public async Task an_inherited_revision_is_read_back_and_can_be_updated()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new RevisionedSaga { Id = id, State = "started" });
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            var loaded = await session.LoadAsync<RevisionedSaga>(id, Token);
            loaded.ShouldNotBeNull();
            loaded.Version.ShouldBe(1);

            loaded.State = "running";
            session.UpdateRevision(loaded, loaded.Version + 1);
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            var loaded = await session.LoadAsync<RevisionedSaga>(id, Token);
            loaded!.Version.ShouldBe(2);
            loaded.State.ShouldBe("running");
        }
    }

    [Fact]
    public async Task an_inherited_guid_version_crosses_sessions()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new VersionedChild { Id = id, Name = "one" });
            await session.SaveChangesAsync(Token);
        }

        VersionedChild loaded;
        await using (var session = _store.LightweightSession())
        {
            loaded = (await session.LoadAsync<VersionedChild>(id, Token))!;
            loaded.Version.ShouldNotBe(Guid.Empty);
        }

        // Stored through a session that never read it: the guard is fed from the mapped member.
        loaded.Name = "two";
        await using (var session = _store.LightweightSession())
        {
            session.Store(loaded);
            await session.SaveChangesAsync(Token);
        }
    }

    [Fact]
    public async Task inherited_soft_delete_members_are_populated()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new SoftDeletedChild { Id = id });
            await session.SaveChangesAsync(Token);
            session.Delete<SoftDeletedChild>(id);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        var deleted = (await query.Query<SoftDeletedChild>().IsDeleted().ToListAsync(Token)).ShouldHaveSingleItem();
        deleted.Deleted.ShouldBeTrue();
        deleted.DeletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task inherited_tracked_members_are_populated()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.CorrelationId = "corr-677";
            session.LastModifiedBy = "saga-runner";
            session.Store(new TrackedChild { Id = id });
            await session.SaveChangesAsync(Token);
        }

        await using var read = _store.LightweightSession();
        var loaded = await read.LoadAsync<TrackedChild>(id, Token);
        loaded!.CorrelationId.ShouldBe("corr-677");
        loaded.LastModifiedBy.ShouldBe("saga-runner");
    }

    /// <summary>
    ///     An explicit implementation on the base class is a private member of the base, which the
    ///     derived type's <c>GetProperties</c> never returns at all.
    /// </summary>
    [Fact]
    public async Task an_explicit_implementation_on_the_base_class_is_mapped()
    {
        _store.Options.Schema.For<ExplicitRevisionChild>().Mapping.Metadata.Revision.Member.ShouldNotBeNull();

        var id = Guid.NewGuid();
        await using (var session = _store.LightweightSession())
        {
            session.Store(new ExplicitRevisionChild { Id = id });
            await session.SaveChangesAsync(Token);
        }

        await using var read = _store.LightweightSession();
        var loaded = await read.LoadAsync<ExplicitRevisionChild>(id, Token);
        ((IRevisioned)loaded!).Version.ShouldBe(1);
    }

    // The cross-assembly bases are Fisher.TestUtils' (CrossAssemblyMetadataBases.cs): the compiler forwards the
    // interface through a hidden accessor with no property behind it, which is Wolverine's Saga shape.

    public class RevisionedSaga : CrossAssemblyRevisionedBase, IRevisioned
    {
        public Guid Id { get; set; }
        public string State { get; set; } = "";
    }

    public abstract class SameAssemblyRevisionedBase
    {
        public int Version { get; set; }
    }

    public class SameAssemblyRevisioned : SameAssemblyRevisionedBase, IRevisioned
    {
        public Guid Id { get; set; }
    }

    public class VersionedChild : CrossAssemblyVersionedBase, IVersioned
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class SoftDeletedChild : CrossAssemblySoftDeletedBase, ISoftDeleted
    {
        public Guid Id { get; set; }
    }

    public class TrackedChild : CrossAssemblyTrackedBase, ITracked
    {
        public Guid Id { get; set; }
    }

    public abstract class ExplicitRevisionBase : IRevisioned
    {
        private int _version;

        int IRevisioned.Version
        {
            get => _version;
            set => _version = value;
        }
    }

    public class ExplicitRevisionChild : ExplicitRevisionBase
    {
        public Guid Id { get; set; }
    }
}
