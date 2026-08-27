using Microsoft.EntityFrameworkCore;

namespace VSaga.Persistence.EFCore;

public sealed class VSagaDbContext(DbContextOptions<VSagaDbContext> options) : DbContext(options)
{
    public DbSet<SagaInstanceEntity> SagaInstances => Set<SagaInstanceEntity>();

    public DbSet<SagaEventLogEntity> SagaEventLog => Set<SagaEventLogEntity>();

    public DbSet<SagaTimeoutEntity> SagaTimeouts => Set<SagaTimeoutEntity>();

    public DbSet<SagaConsumerRegistrationEntity> SagaConsumerRegistrations => Set<SagaConsumerRegistrationEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureSagaInstances(modelBuilder);

        modelBuilder.Entity<SagaEventLogEntity>(b =>
        {
            b.ToTable("SagaEventLog");
            b.HasKey(x => x.Id);
            b.Property(x => x.SagaType).HasMaxLength(200).IsRequired();
            b.Property(x => x.FromState).HasMaxLength(200);
            b.Property(x => x.ToState).HasMaxLength(200);
            b.Property(x => x.MessageType).HasMaxLength(400);
            b.Property(x => x.MessageId).HasMaxLength(200);
            b.Property(x => x.SourceService).HasMaxLength(200);
            b.Property(x => x.DestinationService).HasMaxLength(200);
            b.Property(x => x.CausationId).HasMaxLength(200);
            // SagaType-leading, matching the scoped timeline read: two saga types tracking the same
            // correlation id each own an independent timeline, so neither of these may span both.
            b.HasIndex(x => new { x.SagaType, x.CorrelationId, x.Id });
            // Not unique: a single inbound message legitimately produces multiple entries
            // (MessageReceived, then StepSucceeded/StepFailed) sharing one MessageId — this index is
            // purely to speed up IsDuplicateAsync's existence check, not a uniqueness constraint.
            b.HasIndex(x => new { x.SagaType, x.CorrelationId, x.MessageId });
        });

        modelBuilder.Entity<SagaTimeoutEntity>(b =>
        {
            b.ToTable("SagaTimeouts");
            b.HasKey(x => x.Id);
            b.Property(x => x.SagaType).HasMaxLength(200).IsRequired();
            b.Property(x => x.ForState).HasMaxLength(200).IsRequired();
            b.HasIndex(x => new { x.Status, x.DueAtUtc });
        });

        modelBuilder.Entity<SagaConsumerRegistrationEntity>(b =>
        {
            b.ToTable("SagaConsumerRegistrations");
            b.HasKey(x => new { x.ServiceName, x.MessageType });
            b.Property(x => x.ServiceName).HasMaxLength(200).IsRequired();
            b.Property(x => x.MessageType).HasMaxLength(400).IsRequired();
            b.Property(x => x.QueueName).HasMaxLength(400).IsRequired();
            b.HasIndex(x => x.MessageType);
        });
    }

    /// <summary>Split out of <see cref="OnModelCreating"/> only for length — it is the one entity carrying enough keys and indexes to be worth reading on its own.</summary>
    private static void ConfigureSagaInstances(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SagaInstanceEntity>(b =>
        {
            b.ToTable("SagaInstances");
            // Composite, not CorrelationId alone: two saga types may track the same business
            // correlation id (an orchestrated saga and a choreographed one observing the same flow).
            // SagaType leads the key so the SagaType-prefixed lookups below use it directly.
            b.HasKey(x => new { x.SagaType, x.CorrelationId });
            b.Property(x => x.SagaType).HasMaxLength(200).IsRequired();
            b.Property(x => x.CurrentState).HasMaxLength(200).IsRequired();
            b.Property(x => x.DataJson).IsRequired();
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.SagaType, x.Status });
            b.HasIndex(x => new { x.Status, x.UpdatedAtUtc });
            // Resolving a bare correlation id to the saga instance(s) tracking it — the composite key
            // can't serve this, since its leading column is SagaType.
            b.HasIndex(x => x.CorrelationId);
            b.Property(x => x.ParentSagaType).HasMaxLength(200);
            // Ordered to match FindChildrenAsync's predicate, which always supplies both halves of the
            // parent pointer. Root sagas leave both null, so on a workload without sub-sagas this index
            // stays effectively empty rather than duplicating the table.
            b.HasIndex(x => new { x.ParentSagaType, x.ParentCorrelationId });
        });
    }
}
