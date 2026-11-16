using System.Data.Common;
using JasperFx.Events.Documents;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind the store-agnostic halves of docs/documents/listeners.md and
 * docs/documents/transaction-participants.md (fisher#581).
 *
 * See "Documentation samples" in CLAUDE.md: every sample a reader would copy lives in a
 * #region here and is pulled into the markdown by mdsnippets.
 */

#region sample_shared_save_listener
// Written once against JasperFx's contract, so the same class serves Marten, Polecat and Fisher
public class EnvelopeOutboxListener : IDocumentSaveListener
{
    public Task BeforeSaveChangesAsync(IDocumentSessionOperations session, CancellationToken token)
    {
        // Outside the transaction, once per save. A participant enlisted here joins this commit.
        session.AddTransactionParticipant(new EnvelopeParticipant());
        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit,
        CancellationToken token)
    {
        // After the commit, once, outside the resilience pipeline: the envelopes are durable now
        return Task.CompletedTask;
    }
}

public class EnvelopeParticipant : IDbTransactionParticipant
{
    public async Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction,
        CancellationToken token)
    {
        // Write on the connection and transaction you are handed, never on a second connection
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "insert into envelopes (body) values ('…')";
        await command.ExecuteNonQueryAsync(token);
    }
}
#endregion

public static class shared_session_hooks_samples
{
    public static async Task enroll(IDocumentStore store, object[] documents)
    {
        #region sample_shared_session_hooks
        await using var session = store.LightweightSession();

        // Held as the contract, the way a message bus joins a session the application opened
        IDocumentSessionOperations contract = session;

        contract.AddSaveListener(new EnvelopeOutboxListener());

        // Documents of mixed types, each stored as its own runtime type
        contract.StoreObjects(documents);

        await contract.SaveChangesAsync();
        #endregion
    }
}
