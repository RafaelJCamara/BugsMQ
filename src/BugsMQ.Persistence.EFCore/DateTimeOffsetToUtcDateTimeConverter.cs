using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BugsMQ.Persistence.EFCore;

/// <summary>
/// Stores every DateTimeOffset column as a plain UTC DateTime. All timestamps in this schema are
/// already UTC by convention (the "...AtUtc" naming), and SQLite's EF Core provider cannot translate
/// ORDER BY/comparisons over DateTimeOffset — storing as DateTime keeps the schema portable across
/// SQL Server/PostgreSQL/SQLite without any provider-specific code.
/// </summary>
internal sealed class DateTimeOffsetToUtcDateTimeConverter() : ValueConverter<DateTimeOffset, DateTime>(
    v => v.UtcDateTime,
    v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));
