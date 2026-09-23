using FluentAssertions;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Models.Metadata;
using Trax.Effect.Models.WorkQueue;
using Trax.Effect.Services.ServiceTrain;
using Trax.Mediator.Services.TrainExecution;
using Trax.Mediator.Tests.Postgres.Integration.Fixtures;

namespace Trax.Mediator.Tests.Postgres.Integration.IntegrationTests;

/// <summary>
/// Covers the subject key a train may attach to its queue entry: where it comes from, and what
/// happens when it cannot be produced or is not usable. How dispatch serializes on it is covered
/// in the scheduler's <c>SubjectKeySerializationTests</c>.
///
/// <para>Enforces Trax.Docs/adr/0019-queued-work-for-one-subject-runs-one-at-a-time.md.</para>
/// </summary>
[Property("adr", "Trax.Docs/adr/0019-queued-work-for-one-subject-runs-one-at-a-time.md")]
[TestFixture]
public class SubjectKeyTests : TestSetup
{
    private ITrainExecutionService Execution =>
        Scope.ServiceProvider.GetRequiredService<ITrainExecutionService>();

    private async Task<WorkQueue?> EntryAsync(long id)
    {
        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        return await context.WorkQueues.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
    }

    [SetUp]
    public async Task ResetAsync()
    {
        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        await context
            .WorkQueues.Where(w => w.TrainName!.Contains("SubjectKeyTests"))
            .ExecuteDeleteAsync();
    }

    [Test]
    public async Task A_train_that_does_not_override_the_key_enqueues_with_null()
    {
        var result = await Execution.QueueAsync(
            typeof(IUnkeyedTrain).FullName!,
            "{\"customerId\":42}"
        );

        (await EntryAsync(result.WorkQueueId))!
            .SubjectKey.Should()
            .BeNull("no key means no serialization — every existing train must be untouched");
    }

    [Test]
    public async Task A_train_that_overrides_the_key_stamps_it_on_the_entry()
    {
        var result = await Execution.QueueAsync(
            typeof(IKeyedTrain).FullName!,
            "{\"customerId\":42}"
        );

        (await EntryAsync(result.WorkQueueId))!.SubjectKey.Should().Be("customer-42");
    }

    [Test]
    public async Task The_key_is_derived_from_the_input_not_fixed_per_train()
    {
        var first = await Execution.QueueAsync(typeof(IKeyedTrain).FullName!, "{\"customerId\":1}");
        var second = await Execution.QueueAsync(
            typeof(IKeyedTrain).FullName!,
            "{\"customerId\":2}"
        );

        (await EntryAsync(first.WorkQueueId))!.SubjectKey.Should().Be("customer-1");
        (await EntryAsync(second.WorkQueueId))!
            .SubjectKey.Should()
            .Be("customer-2", "the key identifies the record, so it varies per mutation");
    }

    [Test]
    public async Task A_key_that_cannot_be_computed_aborts_the_enqueue()
    {
        var act = async () =>
            await Execution.QueueAsync(typeof(IThrowingKeyTrain).FullName!, "{\"customerId\":42}");

        (await act.Should().ThrowAsync<Exception>()).WithMessage(
            "*cannot key this mutation*",
            "silently falling back to null would drop the serialization guarantee at exactly "
                + "the moment the caller expected it"
        );

        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        (await context.WorkQueues.CountAsync(w => w.TrainName!.Contains("ThrowingKeyTrain")))
            .Should()
            .Be(0, "an aborted enqueue leaves no entry");
    }

    [Test]
    public async Task A_null_returned_by_the_override_is_stored_as_null()
    {
        var result = await Execution.QueueAsync(
            typeof(INullKeyTrain).FullName!,
            "{\"customerId\":42}"
        );

        (await EntryAsync(result.WorkQueueId))!
            .SubjectKey.Should()
            .BeNull("returning null is a legitimate 'this one needs no serialization'");
    }

