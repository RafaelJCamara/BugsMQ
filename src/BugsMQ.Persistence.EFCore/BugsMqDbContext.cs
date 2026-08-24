using Microsoft.EntityFrameworkCore;

namespace BugsMQ.Persistence.EFCore;

public sealed class BugsMqDbContext(DbContextOptions<BugsMqDbContext> options) : DbContext(options)
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
        modelBuilder.Entity<SagaInstanceEntity>(b =>
        {
            b.ToTable("SagaInstances");
            b.HasKey(x => x.CorrelationId);
            b.Property(x => x.SagaType).HasMaxLength(200).IsRequired();
            b.Property(x => x.CurrentState).HasMaxLength(200).IsRequired();
            b.Property(x => x.DataJson).IsRequired();
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.SagaType, x.Status });
            b.HasIndex(x => new { x.Status, x.UpdatedAtUtc });
        });

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
            b.HasIndex(x => new { x.CorrelationId, x.Id });
            // Not unique: a single inbound message legitimately produces multiple entries
            // (MessageReceived, then StepSucceeded/StepFailed) sharing one MessageId — this index is
            // purely to speed up IsDuplicateAsync's existence check, not a uniqueness constraint.
            b.HasIndex(x => new { x.CorrelationId, x.MessageId });
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
}
