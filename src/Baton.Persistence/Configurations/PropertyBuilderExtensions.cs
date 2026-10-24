using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Baton.Persistence.Configurations;

internal static class PropertyBuilderExtensions
{
    /// <summary>
    /// Column has a database default (kept for parity with <c>0001_baseline.sql</c> and for raw-SQL
    /// inserts), but EF always sends the CLR value. Without this, EF skips CLR-default values on insert
    /// and the database default wins, e.g. <c>Enabled = false</c> would be stored as <c>true</c>.
    /// </summary>
    public static PropertyBuilder<T> HasSuppliedDefault<T>(this PropertyBuilder<T> property, string sql) =>
        property.HasDefaultValueSql(sql).ValueGeneratedNever();
}