    [Test]
    public async Task An_empty_key_aborts_the_enqueue()
    {
        ConfigurableKeyTrain.Key = "";

        var act = async () =>
            await Execution.QueueAsync(typeof(IConfigurableKeyTrain).FullName!, "{}");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            "*empty key*",
            "an empty key would serialize every train returning it against every other"
        );
    }

    [Test]
    public async Task A_key_longer_than_the_limit_aborts_the_enqueue()
    {
        ConfigurableKeyTrain.Key = new string('k', 513);

        var act = async () =>
            await Execution.QueueAsync(typeof(IConfigurableKeyTrain).FullName!, "{}");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            "*limit is 512*",
            "a key too long for the index would insert and then fail its claim on every cycle"
        );
    }

    [Test]
    public async Task A_key_at_the_limit_is_accepted_and_claimable_by_the_index()
    {
        // Three bytes each in UTF-8, the most a single UTF-16 unit can take.
        ConfigurableKeyTrain.Key = new string('\u4e2d', 512);

        var result = await Execution.QueueAsync(typeof(IConfigurableKeyTrain).FullName!, "{}");

        // Dispatching sets the status the subject index covers, which is where an oversized
        // key would fail. Doing it here proves the limit leaves room for multi-byte keys.
        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        var entry = await context.WorkQueues.FirstAsync(w => w.Id == result.WorkQueueId);
        entry.Status = Trax.Effect.Enums.WorkQueueStatus.Dispatched;
        var act = async () => await context.SaveChanges(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task An_enqueue_with_no_input_is_still_keyed()
    {
        var result = await Execution.QueueAsync(typeof(IKeyedTrain).FullName!, null);

        (await EntryAsync(result.WorkQueueId))!
            .SubjectKey.Should()
            .Be(
                "customer-0",
                "no input is read as an empty object, so the key is computed like any other and "
                    + "the entry is not quietly left unserialized"
            );
    }

    // ── probes ──────────────────────────────────────────────────────

    public record UnkeyedInput
    {
        public int CustomerId { get; init; }
    }

    public record KeyedInput
    {
        public int CustomerId { get; init; }
    }

    public record ThrowingKeyInput
    {
        public int CustomerId { get; init; }
    }

    public record NullKeyInput
    {
        public int CustomerId { get; init; }
    }

    public interface IUnkeyedTrain : IServiceTrain<UnkeyedInput, Unit>;

    public class UnkeyedTrain : ServiceTrain<UnkeyedInput, Unit>, IUnkeyedTrain
    {
        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public interface IKeyedTrain : IServiceTrain<KeyedInput, Unit>;

    public class KeyedTrain : ServiceTrain<KeyedInput, Unit>, IKeyedTrain
    {
        protected override string? QueueSubjectKey(Metadata metadata) =>
            $"customer-{metadata.GetInput<KeyedInput>()!.CustomerId}";

        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public interface IThrowingKeyTrain : IServiceTrain<ThrowingKeyInput, Unit>;

    public class ThrowingKeyTrain : ServiceTrain<ThrowingKeyInput, Unit>, IThrowingKeyTrain
    {
        protected override string? QueueSubjectKey(Metadata metadata) =>
            throw new InvalidOperationException("cannot key this mutation");

        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public interface INullKeyTrain : IServiceTrain<NullKeyInput, Unit>;

    public class NullKeyTrain : ServiceTrain<NullKeyInput, Unit>, INullKeyTrain
    {
        protected override string? QueueSubjectKey(Metadata metadata) => null;

        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public record ConfigurableKeyInput
    {
        public int CustomerId { get; init; }
    }

    public interface IConfigurableKeyTrain : IServiceTrain<ConfigurableKeyInput, Unit>;

    public class ConfigurableKeyTrain
        : ServiceTrain<ConfigurableKeyInput, Unit>,
            IConfigurableKeyTrain
    {
        public static string? Key { get; set; }

        protected override string? QueueSubjectKey(Metadata metadata) => Key;

        protected override Task<Either<Exception, Unit>> Junctions() => Task.FromResult(Resolve());
    }
}
